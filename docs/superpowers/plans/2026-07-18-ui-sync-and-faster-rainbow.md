# Sincronizar la UI con lo guardado + rainbow hasta 180/s — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** (1) Que al reabrir la app la INTERFAZ muestre el player/color/brillo/efecto guardados (hoy la luz se restaura en el mando pero la UI se queda en Player 1). (2) Subir la velocidad del rainbow hasta 180 colores/s de forma honesta.

**Architecture:** Dos mejoras. La velocidad se logra separando "cuadros/s" (tope real 64, el reloj de Windows) de "colores/s" (hasta 180): a ≤64/s se muestra cada color (1 por cuadro); por encima, el walker avanza varios colores por cuadro (fraccional, acumulado) — sigue suave porque colores seguidos difieren en ≤1. La sincronía de UI se hace inicializando los controles de la pestaña del mando desde la intención guardada.

**Tech Stack:** .NET 9, WPF, C#, xUnit.

## Global Constraints

- .NET 9, x64, WPF. Sin dependencias NuGet nuevas.
- Archivos de lógica pura sin WPF (el proyecto de tests los enlaza por ruta): `RainbowWalker.cs`, `LightIntent.cs`, `ColourRamp.cs`, `ColourMath.cs`.
- **Este plan SÍ modifica `RainbowWalker.cs` y `LightIntent.cs`** (son el objeto de la mejora; el "congelado" de planes anteriores no aplica aquí). Siguen congelados: `DualSenseLight.cs`, `LightProfile.cs`, `ProfileStore`, `SystemManager.cs`, `PollingCore.cs`, `ColourRamp.cs`, `ColourMath.cs`, `Theme.xaml`.
- Honestidad del label: nunca mostrar un número que el sistema no entrega. A ≤64/s se muestra cada color; >64/s avanza varios por cuadro (indicarlo).
- Paleta de exactamente diez colores; sin color nuevo.
- **Commits SIN `Co-Authored-By`.** git identity: `git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com"`.
- Carpeta de trabajo: `C:\Users\Administrator\Downloads\work ultrapolling\UltraPolling`. El `git push` lo hace el usuario.
- Para probar: `dotnet build` deja el exe en `HidusbfModernGui\bin\Debug\net9.0-windows\HidusbfModernGui.exe` (build de desarrollo). El release es `dist\UltraPolling\UltraPolling.exe` vía `package.ps1`.

---

### Task 1: RainbowWalker — avance fraccional + plan de velocidad (TDD)

**Files:**
- Modify: `HidusbfModernGui/RainbowWalker.cs`
- Test: `HidusbfModernGui.Tests/RainbowWalkerTests.cs` (actualizar)

**Interfaces:**
- Produces:
  - `(byte R, byte G, byte B) Advance(double coloursPerTick)` — avanza el índice de forma fraccional (acumulada) y devuelve el color actual.
  - `(byte R, byte G, byte B) Step()` — compat: `Advance(1.0)`.
  - `static (double intervalMs, double coloursPerTick) SpeedPlan(double coloursPerSec)`
  - `static TimeSpan IntervalFor(double coloursPerSec)`
  - `static double ActualColoursPerSecond(double coloursPerSec)`
  - `double CycleSeconds(double coloursPerSec)`
  - `static bool ShowsEveryColour(double coloursPerSec)`
  - `const double MinColoursPerSecond = 5.0`, `const double MaxColoursPerSecond = 180.0`
- Removes: las sobrecargas basadas en `int ticksPerColour` (`IntervalFor(int)`, `ColoursPerSecond(int)`, `CycleSeconds(int)`) y `FastestTicksPerColour`/`SlowestTicksPerColour`.

- [ ] **Step 1: Escribir/actualizar los tests (fallan)**

En `HidusbfModernGui.Tests/RainbowWalkerTests.cs`, ELIMINAR los tests que llamen a `IntervalFor(int)`, `ColoursPerSecond(int)` o `CycleSeconds(int)` (ya no existen). Mantener los que usan `Step()` / recorrido del ramp. AÑADIR:

```csharp
    [Fact]
    public void SpeedPlan_AtOrBelow64_ShowsEveryColour_OneColourPerTick()
    {
        var (intervalMs, perTick) = RainbowWalker.SpeedPlan(32);
        Assert.Equal(1.0, perTick, 3);
        Assert.True(intervalMs >= 15.625);              // interval is a whole-tick multiple
        Assert.True(RainbowWalker.ShowsEveryColour(32));
    }

    [Fact]
    public void SpeedPlan_Above64_FiresEveryTick_AdvancesMultipleColours()
    {
        var (intervalMs, perTick) = RainbowWalker.SpeedPlan(180);
        Assert.Equal(15.625, intervalMs, 3);            // fastest the timer allows
        Assert.True(perTick > 2.5 && perTick < 3.0);    // 180/64 ~= 2.81
        Assert.False(RainbowWalker.ShowsEveryColour(180));
    }

    [Fact]
    public void SpeedPlan_Clamps()
    {
        Assert.Equal(RainbowWalker.SpeedPlan(5).coloursPerTick,   RainbowWalker.SpeedPlan(1).coloursPerTick,   3);
        Assert.Equal(RainbowWalker.SpeedPlan(180).coloursPerTick, RainbowWalker.SpeedPlan(999).coloursPerTick, 3);
    }

    [Fact]
    public void ActualColoursPerSecond_MatchesTarget_WhenTargetIsAWholeTickRate()
    {
        // 64/s is exactly one colour per 15.625ms tick.
        Assert.Equal(64.0, RainbowWalker.ActualColoursPerSecond(64), 0);
        Assert.Equal(180.0, RainbowWalker.ActualColoursPerSecond(180), 0);
    }

    [Fact]
    public void Advance_Fractional_AccumulatesAcrossTicks()
    {
        var w = new RainbowWalker(RainbowStyle.Smooth);
        var first = w.Advance(0.5);          // returns ramp[0], pos -> 0.5
        var second = w.Advance(0.5);         // still ramp[0], pos -> 1.0
        var third = w.Advance(0.5);          // ramp[1], pos -> 1.5
        Assert.Equal(first, second);         // half-steps do not skip
        Assert.NotEqual(second, third);
    }

    [Fact]
    public void Advance_One_MatchesStep()
    {
        var a = new RainbowWalker(RainbowStyle.Vivid);
        var b = new RainbowWalker(RainbowStyle.Vivid);
        for (int i = 0; i < 10; i++)
            Assert.Equal(a.Step(), b.Advance(1.0));
    }
```

- [ ] **Step 2: Correr y ver fallar**

Run: `dotnet test HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj -v q`
Expected: FALLA de compilación (métodos nuevos no existen; y los tests viejos borrados ya no referencian los métodos int).

- [ ] **Step 3: Reescribir RainbowWalker.cs**

Reemplazar el contenido de `HidusbfModernGui/RainbowWalker.cs` por:

```csharp
using System;

namespace HidusbfModernGui
{
    // Walks a ColourRamp. Speed is expressed in COLOURS PER SECOND. Windows delivers timer
    // ticks on a 15.625ms cadence and no finer (measured), so at most ~64 distinct frames/s
    // are possible. Up to 64 colours/s we show every colour (one per tick). Above that, the
    // timer fires as fast as it can and each tick advances MORE than one colour - fractional,
    // accumulated - so the cycle speeds up past 64/s. Consecutive ramp colours differ by <=1
    // per channel, so advancing ~3 per tick changes the shown colour by <=3/255 per frame:
    // still smooth, but no longer literally every colour.
    public sealed class RainbowWalker
    {
        private readonly ColourRamp _ramp;
        private double _pos;   // fractional position along the ramp

        public RainbowWalker(RainbowStyle style) => _ramp = ColourRamp.For(style);

        // Advance by coloursPerTick (>= 0, may be fractional) and return the colour shown NOW
        // (read-then-advance, so the very first call shows ramp[0] rather than stepping over it).
        public (byte R, byte G, byte B) Advance(double coloursPerTick)
        {
            int idx = ((int)Math.Floor(_pos)) % _ramp.Count;
            if (idx < 0) idx += _ramp.Count;
            var colour = _ramp[idx];

            _pos += Math.Max(0.0, coloursPerTick);
            if (_pos >= _ramp.Count) _pos %= _ramp.Count;
            return colour;
        }

        // Compat: one colour per tick.
        public (byte R, byte G, byte B) Step() => Advance(1.0);

        public const double OsTickMs = 15.625;
        private const double FramesPerSecFloor = 1000.0 / OsTickMs;   // ~64
        public const double MinColoursPerSecond = 5.0;
        public const double MaxColoursPerSecond = 180.0;

        // Maps a target colours/s to (timer interval, colours to advance per tick).
        public static (double intervalMs, double coloursPerTick) SpeedPlan(double coloursPerSec)
        {
            coloursPerSec = Math.Clamp(coloursPerSec, MinColoursPerSecond, MaxColoursPerSecond);
            if (coloursPerSec <= FramesPerSecFloor)
            {
                // Slow enough to show every colour: one per tick, whole-tick interval.
                int ticksPerColour = Math.Max(1, (int)Math.Round(FramesPerSecFloor / coloursPerSec));
                return (ticksPerColour * OsTickMs, 1.0);
            }
            // Faster than the timer can show distinct colours: fire every tick, advance >1.
            return (OsTickMs, coloursPerSec / FramesPerSecFloor);
        }

        // Clamped rather than validated: this feeds DispatcherTimer.Interval from inside a tick,
        // where a throw takes the app down.
        public static TimeSpan IntervalFor(double coloursPerSec) =>
            TimeSpan.FromMilliseconds(SpeedPlan(coloursPerSec).intervalMs);

        // The colours/s actually delivered (accounts for whole-tick rounding at slow speeds).
        public static double ActualColoursPerSecond(double coloursPerSec)
        {
            var (intervalMs, perTick) = SpeedPlan(coloursPerSec);
            return perTick * 1000.0 / intervalMs;
        }

        public double CycleSeconds(double coloursPerSec) =>
            _ramp.Count / ActualColoursPerSecond(coloursPerSec);

        // True while every ramp colour is shown (<= the 64/s frame floor).
        public static bool ShowsEveryColour(double coloursPerSec) =>
            coloursPerSec <= FramesPerSecFloor + 0.001;
    }
}
```

- [ ] **Step 4: Correr los tests y ver pasar**

Run: `dotnet test HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj -v q`
Expected: PASA todo. (Nota: MainWindow.xaml.cs aún no compila hasta la Task 3; correr los tests del proyecto de tests, que enlaza solo los archivos puros, debe pasar. Si el proyecto de tests referencia el GUI y no compila, saltar a build tras la Task 3.)

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/RainbowWalker.cs HidusbfModernGui.Tests/RainbowWalkerTests.cs
git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "feat: walker con avance fraccional y velocidad en colores/s (hasta 180)"
```

---

### Task 2: LightIntent — velocidad en colores/s (TDD)

**Files:**
- Modify: `HidusbfModernGui/LightIntent.cs`
- Test: `HidusbfModernGui.Tests/LightIntentTests.cs`

**Interfaces:**
- Renames: la propiedad `int TicksPerColour` (default 3) pasa a `int RainbowColoursPerSecond` (default 30). `FromRainbow(RainbowStyle, int ticksPerColour, ...)` pasa a `FromRainbow(RainbowStyle style, int coloursPerSecond, PlayerLeds player, LedBrightness brightness)`.

- [ ] **Step 1: Actualizar los tests (fallan)**

En `LightIntentTests.cs`, en `Rainbow_RoundTrips_WithPlayerAndBrightness`, cambiar la llamada y el assert:
```csharp
        var intent = LightIntent.FromRainbow(RainbowStyle.Vivid, 120, PlayerLeds.Player2, LedBrightness.Medium);
        ...
        Assert.Equal(120, loaded.RainbowColoursPerSecond);
```
En `Enums_PersistAsNames`, actualizar la llamada a `FromRainbow(RainbowStyle.Vivid, 30, PlayerLeds.Player4, LedBrightness.Low)`.

- [ ] **Step 2: Correr y ver fallar**

Run: `dotnet test HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj -v q`
Expected: FALLA de compilación (`TicksPerColour`/firma vieja no existen).

- [ ] **Step 3: Editar LightIntent.cs**

En `HidusbfModernGui/LightIntent.cs`:
- Cambiar la propiedad:
```csharp
        public int RainbowColoursPerSecond { get; set; } = 30;
```
(borrar `public int TicksPerColour { get; set; } = 3;`)
- Cambiar el comentario que mencione "TicksPerColour" si lo hay.
- Cambiar la factory:
```csharp
        public static LightIntent FromRainbow(RainbowStyle style, int coloursPerSecond,
                                              PlayerLeds player, LedBrightness brightness) => new LightIntent
        {
            Kind = LightIntentKind.Rainbow,
            Style = style, RainbowColoursPerSecond = coloursPerSecond,
            Player = player, Brightness = brightness
        };
```

- [ ] **Step 4: Correr los tests y ver pasar**

Run: `dotnet test HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj -v q`
Expected: PASA todo (mismo total; el GUI aún no compila, ver nota de Task 1 Step 4).

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/LightIntent.cs HidusbfModernGui.Tests/LightIntentTests.cs
git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "refactor: LightIntent guarda velocidad en colores/s (RainbowColoursPerSecond)"
```

---

### Task 3: Cablear la velocidad 180/s en MainWindow + XAML

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml` (slider RainbowSpeed, ~L549)
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (Rainbow_Toggled, Rainbow_Tick, RainbowSpeed_Changed, UpdateRainbowSpeedText, TicksPerColour, RememberLight)

**Interfaces:**
- Consumes: `RainbowWalker.Advance/SpeedPlan/IntervalFor/ActualColoursPerSecond/CycleSeconds/ShowsEveryColour` (Task 1); `LightIntent.RainbowColoursPerSecond`/`FromRainbow` (Task 2).

- [ ] **Step 1: Slider a colores/s en el XAML**

Reemplazar el `<Slider x:Name="RainbowSpeed" .../>` (~L549-551) por:
```xml
                                                <Slider x:Name="RainbowSpeed" Minimum="5" Maximum="180" Value="30" Width="130"
                                                        IsSnapToTickEnabled="True" TickFrequency="1"
                                                        VerticalAlignment="Center" ValueChanged="RainbowSpeed_Changed"/>
```
(quitar `IsDirectionReversed="True"`: ahora mayor = más rápido de forma natural.)

- [ ] **Step 2: Ajustar el code-behind**

En `MainWindow.xaml.cs`:

(a) Reemplazar la propiedad `TicksPerColour`:
```csharp
        private double TargetColoursPerSecond => RainbowSpeed.Value;
```

(b) En `Rainbow_Toggled`, la línea que fija el intervalo:
```csharp
                _rainbowTimer.Interval = RainbowWalker.IntervalFor(TargetColoursPerSecond);
```

(c) En `Rainbow_Tick`, cambiar el avance:
```csharp
            var (r, g, b) = _rainbowWalker.Advance(RainbowWalker.SpeedPlan(TargetColoursPerSecond).coloursPerTick);
```

(d) En `RainbowSpeed_Changed`:
```csharp
            if (_rainbowTimer != null)
                _rainbowTimer.Interval = RainbowWalker.IntervalFor(TargetColoursPerSecond);
            UpdateRainbowSpeedText();
            RememberLight();
```

(e) `UpdateRainbowSpeedText` — honesto, con aviso arriba de 64/s:
```csharp
        private void UpdateRainbowSpeedText()
        {
            if (RainbowSpeedText == null || RainbowSpeed == null) return;

            var walker = new RainbowWalker(CurrentRainbowStyle);
            double actual = RainbowWalker.ActualColoursPerSecond(TargetColoursPerSecond);
            string suffix = RainbowWalker.ShowsEveryColour(TargetColoursPerSecond) ? "" : " · varios colores/cuadro";
            RainbowSpeedText.Text = $"{actual:0.#}/s · vuelta {walker.CycleSeconds(TargetColoursPerSecond):0.#} s{suffix}";
        }
```

(f) En `RememberLight`, la rama rainbow usa la nueva firma y campo:
```csharp
                if (RainbowStyleList.SelectedItem == null) return;
                var style = (RainbowStyle)((ComboBoxItem)RainbowStyleList.SelectedItem).Tag;
                var lit = CurrentLight();
                intent = LightIntent.FromRainbow(style, (int)Math.Round(TargetColoursPerSecond), player, brightness);
                intent.R = lit.R; intent.G = lit.G; intent.B = lit.B;
```

- [ ] **Step 3: Build**

Run: `dotnet build HidusbfModernGui/HidusbfModernGui.csproj -v q`
Expected: `Build succeeded. 0 Error(s)`. (Si algún otro sitio referenciaba `TicksPerColour`, corregirlo a `TargetColoursPerSecond`.)

- [ ] **Step 4: Tests completos**

Run: `dotnet test HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj -v q`
Expected: `Passed! ... Passed: <=249` (según cuántos tests de walker se borraron/añadieron).

- [ ] **Step 5: Verificación manual**

Compilar y correr `bin\Debug\net9.0-windows\HidusbfModernGui.exe`. Activar Rainbow, arrastrar VELOCIDAD hasta el tope: el texto debe decir ~180/s y "varios colores/cuadro"; el efecto se ve más rápido y suave. A la izquierda del tope (≤64/s) el sufijo desaparece.

- [ ] **Step 6: Commit**

```bash
git add HidusbfModernGui/MainWindow.xaml HidusbfModernGui/MainWindow.xaml.cs
git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "feat: slider de rainbow en colores/s hasta 180 (avance multi-color honesto)"
```

---

### Task 4: La UI refleja el estado guardado al abrir la pestaña (fix "no vuelve el player")

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (BuildLightControls, ~L358 justo antes de crear los presets)

**Interfaces:**
- Consumes: `IntentStore.Load()`, `LightIntent`, `LightIntentKind`, los combos `PlayerLedList`/`BrightnessList`/`RainbowStyleList`, `Picker`, `RainbowSpeed`, `RainbowCheck`, `UpdateSwatch()`, `_updatingLight`.

Contexto: hoy `BuildLightControls` construye los combos con valores por defecto (Player 1, Alto, Suave, azul) — por eso al reabrir la UI muestra Player 1 aunque el mando sí tenga lo guardado. El fix: tras construir, sobreescribir las selecciones desde la intención guardada.

- [ ] **Step 1: Añadir la inicialización desde la intención**

En `BuildLightControls`, DENTRO del `try` (que ya tiene `_updatingLight = true`), JUSTO DESPUÉS de `RainbowStyleList.SelectedIndex = 0; UpdateRainbowHint();` (~L358-359) y ANTES del bloque de presets, insertar:

```csharp
                // Reflejar en la UI lo que se restauro al mando (la intencion guardada), para que
                // no aparezca Player 1/azul cuando el mando ya tiene otro estado. Bajo _updatingLight
                // para no disparar escrituras; el rainbow se arranca despues, fuera del guard.
                var saved = IntentStore.Load();
                if (saved != null)
                {
                    Picker.SelectedColor = Color.FromRgb(saved.R, saved.G, saved.B);
                    UpdateSwatch();
                    SelectComboByTag(PlayerLedList, saved.Player);
                    SelectComboByTag(BrightnessList, saved.Brightness);
                    SelectComboByTag(RainbowStyleList, saved.Style);
                    RainbowSpeed.Value = Math.Clamp(saved.RainbowColoursPerSecond,
                        (int)RainbowWalker.MinColoursPerSecond, (int)RainbowWalker.MaxColoursPerSecond);
                }
```

- [ ] **Step 2: Añadir el helper SelectComboByTag y el arranque del rainbow**

Añadir este helper (junto a BuildLightControls):

```csharp
        // Selecciona en un ComboBox el item cuyo Tag es igual a value (los items se construyen
        // con Tag = el enum). Sin match, deja la seleccion actual.
        private static void SelectComboByTag(ComboBox combo, object value)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (Equals(item.Tag, value)) { combo.SelectedItem = item; return; }
            }
        }
```

Y al FINAL de `BuildLightControls`, DESPUÉS del `finally` que pone `_updatingLight = false` (fuera del guard, para que el toggle arranque el timer con el estilo/velocidad ya fijados), añadir:

```csharp
            // Si lo guardado era un rainbow, reanudar la animacion ahora que los controles existen.
            // Marcar el check dispara Rainbow_Toggled, que arranca el timer con el estilo/velocidad
            // que acabamos de fijar arriba.
            var savedIntent = IntentStore.Load();
            if (savedIntent?.Kind == LightIntentKind.Rainbow && RainbowCheck.IsChecked != true)
            {
                RainbowCheck.IsChecked = true;
            }
```

> Nota: para un intent estático no hace falta reaplicar aquí — el mando ya recibió el color en el arranque (ReapplyIntent). Esto solo sincroniza la UI y, si era rainbow, reanuda la animación.

- [ ] **Step 3: Build**

Run: `dotnet build HidusbfModernGui/HidusbfModernGui.csproj -v q`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 4: Verificación manual (el caso reportado)**

1. Abrir `bin\Debug\net9.0-windows\HidusbfModernGui.exe`, ir al mando, poner **Player 4** y un color (o rainbow). Cerrar.
2. Reabrir, ir a la pestaña del mando: la UI debe mostrar **Player 4** y el color/efecto guardados, no Player 1/azul. Si era rainbow, debe estar animando.

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/MainWindow.xaml.cs
git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "fix: la pestana del mando refleja el estado guardado al abrir (player/color/efecto)"
```

---

## Self-Review

**Cobertura de lo reportado por el usuario:**
- "No vuelve el número de jugador" → Task 4 (la UI se inicializa desde la intención). ✓
- "La velocidad debe llegar hasta 180" → Tasks 1–3 (colores/s hasta 180, honesto). ✓
- "Los perfiles sí se aplican" → sin cambios (ya funciona). ✓
- Confusión del exe (bin\Debug vs dist) → aclarada en Global Constraints; no requiere código. ✓

**Placeholders:** ninguno; código completo en cada paso.

**Consistencia de tipos:** `Advance(double)`, `SpeedPlan(double)`, `IntervalFor(double)`, `ActualColoursPerSecond(double)`, `CycleSeconds(double)`, `ShowsEveryColour(double)` (Task 1) usados en Task 3. `LightIntent.RainbowColoursPerSecond` + `FromRainbow(style, coloursPerSecond, player, brightness)` (Task 2) usados en Task 3 (RememberLight) y Task 4 (init UI). `TargetColoursPerSecond` reemplaza a `TicksPerColour` en todos sus usos.

**Riesgos señalados para el review:**
- Migración de `active.json` viejo: un archivo guardado por la versión anterior tiene la clave `TicksPerColour`, que ya no existe. `Load()` la ignora y `RainbowColoursPerSecond` toma su default (30). Aceptable en beta; documentado.
- Honestidad de la velocidad: >64/s ya NO muestra cada color (micro-avances de ≤3 unidades). Decisión consciente del usuario; el label lo indica con "varios colores/cuadro".
- `BuildLightControls` corre una vez (guard `Items.Count > 0`); llama `IntentStore.Load()` dos veces (Step 1 y Step 2) — barato (lectura de un JSON pequeño una sola vez por sesión). Un reviewer puede unificarlo en una variable si prefiere.

## Execution Handoff

Plan guardado en `docs/superpowers/plans/2026-07-18-ui-sync-and-faster-rainbow.md`. Opciones:
1. **Subagent-Driven (recomendado)** — un subagente por tarea, revisión entre cada una.
2. **Inline** — tarea por tarea en esta sesión con checkpoints.
