# Efectos animados de los LEDs de jugador — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development o superpowers:executing-plans. Steps con checkbox (`- [ ]`).

**Goal:** Animar en tiempo real las 5 luces de jugador del DualSense con efectos en bucle — "Carga" (pulso simétrico) y "Estrellas" (barrido) — que conviven con el color fijo o el rainbow, y se recuerdan entre sesiones.

**Architecture:** Las 5 luces son un mask de 5 bits (byte 44 del reporte 0x02). Un `PlayerLedWalker` puro define la secuencia de masks por efecto y su cadencia. El color (fijo/rainbow) y el mask (fijo/animado) viajan en el MISMO reporte, así que se unifica el tick del rainbow y el de los LEDs en un solo "motor de efectos": un único `DispatcherTimer` que, mientras haya rainbow y/o efecto de LED activo, calcula color + mask + brillo y manda un reporte. Sin efectos, `ApplyLightNow` sigue haciendo escrituras de una sola vez. No se toca `DualSenseLight.cs` (acepta cualquier mask via `(PlayerLeds)valor`).

**Tech Stack:** .NET 9, WPF, C#, xUnit.

## Global Constraints

- .NET 9, x64, WPF. Sin dependencias NuGet nuevas.
- Lógica pura sin WPF (enlazada por ruta en tests): `PlayerLedWalker.cs`, `LightIntent.cs`, `RainbowWalker.cs`.
- **Congelados, NO tocar:** `DualSenseLight.cs` (ya acepta masks arbitrarios), `LightProfile.cs`, `ProfileStore`, `SystemManager.cs`, `PollingCore.cs`, `ColourRamp.cs`, `ColourMath.cs`, `Theme.xaml`.
- Paleta de diez colores; sin color nuevo (los LEDs de jugador son blancos, no cuentan como color de UI).
- Commits SIN `Co-Authored-By`. git identity: `git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com"`.
- Carpeta de trabajo: `C:\Users\Administrator\Downloads\work ultrapolling\UltraPolling`. Push lo hace el usuario. Build de prueba: `bin\Debug\net9.0-windows\HidusbfModernGui.exe`.
- **Mapa de bits (verificado con el enum existente):** las 5 luces de izquierda a derecha son bit0..bit4. `Player1=4=0b00100` = centro, confirma el mapeo. "1 y 4" = par exterior = bits 0,4 = 17. "2 y 3" = par interior = bits 1,3 = 10.

---

### Task 1: PlayerLedWalker + efectos (lógica pura, TDD)

**Files:**
- Create: `HidusbfModernGui/PlayerLedWalker.cs`
- Test: `HidusbfModernGui.Tests/PlayerLedWalkerTests.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj` (enlazar el archivo nuevo)

**Interfaces:**
- Produces: `enum PlayerLedEffect { None, Charge, Twinkle }`; `sealed class PlayerLedWalker` con ctor `(PlayerLedEffect)`, `byte MaskAt(int frameIndex)`, `int FrameCount`, `int FrameMs`, `static int FrameMsFor(PlayerLedEffect)`.

- [ ] **Step 1: Test que falla** — crear `PlayerLedWalkerTests.cs`:

```csharp
using HidusbfModernGui;
using Xunit;

public class PlayerLedWalkerTests
{
    [Fact]
    public void Charge_CyclesOuterInnerOff()
    {
        var w = new PlayerLedWalker(PlayerLedEffect.Charge);
        Assert.Equal(4, w.FrameCount);
        Assert.Equal((byte)17, w.MaskAt(0));   // 1 y 4 (par exterior)
        Assert.Equal((byte)27, w.MaskAt(1));   // + 2 y 3
        Assert.Equal((byte)10, w.MaskAt(2));   // solo 2 y 3
        Assert.Equal((byte)0,  w.MaskAt(3));   // apagado
        Assert.Equal((byte)17, w.MaskAt(4));   // vuelve (wrap)
    }

    [Fact]
    public void Twinkle_SweepsOneLedBackAndForth()
    {
        var w = new PlayerLedWalker(PlayerLedEffect.Twinkle);
        Assert.Equal(new byte[] { 1, 2, 4, 8, 16, 8, 4, 2 },
            new[] { w.MaskAt(0), w.MaskAt(1), w.MaskAt(2), w.MaskAt(3),
                    w.MaskAt(4), w.MaskAt(5), w.MaskAt(6), w.MaskAt(7) });
        Assert.Equal(w.MaskAt(0), w.MaskAt(8));   // wrap
    }

    [Fact]
    public void None_IsAllOff()
    {
        var w = new PlayerLedWalker(PlayerLedEffect.None);
        Assert.Equal(0, w.FrameCount);
        Assert.Equal((byte)0, w.MaskAt(0));
        Assert.Equal((byte)0, w.MaskAt(99));
    }

    [Fact]
    public void MaskAt_WrapsNegative()
    {
        var w = new PlayerLedWalker(PlayerLedEffect.Charge);
        Assert.Equal(w.MaskAt(3), w.MaskAt(-1));
    }

    [Fact]
    public void FrameMs_IsPositiveForRealEffects()
    {
        Assert.True(PlayerLedWalker.FrameMsFor(PlayerLedEffect.Charge) > 0);
        Assert.True(PlayerLedWalker.FrameMsFor(PlayerLedEffect.Twinkle) > 0);
    }
}
```

- [ ] **Step 2: Correr y ver fallar** — `dotnet test HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj -v q` → falla de compilación.

- [ ] **Step 3: Crear PlayerLedWalker.cs**:

```csharp
using System;

namespace HidusbfModernGui
{
    // Efectos animados de las 5 luces de jugador. Cada luz es un bit del mask de 5 bits que
    // va en el byte 44 del reporte 0x02 (bit0 = izquierda .. bit4 = derecha). No es color:
    // son las luces blancas bajo el touchpad. La animacion es una secuencia de masks en bucle.
    public enum PlayerLedEffect { None, Charge, Twinkle }

    public sealed class PlayerLedWalker
    {
        private readonly byte[] _frames;
        public int FrameMs { get; }

        public PlayerLedWalker(PlayerLedEffect effect)
        {
            var (frames, ms) = FramesFor(effect);
            _frames = frames;
            FrameMs = ms;
        }

        public int FrameCount => _frames.Length;

        // El mask para un indice de frame; hace wrap en ambos sentidos. None -> siempre 0.
        public byte MaskAt(int frameIndex)
        {
            if (_frames.Length == 0) return 0;
            int i = frameIndex % _frames.Length;
            if (i < 0) i += _frames.Length;
            return _frames[i];
        }

        public static int FrameMsFor(PlayerLedEffect effect) => FramesFor(effect).frameMs;

        private static (byte[] frames, int frameMs) FramesFor(PlayerLedEffect effect) => effect switch
        {
            // "1 y 4" = par exterior (bits 0,4 = 17). "2 y 3" = par interior (bits 1,3 = 10).
            // Enciende exterior, agrega interior, deja interior, apaga. Efecto de "carga".
            PlayerLedEffect.Charge => (new byte[] { 17, 27, 10, 0 }, 180),
            // Una sola luz que barre de izquierda a derecha y vuelve (tipo estrellas/knight-rider).
            PlayerLedEffect.Twinkle => (new byte[] { 1, 2, 4, 8, 16, 8, 4, 2 }, 110),
            _ => (Array.Empty<byte>(), 150),
        };
    }
}
```

- [ ] **Step 4: Enlazar en el csproj de tests** — añadir junto a los otros `<Compile Include>`:
```xml
    <Compile Include="..\HidusbfModernGui\PlayerLedWalker.cs" Link="PlayerLedWalker.cs" />
```

- [ ] **Step 5: Correr y ver pasar** — `dotnet test HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj -v q` → PASA (243 + 5 = 248).

- [ ] **Step 6: Commit**
```bash
git add HidusbfModernGui/PlayerLedWalker.cs HidusbfModernGui.Tests/PlayerLedWalkerTests.cs HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj
git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "feat: PlayerLedWalker con efectos Carga y Estrellas (TDD)"
```

---

### Task 2: LightIntent recuerda el efecto de LED (TDD)

**Files:**
- Modify: `HidusbfModernGui/LightIntent.cs`
- Test: `HidusbfModernGui.Tests/LightIntentTests.cs`

**Interfaces:**
- Adds: `public PlayerLedEffect PlayerEffect { get; set; } = PlayerLedEffect.None;` a `LightIntent`. (Se guarda para static y para rainbow por igual.)

- [ ] **Step 1: Test** — añadir a `LightIntentTests.cs`:
```csharp
    [Fact]
    public void PlayerEffect_RoundTrips()
    {
        var intent = LightIntent.FromStatic(new LightState(1, 2, 3, PlayerLeds.Player1, LedBrightness.High));
        intent.PlayerEffect = PlayerLedEffect.Twinkle;
        Assert.True(IntentStore.Save(intent).Success);

        var loaded = IntentStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal(PlayerLedEffect.Twinkle, loaded!.PlayerEffect);
    }

    [Fact]
    public void PlayerEffect_DefaultsToNone()
    {
        var intent = LightIntent.FromStatic(new LightState(0, 0, 0, PlayerLeds.Player1, LedBrightness.High));
        Assert.Equal(PlayerLedEffect.None, intent.PlayerEffect);
    }
```

- [ ] **Step 2: Correr y ver fallar** — compile error (PlayerEffect no existe).

- [ ] **Step 3: Añadir la propiedad** en `LightIntent.cs`, junto a las demás props:
```csharp
        public PlayerLedEffect PlayerEffect { get; set; } = PlayerLedEffect.None;
```

- [ ] **Step 4: Correr y ver pasar** — 248 + 2 = 250.

- [ ] **Step 5: Commit**
```bash
git add HidusbfModernGui/LightIntent.cs HidusbfModernGui.Tests/LightIntentTests.cs
git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "feat: LightIntent recuerda el efecto de LED de jugador"
```

---

### Task 3: Motor de efectos unificado (color + LEDs en un tick)

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

Refactor: hoy `_rainbowTimer`/`Rainbow_Tick` solo animan color. Se generalizan a UN motor que corre mientras haya rainbow y/o efecto de LED, y en cada tick manda UN reporte con color (fijo o rainbow) + mask (fijo o animado) + brillo.

**Interfaces:**
- Consumes: `RainbowWalker` (Task R1 previa), `PlayerLedWalker`/`PlayerLedEffect` (Task 1), `DualSenseLight.Apply`, `CurrentRainbowStyle`, `TargetColoursPerSecond`, `Picker`, `PlayStationList`, `PlayerLedList`, `BrightnessList`, `_updatingLight`, `UpdateSwatch`.
- Produces: `PlayerEffectList` (ComboBox, lo crea Task 4), `CurrentPlayerEffect`, `PlayerEffectOn`, `RainbowOn`, `UpdateEffectDriver()`, `Effect_Tick`.

- [ ] **Step 1: Añadir campos y helpers** — junto a `_rainbowTimer`/`_rainbowWalker`:
```csharp
        private PlayerLedWalker? _playerWalker;
        private double _playerFrameAccumMs;   // acumula ms para avanzar el frame del efecto de LED
        private int _playerFrameIndex;

        private bool RainbowOn => RainbowCheck.IsChecked == true;

        private PlayerLedEffect CurrentPlayerEffect =>
            PlayerEffectList?.SelectedItem is ComboBoxItem it ? (PlayerLedEffect)it.Tag : PlayerLedEffect.None;

        private bool PlayerEffectOn => CurrentPlayerEffect != PlayerLedEffect.None;
```

- [ ] **Step 2: Añadir el driver unificado** — reemplaza el arranque del timer que hoy vive en `Rainbow_Toggled`:
```csharp
        // Un solo motor: corre mientras haya rainbow y/o efecto de LED. El intervalo va al ritmo
        // del rainbow si esta activo (hasta 64/s); si solo hay efecto de LED, al ritmo del efecto.
        // El frame del efecto de LED avanza por acumulador de ms, asi su cadencia es independiente
        // de un rainbow mas rapido.
        private void UpdateEffectDriver()
        {
            bool any = RainbowOn || PlayerEffectOn;
            if (!any) { _rainbowTimer?.Stop(); return; }

            _rainbowTimer ??= new DispatcherTimer(DispatcherPriority.Render);
            _rainbowTimer.Tick -= Effect_Tick;
            _rainbowTimer.Tick -= Rainbow_Tick;   // por si quedaba enganchado el handler viejo
            _rainbowTimer.Tick += Effect_Tick;
            _rainbowTimer.Interval = RainbowOn
                ? RainbowWalker.IntervalFor(TargetColoursPerSecond)
                : TimeSpan.FromMilliseconds(PlayerLedWalker.FrameMsFor(CurrentPlayerEffect));

            if (RainbowOn) _rainbowWalker ??= new RainbowWalker(CurrentRainbowStyle);
            if (PlayerEffectOn) _playerWalker ??= new PlayerLedWalker(CurrentPlayerEffect);
            _rainbowTimer.Start();
        }

        private void Effect_Tick(object? sender, EventArgs e)
        {
            if (PlayStationList.SelectedItem is not UsbDeviceModel model) return;
            if (PlayerLedList.SelectedItem == null || BrightnessList.SelectedItem == null) return;

            byte r, g, b;
            if (RainbowOn)
            {
                _rainbowWalker ??= new RainbowWalker(CurrentRainbowStyle);
                (r, g, b) = _rainbowWalker.Advance(RainbowWalker.SpeedPlan(TargetColoursPerSecond).coloursPerTick);
                _updatingLight = true;
                try { Picker.SelectedColor = System.Windows.Media.Color.FromRgb(r, g, b); UpdateSwatch(); }
                finally { _updatingLight = false; }
            }
            else
            {
                var c = Picker.SelectedColor; r = c.R; g = c.G; b = c.B;
            }

            PlayerLeds player;
            if (PlayerEffectOn)
            {
                _playerWalker ??= new PlayerLedWalker(CurrentPlayerEffect);
                _playerFrameAccumMs += _rainbowTimer!.Interval.TotalMilliseconds;
                if (_playerFrameAccumMs >= _playerWalker.FrameMs)
                {
                    _playerFrameAccumMs = 0;
                    _playerFrameIndex++;
                }
                player = (PlayerLeds)_playerWalker.MaskAt(_playerFrameIndex);
            }
            else
            {
                player = (PlayerLeds)((ComboBoxItem)PlayerLedList.SelectedItem).Tag;
            }

            var brightness = (LedBrightness)((ComboBoxItem)BrightnessList.SelectedItem).Tag;
            DualSenseLight.Apply(model.InstanceId, new LightState(r, g, b, player, brightness));
        }
```

- [ ] **Step 3: Reescribir `Rainbow_Toggled` para usar el driver** — su cuerpo pasa a:
```csharp
        private void Rainbow_Toggled(object sender, RoutedEventArgs e)
        {
            if (RainbowOn) _rainbowWalker = new RainbowWalker(CurrentRainbowStyle);
            // Con el rainbow activo el color lo maneja el efecto: deshabilitar el apartado COLOR.
            if (ColorSection != null) ColorSection.IsEnabled = !RainbowOn;
            UpdateEffectDriver();
            LogStatus(RainbowOn ? "Rainbow activo." : "Rainbow desactivado.");
            RememberLight();
        }
```

- [ ] **Step 4: `RainbowStyle_Changed` y `RainbowSpeed_Changed`** — donde hoy retunean/rearman el `_rainbowTimer`, llamar `UpdateEffectDriver();` en su lugar (mantener el `_rainbowWalker = null;` de RainbowStyle_Changed y el `UpdateRainbowSpeedText()`/`UpdateRainbowHint()` que ya tengan). Ej. RainbowSpeed_Changed:
```csharp
        private void RainbowSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateEffectDriver();
            UpdateRainbowSpeedText();
            RememberLight();
        }
```

- [ ] **Step 5: Nuevo handler `PlayerEffect_Changed`**:
```csharp
        private void PlayerEffect_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingLight) return;
            if (PlayerEffectOn) { _playerWalker = new PlayerLedWalker(CurrentPlayerEffect); _playerFrameIndex = 0; _playerFrameAccumMs = 0; }
            // Con un efecto de LED activo, la seleccion fija de Player la maneja el efecto.
            if (PlayerLedList != null) PlayerLedList.IsEnabled = !PlayerEffectOn;
            UpdateEffectDriver();
            RememberLight();
        }
```

- [ ] **Step 6: Build** — `dotnet build HidusbfModernGui/HidusbfModernGui.csproj -v q` → 0 errores. (Si `Rainbow_Tick` queda sin referencias, dejarlo o borrarlo; el driver usa `Effect_Tick`. Si se borra, quitar también su suscripción. Preferible borrarlo para no dejar código muerto — verificar con grep que nada lo referencia salvo su definición.)

- [ ] **Step 7: Commit**
```bash
git add HidusbfModernGui/MainWindow.xaml.cs
git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "feat: motor de efectos unificado (color + LEDs de jugador en un reporte)"
```

---

### Task 4: UI — selector "EFECTO" de LED en la sección LED DE JUGADOR

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml`
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (BuildLightControls: poblar el nuevo combo)

**Interfaces:**
- Consumes: `PlayerEffect_Changed` (Task 3). Produces: `PlayerEffectList` (ComboBox).

- [ ] **Step 1: Añadir el combo al XAML** — dentro de la sección "LED DE JUGADOR" (el `StackPanel Horizontal` que tiene `PlayerLedList` y `BRILLO`, ~L526-532), añadir tras el brillo:
```xml
                                                <TextBlock Text="EFECTO" Style="{StaticResource FieldLabel}" VerticalAlignment="Center" Margin="14,0,8,0"/>
                                                <ComboBox x:Name="PlayerEffectList" Width="120" HorizontalAlignment="Left"
                                                          SelectionChanged="PlayerEffect_Changed"/>
```
(Si la fila queda estrecha, envolver en un `WrapPanel` o pasar el efecto a una fila nueva debajo — a criterio del implementador, respetando el estilo existente.)

- [ ] **Step 2: Poblar el combo en `BuildLightControls`** — junto a donde se llena `RainbowStyleList` (bajo `_updatingLight`), añadir:
```csharp
                foreach (var (label, value) in new (string, PlayerLedEffect)[]
                         {
                             ("Ninguno", PlayerLedEffect.None),
                             ("Carga", PlayerLedEffect.Charge),
                             ("Estrellas", PlayerLedEffect.Twinkle),
                         })
                    PlayerEffectList.Items.Add(new ComboBoxItem { Content = label, Tag = value });
                PlayerEffectList.SelectedIndex = 0;
```

- [ ] **Step 3: Build** — 0 errores.

- [ ] **Step 4: Verificación manual** — abrir el mando, elegir EFECTO = Carga → las luces hacen el pulso; = Estrellas → barrido; = Ninguno → vuelve a la selección Player fija. Debe funcionar con color fijo y con rainbow a la vez. Con un efecto activo, el combo de Player fijo queda deshabilitado.

- [ ] **Step 5: Commit**
```bash
git add HidusbfModernGui/MainWindow.xaml HidusbfModernGui/MainWindow.xaml.cs
git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "feat: selector EFECTO de LED (Ninguno/Carga/Estrellas) en la pestana del mando"
```

---

### Task 5: Persistencia del efecto de LED (guardar + restaurar en la UI)

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (RememberLight, BuildLightControls init-from-intent, ReapplyIntent)

- [ ] **Step 1: Guardar el efecto** — en `RememberLight()`, tras construir `intent` (ambas ramas), antes de agendar el guardado:
```csharp
            intent.PlayerEffect = CurrentPlayerEffect;
```

- [ ] **Step 2: Restaurar en la UI** — en `BuildLightControls`, en el bloque "init desde la intención guardada" (bajo `_updatingLight`), tras fijar los demás combos:
```csharp
                    SelectComboByTag(PlayerEffectList, saved.PlayerEffect);
                    PlayerLedList.IsEnabled = saved.PlayerEffect == PlayerLedEffect.None;
```
Y en el bloque de reanudar-efecto tras el `finally` (donde se marca RainbowCheck si era rainbow), añadir el arranque del efecto de LED:
```csharp
            if (savedIntent != null && savedIntent.PlayerEffect != PlayerLedEffect.None)
            {
                _playerWalker = new PlayerLedWalker(savedIntent.PlayerEffect);
                _playerFrameIndex = 0; _playerFrameAccumMs = 0;
                UpdateEffectDriver();
            }
```

- [ ] **Step 3: Reaplicar al arrancar** — `ReapplyIntent` aplica al mando el estado guardado. Para un efecto de LED, el color base ya se aplica; la animación arranca cuando se abre la pestaña (Step 2), consistente con cómo se reanuda el rainbow. No requiere cambio adicional en ReapplyIntent salvo confirmar que no rompe con `PlayerEffect != None` (aplica el mask base del frame 0 o la selección fija; aceptable).

- [ ] **Step 4: Build + verificación** — elegir EFECTO Carga, cerrar, reabrir → la pestaña del mando debe mostrar EFECTO=Carga y la animación corriendo.

- [ ] **Step 5: Commit**
```bash
git add HidusbfModernGui/MainWindow.xaml.cs
git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "feat: recordar y restaurar el efecto de LED entre sesiones"
```

---

## Self-Review

**Cobertura:** efecto Carga (pulso 1&4 / 2&3) y Estrellas (barrido) → Task 1 (walker) + Task 3 (motor) + Task 4 (UI). Convivencia con color fijo y rainbow → Task 3 (un reporte por tick). Persistencia → Task 5. ✓

**Placeholders:** ninguno.

**Consistencia de tipos:** `PlayerLedEffect`/`PlayerLedWalker.MaskAt/FrameMs/FrameMsFor` (Task 1) usados en Tasks 3 y 5; `LightIntent.PlayerEffect` (Task 2) usado en Tasks 4 (UI) y 5. `CurrentPlayerEffect`/`PlayerEffectOn`/`RainbowOn`/`UpdateEffectDriver`/`Effect_Tick` (Task 3) usados en 4 y 5. El mask se envía como `(PlayerLeds)byte` — `DualSenseLight` (congelado) ya lo acepta.

**Riesgos / decisiones para el review y el usuario:**
- **Patrones propuestos** (Carga `[17,27,10,0]`, Estrellas `[1,2,4,8,16,8,4,2]`) y sus cadencias (180/110 ms) son mi propuesta. Si el usuario quiere otras luces/velocidades, es cambiar los arrays/ms en `PlayerLedWalker.FramesFor` — un solo sitio.
- **Sin control de velocidad del efecto de LED** por ahora (velocidad fija por efecto, YAGNI). Se puede añadir un slider después si se pide.
- **Refactor del tick del rainbow** a `Effect_Tick`: hay que enganchar/desenganchar handlers con cuidado (el driver quita `Rainbow_Tick` y usa `Effect_Tick`). El review debe confirmar que no quedan dos handlers suscritos ni `Rainbow_Tick` colgando.
- **Anti-cheat:** es otro lazo de escritura HID mientras la app está abierta — igual de benigno que el rainbow (tráfico HID normal), no añade riesgo de parcheo. Mismo consejo de siempre para online.

## Execution Handoff

Plan en `docs/superpowers/plans/2026-07-18-player-led-effects.md`. Opciones: (1) Subagentes (recomendado), (2) Inline.
