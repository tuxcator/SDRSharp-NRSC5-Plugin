namespace SDRSharp.NRSC5;

/// <summary>How far the FCC licence lookup for the current station has got.</summary>
internal enum StationLookupState
{
    /// <summary>No facility ID received yet, so there is nothing to look up.</summary>
    Idle,
    Pending,
    Resolved,
    /// <summary>The query succeeded but the facility ID is not in the FM database.</summary>
    NotFound,
    /// <summary>Network or parse failure. The SIS fields stay usable on their own.</summary>
    Failed,
    /// <summary>Station is outside the FCC's remit, e.g. a Mexican or Canadian licensee.</summary>
    Unsupported
}

/// <summary>
/// What the panel knows about the station itself rather than about the signal.
///
/// Two sources feed it and they never overlap. The station broadcasts its own
/// identity in SIS frames: call sign, slogan, message, FCC facility ID and the
/// transmitter site. The FCC's public licence database supplies what is not on
/// the air, keyed by that same facility ID: community of licence, ERP and HAAT.
///
/// The PI code comes from neither. It is not transmitted over HD Radio and no
/// database publishes it, but for US stations it is a pure function of the call
/// sign, so it is derived here and matches what an RDS receiver would display.
/// </summary>
internal sealed record StationFacts(
    string Callsign,
    string Slogan,
    string Message,
    string CountryCode,
    int FacilityId,
    float Latitude,
    float Longitude,
    int Altitude,
    bool HasLocation,
    string City,
    string State,
    string Licensee,
    string StationClass,
    double ErpKw,
    double HaatMeters,
    StationLookupState Lookup,
    string SiteCity,
    string SiteState,
    string SiteSource,
    StationLookupState SiteLookup)
{
    public static StationFacts Empty { get; } = new(
        "", "", "", "", 0, 0, 0, 0, false, "", "", "", "", 0, 0, StationLookupState.Idle,
        "", "", "", StationLookupState.Idle);

    /// <summary>Community of licence, as the FCC records it. An administrative fact.</summary>
    public string Place => Join(City, State);

    /// <summary>
    /// Where the transmitter actually stands, from reverse geocoding the coordinates SIS
    /// carries. Regularly a different town from the community of licence: KQRS is
    /// licensed to Golden Valley and transmits from Shoreview.
    /// </summary>
    public string SitePlace => Join(SiteCity, SiteState);

    private static string Join(string city, string state) => (city.Length, state.Length) switch
    {
        (0, 0) => "",
        (_, 0) => city,
        (0, _) => state,
        _ => $"{city}, {state}"
    };

    /// <summary>PI code derived from the call sign, or null when the rule does not apply.</summary>
    public int? PiCode => PiCodeFor(Callsign);

    /// <summary>
    /// RBDS call sign to PI code, per the rule in the NRSC-4 US RBDS standard: read the
    /// three letters after the K or W as a base-26 number and add a prefix constant,
    /// 0x1000 for K and 0x54A8 for W. WKTI is the standard's own worked example and
    /// comes out as 0x7106.
    ///
    /// Only four-letter US call signs are covered. Three-letter ones (KOA, WGN) are a
    /// hard-coded exception table in the standard rather than a formula, and Canadian
    /// and Mexican PI codes are assigned rather than derived, so those all return null
    /// instead of a plausible-looking wrong answer.
    /// </summary>
    public static int? PiCodeFor(string? callsign)
    {
        var call = NormalizeCallsign(callsign);
        if (call.Length != 4) return null;

        var prefix = call[0] switch
        {
            'K' => 0x1000,
            'W' => 0x54A8,
            _ => -1
        };
        if (prefix < 0) return null;

        var value = prefix;
        var weight = 676;
        for (var i = 1; i < 4; i++)
        {
            if (call[i] is < 'A' or > 'Z') return null;
            value += weight * (call[i] - 'A');
            weight /= 26;
        }
        return value;
    }

    /// <summary>
    /// Strips the service suffix stations append in SIS. "KQRS-FM" and "KQRS FM" both
    /// carry the PI code of KQRS, because the suffix is not part of the call sign proper.
    /// </summary>
    internal static string NormalizeCallsign(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign)) return "";
        var call = callsign.Trim().ToUpperInvariant();
        var cut = call.IndexOfAny(['-', ' ', '/']);
        if (cut > 0) call = call[..cut];
        return call;
    }
}
