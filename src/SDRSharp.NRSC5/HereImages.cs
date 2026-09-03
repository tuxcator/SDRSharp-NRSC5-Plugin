namespace SDRSharp.NRSC5;

/// <summary>
/// One piece of a HERE map, with the ground it covers. Every tile carries its own
/// corners, which is what makes the mosaic assemblable without knowing how the station
/// chose to number the pieces: they are laid out by latitude and longitude, not by index.
/// </summary>
internal sealed record HereTile(
    int Part,
    byte[] Data,
    float North,
    float West,
    float South,
    float East)
{
    /// <summary>Rejects the placeholder corners a station sends before it has a real map.</summary>
    public bool HasBounds => North > South && East > West &&
                             Math.Abs(North) <= 90 && Math.Abs(South) <= 90 &&
                             Math.Abs(East) <= 180 && Math.Abs(West) <= 180;
}

/// <summary>
/// A whole HERE map as it arrives: traffic comes as nine tiles that trickle in over a
/// minute or two, weather as a single image. The sequence number changes when the
/// station publishes a new map, which is what starts a fresh set.
/// </summary>
internal sealed record HereImageSet(
    bool IsTraffic,
    int Sequence,
    DateTime TimeUtc,
    IReadOnlyList<HereTile> Tiles,
    int Expected)
{
    public int Received => Tiles.Count;

    public bool Complete => Expected > 0 && Received >= Expected;

    public float North => Tiles.Count == 0 ? 0 : Tiles.Max(t => t.North);
    public float South => Tiles.Count == 0 ? 0 : Tiles.Min(t => t.South);
    public float East => Tiles.Count == 0 ? 0 : Tiles.Max(t => t.East);
    public float West => Tiles.Count == 0 ? 0 : Tiles.Min(t => t.West);

    public string Describe() => IsTraffic
        ? $"TRAFFIC  ·  {Received}/{Expected} tiles"
        : "WEATHER";
}

/// <summary>
/// An emergency alert as the station broadcasts it. Amber alerts arrive here under the
/// Safety or Rescue category, hurricanes and storms under Weather, earthquakes under
/// Geophysical; the plugin does not filter by category, it shows what arrives.
/// </summary>
internal sealed record HdAlert(
    string Message,
    int Category1,
    int Category2,
    int LocationFormat,
    IReadOnlyList<int> Locations,
    DateTime ReceivedUtc)
{
    /// <summary>The categories that are set, named. Both may be, or neither.</summary>
    public string Categories
    {
        get
        {
            var names = new List<string>(2);
            foreach (var category in new[] { Category1, Category2 })
            {
                var name = Nrsc5AlertCategory.Describe(category);
                if (name.Length > 0 && !names.Contains(name)) names.Add(name);
            }
            return names.Count == 0 ? "Uncategorised" : string.Join(" · ", names);
        }
    }

    /// <summary>
    /// The counties or postcodes the alert applies to. They are raw SAME, FIPS or ZIP
    /// codes; naming them would need a lookup table the plugin does not carry, so the
    /// format is spelled out and the codes are shown as broadcast.
    /// </summary>
    public string DescribeLocations()
    {
        if (Locations.Count == 0) return "";
        var format = Nrsc5LocationFormat.Describe(LocationFormat);
        var codes = string.Join(", ", Locations.Take(24));
        if (Locations.Count > 24) codes += $", +{Locations.Count - 24} more";
        return format.Length > 0 ? $"{format}: {codes}" : codes;
    }
}

/// <summary>Everything the data services have delivered for the station in tune.</summary>
internal sealed record HereData(
    HereImageSet? Traffic,
    HereImageSet? Weather,
    IReadOnlyList<HdAlert> Alerts)
{
    public static HereData Empty { get; } = new(null, null, []);

    public bool IsEmpty => Traffic is null && Weather is null && Alerts.Count == 0;
}
