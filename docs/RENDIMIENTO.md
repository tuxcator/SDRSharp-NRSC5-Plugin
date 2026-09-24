# Rendimiento

## Dev 4.0.0

La 4.0.0 cambia **dónde** se decodifica, no solo lo rápido que corre cada etapa.

### La decodificación sale del hilo de SDR#

En modo *pipe*, `libnrsc5` no tiene hilo propio: `nrsc5_pipe_samples_cf32` ejecuta la
adquisición, la sincronía, el Viterbi y el códec HDC dentro de la llamada (verificado en
`src/nrsc5.c` e `src/input.c`: el hilo de trabajo de nrsc5 solo existe para dispositivos
RTL-SDR y rtl_tcp). Hasta la 3.3.5 esa llamada se hacía desde el callback IQ de SDR#, así
que toda la decodificación competía con el propio procesamiento de SDR#.

Coste de `libnrsc5` medido aislado, alimentado con ruido a 744 187,5 S/s (la búsqueda de
sincronía corre sin parar): **9,29 ms por cada bloque de 36 ms de señal, 25,6 % de un
núcleo**.

En la 4.0.0 el callback IQ solo copia el bloque a una cola acotada de búferes reutilizados
y vuelve; el mezclador, el remuestreador y `libnrsc5` corren en el hilo `NRSC-5 decoder`.

Medición en vivo con SDR# 1.0.0.1921, Airspy HF+ Discovery a 912 ksps, XHTKR 103,7 MHz
con HD1/HD2/HD3, CPU por hilo durante 40 s (Windows, 8 núcleos). El hilo IQ de SDR# se
identificó por su id de sistema, que la propia 4.0.0 registra en la traza:

| Hilo | Dev 3.3.5 | Dev 4.0.0 |
|---|---:|---:|
| Hilo IQ de SDR# (el que llama al plugin) | 16,3 % | 5,2 % |
| Parte del plugin en ese hilo, medida por dentro | toda la decodificación | 0,3 % |
| `NRSC-5 decoder` | — | 12,1 % |
| Bloques IQ descartados | — | 0 |
| Proceso SDR# completo | 77,8 % | 74,9 % |

Las cifras cuadran entre sí: 5,2 % de SDR# más 12,1 % del decodificador suman lo que en la
3.3.5 cargaba un solo hilo. **El hilo IQ de SDR# queda unas tres veces más ligero y lo que
el plugin le cuesta baja unas cuarenta veces.** El total del proceso no baja: el trabajo no
desaparece, se muda a otro núcleo. La recepción variaba por el viento sobre la antena, así
que MER, BER y tiempo de enganche no se comparan entre versiones.

El panel muestra estas cargas, medidas por el plugin, en el tooltip de la línea IQ.

### Remuestreador

Banco de coeficientes sin duplicar (se reparten sobre el par I/Q en registros con un
`vpermps` cada cuatro taps), dos acumuladores, FMA y reducción horizontal con dos sumas y
una permutación. El cuello de botella era la memoria: cada muestra de salida salta a una
fase distinta del banco de 512 fases, y la 3.3.5 duplicaba cada coeficiente (164 KB de
banco a 40 taps, 655 KB a 160).

Tres generaciones en el mismo proceso (`tests\DspChecks`, `--benchmark`), bloques de
32 768 muestras complejas, mediana de cinco lotes, con SDR# en marcha en la misma máquina:

| Entrada | 3.3.4 | 3.3.5 | 4.0.0 | vs 3.3.5 | Carga 3.3.5 → 4.0.0 |
|---|---:|---:|---:|---:|---:|
| 744,2 kS/s | 3,468 ms | 1,847 ms | 1,475 ms | 1,25× | 4,20 % → 3,35 % |
| 768 kS/s | 4,730 ms | 1,752 ms | 1,599 ms | 1,10× | 4,11 % → 3,75 % |
| 912 kS/s | 4,539 ms | 1,835 ms | 1,454 ms | 1,26× | 5,11 % → 4,05 % |
| 1 024 kS/s | 3,849 ms | 1,508 ms | 1,423 ms | 1,06× | 4,71 % → 4,45 % |
| 1 200 kS/s | 3,377 ms | 1,460 ms | 1,199 ms | 1,22× | 5,35 % → 4,39 % |
| 2 400 kS/s | 4,045 ms | 1,196 ms | 0,680 ms | 1,76× | 8,76 % → 4,98 % |
| 4 800 kS/s | 2,930 ms | 0,847 ms | 0,483 ms | 1,75× | 12,40 % → 7,08 % |

La columna de carga es la parte de un núcleo necesaria para seguir el flujo en tiempo
real; importa más que los milisegundos por bloque, porque un bloque a 4,8 MS/s cubre cinco
veces menos señal que a 912 kS/s. La ganancia se concentra en 2,4 MS/s y más, la tasa
habitual de un RTL-SDR, donde el banco es mayor. Error frente a la 3.3.5: 3,6·10⁻⁷ como
máximo; frente a la 3.3.4, el mismo 7,2·10⁻⁷ de antes.

Probado y descartado: mover el estado del bucle a variables locales no cambió nada
medible. No hay ruta AVX-512: esta máquina no la tiene y no se publica código que no se
ha podido ejecutar.

### Lo que no se ha tocado

`libnrsc5`, que es con diferencia la etapa más cara, se usa tal cual: recompilarla con
otras opciones requiere MSYS2, que no está en esta máquina, y el script existente ya usa
`Release` y `USE_SSE=ON`.

---

## Dev 3.3.5

Dev 3.3.5 incorpora las optimizaciones medidas inicialmente sobre Dev 3.3.4.
Se optimizan el mezclador IQ, el filtro
de remuestreo y la entrada de audio PCM. No se cambian el filtro antialias, los
búferes configurados por el usuario ni las DLL nativas de libnrsc5.

### Medición

Prueba sintética en Windows x64, .NET 9.0.18, con aceleración Vector256 disponible.
Cada bloque contiene 32 768 muestras complejas. Se comparó el código anterior de
Dev 3.3.4 con el optimizado en Release, tras 150 bloques de calentamiento, tomando
la mediana de cinco lotes de 30 bloques. Los tiempos dependen de la CPU y su carga.

| Etapa / entrada | Anterior, ms/bloque | Optimizado, ms/bloque | Aceleración |
|---|---:|---:|---:|
| Remuestreo 744.1875 kS/s | 2.125 | 1.217 | 1.75× |
| Remuestreo 768 kS/s | 2.333 | 1.038 | 2.25× |
| Remuestreo 912 kS/s | 2.516 | 1.367 | 1.84× |
| Remuestreo 1024 kS/s | 2.356 | 1.171 | 2.01× |
| Remuestreo 1200 kS/s | 2.226 | 1.083 | 2.06× |
| Remuestreo 2400 kS/s | 2.579 | 0.759 | 3.40× |
| Remuestreo 4800 kS/s | 1.922 | 0.658 | 2.92× |
| Mezclador a 912 kS/s, desplazamiento 157321.125 Hz | 0.823 | 0.150 | 5.50× |
| Mezclador a 912 kS/s, desplazamiento y fase cero | 0.390 | 0.011 | 34.16× |

Estos valores miden etapas aisladas: no equivalen a esa aceleración del plugin
completo ni a una reducción proporcional del tiempo hasta escuchar HD. No se ha
medido una recepción real en SDR# ni se dispone de una grabación IQ de referencia
para comparar MER, BER o tiempo de enganche. La sincronización y las tramas de
audio siguen dependiendo de la señal recibida y de libnrsc5.

### Verificación

- Comparación con el filtro escalar original en siete tasas IQ, con bloques
  pequeños y grandes, reinicios y cambios de tasa: error absoluto máximo
  `7.153e-7` en las muestras de prueba.
- Mezclador frente al oscilador original durante millones de muestras, incluidos
  desplazamientos positivos, negativos y cero, reinicios y cambios de tasa:
  error absoluto máximo `1.192e-7`.
- Continuidad entre bloques y comprobación de paso a 200 kHz y rechazo fuera
  de banda. La cuantización existente de 512 fases admite diferencias de una
  fase cerca de sus fronteras al cambiar el tamaño de bloque; esa comparación
  usa una tolerancia distinta de la comparación muestra a muestra con el original.
- Orden estéreo y conversión PCM, vueltas del búfer, desbordamiento, vaciado,
  crecimiento y muestras incompletas.
- Cero bytes de asignaciones administradas en el hilo de prueba para mezclador,
  remuestreador y escritura PCM una vez reservados los búferes.

## Reproducir

Desde la raíz del repositorio, con el SDK local y las dependencias ya preparados:

```powershell
.\.tools\dotnet\dotnet.exe run --project tests\DspChecks\DspChecks.csproj -c Release -- --benchmark
```

Verificación de la ruta escalar, desactivando intrínsecos solamente para la prueba:

```powershell
$previousIntrinsics = $env:DOTNET_EnableHWIntrinsic
try {
    $env:DOTNET_EnableHWIntrinsic = '0'
    .\.tools\dotnet\dotnet.exe run --project tests\DspChecks\DspChecks.csproj -c Release --no-build
} finally {
    $env:DOTNET_EnableHWIntrinsic = $previousIntrinsics
}
```

`scripts\Build.ps1 -SkipDependencies` ejecuta las pruebas DSP normales, las
comprobaciones del proyecto y la prueba de carga/apertura/cierre de libnrsc5
antes de crear el ZIP. El benchmark es opcional y no impone un umbral de velocidad
a otros equipos. Los archivos `ReferenceResampler.cs` y `ReferenceMixer` son
referencias de prueba del algoritmo anterior; no se incluyen en el plugin.
