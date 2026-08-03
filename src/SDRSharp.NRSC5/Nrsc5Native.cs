using System.Reflection;
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
    StationName = 16
}
