# Remapeador de mando (v1) — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps con checkbox (`- [ ]`).

**Goal:** Una pestaña "MANDO VIRTUAL" que lee el DualSense físico, aplica personalización (curvas/zonas muertas de stick, remapeo de botones, punto de disparo de gatillos, mapeo del touchpad por zonas) y la entrega a un DS4 virtual (ViGEm) mientras oculta el físico (HidHide) — con una UI simple que nunca muestra valores crudos.

**Architecture:** Tres capas. (1) Núcleo de transformación PURO y testeable (TDD). (2) Capa de E/S dependiente de hardware/drivers (lector HID + DS4 virtual ViGEm + HidHide), que arranca con un spike de-riesgo. (3) UI simplificada + perfiles JSON. Solo configuración de mando: sin macros, sin teclado/ratón, sin evasión.

**Tech Stack:** .NET 9, WPF, C#, xUnit. NuGets nuevos: `Nefarius.ViGEm.Client` (DS4 virtual, BSD-3), `Nefarius.Drivers.HidHide` (ocultar el físico), `HidSharp` (leer el físico). Drivers externos que el usuario instala: ViGEmBus, HidHide (ya instalados en la máquina del usuario: ViGEmBus 1.22.0, HidHide 1.5.230).

## Global Constraints

- .NET 9, x64, WPF. Lógica pura sin WPF (el proyecto de tests la enlaza por ruta).
- La UI NUNCA muestra valores crudos (deadzone 0.0–1.0, puntos Bézier, bytes). Solo controles con nombre claro, % y vista previa; lo confuso se defaultea y va en "Avanzado".
- NO copiar código de DS4Windows (GPLv3): reimplementar conceptos. ViGEm.Client es BSD-3.
- Drivers externos (ViGEmBus, HidHide) rompen el portable de un archivo; la app detecta y guía. El README debe decirlo.
- Sin macros, sin emulación de teclado/ratón, sin auto-aim, sin anti-retroceso, sin evasión de anticheat. Copy honesto de anticheat en la pestaña + README.
- Congelados (no tocar): `DualSenseLight.cs`, `LightProfile.cs`, `SystemManager.cs`, `PollingCore.cs`, `ColourRamp.cs`, `ColourMath.cs`, `RainbowWalker.cs`, `PlayerLedWalker.cs`, `LightIntent.cs`, `Theme.xaml`.
- Commits SIN `Co-Authored-By`. git: `git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com"`. Push lo hace el usuario.
- Carpeta: `C:\Users\Administrator\Downloads\work ultrapolling\UltraPolling`. Build de prueba: `bin\Debug\net9.0-windows\UltraPolling.exe`.

---

# FASE 1 — Núcleo de transformación (lógica pura, TDD)

Unidades: `ControllerState` (estado normalizado), `InputTransform` (deadzone/curva/gatillo/remapeo/touchpad), `RemapSettings` (traducción UI↔parámetros precisos), `RemapProfile` + `RemapProfileStore` (persistencia). Todo sin WPF, enlazado por ruta a los tests.

### Task 1: ControllerState + deadzone/curva de stick

**Files:**
- Create: `HidusbfModernGui/ControllerState.cs`
- Create: `HidusbfModernGui/InputTransform.cs`
- Test: `HidusbfModernGui.Tests/InputTransformStickTests.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj` (enlazar los 2 archivos nuevos)

**Interfaces:**
- Produces:
  - `record struct StickInput(double X, double Y)` con `X,Y` en −1..1.
  - `enum ResponseCurve { Precisa, Normal, Rapida }`
  - `static (double X, double Y) InputTransform.ApplyStick(StickInput s, double innerDeadzone, double outerDeadzone, ResponseCurve curve)` — deadzone radial + curva; salida en −1..1.

- [ ] **Step 1: Test que falla** — crear `InputTransformStickTests.cs`:

```csharp
using System;
using HidusbfModernGui;
using Xunit;

public class InputTransformStickTests
{
    [Fact]
    public void InsideInnerDeadzone_IsZero()
    {
        var (x, y) = InputTransform.ApplyStick(new StickInput(0.05, 0.0), 0.10, 1.0, ResponseCurve.Normal);
        Assert.Equal(0.0, x, 3);
        Assert.Equal(0.0, y, 3);
    }

    [Fact]
    public void AtOuterEdge_IsFullTilt()
    {
        var (x, y) = InputTransform.ApplyStick(new StickInput(1.0, 0.0), 0.10, 1.0, ResponseCurve.Normal);
        Assert.Equal(1.0, x, 2);
        Assert.Equal(0.0, y, 2);
    }

    [Fact]
    public void JustPastInnerDeadzone_StartsFromZero_NotAJump()
    {
        // Rescale: a magnitude just above the inner deadzone maps to near 0, not to a step.
        var (x, _) = InputTransform.ApplyStick(new StickInput(0.11, 0.0), 0.10, 1.0, ResponseCurve.Normal);
        Assert.True(x > 0.0 && x < 0.05);
    }

    [Fact]
    public void OuterDeadzone_ReachesFullBeforePhysicalMax()
    {
        // With outer deadzone 0.90, a 0.90 magnitude already means full output.
        var (x, _) = InputTransform.ApplyStick(new StickInput(0.90, 0.0), 0.0, 0.90, ResponseCurve.Normal);
        Assert.Equal(1.0, x, 2);
    }

    [Fact]
    public void PrecisaCurve_IsGentlerThanNormal_MidRange()
    {
        var (xp, _) = InputTransform.ApplyStick(new StickInput(0.5, 0.0), 0.0, 1.0, ResponseCurve.Precisa);
        var (xn, _) = InputTransform.ApplyStick(new StickInput(0.5, 0.0), 0.0, 1.0, ResponseCurve.Normal);
        Assert.True(xp < xn);   // más control fino en el centro
    }

    [Fact]
    public void RapidaCurve_IsSharperThanNormal_MidRange()
    {
        var (xr, _) = InputTransform.ApplyStick(new StickInput(0.5, 0.0), 0.0, 1.0, ResponseCurve.Rapida);
        var (xn, _) = InputTransform.ApplyStick(new StickInput(0.5, 0.0), 0.0, 1.0, ResponseCurve.Normal);
        Assert.True(xr > xn);
    }

    [Fact]
    public void Direction_IsPreserved_OnDiagonal()
    {
        var (x, y) = InputTransform.ApplyStick(new StickInput(0.6, 0.6), 0.1, 1.0, ResponseCurve.Normal);
        Assert.Equal(x, y, 3);   // 45° se mantiene 45°
    }
}
```

- [ ] **Step 2: Correr y ver fallar** — `dotnet test HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj -v q` → compile error.

- [ ] **Step 3: Crear `ControllerState.cs`**:

```csharp
namespace HidusbfModernGui
{
    // Estado normalizado del mando, independiente del hardware: sticks y gatillos en
    // punto flotante, botones por bandera. La capa de E/S traduce el reporte HID crudo
    // a esto, y el nucleo de transformacion trabaja solo con esto.
    public readonly record struct StickInput(double X, double Y);   // -1..1

    public enum ResponseCurve { Precisa, Normal, Rapida }

    // Botones del mando (superset DS4/DualSense). El remapeo mapea de uno a otro.
    public enum PadButton
    {
        None,
        Cross, Circle, Square, Triangle,
        DpadUp, DpadDown, DpadLeft, DpadRight,
        L1, R1, L2, R2, L3, R3,
        Share, Options, PS, TouchpadClick
    }

    public sealed class ControllerState
    {
        public StickInput Left { get; set; }
        public StickInput Right { get; set; }
        public double L2 { get; set; }   // 0..1
        public double R2 { get; set; }   // 0..1
        public System.Collections.Generic.HashSet<PadButton> Pressed { get; set; } = new();
        // Touchpad: coordenadas crudas del primer toque y si hay toque.
        public bool TouchActive { get; set; }
        public int TouchX { get; set; }  // 0..1920 aprox
        public int TouchY { get; set; }  // 0..1080 aprox
    }
}
```

- [ ] **Step 4: Crear `InputTransform.cs` con `ApplyStick`**:

```csharp
using System;

namespace HidusbfModernGui
{
    // Transformaciones puras del input. Sin WPF, sin hardware: entra un valor, sale otro.
    public static class InputTransform
    {
        // Deadzone radial (por magnitud, no por eje, para no deformar diagonales) + reescalado
        // entre inner y outer + curva de respuesta. Entrada y salida en -1..1.
        public static (double X, double Y) ApplyStick(StickInput s, double innerDeadzone,
                                                       double outerDeadzone, ResponseCurve curve)
        {
            double mag = Math.Sqrt(s.X * s.X + s.Y * s.Y);
            if (mag <= innerDeadzone || mag <= 0.0) return (0.0, 0.0);

            double outer = Math.Max(outerDeadzone, innerDeadzone + 1e-6);
            // Reescala [inner, outer] -> [0, 1].
            double t = (mag - innerDeadzone) / (outer - innerDeadzone);
            t = Math.Clamp(t, 0.0, 1.0);
            t = ApplyCurve(t, curve);

            double ux = s.X / mag, uy = s.Y / mag;   // direccion unitaria (preserva el angulo)
            return (ux * t, uy * t);
        }

        // Curva de respuesta como exponente sobre la magnitud normalizada. >1 = mas control
        // fino cerca del centro (Precisa); <1 = mas agresivo (Rapida); 1 = lineal (Normal).
        private static double ApplyCurve(double t, ResponseCurve curve) => curve switch
        {
            ResponseCurve.Precisa => Math.Pow(t, 1.8),
            ResponseCurve.Rapida  => Math.Pow(t, 0.6),
            _                     => t,
        };
    }
}
```

- [ ] **Step 5: Enlazar en el csproj de tests** — añadir junto a los otros `<Compile Include>`:
```xml
    <Compile Include="..\HidusbfModernGui\ControllerState.cs" Link="ControllerState.cs" />
    <Compile Include="..\HidusbfModernGui\InputTransform.cs" Link="InputTransform.cs" />
```

- [ ] **Step 6: Correr y ver pasar** — `dotnet test ... -v q` → todos verdes (252 previos + 7 nuevos = 259).

- [ ] **Step 7: Commit**
```bash
git add HidusbfModernGui/ControllerState.cs HidusbfModernGui/InputTransform.cs HidusbfModernGui.Tests/InputTransformStickTests.cs HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj
git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "feat: nucleo de transformacion de stick (deadzone radial + curva, TDD)"
```

### Task 2: Gatillos, remapeo y zonas del touchpad

**Files:**
- Modify: `HidusbfModernGui/InputTransform.cs`
- Test: `HidusbfModernGui.Tests/InputTransformMapTests.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj` (enlazar el test nuevo)

**Interfaces:**
- Produces:
  - `static double InputTransform.ApplyTrigger(double value, double triggerPoint)` — value 0..1, triggerPoint 0..1 (a qué recorrido cuenta como "a fondo"); devuelve 0..1 reescalado (hair trigger).
  - `static PadButton InputTransform.Remap(PadButton pressed, IReadOnlyDictionary<PadButton,PadButton> table)` — devuelve el destino o el mismo si no hay entrada.
  - `enum TouchZone { None, ArribaIzq, ArribaDer, AbajoIzq, AbajoDer }`
  - `static TouchZone InputTransform.ResolveTouchZone(bool touched, int x, int y, int xSplit, int ySplit)` — divide el touchpad en 4 por los cortes X/Y.

- [ ] **Step 1: Test que falla** — crear `InputTransformMapTests.cs`:

```csharp
using System.Collections.Generic;
using HidusbfModernGui;
using Xunit;

public class InputTransformMapTests
{
    [Fact]
    public void Trigger_BelowPoint_IsZero()
        => Assert.Equal(0.0, InputTransform.ApplyTrigger(0.10, 0.20), 3);

    [Fact]
    public void Trigger_AtPoint_IsFull()
        => Assert.Equal(1.0, InputTransform.ApplyTrigger(0.20, 0.20), 3);

    [Fact]
    public void Trigger_AbovePoint_StaysFull()
        => Assert.Equal(1.0, InputTransform.ApplyTrigger(0.9, 0.20), 3);

    [Fact]
    public void Trigger_PointZero_IsLinearPassthrough()
        => Assert.Equal(0.5, InputTransform.ApplyTrigger(0.5, 0.0), 3);

    [Fact]
    public void Remap_SwapsWhenMapped()
    {
        var table = new Dictionary<PadButton, PadButton> { [PadButton.Cross] = PadButton.Square };
        Assert.Equal(PadButton.Square, InputTransform.Remap(PadButton.Cross, table));
        Assert.Equal(PadButton.Circle, InputTransform.Remap(PadButton.Circle, table)); // sin entrada: igual
    }

    [Theory]
    [InlineData(100, 100, TouchZone.ArribaIzq)]
    [InlineData(1800, 100, TouchZone.ArribaDer)]
    [InlineData(100, 1000, TouchZone.AbajoIzq)]
    [InlineData(1800, 1000, TouchZone.AbajoDer)]
    public void TouchZone_SplitsIntoFourQuadrants(int x, int y, TouchZone expected)
        => Assert.Equal(expected, InputTransform.ResolveTouchZone(true, x, y, 960, 540));

    [Fact]
    public void TouchZone_NotTouched_IsNone()
        => Assert.Equal(TouchZone.None, InputTransform.ResolveTouchZone(false, 100, 100, 960, 540));
}
```

- [ ] **Step 2: Correr y ver fallar.**

- [ ] **Step 3: Añadir a `InputTransform.cs`**:

```csharp
        // Hair trigger: por debajo del punto = 0; en el punto o mas = reescala [point,1] a [0,1],
        // con point==0 como passthrough lineal.
        public static double ApplyTrigger(double value, double triggerPoint)
        {
            double p = Math.Clamp(triggerPoint, 0.0, 0.99);
            if (p <= 0.0) return Math.Clamp(value, 0.0, 1.0);
            if (value < p) return 0.0;
            return Math.Clamp((value - p) / (1.0 - p), 0.0, 1.0);
        }

        public static PadButton Remap(PadButton pressed,
            System.Collections.Generic.IReadOnlyDictionary<PadButton, PadButton> table)
            => table != null && table.TryGetValue(pressed, out var to) ? to : pressed;

        public static TouchZone ResolveTouchZone(bool touched, int x, int y, int xSplit, int ySplit)
        {
            if (!touched) return TouchZone.None;
            bool left = x < xSplit, top = y < ySplit;
            return (top, left) switch
            {
                (true, true)   => TouchZone.ArribaIzq,
                (true, false)  => TouchZone.ArribaDer,
                (false, true)  => TouchZone.AbajoIzq,
                (false, false) => TouchZone.AbajoDer,
            };
        }
```
Y añadir `public enum TouchZone { None, ArribaIzq, ArribaDer, AbajoIzq, AbajoDer }` en `ControllerState.cs`.

- [ ] **Step 4: Enlazar el test** en el csproj y correr → verde (259 + 8 = 267).

- [ ] **Step 5: Commit** — `git ... commit -m "feat: gatillo (hair trigger), remapeo y zonas del touchpad (TDD)"`

### Task 3: Traducción UI↔parámetros (RemapSettings)

**Files:**
- Create: `HidusbfModernGui/RemapSettings.cs`
- Test: `HidusbfModernGui.Tests/RemapSettingsTests.cs` (+ enlazar en csproj)

**Interfaces:**
- Produces: `sealed class RemapSettings` con los valores AMIGABLES de la UI + su conversión a los parámetros precisos que consume `InputTransform`:
  - `int LeftDeadzonePct` (0–30) → `LeftInnerDeadzone` (0.0–0.30)
  - `int LeftReachPct` (70–100, avanzado) → `LeftOuterDeadzone`
  - `ResponseCurve LeftCurve`; ídem Right.
  - `int L2PointPct`,`int R2PointPct` (0–100) → `TriggerPoint` 0.0–1.0
  - `Dictionary<PadButton,PadButton> ButtonRemap`
  - `Dictionary<TouchZone,PadButton> TouchZoneMap`
  - Getters derivados: `LeftInnerDeadzone`, `LeftOuterDeadzone`, `L2Point`, etc.

- [ ] **Step 1: Test que falla** — crear `RemapSettingsTests.cs`:

```csharp
using HidusbfModernGui;
using Xunit;

public class RemapSettingsTests
{
    [Fact]
    public void Defaults_AreNeutral()
    {
        var s = new RemapSettings();
        Assert.Equal(0.0, s.LeftInnerDeadzone, 3);   // 0% -> 0.0
        Assert.Equal(1.0, s.LeftOuterDeadzone, 3);   // 100% -> 1.0
        Assert.Equal(ResponseCurve.Normal, s.LeftCurve);
        Assert.Equal(0.0, s.L2Point, 3);
    }

    [Fact]
    public void PercentagesConvertToFractions()
    {
        var s = new RemapSettings { LeftDeadzonePct = 15, LeftReachPct = 90, L2PointPct = 20 };
        Assert.Equal(0.15, s.LeftInnerDeadzone, 3);
        Assert.Equal(0.90, s.LeftOuterDeadzone, 3);
        Assert.Equal(0.20, s.L2Point, 3);
    }

    [Fact]
    public void PercentagesClampToUiRanges()
    {
        var s = new RemapSettings { LeftDeadzonePct = 99, LeftReachPct = 10 };
        Assert.Equal(0.30, s.LeftInnerDeadzone, 3);   // tope 30%
        Assert.Equal(0.70, s.LeftOuterDeadzone, 3);   // piso 70%
    }
}
```

- [ ] **Step 2: Correr y ver fallar.**

- [ ] **Step 3: Crear `RemapSettings.cs`**:

```csharp
using System;
using System.Collections.Generic;

namespace HidusbfModernGui
{
    // Los valores AMIGABLES que ve el usuario (en %, preajustes) y su conversion a los
    // parametros precisos que consume InputTransform. La UI edita esto; el motor lee los
    // getters derivados. Clase mutable con props settables para round-trip de System.Text.Json.
    public sealed class RemapSettings
    {
        // Sticks (izquierdo)
        public int LeftDeadzonePct { get; set; } = 0;    // 0..30
        public int LeftReachPct { get; set; } = 100;     // 70..100 (avanzado)
        public ResponseCurve LeftCurve { get; set; } = ResponseCurve.Normal;
        // Sticks (derecho)
        public int RightDeadzonePct { get; set; } = 0;
        public int RightReachPct { get; set; } = 100;
        public ResponseCurve RightCurve { get; set; } = ResponseCurve.Normal;
        // Gatillos
        public int L2PointPct { get; set; } = 0;         // 0..100
        public int R2PointPct { get; set; } = 0;
        // Remapeo y touchpad
        public Dictionary<PadButton, PadButton> ButtonRemap { get; set; } = new();
        public Dictionary<TouchZone, PadButton> TouchZoneMap { get; set; } = new();

        public double LeftInnerDeadzone  => Math.Clamp(LeftDeadzonePct, 0, 30) / 100.0;
        public double LeftOuterDeadzone  => Math.Clamp(LeftReachPct, 70, 100) / 100.0;
        public double RightInnerDeadzone => Math.Clamp(RightDeadzonePct, 0, 30) / 100.0;
        public double RightOuterDeadzone => Math.Clamp(RightReachPct, 70, 100) / 100.0;
        public double L2Point => Math.Clamp(L2PointPct, 0, 100) / 100.0;
        public double R2Point => Math.Clamp(R2PointPct, 0, 100) / 100.0;
    }
}
```

- [ ] **Step 4: Enlazar + correr** → verde (267 + 3 = 270).
- [ ] **Step 5: Commit** — `git ... commit -m "feat: RemapSettings (traduccion UI amigable a parametros precisos, TDD)"`

### Task 4: RemapProfile + RemapProfileStore (persistencia, TDD)

**Files:**
- Create: `HidusbfModernGui/RemapProfileStore.cs`
- Test: `HidusbfModernGui.Tests/RemapProfileStoreTests.cs` (+ enlazar)

**Interfaces:**
- Produces: `class RemapProfile { string Name; RemapSettings Settings; }` y `static RemapProfileStore` con `Load()/Save(list)` sobre `%APPDATA%\UltraPolling\remap-profiles.json`, patrón atómico + `.backup`, `JsonStringEnumConverter`, `OverrideDirectoryForTests`. Espejo exacto de `IntentStore`/`ProfileStore`.

- [ ] **Step 1: Test que falla** — round-trip de un perfil con deadzone/curva/remap; enums como nombre; archivo corrupto → lista vacía; backup al sobrescribir. (Copiar el patrón de `LightIntentTests`/`ProfileStoreTests`, usando `RemapProfileStore.OverrideDirectoryForTests`.)

```csharp
using System.Collections.Generic;
using System.IO;
using System;
using HidusbfModernGui;
using Xunit;

public class RemapProfileStoreTests : IDisposable
{
    private readonly string _dir;
    public RemapProfileStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "UPRemapTests_" + Guid.NewGuid().ToString("N"));
        RemapProfileStore.OverrideDirectoryForTests(_dir);
    }
    public void Dispose()
    {
        RemapProfileStore.OverrideDirectoryForTests(null);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void RoundTrips()
    {
        var p = new RemapProfile { Name = "FPS", Settings = new RemapSettings {
            LeftDeadzonePct = 12, LeftCurve = ResponseCurve.Precisa, L2PointPct = 25,
            ButtonRemap = new() { [PadButton.Cross] = PadButton.R1 } } };
        Assert.True(RemapProfileStore.Save(new[] { p }).Success);
        var loaded = RemapProfileStore.Load();
        Assert.Single(loaded);
        Assert.Equal("FPS", loaded[0].Name);
        Assert.Equal(12, loaded[0].Settings.LeftDeadzonePct);
        Assert.Equal(ResponseCurve.Precisa, loaded[0].Settings.LeftCurve);
        Assert.Equal(PadButton.R1, loaded[0].Settings.ButtonRemap[PadButton.Cross]);
    }

    [Fact]
    public void CorruptFile_ReturnsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(RemapProfileStore.Path, "{ not json");
        Assert.Empty(RemapProfileStore.Load());
    }

    [Fact]
    public void Save_CreatesBackupOnOverwrite()
    {
        RemapProfileStore.Save(new[] { new RemapProfile { Name = "a", Settings = new() } });
        RemapProfileStore.Save(new[] { new RemapProfile { Name = "b", Settings = new() } });
        Assert.True(File.Exists(RemapProfileStore.Path + ".backup"));
    }
}
```

- [ ] **Step 2: Correr y ver fallar.**
- [ ] **Step 3: Crear `RemapProfileStore.cs`** — copiar la estructura de `IntentStore` (mismo `Directory_`, `OverrideDirectoryForTests`, `Options` con `WriteIndented` + `JsonStringEnumConverter`, `Load`/`Save` con copia a `.backup`), con `RemapProfile { public string Name {get;set;}=""; public RemapSettings Settings {get;set;}=new(); }` y `Path = ...\remap-profiles.json`. Load devuelve `List<RemapProfile>`.
- [ ] **Step 4: Enlazar + correr** → verde (270 + 3 = 273).
- [ ] **Step 5: Commit** — `git ... commit -m "feat: RemapProfile + RemapProfileStore (perfiles del remapeador, TDD)"`

---

# FASE 2 — Capa de E/S (hardware/drivers; verificación manual)

> **Nota honesta:** esta fase depende de librerías externas (ViGEm.Client, HidHide, HidSharp) y del layout real del reporte HID del DualSense. Por eso arranca con un SPIKE que fija en hardware real las APIs y los offsets antes de construir encima. Los "expected" aquí son comprobaciones manuales, no asserts.

### Task 5: Dependencias + detección de drivers

**Files:**
- Modify: `HidusbfModernGui/HidusbfModernGui.csproj` (PackageReference)
- Create: `HidusbfModernGui/DriverCheck.cs`
- Modify: `Portable.pubxml` / `package.ps1` — solo verificar que el single-file sigue OK con los NuGets nuevos.

- [ ] **Step 1: Añadir los NuGets** al csproj:
```xml
    <PackageReference Include="Nefarius.ViGEm.Client" Version="1.21.256" />
    <PackageReference Include="Nefarius.Drivers.HidHide" Version="1.16.107" />
    <PackageReference Include="HidSharp" Version="2.1.0" />
```
(Verificar las últimas versiones estables en nuget.org al implementar; fijar la que resuelva.)

- [ ] **Step 2: `DriverCheck.cs`** — detectar ViGEmBus y HidHide instalados. Both instalan un servicio/driver; comprobar por la existencia del servicio (`ViGEmBus`) y del dispositivo/servicio de HidHide (`HidHide`). Método `static (bool vigem, bool hidhide) DriverCheck.Detect()` consultando `ServiceController.GetDevices()`/registro. (Reusar el estilo de consulta de servicios ya presente en el proyecto.)

- [ ] **Step 3: Build + verificar single-file** — `powershell -File package.ps1`; confirmar que `dist\UltraPolling\UltraPolling.exe` sigue siendo un solo archivo >40MB y arranca. Los NuGets son manejados; deben quedar dentro del bundle.

- [ ] **Step 4: Commit** — `git ... commit -m "chore: dependencias ViGEm/HidHide/HidSharp + deteccion de drivers"`

### Task 6: SPIKE — passthrough físico→virtual con HidHide

**Files:**
- Create: `HidusbfModernGui/DualSenseReader.cs`, `HidusbfModernGui/VirtualPad.cs`, `HidusbfModernGui/HidHideControl.cs` (esqueletos que este spike llena)

Objetivo del spike: **probar en hardware real** que se puede (a) leer el DualSense físico y parsear sticks/gatillos/botones/touchpad, (b) crear un DS4 virtual con ViGEm y reflejar el estado, (c) ocultar el físico con HidHide dejando la app en lista blanca. Al terminar, los offsets del reporte y las firmas exactas de las APIs quedan FIJADOS para las tareas siguientes.

- [ ] **Step 1: `DualSenseReader`** — con HidSharp, abrir el HID del DualSense (VID_054C) por USB, leer input reports en un hilo, y parsear a `ControllerState`. Offsets de partida para el reporte USB 0x01 (verificar con un volcado real): LX=1, LY=2, RX=3, RY=4, L2=5, R2=6, botones cara/dpad=8, L1/R1/L2/R2/share/options/L3/R3=9, PS/touchpad-click=10, touchpad primer punto ~ bytes 33–36 (X = b34 | ((b35 & 0x0F)<<8), Y = ((b35 & 0xF0)>>4) | (b36<<4), touch activo = (b33 & 0x80)==0). **Confirmar con un volcado real antes de fiarse.**
- [ ] **Step 2: `VirtualPad`** — con `Nefarius.ViGEm.Client`: `new ViGEmClient()`, `CreateDualShock4Controller()`, `Connect()`, y por frame setear botones/ejes/gatillos y `SubmitReport()`. Fijar los nombres exactos de los métodos de report DS4 de la versión instalada.
- [ ] **Step 3: `HidHideControl`** — con `Nefarius.Drivers.HidHide`: añadir la ruta del exe a la lista blanca, añadir el instance path del DualSense a la lista de ocultos, y activar el ocultamiento. Al desactivar, revertir.
- [ ] **Step 4: Prueba manual (el spike)** — un modo temporal (botón oculto o arranque con argumento) que: oculta el físico, crea el virtual, y copia el estado 1:1. Verificar en "Configurar dispositivos de juego USB" (joy.cpl) que aparece un **Wireless Controller virtual** que responde, y que el físico ya **no** aparece para otras apps. Confirmar que la luz del físico sigue controlándose desde UltraPolling (lista blanca OK).
- [ ] **Step 5: Commit** — `git ... commit -m "spike: passthrough DualSense fisico -> DS4 virtual con HidHide (offsets/APIs fijados)"`

### Task 7: RemapEngine — meter la transformación en el lazo

**Files:**
- Create: `HidusbfModernGui/RemapEngine.cs`

- [ ] **Step 1** — `RemapEngine` con `Start(RemapSettings)`, `Stop()`, `Update(RemapSettings)`. Lazo: leer `ControllerState` del reader → aplicar `InputTransform` (ApplyStick por stick, ApplyTrigger por gatillo, Remap por botón, ResolveTouchZone→botón virtual) usando los getters de `RemapSettings` → empujar al `VirtualPad`. Start también activa HidHide; Stop lo revierte y desconecta el virtual (estado limpio, físico visible).
- [ ] **Step 2: Prueba manual** — activar con un perfil que, p.ej., intercambie Cross↔Square y ponga deadzone 15% + curva Precisa; verificar en joy.cpl que el virtual refleja la transformación.
- [ ] **Step 3: Commit** — `git ... commit -m "feat: RemapEngine (fisico -> transformar -> DS4 virtual)"`

---

# FASE 3 — UI simplificada + perfiles

Pestaña nueva como **UserControl** (`RemapPage.xaml`) para no inflar más `MainWindow`. Nada de valores crudos.

### Task 8: Pestaña "MANDO VIRTUAL" + activar/desactivar + guía de drivers

**Files:**
- Create: `HidusbfModernGui/RemapPage.xaml` + `.xaml.cs`
- Modify: `HidusbfModernGui/MainWindow.xaml` (ícono en el sidebar + host de la página), `MainWindow.xaml.cs` (navegación)

- [ ] **Step 1** — añadir un botón al sidebar (ícono gamepad-2 o similar de la paleta) que muestra `RemapPage`. La página: si `DriverCheck.Detect()` reporta faltantes, muestra una tarjeta con guía de instalación (enlaces a ViGEmBus/HidHide) y desactiva el resto. Si están, muestra el interruptor **Activar mando virtual** (llama `RemapEngine.Start/Stop`) y un aviso honesto de anticheat (texto del spec).
- [ ] **Step 2: Build + verificación manual** — la pestaña aparece; con drivers presentes muestra el interruptor; activar/desactivar arranca/para el engine.
- [ ] **Step 3: Commit** — `git ... commit -m "feat: pestana MANDO VIRTUAL (activar/desactivar + guia de drivers + aviso honesto)"`

### Task 9: UI de sticks (zona muerta + respuesta + vista previa de curva)

**Files:** Modify `RemapPage.xaml(.cs)`

- [ ] **Step 1** — por cada stick: slider "Zona muerta" (0–30%), 3 botones "Respuesta" (Precisa/Normal/Rápida), y una gráfica de la curva en vivo (dibujar `InputTransform.ApplyStick` sobre 0..1 en un Canvas/Path). "Alcance" (70–100%) va en un expander "Avanzado". Cada cambio actualiza `RemapSettings` y `RemapEngine.Update`.
- [ ] **Step 2: Verificación manual** — mover los controles cambia la curva dibujada y el comportamiento del virtual.
- [ ] **Step 3: Commit** — `git ... commit -m "feat: UI de sticks (zona muerta + respuesta + vista previa de curva)"`

### Task 10: UI de gatillos + remapeo de botones

**Files:** Modify `RemapPage.xaml(.cs)`

- [ ] **Step 1** — Gatillos: dos sliders "Punto de disparo" (0–100%) con barra visual. Botones: un diagrama del mando (o lista) donde cada botón abre un desplegable con los destinos (`PadButton`), escribiendo en `RemapSettings.ButtonRemap`.
- [ ] **Step 2: Verificación manual** — un punto de disparo bajo hace el gatillo casi digital; un remapeo intercambia botones en el virtual.
- [ ] **Step 3: Commit** — `git ... commit -m "feat: UI de gatillos (punto de disparo) y remapeo de botones"`

### Task 11: UI del touchpad por zonas

**Files:** Modify `RemapPage.xaml(.cs)`

- [ ] **Step 1** — una rejilla 2×2 que representa el touchpad; cada zona (Arriba-Izq/Der, Abajo-Izq/Der) abre un desplegable de `PadButton`, escribiendo en `RemapSettings.TouchZoneMap`. El engine ya resuelve la zona→botón (Task 7 / InputTransform.ResolveTouchZone).
- [ ] **Step 2: Verificación manual** — tocar cada cuadrante dispara el botón asignado en el virtual.
- [ ] **Step 3: Commit** — `git ... commit -m "feat: UI del touchpad por zonas (4 cuadrantes -> boton)"`

### Task 12: Perfiles (guardar/cargar) + persistencia + vista previa en vivo

**Files:** Modify `RemapPage.xaml(.cs)`

- [ ] **Step 1** — barra de perfiles (desplegable + GUARDAR/CARGAR/BORRAR) sobre `RemapProfileStore`; al cargar, reconstruir los controles desde `RemapSettings`. Guardar el perfil activo al cambiar (debounce, como la luz). Una vista previa en vivo: leer el `ControllerState` post-transformación del engine y dibujar sticks/gatillos/botones para que el usuario vea el efecto sin abrir un juego.
- [ ] **Step 2: Verificación manual** — guardar un perfil, cambiarlo, cargarlo; cerrar y reabrir mantiene el perfil activo.
- [ ] **Step 3: Commit** — `git ... commit -m "feat: perfiles del remapeador + vista previa en vivo"`

### Task 13: Copy honesto de anticheat en el README

**Files:** Modify `README.md`

- [ ] **Step 1** — sección nueva que explique el remapeador: qué hace, que requiere ViGEmBus + HidHide (se instalan aparte, rompe el portable), y el aviso honesto de anticheat (mecanismo detectable aunque no hagas trampa; ideal para un jugador / sin anticheat; online bajo tu riesgo; sin macros/KB-M/evasión).
- [ ] **Step 2: Commit** — `git ... commit -m "docs: seccion del remapeador en el README (requisitos + anticheat honesto)"`

### Task 14: Interruptor maestro + estado del mando (físico / virtual / HidHide)

**Files:** Modify `HidusbfModernGui/MainWindow.xaml` (el `ConfigPanel` del hub + la barra de estado del header), `MainWindow.xaml.cs`. Depende del motor: `RemapEngine` (Task 7), `DriverCheck` (Task 5), y el estado de `HidHideControl`/`DualSenseReader`/`VirtualPad`.

Requisito del usuario: tiene que quedar clarísimo si la configuración está activa, qué mando ve el juego (físico o virtual), y si el físico está oculto — y poder volver al **mando nativo** en un clic.

- [ ] **Step 1: Interruptor maestro (default APAGADO = nativo).** En `ConfigPanel`, arriba del todo, un switch grande **"CONFIGURACIÓN DEL MANDO"** con estados **DESACTIVADA / ACTIVADA**. Por defecto **DESACTIVADA**: no se crea el virtual, no se oculta el físico, ViGEm/HidHide no actúan → el juego ve tu **DualSense nativo** (estado limpio y el más seguro para anticheat). ACTIVADA → `RemapEngine.Start(_remap)` (oculta el físico con HidHide + crea el DS4 virtual con tu config). Al pasar a DESACTIVADA → `RemapEngine.Stop()` que **revierte del todo**: muestra el físico, quita el virtual, mando nativo al instante. El switch nunca arranca solo; siempre lo activa el usuario.

- [ ] **Step 2: Panel "ESTADO DEL MANDO"** con indicadores en vivo (punto + texto, estilo `StatusDot`/paleta), actualizados por un timer de UI o por eventos del engine:
  - **Físico:** `Conectado` / `No detectado`, y `Visible` / `Oculto (HidHide)`.
  - **Virtual:** `Activo` / `Inactivo`.
  - **Lo que ve el juego:** una línea resumen — `MANDO NATIVO` (cuando está DESACTIVADA) o `MANDO VIRTUAL (tu config)` (cuando ACTIVADA).
  Estos indicadores leen el estado real (`HidHideControl` sabe si el físico está oculto; `VirtualPad` si el virtual está conectado; `DualSenseReader`/`DriverCheck` si hay físico) — no un flag optimista.

- [ ] **Step 3: Indicador en el header (visible desde cualquier pestaña).** Un badge pequeño en la barra de estado superior: **"MANDO: NATIVO"** (gris, `TextLabelBrush`) cuando la config está desactivada, o **"MANDO: VIRTUAL"** (acento) cuando está activa. Objetivo de seguridad: que nunca lo dejes activado sin darte cuenta antes de una partida con anticheat.

- [ ] **Step 4: Comunicación explícita con HidHide + aviso al usuario.** `HidHideControl` habla con el driver vía el servicio de `Nefarius.Drivers.HidHide` (leer/escribir `IsActive`, `AddApplicationPath`/whitelist, `AddBlockedInstanceId`/ocultos, y **leer** las listas y el flag). Al ACTIVAR: (1) meter la ruta del exe en la lista blanca, (2) añadir el instance path del DualSense a ocultos, (3) `IsActive = true`; luego **releer el estado real** y avisar en la UI/log: *"HidHide ACTIVO — físico oculto; el juego usa el mando virtual"*. Al DESACTIVAR: quitar el dispositivo de ocultos / `IsActive = false`, releer, y avisar *"físico visible — mando nativo"*. El indicador de estado (Step 2) SIEMPRE lee lo que HidHide reporta, no un flag propio (así detecta también un estado dejado por un crash o por otra app).
  - **HidHide NO es un botón suelto.** Ocultar el físico sin crear el virtual dejaría al juego **sin ningún mando** (estado roto). Por eso el ocultamiento va SIEMPRE acoplado al interruptor maestro: ACTIVAR = ocultar físico **y** crear virtual (en ese orden: primero virtual listo, luego ocultar); DESACTIVAR = mostrar físico **y** quitar virtual. La UI no ofrece ocultar el físico por separado.
  - **Guard de arranque:** si `RemapEngine.Stop()` o el cierre de la app fallaran al revertir (o hubo un crash con la config activa), en `Window_Loaded` **re-mostrar el físico** (quitar nuestro dispositivo de la lista de ocultos de HidHide) para no dejar el mando "desaparecido". Este guard consulta el estado real de HidHide al abrir.

- [ ] **Step 5: Verificación manual (hardware).** Con el mando conectado: por defecto el header dice NATIVO y `joy.cpl` muestra tu DualSense real. Activar → header VIRTUAL, `joy.cpl` muestra el DS4 virtual y el físico desaparece para otras apps, el panel de estado lo refleja. Desactivar → vuelve el físico al instante, header NATIVO. Cerrar la app con la config activa y reabrir → el físico debe estar visible de nuevo (guard de arranque).

- [ ] **Step 6: Commit** — `git ... commit -m "feat: interruptor maestro + estado del mando (fisico/virtual/HidHide) con default nativo"`

---

## Self-Review

**Cobertura del spec:** núcleo puro (deadzone/curva/gatillo/remapeo/touchpad) → Tasks 1–2; traducción UI↔parámetros → Task 3; persistencia → Task 4; deps+detección → Task 5; E/S (lector/virtual/HidHide) → Tasks 6–7; UI simple (sticks/gatillos/botones/touchpad/perfiles/vista previa) → Tasks 8–12; anticheat honesto → Task 8 (UI) + Task 13 (README); **interruptor maestro + estado físico/virtual/HidHide + default nativo + indicador en header → Task 14**; coexistencia luz/overclock → vía lista blanca de HidHide (Task 6). Alcance v1 = sticks+botones+touchpad+gatillos. ✓

**Nota de UI:** la pestaña/UI del remapeador (Fase 3 original, Tasks 8–12) fue reemplazada por el plan `2026-07-18-controller-ui-restructure.md` (hub Configurar/Luces). El interruptor maestro + estado del mando (Task 14) viven en ese `ConfigPanel`. El plan de curvas `2026-07-18-stick-curves.md` añade Dinámica/Digital. Este plan sigue siendo la fuente del **motor** (Fase 2: Tasks 5–7) y de la Task 14.

**Placeholders:** la Fase 1 tiene código completo. La Fase 2/3 es hardware/driver-dependiente: se marca explícitamente como spike-first + verificación manual, con los puntos exactos de API/offsets a confirmar contra la librería/dispositivo real (no es un placeholder, es la naturaleza del código que toca hardware externo).

**Consistencia de tipos:** `ControllerState`, `StickInput`, `ResponseCurve`, `PadButton`, `TouchZone` (Task 1–2) usados por `RemapSettings` (Task 3), `RemapProfile` (Task 4), `RemapEngine` (Task 7) y la UI. `InputTransform.ApplyStick/ApplyTrigger/Remap/ResolveTouchZone` con las firmas de Tasks 1–2 consumidas en Task 7. `RemapSettings` getters (`LeftInnerDeadzone`, `L2Point`, ...) usados por el engine.

**Riesgo señalado:** los offsets del reporte DualSense y las firmas exactas de ViGEm.Client/HidHide se FIJAN en el spike (Task 6) antes de construir encima; si el spike revela algo distinto, ajustar Tasks 7+ en consecuencia. La UI grande justifica un `UserControl` aparte (no inflar `MainWindow`).

## Execution Handoff

Plan guardado en `docs/superpowers/plans/2026-07-18-controller-remapper.md`. Opciones:
1. **Subagentes (recomendado)** — un agente por tarea, revisión entre cada una.
2. **Inline** — tarea por tarea con checkpoints.
