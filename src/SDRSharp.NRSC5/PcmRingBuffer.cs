namespace SDRSharp.NRSC5;

internal sealed class PcmRingBuffer
{
    private readonly object _gate = new();
    private readonly float[] _samples;
    private int _read;
    private int _write;
    private int _count;

    public PcmRingBuffer(int frames)
    {
        _samples = new float[Math.Max(4096, frames * 2)];
    }

    public int AvailableFrames
    {
        get { lock (_gate) return _count / 2; }
    }

    public void Write(short[] source)
    {
        lock (_gate)
        {
            var required = Math.Min(source.Length, _samples.Length);
            while (_count + required > _samples.Length)
            {
                _read = (_read + 2) % _samples.Length;
                _count = Math.Max(0, _count - 2);
            }

            var start = Math.Max(0, source.Length - required);
            for (var i = start; i < source.Length; i++)
            {
                _samples[_write] = source[i] / 32768f;
                _write = (_write + 1) % _samples.Length;
                if (_count < _samples.Length) _count++;
            }
        }
    }

    public bool TryReadFrame(out float left, out float right)
    {
        lock (_gate)
        {
            if (_count < 2)
            {
                left = right = 0;
                return false;
            }

            left = _samples[_read];
            _read = (_read + 1) % _samples.Length;
            right = _samples[_read];
            _read = (_read + 1) % _samples.Length;
            _count -= 2;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _read = _write = _count = 0;
        }
    }
}
