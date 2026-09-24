namespace SDRSharp.NRSC5;

/// <summary>What an ID3 XHDR frame tells the receiver to do with the artwork.</summary>
internal enum ArtworkDirective
{
    /// <summary>The ID3 frame carried no XHDR at all.</summary>
    None,
    /// <summary>Show the image with the given LOT id.</summary>
    Show,
    /// <summary>"Flush memory". Only meaningful from a station that also links images; see <see cref="ArtworkResolver"/>.</summary>
    Clear
}

/// <summary>
/// The XHDR as libnrsc5 reports it. The semantics are fixed by how <c>src/output.c</c>
/// parses the frame: param 0 with a two-byte extension carries a LOT id, param 1 with no
/// extension means "flush memory", and -1 means the ID3 frame had no XHDR.
///
/// Up to Dev 3.3.5 the plugin read only the LOT id and ignored the parameter, so a
/// station clearing its artwork for a jingle or a spot left the previous song's image on
/// screen - one of the two reasons the artwork looked out of step with what was playing.
/// </summary>
internal readonly record struct XhdrReference(ArtworkDirective Directive, int Lot)
{
    public static XhdrReference Absent { get; } = new(ArtworkDirective.None, -1);

    public static XhdrReference FromNative(uint mime, int param, int lot) => param switch
    {
        0 when lot >= 0 && Nrsc5Mime.IsImage(mime) => new(ArtworkDirective.Show, lot),
        1 => new(ArtworkDirective.Clear, -1),
        _ => Absent
    };
}

/// <summary>
/// One program's now-playing metadata, as decoded. <see cref="LotStamp"/> is the value of
/// the engine's LOT counter when this track's first ID3 frame arrived: images that turn
/// up after it can belong to this track, images from before it cannot.
/// </summary>
internal sealed record TrackInfo(
    int Program,
    string Title,
    string Artist,
    string Album,
    XhdrReference Xhdr,
    long LotStamp)
{
    /// <summary>Stations repeat the same ID3 every few seconds; this says whether it is the same track.</summary>
    public bool IsSameTrackAs(TrackInfo? other) =>
        other is not null && other.Program == Program &&
        string.Equals(other.Title, Title, StringComparison.Ordinal) &&
        string.Equals(other.Artist, Artist, StringComparison.Ordinal);

    /// <summary>
    /// Folds a freshly decoded ID3 frame into what is already known about the program.
    /// Stations send the XHDR in some ID3 frames and not in others, so a repeat of the
    /// same track without one keeps the reference it already had. Until 3.3.5 such a
    /// repeat wiped the reference, and the frame fell back to another song's image.
    /// A new track without an XHDR starts with none: the old reference was the old song's.
    /// </summary>
    public static TrackInfo Merge(TrackInfo? previous, TrackInfo incoming)
    {
        if (!incoming.IsSameTrackAs(previous)) return incoming;
        return incoming with
        {
            Xhdr = incoming.Xhdr.Directive == ArtworkDirective.None ? previous!.Xhdr : incoming.Xhdr,
            LotStamp = previous!.LotStamp
        };
    }
}

/// <summary>What the artwork frame should show, and whether that is the station logo.</summary>
internal readonly record struct ArtworkChoice(byte[]? Image, bool IsStationLogo)
{
    public static ArtworkChoice Logo(byte[]? logo) => new(logo, logo is not null);
}

/// <summary>
/// Decides the artwork for the track the listener is hearing. The one rule that matters:
/// never show an image that belongs to another track. When the right image is not known
/// or has not arrived yet, the station logo is the honest answer, not the most recent
/// picture that happened to be in memory - which is what 3.3.5 did, and why a station
/// ID or a jingle was shown with the previous song's cover.
///
/// The XHDR is only trusted from a station that actually uses it to link images, that
/// is, one that has sent parameter 0 at least once. XHTKR 103.7, watched live, sends
/// parameter 1 ("flush") in every one of its ID3 frames, songs included, and never
/// parameter 0 - yet broadcasts an image on each subchannel a few times per song. Read
/// literally, that station would never show artwork at all. For such a station the XHDR
/// carries no information, and arrival order is the only clue left: an image received
/// during this track is taken as this track's, and nothing received before it.
/// </summary>
internal static class ArtworkResolver
{
    /// <param name="xhdr">The presented track's XHDR.</param>
    /// <param name="stationLinksImages">Whether this program has ever linked an image with XHDR parameter 0.</param>
    /// <param name="referenced">The image the XHDR names, if it has arrived.</param>
    /// <param name="artSinceTrackStart">The newest image received after this track began.</param>
    /// <param name="logo">The logo for this program, if any.</param>
    public static ArtworkChoice Resolve(
        XhdrReference xhdr,
        bool stationLinksImages,
        byte[]? referenced,
        byte[]? artSinceTrackStart,
        byte[]? logo)
    {
        // The station said which image. Until it arrives, the logo - not a guess.
        if (xhdr.Directive == ArtworkDirective.Show)
            return referenced is not null ? new ArtworkChoice(referenced, false) : ArtworkChoice.Logo(logo);

        // A station that links images and did not link one here means this track has none.
        if (stationLinksImages) return ArtworkChoice.Logo(logo);

        return artSinceTrackStart is not null ? new ArtworkChoice(artSinceTrackStart, false) : ArtworkChoice.Logo(logo);
    }
}

/// <summary>
/// Holds decoded metadata until the audio it belongs to is actually heard.
///
/// Metadata is decoded at the same moment as the audio it describes, but that audio then
/// waits in the prebuffer before it reaches the speakers: 0.75 s by default, up to 10 s.
/// Shown on arrival, the title and the artwork changed that much ahead of the song, and
/// with a long buffer the whole previous song's tail played under the next song's cover.
///
/// Each entry is stamped with the prebuffer's write position when it was decoded and is
/// released once playback has consumed up to that position. The age limit is a backstop
/// for the cases where playback stalls and the position would otherwise never be reached.
/// Not thread-safe: the engine holds its own lock around it.
/// </summary>
internal sealed class PresentationQueue<T>
{
    private readonly Queue<(long Position, long Ticks, T Item)> _items = new();

    public int Count => _items.Count;

    public void Enqueue(long position, long ticks, T item) => _items.Enqueue((position, ticks, item));

    /// <summary>
    /// Takes everything now due and returns the newest of it: when several frames fall
    /// due at once, only the last one describes what is playing.
    /// </summary>
    public bool TryTakeDue(long consumed, long nowTicks, long maxAgeTicks, out T item)
    {
        item = default!;
        var found = false;
        while (_items.Count > 0)
        {
            var head = _items.Peek();
            if (head.Position > consumed && nowTicks - head.Ticks < maxAgeTicks) break;
            item = _items.Dequeue().Item;
            found = true;
        }
        return found;
    }

    public void Clear() => _items.Clear();
}
