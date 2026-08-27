# Instalación en Windows

Esta guía instala el plugin **SDRSharp NRSC-5 HD Radio** en una copia independiente de SDR# x64.

## 1. Requisitos

- Windows 10 u 11 de 64 bits.
- SDR# x64 compatible con plugins .NET 9.
- Airspy HF+ Discovery o RTL-SDR conectado por USB.
- Controlador correspondiente al receptor. Para RTL-SDR normalmente se utiliza WinUSB mediante Zadig.
- Una emisora FM que transmita HD Radio/NRSC-5.

> El paquete es exclusivamente x64. No funciona dentro de `sdrsharp-x86`.

## 2. Smart App Control de Windows 11

El plugin y `libnrsc5` son compilaciones comunitarias sin firma comercial. Cuando **Control inteligente de aplicaciones (Smart App Control)** está activado, Windows puede bloquear `SDRSharp.NRSC5.dll` y SDR# no mostrará la opción NRSC-5.

El bloqueo aparece en `PluginError.log` con el código `0x800711C7` y en Integridad de código como evento 3077.

Opciones compatibles:

1. Usar binarios firmados con un certificado de una autoridad reconocida por Microsoft.
2. Desactivar manualmente Smart App Control desde **Seguridad de Windows > Control de aplicaciones y navegador > Configuración de Control inteligente de aplicaciones**.

Advertencia: desactivar Smart App Control reduce la protección del equipo. Microsoft indica que no puede volver a activarse sin restablecer o reinstalar Windows. No existe una excepción individual por archivo y un certificado local autofirmado no satisface esta política.

Reinicie Windows después de cambiar la configuración.

## 3. Descargar

1. Abra la sección **Releases** del repositorio.
2. Descargue `SDRSharp-NRSC5-Plugin-v0.1.0-win-x64.zip`.
3. Extraiga el ZIP completamente. No ejecute el instalador desde dentro del archivo comprimido.

## 4. Instalar automáticamente

Cierre SDR# y ejecute desde PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-Package.ps1 -SdrSharpDir "C:\Ruta\A\sdrsharp-x64"
```

También puede arrastrar la carpeta de SDR# x64 sobre `Instalar.cmd`.

El instalador crea esta estructura:

```text
sdrsharp-x64\
├── NRSC5Runtime\
│   ├── libnrsc5.dll
│   ├── libfftw3f-3.dll
│   ├── libgcc_s_seh-1.dll
│   ├── librtlsdr.dll
│   ├── libusb-1.0.dll
│   └── libwinpthread-1.dll
└── Plugins\
    └── SDRSharp-NRSC5-Plugin\
        └── SDRSharp.NRSC5.dll
```

Las DLL nativas deben permanecer fuera de `Plugins`. SDR# examina recursivamente esa carpeta y puede intentar abrirlas como ensamblados administrados.

## 5. Verificar el plugin

1. Ejecute `SDRSharp.dotnet9.exe`.
2. Busque **NRSC-5 HD Radio** dentro del panel **Digital Radio**.
3. Si la opción no aparece, cierre SDR# y revise `PluginError.log` en la carpeta principal de SDR#.

## 6. Configurar el receptor

### Airspy HF+ Discovery

- Fuente: Airspy HF+.
- Tasa de muestreo recomendada: `768 kS/s`.
- Modo: `WFM`.
- Centre exactamente la frecuencia, por ejemplo `103.700 MHz`.
- Use aproximadamente `400 kHz` de ancho para incluir las bandas laterales digitales NRSC-5.

### RTL-SDR

- Fuente: RTL-SDR USB.
- Controlador: WinUSB.
- Tasa de muestreo: `1.024 MS/s` o `1.2 MS/s`.
- Modo: `WFM`.
- Evite una ganancia excesiva que pueda saturar el receptor.

## 7. Decodificar HD Radio

1. Sintonice el centro exacto de la emisora FM.
2. Abra el panel NRSC-5.
3. Seleccione HD1, HD2, HD3 u otro subcanal disponible.
4. Active **Decodificar HD Radio**.
5. Mantenga habilitado **Usar audio HD al sincronizar** para cambiar automáticamente entre audio analógico y digital.

La sincronización depende de recibir correctamente ambas bandas laterales digitales. Una señal analógica fuerte no garantiza suficiente MER para HD Radio.

## Monitor profesional y Artwork

El panel muestra de forma separada:

- Potencia RF medida en dBFS sobre el IQ recibido.
- Nivel dBm estimado. Ajuste **dBm** usando una señal de referencia conocida; este valor no sustituye un medidor RF calibrado.
- SNR derivado del promedio MER de ambas bandas laterales.
- MER inferior/superior y BER.
- Bitrate real de los paquetes HDC del subcanal seleccionado.
- Información continua de tasa IQ, desplazamiento VFO y pico dBFS sin carga FFT adicional.
- Estado del buffer HD y lista de los subcanales que la emisora transmite realmente.
- Artwork transmitido por la emisora mediante ID3/XHDR y LOT, centrado en un marco cuadrado y corregido según su orientación EXIF. Si la emisora no envía carátula de canción se muestra su logotipo, y si no envía ninguna imagen aparece el marcador HD Artwork.

### Buffer de audio HD

La casilla **Buffer** activa un prebúfer ajustable entre 0.1 y 10 segundos. Más segundos absorben
mejor los desvanecimientos breves; menos segundos reducen la latencia frente al audio analógico.
Al desactivar la casilla el audio HD arranca con el primer bloque decodificado. La línea de estado
indica el llenado actual contra el objetivo y se pone en color cuando ya alcanzó el umbral.

La tolerancia ante pérdida de sincronía nunca es menor que el buffer configurado, de modo que subir
el buffer también hace más paciente el retorno automático al audio analógico.

### Selección de subcanales

**Previous** y **Next** recorren únicamente los subcanales anunciados por la tabla SIG de la emisora
y por los descriptores de servicio de audio. Mientras no llegue esa información el selector recorre
HD1 a HD8 de forma cíclica para no dejarlo atrapado en HD1.

## 8. Solución de problemas

| Síntoma | Causa probable | Solución |
|---|---|---|
| El panel NRSC-5 no aparece | Smart App Control bloqueó la DLL | Revise `PluginError.log`, el evento 3077 y la sección 2 |
| Error `0x800711C7` | Política de Integridad de código | Use binarios firmados o cambie Smart App Control conscientemente |
| `libnghttp3-9.dll` no está diseñada para Windows | Paquete antiguo con dependencias ajenas | Elimine la instalación antigua e instale la versión actual |
| Error de arquitectura | SDR# x86 con runtime x64 | Instale SDR# x64 en otra carpeta |
| No sincroniza HD | Frecuencia, ancho, muestra o MER insuficiente | Centre la frecuencia, use 400 kHz y revise antena/ganancia |
| Se oye analógico pero no HD | La emisora no transmite NRSC-5 o sus laterales son débiles | Pruebe una emisora HD confirmada y observe MER/BER |

## 9. Desinstalar

Cierre SDR# y elimine únicamente:

```text
C:\Ruta\A\sdrsharp-x64\Plugins\SDRSharp-NRSC5-Plugin
C:\Ruta\A\sdrsharp-x64\NRSC5Runtime
```

No elimine otras carpetas de plugins ni archivos principales de SDR#.

## 10. Compilar desde el código fuente

Clone el repositorio y ejecute:

```powershell
.\Compilar.cmd
```

El proceso descarga el SDK oficial de plugins de SDR#, prepara un SDK .NET 9 local, importa el runtime Win64 de NRSC5, compila, ejecuta las pruebas y genera el ZIP dentro de `dist`.