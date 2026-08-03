# Historial de cambios

## 0.1.0

- Primera versión independiente del plugin SDRSharp NRSC-5 para Windows x64.
- Captura IQ desde SDR# sin abrir el receptor por segunda vez.
- Compatibilidad inicial con Airspy HF+ Discovery y RTL-SDR.
- Selección de servicios HD1 a HD8.
- Audio HD con retorno automático a FM analógico.
- Datos de sincronización, MER, BER, estación, título y artista.
- Runtime nativo reducido a las seis DLL realmente necesarias.
- Runtime instalado fuera de `Plugins` para evitar que SDR# examine DLL nativas.
- Diagnóstico documentado para Smart App Control y el error `0x800711C7`.