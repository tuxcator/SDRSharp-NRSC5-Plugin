using SDRSharp.Radio;

namespace SDRSharp.NRSC5;

internal sealed class IqHook : IIQProcessor
{
    private readonly Nrsc5Engine _engine;
    public IqHook(Nrsc5Engine engine) => _engine = engine;
    public bool Enabled { get; set; } = true;
    public double SampleRate { set => _engine.InputSampleRate = value; }
    public unsafe void Process(Complex* buffer, int length)
    {
        if (Enabled) _engine.ProcessIq(buffer, length);
    }
}

internal sealed class AudioHook : IRealProcessor
{
    private readonly Nrsc5Engine _engine;
    public AudioHook(Nrsc5Engine engine) => _engine = engine;
    public bool Enabled { get; set; } = true;
    public double SampleRate { set => _engine.OutputSampleRate = value; }
    public unsafe void Process(float* buffer, int length)
    {
        if (Enabled) _engine.ProcessAudio(buffer, length);
    }
}
