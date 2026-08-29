namespace SDRSharp.NRSC5;

/// <summary>
/// Arbitrary-ratio complex resampler built on a Kaiser-windowed sinc polyphase bank.
///
/// The previous implementation interpolated linearly, which has no usable stopband:
/// at the 768 kS/s of an Airspy HF+ the decimation ratio is only 1.03 and the damage
/// is small, but an RTL-SDR at 1.2 or 2.4 MS/s folds everything above 372 kHz straight
/// back onto the digital sidebands. The bank below places a real anti-alias filter in
/// front of the decimation, with the cutoff tracking whichever rate is lower.
///
/// Callers must mix the wanted carrier down to DC *before* resampling, otherwise the
/// filter removes the very signal being tuned.
/// </summary>
internal sealed class PolyphaseResampler
{
    private const int Phases = 512;
    private const double KaiserBeta = 8.6;

    // A fixed tap count would keep the transition band a constant fraction of the *input*
    // rate, so the faster the input the wider the skirt in Hz. At 2.4 MS/s a 24-tap design
    // only reaches -12 dB by 400 kHz, which lets an adjacent channel fold straight onto the
    // sidebands. Scaling the length with the decimation ratio keeps the skirt ~75 kHz wide
    // in absolute terms whatever the front end is running at.
    private const int MinTaps = 32;
    private const int MaxTaps = 160;

    private float[] _bank = Array.Empty<float>();
    private int _taps;
    private int _leftWing;
    private int _rightWing;
    private float[] _work = Array.Empty<float>();
    private int _workPairs;
    private double _inputRate;
    private double _outputRate;
    private double _step = 1;
    private double _position;
    private bool _primed;

    /// <summary>
    /// Rebuilds the filter bank when either rate changes. Rebuilding is deliberately
    /// tied to a rate change only: re-assigning the same rate must not disturb the
    /// running phase, or the decoder loses lock every time SDR# re-announces its rate.
    /// </summary>
    public void Configure(double inputRate, double outputRate)
    {
        if (inputRate <= 0 || outputRate <= 0) return;
        if (Math.Abs(inputRate - _inputRate) < 0.5 && Math.Abs(outputRate - _outputRate) < 0.5) return;

        _inputRate = inputRate;
        _outputRate = outputRate;
        _step = inputRate / outputRate;

        var ratio = Math.Max(1.0, inputRate / outputRate);
        _taps = Math.Clamp((int)Math.Ceiling(32 * ratio / 2) * 2, MinTaps, MaxTaps);
        _leftWing = _taps / 2 - 1;
        _rightWing = _taps / 2;
        if (_bank.Length < Phases * _taps) _bank = new float[Phases * _taps];

        BuildBank(Math.Min(1.0, outputRate / inputRate) * 0.45);
        Reset();
    }

    public void Reset()
    {
        _workPairs = 0;
        _position = 0;
        _primed = false;
    }

    /// <summary>
    /// Consumes <paramref name="complexCount"/> interleaved complex samples and appends
    /// the resampled result to <paramref name="output"/>, returning the number of complex
    /// samples produced. History is carried across calls so the stream stays continuous.
    /// </summary>
    public int Process(float[] input, int complexCount, ref float[] output)
    {
        if (complexCount <= 0 || _inputRate <= 0) return 0;

        Append(input, complexCount);

        // The first block has no past to filter against. Pre-load the window with the
        // leading sample so the very first outputs are not a fade-in from silence.
        if (!_primed)
        {
            _primed = true;
            _position = _leftWing;
        }

        var maxOut = (int)((_workPairs - _position - _rightWing) / _step) + 2;
        if (maxOut < 0) maxOut = 0;
        EnsureCapacity(ref output, maxOut * 2);

        var produced = 0;
        while (true)
        {
            var baseIndex = (int)Math.Floor(_position);
            if (baseIndex + _rightWing >= _workPairs) break;
            if (baseIndex < _leftWing) { _position = _leftWing; continue; }

            var frac = _position - baseIndex;
            var phase = (int)(frac * Phases);
            if (phase >= Phases) phase = Phases - 1;

            var coefficients = phase * _taps;
            var start = (baseIndex - _leftWing) * 2;
            float real = 0, imag = 0;
            for (var tap = 0; tap < _taps; tap++)
            {
                var h = _bank[coefficients + tap];
                real += h * _work[start + tap * 2];
                imag += h * _work[start + tap * 2 + 1];
            }

            if (produced * 2 + 1 >= output.Length) EnsureCapacity(ref output, (produced + 1) * 2);
            output[produced * 2] = real;
            output[produced * 2 + 1] = imag;
            produced++;
            _position += _step;
        }

        Consume();
        return produced;
    }

    private void Append(float[] input, int complexCount)
    {
        var required = (_workPairs + complexCount) * 2;
        if (_work.Length < required) Array.Resize(ref _work, Math.Max(required, _work.Length * 2));
        Array.Copy(input, 0, _work, _workPairs * 2, complexCount * 2);
        _workPairs += complexCount;
    }

    /// <summary>Drops the input samples no future output can reach, keeping the filter window.</summary>
    private void Consume()
    {
        var keepFrom = (int)Math.Floor(_position) - _leftWing;
        if (keepFrom <= 0) return;
        if (keepFrom > _workPairs) keepFrom = _workPairs;

        var remaining = _workPairs - keepFrom;
        if (remaining > 0) Array.Copy(_work, keepFrom * 2, _work, 0, remaining * 2);
        _workPairs = remaining;
        _position -= keepFrom;
    }

    private static void EnsureCapacity(ref float[] buffer, int length)
    {
        if (buffer.Length < length) Array.Resize(ref buffer, Math.Max(length, buffer.Length * 2));
    }

    /// <summary>
    /// Builds <see cref="Phases"/> fractionally-delayed copies of the same windowed sinc.
    /// Each phase is normalised to unity DC gain so the output level does not depend on
    /// where a sample happens to land between two input samples.
    /// </summary>
    private void BuildBank(double normalisedCutoff)
    {
        var i0Beta = BesselI0(KaiserBeta);
        var halfWidth = _taps / 2.0;

        for (var phase = 0; phase < Phases; phase++)
        {
            var frac = (double)phase / Phases;
            var offset = phase * _taps;
            double sum = 0;

            for (var tap = 0; tap < _taps; tap++)
            {
                var t = tap - _leftWing - frac;
                var sinc = 2 * normalisedCutoff * Sinc(2 * normalisedCutoff * t);

                var ratio = t / halfWidth;
                if (ratio < -1) ratio = -1;
                else if (ratio > 1) ratio = 1;
                var window = BesselI0(KaiserBeta * Math.Sqrt(1 - ratio * ratio)) / i0Beta;

                var value = sinc * window;
                _bank[offset + tap] = (float)value;
                sum += value;
            }

            if (Math.Abs(sum) < 1e-12) continue;
            var gain = (float)(1.0 / sum);
            for (var tap = 0; tap < _taps; tap++) _bank[offset + tap] *= gain;
        }
    }

    private static double Sinc(double x) =>
        Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1;
        var halfSquared = x * x / 4;
        for (var k = 1; k < 40; k++)
        {
            term *= halfSquared / (k * (double)k);
            sum += term;
            if (term < sum * 1e-16) break;
        }
        return sum;
    }
}
