# Rediseño de la UI del mando: menos botones, mando grande y remapeo visual — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convertir el configurador del mando de una columna larga de texto y botones sueltos en una pantalla de trabajo: el mando grande al centro, el remapeo se hace **haciendo clic sobre el propio mando**, los textos largos se esconden tras un "?", las cuatro acciones de skin/streamer se juntan en un botón circular, los gatillos tienen medidor en vivo, y **un solo perfil** guarda luz + configuración del mando.

**Architecture:** El núcleo nuevo es puro y testeable: `GameProfile`/`GameProfileStore` (perfil unificado + migración desde los dos archivos actuales) y la aritmética de hit-testing (`PadVisualMath.ToBaseCoords` + zonas por botón). La UI se reorganiza alrededor de `PadVisualHost`, que pasa de ser un dibujo pasivo a un control interactivo: expone sus zonas en coordenadas de la imagen base y emite `ButtonClicked`. Los textos largos salen del flujo a `Popup`s de ayuda. La rejilla del configurador pasa de una columna a columnas adaptativas.

**Tech Stack:** .NET 9 WPF, xUnit, System.Text.Json. Sin dependencias nuevas.

## Global Constraints

- UI en **español**, tema **monocromo** (excepciones ya vigentes: los 3 puntos del editor de curvas y el arte de un skin).
- El proyecto de tests **linkea fuentes individualmente** en `HidusbfModernGui.Tests.csproj`: **todo archivo nuevo del núcleo hay que añadirlo ahí**. Nada de WPF/HidSharp/Nefarius en tests.
- **Nunca perder perfiles del usuario.** La migración **lee** `profiles.json` y `remap-profiles.json` y **no los borra ni los modifica**: quedan como respaldo. Si la migración falla, la app arranca con lista vacía pero los archivos viejos siguen intactos.
- El motor (`RemapEngine`, `VisualizerFeed`, `PadSkin`) **no se toca**: este plan es de presentación e interacción.
- Todo lo que se quite de la pantalla tiene que seguir siendo **alcanzable** (el aviso de anticheat va a Ajustes; las descripciones van a un "?"). Quitar información sin destino es perder información.
- Commits **sin** Co-Authored-By. El push lo hace el usuario.

## Contexto verificado

- `MainWindow.xaml`: pestaña "Light" = hub del mando, con sub-nav `ConfigTabBtn`/`LucesTabBtn` → `ConfigPanel` / `LucesPanel`. Dentro de `ConfigPanel`: aviso anticheat (Border), tarjeta "MANDO VIRTUAL" (`MasterToggleBtn` + `MasterStatusText`), tarjeta "MANDO EN VIVO" (`ConfigPadVisual` + `SkinStatusText` + `ReloadSkin`/`CalibrationCheck`/`StreamerToggle`/`StreamerClickThrough`), botones de pestaña `StickTabBtn`/`GatilloTabBtn`/`TouchpadTabBtn`/`BotonTabBtn` → `TabSticks`/`TabGatillos`/`TabTouchpad`/`TabBotones`, y "PERFILES DEL REMAPEO".
- `PadVisualHost` envuelve `PadVisual` (vector, canvas base 360×260) y `SkinnedPadVisual` (skin, canvas base `BaseWidth`×`BaseHeight`), ambos dentro de un `Viewbox Stretch="Uniform"`. Ambos exponen `Update(ControllerState)`.
- `PadSkin.Parts` es un `Dictionary<string, SkinPart>` donde `SkinPart.Dst` es el rectángulo del botón **en píxeles de la imagen base** — es decir, **el skin ya contiene la geometría de cada botón**: es la fuente de las zonas clicables, no hay que medir nada nuevo.
- Claves de parte existentes: `stick.left/right`, `btn.cross/circle/square/triangle`, `dpad.up/down/left/right`, `btn.l1/r1/l2/r2`, `btn.share/options/ps/touchpad`.
- Perfiles hoy: `LightProfile{Name, Rate?, R,G,B, Player, Brightness, Rainbow}` en `profiles.json`; `RemapProfile{Name, Settings}` en `remap-profiles.json`; `LightIntent` (más rico que LightProfile: `Kind, R,G,B, Player, Brightness, Style, RainbowColoursPerSecond, PlayerEffect, PlayerEffectFps`) en `intents.json`.
- `RemapSettings` tiene `ButtonRemap: Dictionary<PadButton,PadButton>` y `TouchZoneMap: Dictionary<TouchZone,PadButton>`.

## Estructura de archivos

- Create: `HidusbfModernGui/GameProfile.cs` (perfil unificado + store + migración, puro)
- Create: `HidusbfModernGui/PadHitZones.cs` (zonas por botón + mapeo de coordenadas, puro)
- Create: `HidusbfModernGui/ProfilesBar.xaml(.cs)` (componente único de perfiles)
- Create: `HidusbfModernGui/RemapPopup.xaml(.cs)` (elegir destino al pulsar un botón del mando)
- Modify: `HidusbfModernGui/PadVisual.xaml.cs`, `SkinnedPadVisual.xaml.cs`, `PadVisualHost.xaml(.cs)` (zonas + evento de clic)
- Modify: `HidusbfModernGui/PadVisualMath.cs` (mapeo Viewbox → base)
- Modify: `HidusbfModernGui/MainWindow.xaml(.cs)` (toda la reorganización)
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj` (+2 links)
- Test: `HidusbfModernGui.Tests/GameProfileTests.cs`, `PadHitZonesTests.cs`, `PadVisualMathTests.cs`
- Modify: `README.md`, `docs/DOCUMENTACION.md`

---

### Task 1: `GameProfile` — un perfil que guarda luz + mando, con migración (TDD)

**Files:**
- Create: `HidusbfModernGui/GameProfile.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj`
- Test: `HidusbfModernGui.Tests/GameProfileTests.cs`

**Interfaces:**
- Produces: `GameProfile { string Name; int? Rate; LightIntent? Light; RemapSettings? Remap; }`; `GameProfileStore.Load()/Save(IEnumerable<GameProfile>)/Path/OverrideDirectoryForTests`; `GameProfileStore.Migrate(IEnumerable<LightProfile> oldLight, IEnumerable<RemapProfile> oldRemap) -> List<GameProfile>` (pura, sin disco). Consumido por `ProfilesBar` (Task 2) y `MainWindow`.

**Regla de migración:** un perfil de luz y uno de remapeo **con el mismo nombre** se funden en uno solo. Los que no casan entran con la mitad que tengan (`Light` o `Remap` en null = "este perfil no toca eso"). El pseudo-perfil `__ultimo_usado__` del remapeo **no** se migra: es estado interno, no un perfil del usuario.

- [ ] **Step 1: Link en el csproj** (antes de los tests):

```xml
<Compile Include="..\HidusbfModernGui\GameProfile.cs" Link="GameProfile.cs" />
```

- [ ] **Step 2: Tests que fallan** — crear `HidusbfModernGui.Tests/GameProfileTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HidusbfModernGui;
using Xunit;

public class GameProfileTests : IDisposable
{
    private readonly string _dir;

    public GameProfileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "UltraPollingGP_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        GameProfileStore.OverrideDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        GameProfileStore.OverrideDirectoryForTests(null);
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Load_WithoutFile_ReturnsEmpty() => Assert.Empty(GameProfileStore.Load());

    [Fact]
    public void SaveAndLoad_RoundTripsLightAndRemap()
    {
        var p = new GameProfile
        {
            Name = "Warzone",
            Rate = 8000,
            Light = new LightIntent { R = 10, G = 20, B = 30, PlayerEffect = PlayerLedEffect.Breathe },
            Remap = new RemapSettings { LeftDeadzonePct = 12, LeftCurve = ResponseCurve.Propia },
        };
        Assert.True(GameProfileStore.Save(new[] { p }).Success);

        var back = GameProfileStore.Load();
        Assert.Single(back);
        Assert.Equal("Warzone", back[0].Name);
        Assert.Equal(8000, back[0].Rate);
        Assert.Equal(20, back[0].Light!.G);
        Assert.Equal(PlayerLedEffect.Breathe, back[0].Light!.PlayerEffect);
        Assert.Equal(12, back[0].Remap!.LeftDeadzonePct);
        Assert.Equal(ResponseCurve.Propia, back[0].Remap!.LeftCurve);
    }

    [Fact]
    public void Migrate_MergesByName()
    {
        var light = new[] { new LightProfile { Name = "Apex", Rate = 1000, R = 255 } };
        var remap = new[] { new RemapProfile { Name = "Apex", Settings = new RemapSettings { RightDeadzonePct = 7 } } };

        var merged = GameProfileStore.Migrate(light, remap);

        var apex = Assert.Single(merged);
        Assert.Equal("Apex", apex.Name);
        Assert.Equal(1000, apex.Rate);
        Assert.Equal(255, apex.Light!.R);
        Assert.Equal(7, apex.Remap!.RightDeadzonePct);
    }

    [Fact]
    public void Migrate_KeepsUnmatchedHalves()
    {
        var light = new[] { new LightProfile { Name = "SoloLuz", B = 99 } };
        var remap = new[] { new RemapProfile { Name = "SoloMando", Settings = new RemapSettings { L2PointPct = 40 } } };

        var merged = GameProfileStore.Migrate(light, remap);

        Assert.Equal(2, merged.Count);
        var luz = merged.Single(p => p.Name == "SoloLuz");
        Assert.NotNull(luz.Light);
        Assert.Null(luz.Remap);
        var mando = merged.Single(p => p.Name == "SoloMando");
        Assert.Null(mando.Light);
        Assert.Equal(40, mando.Remap!.L2PointPct);
    }

    [Fact]
    public void Migrate_SkipsInternalLastUsedPseudoProfile()
    {
        var remap = new[]
        {
            new RemapProfile { Name = "__ultimo_usado__", Settings = new RemapSettings() },
            new RemapProfile { Name = "Real", Settings = new RemapSettings() },
        };
        var merged = GameProfileStore.Migrate(Array.Empty<LightProfile>(), remap);
        Assert.Equal("Real", Assert.Single(merged).Name);
    }

    [Fact]
    public void Migrate_IsCaseInsensitiveOnName()
    {
        var light = new[] { new LightProfile { Name = "duo", R = 5 } };
        var remap = new[] { new RemapProfile { Name = "DUO", Settings = new RemapSettings() } };
        var merged = GameProfileStore.Migrate(light, remap);
        Assert.Single(merged);
        Assert.NotNull(merged[0].Light);
        Assert.NotNull(merged[0].Remap);
    }

    [Fact]
    public void Migrate_EmptyInputs_ReturnsEmpty()
        => Assert.Empty(GameProfileStore.Migrate(Array.Empty<LightProfile>(), Array.Empty<RemapProfile>()));
}
```

- [ ] **Step 3: Verificar que fallan** — `dotnet test HidusbfModernGui.Tests\HidusbfModernGui.Tests.csproj --filter "FullyQualifiedName~GameProfileTests"`. Esperado: error de compilación.

- [ ] **Step 4: Implementación** — crear `HidusbfModernGui/GameProfile.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HidusbfModernGui
{
    // Un perfil de juego: TODO lo que el usuario configura para una sesion, en un solo
    // sitio - la luz, la configuracion del mando y (opcional) la tasa de sondeo. Sustituye
    // a la pareja LightProfile + RemapProfile, que obligaba a guardar y cargar dos veces
    // cosas que en la practica van juntas ("mi perfil de Warzone").
    //
    // Cualquiera de las dos mitades puede ser null: un perfil que solo cambia la luz deja
    // Remap en null y no toca la configuracion del mando al cargarse, y viceversa.
    public sealed class GameProfile
    {
        public string Name { get; set; } = "";
        public int? Rate { get; set; }              // null = no tocar la tasa
        public LightIntent? Light { get; set; }     // null = no tocar la luz
        public RemapSettings? Remap { get; set; }   // null = no tocar el mando
    }

    // Espejo de los stores existentes: mismo %APPDATA%\UltraPolling, escritura atomica con
    // copia .backup, enums por nombre. Archivo propio (game-profiles.json).
    public static class GameProfileStore
    {
        // El remapeo guarda su ultimo estado como un pseudo-perfil con este nombre. Es
        // estado interno de la app, no un perfil del usuario: no se migra ni se lista.
        public const string LastUsedPseudoProfile = "__ultimo_usado__";

        private static string? _overrideDir;

        internal static void OverrideDirectoryForTests(string? dir) => _overrideDir = dir;

        private static string Directory_ => _overrideDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraPolling");

        public static string Path => System.IO.Path.Combine(Directory_, "game-profiles.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        public static List<GameProfile> Load()
        {
            try
            {
                if (!File.Exists(Path)) return new List<GameProfile>();
                string json = File.ReadAllText(Path);
                if (string.IsNullOrWhiteSpace(json)) return new List<GameProfile>();
                return JsonSerializer.Deserialize<List<GameProfile>>(json, Options) ?? new List<GameProfile>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GameProfileStore.Load failed, starting empty: {ex.Message}");
                return new List<GameProfile>();
            }
        }

        public static OpResult Save(IEnumerable<GameProfile> profiles)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory_);
                if (File.Exists(Path)) File.Copy(Path, Path + ".backup", true);
                File.WriteAllText(Path, JsonSerializer.Serialize(profiles, Options));
                return OpResult.Ok();
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"No se pudieron guardar los perfiles: {ex.Message}");
            }
        }

        // Funde los perfiles viejos en los nuevos. Pura (no toca disco) para poder probarla.
        // Los que comparten nombre (sin distinguir mayusculas) se unen en uno; los demas
        // entran con la mitad que tengan. Los archivos viejos NO se tocan: siguen en disco
        // como respaldo por si algo sale mal.
        public static List<GameProfile> Migrate(IEnumerable<LightProfile> light, IEnumerable<RemapProfile> remap)
        {
            var byName = new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase);

            foreach (var l in light ?? Enumerable.Empty<LightProfile>())
            {
                if (l == null || string.IsNullOrWhiteSpace(l.Name)) continue;
                var g = Get(byName, l.Name);
                g.Rate = l.Rate;
                g.Light = new LightIntent
                {
                    Kind = l.Rainbow ? LightIntentKind.Rainbow : LightIntentKind.Static,
                    R = l.R, G = l.G, B = l.B,
                    Player = l.Player,
                    Brightness = l.Brightness,
                };
            }

            foreach (var r in remap ?? Enumerable.Empty<RemapProfile>())
            {
                if (r == null || string.IsNullOrWhiteSpace(r.Name)) continue;
                if (string.Equals(r.Name, LastUsedPseudoProfile, StringComparison.OrdinalIgnoreCase)) continue;
                Get(byName, r.Name).Remap = r.Settings;
            }

            return byName.Values.ToList();

            static GameProfile Get(Dictionary<string, GameProfile> map, string name)
            {
                if (!map.TryGetValue(name, out var g))
                {
                    g = new GameProfile { Name = name };
                    map[name] = g;
                }
                return g;
            }
        }
    }
}
```

- [ ] **Step 5: Verificar que pasan** — mismo filtro PASS + suite completa PASS.
- [ ] **Step 6: Commit** — `git add HidusbfModernGui/GameProfile.cs HidusbfModernGui.Tests/GameProfileTests.cs HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj && git commit -m "feat: GameProfile - un perfil unico con luz y mando, con migracion (TDD)"`

---

### Task 2: `PadHitZones` — de un clic a un botón del mando (TDD)

**Files:**
- Create: `HidusbfModernGui/PadHitZones.cs`
- Modify: `HidusbfModernGui/PadVisualMath.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj`
- Test: `HidusbfModernGui.Tests/PadHitZonesTests.cs`, `PadVisualMathTests.cs`

**Interfaces:**
- Produces: `PadVisualMath.ToBaseCoords(double clickX, double clickY, double controlW, double controlH, double baseW, double baseH) -> (double X, double Y)?` (null si el clic cae en las bandas vacías del Viewbox); `PadZone(PadButton Button, double X, double Y, double W, double H)`; `PadHitZones.Find(IReadOnlyList<PadZone> zones, double x, double y) -> PadButton?` (la zona **más pequeña** que contiene el punto, para que un botón encima de una zona grande gane). Consumido por `PadVisualHost` (Task 3).

- [ ] **Step 1: Link en el csproj**:

```xml
<Compile Include="..\HidusbfModernGui\PadHitZones.cs" Link="PadHitZones.cs" />
```

- [ ] **Step 2: Tests que fallan** — añadir a `PadVisualMathTests.cs`:

```csharp
[Fact]
public void ToBaseCoords_ExactFit_MapsProportionally()
{
    // Control 200x100 sobre una base 400x200: escala 0.5, sin bandas.
    var p = PadVisualMath.ToBaseCoords(100, 50, 200, 100, 400, 200);
    Assert.NotNull(p);
    Assert.Equal(200, p!.Value.X, 3);
    Assert.Equal(100, p.Value.Y, 3);
}

[Fact]
public void ToBaseCoords_LetterboxedHorizontally_SubtractsTheBands()
{
    // Base 100x100 en un control 300x100: escala 1, banda de 100 a cada lado.
    var p = PadVisualMath.ToBaseCoords(150, 50, 300, 100, 100, 100);
    Assert.NotNull(p);
    Assert.Equal(50, p!.Value.X, 3);
    Assert.Equal(50, p.Value.Y, 3);
}

[Fact]
public void ToBaseCoords_ClickOnTheEmptyBand_IsNull()
{
    Assert.Null(PadVisualMath.ToBaseCoords(10, 50, 300, 100, 100, 100));
}

[Fact]
public void ToBaseCoords_DegenerateSizes_AreNullNotCrash()
{
    Assert.Null(PadVisualMath.ToBaseCoords(5, 5, 0, 100, 100, 100));
    Assert.Null(PadVisualMath.ToBaseCoords(5, 5, 100, 100, 0, 100));
}
```

y crear `HidusbfModernGui.Tests/PadHitZonesTests.cs`:

```csharp
using System.Collections.Generic;
using HidusbfModernGui;
using Xunit;

public class PadHitZonesTests
{
    private static List<PadZone> Zones() => new()
    {
        new PadZone(PadButton.TouchpadClick, 0, 0, 100, 100),   // grande
        new PadZone(PadButton.Cross, 40, 40, 20, 20),           // pequena, encima
    };

    [Fact]
    public void Find_InsideSmallZone_PrefersTheSmallest()
        => Assert.Equal(PadButton.Cross, PadHitZones.Find(Zones(), 50, 50));

    [Fact]
    public void Find_InsideOnlyTheBigZone_ReturnsIt()
        => Assert.Equal(PadButton.TouchpadClick, PadHitZones.Find(Zones(), 10, 10));

    [Fact]
    public void Find_Outside_ReturnsNull()
        => Assert.Null(PadHitZones.Find(Zones(), 500, 500));

    [Fact]
    public void Find_EmptyOrNull_ReturnsNull()
    {
        Assert.Null(PadHitZones.Find(new List<PadZone>(), 5, 5));
        Assert.Null(PadHitZones.Find(null, 5, 5));
    }

    [Fact]
    public void Find_OnTheBorder_CountsAsInside()
        => Assert.Equal(PadButton.Cross, PadHitZones.Find(Zones(), 40, 40));
}
```

- [ ] **Step 3: Verificar que fallan** (compilación).

- [ ] **Step 4: Implementación** — añadir a `PadVisualMath.cs`:

```csharp
// Un Viewbox Stretch="Uniform" escala el lienzo base al maximo que quepa y centra el
// sobrante en bandas vacias. Para saber que boton hay bajo el raton hay que deshacer esas
// dos cosas: quitar la banda y dividir por la escala. Devuelve null si el clic cayo en la
// banda (fuera del mando), que no es un fallo sino "ahi no hay nada".
public static (double X, double Y)? ToBaseCoords(double clickX, double clickY,
    double controlW, double controlH, double baseW, double baseH)
{
    if (controlW <= 0 || controlH <= 0 || baseW <= 0 || baseH <= 0) return null;

    double scale = Math.Min(controlW / baseW, controlH / baseH);
    double drawnW = baseW * scale, drawnH = baseH * scale;
    double offsetX = (controlW - drawnW) / 2.0, offsetY = (controlH - drawnH) / 2.0;

    double x = (clickX - offsetX) / scale;
    double y = (clickY - offsetY) / scale;
    if (x < 0 || y < 0 || x > baseW || y > baseH) return null;
    return (x, y);
}
```

y crear `HidusbfModernGui/PadHitZones.cs`:

```csharp
using System.Collections.Generic;

namespace HidusbfModernGui
{
    // Un boton del mando y el rectangulo que ocupa, en pixeles del lienzo base del dibujo
    // (el mismo sistema de coordenadas que usa PadSkin.Parts[].Dst).
    public readonly record struct PadZone(PadButton Button, double X, double Y, double W, double H)
    {
        public bool Contains(double px, double py)
            => px >= X && py >= Y && px <= X + W && py <= Y + H;

        public double Area => W * H;
    }

    public static class PadHitZones
    {
        // El boton bajo el punto. Cuando varias zonas se solapan gana la MAS PEQUENA: el
        // touchpad ocupa media carcasa y los botones de cara caen dentro de zonas grandes,
        // asi que "la ultima que se dibujo" o "la primera que coincide" darian el boton
        // equivocado segun el orden del diccionario del skin.
        public static PadButton? Find(IReadOnlyList<PadZone>? zones, double x, double y)
        {
            if (zones == null) return null;

            PadButton? best = null;
            double bestArea = double.MaxValue;
            foreach (var z in zones)
            {
                if (!z.Contains(x, y) || z.Area >= bestArea) continue;
                best = z.Button;
                bestArea = z.Area;
            }
            return best;
        }
    }
}
```

- [ ] **Step 5: Verificar que pasan** (filtro + suite completa).
- [ ] **Step 6: Commit** — `git add -u && git add HidusbfModernGui/PadHitZones.cs && git commit -m "feat: PadHitZones - de un clic en el mando al boton que hay debajo (TDD)"`

---

### Task 3: El mando se vuelve interactivo (`ButtonClicked`)

**Files:**
- Modify: `HidusbfModernGui/PadVisual.xaml.cs`, `SkinnedPadVisual.xaml.cs`, `PadVisualHost.xaml(.cs)`

**Interfaces:**
- Produces: en `PadVisual` y `SkinnedPadVisual`, `IReadOnlyList<PadZone> HitZones` y `(double W, double H) BaseSize`; en `PadVisualHost`, `event EventHandler<PadButton>? ButtonClicked`, `bool InteractiveRemap { get; set; }` (cuando es false el mando no reacciona al ratón: es el modo por defecto para el overlay de streaming).

- [ ] **Step 1: `SkinnedPadVisual`** — las zonas salen del propio manifiesto (ya son rectángulos en coordenadas de la base). Añadir:

```csharp
// Las zonas clicables son los mismos Dst del manifiesto: el skin ya describe donde esta
// cada boton, asi que no hay geometria duplicada que pueda desincronizarse del dibujo.
private readonly List<PadZone> _zones = new();
public IReadOnlyList<PadZone> HitZones => _zones;
public (double W, double H) BaseSize => _skin == null ? (0, 0) : (_skin.BaseWidth, _skin.BaseHeight);
```

y dentro de `Load(PadSkin skin)`, al construir cada capa (junto a `AddCalibrationBox`), registrar la zona traduciendo la clave a `PadButton` con un mapa estático:

```csharp
// Clave del manifiesto -> boton. Las claves de stick no entran: un stick no se remapea.
private static readonly Dictionary<string, PadButton> ZoneKeys = new()
{
    ["btn.cross"] = PadButton.Cross,       ["btn.circle"] = PadButton.Circle,
    ["btn.square"] = PadButton.Square,     ["btn.triangle"] = PadButton.Triangle,
    ["dpad.up"] = PadButton.DpadUp,        ["dpad.down"] = PadButton.DpadDown,
    ["dpad.left"] = PadButton.DpadLeft,    ["dpad.right"] = PadButton.DpadRight,
    ["btn.l1"] = PadButton.L1,             ["btn.r1"] = PadButton.R1,
    ["btn.l2"] = PadButton.L2,             ["btn.r2"] = PadButton.R2,
    ["btn.share"] = PadButton.Share,       ["btn.options"] = PadButton.Options,
    ["btn.ps"] = PadButton.PS,             ["btn.touchpad"] = PadButton.TouchpadClick,
};
```

`_zones.Clear()` va junto a los demás `Clear()` del principio de `Load`.

- [ ] **Step 2: `PadVisual`** (vector) — mismas zonas, pero escritas a mano contra su geometría fija (canvas 360×260), tomando los valores de las constantes y del XAML que ya existen. `BaseSize => (360, 260)`. Los rectángulos deben coincidir con lo dibujado; el criterio de aceptación es la prueba manual del Step 4 (pulsar cada botón dibujado abre el popup correcto).

- [ ] **Step 3: `PadVisualHost`** — el clic se resuelve aquí, no en los hijos, porque el host es quien sabe cuál de los dos dibujos está visible:

```csharp
// Remapeo visual: un clic sobre el mando dice que boton se pulso. Apagado por defecto
// (el overlay de streaming no debe reaccionar al raton); MainWindow lo enciende en el
// configurador.
public bool InteractiveRemap { get; set; }
public event EventHandler<PadButton>? ButtonClicked;

private void Host_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
{
    if (!InteractiveRemap) return;

    bool skinned = Skinned.Visibility == Visibility.Visible;
    var (bw, bh) = skinned ? Skinned.BaseSize : Vector.BaseSize;
    var zones = skinned ? Skinned.HitZones : Vector.HitZones;

    var pos = e.GetPosition(this);
    var basePt = PadVisualMath.ToBaseCoords(pos.X, pos.Y, ActualWidth, ActualHeight, bw, bh);
    if (basePt == null) return;

    var btn = PadHitZones.Find(zones, basePt.Value.X, basePt.Value.Y);
    if (btn != null) ButtonClicked?.Invoke(this, btn.Value);
}
```

El `Grid` raíz de `PadVisualHost.xaml` necesita `Background="Transparent"` y `MouseLeftButtonUp="Host_MouseLeftButtonUp"` (sin fondo, las zonas vacías no reciben el ratón). Cuando `InteractiveRemap` es true, poner `Cursor="Hand"`.

- [ ] **Step 4: Verificación** — build 0/0, suite completa PASS. Manual (tras Task 6, que es quien enciende `InteractiveRemap`): pulsar cada botón del mando dibujado y comprobar que se detecta el correcto, con skin y sin skin.
- [ ] **Step 5: Commit** — `git add -u && git commit -m "feat: el mando dibujado detecta clics por boton (zonas del skin y del vector)"`

---

### Task 4: Menos ruido — textos a "?" y a Ajustes, y el botón circular de opciones

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml(.cs)`

- [ ] **Step 1: El aviso de anticheat se muda a Ajustes.** Cortar el `Border` del aviso completo de `ConfigPanel` y pegarlo en la pestaña "Settings" (donde ya viven SERVICIO, MODO DEL DRIVER, RECURSOS Y DRIVERS), bajo un encabezado `RIESGO Y ANTICHEAT` con el mismo `SectionHeading`. El texto **no cambia**: es un aviso honesto y sigue accesible, solo deja de ocupar la pantalla de trabajo.

- [ ] **Step 2: La descripción de MANDO VIRTUAL pasa a un "?".** Quitar el `TextBlock` largo de la tarjeta. Junto a `MasterToggleBtn`, en un `StackPanel Orientation="Horizontal"`, añadir un botón redondo de ayuda que abre un `Popup`:

```xml
<Button x:Name="VirtualPadHelpBtn" Style="{StaticResource RoundIconButton}" Content="?"
        Click="ToggleVirtualPadHelp" ToolTip="Que es el mando virtual" Margin="10,0,0,0"/>
<Popup x:Name="VirtualPadHelpPopup" PlacementTarget="{Binding ElementName=VirtualPadHelpBtn}"
       Placement="Bottom" StaysOpen="False" AllowsTransparency="True">
    <Border Background="{StaticResource SurfaceAltBrush}" BorderBrush="{StaticResource BorderBrush}"
            BorderThickness="1" Padding="16" MaxWidth="420">
        <TextBlock Style="{StaticResource FieldLabel}" TextWrapping="Wrap"
                   Text="Activa el mando virtual para que los ajustes de abajo (sticks, gatillos, touchpad, botones) se apliquen al juego: el DualSense fisico se oculta y el juego ve un DS4 virtual con tu configuracion. Desactivalo para volver al mando nativo tal cual. Requiere los drivers ViGEmBus y HidHide (Nefarius), instalados aparte."/>
    </Border>
</Popup>
```

Estilo nuevo `RoundIconButton` en `Theme.xaml` (círculo de 26 px, borde del tema, hover como `InstrumentButton`), reutilizable por el botón de la tuerca del Step 4.

- [ ] **Step 3: Fuera "MANDO NATIVO — ...".** `MasterStatusText` deja de mostrar la frase larga cuando el motor está apagado: el propio botón ya dice `ACTIVAR MANDO VIRTUAL`, así que el estado apagado no necesita texto. En `StopEngine`/`CleanupEngine` y en el estado inicial, poner `MasterStatusText.Text = ""` (y `Visibility=Collapsed` para que no deje hueco). **Con el motor encendido el texto se mantiene**: ahí sí informa (físico oculto / virtual activo / reportes). Si `Sanitize`/arranque detecta que faltan drivers, ese mensaje también se sigue mostrando — es un error, no decoración.

- [ ] **Step 4: Las cuatro acciones del mando en un botón circular.** Quitar `RECARGAR SKIN`, `Modo calibracion`, `MODO STREAMER` y `Overlay atraviesa clic` de la tarjeta. En su lugar, un único botón redondo (tuerca) anclado **arriba a la derecha de la tarjeta del mando**, que abre un `Popup` con las cuatro opciones en vertical:

```xml
<Button x:Name="PadOptionsBtn" Style="{StaticResource RoundIconButton}"
        HorizontalAlignment="Right" VerticalAlignment="Top" Margin="0,8,8,0"
        Click="TogglePadOptions" ToolTip="Opciones del visualizador">
    <Path Data="{StaticResource SettingsIconPath}" Fill="{StaticResource TextLabelBrush}"
          Stretch="Uniform" Width="14" Height="14"/>
</Button>
<Popup x:Name="PadOptionsPopup" PlacementTarget="{Binding ElementName=PadOptionsBtn}"
       Placement="Bottom" StaysOpen="False" AllowsTransparency="True">
    <Border Background="{StaticResource SurfaceAltBrush}" BorderBrush="{StaticResource BorderBrush}"
            BorderThickness="1" Padding="12">
        <StackPanel MinWidth="210">
            <TextBlock x:Name="SkinStatusText" Style="{StaticResource FieldLabel}" Margin="0,0,0,8"/>
            <Button Content="RECARGAR SKIN" Style="{StaticResource SecondaryButton}" Click="ReloadSkin_Click"/>
            <CheckBox x:Name="CalibrationCheck" Content="Modo calibracion" Click="Calibration_Click" Margin="0,10,0,0"/>
            <Rectangle Height="1" Fill="{StaticResource BorderBrush}" Margin="0,10"/>
            <Button x:Name="StreamerToggle" Content="MODO STREAMER" Style="{StaticResource SecondaryButton}" Click="StreamerToggle_Click"/>
            <CheckBox x:Name="StreamerClickThrough" Content="Overlay atraviesa clic" Click="StreamerClickThrough_Click" Margin="0,10,0,0"/>
        </StackPanel>
    </Border>
</Popup>
```

Los handlers **no cambian de nombre ni de cuerpo**: solo se mueven los controles, así que `ReloadSkin_Click`, `Calibration_Click`, `StreamerToggle_Click` y `StreamerClickThrough_Click` siguen valiendo tal cual. `TogglePadOptions` y `ToggleVirtualPadHelp` son dos líneas (`X.IsOpen = !X.IsOpen`).

- [ ] **Step 5: Verificación** — build 0/0, suite completa PASS. Manual: el aviso aparece en Ajustes; el "?" abre y cierra la explicación; con el motor apagado no hay frase suelta; la tuerca abre las cuatro opciones y todas siguen funcionando (recargar skin, calibración, streamer, atraviesa-clic).
- [ ] **Step 6: Commit** — `git add -u && git commit -m "refactor(ui): aviso a Ajustes, descripciones tras un ?, y las opciones del visualizador en un boton circular"`

---

### Task 5: El mando grande y el configurador en columnas

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml`

- [ ] **Step 1: Mando más grande.** `ConfigPadVisual` pasa de `Height="240"` a `MinHeight="360"` con `MaxHeight="520"` y `HorizontalAlignment="Stretch"`, dentro de una tarjeta que ya no compite con textos (los quitó la Task 4). Al maximizar, el `Viewbox` lo escala solo.

- [ ] **Step 2: Las cuatro pestañas se convierten en secciones en columnas.** Hoy `StickTabBtn`/`GatilloTabBtn`/`TouchpadTabBtn`/`BotonTabBtn` alternan la visibilidad de cuatro `Grid`. En una pantalla ancha eso desaprovecha todo el espacio y obliga a navegar. Sustituir por un único contenedor con las cuatro secciones visibles a la vez, repartidas por un `WrapPanel` de tarjetas de ancho fijo:

```xml
<WrapPanel Orientation="Horizontal" ItemWidth="470">
    <!-- STICK IZQUIERDO -->  <!-- STICK DERECHO -->
    <!-- GATILLOS -->         <!-- TOUCHPAD + BOTONES -->
</WrapPanel>
```

Con `ItemWidth="470"`: a 1080 de ancho caben 2 columnas (como hoy pero sin pestañas), a 1920 maximizada caben 3. El `WrapPanel` decide solo, sin código.

Los botones de pestaña desaparecen; **el contenido de cada `Grid` (`TabSticks` etc.) se conserva tal cual**, solo cambia el contenedor. Los `x:Name` de todos los controles internos y sus handlers **no se tocan** — es una reorganización visual, no funcional. Los métodos `ShowStickTab`/`ShowGatilloTab`/`ShowTouchpadTab`/`ShowBotonTab` quedan sin uso: eliminarlos junto con los botones (el compilador señala cualquier referencia superviviente).

- [ ] **Step 3: Verificación** — build 0/0, suite completa PASS. Manual: en ventana normal se ven dos columnas; maximizada, tres; el mando se ve claramente más grande; todos los controles (sliders, combos, curvas, MIS CURVAS) siguen funcionando.
- [ ] **Step 4: Commit** — `git add -u && git commit -m "refactor(ui): mando grande y configurador en columnas adaptativas (sin pestanas)"`

---

### Task 6: Medidores de gatillo en vivo + remapeo por clic

**Files:**
- Create: `HidusbfModernGui/RemapPopup.xaml(.cs)`
- Modify: `HidusbfModernGui/MainWindow.xaml(.cs)`

**Interfaces:**
- Consumes: `PadVisualHost.ButtonClicked` (Task 3), `_remap.ButtonRemap`, `RemapEngine`/`VisualizerTick`.
- Produces: `RemapPopup` (lista de `PadButton` destino + opción "Sin cambios"), `MainWindow.ShowRemapFor(PadButton)`.

- [ ] **Step 1: Medidor de gatillo.** En la sección GATILLOS, bajo cada slider de punto de disparo, una barra de recorrido en vivo:

```xml
<Grid Height="14" Margin="0,6,0,0">
    <Rectangle Fill="{StaticResource SurfaceAltBrush}" RadiusX="3" RadiusY="3"/>
    <!-- Recorrido actual del gatillo fisico -->
    <Rectangle x:Name="L2LiveBar" Fill="{StaticResource TextDataBrush}" RadiusX="3" RadiusY="3"
               HorizontalAlignment="Left" Width="0"/>
    <!-- Marca del punto de disparo elegido -->
    <Rectangle x:Name="L2PointMark" Width="2" Fill="{StaticResource StatusWarnBrush}"
               HorizontalAlignment="Left" Margin="0"/>
</Grid>
```

En `VisualizerTick` (que ya tiene `raw` y `outState`), añadir una llamada `UpdateTriggerMeters(raw)`:

```csharp
// El medidor muestra el recorrido FISICO (raw), no el transformado: es la referencia
// contra la que el usuario coloca el punto de disparo. La marca ambar es ese punto; con
// hair-trigger la salida ya es 0 o 1, asi que dibujar la salida no diria nada util.
private void UpdateTriggerMeters(ControllerState raw)
{
    if (L2LiveBar == null) return;   // aun no parseado
    double w = TriggerMeterWidth;    // ancho de la pista, constante que casa con el XAML
    L2LiveBar.Width = w * PadVisualMath.Fill01(raw.L2);
    R2LiveBar.Width = w * PadVisualMath.Fill01(raw.R2);
    L2PointMark.Margin = new Thickness(w * (_remap.L2PointPct / 100.0), 0, 0, 0);
    R2PointMark.Margin = new Thickness(w * (_remap.R2PointPct / 100.0), 0, 0, 0);
}
```

Los handlers `L2Point_Changed`/`R2Point_Changed` también llaman a `UpdateTriggerMeters` con el último estado conocido para que la marca se mueva aunque el gatillo esté quieto (guardar el último `raw` en un campo `_lastRaw` en `VisualizerTick`).

- [ ] **Step 2: `RemapPopup`.** Un `Popup` con una rejilla de botones: los mismos destinos que hoy ofrece la lista desplegable (`RemapTargets`), más "Sin cambios" arriba. Cada destino es un `Button` con el nombre del botón; al pulsar, se cierra y devuelve la elección por un evento `Chosen(PadButton)` (o `PadButton.None` para limpiar).

- [ ] **Step 3: Cablearlo.** En `BuildRemapControls` (o donde se inicializa el configurador):

```csharp
ConfigPadVisual.InteractiveRemap = true;
ConfigPadVisual.ButtonClicked += (s, btn) => ShowRemapFor(btn);
```

y:

```csharp
// Remapeo visual: al pulsar un boton del mando dibujado se elige a que se reasigna. El
// touchpad se trata aparte (sus 4 zonas no son un boton unico), asi que un clic en el
// touchpad abre la eleccion de la ZONA tocada segun donde se pulso.
private void ShowRemapFor(PadButton source)
{
    var popup = new RemapPopup(source, _remap.ButtonRemap.TryGetValue(source, out var cur) ? cur : source);
    popup.Chosen += (_, target) =>
    {
        if (target == PadButton.None || target == source) _remap.ButtonRemap.Remove(source);
        else _remap.ButtonRemap[source] = target;
        RememberRemap();
        RefreshRemapBadges();
    };
    popup.ShowFor(ConfigPadVisual);
}
```

- [ ] **Step 4: Ver los remapeos activos sobre el mando.** `RefreshRemapBadges()` dibuja, sobre cada botón remapeado, una etiqueta pequeña con el destino (reutilizando las zonas de la Task 3 para colocarla). Así el usuario ve de un vistazo qué está remapeado sin una tabla aparte. La sección BOTONES deja de ser una lista de desplegables: se queda con el badge sobre el mando y un botón `LIMPIAR REMAPEO` que vacía `ButtonRemap`.
- [ ] **Step 5: Touchpad clicable.** Igual que los botones, pero las 4 zonas: al pulsar el touchpad dibujado, `ToBaseCoords` da el punto exacto → se calcula el cuadrante dentro del `Dst` del touchpad → se abre el mismo popup para `TouchZoneMap[zona]`. La rejilla 2×2 de desplegables actual se elimina.
- [ ] **Step 6: Verificación** — build 0/0, suite completa PASS. Manual: apretar los gatillos mueve las barras; mover el slider mueve la marca ámbar; pulsar Cruz en el dibujo abre el popup y elegir Cuadrado deja el badge; con el mando virtual activo, pulsar Cruz en el mando físico dispara Cuadrado en joy.cpl; pulsar cada cuadrante del touchpad asigna esa zona.
- [ ] **Step 7: Commit** — `git add -u && git add HidusbfModernGui/RemapPopup.xaml HidusbfModernGui/RemapPopup.xaml.cs && git commit -m "feat: medidores de gatillo en vivo y remapeo pulsando sobre el mando"`

---

### Task 7: Perfiles unificados en las dos páginas

**Files:**
- Create: `HidusbfModernGui/ProfilesBar.xaml(.cs)`
- Modify: `HidusbfModernGui/MainWindow.xaml(.cs)`

**Interfaces:**
- Consumes: `GameProfileStore` (Task 1).
- Produces: `ProfilesBar` con `event EventHandler<GameProfile>? LoadRequested`, `event EventHandler<string>? SaveRequested`, `void Refresh(IEnumerable<GameProfile>)`. Se instancia **dos veces** (configurador y luces) sobre la misma lista.

- [ ] **Step 1: `ProfilesBar`** — un `UserControl` con el combo de perfiles, `CARGAR`, `GUARDAR`, `BORRAR` y la caja de nombre (lo que hoy hay duplicado en las dos páginas), sin lógica de negocio: solo emite eventos.
- [ ] **Step 2: Migración al arrancar.** En `Window_Loaded`, antes de poblar la UI:

```csharp
// Un solo perfil para todo. Si aun no existe game-profiles.json, se funden los dos
// archivos viejos (luz y remapeo) en el nuevo. Los viejos NO se borran: quedan como
// respaldo, y si algo saliera mal el usuario no ha perdido nada.
_gameProfiles = GameProfileStore.Load();
if (_gameProfiles.Count == 0 && !System.IO.File.Exists(GameProfileStore.Path))
{
    _gameProfiles = GameProfileStore.Migrate(ProfileStore.Load(), RemapProfileStore.Load());
    if (_gameProfiles.Count > 0)
    {
        GameProfileStore.Save(_gameProfiles);
        LogStatus($"{_gameProfiles.Count} perfiles migrados al formato unico (los antiguos siguen guardados).");
    }
}
```

- [ ] **Step 3: Cargar y guardar.** `CARGAR` aplica las mitades que el perfil traiga: `Light != null` → aplicar la intención de luz por el camino que ya existe; `Remap != null` → `_remap = CloneRemapSettings(p.Remap); _remap.Sanitize(); ApplyRemapSettingsToControls();`; `Rate != null` → seleccionar esa tasa. `GUARDAR` toma el nombre y guarda **las dos mitades a la vez** (luz actual + `_remap` actual).
- [ ] **Step 4: Sustituir las dos barras viejas.** Quitar "PERFILES DEL REMAPEO" del configurador y la sección de perfiles de la página de luces; poner una `ProfilesBar` en cada una, ambas apuntando a la misma lista y refrescándose mutuamente tras guardar o borrar. `RemapProfileStore` se conserva **solo** para el pseudo-perfil `__ultimo_usado__` (el estado en vivo del configurador), que no es un perfil de usuario.
- [ ] **Step 5: Verificación** — build 0/0, suite completa PASS. Manual: al abrir, los perfiles viejos aparecen ya migrados; guardar un perfil desde el configurador y cargarlo desde Luces aplica ambas cosas; los archivos `profiles.json` y `remap-profiles.json` siguen en `%APPDATA%` intactos.
- [ ] **Step 6: Commit** — `git add -u && git add HidusbfModernGui/ProfilesBar.xaml HidusbfModernGui/ProfilesBar.xaml.cs && git commit -m "feat: un perfil unico (luz + mando) compartido por las dos paginas, con migracion"`

---

### Task 8: Documentación y verificación integral

**Files:**
- Modify: `README.md`, `docs/DOCUMENTACION.md`

- [ ] **Step 1: README** — en la viñeta **Configurar el mando**: el remapeo se hace pulsando sobre el mando en pantalla; los perfiles guardan luz y configuración juntas.
- [ ] **Step 2: `docs/DOCUMENTACION.md`** — añadir `GameProfile.cs`, `PadHitZones.cs`, `ProfilesBar`, `RemapPopup` al mapa de módulos; documentar el perfil unificado y **la regla de migración** (fusión por nombre, `__ultimo_usado__` excluido, archivos viejos intactos); y el hit-testing (zonas en coordenadas de la base, se deshace el Viewbox, gana la zona más pequeña).
- [ ] **Step 3** — `dotnet test` completo verde y `.\package.ps1` termina en "Package ready" sin warnings nuevos.
- [ ] **Step 4: Prueba integral (usuario, con hardware)** — remapear pulsando el mando y verlo en joy.cpl; medidores de gatillo; guardar/cargar un perfil unificado; el mando grande y las columnas en maximizado.
- [ ] **Step 5: Commit** — `git add -u && git commit -m "docs: rediseno de la UI del mando (remapeo visual, perfiles unicos, medidores)"`

---

## Self-review

- **Cobertura del pedido:** menos botones (Tasks 4 y 5: cuatro acciones → una tuerca; cuatro pestañas → columnas); mando más grande (Task 5); fuera el aviso de anticheat → Ajustes y las descripciones → "?" (Task 4); fuera "MANDO NATIVO…" (Task 4 Step 3); medición de gatillos (Task 6 Step 1); columnas con stick izquierdo/derecho (Task 5 Step 2); perfiles universales luz+mando (Tasks 1 y 7); touchpad y botones dejan de ser "crudos" → remapeo pulsando el mando (Task 6, con la decisión del usuario). ✓
- **Placeholders:** el núcleo puro y el cableado llevan código completo; las partes puramente visuales (zonas del vector en Task 3 Step 2, `RemapPopup`, `ProfilesBar`) van con estructura, contrato y criterio de aceptación manual — el mismo patrón que ya se usó para `PadVisual`. ✓
- **Tipos consistentes:** `PadZone`/`PadHitZones.Find` (Task 2) consumidos por los dos dibujos y el host (Task 3) y por el touchpad (Task 6); `PadVisualMath.ToBaseCoords` (Task 2) usado en Task 3; `GameProfile`/`GameProfileStore` (Task 1) usados por `ProfilesBar` y `MainWindow` (Task 7); `PadVisualHost.ButtonClicked` (Task 3) consumido en Task 6. ✓
- **Restricción de tests:** solo se linkean `GameProfile.cs` y `PadHitZones.cs` (puros); nada de WPF. ✓
- **Seguridad de datos:** la migración no borra ni modifica los archivos viejos, y solo corre si el archivo nuevo no existe (no puede pisar perfiles ya migrados). ✓
- **Riesgo asumido y acotado:** Task 5 mueve mucho XAML sin tocar `x:Name` ni handlers, para que el compilador cace cualquier referencia rota; Task 6 elimina la rejilla de desplegables solo después de que el remapeo por clic funcione. ✓
