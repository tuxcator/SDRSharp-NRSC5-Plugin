namespace SDRSharp.NRSC5;

/// <summary>A continuous complex oscillator, owned by the decoder thread.</summary>
internal sealed class IqMixer
{
    private double _real = 1;
    private double _imag;
    private int _sinceNormalize;

    public void Reset()
    {
        _real = 1;
        _imag = 0;
        _sinceNormalize = 0;
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output, double offsetHz, double sampleRate)
    {
        if ((input.Length & 1) != 0 || output.Length < input.Length)
            throw new ArgumentException("IQ buffers must contain complete complex samples.");

        // At DC the oscillator must retain its phase after a spectrum recenter.
        // Only the initial/reset unity phase is an exact pass-through.
        if (offsetHz == 0 && _real == 1 && _imag == 0)
        {
            input.CopyTo(output);
            return;
        }

        var (stepImag, stepReal) = Math.SinCos(-2 * Math.PI * offsetHz / sampleRate);
        var real = _real;
        var imag = _imag;
        var sinceNormalize = _sinceNormalize;
        for (var index = 0; index < input.Length; index += 2)
        {
            var i = input[index];
            var q = input[index + 1];
            var cos = (float)real;
            var sin = (float)imag;
            output[index] = i * cos - q * sin;
            output[index + 1] = i * sin + q * cos;

            var nextReal = real * stepReal - imag * stepImag;
            imag = real * stepImag + imag * stepReal;
            real = nextReal;
            // Bound roundoff over long listening sessions without trig per sample.
            if (++sinceNormalize == 4096)
            {
                var gain = 1 / Math.Sqrt(real * real + imag * imag);
                real *= gain;
                imag *= gain;
                sinceNormalize = 0;
            }
        }
        _real = real;
        _imag = imag;
        _sinceNormalize = sinceNormalize;
    }
}
