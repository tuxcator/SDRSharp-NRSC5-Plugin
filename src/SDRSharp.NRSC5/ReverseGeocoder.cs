using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace SDRSharp.NRSC5;

/// <summary>The populated place a transmitter stands in, and which service named it.</summary>
internal sealed record GeocodedSite(string City, string State, string Source)
{
    public string Place => (City.Length, State.Length) switch
    {
        (0, 0) => "",
        (_, 0) => City,
        (0, _) => State,
        _ => $"{City}, {State}"
    };
}

/// <summary>
/// Turns the transmitter coordinates a station broadcasts in SIS into the name of the
/// place it stands in. That is not the same as the community of licence the FCC records:
/// KQRS is licensed to Golden Valley but transmits from Shoreview, fifteen kilometres
/// away, and it is the second one that says where to point an antenna.
///
/// Two services, in order. The US Census geocoder is public-domain government data,
/// needs no key, and is the better of the two on a transmitter site: towers stand in
/// unincorporated country more often than not, and the Census still names those through
/// its designated places while OpenStreetMap falls back to the county. Nominatim covers
/// everywhere the Census does not, which for HD Radio means Mexico and Canada.
/// </summary>
internal sealed class ReverseGeocoder : IDisposable
{
    private const string CensusFormat =
        "https://geocoding.geo.census.gov/geocoder/geographies/coordinates?x={1}&y={0}" +
        "&benchmark=Public_AR_Current&vintage=Current_Current" +
        "&layers=Incorporated%20Places,Census%20Designated%20Places,Counties,States&format=json";

    private const string NominatimFormat =
        "https://nominatim.openstreetmap.org/reverse?lat={0}&lon={1}&format=jsonv2&zoom=13";

    // Nominatim's usage policy caps the public instance at one request per second and
    // asks that results be cached. The cache does most of the work; this is the floor
    // under whatever it misses.
    private static readonly TimeSpan NominatimInterval = TimeSpan.FromMilliseconds(1100);

    private readonly HttpClient _http;
    private readonly LookupCache<GeocodedSite> _cache =
        new("transmitter-sites.json", TimeSpan.FromDays(180), 2000);
    private readonly SemaphoreSlim _nominatimGate = new(1, 1);
    private long _lastNominatimTicks;

    public ReverseGeocoder()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        // Nominatim rejects requests without a User-Agent that identifies the application.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"SDRSharp-NRSC5-Plugin/{PluginInfo.DevelopmentVersion} (+https://github.com/tuxcator/SDRSharp-NRSC5-Plugin)");
    }

    public void Dispose()
    {
        _http.Dispose();
        _nominatimGate.Dispose();
    }

    /// <summary>
    /// Names the place at these coordinates, or returns null when neither service can.
    /// <paramref name="countryCode"/> is the one from SIS; anything but US skips the
    /// Census, whose coverage stops at the border.
    /// </summary>
    public Task<GeocodedSite?> LookupAsync(float latitude, float longitude, string countryCode, CancellationToken token)
    {
        if (!IsPlausible(latitude, longitude)) return Task.FromResult<GeocodedSite?>(null);

        var key = LookupCache<GeocodedSite>.CoordinateKey(latitude, longitude);
        return _cache.GetOrAddAsync(key, async cancellation =>
        {
            var unitedStates = countryCode.Length == 0 ||
                               countryCode.Equals("US", StringComparison.OrdinalIgnoreCase);
            if (unitedStates)
            {
                var census = await QueryCensusAsync(latitude, longitude, cancellation).ConfigureAwait(false);
                if (census is not null) return census;
            }
            return await QueryNominatimAsync(latitude, longitude, cancellation).ConfigureAwait(false);
        }, token);
    }

    /// <summary>
    /// A station that has not sent its location yet reads as 0,0 - a real coordinate in
    /// the Atlantic, so it has to be rejected explicitly rather than left to the geocoder.
    /// </summary>
    internal static bool IsPlausible(float latitude, float longitude) =>
        Math.Abs(latitude) <= 90 && Math.Abs(longitude) <= 180 &&
        (Math.Abs(latitude) > 0.01 || Math.Abs(longitude) > 0.01);

    private async Task<GeocodedSite?> QueryCensusAsync(float latitude, float longitude, CancellationToken token)
    {
        var url = string.Format(CultureInfo.InvariantCulture, CensusFormat, latitude, longitude);
        var body = await _http.GetStringAsync(url, token).ConfigureAwait(false);
        return ParseCensus(body);
    }

    private async Task<GeocodedSite?> QueryNominatimAsync(float latitude, float longitude, CancellationToken token)
    {
        await _nominatimGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var since = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastNominatimTicks));
            if (Volatile.Read(ref _lastNominatimTicks) != 0 && since < NominatimInterval)
                await Task.Delay(NominatimInterval - since, token).ConfigureAwait(false);
            Volatile.Write(ref _lastNominatimTicks, Stopwatch.GetTimestamp());

            var url = string.Format(CultureInfo.InvariantCulture, NominatimFormat, latitude, longitude);
            var body = await _http.GetStringAsync(url, token).ConfigureAwait(false);
            return ParseNominatim(body);
        }
        finally
        {
            _nominatimGate.Release();
        }
    }

    /// <summary>
    /// The Census answers in layers. An incorporated place is the real answer; a census
    /// designated place names the unincorporated country most towers stand in; the county
    /// is the last thing worth showing before giving up and naming only the state.
    /// BASENAME is used rather than NAME because NAME carries the classification with it,
    /// as in "Shoreview city" and "Ramsey County".
    /// </summary>
    internal static GeocodedSite? ParseCensus(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("geographies", out var geographies))
            return null;

        var state = Layer(geographies, "States", "STUSAB");
        var city = Layer(geographies, "Incorporated Places", "BASENAME")
                   ?? Layer(geographies, "Census Designated Places", "BASENAME");
        if (city is null)
        {
            var county = Layer(geographies, "Counties", "BASENAME");
            if (county is not null) city = $"{county} Co.";
        }

        if (city is null && state is null) return null;
        return new GeocodedSite(city ?? "", state ?? "", "US Census");

        static string? Layer(JsonElement geographies, string name, string field)
        {
            if (!geographies.TryGetProperty(name, out var entries) ||
                entries.ValueKind != JsonValueKind.Array ||
                entries.GetArrayLength() == 0)
                return null;
            var value = entries[0].TryGetProperty(field, out var text) ? text.GetString() : null;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    /// <summary>
    /// OpenStreetMap files populated places under whichever of a dozen keys fits the
    /// local administrative vocabulary, so they are tried in descending size order.
    /// </summary>
    internal static GeocodedSite? ParseNominatim(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("address", out var address)) return null;

        string? city = null;
        foreach (var key in new[] { "city", "town", "village", "municipality", "hamlet", "suburb", "county" })
        {
            if (!address.TryGetProperty(key, out var value)) continue;
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            city = text.Trim();
            break;
        }

        // "US-CO" and "MX-BCN" carry the short form of the state; the spelt-out name is
        // too long for the panel cell whenever the city name is not tiny.
        string? state = null;
        if (address.TryGetProperty("ISO3166-2-lvl4", out var iso) && iso.GetString() is { } code)
        {
            var dash = code.IndexOf('-');
            if (dash > 0 && dash < code.Length - 1) state = code[(dash + 1)..];
        }
        if (state is null && address.TryGetProperty("state", out var name)) state = name.GetString()?.Trim();

        if (city is null && state is null) return null;
        return new GeocodedSite(city ?? "", state ?? "", "OpenStreetMap");
    }
}
