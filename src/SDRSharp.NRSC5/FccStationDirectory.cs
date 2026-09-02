using System.Globalization;
using System.Net.Http;

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
    private readonly LookupCache<FccRecord> _cache =
        new("fcc-fm-cache.json", TimeSpan.FromDays(30), 2000);

    public FccStationDirectory()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"SDRSharp-NRSC5-Plugin/{PluginInfo.DevelopmentVersion} (+https://github.com/tuxcator/SDRSharp-NRSC5-Plugin)");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Returns the licence record for a facility, or null when the FCC does not list it.
    /// Concurrent callers asking for the same facility share one request.
    /// </summary>
    public Task<FccRecord?> LookupAsync(int facilityId, CancellationToken token)
    {
        if (facilityId <= 0) return Task.FromResult<FccRecord?>(null);
        return _cache.GetOrAddAsync(
            facilityId.ToString(CultureInfo.InvariantCulture),
            async cancellation =>
            {
                var url = string.Format(CultureInfo.InvariantCulture, QueryFormat, facilityId);
                var body = await _http.GetStringAsync(url, cancellation).ConfigureAwait(false);
                return Parse(body, facilityId);
            },
            token);
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

}
