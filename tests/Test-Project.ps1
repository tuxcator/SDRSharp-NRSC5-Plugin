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
    'scripts\Get-Dependencies.ps1',
    'scripts\Build.ps1',
    'scripts\Install.ps1'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative))) { throw "Falta $relative" }
}

$source = (Get-ChildItem -LiteralPath (Join-Path $root 'src\SDRSharp.NRSC5') -Filter '*.cs' -File | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName }) -join [Environment]::NewLine
foreach ($token in 'ProcessorType.RawIQ','Nrsc5Native.NativeFmSampleRate','nrsc5_pipe_samples_cf32','ProcessAudio','SelectedProgram') {
    if ($source -notmatch [regex]::Escape($token)) { throw "Falta integracion: $token" }
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
