# Historial de cambios

*[English version](CHANGELOG.md)*

## Sin publicar — compilación de desarrollo 3.3.4

- **Mapas de tráfico y clima, y alertas de emergencia, en una ventana propia.** Dos
  botones nuevos al pie del panel la abren. Un mapa necesita cientos de píxeles en ambas
  direcciones y el panel va acoplado en una columna de unos 250, así que se le da un
  marco que el oyente puede dimensionar y colocar donde quiera; la ventana no es modal y
  cerrarla no toca el decodificador.
- Los mapas vienen del servicio de datos HERE, que libnrsc5 ya reportaba por
  `NRSC5_EVENT_HERE_IMAGE` y el plugin descartaba. Un mapa de tráfico llega en nueve
  teselas a lo largo de un minuto o dos; el de clima llega entero.
- **El mosaico se arma con las esquinas de cada tesela, no con su número de pieza.** Cada
  tesela lleva la latitud y la longitud del terreno que cubre, así que el mapa se coloca
  geográficamente y no depende de adivinar en qué orden numera una emisora las nueve
  partes, orden que no está documentado en ningún sitio.
- Las alertas de emergencia llegan por `NRSC5_EVENT_EMERGENCY_ALERT`, también descartado
  hasta ahora. Las alertas Amber entran como Safety o Rescue, los huracanes y tormentas
  como Weather, los sismos como Geophysical; el plugin muestra lo que llega en vez de
  filtrar por categoría. Los condados o códigos postales que cubre una alerta se muestran
  como los códigos SAME, FIPS o ZIP crudos que emite la emisora, con el formato indicado.
- Solo las emisoras que llevan el servicio de datos HERE emiten estos mapas, lo que en la
  práctica son las estadounidenses grandes. En una que no lo lleve, la ventana se abre
  vacía y lo dice.

## Sin publicar — compilación de desarrollo 3.3.3

- **Un emplazamiento que contradice al indicativo se marca en vez de nombrarse.** La 3.3.1
  daba por hecho el pueblo al que geocodificaban las coordenadas. Sintonizar XHPQ-FM desde
  un receptor en Querétaro dejó claro lo que eso cuesta: la emisora transmite coordenadas
  en San Marcos, California, a unos 2500 km, junto con un código de país US y un
  identificador de instalación de la FCC, el 22, que no figura en ninguna base de la FCC.
  Su excitador nunca se configuró. El panel nombraba con aplomo un pueblo desde el que la
  emisora demostrablemente no transmite.
- El indicativo decide ahora a qué país pertenece una emisora, por encima del código de
  país de sus tramas SIS. De todo el bloque de identidad es el único campo que una emisora
  acierta, porque es lo que lee el oyente; el resto se configura una vez en la instalación
  y suele quedarse con lo que traía el excitador. `K` y `W` son Estados Unidos, `X` México
  y `C` Canadá.
- Cuando el sitio geocodificado cae en un país distinto al del indicativo, la celda muestra
  las coordenadas crudas con un interrogante y el tooltip explica qué falla. A una emisora
  cuyo indicativo no es estadounidense ya no se le consulta la FCC.

## Sin publicar — compilación de desarrollo 3.3.1

- **LOCATION ahora nombra la población donde está el transmisor**, en vez de un par de
  coordenadas. Las coordenadas que llegan por SIS se geocodifican con el servicio del US
  Census, que es de dominio público y no pide clave, con Nominatim de OpenStreetMap como
  respaldo y para fuera de Estados Unidos. Se respeta la política de Nominatim: User-Agent
  identificable, como mucho una petición por segundo, y resultados en caché 180 días.
- La población del transmisor **no** suele coincidir con la ciudad de licencia, así que
  ahora se muestran las dos: la del transmisor en la celda y la de licencia a su lado en
  el tooltip. KQRS está licenciada en Golden Valley y transmite desde Shoreview, a quince
  kilómetros, y es la segunda la que dice hacia dónde apuntar una antena.
- Google Maps se consideró y se descartó: su API de geocodificación exige clave y cuenta
  de facturación, algo que no puede viajar dentro de un plugin público.
- La caché en memoria y disco de la consulta a la FCC y del geocodificador es ahora el
  mismo código, y el fichero que escribe lleva versión de formato. Sin ella, la caché
  escrita por la 3.3 se releía como «el servicio no tiene registro» y habría dejado los
  campos de licencia vacíos durante los treinta días de su vigencia tras actualizar.

## Sin publicar — compilación de desarrollo 3.3

- Nueva fila de **información de la emisora** bajo los datos de la canción, con el **eslogan**,
  el **código PI**, la **ubicación** y la **potencia**.
- El eslogan, el indicativo, el identificador de instalación de la FCC y el emplazamiento del
  transmisor salen ahora de las tramas SIS que la propia emisora transmite. libnrsc5 ya entregaba
  esos eventos; el plugin los descartaba todos salvo el nombre de la emisora.
- La ciudad de licencia, la potencia radiada (ERP) y la altura sobre el terreno medio (HAAT)
  vienen del servicio público FM Query de la FCC, consultado con el identificador de instalación
  que la emisora transmite. Las respuestas se guardan en memoria y en disco durante 30 días,
  porque quien recorre la banda vuelve constantemente a las mismas emisoras y una licencia cambia
  a lo sumo unas pocas veces al año. La consulta corre fuera del hilo del decodificador y, si
  falla, solo cuesta esos tres campos: todo lo que llega por SIS ya está en pantalla. Las
  emisoras licenciadas fuera de Estados Unidos no se consultan.
- El código PI se deriva del indicativo con la regla RBDS, que es lo que mostraría un receptor
  RDS: HD Radio no lo transmite y ninguna base de datos lo publica. Los indicativos de tres
  letras son una tabla de excepciones del estándar en vez de una fórmula, y los códigos PI de
  Canadá y México se asignan en lugar de derivarse, así que esos se dejan vacíos en vez de
  inventarlos.
- El concesionario, la clase, el HAAT y las coordenadas del transmisor están en el tooltip, de
  modo que los campos nuevos le cuestan al Artwork una sola fila de 37 px.
- `radio-locator.com` y `fmlist.org` **no** se consultan, a propósito. Las páginas con eslogan,
  potencia y ubicación están marcadas como `Disallow` para todo agente en sus `robots.txt`
  (`/info` y `/cgi-bin/pat` en radio-locator, `/export/` y `/demoapi/` en fmlist), y fmlist no
  tiene API sin autenticación. Una prueba del proyecto falla si cualquiera de los dos dominios
  reaparece en el código.

- Cada cambio de emisora entra en HD1. La lista de subcanales pertenece a la emisora, así que
  conservar un HD2 o HD3 al pasar a una emisora que solo transmite HD1 dejaba al decodificador
  esperando un audio que nunca llegaba mientras sonaba el analógico. Un ajuste fino de la misma
  emisora, por debajo de 50 kHz, respeta el subcanal en curso, y el selector no se mueve solo en
  ningún otro caso.
- El cambio de subcanal ya no deja pasar la emisión analógica. El nivel baja con una rampa de
  20 ms, el silencio cubre el rellenado del búfer y el nuevo subcanal entra con otra rampa. La
  retención caduca a los 3 segundos o al búfer más 2 segundos, lo que sea mayor, para que un
  subcanal que nunca entregue audio no deje al oyente en silencio permanente.
- Nuevo botón **Surround** que ensancha la imagen estéreo del audio HD: separa medio y lateral,
  refuerza el lateral con una copia retrasada 14 ms y mantiene los graves por debajo de 250 Hz al
  centro para que la mezcla no se vacíe ni se cancele en un altavoz mono. Solo actúa sobre el
  audio del códec HDC, nunca sobre la ruta analógica de SDR#, y no altera el nivel del centro, de
  modo que se percibe como anchura y no como volumen.
- La cabecera del panel muestra la versión de desarrollo en curso.
- La documentación se publica en inglés y español: el README en inglés es la portada del
  repositorio, `docs/INSTALLATION.md` refleja `docs/INSTALACION.md`, y ambas guías usan ya los
  nombres reales de los controles, indican el requisito de 744.1875 kS/s y documentan los ajustes
  de ganancia del Airspy HF+.

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