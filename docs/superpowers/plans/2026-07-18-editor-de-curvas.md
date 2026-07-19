# Editor de curvas personalizadas + luces compatibles con el mando virtual — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Arreglar que la página de luces deja de funcionar con el mando virtual activo, y añadir una curva de respuesta "Editor" con puntos arrastrables (interpolación monótona PCHIP) que el usuario dibuja sobre la gráfica CURVA existente.

**Architecture:** El núcleo sigue siendo puro y testeable: `CurvePoint` + `InputTransform.ShapeCustom` (Fritsch–Carlson) entran por overloads nuevos de `Shape`/`ApplyStick`; `RemapSettings` guarda los puntos por stick y los perfiles JSON los round-tripean; `RemapEngine.Transform` los pasa al overload. La UI reusa el canvas CURVA (220×100) añadiendo 3 puntos arrastrables cuando la curva elegida es `Propia`. El bug de luces se arregla en dos capas: `IsPlayStation` excluye el DS4 virtual (PID_05C4) y, cuando el escaneo externo no ve el físico oculto, se resuelve su instance id **en-proceso** (nuestro exe está en la whitelist de HidHide).

**Tech Stack:** .NET 9 WPF (sin libs nuevas), xUnit, System.Text.Json, cfgmgr32 (ya usado), Nefarius.Utilities.DeviceManagement (ya referenciado).

## Global Constraints

- UI en **español**, tema **monocromo** (grises/blanco; nada de color nuevo).
- El proyecto de tests **linkea archivos individualmente** en `HidusbfModernGui.Tests.csproj` (`<Compile Include="..\HidusbfModernGui\X.cs">`): **todo archivo nuevo del núcleo debe añadirse ahí o los tests no lo ven** (lección del RemapEngine).
- El proyecto de tests **no referencia** Nefarius/HidSharp/WPF: nada que los toque puede linkearse a tests (por eso `HidHideControl.cs`, `DualSenseReader.cs`, `VirtualPad.cs` y `MainWindow.xaml.cs` se verifican a mano).
- Enum `ResponseCurve`: los perfiles guardan el **nombre** del valor (`"LeftCurve": "Normal"`); solo se puede **añadir al final**, nunca renombrar valores existentes.
- Commits **sin** Co-Authored-By. El push lo hace el usuario.
- Sin macros, sin teclado/ratón, sin evasión de anticheat: esto solo reconfigura el mando.
- Identidad git del repo: `UltraPolling <calizayacristhian96@gmail.com>` (ya configurada localmente).

## Mapa de todo lo que se configura (pedido explícito del usuario)

Cada cosa configurable, dónde vive y cómo llega al hardware — para que el editor de curvas (y cualquier feature futura) se integre sin romper nada:

| Configura | Objeto en memoria | Persistencia | Cómo se aplica | Con el mando virtual activo |
|---|---|---|---|---|
| Tasa de sondeo + modo del driver | `_allDevices` / registro | Registro de Windows (hidusbf) | APLICAR CAMBIOS → filtro + replug | Independiente: acelera al FÍSICO; el lector del motor lee a esa tasa |
| Luz: color, LED jugador, brillo, efectos, velocidades | `LightIntent` | `%APPDATA%\UltraPolling\intents.json` | Output report HID 0x02 al FÍSICO vía `DualSenseLight.Apply(instanceId)` | **HOY ROTO** (Tasks 1–2): el escaneo PS no ve el físico oculto y el virtual (VID_054C) se cuela en la lista |
| Sticks: zona muerta, alcance, curva, curvatura % | `RemapSettings` (`_remap`) | `remap-profiles.json` (`__ultimo_usado__` + perfiles) | `RemapEngine.Transform` en `EngineTick`, en vivo | Es su razón de ser; sin motor activo se edita pero no actúa |
| Gatillos: punto de disparo L2/R2 | `RemapSettings` | ídem | ídem | ídem |
| Botones: tabla origen→destino | `RemapSettings.ButtonRemap` | ídem | ídem | ídem |
| Touchpad: zona→botón | `RemapSettings.TouchZoneMap` | ídem | ídem | ídem |
| **NUEVO — puntos de la curva Editor por stick** | `RemapSettings.LeftCurvePoints` / `RightCurvePoints` (Task 4) | ídem (round-trip JSON) | `ShapeCustom` dentro de `Transform` | En vivo, como todo `_remap` |
| Interruptor maestro | `_engineRunning` | **No persiste** (siempre OFF al abrir: el estado seguro) | `StartEngine`/`StopEngine` | — |

Deuda conocida (fuera de alcance, anotar si molesta en juego): `EngineTick` empuja al virtual desde un `DispatcherTimer` de 8 ms en el hilo de UI (~125 Hz efectivos y con jitter si la UI está ocupada), aunque el físico entregue 8 kHz. Si tras el editor de curvas la mira aún se siente rara, la siguiente palanca es mover el push a un hilo propio.

## Estructura de archivos

- Modify: `HidusbfModernGui/DualSenseLight.cs` (filtro PID en `IsPlayStation`)
- Modify: `HidusbfModernGui/HidDeviceLocator.cs` (interfaces del propio nodo, no solo hijos)
- Modify: `HidusbfModernGui/HidHideControl.cs` (`FindPhysicalGamepadInstanceId` pasa a `public`)
- Modify: `HidusbfModernGui/ControllerState.cs` (`CurvePoint`, valor `Propia`)
- Modify: `HidusbfModernGui/InputTransform.cs` (`ShapeCustom` PCHIP + overloads)
- Modify: `HidusbfModernGui/RemapSettings.cs` (puntos por stick + default)
- Modify: `HidusbfModernGui/RemapEngine.cs` (pasar puntos al overload)
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (fallback de luces, combo Editor, arrastre de puntos, clone)
- Modify: `HidusbfModernGui/MainWindow.xaml` (eventos de ratón en los 2 canvas)
- Test: `HidusbfModernGui.Tests/DualSenseLightTests.cs`, `InputTransformCurveTests.cs`, `RemapSettingsTests.cs`, `RemapProfileStoreTests.cs`, `RemapEngineTests.cs`

---

### Task 1: Luces nunca apuntan al DS4 virtual (filtro PID)

**Files:**
- Modify: `HidusbfModernGui/DualSenseLight.cs:57-58`
- Test: `HidusbfModernGui.Tests/DualSenseLightTests.cs`

**Interfaces:**
- Consumes: `UsbDeviceModel.InstanceId`.
- Produces: `DualSenseLight.IsPlayStation(UsbDeviceModel)` que acepta el físico (PID_0CE6 o cualquier Sony que no sea el virtual) y **rechaza PID_05C4**.

- [ ] **Step 1: Test que falla**

```csharp
[Fact]
public void IsPlayStation_RejectsOurVirtualDs4()
{
    var virt = new UsbDeviceModel { InstanceId = @"HID\VID_054C&PID_05C4&Col01\1&2d595ca7&1&0000" };
    Assert.False(DualSenseLight.IsPlayStation(virt));
}

[Fact]
public void IsPlayStation_AcceptsPhysicalDualSense()
{
    var fis = new UsbDeviceModel { InstanceId = @"USB\VID_054C&PID_0CE6\6&227ba791&0&4" };
    Assert.True(DualSenseLight.IsPlayStation(fis));
}
```

- [ ] **Step 2: Verificar que falla** — `dotnet test HidusbfModernGui.Tests\HidusbfModernGui.Tests.csproj --filter "FullyQualifiedName~DualSenseLightTests"`. Esperado: `IsPlayStation_RejectsOurVirtualDs4` FALLA (hoy devuelve true).

- [ ] **Step 3: Implementación mínima** — en `DualSenseLight.cs` reemplazar `IsPlayStation`:

```csharp
// Sony, pero NUNCA nuestro propio DS4 virtual de ViGEm (VID_054C&PID_05C4): escribirle
// el reporte 0x02 del DualSense no hace nada y, peor, roba el lugar del fisico en la lista.
public static bool IsPlayStation(UsbDeviceModel model) =>
    model?.InstanceId?.IndexOf("VID_054C", StringComparison.OrdinalIgnoreCase) >= 0 &&
    model.InstanceId.IndexOf("PID_05C4", StringComparison.OrdinalIgnoreCase) < 0;
```

- [ ] **Step 4: Verificar que pasa** — mismo comando. Esperado: PASS (y toda la clase de tests en verde).
- [ ] **Step 5: Commit** — `git add HidusbfModernGui/DualSenseLight.cs HidusbfModernGui.Tests/DualSenseLightTests.cs && git commit -m "fix: la pagina de luces nunca apunta al DS4 virtual (excluye PID_05C4)"`

---

### Task 2: Luces encuentran al físico oculto (resolución en-proceso)

Con HidHide activo, el escaneo por PowerShell (proceso externo, no whitelisteado) no ve el físico → la lista queda vacía y la página de luces muere. Nuestro exe SÍ lo ve. Se resuelve el instance id en-proceso y se inyecta una entrada sintética en la lista.

**Files:**
- Modify: `HidusbfModernGui/HidHideControl.cs:247` (private → public)
- Modify: `HidusbfModernGui/HidDeviceLocator.cs:56-63`
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (`RefreshPlayStationDevices` ~L1531, `ReapplyIntent` ~L1838)

**Interfaces:**
- Consumes: `HidHideControl.FindPhysicalGamepadInstanceId()` (ahora `public static`, devuelve `string?` con el id `HID\VID_054C&PID_0CE6...`), `HidDeviceLocator.FindHidPaths(string)`.
- Produces: la lista de la página de luces siempre contiene el físico si existe, oculto o no.

Sin test unitario: todo el camino toca cfgmgr32/Devcon/hardware (el proyecto de tests no referencia Nefarius). Verificación manual en Step 4.

- [ ] **Step 1: `FindHidPaths` acepta el propio nodo HID** — hoy solo busca interfaces en los **hijos** del id recibido (`CM_Get_Child` primero), así que pasarle un id `HID\...` directo devuelve vacío. En `HidDeviceLocator.FindHidPaths`, tras el `CM_Locate_DevNode`, incluir también las interfaces del propio nodo:

```csharp
if (CM_Locate_DevNode(out uint dev, usbInstanceId, 0) != CR_SUCCESS) return paths;

// El propio nodo primero: si el id ya ES el nodo HID (la ruta en-proceso de las luces
// con HidHide activo le pasa HID\VID_054C&PID_0CE6...), su interfaz esta aqui, no en
// un hijo. Para un id USB compuesto (el camino clasico) esto no aporta nada y no rompe.
paths.AddRange(InterfacesFor(usbInstanceId, hidGuid));

if (CM_Get_Child(out uint child, dev, 0) != CR_SUCCESS) return paths;
```

- [ ] **Step 2: exponer el resolutor** — en `HidHideControl.cs`, cambiar la firma de `FindPhysicalGamepadInstanceId` de `private static` a `public static` (el comentario existente ya describe el contrato; no cambiar el cuerpo).

- [ ] **Step 3: fallback en las dos rutas de las luces** — en `MainWindow.xaml.cs`:

En `RefreshPlayStationDevices()` (tras `var ps = _allDevices.Where(DualSenseLight.IsPlayStation).ToList();`):

```csharp
// Con HidHide ocultando el fisico, el escaneo (PowerShell, proceso externo sin
// whitelist) no lo ve, pero NOSOTROS si: se resuelve en-proceso y se inyecta una
// entrada sintetica para que la pagina de luces siga funcionando con el motor activo.
if (ps.Count == 0)
{
    var hiddenId = HidHideControl.FindPhysicalGamepadInstanceId();
    if (hiddenId != null)
        ps.Add(new UsbDeviceModel
        {
            Name = "DualSense (oculto por HidHide)",
            InstanceId = hiddenId,
            Status = "OK",
            Class = "HIDClass",
        });
}
```

En `ReapplyIntent()` (reemplazar la línea `var pad = _allDevices.FirstOrDefault(DualSenseLight.IsPlayStation);`):

```csharp
var pad = _allDevices.FirstOrDefault(DualSenseLight.IsPlayStation);
if (pad == null)
{
    var hiddenId = HidHideControl.FindPhysicalGamepadInstanceId();
    if (hiddenId != null)
        pad = new UsbDeviceModel { Name = "DualSense (oculto)", InstanceId = hiddenId, Status = "OK", Class = "HIDClass" };
}
```

- [ ] **Step 4: Prueba manual** — compilar (`dotnet build HidusbfModernGui\HidusbfModernGui.csproj -c Debug`), abrir como admin con el DualSense por USB:
  1. SIN motor: Luces del mando → cambiar color → la barra cambia (regresión cero).
  2. ACTIVAR MANDO VIRTUAL → Luces del mando → cambiar color → **la barra del físico cambia** (antes: nada). La lista muestra "DualSense (oculto por HidHide)" o el nombre normal.
  3. VOLVER AL MANDO NATIVO → luces siguen funcionando.
- [ ] **Step 5: Commit** — `git add -u && git commit -m "fix: las luces funcionan con el mando virtual activo (resolucion en-proceso del fisico oculto)"`

---

### Task 3: Núcleo — `CurvePoint` + `ShapeCustom` PCHIP (TDD)

Interpolación cúbica monótona de Fritsch–Carlson: pasa exactamente por los puntos del usuario, suave, y **sin sobreimpulso** (nunca se sale del rango entre puntos vecinos — crítico para que la mira no haga cosas raras entre punto y punto).

**Files:**
- Modify: `HidusbfModernGui/ControllerState.cs`
- Modify: `HidusbfModernGui/InputTransform.cs`
- Test: `HidusbfModernGui.Tests/InputTransformCurveTests.cs`

**Interfaces:**
- Produces: `readonly record struct CurvePoint(double X, double Y)`; `ResponseCurve.Propia` (añadido AL FINAL del enum); `InputTransform.ShapeCustom(double t, IReadOnlyList<CurvePoint>? points)`; overloads `Shape(double t, ResponseCurve curve, int curvaturePct, IReadOnlyList<CurvePoint>? points)` y `ApplyStick(StickInput s, double inner, double outer, ResponseCurve curve, int curvaturePct, IReadOnlyList<CurvePoint>? points)`.
- El `Shape(t, curve, pct)` viejo con `Propia` devuelve `t` (lineal neutro): el icono del combo y cualquier llamada sin puntos no explotan.

- [ ] **Step 1: Tests que fallan** — añadir a `InputTransformCurveTests.cs`:

```csharp
private static List<CurvePoint> Diagonal() => new()
{
    new(0, 0), new(0.25, 0.25), new(0.5, 0.5), new(0.75, 0.75), new(1, 1),
};

[Fact]
public void ShapeCustom_DiagonalPoints_IsIdentity()
{
    foreach (var t in new[] { 0.0, 0.1, 0.33, 0.5, 0.77, 1.0 })
        Assert.Equal(t, InputTransform.ShapeCustom(t, Diagonal()), 3);
}

[Fact]
public void ShapeCustom_PassesThroughEveryPoint()
{
    var pts = new List<CurvePoint> { new(0, 0), new(0.3, 0.6), new(0.7, 0.65), new(1, 1) };
    foreach (var p in pts)
        Assert.Equal(p.Y, InputTransform.ShapeCustom(p.X, pts), 3);
}

[Fact]
public void ShapeCustom_NoOvershoot_BetweenPoints()
{
    // Subida brusca y luego casi plano: un spline ingenuo sobreimpulsa por encima de 0.9
    // entre 0.5 y 1.0; PCHIP no debe salirse de [0.9, 1.0] en ese tramo.
    var pts = new List<CurvePoint> { new(0, 0), new(0.5, 0.9), new(1, 1) };
    for (double t = 0.5; t <= 1.0; t += 0.05)
    {
        double y = InputTransform.ShapeCustom(t, pts);
        Assert.InRange(y, 0.9 - 1e-9, 1.0 + 1e-9);
    }
}

[Fact]
public void ShapeCustom_FlatSegment_StaysFlat()
{
    var pts = new List<CurvePoint> { new(0, 0.5), new(0.5, 0.5), new(1, 1) };
    Assert.Equal(0.5, InputTransform.ShapeCustom(0.25, pts), 3);
}

[Fact]
public void ShapeCustom_ClampsOutsideAndHandlesUnsorted()
{
    var pts = new List<CurvePoint> { new(1, 1), new(0, 0), new(0.5, 0.8) };  // desordenados
    Assert.Equal(0.0, InputTransform.ShapeCustom(-0.5, pts), 3);
    Assert.Equal(1.0, InputTransform.ShapeCustom(1.5, pts), 3);
    Assert.Equal(0.8, InputTransform.ShapeCustom(0.5, pts), 3);
}

[Fact]
public void ShapeCustom_NullOrTooFewPoints_IsLinear()
{
    Assert.Equal(0.4, InputTransform.ShapeCustom(0.4, null), 3);
    Assert.Equal(0.4, InputTransform.ShapeCustom(0.4, new List<CurvePoint> { new(0, 0) }), 3);
}

[Fact]
public void Shape_Propia_WithoutPoints_IsLinear()
{
    Assert.Equal(0.6, InputTransform.Shape(0.6, ResponseCurve.Propia, 50), 3);
}

[Fact]
public void ApplyStick_WithCustomPoints_UsesThem()
{
    var pts = new List<CurvePoint> { new(0, 0), new(0.5, 0.9), new(1, 1) };
    var (x, _) = InputTransform.ApplyStick(new StickInput(0.5, 0.0), 0.0, 1.0,
                                           ResponseCurve.Propia, 50, pts);
    Assert.Equal(0.9, x, 2);
}
```

- [ ] **Step 2: Verificar que falla** — `dotnet test ... --filter "FullyQualifiedName~InputTransformCurveTests"`. Esperado: error de compilación (`CurvePoint`/`ShapeCustom`/`Propia` no existen).

- [ ] **Step 3: Implementación** — en `ControllerState.cs`:

```csharp
public enum ResponseCurve { Precisa, Normal, Rapida, Personalizada, Dinamica, Digital, Propia }

// Un punto de la curva Editor, en coordenadas normalizadas 0..1 (X = entrada del stick
// ya sin deadzone, Y = salida). Record struct posicional: System.Text.Json lo
// (de)serializa por su constructor, asi que viaja tal cual dentro de RemapSettings.
public readonly record struct CurvePoint(double X, double Y);
```

En `InputTransform.cs` (añadir; `Shape` existente gana un `case ResponseCurve.Propia: return t;`):

```csharp
// Curva Editor: interpolacion cubica monotona de Fritsch-Carlson (PCHIP) por los puntos
// del usuario. Pasa exactamente por cada punto, es suave, y NUNCA sobreimpulsa (la
// salida entre dos puntos queda dentro del rango de esos puntos): entre puntos vecinos
// la mira jamas hace algo que el usuario no dibujo. Con <2 puntos degrada a lineal.
public static double ShapeCustom(double t, System.Collections.Generic.IReadOnlyList<CurvePoint>? points)
{
    t = Math.Clamp(t, 0.0, 1.0);
    if (points == null || points.Count < 2) return t;

    var p = points.OrderBy(q => q.X).ToArray();
    int n = p.Length;
    if (t <= p[0].X) return Math.Clamp(p[0].Y, 0.0, 1.0);
    if (t >= p[n - 1].X) return Math.Clamp(p[n - 1].Y, 0.0, 1.0);

    // Secantes de cada tramo y tangentes en cada punto.
    var h = new double[n - 1];
    var delta = new double[n - 1];
    for (int i = 0; i < n - 1; i++)
    {
        h[i] = Math.Max(p[i + 1].X - p[i].X, 1e-9);
        delta[i] = (p[i + 1].Y - p[i].Y) / h[i];
    }
    var m = new double[n];
    m[0] = delta[0];
    m[n - 1] = delta[n - 2];
    for (int i = 1; i < n - 1; i++)
        m[i] = delta[i - 1] * delta[i] <= 0 ? 0.0 : (delta[i - 1] + delta[i]) / 2.0;

    // Limitador de Fritsch-Carlson: recorta las tangentes que producirian sobreimpulso.
    for (int i = 0; i < n - 1; i++)
    {
        if (delta[i] == 0) { m[i] = 0; m[i + 1] = 0; continue; }
        double a = m[i] / delta[i], b = m[i + 1] / delta[i];
        double s = a * a + b * b;
        if (s > 9.0)
        {
            double tau = 3.0 / Math.Sqrt(s);
            m[i] = tau * a * delta[i];
            m[i + 1] = tau * b * delta[i];
        }
    }

    // Evaluacion del hermite cubico en el tramo que contiene t.
    int k = 0;
    while (k < n - 2 && t > p[k + 1].X) k++;
    double u = (t - p[k].X) / h[k];
    double u2 = u * u, u3 = u2 * u;
    double y = p[k].Y * (2 * u3 - 3 * u2 + 1)
             + h[k] * m[k] * (u3 - 2 * u2 + u)
             + p[k + 1].Y * (-2 * u3 + 3 * u2)
             + h[k] * m[k + 1] * (u3 - u2);
    return Math.Clamp(y, 0.0, 1.0);
}

// Shape con la curva Editor: Propia usa los puntos; el resto ignora points y delega.
public static double Shape(double t, ResponseCurve curve, int curvaturePct,
                           System.Collections.Generic.IReadOnlyList<CurvePoint>? points)
    => curve == ResponseCurve.Propia ? ShapeCustom(t, points) : Shape(t, curve, curvaturePct);

// ApplyStick con puntos: identico al overload de curva+curvatura, pero la forma puede
// ser la curva Editor. Es la via que usa RemapEngine.
public static (double X, double Y) ApplyStick(StickInput s, double innerDeadzone,
    double outerDeadzone, ResponseCurve curve, int curvaturePct,
    System.Collections.Generic.IReadOnlyList<CurvePoint>? points)
{
    double mag = Math.Sqrt(s.X * s.X + s.Y * s.Y);
    if (mag <= innerDeadzone || mag <= 0.0) return (0.0, 0.0);
    double outer = Math.Max(outerDeadzone, innerDeadzone + 1e-6);
    double t = Math.Clamp((mag - innerDeadzone) / (outer - innerDeadzone), 0.0, 1.0);
    t = Shape(t, curve, curvaturePct, points);
    double ux = s.X / mag, uy = s.Y / mag;
    return (ux * t, uy * t);
}
```

`InputTransform.cs` necesita `using System.Linq;` para el `OrderBy`.

- [ ] **Step 4: Verificar que pasa** — mismo filtro. Esperado: PASS todos.
- [ ] **Step 5: Commit** — `git add HidusbfModernGui/ControllerState.cs HidusbfModernGui/InputTransform.cs HidusbfModernGui.Tests/InputTransformCurveTests.cs && git commit -m "feat: curva Editor - CurvePoint + PCHIP monotono en InputTransform (TDD)"`

---

### Task 4: `RemapSettings` guarda los puntos, los perfiles los round-tripean, el motor los usa (TDD)

**Files:**
- Modify: `HidusbfModernGui/RemapSettings.cs`
- Modify: `HidusbfModernGui/RemapEngine.cs`
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (`CloneRemapSettings` ~L1103)
- Test: `HidusbfModernGui.Tests/RemapSettingsTests.cs`, `RemapProfileStoreTests.cs`, `RemapEngineTests.cs`

**Interfaces:**
- Produces: `RemapSettings.LeftCurvePoints` / `RightCurvePoints` (`List<CurvePoint>`, default = 5 puntos en la diagonal via `RemapSettings.DefaultCurvePoints()`), consumidos por `RemapEngine.Transform` a través del overload de `ApplyStick` con puntos.

- [ ] **Step 1: Tests que fallan**

En `RemapSettingsTests.cs`:

```csharp
[Fact]
public void CurvePoints_DefaultToFiveDiagonalPoints()
{
    var s = new RemapSettings();
    Assert.Equal(5, s.LeftCurvePoints.Count);
    Assert.Equal(new CurvePoint(0, 0), s.LeftCurvePoints[0]);
    Assert.Equal(new CurvePoint(1, 1), s.LeftCurvePoints[^1]);
    Assert.Equal(new CurvePoint(0.5, 0.5), s.RightCurvePoints[2]);
}
```

En `RemapProfileStoreTests.cs` (siguiendo el patrón de aislamiento de disco que ya usen los tests existentes de ese archivo):

```csharp
[Fact]
public void CurvePoints_RoundTripThroughJson()
{
    var s = new RemapSettings
    {
        LeftCurve = ResponseCurve.Propia,
        LeftCurvePoints = new() { new(0, 0), new(0.3, 0.6), new(0.7, 0.65), new(1, 1) },
    };
    string json = System.Text.Json.JsonSerializer.Serialize(s);
    var back = System.Text.Json.JsonSerializer.Deserialize<RemapSettings>(json)!;
    Assert.Equal(ResponseCurve.Propia, back.LeftCurve);
    Assert.Equal(4, back.LeftCurvePoints.Count);
    Assert.Equal(0.6, back.LeftCurvePoints[1].Y, 3);
}
```

(Si `RemapProfileStore` serializa con opciones propias — p. ej. `JsonStringEnumConverter` — usar esas mismas opciones en el test, no otras.)

En `RemapEngineTests.cs`:

```csharp
[Fact]
public void CustomCurvePoints_ReachTheOutput()
{
    var s = new RemapSettings
    {
        LeftCurve = ResponseCurve.Propia,
        LeftCurvePoints = new() { new(0, 0), new(0.5, 0.9), new(1, 1) },
    };
    var input = new ControllerState { Left = new StickInput(0.5, 0.0) };
    var outp = RemapEngine.Transform(input, s);
    Assert.Equal(0.9, outp.Left.X, 2);
}
```

- [ ] **Step 2: Verificar que fallan** — compilación (propiedades inexistentes) / assert.

- [ ] **Step 3: Implementación**

`RemapSettings.cs` (junto a los otros props):

```csharp
// Puntos de la curva "Editor" (ResponseCurve.Propia), en 0..1. Siempre 5: extremos
// (0,0)/(1,1) fijos en la UI y 3 interiores arrastrables. Solo actuan cuando la curva
// del stick es Propia; se guardan siempre (el usuario no pierde su dibujo al cambiar
// de preset y volver).
public List<CurvePoint> LeftCurvePoints { get; set; } = DefaultCurvePoints();
public List<CurvePoint> RightCurvePoints { get; set; } = DefaultCurvePoints();

public static List<CurvePoint> DefaultCurvePoints() => new()
{
    new(0, 0), new(0.25, 0.25), new(0.5, 0.5), new(0.75, 0.75), new(1, 1),
};
```

`RemapEngine.cs` — las dos llamadas a `ApplyStick` ganan el argumento de puntos:

```csharp
var (lx, ly) = InputTransform.ApplyStick(
    input.Left, s.LeftInnerDeadzone, s.LeftOuterDeadzone, s.LeftCurve, s.LeftCurvaturePct, s.LeftCurvePoints);
var (rx, ry) = InputTransform.ApplyStick(
    input.Right, s.RightInnerDeadzone, s.RightOuterDeadzone, s.RightCurve, s.RightCurvaturePct, s.RightCurvePoints);
```

`MainWindow.xaml.cs` — `CloneRemapSettings` copia las listas nuevas (¡si se olvida, cargar un perfil comparte la lista y editar el perfil A modifica el B!):

```csharp
LeftCurvePoints = new List<CurvePoint>(s.LeftCurvePoints),
RightCurvePoints = new List<CurvePoint>(s.RightCurvePoints),
```

- [ ] **Step 4: Verificar que pasan** — `dotnet test` completo (todo el proyecto). Esperado: PASS total.
- [ ] **Step 5: Commit** — `git add -u && git commit -m "feat: puntos de la curva Editor en RemapSettings/perfiles y en el motor (TDD)"`

---

### Task 5: UI — opción "Editor" con puntos arrastrables sobre el canvas CURVA

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml` (los 2 canvas de curva, ~L556 el izquierdo)
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (`CurvePresets` ~L772, `AddCurveItem` ~L802, `DrawCurve`/`RedrawLeftCurve`/`RedrawRightCurve` ~L1046-1078, handlers nuevos)

**Interfaces:**
- Consumes: `_remap.LeftCurvePoints`/`RightCurvePoints` (Task 4), `InputTransform.Shape(t, curve, pct, points)` (Task 3), `RememberRemap()` (persistencia debounced existente).
- Produces: al elegir "Editor" en RESPUESTA, aparecen 3 puntos arrastrables sobre la gráfica; arrastrarlos redibuja la curva en vivo y (con el motor activo) cambia el mando al instante.

- [ ] **Step 1: opción en el combo** — en `CurvePresets` añadir al final:

```csharp
("Editor", ResponseCurve.Propia),
```

En `AddCurveItem`, el muestreo del icono usa `Shape(t, curve, 50)`, que para `Propia` es lineal (idéntico a Lineal, confuso). Caso especial con una forma de ejemplo:

```csharp
private static readonly CurvePoint[] IconPropiaPoints =
    { new(0, 0), new(0.3, 0.55), new(0.7, 0.6), new(1, 1) };

// dentro del for de AddCurveItem:
double y = curve == ResponseCurve.Propia
    ? InputTransform.ShapeCustom(t, IconPropiaPoints)
    : InputTransform.Shape(t, curve, 50);
```

- [ ] **Step 2: DrawCurve muestrea con puntos** — `DrawCurve` gana un parámetro `IReadOnlyList<CurvePoint>? points` y usa el overload nuevo de `ApplyStick`; `RedrawLeftCurve`/`RedrawRightCurve` pasan `_remap.LeftCurvePoints`/`_remap.RightCurvePoints`.

- [ ] **Step 3: XAML — eventos de ratón** en ambos canvas (mismo patrón, cambia el prefijo Left/Right):

```xml
<Canvas x:Name="LeftCurveCanvas" Width="220" Height="100" ClipToBounds="True"
        Background="Transparent"
        MouseLeftButtonDown="LeftCurveCanvas_MouseDown"
        MouseMove="LeftCurveCanvas_MouseMove"
        MouseLeftButtonUp="LeftCurveCanvas_MouseUp">
```

(`Background="Transparent"` es obligatorio: sin fondo, el área vacía del canvas no recibe hit-testing y el arrastre solo funcionaría pinchando la línea.)

- [ ] **Step 4: code-behind — marcadores + arrastre.** Un solo motor de edición parametrizado y wrappers finos por stick:

```csharp
// ===== Editor de curva (ResponseCurve.Propia): 3 puntos interiores arrastrables =====
// Los extremos (0,0)/(1,1) son fijos: la zona muerta y el alcance ya los gobiernan los
// sliders. Solo se arrastran los indices 1..3 de la lista de 5.
private readonly List<System.Windows.Shapes.Ellipse> _leftCurveDots = new();
private readonly List<System.Windows.Shapes.Ellipse> _rightCurveDots = new();
private int _dragIndex = -1;
private bool _dragIsLeft;

private void EnsureCurveDots(Canvas canvas, List<System.Windows.Shapes.Ellipse> dots)
{
    if (dots.Count > 0) return;
    for (int i = 0; i < 3; i++)
    {
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 9, Height = 9,
            Fill = (Brush)FindResource("TextDataBrush"),
            Visibility = Visibility.Collapsed,
        };
        dots.Add(dot);
        canvas.Children.Add(dot);
    }
}

// Coloca los 3 marcadores segun los puntos 1..3 y los muestra solo si la curva es Propia.
private void RefreshCurveDots(Canvas canvas, List<System.Windows.Shapes.Ellipse> dots,
                              List<CurvePoint> pts, ResponseCurve curve)
{
    EnsureCurveDots(canvas, dots);
    bool show = curve == ResponseCurve.Propia;
    for (int i = 0; i < 3; i++)
    {
        var p = pts[i + 1];
        Canvas.SetLeft(dots[i], p.X * canvas.Width - dots[i].Width / 2);
        Canvas.SetTop(dots[i], (1 - p.Y) * canvas.Height - dots[i].Height / 2);
        dots[i].Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }
}

private void CurveCanvas_Down(Canvas canvas, List<CurvePoint> pts, ResponseCurve curve,
                              bool isLeft, MouseButtonEventArgs e)
{
    if (curve != ResponseCurve.Propia) return;
    var pos = e.GetPosition(canvas);
    double x = pos.X / canvas.Width, y = 1 - pos.Y / canvas.Height;

    // El punto interior mas cercano al clic (radio de captura generoso: 15% del ancho).
    int best = -1; double bestDist = 0.15;
    for (int i = 1; i <= 3; i++)
    {
        double d = Math.Sqrt(Math.Pow(pts[i].X - x, 2) + Math.Pow(pts[i].Y - y, 2));
        if (d < bestDist) { bestDist = d; best = i; }
    }
    if (best < 0) return;
    _dragIndex = best;
    _dragIsLeft = isLeft;
    canvas.CaptureMouse();
    e.Handled = true;
}

private void CurveCanvas_Move(Canvas canvas, List<CurvePoint> pts, MouseEventArgs e)
{
    if (_dragIndex < 0 || e.LeftButton != MouseButtonState.Pressed) return;
    var pos = e.GetPosition(canvas);
    // X acotada entre los vecinos (con margen) para que la curva siga siendo una funcion;
    // Y libre en 0..1.
    double minX = pts[_dragIndex - 1].X + 0.03, maxX = pts[_dragIndex + 1].X - 0.03;
    double x = Math.Clamp(pos.X / canvas.Width, minX, maxX);
    double y = Math.Clamp(1 - pos.Y / canvas.Height, 0.0, 1.0);
    pts[_dragIndex] = new CurvePoint(x, y);
    if (_dragIsLeft) RedrawLeftCurve(); else RedrawRightCurve();
}

private void CurveCanvas_Up(Canvas canvas)
{
    if (_dragIndex < 0) return;
    _dragIndex = -1;
    canvas.ReleaseMouseCapture();
    RememberRemap();   // persiste el dibujo (debounced, como todo _remap)
}

// Wrappers por stick (los que referencia el XAML):
private void LeftCurveCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    => CurveCanvas_Down(LeftCurveCanvas, _remap.LeftCurvePoints, _remap.LeftCurve, true, e);
private void LeftCurveCanvas_MouseMove(object sender, MouseEventArgs e)
    => CurveCanvas_Move(LeftCurveCanvas, _remap.LeftCurvePoints, e);
private void LeftCurveCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    => CurveCanvas_Up(LeftCurveCanvas);
private void RightCurveCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    => CurveCanvas_Down(RightCurveCanvas, _remap.RightCurvePoints, _remap.RightCurve, false, e);
private void RightCurveCanvas_MouseMove(object sender, MouseEventArgs e)
    => CurveCanvas_Move(RightCurveCanvas, _remap.RightCurvePoints, e);
private void RightCurveCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    => CurveCanvas_Up(RightCurveCanvas);
```

Integración: `RedrawLeftCurve()` termina llamando `RefreshCurveDots(LeftCurveCanvas, _leftCurveDots, _remap.LeftCurvePoints, _remap.LeftCurve);` (ídem el derecho) — así los marcadores siguen a la curva se redibuje por lo que se redibuje (cambio de combo, CARGAR perfil, arrastre). Si el nombre real del canvas derecho difiere (`RightCurveCanvas`), usar el del XAML.

- [ ] **Step 5: Prueba manual** — compilar y abrir:
  1. STICKS → RESPUESTA → "Editor": aparecen 3 puntos sobre la diagonal.
  2. Arrastrarlos: la curva se redibuja suave pasando por los puntos, sin salirse del cuadro.
  3. Cambiar a "Precisa" → los puntos se ocultan; volver a "Editor" → el dibujo sigue ahí.
  4. GUARDAR perfil, cambiar el dibujo, CARGAR → el dibujo guardado vuelve (y los combos/gráficas lo reflejan).
  5. Con el MANDO VIRTUAL activo y joy.cpl abierto: arrastrar un punto cambia la respuesta del stick **en vivo**.
- [ ] **Step 6: Commit** — `git add -u && git commit -m "feat: editor de curvas - puntos arrastrables sobre la grafica CURVA"`

---

### Task 6: Copys + verificación integral

**Files:**
- Modify: `README.md` (línea de "Configurar el mando": mencionar el editor de curvas)
- Modify: `HidusbfModernGui/MainWindow.xaml` (si algún texto de ayuda enumera las curvas, añadir "Editor")

- [ ] **Step 1** — README, en la viñeta de **Configurar el mando**, tras "curvas de respuesta por stick": añadir "(presets o una curva propia dibujada punto a punto)".
- [ ] **Step 2** — `dotnet test` completo: TODO verde. `.\package.ps1`: empaqueta sin warnings nuevos.
- [ ] **Step 3** — Prueba integral con hardware (usuario): luces con motor activo (Task 2), curva Editor en juego real.
- [ ] **Step 4: Commit** — `git add -u && git commit -m "docs: curva Editor en el README + verificacion integral"`

---

## Self-review

- **Cobertura:** bug de luces (Tasks 1–2, las dos capas del fallo), inventario de configuración (sección Mapa), editor de curvas de punta a punta (núcleo Task 3, persistencia/motor Task 4, UI Task 5, docs Task 6). ✓
- **Placeholders:** ninguno; todo step de código lleva el código. Los dos puntos deliberadamente abiertos están señalados con su alternativa concreta (opciones JSON del store en Task 4; nombre real del canvas derecho en Task 5). ✓
- **Tipos consistentes:** `CurvePoint(double X, double Y)` (Task 3) usado en `RemapSettings` (Task 4), `RemapEngine` (Task 4) y UI (Task 5); overload `ApplyStick(..., IReadOnlyList<CurvePoint>?)` definido en Task 3 y consumido en Tasks 4–5; `ResponseCurve.Propia` añadido al final del enum. ✓
- **Restricción de tests respetada:** los archivos nuevos del núcleo ya están linkeados (`ControllerState.cs`, `InputTransform.cs`, `RemapSettings.cs`, `RemapEngine.cs`, `RemapProfileStore.cs` están todos en el csproj de tests); no se linkea nada que toque Nefarius/WPF. ✓
