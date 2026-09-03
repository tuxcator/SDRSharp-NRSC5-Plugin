using SDRSharp.Radio;

namespace SDRSharp.NRSC5;

/// <summary>
/// The two taps this plugin puts into SDR#'s signal chain. SDR# owns the receiver and
/// calls these on its own threads, so both are deliberately thin: they check a flag and
/// hand the buffer straight to the engine. Anything slow here would stall the audio path
/// of the whole application, not just this plugin.
/// </summary>
internal sealed class IqHook : IIQProcessor
{
    private readonly Nrsc5Engine _engine;

    public IqHook(Nrsc5Engine engine) => _engine = engine;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// SDR# pushes the rate in whenever the source changes. The engine needs it to know
    /// how far it has to resample down to the 744187.5 S/s the decoder expects.
    /// </summary>
    public double SampleRate { set => _engine.InputSampleRate = value; }

    /// <summary>
    /// Raw IQ, before demodulation. Registered as <c>ProcessorType.RawIQ</c>, which is
    /// the only tap that still carries the digital sidebands: by the time SDR# has
    /// demodulated FM they are gone.
    /// </summary>
    public unsafe void Process(Complex* buffer, int length)
    {
        if (Enabled) _engine.ProcessIq(buffer, length);
    }
}

/// <summary>
/// The audio tap, registered as <c>ProcessorType.MonitorAF</c>. This is where the plugin
/// substitutes decoded HD audio for the analog programme SDR# produced, by overwriting
/// the buffer in place.
/// </summary>
internal sealed class AudioHook : IRealProcessor
{
    private readonly Nrsc5Engine _engine;

    public AudioHook(Nrsc5Engine engine) => _engine = engine;

    public bool Enabled { get; set; } = true;

    /// <summary>The output rate, which the engine resamples the 44.1 kHz HD audio to.</summary>
    public double SampleRate { set => _engine.OutputSampleRate = value; }

    public unsafe void Process(float* buffer, int length)
    {
        if (Enabled) _engine.ProcessAudio(buffer, length);
    }
}
