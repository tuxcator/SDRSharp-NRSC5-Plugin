using System.Runtime.InteropServices;

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

[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
static extern IntPtr AddDllDirectory(string path);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int OpenPipe(out IntPtr state);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void Close(IntPtr state);
