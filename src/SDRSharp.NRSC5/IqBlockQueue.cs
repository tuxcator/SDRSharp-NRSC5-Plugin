namespace SDRSharp.NRSC5;

/// <summary>
/// Hands IQ from SDR#'s signal thread to the plugin's own decoder thread.
///
/// libnrsc5 in pipe mode has no thread of its own: <c>nrsc5_pipe_samples_cf32</c> runs
/// acquisition, sync, Viterbi and the HDC codec inside the call. Up to Dev 3.3.5 that call
/// was made from SDR#'s IQ callback, so every millisecond of decoding was a millisecond
/// SDR#'s own DSP could not use - measured at 9 ms of every 36 ms block while searching
/// for sync. Behind this queue the callback only copies the block and returns, and the
/// decoding runs on another core.
///
/// Buffers are pooled, so a steady stream allocates nothing. The queue is bounded: if the
/// decoder cannot keep up, new blocks are dropped rather than letting memory and latency
/// grow without limit. A dropped block costs the decoder its lock; an unbounded queue
/// would cost it everything, later.
/// </summary>
internal sealed class IqBlockQueue : IDisposable
{
    /// <summary>One captured block: interleaved I/Q, and the conditions it was captured under.</summary>
    internal readonly record struct Block(float[] Buffer, int Floats, double Rate, double Offset, int Generation);

    private readonly object _gate = new();
    private readonly Queue<Block> _queue = new();
    private readonly Stack<float[]> _pool = new();
    private readonly SemaphoreSlim _signal = new(0);
    private int _queuedFloats;
    private long _dropped;

    /// <summary>Ceiling on how much IQ may wait, in floats. Set from the sample rate.</summary>
    public int MaxQueuedFloats { get; set; } = 2 * 1_048_576;

    /// <summary>Blocks refused because the decoder had fallen behind.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public int QueuedFloats
    {
        get { lock (_gate) return _queuedFloats; }
    }

    /// <summary>
    /// Copies a block in. Called on SDR#'s thread, so it never waits for the decoder:
    /// the copy happens outside the lock, and a full queue is a refusal, not a block.
    /// </summary>
    public bool TryEnqueue(ReadOnlySpan<float> samples, double rate, double offset, int generation)
    {
        if (samples.IsEmpty) return false;

        float[] buffer;
        lock (_gate)
        {
            if (_queuedFloats + samples.Length > MaxQueuedFloats)
            {
                _dropped++;
                return false;
            }
            // Reserved now, so the ceiling holds even though the copy happens unlocked.
            _queuedFloats += samples.Length;
            buffer = RentLocked(samples.Length);
        }

        samples.CopyTo(buffer);

        lock (_gate) _queue.Enqueue(new Block(buffer, samples.Length, rate, offset, generation));
        _signal.Release();
        return true;
    }

    /// <summary>Waits up to <paramref name="timeoutMs"/> for a block. The caller must <see cref="Return"/> it.</summary>
    public bool TryDequeue(int timeoutMs, out Block block)
    {
        block = default;
        if (!_signal.Wait(timeoutMs)) return false;
        lock (_gate)
        {
            // A Clear() can empty the queue after the signal was raised.
            if (_queue.Count == 0) return false;
            block = _queue.Dequeue();
            _queuedFloats -= block.Floats;
            return true;
        }
    }

    public void Return(float[] buffer)
    {
        lock (_gate) _pool.Push(buffer);
    }

    /// <summary>Wakes a waiting decoder without giving it a block, so it can notice it is being stopped.</summary>
    public void Wake() => _signal.Release();

    /// <summary>
    /// Drops everything waiting. Used on retune: IQ from the previous frequency must never
    /// reach a decoder that has just been reset for the new one.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            // Subtract only what is removed. A block still being copied has its floats
            // reserved but is not in the queue yet; zeroing the count would let that
            // block drive it negative when it is taken, and the ceiling would stop holding.
            while (_queue.Count > 0)
            {
                var block = _queue.Dequeue();
                _queuedFloats -= block.Floats;
                _pool.Push(block.Buffer);
            }
        }
    }

    public void Dispose() => _signal.Dispose();

    /// <summary>Caller holds <see cref="_gate"/>. Buffers are sized generously so they fit the next block too.</summary>
    private float[] RentLocked(int floats)
    {
        while (_pool.Count > 0)
        {
            var candidate = _pool.Pop();
            if (candidate.Length >= floats) return candidate;
            // Too small for this rate: let it go rather than keep a buffer nobody can use.
        }
        return new float[Math.Max(floats, 65536)];
    }
}
