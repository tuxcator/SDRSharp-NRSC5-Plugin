# SDRSharp NRSC-5 HD Radio Plugin

Plugin experimental e independiente para decodificar emisiones FM HD Radio/NRSC-5 dentro de SDR# sin abrir el Airspy HF+ o RTL-SDR una segunda vez.

## Documentación

- [Instalación completa en Windows](docs/INSTALACION.md)
- [Historial de cambios](CHANGELOG.md)
- Los paquetes listos para instalar se publican en [Releases](https://github.com/tuxcator/SDRSharp-NRSC5-Plugin/releases).

## Estado de la version 0.1.0

- Captura IQ crudo mediante la API oficial de plugins de SDR#.
- Centra digitalmente el VFO seleccionado y remuestrea a 744187.5 muestras complejas por segundo.
- Alimenta `libnrsc5` mediante `nrsc5_open_pipe` y `nrsc5_pipe_samples_cf32`.
- Muestra sincronizacion, MER, BER, nombre de estación, título, artista y álbum.
- Recibe y muestra el Artwork de la canción y el logotipo de la emisora mediante eventos
  ID3/XHDR y archivos LOT, con resolución por prioridad cuando la emisora no envía XHDR.
- Buffer de audio HD opcional y ajustable por segundos, con indicador de llenado.
- Monitor profesional con potencia dBFS, dBm estimado/calibrable, SNR/MER, MER lateral, BER y bitrate HDC real.
- Interfaz universal en inglés y selector intuitivo Previous/Next que recorre únicamente los subcanales que la emisora transmite, descubiertos por la tabla SIG.
- Diseño responsivo sin scroll interno: el Artwork cuadrado y las métricas ampliadas se ajustan proporcionalmente al tamaño del panel.
- Permite seleccionar HD1 a HD8.
- Reemplaza el audio analógico con PCM HD cuando existe sincronía y vuelve al analógico cuando se pierde.
- Estabiliza el audio HD con prebúfer inicial de aproximadamente 743 ms, tolerancia de 1.5 s ante pérdidas breves de sincronía y recentrado del espectro sin reiniciar el decodificador y recuperación inmediata tras un underflow breve.
- Diseñado inicialmente para SDR# x64 con Airspy HF+ Discovery a 768 kS/s. RTL-SDR debe usar al menos 1.024 MS/s.

## Compilar

Ejecute:

```bat
Compilar.cmd
```

El script descarga el SDK oficial de SDR# y un SDK .NET 9 local. Por defecto busca el runtime Win64 de nrsc5 en una carpeta hermana:

```text
..\FM-DX-Windows-Portable\runtime\nrsc5
```

También puede indicar otra ubicación:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -Nrsc5Runtime "C:\Ruta\A\nrsc5"
```

El paquete se genera en `dist\SDRSharp-NRSC5-Plugin`.

## Instalar

Cierre SDR#. Ejecute:

```bat
Instalar.cmd "C:\Ruta\A\SDRSharp"
```

También puede arrastrar la carpeta de SDR# sobre `Instalar.cmd`. El instalador copia:

- `SDRSharp.NRSC5.dll` en `Plugins\SDRSharp-NRSC5-Plugin`.
- Las seis DLL nativas necesarias en `NRSC5Runtime`, junto a los ejecutables de SDR# y fuera de `Plugins`.
- La entrada de `Plugin.xml` cuando SDR# utiliza ese archivo. Las versiones nuevas detectan el ensamblado desde su directorio de plugins.

## Uso recomendado

1. Seleccione Airspy HF+ o RTL-SDR en SDR#.
2. Use modo WFM y sintonice el centro exacto de la emisora, por ejemplo 103.7 MHz.
3. Configure ancho RF suficiente para toda la señal híbrida, aproximadamente 400 kHz.
4. Airspy HF+: use 768 kS/s. RTL-SDR: use 1.024 o 1.2 MS/s.
5. Abra **Digital Radio > NRSC-5 HD Radio**.
6. Seleccione HD1, HD2, etc. y active **Enable HD Radio decoding**.
7. Mantenga habilitado **Automatic HD audio** para el cambio automático analógico/HD.
8. Ajuste **Audio buffer** según su enlace: más segundos absorben mejor los cortes breves,
   menos segundos reducen la latencia. Desactive la casilla para latencia mínima.

## Smart App Control de Windows 11

El plugin y el runtime nativo se compilan localmente y no tienen una firma comercial. Si Smart App Control esta en modo **Activado**, Windows puede bloquear `SDRSharp.NRSC5.dll` con el evento 3077 de Integridad de codigo. No existe una excepcion por archivo para Smart App Control: use una compilacion firmada con un certificado de una autoridad admitida o decida desde Seguridad de Windows si desea desactivar esa proteccion. Desactivarla reduce la seguridad y Microsoft indica que no puede volver a activarse sin restablecer o reinstalar Windows.

## Limitaciones conocidas

- El runtime nativo incluido es Win64; SDR# x86 requiere compilar nrsc5 y sus dependencias para 32 bits.
- La recepción depende de que la emisora transmita NRSC-5 y de que ambas bandas laterales digitales tengan MER suficiente.
- El plugin no transmite, cifra ni evita controles de acceso. Solo decodifica emisiones recibidas legalmente.

## Licencias

El código del plugin se distribuye bajo GPL-3.0-or-later para ser compatible con nrsc5. Los ensamblados del SDK de SDR# se descargan desde Airspy y no deben publicarse dentro del repositorio.
