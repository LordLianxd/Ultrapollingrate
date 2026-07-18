# Velocidad para los efectos de LED + animación "Respiración" — Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps con checkbox.

**Goal:** Dar al usuario una barra de VELOCIDAD para los efectos de LED de jugador (hoy corren a un ritmo fijo, se ve feo), y añadir la animación "Respiración desde el centro".

**Architecture:** El `PlayerLedWalker` solo define la SECUENCIA de masks (frames); la CADENCIA deja de venir del walker y pasa a ser un ajuste del usuario en frames/segundo. El motor de efectos unificado usa ese fps para el umbral del acumulador y para el intervalo del timer cuando solo hay efecto de LED. El fps se persiste en `LightIntent`.

**Tech Stack:** .NET 9, WPF, C#, xUnit.

## Global Constraints
- .NET 9, x64. Sin NuGet nuevo. Lógica pura sin WPF: `PlayerLedWalker.cs`, `LightIntent.cs`.
- Congelados (no tocar): `DualSenseLight.cs`, `LightProfile.cs`, `SystemManager.cs`, `PollingCore.cs`, `ColourRamp.cs`, `ColourMath.cs`, `Theme.xaml`, `RainbowWalker.cs`.
- Commits SIN `Co-Authored-By`. git: `git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com"`.
- Carpeta: `C:\Users\Administrator\Downloads\work ultrapolling\UltraPolling`. Push lo hace el usuario. Build de prueba: `bin\Debug\net9.0-windows\HidusbfModernGui.exe`.
- Las 5 luces son on/off (bit0..bit4). Centro=bit2(4). Centro+flancos internos (LED2,3,4)=bits1,2,3=14. Todas=31.

---

### Task 1: Animación "Breathe" en PlayerLedWalker (TDD)

**Files:** Modify `HidusbfModernGui/PlayerLedWalker.cs`; Test `HidusbfModernGui.Tests/PlayerLedWalkerTests.cs`.

**Interfaces:** enum gana `Breathe`. Frames de Breathe: `[0, 4, 14, 31, 14, 4]` (apagado → centro → +flancos → todas → contrae).

- [ ] **Step 1: Test (falla)** — añadir a `PlayerLedWalkerTests.cs`:
```csharp
    [Fact]
    public void Breathe_ExpandsFromCentreAndContracts()
    {
        var w = new PlayerLedWalker(PlayerLedEffect.Breathe);
        Assert.Equal(new byte[] { 0, 4, 14, 31, 14, 4 },
            new[] { w.MaskAt(0), w.MaskAt(1), w.MaskAt(2), w.MaskAt(3), w.MaskAt(4), w.MaskAt(5) });
        Assert.Equal(w.MaskAt(0), w.MaskAt(6)); // wrap
    }
```
- [ ] **Step 2: Correr y ver fallar** — `dotnet test HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj -v q`.
- [ ] **Step 3: Implementar** — en `PlayerLedWalker.cs`: añadir `Breathe` al enum `PlayerLedEffect`, y en `FramesFor` una rama:
```csharp
            // Centro hacia afuera y de vuelta: respiracion simetrica.
            PlayerLedEffect.Breathe => (new byte[] { 0, 4, 14, 31, 14, 4 }, 140),
```
- [ ] **Step 4: Correr y ver pasar** — 250 + 1 = 251.
- [ ] **Step 5: Commit** — `git ... commit -m "feat: animacion Breathe (respiracion desde el centro)"`

---

### Task 2: LightIntent recuerda la velocidad del efecto de LED (TDD)

**Files:** Modify `HidusbfModernGui/LightIntent.cs`; Test `HidusbfModernGui.Tests/LightIntentTests.cs`.

**Interfaces:** `LightIntent` gana `public int PlayerEffectFps { get; set; } = 6;`

- [ ] **Step 1: Test (falla)** — añadir:
```csharp
    [Fact]
    public void PlayerEffectFps_RoundTrips_DefaultsTo6()
    {
        var fresh = LightIntent.FromStatic(new LightState(0,0,0, PlayerLeds.Player1, LedBrightness.High));
        Assert.Equal(6, fresh.PlayerEffectFps);

        fresh.PlayerEffectFps = 12;
        Assert.True(IntentStore.Save(fresh).Success);
        Assert.Equal(12, IntentStore.Load()!.PlayerEffectFps);
    }
```
- [ ] **Step 2: Correr y ver fallar.**
- [ ] **Step 3: Implementar** — añadir la propiedad a `LightIntent` junto a las demás:
```csharp
        public int PlayerEffectFps { get; set; } = 6;
```
- [ ] **Step 4: Correr y ver pasar** — 251 + 1 = 252.
- [ ] **Step 5: Commit** — `git ... commit -m "feat: LightIntent recuerda la velocidad del efecto de LED (fps)"`

---

### Task 3: Barra VELOCIDAD del efecto de LED + cadencia por fps + Respiración en la UI (integración)

**Files:** Modify `HidusbfModernGui/MainWindow.xaml` y `HidusbfModernGui/MainWindow.xaml.cs`. (XAML y code juntos: el slider llama al handler y el code lee el slider — dependencia mutua, un solo commit.)

**Interfaces:** Consume `PlayerLedEffect.Breathe` (Task 1), `LightIntent.PlayerEffectFps` (Task 2). Produce `PlayerSpeed` (Slider), `PlayerSpeedText` (TextBlock), `PlayerSpeed_Changed`, `PlayerEffectFps` (propiedad).

- [ ] **Step 1: XAML** — en la fila de "LED DE JUGADOR", tras el ComboBox `PlayerEffectList` (el selector EFECTO), añadir la barra de velocidad. Y añadir "Respiracion" como item del combo se hace en el code (Step 4). Insertar:
```xml
                                                <TextBlock Text="VELOCIDAD" Style="{StaticResource FieldLabel}" VerticalAlignment="Center" Margin="14,0,8,0"/>
                                                <Slider x:Name="PlayerSpeed" Minimum="2" Maximum="20" Value="6" Width="110"
                                                        IsSnapToTickEnabled="True" TickFrequency="1"
                                                        VerticalAlignment="Center" ValueChanged="PlayerSpeed_Changed"/>
                                                <TextBlock x:Name="PlayerSpeedText" Text="" Style="{StaticResource DataText}"
                                                           VerticalAlignment="Center" Margin="8,0,0,0"/>
```
(Si la fila queda muy ancha, envolver EFECTO+VELOCIDAD en un WrapPanel o pasar a una fila nueva bajo Player/Brillo — a criterio, respetando el estilo.)

- [ ] **Step 2: Propiedad fps y su texto** — en `MainWindow.xaml.cs`:
```csharp
        // Velocidad del efecto de LED en frames/segundo (la barra VELOCIDAD del apartado del mando).
        private double PlayerEffectFps => PlayerSpeed?.Value ?? 6;

        private void UpdatePlayerSpeedText()
        {
            if (PlayerSpeedText == null || PlayerSpeed == null) return;
            PlayerSpeedText.Text = $"{PlayerSpeed.Value:0}/s";
        }
```

- [ ] **Step 3: Usar el fps en la cadencia** — en `Effect_Tick`, donde avanza el frame del efecto de LED, sustituir el umbral fijo del walker por el fps del usuario:
```csharp
                _playerWalker ??= new PlayerLedWalker(CurrentPlayerEffect);
                double frameMs = 1000.0 / PlayerEffectFps;
                _playerFrameAccumMs += _rainbowTimer!.Interval.TotalMilliseconds;
                if (_playerFrameAccumMs >= frameMs) { _playerFrameAccumMs -= frameMs; _playerFrameIndex++; }
                player = (PlayerLeds)_playerWalker.MaskAt(_playerFrameIndex);
```
Y en `UpdateEffectDriver`, cuando SOLO hay efecto de LED (no rainbow), el intervalo pasa a derivarse del fps en vez de `PlayerLedWalker.FrameMsFor(...)`:
```csharp
            _rainbowTimer.Interval = RainbowOn
                ? RainbowWalker.IntervalFor(TargetColoursPerSecond)
                : TimeSpan.FromMilliseconds(1000.0 / PlayerEffectFps);
```

- [ ] **Step 4: Poblar "Respiracion" en el combo + handler + habilitar/deshabilitar la barra** — en `BuildLightControls`, donde se llena `PlayerEffectList`, añadir la opción:
```csharp
                             ("Respiracion", PlayerLedEffect.Breathe),
```
(dejar "Ninguno" primero; el orden sugerido: Ninguno, Carga, Estrellas, Respiracion.)
Añadir el handler:
```csharp
        private void PlayerSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_rainbowTimer != null && !RainbowOn && PlayerEffectOn)
                _rainbowTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / PlayerEffectFps);
            UpdatePlayerSpeedText();
            if (!_updatingLight) RememberLight();
        }
```
En `PlayerEffect_Changed`, habilitar la barra solo cuando hay efecto: añadir `if (PlayerSpeed != null) PlayerSpeed.IsEnabled = PlayerEffectOn;` (junto a la línea que deshabilita `PlayerLedList`). Y llamar `UpdatePlayerSpeedText();` una vez al final de `BuildLightControls`.

- [ ] **Step 5: Persistencia del fps** — en `RememberLight()`, tras `intent.PlayerEffect = CurrentPlayerEffect;`, añadir `intent.PlayerEffectFps = (int)Math.Round(PlayerEffectFps);`. En `BuildLightControls`, en el bloque "init desde la intención guardada" (bajo `_updatingLight`), añadir:
```csharp
                    PlayerSpeed.Value = Math.Clamp(saved.PlayerEffectFps, 2, 20);
                    PlayerSpeed.IsEnabled = saved.PlayerEffect != PlayerLedEffect.None;
```

- [ ] **Step 6: Build + tests** — `dotnet build ...` (0 errores) y `dotnet test ...` (252). Si un exe bloquea la salida, pararlo primero.

- [ ] **Step 7: Verificación manual** — activar un efecto; la barra VELOCIDAD debe cambiar el ritmo en vivo; el texto muestra "N/s"; con Ninguno la barra se deshabilita; Respiracion abre/cierra desde el centro; cerrar y reabrir mantiene efecto+velocidad.

- [ ] **Step 8: Commit** — `git ... commit -m "feat: barra de velocidad del efecto de LED + animacion Respiracion"`

---

## Self-Review
- Cobertura: barra de velocidad → Task 3 (slider + fps en la cadencia + persistencia). Respiración → Task 1 + combo en Task 3. ✓
- Placeholders: ninguno.
- Consistencia: `PlayerEffectFps` (propiedad, Task 3) y `LightIntent.PlayerEffectFps` (Task 2) distintos nombres del mismo concepto (UI vs persistencia); RememberLight puentea uno a otro. `PlayerLedEffect.Breathe` (Task 1) usado en el combo. `PlayerLedWalker.FrameMs`/`FrameMsFor` quedan sin uso en MainWindow (los reemplaza el fps) — se dejan por los tests; marcar para posible limpieza.
- Riesgos para el review: (a) el acumulador ahora resta `frameMs` del fps del usuario, no del walker — confirmar que no hay doble fuente de verdad; (b) al cambiar la velocidad con el efecto corriendo y rainbow ON, el intervalo del timer es el del rainbow y el fps solo afecta el umbral del acumulador (correcto); (c) habilitar/deshabilitar `PlayerSpeed` en todos los caminos (cambio de efecto y restauración).

## Execution Handoff
Ejecución por subagentes (un agente por tarea, revisión entre cada una, revisión amplia al final).
