using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace SDRSharp.NRSC5;

/// <summary>One FM facility as the FCC licenses it.</summary>
internal sealed record FccRecord(
    string Callsign,
    string City,
    string State,
    string Country,
    string StationClass,
    double ErpKw,
    double HaatMeters,
    string Licensee);

/// <summary>
/// Resolves the FCC facility ID that the station broadcasts in its SIS frames into the
/// licence record for that facility, which is where the community of licence and the
/// ERP come from. The query is the FCC's own public FM Query service; its output is
/// public-domain government data and needs no key.
///
/// Every lookup is cached, in memory and on disk, because a listener band-scanning
/// revisits the same handful of stations constantly and the answer changes at most a
/// few times a year. A failure is never fatal: the SIS fields stand on their own and
/// the panel simply leaves the licence fields blank.
/// </summary>
internal sealed class FccStationDirectory : IDisposable
{
    // "list=4" is the pipe-delimited output; the HTML modes would have to be scraped.
    private const string QueryFormat = "https://transition.fcc.gov/fcc-bin/fmq?facid={0}&list=4";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private const int MaxCacheEntries = 2000;

    // Column layout of a "list=4" row, from a live response:
    // |KQRS-FM |92.5 MHz |FM |223 |ND |H |C |- |LIC |GOLDEN VALLEY |MN |US
    // |BLH-19910814KB |100. kW |100. kW |315.0 |315.0 |35505 |N |45 |3 |29.8 | ...
    // |W |93 |7 |27.7 |RADIO LICENSE HOLDINGS LLC |
    // Index 0 is the empty string before the leading pipe.
    private const int ColumnCallsign = 1;
    private const int ColumnService = 3;
    private const int ColumnClass = 7;
    private const int ColumnStatus = 9;
    private const int ColumnCity = 10;
    private const int ColumnState = 11;
    private const int ColumnCountry = 12;
    private const int ColumnErpHorizontal = 14;
    private const int ColumnErpVertical = 15;
    private const int ColumnHaat = 16;
    private const int ColumnFacilityId = 18;
    private const int ColumnLicensee = 27;

    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly Dictionary<int, CacheEntry> _cache = new();
    private readonly Dictionary<int, Task<FccRecord?>> _inFlight = new();
    private readonly string _cachePath;
    private bool _cacheLoaded;

    public FccStationDirectory()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"SDRSharp-NRSC5-Plugin/{PluginInfo.DevelopmentVersion} (+https://github.com/tuxcator/SDRSharp-NRSC5-Plugin)");
        _cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SDRSharp.NRSC5",
            "fcc-fm-cache.json");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Returns the licence record for a facility, or null when the FCC does not list it.
    /// Concurrent callers asking for the same facility share one request.
    /// </summary>
    public Task<FccRecord?> LookupAsync(int facilityId, CancellationToken token)
    {
        if (facilityId <= 0) return Task.FromResult<FccRecord?>(null);

        lock (_gate)
        {
            EnsureCacheLoaded();
            if (_cache.TryGetValue(facilityId, out var cached) &&
                DateTimeOffset.UtcNow - cached.Fetched < CacheLifetime)
                return Task.FromResult(cached.Record);

            if (_inFlight.TryGetValue(facilityId, out var running)) return running;

            var task = FetchAsync(facilityId, token);
            _inFlight[facilityId] = task;
            // Registered while the lock is held, so the continuation cannot clear the
            // entry before it has been added even if the fetch already finished.
            _ = task.ContinueWith(
                _ => { lock (_gate) _inFlight.Remove(facilityId); },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>
    /// A null result means the FCC answered but does not list the facility, which is
    /// worth caching. Being offline or blocked throws instead, so nothing is cached and
    /// the next tune to the same station retries.
    /// </summary>
    private async Task<FccRecord?> FetchAsync(int facilityId, CancellationToken token)
    {
        var url = string.Format(CultureInfo.InvariantCulture, QueryFormat, facilityId);
        var body = await _http.GetStringAsync(url, token).ConfigureAwait(false);
        var record = Parse(body, facilityId);
        Store(facilityId, record);
        return record;
    }

    /// <summary>
    /// Picks the licensed main facility out of the rows the FCC returns. A facility
    /// usually has several: the licensed FM record plus special temporary authorities
    /// and applications, which carry the powers the station is not actually running.
    /// </summary>
    internal static FccRecord? Parse(string body, int facilityId)
    {
        FccRecord? best = null;
        var bestRank = int.MinValue;

        foreach (var line in body.Split('\n'))
        {
            if (line.Length == 0 || line[0] != '|') continue;
            var columns = line.Split('|');
            if (columns.Length <= ColumnLicensee) continue;

            var id = Field(columns, ColumnFacilityId);
            if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId) ||
                parsedId != facilityId)
                continue;

            var service = Field(columns, ColumnService);
            var status = Field(columns, ColumnStatus);
            var rank = RankFor(service, status);
            if (rank <= bestRank) continue;

            bestRank = rank;
            best = new FccRecord(
                Field(columns, ColumnCallsign),
                ToTitleCase(Field(columns, ColumnCity)),
                Field(columns, ColumnState),
                Field(columns, ColumnCountry),
                Field(columns, ColumnClass),
                ParseErpKw(columns),
                ParseNumber(Field(columns, ColumnHaat)),
                ToTitleCase(Field(columns, ColumnLicensee)));
        }

        return best;
    }

    /// <summary>
    /// A licensed "FM" row beats everything. "FS" and "FA" rows are auxiliary or
    /// applied-for facilities and are only used when there is nothing better.
    /// </summary>
    private static int RankFor(string service, string status)
    {
        var serviceRank = service switch
        {
            "FM" => 200,
            "FL" or "FX" => 100,
            _ => 0
        };
        var statusRank = status switch
        {
            "LIC" => 20,
            "CP" => 10,
            _ => 0
        };
        return serviceRank + statusRank;
    }

    /// <summary>
    /// Stations are identified by the larger of their horizontal and vertical ERP,
    /// which is how the FCC and every station directory quote it.
    /// </summary>
    private static double ParseErpKw(string[] columns) => Math.Max(
        ParseNumber(Field(columns, ColumnErpHorizontal).Replace("kW", "")),
        ParseNumber(Field(columns, ColumnErpVertical).Replace("kW", "")));

    private static double ParseNumber(string value) =>
        double.TryParse(value.Trim().TrimEnd('.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static string Field(string[] columns, int index) =>
        index < columns.Length ? columns[index].Trim() : "";

    /// <summary>Corporate suffixes the FCC writes as initialisms and title casing would ruin.</summary>
    private static readonly HashSet<string> Initialisms =
        new(StringComparer.Ordinal) { "LLC", "INC", "LP", "LLP", "PLC", "CO", "LTD", "USA", "II", "III" };

    /// <summary>The FCC shouts its place and licensee names; the panel does not.</summary>
    internal static string ToTitleCase(string value)
    {
        if (value.Length == 0 || value == "-") return "";
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
            if (!Initialisms.Contains(words[i].Trim(',', '.').ToUpperInvariant()))
                words[i] = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words[i].ToLowerInvariant());
        return string.Join(' ', words);
    }

    private void Store(int facilityId, FccRecord? record)
    {
        lock (_gate)
        {
            _cache[facilityId] = new CacheEntry(record, DateTimeOffset.UtcNow);
            TrimCache();
            SaveCache();
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void TrimCache()
    {
        if (_cache.Count <= MaxCacheEntries) return;
        foreach (var stale in _cache.OrderBy(entry => entry.Value.Fetched)
                     .Take(_cache.Count - MaxCacheEntries)
                     .Select(entry => entry.Key)
                     .ToList())
            _cache.Remove(stale);
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;
        _cacheLoaded = true;
        try
        {
            if (!File.Exists(_cachePath)) return;
            var stored = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(_cachePath));
            if (stored is null) return;
            foreach (var (key, entry) in stored)
                if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    _cache[id] = entry;
        }
        catch
        {
            // A corrupt or unreadable cache is not worth reporting: it is rebuilt on use.
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void SaveCache()
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (directory is not null) Directory.CreateDirectory(directory);
            var stored = _cache.ToDictionary(
                entry => entry.Key.ToString(CultureInfo.InvariantCulture),
                entry => entry.Value);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(stored));
        }
        catch
        {
            // Read-only or roaming profile. The in-memory cache still does its job.
        }
    }

    internal sealed record CacheEntry(FccRecord? Record, DateTimeOffset Fetched);
}
