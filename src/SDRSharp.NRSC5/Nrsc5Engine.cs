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
    float MerLower,
    float MerUpper,
    float Ber,
    double InputRate,
    double OffsetHz)
{
    public static Nrsc5Status Idle { get; } = new(false, "Desactivado", "", "", "", 0, 0, 0, 0, 0);
}

internal sealed class Nrsc5Engine : IDisposable
{
    private readonly object _sessionGate = new();
    private readonly object _iqGate = new();
    private readonly object _audioGate = new();
    private readonly object _statusGate = new();
    private readonly PcmRingBuffer _audio = new(44100 * 6);
    private readonly Nrsc5Native.EventCallback _callback;
    private readonly System.Threading.Timer _retuneTimer;
    private IntPtr _session;
    private bool _disposed;
    private bool _enabled;
    private bool _replaceAnalogAudio = true;
    private int _selectedProgram;
    private double _inputSampleRate;
    private double _outputSampleRate;
    private double _tuningOffset;
    private double _samplesUntilOutput;
    private double _ncoPhase;
    private bool _havePreviousIq;
    private float _previousI;
    private float _previousQ;
    private float[] _iqOutput = new float[65536];
    private bool _haveAudioPair;
    private float _audioLeftA, _audioRightA, _audioLeftB, _audioRightB;
    private double _audioPhase;
    private Nrsc5Status _status = Nrsc5Status.Idle;

    public Nrsc5Engine()
    {
        _callback = OnNativeEvent;
        _retuneTimer = new System.Threading.Timer(_ => { if (Enabled) Restart(); }, null, Timeout.Infinite, Timeout.Infinite);
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
            else Stop("Desactivado");
        }
    }

    public bool ReplaceAnalogAudio
    {
        get => Volatile.Read(ref _replaceAnalogAudio);
        set => Volatile.Write(ref _replaceAnalogAudio, value);
    }

    public int SelectedProgram
    {
        get => Volatile.Read(ref _selectedProgram);
        set
        {
            Volatile.Write(ref _selectedProgram, Math.Clamp(value, 0, 7));
            ResetAudio();
            UpdateStatus(s => s with { Title = "", Artist = "", Message = s.Synced ? $"Sincronizado HD{value + 1}" : s.Message });
        }
    }

    public double InputSampleRate
    {
        get => Volatile.Read(ref _inputSampleRate);
        set
        {
            Volatile.Write(ref _inputSampleRate, value);
            ResetIq();
        }
    }

    public double OutputSampleRate
    {
        get => Volatile.Read(ref _outputSampleRate);
        set => Volatile.Write(ref _outputSampleRate, value);
    }

    public void SetTuningOffset(double value)
    {
        Volatile.Write(ref _tuningOffset, value);
        UpdateStatus(s => s with { OffsetHz = value });
    }

    public void NotifyFrequencyChanged(long frequency)
    {
        ResetAudio();
        UpdateStatus(_ => Nrsc5Status.Idle with { Message = $"Sintonizando {frequency / 1_000_000.0:0.0} MHz..." });
        if (Enabled) _retuneTimer.Change(350, Timeout.Infinite);
    }

    public void Restart()
    {
        if (_disposed || !Enabled) return;
        Stop("Reiniciando...");
        if (Enabled) Start();
    }

    public unsafe void ProcessIq(Complex* buffer, int length)
    {
        if (!Enabled || length <= 1) return;

        var inputRate = InputSampleRate;
        if (inputRate < Nrsc5Native.NativeFmSampleRate)
        {
            UpdateStatus(s => s with { Synced = false, Message = $"IQ insuficiente: {inputRate / 1000:0} kS/s; minimo 744.2 kS/s" });
            return;
        }

        int produced;
        lock (_iqGate)
        {
            var maxComplex = (int)Math.Ceiling(length * Nrsc5Native.NativeFmSampleRate / inputRate) + 4;
            EnsureIqCapacity(maxComplex * 2);
            produced = ResampleAndMix(buffer, length, inputRate, _iqOutput);
        }

        if (produced == 0) return;
        lock (_sessionGate)
        {
            if (_session == IntPtr.Zero) return;
            fixed (float* samples = _iqOutput)
                Nrsc5Native.nrsc5_pipe_samples_cf32(_session, samples, (uint)(produced * 2));
        }
    }

    public unsafe void ProcessAudio(float* buffer, int length)
    {
        if (!Enabled || !ReplaceAnalogAudio || !Status.Synced || length < 2) return;

        var outputRate = OutputSampleRate;
        if (outputRate <= 0) return;
        var frames = length / 2;
        var required = (int)Math.Ceiling(frames * Nrsc5Native.AudioSampleRate / outputRate) + 3;
        if (_audio.AvailableFrames < required) return;

        lock (_audioGate)
        {
            if (!_haveAudioPair)
            {
                if (!_audio.TryReadFrame(out _audioLeftA, out _audioRightA) ||
                    !_audio.TryReadFrame(out _audioLeftB, out _audioRightB)) return;
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
        _retuneTimer.Dispose();
        Stop("Cerrado");
    }

    private void Start()
    {
        lock (_sessionGate)
        {
            if (_session != IntPtr.Zero) return;
            try
            {
                if (!Environment.Is64BitProcess)
                    throw new PlatformNotSupportedException("Este paquete requiere SDR# x64; el proceso actual es x86.");

                if (Nrsc5Native.nrsc5_open_pipe(out var state) != 0 || state == IntPtr.Zero)
                    throw new InvalidOperationException("nrsc5_open_pipe fallo.");

                Nrsc5Native.nrsc5_set_callback(state, _callback, IntPtr.Zero);
                if (Nrsc5Native.nrsc5_set_mode(state, 0) != 0)
                {
                    Nrsc5Native.nrsc5_close(state);
                    throw new InvalidOperationException("No se pudo seleccionar NRSC-5 FM.");
                }

                Nrsc5Native.nrsc5_start(state);
                _session = state;
                ResetIq();
                ResetAudio();
                UpdateStatus(_ => Nrsc5Status.Idle with { Message = "Buscando señal NRSC-5..." });
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
        UpdateStatus(_ => Nrsc5Status.Idle with { Message = message });
    }

    private unsafe int ResampleAndMix(Complex* input, int length, double inputRate, float[] output)
    {
        var step = inputRate / Nrsc5Native.NativeFmSampleRate;
        var phaseStep = -2.0 * Math.PI * Volatile.Read(ref _tuningOffset) / Nrsc5Native.NativeFmSampleRate;
        var outIndex = 0;

        for (var index = 0; index < length; index++)
        {
            var currentI = input[index].Real;
            var currentQ = input[index].Imag;
            if (!_havePreviousIq)
            {
                _previousI = currentI;
                _previousQ = currentQ;
                _havePreviousIq = true;
                continue;
            }

            while (_samplesUntilOutput <= 1.0)
            {
                var t = (float)_samplesUntilOutput;
                var i = _previousI + (currentI - _previousI) * t;
                var q = _previousQ + (currentQ - _previousQ) * t;
                var cos = (float)Math.Cos(_ncoPhase);
                var sin = (float)Math.Sin(_ncoPhase);
                output[outIndex * 2] = i * cos - q * sin;
                output[outIndex * 2 + 1] = i * sin + q * cos;
                outIndex++;
                _ncoPhase += phaseStep;
                if (_ncoPhase > Math.PI) _ncoPhase -= 2 * Math.PI;
                else if (_ncoPhase < -Math.PI) _ncoPhase += 2 * Math.PI;
                _samplesUntilOutput += step;
            }

            _samplesUntilOutput -= 1.0;
            _previousI = currentI;
            _previousQ = currentQ;
        }

        return outIndex;
    }

    private void OnNativeEvent(IntPtr evt, IntPtr opaque)
    {
        try
        {
            var type = (Nrsc5Event)Marshal.ReadInt32(evt);
            var union = IntPtr.Add(evt, IntPtr.Size == 8 ? 8 : 4);
            switch (type)
            {
                case Nrsc5Event.Sync:
                    UpdateStatus(s => s with { Synced = true, Message = $"Sincronizado HD{SelectedProgram + 1}" });
                    break;
                case Nrsc5Event.LostSync:
                    ResetAudio();
                    UpdateStatus(s => s with { Synced = false, Message = "Se perdio la señal HD; audio analogico activo" });
                    break;
                case Nrsc5Event.Mer:
                    UpdateStatus(s => s with
                    {
                        MerLower = ReadFloat(union, 0),
                        MerUpper = ReadFloat(union, 4)
                    });
                    break;
                case Nrsc5Event.Ber:
                    UpdateStatus(s => s with { Ber = ReadFloat(union, 0) });
                    break;
                case Nrsc5Event.Audio:
                    ReceiveAudio(union);
                    break;
                case Nrsc5Event.Id3:
                    ReceiveId3(union);
                    break;
                case Nrsc5Event.StationName:
                    var station = ReadUtf8(Marshal.ReadIntPtr(union));
                    if (!string.IsNullOrWhiteSpace(station)) UpdateStatus(s => s with { Station = station });
                    break;
            }
        }
        catch
        {
            // Nunca permita que una excepcion administrada cruce el callback nativo.
        }
    }

    private void ReceiveAudio(IntPtr union)
    {
        var pointerOffset = IntPtr.Size == 8 ? 8 : 4;
        var program = Marshal.ReadInt32(union);
        if (program != SelectedProgram) return;
        var data = Marshal.ReadIntPtr(union, pointerOffset);
        var countOffset = pointerOffset + IntPtr.Size;
        var count64 = IntPtr.Size == 8 ? Marshal.ReadInt64(union, countOffset) : Marshal.ReadInt32(union, countOffset);
        if (data == IntPtr.Zero || count64 <= 0 || count64 > 65536) return;
        var pcm = new short[(int)count64];
        Marshal.Copy(data, pcm, 0, pcm.Length);
        _audio.Write(pcm);
    }

    private void ReceiveId3(IntPtr union)
    {
        var program = Marshal.ReadInt32(union);
        if (program != SelectedProgram) return;
        var pointerOffset = IntPtr.Size == 8 ? 8 : 4;
        var title = ReadUtf8(Marshal.ReadIntPtr(union, pointerOffset));
        var artist = ReadUtf8(Marshal.ReadIntPtr(union, pointerOffset + IntPtr.Size));
        UpdateStatus(s => s with { Title = title, Artist = artist });
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

    private void ResetIq()
    {
        lock (_iqGate)
        {
            _samplesUntilOutput = 0;
            _ncoPhase = 0;
            _havePreviousIq = false;
        }
    }

    private void ResetAudio()
    {
        lock (_audioGate)
        {
            _audio.Clear();
            _haveAudioPair = false;
            _audioPhase = 0;
        }
    }

    private void EnsureIqCapacity(int length)
    {
        if (_iqOutput.Length < length) Array.Resize(ref _iqOutput, Math.Max(length, _iqOutput.Length * 2));
    }

    private static float ReadFloat(IntPtr pointer, int offset) =>
        BitConverter.Int32BitsToSingle(Marshal.ReadInt32(pointer, offset));

    private static string ReadUtf8(IntPtr pointer) =>
        pointer == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(pointer) ?? "";
}
