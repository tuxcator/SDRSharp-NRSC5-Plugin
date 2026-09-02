using System.Runtime.InteropServices;
using SDRSharp.NRSC5;

VerifyEventLayout();
VerifyPiCodes();
VerifyFccParsing();

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
    Check("station_id.country_code", Nrsc5Layout.StationIdCountryCode, 0);
    Check("station_id.fcc_facility_id", Nrsc5Layout.StationIdFacilityId, 8);
    Check("station_slogan.slogan", Nrsc5Layout.StationSloganSlogan, 0);
    Check("station_message.message", Nrsc5Layout.StationMessageMessage, 0);
    Check("station_location.latitude", Nrsc5Layout.StationLocationLatitude, 0);
    Check("station_location.longitude", Nrsc5Layout.StationLocationLongitude, 4);
    Check("station_location.altitude", Nrsc5Layout.StationLocationAltitude, 8);

    Console.WriteLine("[OK] Los offsets de nrsc5_event_t coinciden con el encabezado oficial x64.");

    static void Check(string name, int actual, int expected)
    {
        if (actual != expected)
            throw new InvalidOperationException($"Offset {name}: se esperaba {expected}, se calculo {actual}.");
    }
}

// El PI code no viaja por HD Radio ni lo publica ninguna base de datos: se deriva del
// indicativo. WKTI es el ejemplo resuelto del propio estandar RBDS, asi que sirve de ancla.
static void VerifyPiCodes()
{
    Expect("WKTI", 0x7106);
    Expect("KQRS-FM", 0x3C0C);
    Expect("KQRS", 0x3C0C);

    // Sin formula: los indicativos de tres letras son una tabla del estandar, y Canada y
    // Mexico asignan el PI en vez de derivarlo. Vale mas un hueco que un numero inventado.
    ExpectNone("KOA");
    ExpectNone("CBLA-FM");
    ExpectNone("XHFM");
    ExpectNone("");

    Console.WriteLine("[OK] El PI code derivado del indicativo coincide con la regla RBDS.");

    static void Expect(string callsign, int expected)
    {
        var actual = StationFacts.PiCodeFor(callsign);
        if (actual != expected)
            throw new InvalidOperationException(
                $"PI de {callsign}: se esperaba 0x{expected:X4}, se obtuvo {(actual is { } pi ? $"0x{pi:X4}" : "nada")}.");
    }

    static void ExpectNone(string callsign)
    {
        if (StationFacts.PiCodeFor(callsign) is { } pi)
            throw new InvalidOperationException($"PI de '{callsign}': no debe derivarse, se obtuvo 0x{pi:X4}.");
    }
}

// Respuesta real de la FCC FM Query para la facility 35505 (KQRS-FM). Una emisora tiene
// varias filas y solo la licenciada "FM" lleva la potencia que de verdad radia: las "FS"
// son auxiliares de 32 kW y elegirlas mostraria una potencia falsa en el panel.
static void VerifyFccParsing()
{
    const string licensed =
        "|KQRS-FM     |92.5  MHz |FM |223 |ND  |H                   |C  |-  |LIC    |GOLDEN VALLEY            |MN |US |BLH-19910814KB      |100.   kW |100.   kW |315.0   |315.0   |35505      |N |45 |3  |29.8  |W |93  |7  |27.7  |RADIO LICENSE HOLDINGS LLC                                                  |   0.00 km |   0.00 mi |  0.00 deg |593.   m|593.0  m|-         |-       |-       |       m|199108140 |cf83d1b7d0ba49ec803ec1b6063ebeb7   |c257291be3ad492fba20c1b6063ebeb7   |";
    const string auxiliary =
        "|KQRS-FM     |92.5  MHz |FS |223 |ND  |H                   |C  |-  |LIC    |GOLDEN VALLEY            |MN |US |BMLH-20081010BDD    |32.    kW |32.    kW |251.0   |251.0   |35505      |N |44 |58 |33.9  |W |93  |16 |20.8  |RADIO LICENSE HOLDINGS LLC                                                  |   0.00 km |   0.00 mi |  0.00 deg |519.   m|519.0  m|-         |-       |1029019 |       m|200810108 |a7ba1757f747496cb020c1b6063ebeb7   |5f2191e9420d4e7e8983c1b6063ebeb7   |";
    const string other =
        "|KXXX-FM     |92.5  MHz |FM |223 |ND  |H                   |C  |-  |LIC    |SOMEWHERE ELSE           |WI |US |BLH-19910814KB      |6.0    kW |6.0    kW |100.0   |100.0   |99999      |N |45 |3  |29.8  |W |93  |7  |27.7  |SOME OTHER OWNER INC                                                        |   0.00 km |   0.00 mi |  0.00 deg |593.   m|593.0  m|-         |-       |-       |       m|199108140 |cf83d1b7d0ba49ec803ec1b6063ebeb7   |c257291be3ad492fba20c1b6063ebeb7   |";

    // El orden de las filas no debe decidir: la auxiliar aparece primero en el segundo caso.
    CheckKqrs(string.Join("\n", licensed, auxiliary, other));
    CheckKqrs(string.Join("\n", auxiliary, other, licensed));

    if (FccStationDirectory.Parse(string.Join("\n", auxiliary, other), 12345) is not null)
        throw new InvalidOperationException("Una facility ausente debe devolver nada, no la fila de otra emisora.");

    // Los sufijos societarios son siglas: "Radio License Holdings Llc" se lee como un error.
    if (FccStationDirectory.ToTitleCase("SOME OTHER OWNER INC") != "Some Other Owner INC")
        throw new InvalidOperationException("Las siglas societarias deben conservarse en mayusculas.");

    Console.WriteLine("[OK] El parseo de la FCC elige la licencia principal y su potencia real.");

    static void CheckKqrs(string body)
    {
        var record = FccStationDirectory.Parse(body, 35505)
            ?? throw new InvalidOperationException("No se reconocio la facility 35505 en la respuesta de la FCC.");
        Check("indicativo", record.Callsign, "KQRS-FM");
        Check("ciudad", record.City, "Golden Valley");
        Check("estado", record.State, "MN");
        Check("clase", record.StationClass, "C");
        Check("potencia", record.ErpKw.ToString("0.##"), "100");
        Check("HAAT", record.HaatMeters.ToString("0.##"), "315");
    }

    static void Check(string name, string actual, string expected)
    {
        if (actual != expected)
            throw new InvalidOperationException($"Campo {name}: se esperaba '{expected}', se obtuvo '{actual}'.");
    }
}

[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
static extern IntPtr AddDllDirectory(string path);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int OpenPipe(out IntPtr state);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void Close(IntPtr state);
