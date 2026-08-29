using System.Runtime.InteropServices;

namespace SDRSharp.NRSC5;

internal static class Nrsc5Native
{
    internal const double NativeFmSampleRate = 744187.5;
    internal const double AudioSampleRate = 44100.0;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void EventCallback(IntPtr evt, IntPtr opaque);

    static Nrsc5Native()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Keep native DLLs outside Plugins: SDR# recursively inspects that tree
        // and would try to load native dependencies as managed plugins.
        var runtimeDir = Path.Combine(AppContext.BaseDirectory, "NRSC5Runtime");
        if (Directory.Exists(runtimeDir)) AddDllDirectory(runtimeDir);

        NativeLibrary.SetDllImportResolver(typeof(Nrsc5Native).Assembly, (name, assembly, path) =>
        {
            if (!name.Equals("libnrsc5", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
            var fullPath = Path.Combine(runtimeDir, "libnrsc5.dll");
            return File.Exists(fullPath) ? NativeLibrary.Load(fullPath) : IntPtr.Zero;
        });
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    [DllImport("libnrsc5", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int nrsc5_open_pipe(out IntPtr state);

    [DllImport("libnrsc5", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void nrsc5_close(IntPtr state);

    [DllImport("libnrsc5", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void nrsc5_start(IntPtr state);

    [DllImport("libnrsc5", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void nrsc5_stop(IntPtr state);

    [DllImport("libnrsc5", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int nrsc5_set_mode(IntPtr state, int mode);

    [DllImport("libnrsc5", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void nrsc5_set_callback(IntPtr state, EventCallback callback, IntPtr opaque);

    [DllImport("libnrsc5", CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int nrsc5_pipe_samples_cf32(IntPtr state, float* samples, uint length);
}

internal enum Nrsc5Event
{
    LostDevice = 0,
    Iq = 1,
    Sync = 2,
    LostSync = 3,
    Mer = 4,
    Ber = 5,
    Hdc = 6,
    Audio = 7,
    Id3 = 8,
    Sig = 9,
    Lot = 10,
    Sis = 11,
    Stream = 12,
    Packet = 13,
    AudioService = 14,
    StationId = 15,
    StationName = 16,
    StationSlogan = 17,
    StationMessage = 18,
    StationLocation = 19,
    AudioServiceDescriptor = 20,
    DataServiceDescriptor = 21,
    EmergencyAlert = 22,
    HereImage = 23,
    LotHeader = 24,
    LotFragment = 25,
    Agc = 26,
    ExciterInfo = 27,
    ImporterInfo = 28,
    LeapSecondOffset = 29,
    LocalTime = 30
}

internal static class Nrsc5Mime
{
    internal const uint PrimaryImage = 0xBE4B7536;
    internal const uint StationLogo = 0xD9C72536;
    internal const uint Jpeg = 0x1E653E9C;
    internal const uint Png = 0x4F328CA0;

    internal static bool IsImage(uint mime) =>
        mime is PrimaryImage or StationLogo or Jpeg or Png;
}

internal static class Nrsc5SigServiceType
{
    internal const int Audio = 0;
    internal const int Data = 1;
}

/// <summary>
/// Byte offsets of every <c>nrsc5_event_t</c> union member this plugin reads,
/// derived from the C layout rules instead of being written out by hand.
/// Verified against upstream <c>include/nrsc5.h</c>.
///
/// The union is preceded by <c>unsigned int event</c> and then padded up to the
/// alignment of its widest member, which is a pointer on both targets.
/// </summary>
internal static class Nrsc5Layout
{
    private static readonly int Ptr = IntPtr.Size;

    /// <summary>Offset of the union relative to the start of the event struct.</summary>
    internal static readonly int Union = Align(sizeof(uint), IntPtr.Size);

    // struct { float lower; float upper; } mer;
    internal const int MerLower = 0;
    internal const int MerUpper = 4;

    // struct { float cber; } ber;
    internal const int BerCber = 0;

    // struct { unsigned int program; const uint8_t *data; size_t count; unsigned int flags; } hdc;
    internal const int HdcProgram = 0;
    internal static readonly int HdcData = Align(4, Ptr);
    internal static readonly int HdcCount = HdcData + Ptr;
    internal static readonly int HdcFlags = HdcCount + Ptr;

    // struct { unsigned int program; const int16_t *data; size_t count; } audio;
    internal const int AudioProgram = 0;
    internal static readonly int AudioData = Align(4, Ptr);
    internal static readonly int AudioCount = AudioData + Ptr;

    // struct { unsigned int program; const char *title, *artist, *album, *genre;
    //          struct { const char *owner, *id; } ufid;
    //          struct { uint32_t mime; int param; int lot; } xhdr; ... } id3;
    internal const int Id3Program = 0;
    internal static readonly int Id3Title = Align(4, Ptr);
    internal static readonly int Id3Artist = Id3Title + Ptr;
    internal static readonly int Id3Album = Id3Artist + Ptr;
    internal static readonly int Id3Genre = Id3Album + Ptr;
    internal static readonly int Id3UfidOwner = Id3Genre + Ptr;
    internal static readonly int Id3UfidId = Id3UfidOwner + Ptr;
    internal static readonly int Id3XhdrMime = Id3UfidId + Ptr;
    internal static readonly int Id3XhdrParam = Id3XhdrMime + 4;
    internal static readonly int Id3XhdrLot = Id3XhdrParam + 4;

    // struct { uint16_t port; unsigned int lot, size; uint32_t mime; const char *name;
    //          const uint8_t *data; struct tm *expiry_utc;
    //          nrsc5_sig_service_t *service; nrsc5_sig_component_t *component; } lot;
    internal const int LotPort = 0;
    internal const int LotId = 4;
    internal const int LotSize = 8;
    internal const int LotMime = 12;
    internal static readonly int LotName = Align(16, Ptr);
    internal static readonly int LotData = LotName + Ptr;
    internal static readonly int LotExpiry = LotData + Ptr;
    internal static readonly int LotService = LotExpiry + Ptr;
    internal static readonly int LotComponent = LotService + Ptr;

    // struct { unsigned int program, access, type, codec_mode, blend_control;
    //          int digital_audio_gain; unsigned int common_delay, latency; } audio_service;
    internal const int AudioServiceProgram = 0;

    // struct { nrsc5_sig_service_t *services; } sig;
    internal const int SigServices = 0;

    // struct { const char *name; } station_name;
    internal const int StationNameName = 0;

    // struct nrsc5_sig_service_t { next; uint8_t type; uint16_t number;
    //                              const char *name; components; audio_component; }
    internal const int SigServiceNext = 0;
    internal static readonly int SigServiceType = Ptr;
    internal static readonly int SigServiceNumber = Align(Ptr + 1, 2);
    internal static readonly int SigServiceName = Align(SigServiceNumber + 2, Ptr);
    internal static readonly int SigServiceComponents = SigServiceName + Ptr;
    internal static readonly int SigServiceAudioComponent = SigServiceComponents + Ptr;

    // struct nrsc5_sig_component_t { next; uint8_t type; uint8_t id;
    //   union { struct { uint16_t port, service_data_type; uint8_t type; uint32_t mime; } data;
    //           struct { uint8_t port, type; uint32_t mime; } audio; }; }
    // The widest union member is uint32_t, so the union starts on a 4-byte boundary.
    internal const int SigComponentNext = 0;
    internal static readonly int SigComponentType = Ptr;
    internal static readonly int SigComponentId = Ptr + 1;
    private static readonly int SigComponentUnion = Align(Ptr + 2, 4);
    internal static readonly int SigComponentDataPort = SigComponentUnion;
    internal static readonly int SigComponentDataMime = Align(SigComponentUnion + 5, 4);
    internal static readonly int SigComponentAudioPort = SigComponentUnion;
    internal static readonly int SigComponentAudioMime = Align(SigComponentUnion + 2, 4);

    private static int Align(int offset, int alignment) =>
        (offset + alignment - 1) / alignment * alignment;
}
