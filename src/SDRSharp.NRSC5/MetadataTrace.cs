namespace SDRSharp.NRSC5;

/// <summary>
/// An opt-in log of the now-playing metadata: every ID3 frame and its XHDR, every LOT
/// image, and the moment each track is actually shown. It exists because artwork that
/// "does not match the song" cannot be diagnosed from a screenshot: the question is
/// always which frame arrived when, and what it pointed at. Every ten seconds it also
/// records what decoding costs each thread, measured from inside the plugin.
///
/// Off unless the file <c>%LOCALAPPDATA%\SDRSharp.NRSC5\trace.enabled</c> exists when SDR#
/// starts; the log is written next to it as <c>metadata-trace.log</c>. Callers check
/// <see cref="Enabled"/> first so a disabled trace costs no string formatting.
/// </summary>
internal static class MetadataTrace
{
    private static readonly object Gate = new();
    private static readonly StreamWriter? Writer = Open();

    public static bool Enabled => Writer is not null;

    /// <summary>The OS id of the calling thread, to match what the trace says against a CPU profiler.</summary>
    public static uint CurrentOsThreadId() => GetCurrentThreadId();

    [System.Runtime.InteropServices.DllImport("kernel32")]
    private static extern uint GetCurrentThreadId();

    public static void Write(string line)
    {
        if (Writer is null) return;
        lock (Gate)
        {
            try
            {
                Writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {line}");
            }
            catch
            {
                // A full disk or a locked file must never reach the decoder thread.
            }
        }
    }

    private static StreamWriter? Open()
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SDRSharp.NRSC5");
            if (!File.Exists(Path.Combine(folder, "trace.enabled"))) return null;
            var writer = new StreamWriter(Path.Combine(folder, "metadata-trace.log"), append: true) { AutoFlush = true };
            writer.WriteLine($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss}  SDRSharp NRSC-5 {PluginInfo.DevelopmentVersion}");
            return writer;
        }
        catch
        {
            return null;
        }
    }
}
