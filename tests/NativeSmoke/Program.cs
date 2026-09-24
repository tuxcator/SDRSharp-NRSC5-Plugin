using System.Runtime.InteropServices;
using SDRSharp.NRSC5;

VerifyEventLayout();
VerifyPiCodes();
VerifyFccParsing();
VerifyGeocoding();
VerifySuspectSites();
VerifyDataServices();
VerifyTrackPresentation();
VerifyIqQueue();

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
    // 4.0.0 reconoce los logos por el componente SIG de su puerto: estos offsets pasan a importar.
    Check("lot.component", Nrsc5Layout.LotComponent, 48);
    Check("sig_service.components", Nrsc5Layout.SigServiceComponents, 24);
    Check("sig_component.type", Nrsc5Layout.SigComponentType, 8);
    Check("sig_component.data.port", Nrsc5Layout.SigComponentDataPort, 12);
    Check("sig_component.data.mime", Nrsc5Layout.SigComponentDataMime, 20);
    Check("station_id.country_code", Nrsc5Layout.StationIdCountryCode, 0);
    Check("station_id.fcc_facility_id", Nrsc5Layout.StationIdFacilityId, 8);
    Check("station_slogan.slogan", Nrsc5Layout.StationSloganSlogan, 0);
    Check("station_message.message", Nrsc5Layout.StationMessageMessage, 0);
    Check("station_location.latitude", Nrsc5Layout.StationLocationLatitude, 0);
    Check("station_location.longitude", Nrsc5Layout.StationLocationLongitude, 4);
    Check("station_location.altitude", Nrsc5Layout.StationLocationAltitude, 8);
    Check("here_image.image_type", Nrsc5Layout.HereImageType, 0);
    Check("here_image.seq", Nrsc5Layout.HereImageSeq, 4);
    Check("here_image.n1", Nrsc5Layout.HereImageN1, 8);
    Check("here_image.n2", Nrsc5Layout.HereImageN2, 12);
    Check("here_image.time_utc", Nrsc5Layout.HereImageTime, 16);
    Check("here_image.latitude1", Nrsc5Layout.HereImageLatitude1, 24);
    Check("here_image.longitude1", Nrsc5Layout.HereImageLongitude1, 28);
    Check("here_image.latitude2", Nrsc5Layout.HereImageLatitude2, 32);
    Check("here_image.longitude2", Nrsc5Layout.HereImageLongitude2, 36);
    Check("here_image.name", Nrsc5Layout.HereImageName, 40);
    Check("here_image.size", Nrsc5Layout.HereImageSize, 48);
    Check("here_image.data", Nrsc5Layout.HereImageData, 56);
    Check("emergency_alert.message", Nrsc5Layout.AlertMessage, 0);
    Check("emergency_alert.control_data", Nrsc5Layout.AlertControlData, 8);
    Check("emergency_alert.control_data_length", Nrsc5Layout.AlertControlDataLength, 16);
    Check("emergency_alert.category1", Nrsc5Layout.AlertCategory1, 20);
    Check("emergency_alert.category2", Nrsc5Layout.AlertCategory2, 24);
    Check("emergency_alert.location_format", Nrsc5Layout.AlertLocationFormat, 28);
    Check("emergency_alert.num_locations", Nrsc5Layout.AlertNumLocations, 32);
    Check("emergency_alert.locations", Nrsc5Layout.AlertLocations, 40);

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

// La ubicacion que sale en el panel es la del transmisor, que no es la ciudad de
// licencia: KQRS esta licenciada en Golden Valley y transmite desde Shoreview. Las
// respuestas de abajo son reales, recortadas a los campos que se leen.
static void VerifyGeocoding()
{
    // Sitio urbano: el Census lo resuelve como lugar incorporado.
    CheckCensus("""
        {"result":{"geographies":{
          "States":[{"BASENAME":"Minnesota","STUSAB":"MN","NAME":"Minnesota"}],
          "Incorporated Places":[{"BASENAME":"Shoreview","NAME":"Shoreview city"}],
          "Counties":[{"BASENAME":"Ramsey","NAME":"Ramsey County"}]}}}
        """, "Shoreview, MN");

    // Sitio rural: no hay lugar incorporado, pero si un census designated place. La
    // mayoria de las torres estan en campo abierto, asi que este es el caso normal.
    CheckCensus("""
        {"result":{"geographies":{
          "States":[{"BASENAME":"Colorado","STUSAB":"CO"}],
          "Census Designated Places":[{"BASENAME":"Meridian"}],
          "Counties":[{"BASENAME":"Douglas"}]}}}
        """, "Meridian, CO");

    // Ni lugar ni CDP: queda el condado, que sigue siendo mejor que unas coordenadas.
    CheckCensus("""
        {"result":{"geographies":{
          "States":[{"BASENAME":"Nevada","STUSAB":"NV"}],
          "Counties":[{"BASENAME":"Nye"}]}}}
        """, "Nye Co., NV");

    if (ReverseGeocoder.ParseCensus("""{"result":{"geographies":{}}}""") is not null)
        throw new InvalidOperationException("Una respuesta del Census sin capas debe devolver nada.");

    // Fuera de Estados Unidos manda OpenStreetMap. El estado sale del codigo ISO porque
    // "Baja California" entero no cabe en la celda junto al nombre de la ciudad.
    var tijuana = ReverseGeocoder.ParseNominatim("""
        {"address":{"city":"Tijuana","county":"Municipio de Tijuana","state":"Baja California",
         "ISO3166-2-lvl4":"MX-BCN","country_code":"mx"}}
        """) ?? throw new InvalidOperationException("No se reconocio la respuesta de Nominatim.");
    if (tijuana.Place != "Tijuana, BCN")
        throw new InvalidOperationException($"Nominatim: se esperaba 'Tijuana, BCN', se obtuvo '{tijuana.Place}'.");
    if (tijuana.CountryCode != "MX")
        throw new InvalidOperationException($"Nominatim debe informar el pais, se obtuvo '{tijuana.CountryCode}'.");

    // OSM archiva los nucleos bajo la etiqueta que use la administracion local.
    var village = ReverseGeocoder.ParseNominatim("""
        {"address":{"village":"Lostwithiel","state":"England","ISO3166-2-lvl4":"GB-ENG"}}
        """) ?? throw new InvalidOperationException("No se reconocio un nucleo etiquetado como village.");
    if (village.Place != "Lostwithiel, ENG")
        throw new InvalidOperationException($"Nominatim village: se obtuvo '{village.Place}'.");

    // 0,0 es una coordenada real en el Atlantico: una emisora que aun no ha mandado su
    // posicion no debe acabar geocodificada ahi.
    if (ReverseGeocoder.IsPlausible(0, 0))
        throw new InvalidOperationException("0,0 no debe considerarse una posicion valida.");
    if (!ReverseGeocoder.IsPlausible(45.0583f, -93.1244f))
        throw new InvalidOperationException("Una posicion real debe aceptarse.");

    Console.WriteLine("[OK] La geocodificacion inversa nombra el sitio del transmisor en EE.UU. y fuera.");

    static void CheckCensus(string body, string expected)
    {
        var site = ReverseGeocoder.ParseCensus(body)
            ?? throw new InvalidOperationException("No se reconocio la respuesta del Census.");
        if (site.Place != expected)
            throw new InvalidOperationException($"Census: se esperaba '{expected}', se obtuvo '{site.Place}'.");
    }
}

// Caso real: XHPQ-FM, recibida en Queretaro, emite unas coordenadas en San Marcos,
// California, a 2500 km, con codigo de pais US y un facility ID 22 que la FCC no lista.
// Su excitador nunca se configuro. El indicativo es lo unico que una emisora asi acierta,
// porque es lo que lee el oyente, asi que es el indicativo el que decide.
static void VerifySuspectSites()
{
    ExpectCountry("KQRS-FM", "US");
    ExpectCountry("WKST", "US");
    ExpectCountry("XHPQ-FM", "MX");
    ExpectCountry("CBLA-FM", "CA");
    ExpectCountry("", "");

    var honest = StationFacts.Empty with { Callsign = "KQRS-FM", SiteCity = "Shoreview", SiteState = "MN", SiteCountry = "US" };
    if (honest.SiteContradictsCallsign)
        throw new InvalidOperationException("Una emisora estadounidense en suelo estadounidense no es sospechosa.");

    var xhpq = StationFacts.Empty with { Callsign = "XHPQ-FM", SiteCity = "San Marcos", SiteState = "CA", SiteCountry = "US" };
    if (!xhpq.SiteContradictsCallsign)
        throw new InvalidOperationException("Un indicativo mexicano con emplazamiento en EE.UU. debe marcarse como dudoso.");

    // Sin uno de los dos paises no hay contradiccion que declarar, solo ignorancia.
    var pending = StationFacts.Empty with { Callsign = "XHPQ-FM" };
    if (pending.SiteContradictsCallsign)
        throw new InvalidOperationException("Sin sitio geocodificado no puede haber contradiccion.");
    var unknown = StationFacts.Empty with { SiteCountry = "US" };
    if (unknown.SiteContradictsCallsign)
        throw new InvalidOperationException("Sin indicativo no puede haber contradiccion.");

    Console.WriteLine("[OK] Un emplazamiento que contradice al indicativo se marca en vez de nombrarse.");

    static void ExpectCountry(string callsign, string expected)
    {
        var actual = StationFacts.CountryFromCallsign(callsign);
        if (actual != expected)
            throw new InvalidOperationException($"Pais de '{callsign}': se esperaba '{expected}', se obtuvo '{actual}'.");
    }
}

// El Artwork desfasado de la 3.3.5 tenia dos causas: el parametro del XHDR se ignoraba,
// y cuando la imagen correcta no estaba se mostraba la ultima vista, que era de otra
// cancion. Visto en vivo en XHTKR 103.7: la cuna "LA KE BUENA / ESCUCHAS" salia con la
// foto de un grupo de la cancion anterior.
static void VerifyTrackPresentation()
{
    const uint primary = Nrsc5Mime.PrimaryImage;
    // Semantica de nrsc5 src/output.c: param 0 + lot = mostrar, 1 = borrar, -1 = sin XHDR.
    Check(XhdrReference.FromNative(primary, 0, 1234) == new XhdrReference(ArtworkDirective.Show, 1234), "XHDR param 0 muestra el LOT");
    Check(XhdrReference.FromNative(primary, 1, -1).Directive == ArtworkDirective.Clear, "XHDR param 1 borra la imagen");
    Check(XhdrReference.FromNative(0, -1, -1) == XhdrReference.Absent, "Sin XHDR no hay referencia");
    Check(XhdrReference.FromNative(0x12345678, 0, 5) == XhdrReference.Absent, "Un MIME que no es imagen no es Artwork");

    var song = new TrackInfo(0, "Me Vas A Extranar", "Banda MS", "", new XhdrReference(ArtworkDirective.Show, 7), 10);
    var repeat = TrackInfo.Merge(song, new TrackInfo(0, "Me Vas A Extranar", "Banda MS", "", XhdrReference.Absent, 25));
    Check(repeat.Xhdr.Lot == 7 && repeat.LotStamp == 10, "Repetir la cancion sin XHDR conserva su imagen y su inicio");
    var liner = TrackInfo.Merge(song, new TrackInfo(0, "LA KE BUENA", "ESCUCHAS", "", XhdrReference.Absent, 25));
    Check(liner.Xhdr == XhdrReference.Absent && liner.LotStamp == 25, "Una pista nueva sin XHDR no hereda la imagen anterior");
    var flushed = TrackInfo.Merge(song, new TrackInfo(0, "Me Vas A Extranar", "Banda MS", "", new XhdrReference(ArtworkDirective.Clear, -1), 30));
    Check(flushed.Xhdr.Directive == ArtworkDirective.Clear, "Un borrado explicito gana aunque sea la misma cancion");
    var otherProgram = TrackInfo.Merge(song, song with { Program = 1, Xhdr = XhdrReference.Absent });
    Check(otherProgram.Xhdr == XhdrReference.Absent, "Otro subcanal nunca hereda");

    byte[] cover = [1], previousCover = [2], logo = [3];
    Check(ArtworkResolver.Resolve(new(ArtworkDirective.Show, 7), true, cover, previousCover, logo) == new ArtworkChoice(cover, false),
        "La imagen referenciada gana");
    Check(ArtworkResolver.Resolve(new(ArtworkDirective.Show, 7), true, null, previousCover, logo) == new ArtworkChoice(logo, true),
        "Mientras la imagen no llega se muestra el logo, nunca otra portada");
    Check(ArtworkResolver.Resolve(new(ArtworkDirective.Clear, -1), true, null, cover, logo) == new ArtworkChoice(logo, true),
        "En una emisora que vincula imagenes, un borrado muestra el logo");
    Check(ArtworkResolver.Resolve(XhdrReference.Absent, true, null, cover, logo) == new ArtworkChoice(logo, true),
        "En una emisora que vincula imagenes, una pista sin vinculo no tiene imagen");
    // XHTKR 103.7 en vivo: 730 ID3 seguidos con param 1 y ninguno con param 0, pero imagenes
    // en cada subcanal durante las canciones. Su param 1 no informa de nada.
    Check(ArtworkResolver.Resolve(new(ArtworkDirective.Clear, -1), false, null, cover, logo) == new ArtworkChoice(cover, false),
        "Si la emisora nunca vincula, la imagen llegada durante la pista es de la pista");
    Check(ArtworkResolver.Resolve(new(ArtworkDirective.Clear, -1), false, null, null, logo) == new ArtworkChoice(logo, true),
        "Si nada ha llegado durante la pista, el logo: nunca la portada anterior");
    Check(ArtworkResolver.Resolve(XhdrReference.Absent, false, null, cover, logo) == new ArtworkChoice(cover, false),
        "Sin XHDR nunca, la imagen llegada durante la pista es de la pista");
    Check(ArtworkResolver.Resolve(XhdrReference.Absent, false, null, null, null) == new ArtworkChoice(null, false),
        "Sin nada que mostrar, nada");

    var queue = new PresentationQueue<string>();
    queue.Enqueue(1000, 0, "a");
    queue.Enqueue(2000, 0, "b");
    queue.Enqueue(3000, 0, "c");
    Check(!queue.TryTakeDue(999, 1, 100, out _), "Nada se muestra antes de que su audio suene");
    Check(queue.TryTakeDue(2500, 1, 100, out var due) && due == "b" && queue.Count == 1,
        "Si vencen varias, se muestra la mas reciente");
    Check(queue.TryTakeDue(0, 100, 100, out due) && due == "c", "El limite de edad las libera si la reproduccion se detiene");

    Console.WriteLine("[OK] El Artwork sigue al XHDR y a la pista que suena, nunca a la ultima imagen vista.");

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

// La cola entre el hilo IQ de SDR# y el decodificador: acotada, con buferes reutilizados,
// y sin mezclar bloques aunque productor y consumidor corran a la vez.
static void VerifyIqQueue()
{
    using (var queue = new IqBlockQueue { MaxQueuedFloats = 1000 })
    {
        var data = Enumerable.Range(0, 400).Select(i => (float)i).ToArray();
        Check(queue.TryEnqueue(data, 912000, 100, 1), "Un bloque entra");
        Check(queue.TryDequeue(0, out var block), "Un bloque sale");
        Check(block.Floats == 400 && block.Rate == 912000 && block.Offset == 100 && block.Generation == 1,
            "El bloque conserva tasa, desplazamiento y generacion");
        Check(block.Buffer.AsSpan(0, 400).SequenceEqual(data), "El contenido llega intacto");
        var reused = block.Buffer;
        queue.Return(block.Buffer);
        Check(queue.TryEnqueue(data, 912000, 100, 2) && queue.TryDequeue(0, out block) && ReferenceEquals(block.Buffer, reused),
            "Un bufer devuelto se reutiliza en vez de asignar otro");
        queue.Return(block.Buffer);

        Check(queue.TryEnqueue(data, 912000, 100, 3) && queue.TryEnqueue(data, 912000, 100, 3), "Dos bloques caben");
        Check(!queue.TryEnqueue(data, 912000, 100, 3) && queue.Dropped == 1, "El tercero excede el limite y se descarta");
        queue.Clear();
        Check(queue.QueuedFloats == 0 && !queue.TryDequeue(0, out _), "Clear vacia la cola");
    }

    using (var queue = new IqBlockQueue { MaxQueuedFloats = 64 * 1024 * 1024 })
    {
        const int blocks = 3000;
        var producer = new Thread(() =>
        {
            var payload = new float[2048];
            for (var n = 0; n < blocks; n++)
            {
                Array.Fill(payload, n);
                queue.TryEnqueue(payload.AsSpan(0, 512 + n % 1500), 912000, 0, n);
            }
        });
        producer.Start();
        var last = -1;
        var taken = 0;
        while (producer.IsAlive || queue.QueuedFloats > 0)
        {
            if (!queue.TryDequeue(10, out var block)) continue;
            Check(block.Generation > last, "Los bloques salen en orden");
            last = block.Generation;
            Check(block.Floats == 512 + block.Generation % 1500, "Longitud del bloque");
            for (var i = 0; i < block.Floats; i++) Check(block.Buffer[i] == block.Generation, "Bloque sin mezclar con otro");
            queue.Return(block.Buffer);
            // Clear from this side, so it can land while the producer is mid-copy: the
            // race that used to drive the queued count negative.
            if (++taken % 200 == 0) queue.Clear();
        }
        producer.Join();
        Check(taken > 0 && queue.QueuedFloats == 0, "La cuenta vuelve a cero aunque Clear coincida con copias en curso");
    }

    Console.WriteLine("[OK] La cola IQ es acotada, reutiliza buferes y no mezcla bloques entre hilos.");

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

// Un mapa de trafico llega en nueve teselas a lo largo de un minuto o dos, cada una con
// sus propias esquinas. El mosaico se arma por geografia, no por el numero de pieza, asi
// que lo que hay que sostener es que los limites del conjunto son la union de los de las
// teselas y que una tesela sin limites reales no cuenta.
static void VerifyDataServices()
{
    var tiles = new List<HereTile>
    {
        new(1, [1], 42.0f, -88.0f, 41.5f, -87.5f),
        new(2, [2], 42.0f, -87.5f, 41.5f, -87.0f),
        new(3, [3], 41.5f, -88.0f, 41.0f, -87.5f)
    };
    var set = new HereImageSet(true, 3, new DateTime(2026, 9, 2, 14, 30, 0, DateTimeKind.Utc), tiles, 9);

    CheckFloat("norte", set.North, 42.0f);
    CheckFloat("sur", set.South, 41.0f);
    CheckFloat("oeste", set.West, -88.0f);
    CheckFloat("este", set.East, -87.0f);
    if (set.Received != 3) throw new InvalidOperationException($"Recibidas: se esperaban 3, hubo {set.Received}.");
    if (set.Complete) throw new InvalidOperationException("Tres de nueve teselas no es un mapa completo.");
    if (!new HereImageSet(true, 3, default, tiles, 3).Complete)
        throw new InvalidOperationException("Tres de tres teselas si es un mapa completo.");

    // Una emisora que aun no tiene mapa manda esquinas degeneradas; esas no se pintan.
    if (new HereTile(1, [0], 0, 0, 0, 0).HasBounds)
        throw new InvalidOperationException("Unas esquinas en cero no son limites validos.");
    if (new HereTile(1, [0], 41.0f, -88.0f, 42.0f, -87.0f).HasBounds)
        throw new InvalidOperationException("El norte por debajo del sur no son limites validos.");
    if (!tiles[0].HasBounds)
        throw new InvalidOperationException("Unos limites reales deben aceptarse.");

    // Una alerta Amber llega como Safety o Rescue; un huracan como Weather.
    var amber = new HdAlert("AMBER Alert: silver sedan, plate 8XYZ123", 4, 6, 1, [17031, 17043], DateTime.UtcNow);
    if (amber.Categories != "Safety · Rescue")
        throw new InvalidOperationException($"Categorias: se obtuvo '{amber.Categories}'.");
    if (amber.DescribeLocations() != "FIPS: 17031, 17043")
        throw new InvalidOperationException($"Localizaciones: se obtuvo '{amber.DescribeLocations()}'.");

    var storm = new HdAlert("Hurricane warning in effect", 3, 0, 0, [], DateTime.UtcNow);
    if (storm.Categories != "Weather")
        throw new InvalidOperationException($"Una categoria sola: se obtuvo '{storm.Categories}'.");
    if (storm.DescribeLocations().Length != 0)
        throw new InvalidOperationException("Sin codigos no debe describirse ninguna localizacion.");
    if (new HdAlert("x", 0, 0, 0, [], DateTime.UtcNow).Categories != "Uncategorised")
        throw new InvalidOperationException("Una alerta sin categoria debe decirlo, no quedarse vacia.");

    // Una emisora sin emplazamiento configurado manda 0,0. El panel lo enseñaba como
    // "0,000N 0,000E", que es el Atlantico disfrazado de respuesta.
    if (ReverseGeocoder.IsPlausible(0, 0))
        throw new InvalidOperationException("0,0 no puede contar como emplazamiento.");
    if (StationFacts.Empty.HasLocation)
        throw new InvalidOperationException("Sin evento de posicion no hay emplazamiento.");

    if (!HereData.Empty.IsEmpty) throw new InvalidOperationException("HereData.Empty debe estar vacio.");
    if (HereData.Empty with { Traffic = set } is { IsEmpty: true })
        throw new InvalidOperationException("Con un mapa de trafico ya no esta vacio.");

    Console.WriteLine("[OK] Los mapas de trafico y clima se ubican por geografia y las alertas se clasifican.");

    static void CheckFloat(string name, float actual, float expected)
    {
        if (Math.Abs(actual - expected) > 0.0001f)
            throw new InvalidOperationException($"Limite {name}: se esperaba {expected}, se obtuvo {actual}.");
    }
}

[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
static extern IntPtr AddDllDirectory(string path);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int OpenPipe(out IntPtr state);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void Close(IntPtr state);
