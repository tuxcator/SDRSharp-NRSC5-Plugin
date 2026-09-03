namespace SDRSharp.NRSC5;

/// <summary>
/// Spreads the decoded HD stereo image beyond the speakers. Mid and side are separated, the
/// side signal is boosted and mixed with a short delayed copy of itself, and the result is
/// folded back into left and right. The delay is what carries the effect: a plain side boost
/// only sounds louder, while a few milliseconds of it reads as room. Low frequencies are
/// held at the centre, because widening them hollows out the mix and cancels on a mono
/// speaker. HDC audio is already heavily band limited, so the widening stays moderate.
///
/// The delayed copy makes this a comb filter on the side signal: individual tones can land
/// on a cancelling phase, but broadband material gains roughly sqrt(SideBoost^2 +
/// DelayedSideMix^2) of extra side energy, which is where the sense of space comes from.
/// </summary>
internal sealed class SurroundProcessor
{
    private const double DelaySeconds = 0.014;
    private const float SideBoost = 1.5f;
    private const float DelayedSideMix = 0.6f;
    private const float BassSplitHz = 250f;
    private const float ClipKnee = 0.8f;

    private float[] _delay = [];
    private int _writeIndex;
    private double _sampleRate;
    private float _bassState;
    private float _bassCoefficient;
    private volatile bool _enabled;
    private volatile bool _resetPending;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            // The buffers belong to the audio thread, so the UI only asks for the reset.
            _resetPending = true;
        }
    }

    /// <summary>Sizes the delay line for the output rate. Called from the audio thread.</summary>
    public void Configure(double sampleRate)
    {
        if (sampleRate <= 0 || Math.Abs(sampleRate - _sampleRate) < 0.5) return;
        _sampleRate = sampleRate;
        _delay = new float[Math.Max(1, (int)(sampleRate * DelaySeconds))];
        _bassCoefficient = (float)(1.0 - Math.Exp(-2.0 * Math.PI * BassSplitHz / sampleRate));
        ClearState();
    }

    public void Reset() => _resetPending = true;

    /// <summary>
    /// Widens one stereo frame in place. Mid and side are separated, the side is boosted and
    /// mixed with a delayed copy, and the low end stays centred so the result does not hollow
    /// out or cancel when someone listens in mono.
    /// </summary>
    public void Process(ref float left, ref float right)
    {
        if (_resetPending)
        {
            ClearState();
            _resetPending = false;
        }

        if (!_enabled || _delay.Length == 0) return;

        var mid = (left + right) * 0.5f;
        var side = (left - right) * 0.5f;

        // One pole split: everything below BassSplitHz is put back untouched further down.
        _bassState += _bassCoefficient * (side - _bassState);
        var wideSide = side - _bassState;

        var delayed = _delay[_writeIndex];
        _delay[_writeIndex] = wideSide;
        if (++_writeIndex >= _delay.Length) _writeIndex = 0;

        // Only the side path is scaled. Leaving mid at unity keeps the perceived loudness
        // steady when the button is toggled, so the effect reads as width, not as volume.
        var spread = wideSide * SideBoost + delayed * DelayedSideMix + _bassState;
        left = SoftClip(mid + spread);
        right = SoftClip(mid - spread);
    }

    /// <summary>
    /// Empties the delay line and the filter memory, so a new station does not inherit the
    /// tail of the previous one.
    /// </summary>
    private void ClearState()
    {
        Array.Clear(_delay);
        _writeIndex = 0;
        _bassState = 0;
    }

    /// <summary>Bends the peaks a widened side signal can push past full scale, rather than wrapping them.</summary>
    private static float SoftClip(float sample)
    {
        var magnitude = Math.Abs(sample);
        if (magnitude <= ClipKnee) return sample;
        var excess = magnitude - ClipKnee;
        var limited = ClipKnee + excess / (1f + excess / (1f - ClipKnee));
        return sample < 0 ? -limited : limited;
    }
}
