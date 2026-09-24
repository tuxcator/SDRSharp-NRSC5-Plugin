using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SDRSharp.Radio;

namespace SDRSharp.NRSC5;

internal sealed record Nrsc5Status(
    bool Synced,
    string Message,
    string Station,
    string Title,
    string Artist,
    string Album,
    float MerLower,
    float MerUpper,
    float Ber,
    double InputRate,
    double OffsetHz,
    float SignalDbfs,
    float PeakDbfs,
    float EstimatedDbm,
    float SnrDb,
    float BitrateKbps,
    byte[]? Artwork,
    bool ArtworkIsStationLogo,
    int ProgramMask,
    int SelectedProgram,
    float BufferedSeconds,
    float BufferTargetSeconds,
    float DecoderLoad,
    float IqThreadLoad,
    long DroppedBlocks)
{
    public static Nrsc5Status Idle { get; } = new(
        false, "Disabled", "", "", "", "", 0, 0, 0, 0, 0,
        -120, -120, -150, 0, 0, null, false, 0, 0, 0, 0, 0, 0, 0);

    public bool HasProgram(int index) => (ProgramMask & (1 << index)) != 0;
}

internal sealed class Nrsc5Engine : IDisposable
{
    private const int SignalProbeSamples = 256;
    private const int MinSyncLossGraceMs = 1500;
    private const int MaxArtworkBytes = 8 * 1024 * 1024;
    private const int MaxCachedImages = 32;
    // Metadata waits for its audio to be heard, but never longer than the largest buffer
    // plus a margin: if playback stalls, a title shown late beats one never shown.
    private static readonly long MaxPresentationDelayTicks =
        (long)(Stopwatch.Frequency * (MaxBufferSeconds + 2.0));
    private const int MaxAlerts = 16;
    // Ramp used whenever the output changes source, short enough not to be heard as a dip.
    private const double FadeSeconds = 0.02;
    private const double MinSwitchHoldSeconds = 3.0;

    // Anything further than this is a different station rather than a nudge to peak the
    // current one. FM channels sit at least 100 kHz apart everywhere this decoder applies.
    private const long StationStepHz = 50_000;

    internal const double MinBufferSeconds = 0.1;
    internal const double MaxBufferSeconds = 10.0;
    internal const double DefaultBufferSeconds = 0.75;

    private readonly object _sessionGate = new();
    private readonly object _iqGate = new();
    private readonly object _audioGate = new();
    private readonly object _statusGate = new();
    private readonly object _artworkGate = new();
    private readonly object _bitrateGate = new();
    private readonly object _factsGate = new();
    private readonly object _hereGate = new();
    private readonly FccStationDirectory _fccDirectory = new();
    private readonly ReverseGeocoder _geocoder = new();
    private readonly PcmRingBuffer _audio = new((int)(Nrsc5Native.AudioSampleRate * 3));
    private readonly PolyphaseResampler _resampler = new();
    private readonly SurroundProcessor _surround = new();
    private readonly Nrsc5Native.EventCallback _callback;
    private readonly System.Threading.Timer _retuneTimer;
    private readonly System.Threading.Timer _syncLossTimer;

    // Artwork caches. LOT ids are only unique within a service port, so the cache is
    // keyed by both; a bare lot id collides between subchannels of the same station.
    private readonly Dictionary<(int Port, int Lot), CachedImage> _lotImages = new();
    // Which program each data port belongs to, from the SIG table. A LOT that arrives
    // before the SIG has no service to name its owner; its port still names it once the
    // SIG is in. Without this, such images were pinned to whichever program happened to
    // be selected, which is how one subchannel's logo ended up on another.
    private readonly Dictionary<int, int> _portProgram = new();
    // What the SIG table says each data port carries. XHTKR 103.7 sends its logos as plain
    // JPEG and PNG files ("SLXHTKR$020001.png"), not under the station-logo MIME type, so
    // the file alone does not say it is a logo; the port it arrives on does.
    private readonly Dictionary<int, uint> _portMime = new();
    // Whether each program has ever linked an image with XHDR parameter 0. Only then is its
    // XHDR trusted; see ArtworkResolver for the station that made this necessary.
    private readonly bool[] _programLinksImages = new bool[8];
    private byte[]? _tracedArtwork;
    private long _lotSequence;

    // Now-playing metadata: what was last decoded per program, what the listener is
    // hearing now, and what has been decoded but is still waiting in the prebuffer.
    private readonly object _trackGate = new();
    private readonly TrackInfo?[] _receivedTracks = new TrackInfo?[8];
    private readonly TrackInfo?[] _presentedTracks = new TrackInfo?[8];
    private readonly PresentationQueue<TrackInfo> _pendingTracks = new();

    // The decoder thread. SDR#'s IQ callback only queues the block; mixing, resampling
    // and libnrsc5 all run on this thread, off SDR#'s signal path.
    private readonly IqBlockQueue _iqQueue = new();
    private Thread? _decoderThread;
    private volatile bool _decoderStopping;
    private int _iqGeneration;
    private long _decodeTicks;
    private long _iqThreadTicks;
    private double _accountedSignalSeconds;
    private long _loadWindowStart = Stopwatch.GetTimestamp();
    private int _loadWindows;
    private int _iqThreadTraced;

    private IntPtr _session;
    private bool _disposed;
    private bool _enabled;
    private bool _replaceAnalogAudio = true;
    private bool _bufferingEnabled = true;
    private double _bufferSeconds = DefaultBufferSeconds;
    private int _selectedProgram;
    private int _programMask;
    private double _inputSampleRate;
    private double _outputSampleRate;
    private double _tuningOffset;
    private readonly IqMixer _mixer = new();
    private float[] _mixed = new float[65536];
    private float[] _iqOutput = new float[65536];
    private bool _haveAudioPair;
    private float _audioLeftA, _audioRightA, _audioLeftB, _audioRightB;
    private double _audioPhase;
    private bool _hdAudioActive;
    private bool _switchingProgram;
    private long _switchDeadlineTicks;
    private float _outputGain = 1f;
    private long _lastDigitalTicks;
    private long _lastSignalTicks;
    private long _lastTunedFrequency;
    private float _smoothedDbfs = -120;
    private float _dbmCalibrationOffset = -30;
    private long _bitrateBytes;
    private long _bitrateStartedTicks = Stopwatch.GetTimestamp();
    private Nrsc5Status _status = Nrsc5Status.Idle;
    private StationFacts _facts = StationFacts.Empty;
    private int _lookedUpFacilityId;
    private string _geocodedSite = "";
    private HereData _here = HereData.Empty;
    private readonly List<HereTile> _trafficTiles = [];
    private int _trafficSequence = -1;
    private CancellationTokenSource? _lookupCancellation;

    /// <summary>
    /// A received LOT image. <see cref="Sequence"/> orders arrivals across all ports, and
    /// <see cref="ComponentMime"/> is what the SIG table says the port carries, when known.
    /// </summary>
    private readonly record struct CachedImage(byte[] Bytes, uint Mime, uint ComponentMime, int Program, long Sequence);

    public Nrsc5Engine()
    {
        _callback = OnNativeEvent;
        _retuneTimer = new System.Threading.Timer(_ => { if (Enabled) Restart(); }, null, Timeout.Infinite, Timeout.Infinite);
        _syncLossTimer = new System.Threading.Timer(_ => ConfirmSyncLoss(), null, Timeout.Infinite, Timeout.Infinite);
        ApplyBufferCapacity();
        // Seed the buffer target so a panel attaching later reads the real value
        // instead of the zero in Nrsc5Status.Idle, which renders as "BUFFER OFF".
        PublishBufferState();
    }

    public event Action<Nrsc5Status>? StatusChanged;

    /// <summary>
    /// Raised only when the station's own identity changes, which is a handful of times
    /// per tune. It is deliberately not folded into <see cref="StatusChanged"/>, which
    /// fires about ten times a second to carry the signal meters.
    /// </summary>
    public event Action<StationFacts>? StationFactsChanged;

    public Nrsc5Status Status
    {
        get { lock (_statusGate) return _status; }
    }

    public StationFacts Facts
    {
        get { lock (_factsGate) return _facts; }
    }

    /// <summary>
    /// Raised when a traffic or weather map advances, or an alert arrives. Separate from
    /// the status event because the map window is the only thing that cares, and a
    /// traffic mosaic changes a handful of times a minute at most.
    /// </summary>
    public event Action<HereData>? HereDataChanged;

    public HereData HereData
    {
        get { lock (_hereGate) return _here; }
    }

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled);
        set
        {
            if (_disposed || value == _enabled) return;
            Volatile.Write(ref _enabled, value);
            if (value) Start();
            else Stop("Disabled");
        }
    }

    public bool ReplaceAnalogAudio
    {
        get => Volatile.Read(ref _replaceAnalogAudio);
        set => Volatile.Write(ref _replaceAnalogAudio, value);
    }

    /// <summary>
    /// Widens the decoded HD stereo image. It only ever touches the samples this plugin
    /// writes, so the analog audio SDR# produces on its own is left alone.
    /// </summary>
    public bool SurroundEnabled
    {
        get => _surround.Enabled;
        set => _surround.Enabled = value;
    }

    /// <summary>
    /// When off, HD audio starts as soon as a single block is decoded. That minimises
    /// latency but makes the analog/HD switch chatter on a marginal signal.
    /// </summary>
    public bool BufferingEnabled
    {
        get { lock (_audioGate) return _bufferingEnabled; }
        set
        {
            lock (_audioGate)
            {
                if (_bufferingEnabled == value) return;
                _bufferingEnabled = value;
            }
            ApplyBufferCapacity();
            PublishBufferState();
        }
    }

    /// <summary>Target HD prebuffer, in seconds, honoured only while buffering is enabled.</summary>
    public double BufferSeconds
    {
        get { lock (_audioGate) return _bufferSeconds; }
        set
        {
            value = Math.Clamp(value, MinBufferSeconds, MaxBufferSeconds);
            lock (_audioGate)
            {
                if (Math.Abs(_bufferSeconds - value) < 0.001) return;
                _bufferSeconds = value;
                // A shorter target takes effect immediately; a longer one refills first.
                _hdAudioActive = false;
            }
            ApplyBufferCapacity();
            PublishBufferState();
        }
    }

    public int ProgramMask => Volatile.Read(ref _programMask);

    public int SelectedProgram
    {
        get => Volatile.Read(ref _selectedProgram);
        set
        {
            value = Math.Clamp(value, 0, 7);
            Volatile.Write(ref _selectedProgram, value);
            ResetAudio();
            BeginProgramSwitch();
            ResetBitrate();
            // The prebuffer was just emptied, so nothing is waiting to be heard. The new
            // program's latest metadata describes what it is broadcasting now.
            TrackInfo? latest;
            lock (_trackGate)
            {
                _pendingTracks.Clear();
                latest = _receivedTracks[value];
            }
            UpdateStatus(s => s with
            {
                Title = "",
                Artist = "",
                Album = "",
                BitrateKbps = 0,
                SelectedProgram = value,
                Message = s.Synced ? $"Synchronized HD{value + 1}" : s.Message
            });
            if (latest is not null) PresentTrack(latest);
            else RefreshArtwork();
        }
    }

    /// <summary>
    /// Steps to the next subchannel the station actually broadcasts. Until a SIG table or
    /// an audio service descriptor arrives nothing is known, so it falls back to plain
    /// cycling rather than trapping the user on HD1.
    /// </summary>
    public void StepProgram(int direction)
    {
        if (direction == 0) return;
        var mask = ProgramMask;
        var current = SelectedProgram;

        if (mask == 0)
        {
            SelectedProgram = (current + direction + 8) % 8;
            return;
        }

        for (var hop = 1; hop <= 8; hop++)
        {
            var candidate = ((current + direction * hop) % 8 + 8) % 8;
            if ((mask & (1 << candidate)) == 0) continue;
            SelectedProgram = candidate;
            return;
        }
    }

    public double InputSampleRate
    {
        get => Volatile.Read(ref _inputSampleRate);
        set
        {
            var previous = Volatile.Read(ref _inputSampleRate);
            if (Math.Abs(previous - value) < 0.5) return;
            Volatile.Write(ref _inputSampleRate, value);
            ResetIq();
        }
    }

    public double OutputSampleRate
    {
        get => Volatile.Read(ref _outputSampleRate);
        set => Volatile.Write(ref _outputSampleRate, value);
    }

    public float DbmCalibrationOffset
    {
        get => Volatile.Read(ref _dbmCalibrationOffset);
        set
        {
            value = Math.Clamp(value, -100, 20);
            Volatile.Write(ref _dbmCalibrationOffset, value);
            UpdateStatus(s => s with { EstimatedDbm = s.SignalDbfs + value });
        }
    }

    /// <summary>
    /// How far the VFO sits from the centre of the spectrum. The decoder always works at
    /// baseband, so this is the frequency the mixer has to shift the IQ down by.
    /// </summary>
    public void SetTuningOffset(double value)
    {
        Volatile.Write(ref _tuningOffset, value);
        UpdateStatus(s => s with { OffsetHz = value });
    }

    public void NotifyFrequencyChanged(long frequency)
    {
        // Every new station starts on HD1. The subchannel line-up belongs to the station,
        // not to the listener, so carrying an HD2 or HD3 choice over to a station that only
        // broadcasts HD1 just leaves the decoder waiting for audio that never comes while
        // the analog path plays. Fine tuning the same station keeps the current subchannel.
        var previous = Interlocked.Exchange(ref _lastTunedFrequency, frequency);
        if (previous == 0 || Math.Abs(frequency - previous) > StationStepHz)
        {
            Volatile.Write(ref _selectedProgram, 0);
            // Fine tuning keeps the station facts on screen: SIS repeats slowly, and
            // blanking them on every nudge of the dial would make the panel flicker.
            ResetStationFacts();
            ResetHereData();
        }

        CancelPendingSyncLoss();
        ResetAudio();
        ResetMetadata();
        UpdateStatus(_ => Nrsc5Status.Idle with
        {
            Message = $"Tuning {frequency / 1_000_000.0:0.0} MHz...",
            InputRate = InputSampleRate,
            OffsetHz = _tuningOffset,
            EstimatedDbm = -120 + DbmCalibrationOffset,
            SelectedProgram = SelectedProgram,
            BufferTargetSeconds = (float)EffectiveBufferSeconds()
        });
        if (Enabled) _retuneTimer.Change(350, Timeout.Infinite);
    }

    /// <summary>
    /// Tears the native session down and builds a new one. Used after a retune and by the
    /// panel button, because libnrsc5 has no way to be told the signal changed underneath it.
    /// </summary>
    public void Restart()
    {
        if (_disposed || !Enabled) return;
        Stop("Restarting...");
        if (Enabled) Start();
    }

    /// <summary>
    /// SDR#'s IQ callback. Up to Dev 3.3.5 this mixed, resampled and ran libnrsc5 right
    /// here, and libnrsc5 in pipe mode decodes inside the call: measured at 9 ms of every
    /// 36 ms block while it searches for sync, all of it taken from SDR#'s own DSP thread.
    /// Now the block is copied into a pooled queue and the call returns; the decoder
    /// thread does the rest on another core. Nothing here waits, and nothing allocates
    /// once the pool has warmed up.
    /// </summary>
    public unsafe void ProcessIq(Complex* buffer, int length)
    {
        if (!Enabled || length <= 1) return;

        var inputRate = InputSampleRate;
        if (inputRate < Nrsc5Native.NativeFmSampleRate)
        {
            UpdateStatus(s => s with { Synced = false, Message = $"IQ sample rate too low: {inputRate / 1000:0} kS/s; minimum 744.2 kS/s" });
            return;
        }
        if (Volatile.Read(ref _session) == IntPtr.Zero) return;

        var started = Stopwatch.GetTimestamp();
        if (MetadataTrace.Enabled && Interlocked.Exchange(ref _iqThreadTraced, 1) == 0)
            MetadataTrace.Write($"THRD SDR# calls ProcessIq on OS thread {MetadataTrace.CurrentOsThreadId()}");
        // About a second of IQ may wait. Beyond that the decoder has fallen behind for
        // good, and dropping blocks is kinder than a latency that only ever grows.
        _iqQueue.MaxQueuedFloats = (int)Math.Min(int.MaxValue / 2, inputRate * 2);
        var iq = MemoryMarshal.Cast<Complex, float>(new ReadOnlySpan<Complex>(buffer, length));
        _iqQueue.TryEnqueue(iq, inputRate, Volatile.Read(ref _tuningOffset), Volatile.Read(ref _iqGeneration));
        Interlocked.Add(ref _iqThreadTicks, Stopwatch.GetTimestamp() - started);
    }

    private void EnsureDecoderThread()
    {
        if (_decoderThread is not null) return;
        _decoderThread = new Thread(DecoderLoop)
        {
            IsBackground = true,
            Name = "NRSC-5 decoder",
            // Above normal, like SDR#'s own DSP: falling behind here is heard as a dropout.
            Priority = ThreadPriority.AboveNormal
        };
        _decoderThread.Start();
    }

    /// <summary>
    /// The decoder thread's whole life: take a block, decode it, give the buffer back.
    /// Every event libnrsc5 raises now arrives on this thread instead of SDR#'s.
    /// </summary>
    private void DecoderLoop()
    {
        while (!_decoderStopping)
        {
            IqBlockQueue.Block block;
            try
            {
                if (!_iqQueue.TryDequeue(250, out block)) continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                DecodeBlock(block);
            }
            catch
            {
                // One bad block must not take the decoder thread down with it.
            }
            finally
            {
                _iqQueue.Return(block.Buffer);
            }
        }
    }

    /// <summary>
    /// Mixes the block down to baseband, resamples it to the 744187.5 S/s libnrsc5
    /// expects and decodes it. Mixing has to come first: the anti-alias filter is centred
    /// on DC, so resampling before the shift would filter away the very carrier tuned.
    /// </summary>
    private unsafe void DecodeBlock(IqBlockQueue.Block block)
    {
        var started = Stopwatch.GetTimestamp();
        var complexCount = block.Floats / 2;
        int produced;
        lock (_iqGate)
        {
            // Captured before a retune. The mixer and resampler have already been reset
            // for the new frequency, and this IQ would only smear the old one into it.
            if (block.Generation != _iqGeneration) return;
            _resampler.Configure(block.Rate, Nrsc5Native.NativeFmSampleRate);
            EnsureCapacity(ref _mixed, block.Floats);
            _mixer.Process(new ReadOnlySpan<float>(block.Buffer, 0, block.Floats), _mixed, block.Offset, block.Rate);
            produced = _resampler.Process(_mixed, complexCount, ref _iqOutput);
        }

        if (produced > 0)
        {
            UpdateSignalMonitor(_iqOutput, produced);
            lock (_sessionGate)
            {
                if (_session != IntPtr.Zero)
                {
                    fixed (float* samples = _iqOutput)
                        Nrsc5Native.nrsc5_pipe_samples_cf32(_session, samples, (uint)(produced * 2));
                }
            }
        }

        AccountLoad(Stopwatch.GetTimestamp() - started, complexCount / block.Rate);
    }

    /// <summary>
    /// Publishes, once a second, how much of a core each side costs per second of signal:
    /// the decoder thread, and what is left on SDR#'s IQ thread. Above 100% the decoder
    /// cannot keep up in real time on this machine; the dropped-block count says so too.
    /// </summary>
    private void AccountLoad(long decodeTicks, double signalSeconds)
    {
        _decodeTicks += decodeTicks;
        _accountedSignalSeconds += signalSeconds;
        var now = Stopwatch.GetTimestamp();
        if (now - _loadWindowStart < Stopwatch.Frequency || _accountedSignalSeconds <= 0) return;
        _loadWindowStart = now;

        var signal = _accountedSignalSeconds;
        var decoder = (float)(_decodeTicks / (double)Stopwatch.Frequency / signal);
        var iqThread = (float)(Interlocked.Exchange(ref _iqThreadTicks, 0) / (double)Stopwatch.Frequency / signal);
        _decodeTicks = 0;
        _accountedSignalSeconds = 0;
        // The first window holds JIT compilation and the queue's first allocations against a
        // single block of signal: measured live at 200%, which says nothing about steady state.
        if (++_loadWindows == 1) return;
        var dropped = _iqQueue.Dropped;
        UpdateStatus(s => s with { DecoderLoad = decoder, IqThreadLoad = iqThread, DroppedBlocks = dropped });
        if (MetadataTrace.Enabled && _loadWindows % 10 == 2)
            MetadataTrace.Write($"LOAD decoder thread {MetadataTrace.CurrentOsThreadId()}: {decoder * 100:0.0}% of a core; " +
                                $"SDR# IQ thread: {iqThread * 100:0.000}%; dropped blocks {dropped}");
    }


    /// <summary>
    /// Where HD audio replaces the analog programme, by overwriting SDR#'s buffer in place.
    ///
    /// The swap is never abrupt: the level ramps across the change, and the decision to use
    /// HD at all depends on the prebuffer having enough held to survive a fade. When it runs
    /// dry the analog programme comes back the same way.
    /// </summary>
    public unsafe void ProcessAudio(float* buffer, int length)
    {
        // First, and outside every lock: release any metadata whose audio is now playing.
        PresentDueTracks();
        if (!Enabled || !ReplaceAnalogAudio || !Status.Synced || length < 2) return;

        var outputRate = OutputSampleRate;
        if (outputRate <= 0) return;
        var frames = length / 2;
        var required = (int)Math.Ceiling(frames * Nrsc5Native.AudioSampleRate / outputRate) + 3;

        lock (_audioGate)
        {
            // _hdAudioActive is read and written from both the SDR# audio thread and the
            // UI thread, so the whole arm/disarm decision stays inside the gate.
            var available = _audio.AvailableFrames;
            if (!_hdAudioActive)
            {
                var startup = _bufferingEnabled
                    ? (int)(Nrsc5Native.AudioSampleRate * _bufferSeconds)
                    : 0;
                if (available < Math.Max(required, startup))
                {
                    HoldOverAnalog(buffer, frames, outputRate);
                    return;
                }
                _hdAudioActive = true;
                _switchingProgram = false;
            }
            else if (available < required)
            {
                // Keep the HD path armed. A short producer jitter gap should use
                // this untouched analog block, not force a full startup rebuffer.
                return;
            }

            if (!_haveAudioPair)
            {
                if (!_audio.TryReadFrame(out _audioLeftA, out _audioRightA) ||
                    !_audio.TryReadFrame(out _audioLeftB, out _audioRightB))
                {
                    _hdAudioActive = false;
                    return;
                }
                _haveAudioPair = true;
                _audioPhase = 0;
            }

            var advance = Nrsc5Native.AudioSampleRate / outputRate;
            var gainStep = (float)(1.0 / (FadeSeconds * outputRate));
            _surround.Configure(outputRate);
            for (var frame = 0; frame < frames; frame++)
            {
                while (_audioPhase >= 1.0)
                {
                    _audioLeftA = _audioLeftB;
                    _audioRightA = _audioRightB;
                    if (!_audio.TryReadFrame(out _audioLeftB, out _audioRightB))
                    {
                        _haveAudioPair = false;
                        _hdAudioActive = false;
                        return;
                    }
                    _audioPhase -= 1.0;
                }

                var t = (float)_audioPhase;
                var left = _audioLeftA + (_audioLeftB - _audioLeftA) * t;
                var right = _audioRightA + (_audioRightB - _audioRightA) * t;
                _surround.Process(ref left, ref right);
                if (_outputGain < 1f)
                {
                    // Ramps the new subchannel in, so the switch is not a step edge.
                    _outputGain = Math.Min(1f, _outputGain + gainStep);
                    left *= _outputGain;
                    right *= _outputGain;
                }

                buffer[frame * 2] = left;
                buffer[frame * 2 + 1] = right;
                _audioPhase += advance;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Volatile.Write(ref _enabled, false);
        Stop("Closed");
        _retuneTimer.Dispose();
        _syncLossTimer.Dispose();
        lock (_factsGate)
        {
            _lookupCancellation?.Cancel();
            _lookupCancellation?.Dispose();
            _lookupCancellation = null;
        }
        _fccDirectory.Dispose();
        _geocoder.Dispose();

        // The session is closed, so the decoder has nothing left to feed. Wake it so it
        // notices it is being stopped instead of waiting out its poll.
        _decoderStopping = true;
        _iqQueue.Clear();
        _iqQueue.Wake();
        _decoderThread?.Join(2000);
        _iqQueue.Dispose();
    }

    /// <summary>
    /// Opens a libnrsc5 pipe session and points it at the callback. The decoder is fed
    /// samples rather than opening a radio itself, which is what lets it share the receiver
    /// SDR# already owns.
    /// </summary>
    private void Start()
    {
        lock (_sessionGate)
        {
            if (_session != IntPtr.Zero) return;
            try
            {
                if (!Environment.Is64BitProcess)
                    throw new PlatformNotSupportedException("This package requires SDR# x64; the current process is x86.");

                if (Nrsc5Native.nrsc5_open_pipe(out var state) != 0 || state == IntPtr.Zero)
                    throw new InvalidOperationException("nrsc5_open_pipe failed.");

                Nrsc5Native.nrsc5_set_callback(state, _callback, IntPtr.Zero);
                if (Nrsc5Native.nrsc5_set_mode(state, 0) != 0)
                {
                    Nrsc5Native.nrsc5_close(state);
                    throw new InvalidOperationException("Could not select NRSC-5 FM mode.");
                }

                Nrsc5Native.nrsc5_start(state);
                _session = state;
                EnsureDecoderThread();
                ResetIq();
                ResetAudio();
                ResetMetadata();
                UpdateStatus(_ => Nrsc5Status.Idle with
                {
                    Message = "Searching for NRSC-5 signal...",
                    InputRate = InputSampleRate,
                    OffsetHz = _tuningOffset,
                    EstimatedDbm = -120 + DbmCalibrationOffset,
                    SelectedProgram = SelectedProgram,
                    BufferTargetSeconds = (float)EffectiveBufferSeconds()
                });
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _enabled, false);
                UpdateStatus(_ => Nrsc5Status.Idle with { Message = "Error: " + ex.Message });
            }
        }
    }

    /// <summary>
    /// Closes the native session and clears everything derived from the old signal, so a
    /// restart cannot show the previous station's metadata next to the new one's audio.
    /// </summary>
    private void Stop(string message)
    {
        CancelPendingSyncLoss();
        lock (_sessionGate)
        {
            if (_session != IntPtr.Zero)
            {
                var state = _session;
                _session = IntPtr.Zero;
                Nrsc5Native.nrsc5_stop(state);
                Nrsc5Native.nrsc5_close(state);
            }
        }
        ResetIq();
        ResetAudio();
        ResetMetadata();
        UpdateStatus(_ => Nrsc5Status.Idle with
        {
            Message = message,
            EstimatedDbm = -120 + DbmCalibrationOffset,
            SelectedProgram = SelectedProgram,
            BufferTargetSeconds = (float)EffectiveBufferSeconds()
        });
    }

    /// <summary>
    /// Measures the signal for the panel meters. Deliberately sampled rather than exhaustive:
    /// a couple of hundred samples ten times a second is enough for a meter a human reads,
    /// and this runs on the audio thread where the full sum would not be free.
    /// </summary>
    private void UpdateSignalMonitor(float[] samples, int complexCount)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Volatile.Read(ref _lastSignalTicks);
        if (previous != 0 && (now - previous) / (double)Stopwatch.Frequency < 0.09) return;
        Volatile.Write(ref _lastSignalTicks, now);

        var sampleCount = Math.Min(SignalProbeSamples, complexCount);
        if (sampleCount <= 0) return;
        double power = 0;
        double peak = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var source = Math.Min(complexCount - 1, (int)((long)i * complexCount / sampleCount));
            var re = samples[source * 2];
            var im = samples[source * 2 + 1];
            var magnitude = re * re + im * im;
            power += magnitude;
            if (magnitude > peak) peak = magnitude;
        }

        power /= sampleCount;
        var currentDbfs = (float)(10 * Math.Log10(Math.Max(power, 1e-12)));
        _smoothedDbfs = _smoothedDbfs <= -119 ? currentDbfs : _smoothedDbfs * 0.78f + currentDbfs * 0.22f;
        var peakDbfs = (float)(10 * Math.Log10(Math.Max(peak, 1e-12)));
        var calibration = DbmCalibrationOffset;
        var buffered = (float)(_audio.AvailableFrames / Nrsc5Native.AudioSampleRate);
        UpdateStatus(s => s with
        {
            InputRate = InputSampleRate,
            SignalDbfs = _smoothedDbfs,
            PeakDbfs = peakDbfs,
            EstimatedDbm = _smoothedDbfs + calibration,
            BufferedSeconds = buffered
        });
    }

    /// <summary>
    /// Every event libnrsc5 raises arrives here, on a native thread. The union is read at the
    /// offsets <see cref="Nrsc5Layout"/> derives, and the whole body is wrapped so no managed
    /// exception can ever cross back into C: unwinding through the native frames would take
    /// down SDR#, not just the plugin.
    /// </summary>
    private void OnNativeEvent(IntPtr evt, IntPtr opaque)
    {
        try
        {
            var type = (Nrsc5Event)Marshal.ReadInt32(evt);
            var union = IntPtr.Add(evt, Nrsc5Layout.Union);
            switch (type)
            {
                case Nrsc5Event.Sync:
                    Volatile.Write(ref _lastDigitalTicks, Stopwatch.GetTimestamp());
                    _syncLossTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    UpdateStatus(s => s with { Synced = true, Message = $"Synchronized HD{SelectedProgram + 1}" });
                    break;
                case Nrsc5Event.LostSync:
                    _syncLossTimer.Change(SyncLossGraceMs(), Timeout.Infinite);
                    UpdateStatus(s => s with { Message = "HD signal unstable; holding buffered audio..." });
                    break;
                case Nrsc5Event.Mer:
                    var lower = ReadFloat(union, Nrsc5Layout.MerLower);
                    var upper = ReadFloat(union, Nrsc5Layout.MerUpper);
                    UpdateStatus(s => s with { MerLower = lower, MerUpper = upper, SnrDb = (lower + upper) / 2 });
                    break;
                case Nrsc5Event.Ber:
                    UpdateStatus(s => s with { Ber = ReadFloat(union, Nrsc5Layout.BerCber) });
                    break;
                case Nrsc5Event.Hdc:
                    ReceiveHdc(union);
                    break;
                case Nrsc5Event.Audio:
                    ReceiveAudio(union);
                    break;
                case Nrsc5Event.Id3:
                    ReceiveId3(union);
                    break;
                case Nrsc5Event.Lot:
                    ReceiveLot(union);
                    break;
                case Nrsc5Event.Sig:
                    ReceiveSig(union);
                    break;
                case Nrsc5Event.AudioService:
                    MarkProgramAvailable(Marshal.ReadInt32(union, Nrsc5Layout.AudioServiceProgram));
                    break;
                case Nrsc5Event.StationName:
                    var station = ReadUtf8(Marshal.ReadIntPtr(union, Nrsc5Layout.StationNameName));
                    if (!string.IsNullOrWhiteSpace(station))
                    {
                        UpdateStatus(s => s with { Station = station });
                        UpdateFacts(f => f with { Callsign = station.Trim() });
                    }
                    break;
                case Nrsc5Event.StationSlogan:
                    var slogan = ReadUtf8(Marshal.ReadIntPtr(union, Nrsc5Layout.StationSloganSlogan));
                    if (!string.IsNullOrWhiteSpace(slogan)) UpdateFacts(f => f with { Slogan = slogan.Trim() });
                    break;
                case Nrsc5Event.StationMessage:
                    var stationMessage = ReadUtf8(Marshal.ReadIntPtr(union, Nrsc5Layout.StationMessageMessage));
                    if (!string.IsNullOrWhiteSpace(stationMessage))
                        UpdateFacts(f => f with { Message = stationMessage.Trim() });
                    break;
                case Nrsc5Event.StationId:
                    ReceiveStationId(union);
                    break;
                case Nrsc5Event.HereImage:
                    ReceiveHereImage(union);
                    break;
                case Nrsc5Event.EmergencyAlert:
                    ReceiveEmergencyAlert(union);
                    break;
                case Nrsc5Event.StationLocation:
                    var latitude = ReadFloat(union, Nrsc5Layout.StationLocationLatitude);
                    var longitude = ReadFloat(union, Nrsc5Layout.StationLocationLongitude);
                    var altitude = Marshal.ReadInt32(union, Nrsc5Layout.StationLocationAltitude);
                    // A station that has not set its site sends 0,0, and the panel showed
                    // that as "0,000N 0,000E" - a coordinate in the Atlantic dressed up as
                    // an answer. Only a plausible pair counts as having a location.
                    UpdateFacts(f => f with
                    {
                        Latitude = latitude,
                        Longitude = longitude,
                        Altitude = altitude,
                        HasLocation = ReverseGeocoder.IsPlausible(latitude, longitude)
                    });
                    BeginSiteLookup(latitude, longitude);
                    break;
            }
        }
        catch
        {
            // Never let a managed exception cross back into the native callback.
        }
    }

    /// <summary>
    /// Raw HDC codec frames, used only to measure the real bitrate of the selected subchannel
    /// and to notice that a subchannel exists. The audio itself arrives already decoded.
    /// </summary>
    private void ReceiveHdc(IntPtr union)
    {
        var program = Marshal.ReadInt32(union, Nrsc5Layout.HdcProgram);
        MarkProgramAvailable(program);
        if (program != SelectedProgram) return;
        var count = ReadNativeSize(union, Nrsc5Layout.HdcCount);
        if (count <= 0 || count > 1_048_576) return;

        lock (_bitrateGate)
        {
            _bitrateBytes += count;
            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - _bitrateStartedTicks) / (double)Stopwatch.Frequency;
            if (elapsed < 1.0) return;
            var bitrate = (float)(_bitrateBytes * 8.0 / elapsed / 1000.0);
            _bitrateBytes = 0;
            _bitrateStartedTicks = now;
            UpdateStatus(s => s with { BitrateKbps = bitrate });
        }
    }

    /// <summary>
    /// Decoded PCM for one subchannel. Only the selected one is kept; the others are dropped
    /// here rather than buffered, because a station can carry three or four at once.
    /// </summary>
    private unsafe void ReceiveAudio(IntPtr union)
    {
        var program = Marshal.ReadInt32(union, Nrsc5Layout.AudioProgram);
        MarkProgramAvailable(program);
        if (program != SelectedProgram) return;
        var data = Marshal.ReadIntPtr(union, Nrsc5Layout.AudioData);
        var count = ReadNativeSize(union, Nrsc5Layout.AudioCount);
        if (data == IntPtr.Zero || count <= 0 || count > 65536) return;
        // The native buffer is valid during this callback; Write copies/converts it
        // synchronously, so no temporary managed array or Marshal.Copy is needed.
        var pcm = new ReadOnlySpan<short>((void*)data, (int)count);
        Volatile.Write(ref _lastDigitalTicks, Stopwatch.GetTimestamp());
        _audio.Write(pcm);
    }

    /// <summary>
    /// Song metadata and the XHDR that says which image goes with it. The frame is not
    /// shown on arrival: it is stamped with the prebuffer position of the audio decoded
    /// alongside it and released when that audio is heard. The XHDR parameter is honoured
    /// too: 1 means this track has no image, and a repeat of the same track without an
    /// XHDR keeps the reference it already had.
    /// </summary>
    private void ReceiveId3(IntPtr union)
    {
        var program = Marshal.ReadInt32(union, Nrsc5Layout.Id3Program);
        if (program is < 0 or > 7) return;
        MarkProgramAvailable(program);

        var title = ReadUtf8(Marshal.ReadIntPtr(union, Nrsc5Layout.Id3Title));
        var artist = ReadUtf8(Marshal.ReadIntPtr(union, Nrsc5Layout.Id3Artist));
        var album = ReadUtf8(Marshal.ReadIntPtr(union, Nrsc5Layout.Id3Album));
        var mime = unchecked((uint)Marshal.ReadInt32(union, Nrsc5Layout.Id3XhdrMime));
        var param = Marshal.ReadInt32(union, Nrsc5Layout.Id3XhdrParam);
        var lot = Marshal.ReadInt32(union, Nrsc5Layout.Id3XhdrLot);
        var xhdr = XhdrReference.FromNative(mime, param, lot);

        long lotStamp;
        lock (_artworkGate)
        {
            if (xhdr.Directive == ArtworkDirective.Show) _programLinksImages[program] = true;
            lotStamp = _lotSequence;
        }

        // The audio decoded with this frame sits at the prebuffer's current write position.
        var position = _audio.TotalWrittenFrames;
        TrackInfo track;
        TrackInfo? previous;
        bool presentNow;
        lock (_trackGate)
        {
            previous = _receivedTracks[program];
            track = TrackInfo.Merge(previous, new TrackInfo(program, title, artist, album, xhdr, lotStamp));
            _receivedTracks[program] = track;
            // Only the selected program's audio goes through the prebuffer. Any other
            // program is not being heard, so there is nothing to wait for.
            presentNow = program != SelectedProgram || !ReplaceAnalogAudio;
            if (!presentNow) _pendingTracks.Enqueue(position, Stopwatch.GetTimestamp(), track);
        }

        // Stations repeat the same frame every second or two; only a change is worth a line.
        if (MetadataTrace.Enabled && (!track.IsSameTrackAs(previous) || track.Xhdr != previous!.Xhdr))
            MetadataTrace.Write($"ID3  HD{program + 1} \"{title}\" / \"{artist}\" xhdr={xhdr.Directive}:{xhdr.Lot} (param {param}) " +
                                $"at frame {position}, {(presentNow ? "shown now" : "queued")}");
        if (presentNow) PresentTrack(track);
    }

    /// <summary>
    /// Releases the newest queued track whose audio playback has reached. Called from the
    /// audio callback, so it is cheap when nothing is waiting and takes no lock across the
    /// presentation itself.
    /// </summary>
    private void PresentDueTracks()
    {
        lock (_trackGate)
        {
            if (_pendingTracks.Count == 0) return;
        }

        // With HD audio not replacing the analog programme, nothing consumes the
        // prebuffer, so everything waiting is due now.
        var consumed = ReplaceAnalogAudio ? _audio.TotalConsumedFrames : long.MaxValue;
        TrackInfo? due = null;
        lock (_trackGate)
        {
            if (_pendingTracks.TryTakeDue(consumed, Stopwatch.GetTimestamp(), MaxPresentationDelayTicks, out var track))
                due = track;
        }
        if (due is not null) PresentTrack(due);
    }

    /// <summary>Makes a track the one being heard on its program, and shows it if that program is selected.</summary>
    private void PresentTrack(TrackInfo track)
    {
        TrackInfo? before;
        lock (_trackGate)
        {
            before = _presentedTracks[track.Program];
            _presentedTracks[track.Program] = track;
        }
        if (MetadataTrace.Enabled && (!track.IsSameTrackAs(before) || track.Xhdr != before!.Xhdr))
            MetadataTrace.Write($"SHOW HD{track.Program + 1} \"{track.Title}\" / \"{track.Artist}\" xhdr={track.Xhdr.Directive}:{track.Xhdr.Lot} " +
                                $"(heard at frame {_audio.TotalConsumedFrames})");
        if (track.Program != SelectedProgram) return;
        UpdateStatus(s => s with { Title = track.Title, Artist = track.Artist, Album = track.Album });
        RefreshArtwork();
    }

    /// <summary>
    /// A LOT object: album art or a station logo. Every image is cached with its port, its
    /// program when libnrsc5 knows it, and an arrival number. Which one is shown is decided
    /// later, from the track being heard, never simply from which image came in last.
    /// </summary>
    private void ReceiveLot(IntPtr union)
    {
        var port = Marshal.ReadInt16(union, Nrsc5Layout.LotPort) & 0xFFFF;
        var lot = Marshal.ReadInt32(union, Nrsc5Layout.LotId);
        var size = Marshal.ReadInt32(union, Nrsc5Layout.LotSize);
        var mime = unchecked((uint)Marshal.ReadInt32(union, Nrsc5Layout.LotMime));
        var data = Marshal.ReadIntPtr(union, Nrsc5Layout.LotData);
        if (lot < 0 || size <= 0 || size > MaxArtworkBytes || data == IntPtr.Zero) return;

        if (!Nrsc5Mime.IsImage(mime))
        {
            var signature = new byte[Math.Min(size, 8)];
            Marshal.Copy(data, signature, 0, signature.Length);
            if (!LooksLikeImage(signature)) return;
        }

        var bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, size);
        var program = ProgramFromService(Marshal.ReadIntPtr(union, Nrsc5Layout.LotService));
        var component = Marshal.ReadIntPtr(union, Nrsc5Layout.LotComponent);
        var componentMime = component != IntPtr.Zero
            ? unchecked((uint)Marshal.ReadInt32(component, Nrsc5Layout.SigComponentDataMime))
            : 0u;

        // Images the tracks still point at must survive eviction, or a long listening
        // session could throw away the cover of the very song that is playing.
        var protectedLots = new HashSet<int>();
        lock (_trackGate)
        {
            foreach (var track in _receivedTracks) if (track?.Xhdr.Directive == ArtworkDirective.Show) protectedLots.Add(track.Xhdr.Lot);
            foreach (var track in _presentedTracks) if (track?.Xhdr.Directive == ArtworkDirective.Show) protectedLots.Add(track.Xhdr.Lot);
        }

        bool isLogo;
        lock (_artworkGate)
        {
            if (program < 0 && _portProgram.TryGetValue(port, out var mapped)) program = mapped;
            var image = new CachedImage(bytes, mime, componentMime, program, ++_lotSequence);
            _lotImages[(port, lot)] = image;
            isLogo = IsLogoLocked((port, lot), image);
            TrimLotCacheLocked(protectedLots);
        }

        if (MetadataTrace.Enabled)
            MetadataTrace.Write($"LOT  port {port} lot {lot} {(isLogo ? "logo" : "image")} mime {mime:X8} component {componentMime:X8} " +
                                $"\"{ReadUtf8(Marshal.ReadIntPtr(union, Nrsc5Layout.LotName))}\" {size} bytes, " +
                                $"program {(program >= 0 ? $"HD{program + 1}" : "unknown")}");
        RefreshArtwork();
    }

    /// <summary>Caller holds <see cref="_artworkGate"/>. Evicts oldest first, sparing referenced images and logos.</summary>
    private void TrimLotCacheLocked(HashSet<int> protectedLots)
    {
        while (_lotImages.Count > MaxCachedImages)
        {
            (int Port, int Lot)? victim = null;
            var oldest = long.MaxValue;
            foreach (var (key, image) in _lotImages)
            {
                if (protectedLots.Contains(key.Lot) || IsLogoLocked(key, image)) continue;
                if (image.Sequence < oldest) { oldest = image.Sequence; victim = key; }
            }
            if (victim is null)
            {
                // Everything is protected: fall back to plain oldest-first.
                foreach (var (key, image) in _lotImages)
                    if (image.Sequence < oldest) { oldest = image.Sequence; victim = key; }
            }
            _lotImages.Remove(victim!.Value);
        }
    }

    /// <summary>
    /// Caller holds <see cref="_artworkGate"/>. Whether an image is a station logo: by its own
    /// MIME type, by the SIG component it arrived on, or by what the SIG says its port carries.
    /// Decided at lookup rather than on arrival, so a logo that beat the SIG table in is
    /// recognised as soon as the table turns up.
    /// </summary>
    private bool IsLogoLocked((int Port, int Lot) key, CachedImage image) =>
        image.Mime == Nrsc5Mime.StationLogo ||
        image.ComponentMime == Nrsc5Mime.StationLogo ||
        _portMime.GetValueOrDefault(key.Port) == Nrsc5Mime.StationLogo;

    private static string DescribeMime(uint mime) => mime switch
    {
        Nrsc5Mime.StationLogo => "station logo",
        Nrsc5Mime.PrimaryImage => "primary image",
        _ => $"MIME {mime:X8}"
    };

    /// <summary>Caller holds <see cref="_artworkGate"/>. The program an image belongs to, from its service or its port.</summary>
    private int OwnerLocked((int Port, int Lot) key, CachedImage image) =>
        image.Program >= 0 ? image.Program : _portProgram.GetValueOrDefault(key.Port, -1);

    /// <summary>
    /// Caller holds <see cref="_artworkGate"/>. The image an XHDR names. LOT ids repeat
    /// across ports, so an image owned by another program never qualifies; one whose owner
    /// is still unknown does, if nothing better exists.
    /// </summary>
    private byte[]? FindReferencedLocked(int program, int lot)
    {
        byte[]? unowned = null;
        var unownedSequence = -1L;
        foreach (var (key, image) in _lotImages)
        {
            if (key.Lot != lot) continue;
            var owner = OwnerLocked(key, image);
            if (owner == program) return image.Bytes;
            if (owner < 0 && image.Sequence > unownedSequence) { unowned = image.Bytes; unownedSequence = image.Sequence; }
        }
        return unowned;
    }

    /// <summary>
    /// Caller holds <see cref="_artworkGate"/>. The newest logo for this program, else the
    /// newest one whose owner is unknown. Never another program's: on a multicast station
    /// HD2 is often a different brand, and borrowing HD1's logo would label it wrongly.
    /// </summary>
    private byte[]? FindLogoLocked(int program)
    {
        byte[]? own = null, unowned = null;
        long ownSequence = -1, unownedSequence = -1;
        foreach (var (key, image) in _lotImages)
        {
            if (!IsLogoLocked(key, image)) continue;
            var owner = OwnerLocked(key, image);
            if (owner == program && image.Sequence > ownSequence) { own = image.Bytes; ownSequence = image.Sequence; }
            else if (owner < 0 && image.Sequence > unownedSequence) { unowned = image.Bytes; unownedSequence = image.Sequence; }
        }
        return own ?? unowned;
    }

    /// <summary>Caller holds <see cref="_artworkGate"/>. The newest non-logo image owned by this program.</summary>
    private (byte[]? Bytes, long Sequence) FindLatestArtLocked(int program)
    {
        byte[]? bytes = null;
        var sequence = -1L;
        foreach (var (key, image) in _lotImages)
        {
            if (IsLogoLocked(key, image) || OwnerLocked(key, image) != program) continue;
            if (image.Sequence > sequence) { bytes = image.Bytes; sequence = image.Sequence; }
        }
        return (bytes, sequence);
    }

    /// <summary>
    /// Walks the SIG linked list to learn which subchannels exist and which data ports
    /// belong to each. Pointers are only valid for the duration of the callback, so
    /// everything needed is copied here.
    /// </summary>
    private void ReceiveSig(IntPtr union)
    {
        var ports = new List<(int Port, int Program, uint Mime)>();
        var service = Marshal.ReadIntPtr(union, Nrsc5Layout.SigServices);
        var guard = 0;
        while (service != IntPtr.Zero && guard++ < 64)
        {
            var type = Marshal.ReadByte(service, Nrsc5Layout.SigServiceType);
            var number = Marshal.ReadInt16(service, Nrsc5Layout.SigServiceNumber) & 0xFFFF;
            var audioComponent = Marshal.ReadIntPtr(service, Nrsc5Layout.SigServiceAudioComponent);
            if (type == Nrsc5SigServiceType.Audio)
            {
                var program = number - 1;
                if (audioComponent != IntPtr.Zero) MarkProgramAvailable(program);

                // The data components of an audio service carry that program's images.
                var component = Marshal.ReadIntPtr(service, Nrsc5Layout.SigServiceComponents);
                var inner = 0;
                while (component != IntPtr.Zero && inner++ < 32 && program is >= 0 and <= 7)
                {
                    if (Marshal.ReadByte(component, Nrsc5Layout.SigComponentType) == Nrsc5SigComponentType.Data)
                        ports.Add((Marshal.ReadInt16(component, Nrsc5Layout.SigComponentDataPort) & 0xFFFF, program,
                            unchecked((uint)Marshal.ReadInt32(component, Nrsc5Layout.SigComponentDataMime))));
                    component = Marshal.ReadIntPtr(component, Nrsc5Layout.SigComponentNext);
                }
            }
            service = Marshal.ReadIntPtr(service, Nrsc5Layout.SigServiceNext);
        }

        if (ports.Count == 0) return;
        var changed = false;
        lock (_artworkGate)
        {
            foreach (var (port, program, mime) in ports)
            {
                if (_portProgram.TryGetValue(port, out var known) && known == program &&
                    _portMime.GetValueOrDefault(port) == mime) continue;
                _portProgram[port] = program;
                _portMime[port] = mime;
                changed = true;
            }
        }
        if (changed && MetadataTrace.Enabled)
            foreach (var (port, program, mime) in ports)
                MetadataTrace.Write($"SIG  port {port} -> HD{program + 1} carries {DescribeMime(mime)}");
        // Images that arrived before the SIG may just have found their owner.
        if (changed) RefreshArtwork();
    }

    /// <summary>Maps a SIG service back to a 0-based program index, or -1 when unknown.</summary>
    private static int ProgramFromService(IntPtr service)
    {
        if (service == IntPtr.Zero) return -1;
        try
        {
            var type = Marshal.ReadByte(service, Nrsc5Layout.SigServiceType);
            if (type != Nrsc5SigServiceType.Audio) return -1;
            var number = Marshal.ReadInt16(service, Nrsc5Layout.SigServiceNumber) & 0xFFFF;
            var program = number - 1;
            return program is >= 0 and <= 7 ? program : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Records that a subchannel really is on the air, which is what Previous and Next step
    /// through. Discovered from traffic rather than assumed, so the selector never offers an
    /// HD3 that does not exist.
    /// </summary>
    private void MarkProgramAvailable(int program)
    {
        if (program is < 0 or > 7) return;
        var bit = 1 << program;
        int current, updated;
        do
        {
            current = Volatile.Read(ref _programMask);
            if ((current & bit) != 0) return;
            updated = current | bit;
        }
        while (Interlocked.CompareExchange(ref _programMask, updated, current) != current);

        UpdateStatus(s => s with { ProgramMask = updated });
    }

    /// <summary>
    /// Decides the artwork for the track being heard on the selected program. The rules
    /// live in <see cref="ArtworkResolver"/>; this only gathers what they need. The one that
    /// matters: an image from another track is never shown in place of a missing one.
    /// </summary>
    private void RefreshArtwork()
    {
        var program = SelectedProgram;
        TrackInfo? track;
        lock (_trackGate) track = _presentedTracks[program];

        ArtworkChoice choice;
        lock (_artworkGate)
        {
            var xhdr = track?.Xhdr ?? XhdrReference.Absent;
            var referenced = xhdr.Directive == ArtworkDirective.Show ? FindReferencedLocked(program, xhdr.Lot) : null;
            var latest = FindLatestArtLocked(program);
            var sinceTrackStart = track is not null && latest.Sequence > track.LotStamp ? latest.Bytes : null;
            choice = ArtworkResolver.Resolve(xhdr, _programLinksImages[program], referenced, sinceTrackStart, FindLogoLocked(program));
        }

        if (MetadataTrace.Enabled && !ReferenceEquals(choice.Image, Interlocked.Exchange(ref _tracedArtwork, choice.Image)))
            MetadataTrace.Write($"ART  HD{program + 1} -> {(choice.Image is null ? "placeholder" : choice.IsStationLogo ? "station logo" : $"image {choice.Image.Length} bytes")} " +
                                $"for \"{track?.Title}\"");
        UpdateStatus(s => s with { Artwork = choice.Image, ArtworkIsStationLogo = choice.IsStationLogo });
    }

    private static bool LooksLikeImage(byte[] data) =>
        data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF ||
        data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

    /// <summary>
    /// A HERE map tile. Traffic arrives as nine of them over a minute or two, weather as
    /// one whole image. The sequence number changes when the station publishes a new map,
    /// which is what discards the half-built previous one.
    ///
    /// Only stations carrying the HERE data service send these at all, which in practice
    /// means the larger US stations; most stations send nothing here and the map window
    /// stays empty, which is not a fault.
    /// </summary>
    private void ReceiveHereImage(IntPtr union)
    {
        var imageType = Marshal.ReadInt32(union, Nrsc5Layout.HereImageType);
        if (imageType is not (Nrsc5HereImage.Traffic or Nrsc5HereImage.Weather)) return;

        var size = Marshal.ReadInt32(union, Nrsc5Layout.HereImageSize);
        var data = Marshal.ReadIntPtr(union, Nrsc5Layout.HereImageData);
        if (size <= 0 || size > MaxArtworkBytes || data == IntPtr.Zero) return;

        var bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, size);

        var tile = new HereTile(
            Marshal.ReadInt32(union, Nrsc5Layout.HereImageN1),
            bytes,
            ReadFloat(union, Nrsc5Layout.HereImageLatitude1),
            ReadFloat(union, Nrsc5Layout.HereImageLongitude1),
            ReadFloat(union, Nrsc5Layout.HereImageLatitude2),
            ReadFloat(union, Nrsc5Layout.HereImageLongitude2));

        var sequence = Marshal.ReadInt32(union, Nrsc5Layout.HereImageSeq);
        var timeUtc = ReadTm(Marshal.ReadIntPtr(union, Nrsc5Layout.HereImageTime));
        var expected = Marshal.ReadInt32(union, Nrsc5Layout.HereImageN2);

        HereData next;
        lock (_hereGate)
        {
            if (imageType == Nrsc5HereImage.Weather)
            {
                // Weather is whole in one frame; n1 and n2 count publications, not parts.
                _here = _here with
                {
                    Weather = new HereImageSet(false, sequence, timeUtc, [tile], 1)
                };
            }
            else
            {
                if (sequence != _trafficSequence)
                {
                    _trafficSequence = sequence;
                    _trafficTiles.Clear();
                }
                _trafficTiles.RemoveAll(existing => existing.Part == tile.Part);
                _trafficTiles.Add(tile);
                _here = _here with
                {
                    Traffic = new HereImageSet(
                        true, sequence, timeUtc,
                        _trafficTiles.OrderBy(t => t.Part).ToList(),
                        expected is > 0 and <= Nrsc5HereImage.TrafficTiles ? expected : Nrsc5HereImage.TrafficTiles)
                };
            }
            next = _here;
        }
        HereDataChanged?.Invoke(next);
    }

    /// <summary>
    /// An emergency alert. The same alert repeats for as long as it is active, so it is
    /// matched on its text rather than appended blindly; the newest sits first.
    /// </summary>
    private void ReceiveEmergencyAlert(IntPtr union)
    {
        var message = ReadUtf8(Marshal.ReadIntPtr(union, Nrsc5Layout.AlertMessage)).Trim();
        if (message.Length == 0) return;

        var count = Marshal.ReadInt32(union, Nrsc5Layout.AlertNumLocations);
        var pointer = Marshal.ReadIntPtr(union, Nrsc5Layout.AlertLocations);
        var locations = new List<int>();
        if (pointer != IntPtr.Zero && count is > 0 and <= 1024)
        {
            var raw = new int[count];
            Marshal.Copy(pointer, raw, 0, count);
            locations.AddRange(raw);
        }

        var alert = new HdAlert(
            message,
            Marshal.ReadInt32(union, Nrsc5Layout.AlertCategory1),
            Marshal.ReadInt32(union, Nrsc5Layout.AlertCategory2),
            Marshal.ReadInt32(union, Nrsc5Layout.AlertLocationFormat),
            locations,
            DateTime.UtcNow);

        HereData next;
        lock (_hereGate)
        {
            var alerts = new List<HdAlert>(_here.Alerts.Count + 1) { alert };
            foreach (var existing in _here.Alerts)
                if (!string.Equals(existing.Message, message, StringComparison.Ordinal))
                    alerts.Add(existing);
            if (alerts.Count > MaxAlerts) alerts.RemoveRange(MaxAlerts, alerts.Count - MaxAlerts);
            _here = _here with { Alerts = alerts };
            next = _here;
        }
        HereDataChanged?.Invoke(next);
    }

    /// <summary>Reads the nine-int <c>struct tm</c> libnrsc5 hands over, as UTC.</summary>
    private static DateTime ReadTm(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return default;
        try
        {
            return new DateTime(
                Marshal.ReadInt32(pointer, Nrsc5Layout.TmYear) + 1900,
                Marshal.ReadInt32(pointer, Nrsc5Layout.TmMon) + 1,
                Marshal.ReadInt32(pointer, Nrsc5Layout.TmMday),
                Marshal.ReadInt32(pointer, Nrsc5Layout.TmHour),
                Marshal.ReadInt32(pointer, Nrsc5Layout.TmMin),
                Marshal.ReadInt32(pointer, Nrsc5Layout.TmSec),
                DateTimeKind.Utc);
        }
        catch
        {
            // A station that has not set its clock sends a tm that is not a real date.
            return default;
        }
    }

    private void ResetHereData()
    {
        HereData next;
        lock (_hereGate)
        {
            if (_here.IsEmpty && _trafficTiles.Count == 0) return;
            _trafficTiles.Clear();
            _trafficSequence = -1;
            _here = HereData.Empty;
            next = _here;
        }
        HereDataChanged?.Invoke(next);
    }

    /// <summary>
    /// The station ID frame carries the country and the FCC facility ID, which is the key
    /// the licence lookup needs. It repeats every few seconds, so the lookup guards
    /// against firing again for a facility it has already asked about.
    /// </summary>
    private void ReceiveStationId(IntPtr union)
    {
        var country = ReadUtf8(Marshal.ReadIntPtr(union, Nrsc5Layout.StationIdCountryCode)).Trim();
        var facilityId = Marshal.ReadInt32(union, Nrsc5Layout.StationIdFacilityId);
        UpdateFacts(f => f with { CountryCode = country, FacilityId = facilityId });
        BeginFccLookup(facilityId, EffectiveCountry());
    }

    /// <summary>
    /// Fills in what the station does not broadcast - community of licence, ERP, HAAT -
    /// from the FCC's public database. It runs off the decoder thread, and failing only
    /// costs those three fields: everything SIS carries is already on screen.
    /// </summary>
    private void BeginFccLookup(int facilityId, string countryCode)
    {
        if (facilityId <= 0) return;

        // The FCC only licenses US stations. A Canadian or Mexican HD signal carries a
        // facility ID that means nothing to this database, so it is not queried at all.
        if (countryCode.Length > 0 && !countryCode.Equals("US", StringComparison.OrdinalIgnoreCase))
        {
            UpdateFacts(f => f with { Lookup = StationLookupState.Unsupported });
            return;
        }

        CancellationToken token;
        lock (_factsGate)
        {
            if (_lookedUpFacilityId == facilityId) return;
            _lookedUpFacilityId = facilityId;
            // The token belongs to the station, not to this lookup: the licence query and
            // the site geocoder run side by side and only a retune should cancel either.
            _lookupCancellation ??= new CancellationTokenSource();
            token = _lookupCancellation.Token;
        }

        UpdateFacts(f => f with { Lookup = StationLookupState.Pending });

        _ = Task.Run(async () =>
        {
            try
            {
                ApplyFccRecord(facilityId, await _fccDirectory.LookupAsync(facilityId, token).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                // Retuned before the answer arrived; the new station starts its own lookup.
            }
            catch
            {
                ApplyLookupState(facilityId, StationLookupState.Failed);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Names the town the transmitter stands in. The coordinates arrive in SIS and repeat
    /// every few seconds, so the lookup is keyed on them and only runs when they move.
    /// </summary>
    private void BeginSiteLookup(float latitude, float longitude)
    {
        if (!ReverseGeocoder.IsPlausible(latitude, longitude)) return;

        var key = LookupCache<GeocodedSite>.CoordinateKey(latitude, longitude);
        var country = EffectiveCountry();
        CancellationToken token;
        lock (_factsGate)
        {
            if (_geocodedSite == key) return;
            _geocodedSite = key;
            _lookupCancellation ??= new CancellationTokenSource();
            token = _lookupCancellation.Token;
        }

        UpdateFacts(f => f with { SiteLookup = StationLookupState.Pending });

        _ = Task.Run(async () =>
        {
            try
            {
                var site = await _geocoder.LookupAsync(latitude, longitude, country, token).ConfigureAwait(false);
                UpdateFacts(f => LookupCache<GeocodedSite>.CoordinateKey(f.Latitude, f.Longitude) != key
                    ? f
                    : site is null
                        ? f with { SiteLookup = StationLookupState.NotFound }
                        : f with
                        {
                            SiteCity = site.City,
                            SiteState = site.State,
                            SiteCountry = site.CountryCode,
                            SiteSource = site.Source,
                            SiteLookup = StationLookupState.Resolved
                        });
            }
            catch (OperationCanceledException)
            {
                // Retuned before the answer arrived; the new station starts its own lookup.
            }
            catch
            {
                UpdateFacts(f => LookupCache<GeocodedSite>.CoordinateKey(f.Latitude, f.Longitude) == key
                    ? f with { SiteLookup = StationLookupState.Failed }
                    : f);
            }
        }, CancellationToken.None);
    }

    private void ApplyFccRecord(int facilityId, FccRecord? record)
    {
        if (record is null)
        {
            ApplyLookupState(facilityId, StationLookupState.NotFound);
            return;
        }

        UpdateFacts(f => f.FacilityId != facilityId
            ? f
            : f with
            {
                City = record.City,
                State = record.State,
                Licensee = record.Licensee,
                StationClass = record.StationClass,
                ErpKw = record.ErpKw,
                HaatMeters = record.HaatMeters,
                Lookup = StationLookupState.Resolved
            });
    }

    /// <summary>
    /// Which country's databases to ask. The call sign wins over the country code in the
    /// SIS frames whenever it says anything, because a station that has never touched its
    /// identity block still gets its own call sign right.
    /// </summary>
    private string EffectiveCountry()
    {
        var facts = Facts;
        return facts.CallsignCountry.Length > 0 ? facts.CallsignCountry : facts.CountryCode;
    }

    /// <summary>Ignored once the dial has moved on, so a late answer cannot land on a new station.</summary>
    private void ApplyLookupState(int facilityId, StationLookupState state) =>
        UpdateFacts(f => f.FacilityId == facilityId ? f with { Lookup = state } : f);

    private void ResetStationFacts()
    {
        lock (_factsGate)
        {
            _lookedUpFacilityId = 0;
            _geocodedSite = "";
            _lookupCancellation?.Cancel();
            _lookupCancellation?.Dispose();
            _lookupCancellation = null;
        }
        UpdateFacts(_ => StationFacts.Empty);
    }

    private void UpdateFacts(Func<StationFacts, StationFacts> update)
    {
        StationFacts next;
        lock (_factsGate)
        {
            next = update(_facts);
            if (next == _facts) return;
            _facts = next;
        }
        StationFactsChanged?.Invoke(next);
    }

    private void UpdateStatus(Func<Nrsc5Status, Nrsc5Status> update)
    {
        Nrsc5Status next;
        lock (_statusGate)
        {
            next = update(_status);
            if (next == _status) return;
            _status = next;
        }
        StatusChanged?.Invoke(next);
    }

    private double EffectiveBufferSeconds()
    {
        lock (_audioGate) return _bufferingEnabled ? _bufferSeconds : 0;
    }

    /// <summary>Grace before HD audio is abandoned: never shorter than the buffer itself.</summary>
    private int SyncLossGraceMs()
    {
        var bufferMs = (int)(EffectiveBufferSeconds() * 1000);
        return Math.Max(MinSyncLossGraceMs, bufferMs + 500);
    }

    private void ApplyBufferCapacity()
    {
        double seconds;
        lock (_audioGate) seconds = _bufferingEnabled ? _bufferSeconds : MinBufferSeconds;
        // Twice the target plus a second of slack keeps room for producer bursts.
        _audio.EnsureCapacityFrames((int)(Nrsc5Native.AudioSampleRate * (seconds * 2 + 1)));
    }

    private void PublishBufferState()
    {
        var target = (float)EffectiveBufferSeconds();
        UpdateStatus(s => s with { BufferTargetSeconds = target });
    }

    /// <summary>
    /// Drops the resampler and mixer state and everything still queued for the decoder.
    /// Their history belongs to the old signal, and carrying it across a retune would put
    /// a burst of noise into the first decoded block. The generation bump turns away the
    /// one block the decoder may already be holding.
    /// </summary>
    private void ResetIq()
    {
        lock (_iqGate)
        {
            Interlocked.Increment(ref _iqGeneration);
            _resampler.Reset();
            _mixer.Reset();
            _smoothedDbfs = -120;
            _lastSignalTicks = 0;
        }
        _iqQueue.Clear();
    }

    /// <summary>
    /// Fires once the grace period after a lost-sync event has expired without recovery.
    /// The delay is what stops a momentary fade from throwing away the prebuffer and
    /// audibly dropping back to analog for a second.
    /// </summary>
    private void ConfirmSyncLoss()
    {
        if (_disposed) return;
        var graceMs = SyncLossGraceMs();
        var lastDigital = Volatile.Read(ref _lastDigitalTicks);
        if (lastDigital != 0)
        {
            var elapsedMs = (Stopwatch.GetTimestamp() - lastDigital) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs < graceMs)
            {
                var remainingMs = Math.Max(50, (int)Math.Ceiling(graceMs - elapsedMs));
                _syncLossTimer.Change(remainingMs, Timeout.Infinite);
                return;
            }
        }

        Volatile.Write(ref _lastDigitalTicks, 0);
        ResetAudio();
        UpdateStatus(s => s with
        {
            Synced = false,
            Message = "HD signal lost; analog audio active",
            BitrateKbps = 0,
            BufferedSeconds = 0
        });
    }

    private void CancelPendingSyncLoss()
    {
        _syncLossTimer.Change(Timeout.Infinite, Timeout.Infinite);
        Volatile.Write(ref _lastDigitalTicks, 0);
    }

    /// <summary>
    /// Keeps the analog path out of a subchannel change: SDR# has already written its own
    /// audio into this block, so without it the switch bursts a fragment of the analog
    /// programme between the two HD subchannels. The level is ramped down rather than cut,
    /// and the hold expires so a subchannel that never delivers audio does not leave the
    /// listener in silence.
    /// </summary>
    private unsafe void HoldOverAnalog(float* buffer, int frames, double outputRate)
    {
        if (!_switchingProgram) return;
        if (Stopwatch.GetTimestamp() > _switchDeadlineTicks)
        {
            _switchingProgram = false;
            _outputGain = 1f;
            return;
        }

        var step = (float)(1.0 / (FadeSeconds * outputRate));
        for (var frame = 0; frame < frames; frame++)
        {
            _outputGain = Math.Max(0f, _outputGain - step);
            buffer[frame * 2] *= _outputGain;
            buffer[frame * 2 + 1] *= _outputGain;
        }
    }

    /// <summary>Arms the analog hold for a subchannel change, but never for a station change.</summary>
    private void BeginProgramSwitch()
    {
        // Both reads take other locks, and an UpdateStatus lambda can take the audio gate,
        // so neither may happen while this thread holds it.
        var covering = Enabled && ReplaceAnalogAudio && Status.Synced;
        var holdSeconds = Math.Max(MinSwitchHoldSeconds, EffectiveBufferSeconds() + 2.0);

        lock (_audioGate)
        {
            _switchingProgram = covering;
            _switchDeadlineTicks = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * holdSeconds);
            if (!covering) _outputGain = 1f;
        }
    }

    /// <summary>
    /// Empties the prebuffer and puts the output back on the analog path. Called whenever the
    /// audio in the ring no longer belongs to what the listener is tuned to.
    /// </summary>
    private void ResetAudio()
    {
        lock (_audioGate)
        {
            _audio.Clear();
            _haveAudioPair = false;
            _audioPhase = 0;
            _hdAudioActive = false;
            // A station change or a stop cancels any pending subchannel hold.
            _switchingProgram = false;
            _outputGain = 1f;
            // Otherwise the delay line replays a fragment of the previous station.
            _surround.Reset();
        }
    }

    /// <summary>
    /// Forgets everything the previous station said about itself: artwork, the port map,
    /// every track decoded or waiting to be shown, and the subchannel line-up.
    /// </summary>
    private void ResetMetadata()
    {
        lock (_artworkGate)
        {
            _lotImages.Clear();
            _portProgram.Clear();
            _portMime.Clear();
            Array.Clear(_programLinksImages);
        }
        lock (_trackGate)
        {
            Array.Clear(_receivedTracks);
            Array.Clear(_presentedTracks);
            _pendingTracks.Clear();
        }
        Volatile.Write(ref _programMask, 0);
        ResetBitrate();
    }

    private void ResetBitrate()
    {
        lock (_bitrateGate)
        {
            _bitrateBytes = 0;
            _bitrateStartedTicks = Stopwatch.GetTimestamp();
        }
    }

    private static void EnsureCapacity(ref float[] buffer, int length)
    {
        if (buffer.Length < length) Array.Resize(ref buffer, Math.Max(length, buffer.Length * 2));
    }

    private static long ReadNativeSize(IntPtr pointer, int offset) =>
        IntPtr.Size == 8 ? Marshal.ReadInt64(pointer, offset) : Marshal.ReadInt32(pointer, offset);

    private static float ReadFloat(IntPtr pointer, int offset) =>
        BitConverter.Int32BitsToSingle(Marshal.ReadInt32(pointer, offset));

    private static string ReadUtf8(IntPtr pointer) =>
        pointer == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(pointer) ?? "";
}
