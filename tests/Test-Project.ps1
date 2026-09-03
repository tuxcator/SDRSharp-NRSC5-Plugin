param([string]$Distribution = '')

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$required = @(
    'README.md',
    'LICENSE',
    'THIRD_PARTY_NOTICES.md',
    'Plugin.xml.fragment',
    'src\SDRSharp.NRSC5\SDRSharp.NRSC5.csproj',
    'src\SDRSharp.NRSC5\Nrsc5Plugin.cs',
    'src\SDRSharp.NRSC5\Nrsc5Engine.cs',
    'src\SDRSharp.NRSC5\Nrsc5Native.cs',
    'src\SDRSharp.NRSC5\Nrsc5Panel.cs',
    'src\SDRSharp.NRSC5\PolyphaseResampler.cs',
    'src\SDRSharp.NRSC5\PluginInfo.cs',
    'src\SDRSharp.NRSC5\StationFacts.cs',
    'src\SDRSharp.NRSC5\FccStationDirectory.cs',
    'src\SDRSharp.NRSC5\LookupCache.cs',
    'src\SDRSharp.NRSC5\ReverseGeocoder.cs',
    'src\SDRSharp.NRSC5\HereImages.cs',
    'src\SDRSharp.NRSC5\HereMapsForm.cs',
    'scripts\Get-Dependencies.ps1',
    'scripts\Build.ps1',
    'scripts\Install.ps1'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative))) { throw "Falta $relative" }
}

$source = (Get-ChildItem -LiteralPath (Join-Path $root 'src\SDRSharp.NRSC5') -Filter '*.cs' -File | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName }) -join [Environment]::NewLine
foreach ($token in 'ProcessorType.RawIQ','Nrsc5Native.NativeFmSampleRate','nrsc5_pipe_samples_cf32','ProcessAudio','SelectedProgram','Nrsc5Event.Lot','ReceiveLot','BitrateKbps','SignalProbeSamples','SyncLossGraceMs','ConfirmSyncLoss','_lastDigitalTicks','remainingMs','_lastFrequency','LayoutArtworkSquare','DecodeArtwork','DbmCalibrationOffset','BuildChannelSelector','PREVIOUS','NEXT  ▶','Synchronized HD','AutoScroll = false','RowStyle(SizeType.Percent','Dock = DockStyle.Fill',
    'PolyphaseResampler','MixToBaseband','BufferSeconds','BufferingEnabled','EnsureCapacityFrames',
    'Nrsc5Layout','Nrsc5Mime.StationLogo','RefreshArtwork','_stationLogoByProgram','_latestArtByProgram',
    'ReceiveSig','MarkProgramAvailable','StepProgram','ProgramMask','PanelFonts',
    'Nrsc5Event.StationSlogan','Nrsc5Event.StationId','Nrsc5Event.StationLocation',
    'StationFactsChanged','ResetStationFacts','BeginFccLookup','PiCodeFor','InfoCard',
    'PI CODE','LOCATION','POWER',
    'ReverseGeocoder','ParseCensus','ParseNominatim','BeginSiteLookup','SitePlace','IsPlausible',
    'SiteContradictsCallsign','CountryFromCallsign','EffectiveCountry',
    'Nrsc5Event.HereImage','Nrsc5Event.EmergencyAlert','ReceiveHereImage','ReceiveEmergencyAlert',
    'HereMapsForm','HereDataChanged','ResetHereData','Traffic map','Weather map') {
    if ($source -notmatch [regex]::Escape($token)) { throw "Falta integracion: $token" }
}

# Las paginas de radio-locator.com y fmlist.org que traen eslogan, potencia y ubicacion
# estan prohibidas para clientes automaticos en sus robots.txt, asi que el plugin no las
# consulta: los datos salen de SIS y de la base publica de la FCC.
foreach ($forbidden in 'radio-locator','fmlist.org','cgi-bin/pat') {
    if ($source -match [regex]::Escape($forbidden)) {
        throw "El plugin no debe consultar ${forbidden}: su robots.txt lo prohibe."
    }
}
# Ninguna consulta de red puede bloquear el hilo del decodificador ni el de la interfaz.
if ($source -match '\.LookupAsync\([^)]*\)\.(Result|GetAwaiter)') {
    throw 'Las consultas de red deben esperarse con await, nunca bloqueando el hilo.'
}

# La cache en disco cambio de forma entre la 3.3 y la 3.3.1. Sin version en el fichero,
# la cache vieja se leia como "sin registro" y dejaba los campos vacios 30 dias.
$cache = Get-Content -Raw -LiteralPath (Join-Path $root 'src\SDRSharp.NRSC5\LookupCache.cs')
if ($cache -notmatch 'FormatVersion') {
    throw 'La cache en disco debe llevar version de formato para descartar la de builds anteriores.'
}

$panelSource = Get-Content -Raw -LiteralPath (Join-Path $root 'src\SDRSharp.NRSC5\Nrsc5Panel.cs')
# El mosaico se arma por las esquinas de cada tesela, no por su numero de pieza: el
# orden en que una emisora numera las nueve partes no esta documentado en ningun sitio.
$maps = Get-Content -Raw -LiteralPath (Join-Path $root 'src\SDRSharp.NRSC5\HereMapsForm.cs')
if ($maps -notmatch 'perDegreeX' -or $maps -notmatch 'perDegreeY') {
    throw 'Las teselas del mapa deben ubicarse por latitud y longitud, no por indice.'
}
# Sin propietario, la ventana de mapas se va detras de SDR# en cuanto este toma el foco.
if ($panelSource -notmatch '_maps\.Owner\s*=') {
    throw 'La ventana de mapas debe pertenecer a la ventana de SDR# para quedarse al frente.'
}
# Los mapas necesitan cientos de pixeles: no pueden vivir dentro del panel acoplado.
if ($panelSource -match 'PictureBox\s+_traffic|PictureBox\s+_weather') {
    throw 'Los mapas van en su propia ventana, no incrustados en el panel.'
}

# Nominatim exige un User-Agent identificable y como mucho una peticion por segundo.
$geo = Get-Content -Raw -LiteralPath (Join-Path $root 'src\SDRSharp.NRSC5\ReverseGeocoder.cs')
foreach ($required in 'UserAgent','NominatimInterval') {
    if ($geo -notmatch [regex]::Escape($required)) {
        throw "El geocodificador debe respetar la politica de Nominatim: falta $required."
    }
}

# La regla es sobre el panel acoplado, no sobre cualquier ventana: la lista de alertas
# vive en un marco redimensionable y ahi el scroll es lo correcto.
if ($panelSource -match 'AutoScroll\s*=\s*true' -or $panelSource -match 'MinimumSize\s*=\s*new Size\(370, 700\)') {
    throw 'El panel no debe forzar scroll ni un tamaño rígido.'
}

# El underflow breve debe conservar armada la ruta HD.
if ($source -match 'else\s+if\s*\(available\s*<\s*required\)\s*\{\s*_hdAudioActive\s*=\s*false') {
    throw 'Un underflow breve no debe obligar a llenar otra vez todo el prebuffer HD.'
}
# Recentrar el espectro solo mueve el mezclador digital.
if ($source -match 'PropertyName\s+is\s+nameof\(ISharpControl\.Frequency\)\s+or\s+nameof\(ISharpControl\.CenterFrequency\)') {
    throw 'El recentrado del espectro no debe reiniciar el decodificador NRSC-5.'
}
if ($source -match 'ComputeSpectrum|SpectrumDisplay|NRSC-5 SPECTRUM') {
    throw 'El analizador FFT fue retirado para priorizar las metricas tecnicas y el Artwork.'
}
if ($source -match 'ComboBox\s+_program') {
    throw 'El selector desplegable de subcanales no debe regresar; use Previous/Next.'
}

# Los offsets del struct de eventos se derivan en Nrsc5Layout, no a mano en cada lectura.
if ($source -match 'IntPtr\.Size\s*==\s*8\s*\?\s*8\s*:\s*4') {
    throw 'Los offsets del evento nativo deben salir de Nrsc5Layout, no escribirse a mano.'
}

# El remuestreo debe filtrar antes de decimar.
if ($source -notmatch 'BuildBank' -or $source -notmatch 'KaiserBeta') {
    throw 'El remuestreador debe conservar el filtro anti-alias Kaiser.'
}

# _hdAudioActive se toca desde el hilo de audio y el de UI: siempre bajo _audioGate.
$processAudio = [regex]::Match($source, 'public unsafe void ProcessAudio[\s\S]*?\r?\n    \}').Value
if (-not $processAudio) { throw 'No se pudo aislar ProcessAudio para revisarlo.' }
if ($processAudio -match '_hdAudioActive[\s\S]*lock \(_audioGate\)') {
    throw '_hdAudioActive no debe leerse ni escribirse fuera de _audioGate.'
}
if ($processAudio -notmatch 'lock \(_audioGate\)[\s\S]*_hdAudioActive') {
    throw '_hdAudioActive debe manipularse dentro de _audioGate.'
}

# Las fuentes viven en PanelFonts; crearlas en cada control filtraba un handle GDI.
$panel = Get-Content -Raw -LiteralPath (Join-Path $root 'src\SDRSharp.NRSC5\Nrsc5Panel.cs')
if ($panel -match 'Font\s*=\s*new Font\(') {
    throw 'Las fuentes deben reutilizarse desde PanelFonts en lugar de crearse por control.'
}

if ($Distribution) {
    $nativeDlls = @(
        'libnrsc5.dll',
        'libfftw3f-3.dll',
        'libgcc_s_seh-1.dll',
        'libwinpthread-1.dll',
        'librtlsdr.dll',
        'libusb-1.0.dll'
    )
    foreach ($relative in 'SDRSharp.NRSC5.dll','README.md','LICENSE') {
        if (-not (Test-Path -LiteralPath (Join-Path $Distribution $relative))) { throw "Distribucion incompleta: $relative" }
    }
    foreach ($name in $nativeDlls) {
        $relative = Join-Path 'NRSC5Runtime' $name
        if (-not (Test-Path -LiteralPath (Join-Path $Distribution $relative))) { throw "Distribucion incompleta: $relative" }
    }
    $unexpected = Get-ChildItem -LiteralPath (Join-Path $Distribution 'NRSC5Runtime') -Filter '*.dll' -File |
        Where-Object { $_.Name -notin $nativeDlls }
    if ($unexpected) {
        throw "La distribucion contiene DLL nativas ajenas a NRSC5: $($unexpected.Name -join ', ')"
    }
}

Write-Host 'Todas las pruebas del proyecto completadas correctamente.' -ForegroundColor Green
