using System.Runtime.InteropServices;
using SDRSharp.NRSC5;

VerifyEventLayout();

if (args.Length != 1) throw new ArgumentException("Indique la carpeta NRSC5Runtime.");
var runtime = Path.GetFullPath(args[0]);
var library = Path.Combine(runtime, "libnrsc5.dll");
if (!File.Exists(library)) throw new FileNotFoundException("No se encontro libnrsc5.dll", library);

AddDllDirectory(runtime);
var module = NativeLibrary.Load(library);
try
{
    var open = Marshal.GetDelegateForFunctionPointer<OpenPipe>(NativeLibrary.GetExport(module, "nrsc5_open_pipe"));
    var close = Marshal.GetDelegateForFunctionPointer<Close>(NativeLibrary.GetExport(module, "nrsc5_close"));
    var result = open(out var state);
    if (result != 0 || state == IntPtr.Zero) throw new InvalidOperationException($"nrsc5_open_pipe devolvio {result}.");
    close(state);
    Console.WriteLine("[OK] libnrsc5 cargo, abrio una sesion IQ y la cerro correctamente.");
}
finally
{
    NativeLibrary.Free(module);
}

// Los offsets de nrsc5_event_t se calculan en Nrsc5Layout. Si se desvian, los eventos
// se leen desde bytes equivocados y el sintoma tipico es que el Artwork nunca aparece,
// asi que se comparan contra los valores del encabezado oficial para x64.
static void VerifyEventLayout()
{
    if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Ejecute el smoke test en x64.");

    Check("union", Nrsc5Layout.Union, 8);
    Check("hdc.data", Nrsc5Layout.HdcData, 8);
    Check("hdc.count", Nrsc5Layout.HdcCount, 16);
    Check("audio.data", Nrsc5Layout.AudioData, 8);
    Check("audio.count", Nrsc5Layout.AudioCount, 16);
    Check("id3.title", Nrsc5Layout.Id3Title, 8);
    Check("id3.artist", Nrsc5Layout.Id3Artist, 16);
    Check("id3.album", Nrsc5Layout.Id3Album, 24);
    Check("id3.xhdr.mime", Nrsc5Layout.Id3XhdrMime, 56);
    Check("id3.xhdr.lot", Nrsc5Layout.Id3XhdrLot, 64);
    Check("lot.lot", Nrsc5Layout.LotId, 4);
    Check("lot.size", Nrsc5Layout.LotSize, 8);
    Check("lot.mime", Nrsc5Layout.LotMime, 12);
    Check("lot.data", Nrsc5Layout.LotData, 24);
    Check("lot.service", Nrsc5Layout.LotService, 40);
    Check("sig_service.type", Nrsc5Layout.SigServiceType, 8);
    Check("sig_service.number", Nrsc5Layout.SigServiceNumber, 10);
    Check("sig_service.name", Nrsc5Layout.SigServiceName, 16);
    Check("sig_service.audio_component", Nrsc5Layout.SigServiceAudioComponent, 32);
    Check("sig_service.next", Nrsc5Layout.SigServiceNext, 0);

    Console.WriteLine("[OK] Los offsets de nrsc5_event_t coinciden con el encabezado oficial x64.");

    static void Check(string name, int actual, int expected)
    {
        if (actual != expected)
            throw new InvalidOperationException($"Offset {name}: se esperaba {expected}, se calculo {actual}.");
    }
}

[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
static extern IntPtr AddDllDirectory(string path);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int OpenPipe(out IntPtr state);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void Close(IntPtr state);
