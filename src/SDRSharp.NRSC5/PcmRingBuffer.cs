namespace SDRSharp.NRSC5;

/// <summary>
/// The prebuffer that sits between the decoder and the sound card. Two threads meet
/// here and never at a predictable rate: libnrsc5 delivers HD audio in bursts as whole
/// frames finish decoding, while SDR# asks for samples on a steady clock. The ring
/// absorbs that mismatch, which is what lets HD audio ride out a brief fade instead of
/// dropping back to the analog programme.
///
/// Samples are stored interleaved, left then right, so a "frame" is two of them.
/// Everything is under one lock: the buffers are small and the contention is between
/// exactly two threads, so a lock-free design would buy nothing but bugs.
/// </summary>
internal sealed class PcmRingBuffer
{
    private readonly object _gate = new();
    private float[] _samples;
    private int _read;
    private int _write;
    private int _count;

    // Running totals in frames since the ring was created. Written minus consumed is always
    // what the ring holds, so a position stamped at write time tells exactly when that
    // audio reaches the speakers: once consumed has caught up with it. Consumed counts
    // everything that left the ring, played or dropped, so a skipped frame cannot leave a
    // stamp waiting for a position that playback will never pass through.
    private long _totalWritten;
    private long _totalConsumed;

    public PcmRingBuffer(int frames)
    {
        _samples = new float[Capacity(frames)];
    }

    /// <summary>Stereo frames ready to play. The panel shows this as the buffer fill.</summary>
    public int AvailableFrames
    {
        get { lock (_gate) return _count / 2; }
    }

    public int CapacityFrames
    {
        get { lock (_gate) return _samples.Length / 2; }
    }

    /// <summary>Frames ever written. Metadata decoded now belongs to the audio at this position.</summary>
    public long TotalWrittenFrames
    {
        get { lock (_gate) return _totalWritten; }
    }

    /// <summary>Frames ever played or dropped: how far the listener has got.</summary>
    public long TotalConsumedFrames
    {
        get { lock (_gate) return _totalConsumed; }
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
            _totalConsumed = _totalWritten;
        }
    }

    /// <summary>
    /// Takes a block of 16-bit stereo PCM from the decoder and converts it to float.
    /// When the ring is full the oldest frame is dropped rather than the newest: falling
    /// behind means the listener is hearing stale audio, and catching up matters more
    /// than keeping every sample. Dropping is done a frame at a time so the left and
    /// right channels can never come apart.
    /// </summary>
    public void Write(ReadOnlySpan<short> source)
    {
        // Ignore an incomplete trailing stereo frame, preserving channel alignment.
        source = source[..(source.Length & ~1)];
        lock (_gate)
        {
            var required = Math.Min(source.Length, _samples.Length);
            var dropped = Math.Max(0, _count + required - _samples.Length);
            if (dropped > 0)
            {
                _read = (_read + dropped) % _samples.Length;
                _count -= dropped;
            }
            // A block larger than the whole ring keeps only its tail; the head never
            // enters, so it is written and consumed in the same breath.
            _totalWritten += source.Length / 2;
            _totalConsumed += (dropped + source.Length - required) / 2;

            source = source[^required..];
            var first = Math.Min(required, _samples.Length - _write);
            Convert(source[..first], _samples.AsSpan(_write, first));
            Convert(source[first..], _samples.AsSpan(0, required - first));
            _write = (_write + required) % _samples.Length;
            _count += required;
        }
    }

    private static void Convert(ReadOnlySpan<short> source, Span<float> destination)
    {
        for (var i = 0; i < source.Length; i++) destination[i] = source[i] * (1f / 32768f);
    }

    /// <summary>
    /// Pulls one stereo frame. Returns false when the ring has run dry, which is the
    /// signal the caller uses to fall back to the analog programme.
    /// </summary>
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
            right = _samples[_read + 1];
            _read += 2;
            if (_read == _samples.Length) _read = 0;
            _count -= 2;
            _totalConsumed++;
            return true;
        }
    }

    /// <summary>Drops everything held. Used on retune, so a new station never plays the previous one.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _read = _write = _count = 0;
            _totalConsumed = _totalWritten;
        }
    }

    /// <summary>Two samples per frame, with a floor so a tiny buffer setting still works.</summary>
    private static int Capacity(int frames) => Math.Max(4096, frames * 2);
}
