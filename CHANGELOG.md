# Historial de cambios

## Sin publicar

- El logotipo de la emisora ya se muestra en el recuadro de Artwork. Llegaba por LOT pero
  ningún XHDR de ID3 lo referencia nunca, así que se guardaba en caché y jamás se pintaba.
- El Artwork de las canciones se resuelve por prioridad: la imagen que señala el XHDR actual,
  luego la última carátula recibida en ese subcanal y por último el logotipo de la emisora.
  Las emisoras que envían carátula sin XHDR correspondiente ya no quedan con el recuadro vacío.
- La caché de imágenes se indexa por puerto y LOT. Un identificador LOT solo es único dentro
  de su servicio, de modo que antes las imágenes de dos subcanales podían pisarse entre sí.
- Los offsets de `nrsc5_event_t` se derivan en `Nrsc5Layout` a partir de las reglas de
  alineación de C en lugar de escribirse a mano en cada lectura, y el smoke test los compara
  contra los valores del encabezado oficial para x64.
- Remuestreo con filtro anti-alias polifásico de sinc enventanado en Kaiser, cuyo largo escala
  con la razón de decimación. El rechazo fuera de banda pasa de -12 dB a -90 dB a 400 kHz con
  RTL-SDR a 2.4 MS/s; la interpolación lineal anterior plegaba el canal adyacente sobre las
  bandas laterales digitales.
- La mezcla del VFO ahora ocurre a la tasa de entrada, antes de decimar, que es el único orden
  correcto cuando hay un filtro anti-alias centrado en DC.
- Buffer de audio HD opcional y ajustable entre 0.1 y 10 segundos, con indicador de llenado.
  Al desactivarlo el audio HD arranca con el primer bloque decodificado para latencia mínima.
- La tolerancia ante pérdida de sincronía nunca es menor que el buffer configurado.
- Previous/Next recorre solo los subcanales que la emisora transmite realmente, descubiertos
  por la tabla SIG y por los eventos de servicio de audio. El panel lista los disponibles.
- `_hdAudioActive` se lee y escribe siempre dentro de `_audioGate`; antes lo tocaban sin
  sincronizar el hilo de audio de SDR# y el de la interfaz.
- Las fuentes se comparten desde `PanelFonts` en lugar de construirse por control, lo que
  filtraba un handle GDI por etiqueta cada vez que se recreaba el panel.
- Las guardas de regresión de `tests\Test-Project.ps1` que habían perdido las barras
  invertidas y nunca podían fallar quedaron corregidas y verificadas contra el código previo.

- Artwork de canción recibido por ID3/XHDR y LOT, centrado en el monitor.
- Artwork centrado en un marco 1:1, con ajuste Zoom y corrección de orientación EXIF.
- Interfaz profesional oscura con tarjetas técnicas ampliadas y legibles.
- Métricas separadas de potencia dBFS, dBm estimado/calibrable, SNR/MER, MER lateral, BER y bitrate HDC.
- Analizador FFT eliminado para reducir carga y priorizar la información técnica de la señal.
- Interfaz del plugin traducida completamente al inglés.
- Selector HD1-HD8 reemplazado por botones Previous/Next y trasladado encima de Signal Analysis.
- Scroll interno eliminado; el monitor distribuye Artwork cuadrado y métricas mediante filas proporcionales al redimensionar.
- Prebúfer HD inicial de aproximadamente 743 ms para absorber jitter y evitar alternancias frecuentes entre audio HD y analógico.
- Tolerancia de 1.5 segundos ante pérdidas breves de sincronía antes de vaciar el audio HD.
- Los cambios de Center Frequency ahora actualizan únicamente el mezclador digital y no reinician el decodificador.
- Un underflow breve conserva activa la ruta HD y ya no exige llenar nuevamente todo el prebúfer.
- La pérdida sostenida se confirma desde el último bloque PCM digital válido, no solo desde el evento inicial de sincronía.
- Reasignar la misma tasa IQ ya no reinicia el estado continuo del remuestreador.

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