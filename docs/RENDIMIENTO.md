# Optimización de Dev 3.3.5

Dev 3.3.5 incorpora las optimizaciones medidas inicialmente sobre Dev 3.3.4.
Se optimizan el mezclador IQ, el filtro
de remuestreo y la entrada de audio PCM. No se cambian el filtro antialias, los
búferes configurados por el usuario ni las DLL nativas de libnrsc5.

## Medición local

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

## Verificación

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
