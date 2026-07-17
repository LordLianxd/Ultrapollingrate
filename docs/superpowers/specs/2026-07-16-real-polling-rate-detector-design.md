# UltraPolling — Detector de tasa de sondeo real

**Fecha:** 2026-07-16
**Estado:** Aprobado, pendiente de plan de implementación

## Contexto

UltraPolling configura la tasa de sondeo USB escribiendo `bInterval` en el registro y
dejando que el driver `hidusbf` reescriba el descriptor del endpoint. Hasta hoy la app
**solo puede reportar la tasa que pidió, nunca la que ocurre**.

Ese hueco ha sido el tema de fondo de todo el proyecto. La UI original lo llenaba con
una gráfica de coordenadas Bezier escritas a mano y etiquetas `10s ago / 5s ago / Now`
que no medían nada. Se borró en el rediseño monocromo precisamente por eso: mostraba
datos inventados. El header actual tiene un ecualizador blanco animado con
`Random(20260715)` — honesto en que no pretende ser un dato, pero decorativo.

El propio README de SweetLow admite la limitación: dice que hay que comprobar con una
herramienta externa (Mouse Rate Checker) si la tasa cambió de verdad.

Sin medición, la app no puede responder la única pregunta que importa: **¿funcionó?**

## Prueba de viabilidad (ya ejecutada, no es teoría)

Dos spikes despejaron los dos riesgos técnicos antes de escribir este spec.

**Spike 1 — leer reportes con marcas de tiempo.** Abriendo la interfaz HID del
DualSense y leyendo con `FileStream` asíncrono sobre un handle `FILE_FLAG_OVERLAPPED`:

```
inputReportLength = 64
reports read : 5019
window       : 5012.0 ms
average rate : 1001.2 Hz
median gap   : 0.998 ms  -> 1002.4 Hz
min gap      : 0.007 ms
max gap      : 2.627 ms
Stopwatch    : high resolution (100 ns/tick)
```

Cuatro hechos que esto establece:
1. **El DualSense corre a 1000 Hz reales.** Coincide con lo configurado.
2. **La medición es pasiva.** Nadie tocó el mando y llegaron 5019 reportes. Responde a
   cada sondeo aunque esté quieto.
3. **El reloj sobra.** 100 ns/tick; medir 0.125 ms (8000 Hz) son ~1250 ticks por hueco.
4. **No hay exclusividad.** `FILE_SHARE_READ | FILE_SHARE_WRITE` funcionó.

**Spike 2 — del `InstanceId` a la ruta HID, sin hardcodear VID/PID.** Caminando el
árbol de dispositivos desde el ID que la app ya tiene:

```
USB\VID_054C&PID_0CE6\6&227ba791&0&4
 ├─ MI_00 (audio) -> SWD\MMDEVAPI...          sin HID
 └─ MI_03 -> HID\VID_054C&PID_0CE6&MI_03...   1 interfaz HID
      \\?\HID#VID_054C&PID_0CE6&MI_03#8&2f53efb7&0&0000#{4d1e55b2-...}
```

`CM_Get_Device_Interface_List` sobre cada descendiente da las rutas. El DualSense es
compuesto: MI_00 es audio, MI_03 es el mando. Solo MI_03 tiene HID.

## Objetivos

- Medir la tasa de sondeo **real** del dispositivo seleccionado y mostrarla junto a la
  pedida.
- Convertir el ecualizador del header de decoración en instrumento: alimentarlo con los
  reportes reales.
- Que la app pueda responder "¿se aplicó?" con un número.

## No-objetivos

- **`SystemManager.cs` no cambia.** Ni registro, ni servicio, ni escaneo, ni
  identificación del driver por hash, ni el replug.
- **`PollingCore.cs` solo gana funciones puras.** Nada existente se modifica; sus 97
  tests deben seguir verdes sin tocarlos.
- No se mide más de un dispositivo a la vez. Solo el seleccionado.
- No se guarda histórico ni se exporta nada.

## Qué mide, y qué no

Mide **la llegada de reportes HID**. Eso iguala a la tasa de sondeo únicamente si el
dispositivo responde a cada sondeo. El DualSense sí. Un ratón quieto no manda nada y
mediría 0 — eso no es un fallo del medidor, y la UI debe decir por qué.

Tres casos que la UI tiene que distinguir, y no colapsar en "0 Hz":

| Situación | Qué mostrar |
|---|---|
| El dispositivo no tiene interfaz HID (webcam, audio) | **No medible** |
| Tiene HID pero no llegan reportes | **Sin datos** (y por qué: puede estar inactivo) |
| Llegan reportes | La tasa medida |

## Arquitectura

Cuatro piezas, cada una con un propósito y testeable por separado hasta donde el
hardware lo permite.

### `HidDeviceLocator` (nuevo)
`InstanceId` de USB → lista de rutas de interfaz HID. Camina el árbol con `cfgmgr32`
(`CM_Locate_DevNode`, `CM_Get_Child`, `CM_Get_Sibling`) y pide las interfaces con
`CM_Get_Device_Interface_List`. Validado por el spike 2.

Devuelve lista vacía cuando el dispositivo no tiene HID — que es la señal de "no
medible", no un error.

### `PollingMeter` (nuevo)
Abre el handle, lee reportes en una tarea de fondo, marca cada uno con `Stopwatch`, y
mantiene una **ventana deslizante** de los últimos N huecos. Expone una instantánea
inmutable para que la UI la lea sin cerrojos ni carreras.

Arranca al seleccionar un dispositivo, para al deseleccionar o cerrar. Un handle abierto
como mucho.

### `PollingCore` (amplía)
La estadística, pura y bajo test:

```csharp
public readonly record struct RateSample(double MedianGapMs, double MinGapMs, double MaxGapMs, int Count);

public static RateSample? Summarise(IReadOnlyList<double> gapsMs);
public static double RateFromGapMs(double gapMs);
public static bool RateMatches(double measuredHz, int requestedHz, double tolerancePct = 10);
```

### `MainWindow`
Arranca/para el medidor al cambiar la selección. Un `DispatcherTimer` a ~10 Hz lee la
instantánea y actualiza el ecualizador y el panel. La UI nunca toca el hilo de lectura.

## La mediana, no la media

No es un detalle de estilo. El spike lo probó:

```
min gap  0.007 ms   <- ráfaga: dos reportes pegados
max gap  2.627 ms   <- hipo
median   0.998 ms   <- la verdad
```

La media se la comen los outliers. Un detector que muestre picos crudos parpadea basura
y parece roto justo cuando debería inspirar confianza. **La mediana es el estadístico
que se muestra.** El min y el max se enseñan aparte, como lo que son: los extremos.

## El ecualizador del header

Cada barra es **un intervalo reciente**, altura ∝ tasa instantánea normalizada contra
la mediana de la ventana. Jitter → desorden visible. Un sondeo perdido → barra corta.

Deja de llamar a `Random` y pasa a ser el latido del dispositivo.

**Sin datos: barras planas, quietas y en `TextMuted` (gris).** La quietud es
información: dice que no llega nada. No se inventa movimiento cuando no hay señal — eso
sería volver exactamente a la gráfica que borramos.

### La colisión que hay que evitar

Un sondeo perfectamente estable normaliza a barras **planas**. Sin datos también son
barras **planas**. Los dos estados opuestos —"impecable" y "muerto"— se verían igual si
solo se mirara la altura.

**El color es lo que los separa**, y es un uso legítimo bajo la regla de "el color nunca
decora", porque codifica un hecho: si hay señal o no.

| Estado | Altura | Color |
|---|---|---|
| Reportes llegando | viva, según el jitter real | `TextData` (blanco) |
| Sin datos / no medible / sin selección | plana | `TextMuted` (gris) |

En la práctica el blanco casi nunca queda del todo plano: el spike midió huecos de
0.007 a 2.627 ms con mediana 0.998, así que hay jitter natural y las barras bailan. Pero
el diseño **no puede depender de eso**. Si algún día un dispositivo sondea con jitter
cero, plano-y-blanco debe seguir leyéndose como "perfecto" y plano-y-gris como "nada".
La altura describe la calidad; el color describe la existencia.

## El panel de detalle

```
PEDIDA    1000 Hz
MEDIDA    1001.2 Hz  ●
mediana   0.998 ms
min/max   0.007 / 2.627 ms
```

El punto usa la regla de color existente:
- `StatusOk` — la medida concuerda con la pedida (±10 %)
- `StatusWarn` — no concuerda: se pidió algo que no está pasando
- `TextMuted` — sin datos o no medible

Ese contraste **pedida vs medida** es el producto. Es lo que ninguna otra herramienta
enseña, y lo que responde "¿los 8000 se aplicaron?" con un número.

## Estados y bordes

| Situación | Comportamiento |
|---|---|
| Ningún dispositivo seleccionado | Medidor parado. Barras planas y grises. |
| Dispositivo sin interfaz HID | `MEDIDA: no medible (sin interfaz HID)`. Medidor no arranca. |
| Con HID, sin reportes en 2 s | `MEDIDA: sin datos`. Barras planas. |
| El dispositivo se desconecta mientras mide | El medidor para solo, sin excepción visible. Barras planas. |
| Cambio de selección | Para el medidor anterior antes de abrir el nuevo. Nunca dos handles. |
| El replug quita y devuelve el dispositivo | `ReplugDevice_Click` para el medidor **antes** de llamar a `SystemManager.ReplugDevice`, y lo rearranca cuando el rescan posterior devuelve el dispositivo. No se intenta que el handle sobreviva: el replug retira el nodo del árbol a propósito. |

## Testing

- Los 97 tests de `PollingCore` siguen verdes **sin modificarse**.
- Tests nuevos para `Summarise`, `RateFromGapMs`, `RateMatches`: outliers, listas vacías,
  un solo hueco, huecos cero, tolerancia en los bordes.
- `HidDeviceLocator` y `PollingMeter` **no se pueden testear sin hardware**. Se validan
  ejecutando contra el DualSense y comparando con el resultado conocido del spike:
  1000 Hz configurados → ~1000 Hz medidos.
- **Verificación visual: solo el usuario.**

## Riesgos

1. **Handle HID abierto mientras hay selección.** Se abre con `FILE_SHARE_READ|WRITE` y
   el spike convivió con otras apps, pero **no está probado junto a un juego a pantalla
   completa**. Si molesta, la salida es un botón de medir bajo demanda en vez de continuo.
2. **Coste del propio medidor.** A 8000 Hz son 8000 despertares por segundo. Hay que
   medirlo y reportarlo, no suponerlo. Si el medidor cuesta más que lo que mide, la
   medición miente.
3. **El replug retira el dispositivo del árbol, y un handle abierto puede vetarlo.**
   `RECONECTAR` llama a `CM_Query_And_Remove_SubTree`, que Windows **veta** si algo tiene
   el dispositivo abierto — y el medidor lo tendría abierto. El propio código ya reporta
   el veto (`PendingClose`, `OutstandingOpen`). O sea: si esto se implementa mal, el
   medidor **rompe la función que aplica el overclock**, que es la razón de existir de la
   app. Por eso la tabla de estados obliga a parar el medidor antes del replug; no es una
   optimización, es la condición para que RECONECTAR siga funcionando.
4. **Reportes ≠ sondeos.** Si un dispositivo NAKea sondeos sin datos nuevos, medimos por
   debajo de la tasa real. Es una limitación honesta del método, no un bug, y la UI no
   debe presentar la medida como verdad absoluta del bus.

## Fuera de alcance (futuro)

- Medir varios dispositivos a la vez.
- Histórico o exportación.
- Distinguir sondeo de generación de datos (haría falta trazar el bus USB, no HID).
