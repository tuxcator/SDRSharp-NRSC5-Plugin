namespace SDRSharp.NRSC5;

internal sealed class PcmRingBuffer
{
    private readonly object _gate = new();
    private float[] _samples;
    private int _read;
    private int _write;
    private int _count;

    public PcmRingBuffer(int frames)
    {
        _samples = new float[Capacity(frames)];
    }

    public int AvailableFrames
    {
        get { lock (_gate) return _count / 2; }
    }

    public int CapacityFrames
    {
        get { lock (_gate) return _samples.Length / 2; }
    }

    /// <summary>
    /// Grows the ring so it can hold <paramref name="frames"/> stereo frames. Used when
    /// the user raises the audio buffer length; shrinking is not worth the discontinuity,
    /// so the ring only ever grows during a session.
    /// </summary>
    public void EnsureCapacityFrames(int frames)
    {
        var wanted = Capacity(frames);
        lock (_gate)
        {
            if (_samples.Length >= wanted) return;
            _samples = new float[wanted];
            _read = _write = _count = 0;
        }
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

    private static int Capacity(int frames) => Math.Max(4096, frames * 2);
}
