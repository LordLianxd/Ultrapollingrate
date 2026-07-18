# Persistencia + Overclock de un clic — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que UltraPolling recuerde el color/efecto del mando entre sesiones y lo reaplique al abrir y al reconectar; y que el overclock se aplique con un solo botón en vez de tres.

**Architecture:** Dos features independientes. (A) Overclock de un clic: un botón "APLICAR CAMBIOS" que encadena filtro → tasa → replug, reusando los métodos que ya existen. (B) Persistencia: un nuevo archivo de lógica pura `LightIntent` + `IntentStore` (espejo de `LightProfile`/`ProfileStore`) que guarda la última "intención de luz" en `active.json` y la reaplica en el arranque (tras el escaneo) y al reconectar el mando (hook `WM_DEVICECHANGE`). Sin bandeja y sin reafirmación durante el juego — decisión del usuario, riesgo anticheat mínimo.

**Tech Stack:** .NET 9, WPF, C#, System.Text.Json, xUnit (proyecto de tests existente).

## Global Constraints

- Objetivo `net9.0-windows`, x64. Build self-contained single-file portable (`package.ps1` + `Portable.pubxml`); ninguna dependencia NuGet nueva.
- Los archivos de **lógica pura no llevan WPF** (el proyecto de tests los enlaza por ruta). `LightIntent.cs` debe ser lógica pura.
- Paleta: exactamente diez colores; el color no decora fuera del picker/presets/swatch.
- UI en español; etiquetas de campo en MAYÚSCULAS sin acento.
- **Archivos congelados, NO tocar:** `DualSenseLight.cs`, `LightProfile.cs`/`ProfileStore`, `SystemManager.cs`, `PollingCore.cs`, `ColourMath.cs`, `ColourRamp.cs`, `RainbowWalker.cs`, `Theme.xaml`.
- **Commits sin la línea `Co-Authored-By`** (preferencia del usuario para este proyecto).
- Se trabaja en la carpeta del repo de GitHub: `C:\Users\Administrator\Downloads\work ultrapolling\UltraPolling`. Los `git push` los hace el usuario.
- Identidad git: `git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com"`.

---

# FEATURE A — Overclock de un clic (Opción B)

Hoy el usuario debe: pulsar **FILTRO**, elegir **TASA OBJETIVO**, y pulsar **RECONECTAR** (el único que aplica de verdad). Se reemplaza por: elegir la tasa y pulsar **un** botón "APLICAR CAMBIOS" que hace todo. Se conserva un enlace discreto "Restablecer" para emergencias (la vieja función de REINICIAR/quitar filtro).

Los métodos ya existen y no se tocan: `SystemManager.SetFilterActive`, `ApplyRate(model,rate)` (envuelve `SetDeviceRate`), `SystemManager.ReplugDevice`. El detalle crítico que ya hace `ReplugDevice_Click`: **parar el medidor antes del replug**, porque `CM_Query_And_Remove_SubTree` se veta si algo tiene el dispositivo abierto.

### Task A1: Orquestador de un clic en el code-behind

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (añadir handler `ApplyOverclock_Click`, junto a `ReplugDevice_Click` ~L1017)

**Interfaces:**
- Consumes (ya existen): `DevicesListBox.SelectedItem as UsbDeviceModel`; `DetailRateCombo.SelectedItem as ComboBoxItem` con `Tag` = slot `int`; `SystemManager.SetFilterActive(string,bool) -> OpResult`; `bool ApplyRate(UsbDeviceModel,int)`; `SystemManager.ReplugDevice(string) -> Task<OpResult>`; `_meter`, `_meterTimer`, `LogStatus(string)`, `ShowError(string,string)`, `RefreshDevicesList()`.
- Produces: `private async void ApplyOverclock_Click(object, RoutedEventArgs)` y `private async void ResetOverclock_Click(object, RoutedEventArgs)` (los consume el XAML de A2).

- [ ] **Step 1: Escribir el handler orquestador**

En `MainWindow.xaml.cs`, justo después de `ReplugDevice_Click` (~L1045), añadir:

```csharp
        // Un clic = todo el overclock. Encadena lo que antes eran tres botones: activa el
        // filtro, escribe la tasa y hace el replug (lo unico que la aplica de verdad).
        // Para el medidor antes del replug por la misma razon que ReplugDevice_Click:
        // CM_Query_And_Remove_SubTree se veta si algo tiene el dispositivo abierto.
        private async void ApplyOverclock_Click(object sender, RoutedEventArgs e)
        {
            if (DevicesListBox.SelectedItem is not UsbDeviceModel model)
            {
                LogStatus("Selecciona un dispositivo primero.");
                return;
            }
            if (DetailRateCombo.SelectedItem is not ComboBoxItem item)
            {
                LogStatus("Elige una TASA OBJETIVO.");
                return;
            }
            int rate = (int)item.Tag;

            ApplyOverclockBtn.IsEnabled = false;
            ResetOverclockBtn.IsEnabled = false;
            string original = (string)ApplyOverclockBtn.Content;
            ApplyOverclockBtn.Content = "APLICANDO...";
            _meter.Stop();
            _meterTimer?.Stop();
            try
            {
                var filter = SystemManager.SetFilterActive(model.InstanceId, true);
                if (!filter.Success) { LogStatus($"No se pudo activar el filtro: {filter.Error}"); return; }

                var rateRes = SystemManager.SetDeviceRate(model.InstanceId, model.DriverKey, rate, model.BusSpeed);
                if (!rateRes.Success) { LogStatus($"No se pudo escribir la tasa: {rateRes.Error}"); return; }

                var replug = await SystemManager.ReplugDevice(model.InstanceId);
                if (!replug.Success)
                {
                    LogStatus($"Reconexion fallida: {replug.Error}");
                    ShowError("Reconexion fallida", replug.Error!);
                    return;
                }
                LogStatus($"Overclock aplicado: {rate} Hz. Mueve el dispositivo para ver la tasa medida.");
            }
            finally
            {
                ApplyOverclockBtn.Content = original;
                ApplyOverclockBtn.IsEnabled = true;
                ResetOverclockBtn.IsEnabled = true;
                RefreshDevicesList();   // restaura la seleccion, lo que reinicia el medidor
            }
        }

        // Emergencia: quita el filtro y reconecta, dejando el dispositivo en su estado por
        // defecto. Sustituye a la vieja funcion de REINICIAR/quitar filtro manual.
        private async void ResetOverclock_Click(object sender, RoutedEventArgs e)
        {
            if (DevicesListBox.SelectedItem is not UsbDeviceModel model)
            {
                LogStatus("Selecciona un dispositivo primero.");
                return;
            }
            ApplyOverclockBtn.IsEnabled = false;
            ResetOverclockBtn.IsEnabled = false;
            _meter.Stop();
            _meterTimer?.Stop();
            try
            {
                var filter = SystemManager.SetFilterActive(model.InstanceId, false);
                if (!filter.Success) { LogStatus($"No se pudo quitar el filtro: {filter.Error}"); return; }

                var replug = await SystemManager.ReplugDevice(model.InstanceId);
                if (!replug.Success) { LogStatus($"Reconexion fallida: {replug.Error}"); return; }
                LogStatus($"{model.Name} restablecido a su estado por defecto.");
            }
            finally
            {
                ApplyOverclockBtn.IsEnabled = true;
                ResetOverclockBtn.IsEnabled = true;
                RefreshDevicesList();
            }
        }
```

- [ ] **Step 2: Quitar los handlers que ya no se usan desde el XAML**

`DetailRateCombo_SelectionChanged` ya **no** debe escribir la tasa al vuelo (ahora la escribe el botón). Cambiarlo para que solo recuerde la selección sin aplicar:

```csharp
        private void DetailRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // La tasa se aplica al pulsar APLICAR CAMBIOS, no al elegirla en la lista.
            // Este handler se deja vacio a proposito; el valor se lee del combo en
            // ApplyOverclock_Click.
        }
```

Dejar `FilterToggle_Click`, `RestartDevice_Click` y `ReplugDevice_Click` en el archivo por ahora (no se referencian desde el XAML tras A2; se eliminan como limpieza en el review final si el reviewer lo confirma como código muerto).

- [ ] **Step 3: Compilar**

Run: `dotnet build HidusbfModernGui/HidusbfModernGui.csproj -v q`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add HidusbfModernGui/MainWindow.xaml.cs
git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "feat: orquestador de overclock de un clic (filtro+tasa+replug)"
```

### Task A2: Reemplazar los tres botones por uno en el XAML

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml` (panel de detalle, ~L357-373)

**Interfaces:**
- Consumes: `ApplyOverclock_Click`, `ResetOverclock_Click` (Task A1). Estilos existentes `InstrumentButton`, `FieldLabel`; brush `TextLabelBrush`.
- Produces: botones nombrados `ApplyOverclockBtn`, `ResetOverclockBtn` (los usa A1).

- [ ] **Step 1: Sustituir el bloque de botones**

Reemplazar exactamente este bloque (líneas ~357-373):

```xml
                                        <StackPanel Orientation="Horizontal" Margin="0,22,0,0">
                                            <Button Content="FILTRO" Style="{StaticResource InstrumentButton}"
                                                    Click="FilterToggle_Click" Margin="0,0,10,0"/>
                                            <Button Content="REINICIAR" Style="{StaticResource InstrumentButton}"
                                                    Click="RestartDevice_Click"
                                                    ToolTip="Reinicio PnP: recarga los drivers, pero el dispositivo no abandona el bus USB. Rapido, y a veces no basta para aplicar la tasa."/>
                                        </StackPanel>

                                        <!-- The whole operation, not half of it. A PnP restart alone never re-reads
                                             the endpoint descriptor; a replug alone re-enumerates before hidusbf has
                                             attached. Only the two chained actually apply the rate - established by
                                             testing on real hardware, not from theory. -->
                                        <Button x:Name="ReplugBtn" Content="RECONECTAR (REPLUG)"
                                                Style="{StaticResource InstrumentButton}"
                                                HorizontalAlignment="Left" Margin="0,10,0,0"
                                                Click="ReplugDevice_Click"
                                                ToolTip="Quita el dispositivo del arbol PnP, espera 2 s, lo re-enumera y despues lo reinicia. Los cuatro pasos encadenados: es lo unico que aplica el overclock al 100%."/>
```

por:

```xml
                                        <Button x:Name="ApplyOverclockBtn" Content="APLICAR CAMBIOS"
                                                Style="{StaticResource InstrumentButton}"
                                                HorizontalAlignment="Left" Margin="0,22,0,0"
                                                Click="ApplyOverclock_Click"
                                                ToolTip="Activa el filtro, escribe la tasa elegida y reconecta el dispositivo (los tres pasos encadenados). Es lo unico que aplica el overclock al 100%."/>

                                        <Button x:Name="ResetOverclockBtn" Content="Restablecer valores"
                                                Click="ResetOverclock_Click"
                                                Background="Transparent" BorderThickness="0" Cursor="Hand"
                                                Foreground="{StaticResource TextLabelBrush}"
                                                HorizontalAlignment="Left" Padding="0" Margin="0,10,0,0" FontSize="11"
                                                ToolTip="Quita el filtro y reconecta, dejando el dispositivo en su estado por defecto."/>
```

- [ ] **Step 2: Compilar**

Run: `dotnet build HidusbfModernGui/HidusbfModernGui.csproj -v q`
Expected: `Build succeeded. 0 Error(s)` (si falla por `ReplugBtn`/`FilterToggle_Click` sin referencia, es esperado que compile igual: los handlers siguen existiendo en el code-behind; solo desaparecieron del XAML).

- [ ] **Step 3: Verificación manual (hardware)**

1. Reconstruir el exe: `powershell -NoProfile -ExecutionPolicy Bypass -File package.ps1` y correr `dist\UltraPolling\UltraPolling.exe` como Administrador.
2. Seleccionar un dispositivo, elegir una tasa, pulsar **APLICAR CAMBIOS**. El botón muestra "APLICANDO...", se deshabilita, y tras ~2 s la lista se refresca y el estado dice "Overclock aplicado: N Hz".
3. Mover el dispositivo y comprobar que la tasa MEDIDA sube.
4. Pulsar **Restablecer valores** y confirmar que el dispositivo vuelve a su tasa por defecto.

- [ ] **Step 4: Commit**

```bash
git add HidusbfModernGui/MainWindow.xaml
git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "feat: un solo boton APLICAR CAMBIOS en el panel de overclock"
```

---

# FEATURE B — Persistencia (recordar al abrir + al reconectar)

Guarda la última intención de luz (color fijo **o** rainbow, con player y brillo) en `active.json`, y la reaplica al abrir la app y al reconectar el mando por cable. **No** hay bandeja ni reafirmación durante el juego.

### Task B1: `LightIntent` + `IntentStore` (lógica pura, TDD)

**Files:**
- Create: `HidusbfModernGui/LightIntent.cs`
- Test: `HidusbfModernGui.Tests/LightIntentTests.cs`

**Interfaces:**
- Consumes (ya existen, lógica pura): `LightState(byte,byte,byte,PlayerLeds,LedBrightness)`, `PlayerLeds`, `LedBrightness` (en `DualSenseLight.cs`); `RainbowStyle` (en `ColourMath.cs`); `OpResult`.
- Produces:
  - `enum LightIntentKind { Static, Rainbow }`
  - `sealed class LightIntent` con props settables `Kind, R, G, B, Player, Brightness, Style, TicksPerColour`; métodos `LightState ToLightState()`, `static LightIntent FromStatic(LightState)`, `static LightIntent FromRainbow(RainbowStyle, int, PlayerLeds, LedBrightness)`.
  - `static class IntentStore` con `string Path`, `internal void OverrideDirectoryForTests(string?)`, `LightIntent? Load()`, `OpResult Save(LightIntent)`.

- [ ] **Step 1: Escribir el test que falla**

Crear `HidusbfModernGui.Tests/LightIntentTests.cs`:

```csharp
using System;
using System.IO;
using HidusbfModernGui;
using Xunit;

public class LightIntentTests : IDisposable
{
    private readonly string _dir;

    public LightIntentTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "UltraPollingTests_" + Guid.NewGuid().ToString("N"));
        IntentStore.OverrideDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        IntentStore.OverrideDirectoryForTests(null);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Static_RoundTrips()
    {
        var intent = LightIntent.FromStatic(new LightState(10, 20, 30, PlayerLeds.Player4, LedBrightness.Low));
        Assert.True(IntentStore.Save(intent).Success);

        var loaded = IntentStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal(LightIntentKind.Static, loaded!.Kind);
        Assert.Equal(10, loaded.R);
        Assert.Equal(20, loaded.G);
        Assert.Equal(30, loaded.B);
        Assert.Equal(PlayerLeds.Player4, loaded.Player);
        Assert.Equal(LedBrightness.Low, loaded.Brightness);
    }

    [Fact]
    public void Rainbow_RoundTrips_WithPlayerAndBrightness()
    {
        var intent = LightIntent.FromRainbow(RainbowStyle.Vivid, 7, PlayerLeds.Player2, LedBrightness.Medium);
        Assert.True(IntentStore.Save(intent).Success);

        var loaded = IntentStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal(LightIntentKind.Rainbow, loaded!.Kind);
        Assert.Equal(RainbowStyle.Vivid, loaded.Style);
        Assert.Equal(7, loaded.TicksPerColour);
        Assert.Equal(PlayerLeds.Player2, loaded.Player);
        Assert.Equal(LedBrightness.Medium, loaded.Brightness);
    }

    [Fact]
    public void Enums_PersistAsNames()
    {
        IntentStore.Save(LightIntent.FromRainbow(RainbowStyle.Vivid, 3, PlayerLeds.Player4, LedBrightness.Low));
        string json = File.ReadAllText(IntentStore.Path);
        Assert.Contains("Vivid", json);
        Assert.Contains("Player4", json);
        Assert.Contains("Low", json);
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        Assert.Null(IntentStore.Load());
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNull()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(IntentStore.Path, "{ this is not valid json");
        Assert.Null(IntentStore.Load());
    }

    [Fact]
    public void Save_CreatesBackupOnOverwrite()
    {
        IntentStore.Save(LightIntent.FromStatic(new LightState(1, 1, 1, PlayerLeds.Player1, LedBrightness.High)));
        IntentStore.Save(LightIntent.FromStatic(new LightState(2, 2, 2, PlayerLeds.Player1, LedBrightness.High)));
        Assert.True(File.Exists(IntentStore.Path + ".backup"));
    }

    [Fact]
    public void ToLightState_MapsColourFields()
    {
        var s = LightIntent.FromStatic(new LightState(9, 8, 7, PlayerLeds.Player3, LedBrightness.Medium)).ToLightState();
        Assert.Equal((byte)9, s.R);
        Assert.Equal((byte)8, s.G);
        Assert.Equal((byte)7, s.B);
        Assert.Equal(PlayerLeds.Player3, s.Player);
        Assert.Equal(LedBrightness.Medium, s.Brightness);
    }
}
```

- [ ] **Step 2: Correr los tests y verlos fallar**

Run: `dotnet test HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj -v q`
Expected: FALLA de compilación ("LightIntent/IntentStore no existe").

- [ ] **Step 3: Escribir `LightIntent.cs`**

Crear `HidusbfModernGui/LightIntent.cs`:

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace HidusbfModernGui
{
    public enum LightIntentKind { Static, Rainbow }

    // Lo ultimo que el usuario dejo puesto en el mando. A diferencia de LightProfile (un
    // preset con nombre que ademas guarda la tasa), esto es una sola cosa: el estado vivo
    // de la luz, para reaplicarlo al abrir la app y al reconectar el mando.
    //
    // Clase mutable con props settables por la misma razon que LightProfile: System.Text.Json
    // necesita constructor sin parametros para round-tripear sin ceremonia. Los campos de LED
    // (Player, Brightness) van siempre, tambien en modo Rainbow, porque el tick del rainbow
    // construye su LightState con ellos. El color por-tick NO se guarda: lo deriva el walker.
    public sealed class LightIntent
    {
        public LightIntentKind Kind { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public PlayerLeds Player { get; set; } = PlayerLeds.Player1;
        public LedBrightness Brightness { get; set; } = LedBrightness.High;
        public RainbowStyle Style { get; set; } = RainbowStyle.Smooth;
        public int TicksPerColour { get; set; } = 3;

        public LightState ToLightState() => new LightState(R, G, B, Player, Brightness);

        public static LightIntent FromStatic(LightState s) => new LightIntent
        {
            Kind = LightIntentKind.Static,
            R = s.R, G = s.G, B = s.B, Player = s.Player, Brightness = s.Brightness
        };

        public static LightIntent FromRainbow(RainbowStyle style, int ticksPerColour,
                                              PlayerLeds player, LedBrightness brightness) => new LightIntent
        {
            Kind = LightIntentKind.Rainbow,
            Style = style, TicksPerColour = ticksPerColour,
            Player = player, Brightness = brightness
        };
    }

    // Espejo de ProfileStore: mismo %APPDATA%\UltraPolling, misma escritura atomica con
    // copia .backup, mismos Options (enums como nombre). Se duplica a proposito para no
    // tocar ProfileStore/LightProfile.cs, que estan congelados.
    public static class IntentStore
    {
        private static string? _overrideDir;

        internal static void OverrideDirectoryForTests(string? dir) => _overrideDir = dir;

        private static string Directory_ => _overrideDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraPolling");

        public static string Path => System.IO.Path.Combine(Directory_, "active.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        public static LightIntent? Load()
        {
            try
            {
                if (!File.Exists(Path)) return null;
                string json = File.ReadAllText(Path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<LightIntent>(json, Options);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IntentStore.Load failed, ignoring: {ex.Message}");
                return null;
            }
        }

        public static OpResult Save(LightIntent intent)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory_);
                if (File.Exists(Path)) File.Copy(Path, Path + ".backup", true);
                File.WriteAllText(Path, JsonSerializer.Serialize(intent, Options));
                return OpResult.Ok();
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"No se pudo guardar la intencion de luz: {ex.Message}");
            }
        }
    }
}
```

- [ ] **Step 4: Añadir el archivo al proyecto de tests**

En `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj`, junto a los otros `<Compile Include="..\HidusbfModernGui\...">`, añadir:

```xml
    <Compile Include="..\HidusbfModernGui\LightIntent.cs" Link="LightIntent.cs" />
```

- [ ] **Step 5: Correr los tests y verlos pasar**

Run: `dotnet test HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj -v q`
Expected: PASA todo (los 242 previos + 7 nuevos = 249).

- [ ] **Step 6: Commit**

```bash
git add HidusbfModernGui/LightIntent.cs HidusbfModernGui.Tests/LightIntentTests.cs HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj
git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "feat: LightIntent + IntentStore (persistencia de la luz, TDD)"
```

### Task B2: Guardar la intención en cada cambio de luz (con debounce)

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `LightIntent`, `IntentStore` (Task B1); `CurrentLight() -> LightState`; el estado del rainbow (`_rainbowWalker`/`_rainbowTimer` activos, `RainbowStyleList`, `RainbowSpeed`); `PlayerLedList`, `BrightnessList`; `ApplyLightNow()`, `Rainbow_Toggled`, `RainbowStyle_Changed`, `RainbowSpeed_Changed`.
- Produces: `private void RememberLight()` y un `DispatcherTimer _intentSave`.

- [ ] **Step 1: Añadir el campo del timer y el guardado con debounce**

Junto a los otros timers (~L259-267), añadir:

```csharp
        // Guarda la intencion de luz en disco, agrupando rafagas (arrastrar el picker, girar
        // el rainbow) en una sola escritura. NUNCA se llama por-tick del rainbow.
        private DispatcherTimer? _intentSave;
```

Añadir el método (junto a `ApplyLightNow`, ~L426):

```csharp
        // Construye la intencion actual (color fijo o rainbow) y agenda su guardado.
        private void RememberLight()
        {
            if (_updatingLight) return;                 // no persistir cambios programaticos
            if (PlayerLedList.SelectedItem == null || BrightnessList.SelectedItem == null) return;

            var player = (PlayerLeds)((ComboBoxItem)PlayerLedList.SelectedItem).Tag;
            var brightness = (LedBrightness)((ComboBoxItem)BrightnessList.SelectedItem).Tag;

            LightIntent intent;
            if (_rainbowTimer != null && _rainbowTimer.IsEnabled)
            {
                var style = (RainbowStyle)((ComboBoxItem)RainbowStyleList.SelectedItem).Tag;
                intent = LightIntent.FromRainbow(style, (int)RainbowSpeed.Value, player, brightness);
            }
            else
            {
                intent = LightIntent.FromStatic(CurrentLight());
            }

            if (_intentSave == null)
            {
                _intentSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
                _intentSave.Tick += (s, e) => { _intentSave!.Stop(); IntentStore.Save(_lastIntent!); };
            }
            _lastIntent = intent;
            _intentSave.Stop();
            _intentSave.Start();
        }

        private LightIntent? _lastIntent;
```

- [ ] **Step 2: Llamar `RememberLight()` desde cada setter de luz**

Añadir `RememberLight();` al final (antes del cierre) de estos métodos, tras aplicar el cambio: `ApplyLightNow()`, `Rainbow_Toggled` (cuando se activa/desactiva), `RainbowStyle_Changed`, `RainbowSpeed_Changed`, y `RestoreLight_Click`. Ejemplo en `ApplyLightNow` (tras la línea del `Apply`):

```csharp
            var result = DualSenseLight.Apply(model.InstanceId, CurrentLight());
            if (!result.Success) LogStatus($"No se pudo cambiar la luz: {result.Error}");
            RememberLight();
```

(Repetir el patrón: una llamada a `RememberLight();` al final de cada uno de los métodos citados.)

- [ ] **Step 3: Compilar**

Run: `dotnet build HidusbfModernGui/HidusbfModernGui.csproj -v q`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add HidusbfModernGui/MainWindow.xaml.cs
git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "feat: guardar la intencion de luz al cambiarla (debounce 750ms)"
```

### Task B3: Reaplicar la intención al arrancar

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `IntentStore.Load()`; el callback donde el escaneo puebla `_allDevices` (dentro del `Dispatcher.Invoke` de `RefreshDevicesList`); `PlayStationList`, `DualSenseLight.IsPlayStation`; `_updatingLight`; el arranque del rainbow (`Rainbow_Toggled`/construcción del walker).
- Produces: `private void ReapplyIntent()`; un flag `_intentReapplied` para hacerlo una sola vez.

- [ ] **Step 1: Escribir `ReapplyIntent()`**

> **Nota crítica de timing:** `Window_Loaded` llama `RefreshDevicesList`, que escanea en `Task.Run` y puebla `_allDevices` **más tarde**, dentro de su `Dispatcher.Invoke`. Reaplicar justo después de llamar `RefreshDevicesList` encontraría la lista **vacía**. Por eso `ReapplyIntent()` se llama **desde dentro** de ese `Dispatcher.Invoke` de fin-de-escaneo (busca en `RefreshDevicesList` el punto donde ya se asignó `_allDevices` y se refrescó la UI), protegido por `_intentReapplied` para que corra una sola vez.

```csharp
        private bool _intentReapplied;

        // Reaplica la ultima intencion de luz al primer DualSense presente. Se llama una
        // vez, desde el fin del escaneo (cuando _allDevices ya esta poblada).
        private void ReapplyIntent()
        {
            if (_intentReapplied) return;
            var intent = IntentStore.Load();
            if (intent == null) { _intentReapplied = true; return; }

            var pad = _allDevices.FirstOrDefault(DualSenseLight.IsPlayStation);
            if (pad == null) return;   // sin mando aun; se reintenta al reconectar (Task B4)
            _intentReapplied = true;

            if (intent.Kind == LightIntentKind.Static)
            {
                DualSenseLight.Apply(pad.InstanceId, intent.ToLightState());
            }
            else
            {
                DualSenseLight.Apply(pad.InstanceId, intent.ToLightState()); // color base + LEDs
                // El arranque real del rainbow (walker + timer) se hace cuando el usuario
                // abre la pestana; aqui se deja el mando en un color valido con los LEDs
                // correctos para no arrancar animacion en segundo plano en el Dashboard.
            }
            LogStatus("Color del mando restaurado de la ultima sesion.");
        }
```

- [ ] **Step 2: Llamar `ReapplyIntent()` al final del `Dispatcher.Invoke` de `RefreshDevicesList`**

En `RefreshDevicesList`, dentro del `Dispatcher.Invoke` que asigna `_allDevices` y actualiza la UI, añadir al final: `ReapplyIntent();`

- [ ] **Step 3: Compilar**

Run: `dotnet build HidusbfModernGui/HidusbfModernGui.csproj -v q`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Verificación manual**

1. Abrir la app, poner un color/rainbow y Player 4 en el mando. Cerrar la app.
2. Reabrir. El mando debe volver al color y Player de la última sesión sin tocar nada.

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/MainWindow.xaml.cs
git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "feat: reaplicar la luz guardada al arrancar (tras el escaneo)"
```

### Task B4: Reaplicar al reconectar el mando

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `IntentStore.Load()`, `_allDevices`, `DualSenseLight`, `RefreshDevicesList()`; `SourceInitialized` de la ventana (WPF).
- Produces: hook `WM_DEVICECHANGE` vía `HwndSource`, con debounce, que reaplica la intención.

- [ ] **Step 1: Instalar el hook en `SourceInitialized`**

Añadir al constructor o a `Window_Loaded` una suscripción a `SourceInitialized` (o overridear `OnSourceInitialized`). Añadir estos miembros y método:

```csharp
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVNODES_CHANGED = 0x0007;
        private DispatcherTimer? _deviceChangeDebounce;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var src = System.Windows.Interop.HwndSource.FromHwnd(
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
            src?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DEVICECHANGE && wParam.ToInt32() == DBT_DEVNODES_CHANGED)
            {
                // Los cambios en el arbol de dispositivos llegan en rafaga; agrupa antes de
                // reaccionar. No es el escaneo pesado de PowerShell: solo refresca y reaplica.
                if (_deviceChangeDebounce == null)
                {
                    _deviceChangeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    _deviceChangeDebounce.Tick += (s, ev) =>
                    {
                        _deviceChangeDebounce!.Stop();
                        _intentReapplied = false;   // permitir reaplicar al mando reaparecido
                        RefreshDevicesList();        // repuebla _allDevices y llama ReapplyIntent()
                    };
                }
                _deviceChangeDebounce.Stop();
                _deviceChangeDebounce.Start();
            }
            return IntPtr.Zero;
        }
```

> Nota: `RefreshDevicesList` ya llama `ReapplyIntent()` al final (Task B3). Al poner `_intentReapplied = false` antes, la reconexión del mando dispara una reaplicación. `ReapplyIntent` no hace nada si no hay intención guardada o no hay mando, así que es seguro llamarlo en cada cambio.

- [ ] **Step 2: Compilar**

Run: `dotnet build HidusbfModernGui/HidusbfModernGui.csproj -v q`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Verificación manual**

1. Con la app abierta y un color guardado, desconectar el cable del mando y volver a conectarlo.
2. El color debe volver solo en un par de segundos, sin tocar la app.

- [ ] **Step 4: Commit**

```bash
git add HidusbfModernGui/MainWindow.xaml.cs
git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "feat: reaplicar la luz al reconectar el mando (WM_DEVICECHANGE)"
```

---

## Self-Review

**Cobertura del spec/decisiones:**
- §3 botón único (Opción B) → Feature A (A1 orquestador, A2 XAML). ✓ Incluye el enlace "Restablecer" y el parar-medidor-antes-del-replug.
- §2 persistencia "recordar al abrir + reconectar" → Feature B (B1 modelo+store, B2 guardar, B3 reaplicar al arrancar, B4 reaplicar al reconectar). ✓ Sin bandeja ni reafirmación durante el juego (fuera de alcance por decisión del usuario).
- Sidebar (casita uniforme) → ya hecho antes de este plan (commit `7e3dcc2`), no se replanifica.
- Brillo (¿atenúa color?) → **fuera de este plan**, pendiente de la prueba de hardware del usuario (Player 4 vs Player 1). Se planifica aparte según el resultado.

**Placeholders:** ninguno; todo el código de cada paso está escrito.

**Consistencia de tipos:** `LightIntent`/`IntentStore`/`LightIntentKind` usados en B2–B4 coinciden con las firmas definidas en B1. `RememberLight`/`_lastIntent`/`ReapplyIntent`/`_intentReapplied` se definen en B2/B3 y se reusan en B4. Los métodos de `SystemManager` (`SetFilterActive`, `SetDeviceRate`, `ReplugDevice`) y `ApplyRate` se consumen con las firmas reales verificadas en el código.

**Riesgo señalado para el review:** en A2, si el reviewer confirma que `FilterToggle_Click`, `RestartDevice_Click`, `ReplugDevice_Click` y `ReplugBtn` quedan sin referencia, eliminarlos como código muerto en un commit de limpieza (no antes, por si la verificación manual de A2 falla y hay que volver atrás).

## Execution Handoff

Plan completo y guardado en `docs/superpowers/plans/2026-07-17-persistence-and-oneclick-overclock.md`. Dos opciones de ejecución:

1. **Subagent-Driven (recomendado)** — un subagente fresco por tarea, con review entre tareas, iteración rápida.
2. **Inline** — ejecutar las tareas en esta sesión con checkpoints de revisión.

¿Cuál prefieres?
