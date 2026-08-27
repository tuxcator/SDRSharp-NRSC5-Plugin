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
    float BufferTargetSeconds)
{
    public static Nrsc5Status Idle { get; } = new(
        false, "Disabled", "", "", "", "", 0, 0, 0, 0, 0,
        -120, -120, -150, 0, 0, null, false, 0, 0, 0, 0);

    public bool HasProgram(int index) => (ProgramMask & (1 << index)) != 0;
}

internal sealed class Nrsc5Engine : IDisposable
{
    private const int SignalProbeSamples = 256;
    private const int MinSyncLossGraceMs = 1500;
    private const int MaxArtworkBytes = 8 * 1024 * 1024;
    private const int MaxCachedImages = 24;

    internal const double MinBufferSeconds = 0.1;
    internal const double MaxBufferSeconds = 10.0;
    internal const double DefaultBufferSeconds = 0.75;

    private readonly object _sessionGate = new();
    private readonly object _iqGate = new();
    private readonly object _audioGate = new();
    private readonly object _statusGate = new();
    private readonly object _artworkGate = new();
    private readonly object _bitrateGate = new();
    private readonly PcmRingBuffer _audio = new((int)(Nrsc5Native.AudioSampleRate * 3));
    private readonly PolyphaseResampler _resampler = new();
    private readonly Nrsc5Native.EventCallback _callback;
    private readonly System.Threading.Timer _retuneTimer;
    private readonly System.Threading.Timer _syncLossTimer;

    // Artwork caches. LOT ids are only unique within a service port, so the cache is
    // keyed by both; a bare lot id collides between subchannels of the same station.
    private readonly Dictionary<(int Port, int Lot), CachedImage> _lotImages = new();
    private readonly (int Port, int Lot)[] _xhdrByProgram = new (int, int)[8];
    private readonly byte[]?[] _latestArtByProgram = new byte[8][];
    private readonly byte[]?[] _stationLogoByProgram = new byte[8][];
    private byte[]? _stationLogo;

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
    private double _ncoPhase;
    private float[] _mixed = new float[65536];
    private float[] _iqOutput = new float[65536];
    private bool _haveAudioPair;
    private float _audioLeftA, _audioRightA, _audioLeftB, _audioRightB;
    private double _audioPhase;
    private bool _hdAudioActive;
    private long _lastDigitalTicks;
    private long _lastSignalTicks;
    private float _smoothedDbfs = -120;
    private float _dbmCalibrationOffset = -30;
    private long _bitrateBytes;
    private long _bitrateStartedTicks = Stopwatch.GetTimestamp();
    private Nrsc5Status _status = Nrsc5Status.Idle;

    private readonly record struct CachedImage(byte[] Bytes, uint Mime, int Program);

    public Nrsc5Engine()
    {
        Array.Fill(_xhdrByProgram, (-1, -1));
        _callback = OnNativeEvent;
        _retuneTimer = new System.Threading.Timer(_ => { if (Enabled) Restart(); }, null, Timeout.Infinite, Timeout.Infinite);
        _syncLossTimer = new System.Threading.Timer(_ => ConfirmSyncLoss(), null, Timeout.Infinite, Timeout.Infinite);
        ApplyBufferCapacity();
        // Seed the buffer target so a panel attaching later reads the real value
        // instead of the zero in Nrsc5Status.Idle, which renders as "BUFFER OFF".
        PublishBufferState();
    }

    public event Action<Nrsc5Status>? StatusChanged;

    public Nrsc5Status Status
    {
        get { lock (_statusGate) return _status; }
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
            ResetBitrate();
            UpdateStatus(s => s with
            {
                Title = "",
                Artist = "",
                Album = "",
                BitrateKbps = 0,
                SelectedProgram = value,
                Message = s.Synced ? $"Synchronized HD{value + 1}" : s.Message
            });
            RefreshArtwork();
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

    public void SetTuningOffset(double value)
    {
        Volatile.Write(ref _tuningOffset, value);
        UpdateStatus(s => s with { OffsetHz = value });
    }

    public void NotifyFrequencyChanged(long frequency)
    {
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

    public void Restart()
    {
        if (_disposed || !Enabled) return;
        Stop("Restarting...");
        if (Enabled) Start();
    }

    public unsafe void ProcessIq(Complex* buffer, int length)
    {
        if (!Enabled || length <= 1) return;

        var inputRate = InputSampleRate;
        if (inputRate < Nrsc5Native.NativeFmSampleRate)
        {
            UpdateStatus(s => s with { Synced = false, Message = $"IQ sample rate too low: {inputRate / 1000:0} kS/s; minimum 744.2 kS/s" });
            return;
        }

        int produced;
        lock (_iqGate)
        {
            _resampler.Configure(inputRate, Nrsc5Native.NativeFmSampleRate);
            EnsureCapacity(ref _mixed, length * 2);
            MixToBaseband(buffer, length, inputRate);
            produced = _resampler.Process(_mixed, length, ref _iqOutput);
        }

        if (produced == 0) return;
        UpdateSignalMonitor(_iqOutput, produced);
        lock (_sessionGate)
        {
            if (_session == IntPtr.Zero) return;
            fixed (float* samples = _iqOutput)
                Nrsc5Native.nrsc5_pipe_samples_cf32(_session, samples, (uint)(produced * 2));
        }
    }

    /// <summary>
    /// Shifts the selected VFO down to DC at the incoming sample rate. This has to happen
    /// before decimation: the anti-alias filter is centred on DC, so mixing afterwards
    /// would filter away the very carrier being tuned.
    /// </summary>
    private unsafe void MixToBaseband(Complex* input, int length, double inputRate)
    {
        var phaseStep = -2.0 * Math.PI * Volatile.Read(ref _tuningOffset) / inputRate;
        for (var index = 0; index < length; index++)
        {
            var i = input[index].Real;
            var q = input[index].Imag;
            var cos = (float)Math.Cos(_ncoPhase);
            var sin = (float)Math.Sin(_ncoPhase);
            _mixed[index * 2] = i * cos - q * sin;
            _mixed[index * 2 + 1] = i * sin + q * cos;
            _ncoPhase += phaseStep;
            if (_ncoPhase > Math.PI) _ncoPhase -= 2 * Math.PI;
            else if (_ncoPhase < -Math.PI) _ncoPhase += 2 * Math.PI;
        }
    }

    public unsafe void ProcessAudio(float* buffer, int length)
    {
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
                if (available < Math.Max(required, startup)) return;
                _hdAudioActive = true;
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
                buffer[frame * 2] = _audioLeftA + (_audioLeftB - _audioLeftA) * t;
                buffer[frame * 2 + 1] = _audioRightA + (_audioRightB - _audioRightA) * t;
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
    }

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
                    if (!string.IsNullOrWhiteSpace(station)) UpdateStatus(s => s with { Station = station });
                    break;
            }
        }
        catch
        {
            // Never let a managed exception cross back into the native callback.
        }
    }

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

    private void ReceiveAudio(IntPtr union)
    {
        var program = Marshal.ReadInt32(union, Nrsc5Layout.AudioProgram);
        MarkProgramAvailable(program);
        if (program != SelectedProgram) return;
        var data = Marshal.ReadIntPtr(union, Nrsc5Layout.AudioData);
        var count = ReadNativeSize(union, Nrsc5Layout.AudioCount);
        if (data == IntPtr.Zero || count <= 0 || count > 65536) return;
        var pcm = new short[(int)count];
        Marshal.Copy(data, pcm, 0, pcm.Length);
        Volatile.Write(ref _lastDigitalTicks, Stopwatch.GetTimestamp());
        _audio.Write(pcm);
    }

    private void ReceiveId3(IntPtr union)
    {
        var program = Marshal.ReadInt32(union, Nrsc5Layout.Id3Program);
        if (program is < 0 or > 7) return;
        MarkProgramAvailable(program);

        var title = ReadUtf8(Marshal.ReadIntPtr(union, Nrsc5Layout.Id3Title));
        var artist = ReadUtf8(Marshal.ReadIntPtr(union, Nrsc5Layout.Id3Artist));
        var album = ReadUtf8(Marshal.ReadIntPtr(union, Nrsc5Layout.Id3Album));
        var mime = unchecked((uint)Marshal.ReadInt32(union, Nrsc5Layout.Id3XhdrMime));
        var lot = Marshal.ReadInt32(union, Nrsc5Layout.Id3XhdrLot);

        lock (_artworkGate)
        {
            // The XHDR carries no port, so match the lot id against any port already
            // cached for this program before falling back to a port-agnostic entry.
            _xhdrByProgram[program] = lot >= 0 && Nrsc5Mime.IsImage(mime) ? (-1, lot) : (-1, -1);
        }

        if (program == SelectedProgram)
            UpdateStatus(s => s with { Title = title, Artist = artist, Album = album });
        RefreshArtwork();
    }

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

        lock (_artworkGate)
        {
            if (_lotImages.Count >= MaxCachedImages && !_lotImages.ContainsKey((port, lot)))
                _lotImages.Remove(_lotImages.Keys.First());
            _lotImages[(port, lot)] = new CachedImage(bytes, mime, program);

            if (mime == Nrsc5Mime.StationLogo)
            {
                // A station logo is never referenced by an ID3 XHDR. Keeping it only in
                // the lot cache is why it used to arrive and never appear on screen.
                if (program >= 0) _stationLogoByProgram[program] = bytes;
                else _stationLogo = bytes;
            }
            else if (program >= 0)
            {
                _latestArtByProgram[program] = bytes;
            }
            else
            {
                // No SIG binding yet: assume it belongs to the program being listened to.
                _latestArtByProgram[SelectedProgram] = bytes;
            }
        }

        RefreshArtwork();
    }

    /// <summary>
    /// Walks the SIG linked list to learn which subchannels exist. Pointers are only
    /// valid for the duration of the callback, so everything needed is copied here.
    /// </summary>
    private void ReceiveSig(IntPtr union)
    {
        var service = Marshal.ReadIntPtr(union, Nrsc5Layout.SigServices);
        var guard = 0;
        while (service != IntPtr.Zero && guard++ < 64)
        {
            var type = Marshal.ReadByte(service, Nrsc5Layout.SigServiceType);
            var number = Marshal.ReadInt16(service, Nrsc5Layout.SigServiceNumber) & 0xFFFF;
            var audioComponent = Marshal.ReadIntPtr(service, Nrsc5Layout.SigServiceAudioComponent);
            if (type == Nrsc5SigServiceType.Audio && audioComponent != IntPtr.Zero)
                MarkProgramAvailable(number - 1);
            service = Marshal.ReadIntPtr(service, Nrsc5Layout.SigServiceNext);
        }
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
    /// Resolves what to show for the selected program, most specific first: the image the
    /// current ID3 XHDR points at, then the most recent album art seen on this program,
    /// then the station logo. Stations that broadcast art without a matching XHDR, and
    /// station logos that are never referenced at all, both used to fall through to
    /// nothing at all.
    /// </summary>
    private void RefreshArtwork()
    {
        var program = SelectedProgram;
        byte[]? chosen;
        var isLogo = false;

        lock (_artworkGate)
        {
            chosen = null;
            var (_, lot) = _xhdrByProgram[program];
            if (lot >= 0)
            {
                foreach (var entry in _lotImages)
                {
                    if (entry.Key.Lot != lot) continue;
                    if (entry.Value.Program >= 0 && entry.Value.Program != program) continue;
                    chosen = entry.Value.Bytes;
                    break;
                }
            }

            chosen ??= _latestArtByProgram[program];
            if (chosen is null)
            {
                chosen = _stationLogoByProgram[program] ?? _stationLogo;
                isLogo = chosen is not null;
            }
        }

        var logo = isLogo;
        UpdateStatus(s => s with { Artwork = chosen, ArtworkIsStationLogo = logo });
    }

    private static bool LooksLikeImage(byte[] data) =>
        data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF ||
        data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

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

    private void ResetIq()
    {
        lock (_iqGate)
        {
            _resampler.Reset();
            _ncoPhase = 0;
            _smoothedDbfs = -120;
            _lastSignalTicks = 0;
        }
    }

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

    private void ResetAudio()
    {
        lock (_audioGate)
        {
            _audio.Clear();
            _haveAudioPair = false;
            _audioPhase = 0;
            _hdAudioActive = false;
        }
    }

    private void ResetMetadata()
    {
        lock (_artworkGate)
        {
            _lotImages.Clear();
            Array.Fill(_xhdrByProgram, (-1, -1));
            Array.Clear(_latestArtByProgram);
            Array.Clear(_stationLogoByProgram);
            _stationLogo = null;
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
