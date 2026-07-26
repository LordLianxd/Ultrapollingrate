# Sticks: ajustes por stick y monitor en vivo — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rehacer SENSIBILIDAD Y ZONA MUERTA según la maqueta: pestañas **STICK IZQUIERDO / STICK DERECHO** (un stick a la vez, con todo su espacio), la columna izquierda con ajustes y biblioteca de curvas, y la derecha con un **monitor 2D en vivo**, la curva con el punto de salida real, y métricas medidas. En negro y grises, sin morado.

**Architecture:** Los ajustes ya existen enteros en `RemapSettings` (`LeftDeadzonePct` = Inner, `LeftReachPct` = Alcance Máximo, `LeftCurve`, `LeftCurvaturePct`, `LeftCurvePoints`) y la biblioteca en `CurveLibraryStore`: esa mitad es re-presentación, no lógica nueva. Lo nuevo es la telemetría, y va en un archivo puro y testeable (`StickTelemetry`) con el control WPF encima, igual que se hizo con `TriggerGauge` y su arco.

**Tech Stack:** .NET 9 WPF, xUnit. Sin dependencias nuevas.

## Contexto verificado (leído en el código)

- `RemapSettings` tiene ya, por stick: `DeadzonePct` (0..30), `ReachPct` (70..100), `Curve`, `CurvaturePct` (0..100), `CurvePoints` (5 puntos), más los helpers `LeftInnerDeadzone` / `LeftOuterDeadzone` normalizados a 0..1.
- `InputTransform.ApplyStick(StickInput, innerDeadzone, ...)` y `Shape(...)` / `ShapeCustom(...)` son puros y ya están bajo test: **la transformación no se toca**, sólo se consume para dibujar.
- `CurveLibraryStore` guarda `SavedCurve { Name, Points }` en `curves.json`. La UI de "MIS CURVAS" (cargar / borrar / guardar) ya existe.
- `ControllerState` da los ejes en `-1..1`, y `VisualizerFeed.PhysicalSnapshot()` los sirve mientras la página esté abierta. `TriggerArc` ya usa ese patrón: lector propio abierto al entrar y cerrado al salir, y **no** se cierra si el motor del mando virtual está encendido, porque entonces el lector es suyo.
- `PollingMeter.Snapshot()` da `MedianHz` — es de dónde sale una tasa medida de verdad.
- Paleta: `BgBrush #000000`, `SurfaceBrush #0A0A0A`, `SurfaceAltBrush #111111`, `BorderBrush #1F1F1F`, `TextDataBrush #FFFFFF`, `TextLabelBrush #8A8A8A`, `TextMutedBrush #4A4A4A`.
- **Esto sustituye la disposición en dos columnas** (izquierdo | derecho) del commit `1ec575e`: con un monitor 2D al lado, un stick a la vez necesita el ancho entero.

## Tres cifras de la maqueta que NO se pueden medir

**1. `Latencia: 0.8 ms` — fuera.** Es el mismo error que ya se corrigió en la página de dispositivos (lección L11): la app marca la hora de **llegada** de los reportes, no el instante del movimiento físico. El reporte del DualSense no trae marca de tiempo de origen, así que la resta es imposible. Un número ahí sería inventado. Se sustituye por **ENTRE REPORTES**, que es lo que sí se mide.

**2. `Error: 0.2%` — fuera.** Error respecto a qué. No existe una posición "verdadera" del stick contra la que comparar la reportada: lo único que hay es lo que el mando dice. Sin referencia no hay error que calcular, y una cifra sin definición es peor que ninguna.

**3. `1000 Hz Live` — sí, pero medido.** No puede ser una etiqueta fija. Sale de `PollingMeter.Snapshot()!.MedianHz` sobre el DualSense, y cuando no hay medida dice `SIN DATOS`, como la pastilla de la página de dispositivos.

**En su lugar entran dos que sí se miden y valen más:**

- **DERIVA (drift):** cuánto se separa del centro el stick **en reposo**. Es un defecto real y frecuente, y se mide sin ambigüedad (Task 1).
- **VALORES NUEVOS/s:** cada cuánto **cambia** de verdad el valor del eje, frente a cuántos reportes llegan. Responde a la pregunta que el usuario hizo — si a 8000 Hz el stick manda datos nuevos o el mismo valor repetido — y ninguna otra herramienta lo enseña. Es la diferencia entre la tasa de transporte y la de muestreo del mando.

## Global Constraints

- **Sin morado.** Negro de fondo y **grises** para separar paneles: `SurfaceBrush` para las tarjetas, `SurfaceAltBrush` para los bloques de dentro. El único color permitido sigue siendo el de estado (verde / ámbar / rojo) y sólo para estado — el punto de "en vivo" y el de deriva lo son; el rastro del monitor y la curva son blancos y grises.
- UI en **español**; comentarios de código **en español, acentos y `ñ` sin tildes**.
- La transformación (`InputTransform`) **no se toca**: esta pantalla la dibuja, no la reimplementa.
- El lector del mando se abre al entrar en la página y se cierra al salir, y **no** se cierra con el motor del mando virtual encendido.
- Commits en español, **sin `Co-Authored-By`**. El push lo hace el usuario.

## Estructura de archivos

| Archivo | Responsabilidad |
|---|---|
| `HidusbfModernGui/StickTelemetry.cs` (nuevo) | Rastro, deriva y tasa de valores nuevos. Puro, sin WPF. |
| `HidusbfModernGui.Tests/StickTelemetryTests.cs` (nuevo) | Sus tests. |
| `HidusbfModernGui/StickMonitor.xaml(.cs)` (nuevo) | El monitor 2D: círculo, zona muerta, rastro y punto vivo. |
| `HidusbfModernGui/MainWindow.xaml(.cs)` | Pestañas L/R, dos columnas, sincronizar, métricas. |
| `HidusbfModernGui/Theme.xaml` | Estilos de panel de telemetría, si hacen falta. |

---

### Task 1: `StickTelemetry` — rastro, deriva y valores nuevos (TDD)

**Files:**
- Create: `HidusbfModernGui/StickTelemetry.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj`
- Test: `HidusbfModernGui.Tests/StickTelemetryTests.cs`

**Interfaces:**
- Produces: `sealed class StickTelemetry` con `Push(double x, double y)`, `IReadOnlyList<(double X, double Y)> Trail`, `double DriftRadius`, `DriftLevel Drift`, `double NewValuesPerSecond(double reportHz)`, `void Reset()`; y `enum DriftLevel { Unknown, Ok, Leve, Alta }`.

**Decisiones y por qué:**

- **El rastro es un buffer circular de 120 muestras.** A 60 fps es un segundo: suficiente para ver la forma de un giro y corto para que el círculo no se emborrone. Guarda posiciones, no tiempos: es un dibujo, no una medida.
- **La deriva se mide sólo en reposo.** Se considera reposo cuando 30 muestras seguidas caen dentro de un radio pequeño; si el usuario está moviendo el stick, la deriva **no se actualiza** y conserva el último valor bueno. Medir "centro" mientras alguien apunta daría basura.
- **Los umbrales:** `Ok` por debajo de 2 % del recorrido, `Leve` hasta 5 %, `Alta` por encima. Un DualSense sano se queda muy por debajo del 2 %; el 5 % es donde la deriva ya se nota en juego. **Ojo:** estos dos números son una primera propuesta y hay que confirmarlos con el mando del usuario (Task 5).
- **`NewValuesPerSecond`** cuenta cuántas muestras difieren de la anterior y lo escala por la tasa de reportes. Es la cifra que separa "llegan 8000 reportes" de "el stick dice algo nuevo 8000 veces".

- [ ] **Step 1: Enlazar en el csproj**, junto a las demás líneas `<Compile Include=...>`:

```xml
<Compile Include="..\HidusbfModernGui\StickTelemetry.cs" Link="StickTelemetry.cs" />
```

- [ ] **Step 2: Tests que fallan** — crear `HidusbfModernGui.Tests/StickTelemetryTests.cs`:

```csharp
using System.Linq;
using HidusbfModernGui;
using Xunit;

public class StickTelemetryTests
{
    private static StickTelemetry EnReposo(double x, double y, int muestras = 60)
    {
        var t = new StickTelemetry();
        for (int i = 0; i < muestras; i++) t.Push(x, y);
        return t;
    }

    [Fact]
    public void Trail_StartsEmpty() => Assert.Empty(new StickTelemetry().Trail);

    [Fact]
    public void Trail_NeverGrowsPastItsWindow()
    {
        var t = new StickTelemetry();
        for (int i = 0; i < StickTelemetry.TrailLength * 3; i++) t.Push(0.1, 0.1);
        Assert.Equal(StickTelemetry.TrailLength, t.Trail.Count);
    }

    // El rastro se dibuja del mas viejo al mas nuevo: si el orden se invierte, la estela
    // sale por delante del punto.
    [Fact]
    public void Trail_KeepsTheNewestLast()
    {
        var t = new StickTelemetry();
        t.Push(0.1, 0); t.Push(0.2, 0); t.Push(0.3, 0);
        Assert.Equal(0.3, t.Trail.Last().X, 6);
        Assert.Equal(0.1, t.Trail.First().X, 6);
    }

    [Fact]
    public void Drift_BeforeAnyRest_IsUnknown()
        => Assert.Equal(DriftLevel.Unknown, new StickTelemetry().Drift);

    [Fact]
    public void Drift_PerfectlyCentred_IsOk()
    {
        var t = EnReposo(0, 0);
        Assert.Equal(DriftLevel.Ok, t.Drift);
        Assert.Equal(0.0, t.DriftRadius, 6);
    }

    [Fact]
    public void Drift_SmallOffset_IsLeve()
        => Assert.Equal(DriftLevel.Leve, EnReposo(0.035, 0).Drift);

    [Fact]
    public void Drift_BigOffset_IsAlta()
        => Assert.Equal(DriftLevel.Alta, EnReposo(0.12, 0).Drift);

    // Mientras el stick se MUEVE no hay reposo, asi que no se mide deriva: medir el centro
    // mientras alguien apunta daria un numero sin sentido.
    [Fact]
    public void Drift_WhileMoving_StaysUnknown()
    {
        var t = new StickTelemetry();
        for (int i = 0; i < 200; i++) t.Push(i % 2 == 0 ? -0.8 : 0.8, 0);
        Assert.Equal(DriftLevel.Unknown, t.Drift);
    }

    // Y una vez medida, moverse no la borra: se conserva la ultima lectura buena.
    [Fact]
    public void Drift_SurvivesLaterMovement()
    {
        var t = EnReposo(0.12, 0);
        Assert.Equal(DriftLevel.Alta, t.Drift);
        for (int i = 0; i < 50; i++) t.Push(-0.9, 0.4);
        Assert.Equal(DriftLevel.Alta, t.Drift);
    }

    [Fact]
    public void NewValues_AllIdentical_IsZero()
        => Assert.Equal(0.0, EnReposo(0.5, 0.5).NewValuesPerSecond(1000), 3);

    [Fact]
    public void NewValues_EveryPushDifferent_MatchesTheReportRate()
    {
        var t = new StickTelemetry();
        for (int i = 0; i < 100; i++) t.Push(i / 100.0, 0);
        Assert.Equal(1000.0, t.NewValuesPerSecond(1000), 1);
    }

    // La mitad de las muestras repiten: la tasa de valores nuevos es la mitad.
    [Fact]
    public void NewValues_HalfRepeated_IsHalfTheRate()
    {
        var t = new StickTelemetry();
        for (int i = 0; i < 100; i++) t.Push((i / 2) / 100.0, 0);
        Assert.Equal(500.0, t.NewValuesPerSecond(1000), 25);
    }

    // Una tasa de reportes imposible no puede producir una tasa de valores inventada.
    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NewValues_WithAnImpossibleReportRate_IsZero(double hz)
        => Assert.Equal(0.0, EnReposo(0.2, 0.2).NewValuesPerSecond(hz), 6);

    // Ejes no finitos no pueden entrar al rastro: un punto en NaN no se dibuja, se pierde.
    [Fact]
    public void Push_IgnoresNonFiniteSamples()
    {
        var t = new StickTelemetry();
        t.Push(double.NaN, 0);
        t.Push(0, double.PositiveInfinity);
        Assert.Empty(t.Trail);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var t = EnReposo(0.12, 0);
        t.Reset();
        Assert.Empty(t.Trail);
        Assert.Equal(DriftLevel.Unknown, t.Drift);
    }
}
```

- [ ] **Step 3: Verificar que fallan** (no compila).

- [ ] **Step 4: Implementación** — crear `HidusbfModernGui/StickTelemetry.cs` con la API de arriba. Puntos obligatorios:
  - `TrailLength = 120`, `RestSamples = 30`, `RestRadius = 0.15`, `DriftOk = 0.02`, `DriftLeve = 0.05`, todos `public const` y **nombrados**, nunca en línea.
  - `Push` descarta muestras no finitas **antes** de tocar nada (`double.IsFinite` en los dos ejes). Este proyecto ya se comió el fallo contrario en `RateStability`: en IEEE754 toda comparación con NaN es falsa, así que una guarda escrita sólo con comparaciones lo deja pasar.
  - `NewValuesPerSecond` devuelve 0 si `reportHz` no es finito o no es positivo.
  - Comentar **por qué** la deriva sólo se mide en reposo y por qué se conserva la última lectura.

- [ ] **Step 5: Verificar que pasan** — suite completa PASS. La suite está hoy en `Passed: 462`; esta tarea añade **18** casos (14 `[Fact]` + 4 de la `[Theory]`), así que debe quedar en **`Passed: 480`**.

- [ ] **Step 6: Commit**

```bash
git add HidusbfModernGui/StickTelemetry.cs HidusbfModernGui.Tests/StickTelemetryTests.cs HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj
git commit -m "feat: StickTelemetry - rastro, deriva y valores nuevos por segundo (TDD)"
```

---

### Task 2: `StickMonitor` — el monitor 2D en vivo

**Files:**
- Create: `HidusbfModernGui/StickMonitor.xaml`, `HidusbfModernGui/StickMonitor.xaml.cs`

**Interfaces:**
- Consumes: `StickTelemetry` (Task 1).
- Produces: `StickMonitor` con `Push(double x, double y)`, `double InnerDeadzone`, `double OuterReach`, `DriftLevel Drift`, `void Reset()`. Consumido por Task 4.

**El dibujo, en negro y grises** (la maqueta es morada; aquí no):

- Círculo exterior: borde `BorderBrush`, relleno `SurfaceAltBrush` — es el "gris en ventana" que separa el monitor del fondo.
- **Anillo de zona muerta interior**: círculo de radio `InnerDeadzone` con trazo `TextMutedBrush` discontinuo. Es la única forma de ver *dónde* corta la zona muerta que estás ajustando, y es la razón de que el monitor valga para algo más que mirar.
- **Círculo de alcance máximo**: radio `OuterReach`, trazo `TextMutedBrush`.
- **Cruz de ejes** en `#141414`, para leer el centro sin que compita con el rastro.
- **Rastro**: `Polyline` blanca cuya opacidad cae del punto actual hacia atrás. Se redibuja entero en cada `Push`; con 120 puntos es barato.
- **Punto vivo**: círculo blanco relleno de 7 px.

- [ ] **Step 1: XAML** — un `Canvas` cuadrado de 240×240 dentro de un `Viewbox`, para que encoja con la ventana en vez de salirse. **Este error ya se cometió con `TriggerArc`**: un control de tamaño fijo dentro de una columna elástica se recorta en ventana pequeña.

- [ ] **Step 2: Code-behind** — construir los círculos y ejes una vez en `Loaded`; `Push` sólo mueve el punto y reconstruye los puntos de la `Polyline`. **No** reconstruir el árbol visual por cada muestra: son 60 veces por segundo.

- [ ] **Step 3: Verificación** — `dotnet build` limpio (es el único chequeo que caza errores de XAML) y suite en `Passed: 480`.

- [ ] **Step 4: Commit**

```bash
git add -A HidusbfModernGui && git commit -m "feat(ui): monitor 2D del stick con rastro, zona muerta y alcance"
```

---

### Task 3: La curva, con el punto de salida real

**Files:**
- Modify: el canvas de curva que ya existe en `MainWindow.xaml` (`CURVA (entrada -> salida)`)

**Interfaces:**
- Consumes: `InputTransform.Shape(...)` / `ShapeCustom(...)`, que ya existen y están bajo test.

- [ ] **Step 1: Punto vivo sobre la curva.** Con el stick en `raw`, dibujar un círculo blanco en `(raw, salida)` sobre la curva ya trazada, y debajo el rótulo `Entrada 45% → Salida 32%`.

**La salida se saca de `InputTransform`, no de una fórmula nueva.** Si esta pantalla calculase la curva por su cuenta, podría enseñar una cosa mientras el mando hace otra — y esa mentira es justo la que este proyecto no comete. Reusar la misma función es lo que garantiza que el dibujo y el mando coincidan.

- [ ] **Step 2: Verificación** — build limpio; suite `Passed: 480`. Manual: mover el stick despacio y comprobar que el punto recorre la curva y que en zona muerta la salida se queda en 0.

- [ ] **Step 3: Commit**

---

### Task 4: La página: pestañas por stick, dos columnas y sincronizar

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml`, `HidusbfModernGui/MainWindow.xaml.cs`

- [ ] **Step 1: Pestañas `STICK IZQUIERDO` / `STICK DERECHO`** con el estilo `SegmentButton` que ya usa la sub-nav del mando. **Marcar el segmento inicial en `Window_Loaded`, nunca con `IsChecked="True"` en el XAML**: ese evento llega durante el parseo, antes de que existan los paneles hermanos, y ya tumbó la app una vez.

- [ ] **Step 2: Dos columnas.** Izquierda `AJUSTES Y BIBLIOTECA DE CURVAS` (los controles que ya existen, reordenados: Curva Activa, biblioteca, Zona Muerta Inicial, Alcance Máximo, Tipo de Respuesta, "+ Crear nueva curva limpia"). Derecha `TELEMETRÍA Y MONITOR 2D EN VIVO` con el monitor, la curva y las métricas.

- [ ] **Step 3: Un solo juego de controles, no dos.** Las pestañas cambian a **qué stick** apuntan los mismos controles; no se duplican. Duplicarlos obligaría a mantener dos copias en sincronía y es de donde salen los bugs de "cambié uno y se movió el otro".

- [ ] **Step 4: `Sincronizar L/R`.** Con el interruptor puesto, cambiar cualquier ajuste lo copia al otro stick. Al **activarlo**, copiar del stick visible al otro y decirlo en la barra de estado — que se sincronicen en silencio y el usuario pierda los ajustes del otro stick sin enterarse sería un borrado encubierto.

- [ ] **Step 5: Métricas, con los nombres honestos:**
  - `TASA` — `PollingMeter.Snapshot()?.MedianHz`, o `SIN DATOS`.
  - `ENTRE REPORTES` — `MedianGapMs`. **Nunca "Latencia".**
  - `VALORES NUEVOS` — `StickTelemetry.NewValuesPerSecond(tasa)`.
  - `DERIVA` — `Ok` / `Leve` / `Alta` / `SIN DATOS`, con el punto de estado en verde / ámbar / rojo / gris.
  - **No** hay `Error`.

- [ ] **Step 6: El lector, sólo con la página abierta** — copiar el patrón de `UpdateTriggerArcRunState`: engancharse a `IsVisibleChanged` de la página, abrir el lector al entrar, cerrarlo al salir, y **no** cerrarlo si `_engineRunning || _engineBusy`.

- [ ] **Step 7: Verificación** — build limpio; suite `Passed: 480`. Manual con el mando: las dos pestañas mueven ajustes distintos; sincronizar copia y lo dice; el monitor sigue al stick; soltar el stick deja una deriva estable; salir de la página para el lector (comprobar que la CPU baja).

- [ ] **Step 8: Commit**

---

### Task 5: Calibrar los umbrales con el mando, y documentar

**Files:**
- Modify: `HidusbfModernGui/StickTelemetry.cs` (si la medida lo pide), `HidusbfModernGui.Tests/StickTelemetryTests.cs`, `README.md`, `docs/DOCUMENTACION.md`

- [ ] **Step 1: Medir de verdad.** Con el DualSense del usuario en reposo, anotar el `DriftRadius` real durante un minuto. Si un mando sano supera el 2 %, el umbral `DriftOk` está mal puesto y hay que subirlo **con la medida delante, no a ojo** — y actualizar los tests. Un indicador que marca `Leve` en un mando perfecto es peor que no tenerlo.

- [ ] **Step 2: Medir `VALORES NUEVOS`** a 1000 Hz y a 8000 Hz. Esa comparación es la respuesta a si el mando manda datos nuevos o repite: anotarla en la documentación, porque es un dato que casi ninguna herramienta enseña.

- [ ] **Step 3: README** — explicar las cuatro métricas y, en particular, por qué **no** hay una de latencia:

```markdown
La pantalla de sticks no ensena "latencia" y no es un olvido: la app marca la
hora en que LLEGA cada reporte, y la latencia seria el retardo desde que
mueves el stick. El reporte del DualSense no trae marca de tiempo de origen,
asi que el instante del movimiento no es un dato que la app tenga. Lo que si
mide es ENTRE REPORTES, el hueco entre llegadas.

Tampoco hay un porcentaje de "error": no existe una posicion verdadera del
stick contra la que comparar la reportada, asi que no hay error que calcular.
```

- [ ] **Step 4: DOCUMENTACION.md** — `StickTelemetry` al mapa de módulos, y una lección nueva:

```markdown
- **L15 — Una maqueta puede pedir cifras que no existen.** La de los sticks traia
  "Latencia 0.8 ms" y "Error 0.2%". La primera es la misma confusion que ya costo la
  leccion L11; la segunda no tiene referencia contra la que medirse. Se cambiaron por
  DERIVA y VALORES NUEVOS/s, que si se miden y ademas dicen mas.
```

- [ ] **Step 5: Commit**

---

## Self-review

- **Cobertura de la maqueta:** pestañas L/R → T4 S1; ajustes y biblioteca → T4 S2 (controles ya existentes); Zona Muerta Inicial y Alcance Máximo → ya existen como `DeadzonePct`/`ReachPct`; Tipo de Respuesta → ya existe; monitor 2D con rastro → T1+T2; gráfico de salida con punto vivo → T3; métricas → T4 S5; sincronizar L/R → T4 S4; sin morado → Global Constraints + T2. ✓
- **Lo que la maqueta pedía y no se puede dar, declarado y sustituido:** `Latencia` → `ENTRE REPORTES`; `Error` → fuera, y en su hueco `DERIVA` y `VALORES NUEVOS`, las dos medibles. ✓
- **Lo que no se reimplementa:** `InputTransform` es la única fuente de la curva, para que el dibujo no pueda mentir respecto al mando. `RemapSettings` y `CurveLibraryStore` se consumen tal cual. ✓
- **Riesgo cubierto — el control fijo dentro de columna elástica:** T2 S1 obliga al `Viewbox`. Es exactamente el fallo que ya se cometió con `TriggerArc` y hubo que arreglar después.
- **Riesgo cubierto — `IsChecked` en XAML:** T4 S1 obliga a marcar en `Window_Loaded`. Ese error ya tumbó la app una vez.
- **Riesgo cubierto — NaN:** T1 S4 obliga a `double.IsFinite` antes de las comparaciones, con dos tests que lo fijan. Es la trampa que ya se coló en `RateStability`.
- **Riesgo cubierto — umbrales inventados:** T5 S1 obliga a calibrar `DriftOk` contra el mando real antes de darlo por bueno, y a mover los tests con la medida. Los 2 % y 5 % del plan son una propuesta, no un dato.
- **Riesgo cubierto — sincronizar borra en silencio:** T4 S4 obliga a avisar al activar.
- **Cuentas de tests:** 462 hoy → 480 tras T1 (18 casos) → 480 hasta el final.
