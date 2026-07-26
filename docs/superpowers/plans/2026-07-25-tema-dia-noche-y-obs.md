# Modo día/noche, la tuerca en columna y el enlace de OBS — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cuatro cosas pedidas: (1) el hover de las etiquetas del diagrama deja de tragarse el icono; (2) **modo día y noche** para toda la app, monocromo, con cambio en vivo y recordado entre sesiones; (3) la tuerca del mando en vivo pasa a **una sola columna vertical**; (4) el modo streamer publica un **enlace local para OBS** (Fuente de navegador) que dibuja el mando en tiempo real.

**Architecture:** El tema se cambia **mutando el `Color` de los `SolidColorBrush` que ya están en los recursos de la app**. Los 397 `StaticResource` del XAML apuntan a esas mismas instancias, así que cambiar el color se ve al instante sin recargar ventanas y sin reescribir una sola referencia. El enlace de OBS es un `HttpListener` en `127.0.0.1` que sirve una página con un `<canvas>` y un flujo SSE del estado ya transformado; el canvas dibuja con `drawImage(hoja, sx,sy,sw,sh, dx,dy,dw,dh)`, que es **exactamente** la forma que ya tiene `skin.json`, así que el navegador y WPF pintan lo mismo sin duplicar geometría.

**Tech Stack:** .NET 9 WPF, xUnit, `System.Net.HttpListener`, `System.Text.Json`. Sin dependencias nuevas. En el navegador: canvas 2D y `EventSource`, ambos nativos — sin librerías, y OBS (CEF) los trae de serie.

## Contexto verificado (lo que ya existe)

- **La paleta** son 11 `SolidColorBrush` en `Theme.xaml:7-22`, ninguno con `PresentationOptions:Freeze`. Todo el resto del XAML los consume por `StaticResource`.
- **El bug del hover**: `PillButtonInk` (`Theme.xaml:350-376`) al pasar el ratón pone `Bd.Background = BgBrush` (negro) y `Foreground = TextDataBrush`. Pero el contenido de la píldora **ya no es texto**: `BuildIcon()` crea `Path`/`Ellipse`/`Border` con el pincel `Ink` fijado en code-behind, y esas formas **no heredan `Foreground`**. Resultado: fondo negro + iconos negros = el borrón de la captura del usuario.
- **La tuerca** (`MainWindow.xaml:658-690`) son hoy dos filas horizontales `[Botón][CheckBox]` dentro de un `Popup` de `MinWidth="260"`.
- **El skin** es 100 % datos: `PadSkin { BaseFile, BaseWidth, BaseHeight, StickRadius, Parts: {clave -> {File, Src, Src2?, Dst}} }`. No hay rotaciones ni espejos en el modelo — están horneados en los PNG.
- **El contrato de dibujo** está en `SkinnedPadVisual.Update()`: sticks siempre visibles y desplazados por `PadVisualMath.StickOffset`, `btn.l2`/`btn.r2` con **opacidad** `Fill01(recorrido)`, y el resto sí/no según `Pressed`.
- **El feed** es `VisualizerTick` (`MainWindow.xaml.cs:914-923`): lee el estado crudo, aplica `RemapEngine.Transform` y lo reparte a `ConfigPadVisual` y al overlay. Ese `outState` es justo lo que OBS tiene que ver: **la salida al juego**, no el mando crudo.
- La app corre **elevada** (`app.manifest`, `requireAdministrator`), así que `HttpListener` puede reservar el prefijo sin `netsh urlacl`.

## Global Constraints

- UI en **español**, monocromo. El modo día es la **misma** disciplina invertida (blanco/grises/negro), no una paleta nueva de colores.
- **No se inventan colores** fuera de `ThemePalette`: cualquier color nuevo entra ahí o en los tres pinceles fijos del diagrama, con su motivo escrito.
- El servidor de OBS escucha **solo en `127.0.0.1`**. Nunca `+`, nunca `*`, nunca la IP de la LAN.
- El proyecto de tests linkea fuentes individualmente: `ThemePalette.cs`, `UiPrefs.cs` y `PadWebModel.cs` van al csproj. Nada que toque WPF/HidSharp/Nefarius se puede linkear.
- **Aplicación en vivo**: el tema se aplica al pulsarlo, sin reiniciar y sin botón "Aplicar".
- Sin macros, sin emulación de teclado/ratón, sin evasión de anticheat.
- Commits **sin** Co-Authored-By. El push lo hace el usuario.

## Estructura de archivos

| Archivo | Responsabilidad |
|---|---|
| `HidusbfModernGui/ThemePalette.cs` (nuevo) | Tabla pura `AppTheme -> (clave, hex)`. Sin WPF. |
| `HidusbfModernGui/UiPrefs.cs` (nuevo) | Preferencias de interfaz en `ui.json`: tema y puerto de OBS. Sin WPF. |
| `HidusbfModernGui/ThemeManager.cs` (nuevo) | Aplica una paleta mutando los pinceles vivos. Único archivo del tema que toca WPF. |
| `HidusbfModernGui/PadWebModel.cs` (nuevo) | Skin y estado → JSON del navegador, más la guarda de nombres de archivo. Sin WPF. |
| `HidusbfModernGui/StreamerServer.cs` (nuevo) | `HttpListener` local: página, `skin.json`, sprites y SSE. Sin WPF. |
| `HidusbfModernGui/Theme.xaml` | Pinceles fijos del diagrama + hover arreglado. |
| `HidusbfModernGui/MainWindow.xaml(.cs)` | Interruptor de tema, tuerca en columna, bloque de OBS, feed vivo. |

---

### Task 1: El hover deja de tragarse el icono

**Files:**
- Modify: `HidusbfModernGui/Theme.xaml` (estilo `PillButtonInk`)

**Interfaces:**
- Produces: pincel `DiagramPillHoverBrush`, consumido solo por `PillButtonInk`.

- [ ] **Step 1: Añadir el pincel del lavado**, justo encima del estilo `PillButtonInk` en `Theme.xaml`:

```xml
<!-- Lavado del hover de las etiquetas del diagrama: 8% de negro. NO es una inversion.
     La pildora dejo de contener texto y ahora lleva formas (Path/Ellipse/Border) con su
     pincel fijado en code-behind, y esas NO heredan Foreground: al invertir el fondo a
     tinta, los iconos negros desaparecian dentro del negro. Un fondo que solo se oscurece
     un poco no puede volver a tragarse su propio contenido, pase lo que pase con el.
     Fijo, no de la paleta: vive sobre el panel del diagrama, que tampoco sigue el tema. -->
<SolidColorBrush x:Key="DiagramPillHoverBrush" Color="#14000000"/>
```

- [ ] **Step 2: Cambiar el trigger.** En `PillButtonInk`, sustituir el bloque de triggers entero por:

```xml
<ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True">
        <Setter TargetName="Bd" Property="Background" Value="{StaticResource DiagramPillHoverBrush}"/>
    </Trigger>
</ControlTemplate.Triggers>
```

- [ ] **Step 3: Arreglar el comentario del estilo.** El de arriba (`Theme.xaml:344-349`) todavía dice "Al pasar el raton se invierte (fondo tinta, texto papel)", que es justo lo que se acaba de quitar. Sustituir esa frase por: `Al pasar el raton solo se lava el fondo: invertirlo tragaba los iconos (ver DiagramPillHoverBrush).`

- [ ] **Step 4: Verificación** — `dotnet build HidusbfModernGui\HidusbfModernGui.csproj -c Release`, esperado `0 Warning(s) 0 Error(s)`. Manual: abrir MANDO → CONFIGURAR EL MANDO → ASIGNACION DE BOTONES y pasar el ratón por varias etiquetas, **incluida una de las caras** (círculo relleno) y una de las de texto (`L2`). En las dos el contenido debe seguir viéndose.

- [ ] **Step 5: Commit**

```bash
git add -u && git commit -m "fix(ui): el hover de la etiqueta ya no se traga su propio icono"
```

---

### Task 2: `ThemePalette` — la paleta de los dos modos (TDD)

**Files:**
- Create: `HidusbfModernGui/ThemePalette.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj`
- Test: `HidusbfModernGui.Tests/ThemePaletteTests.cs`

**Interfaces:**
- Produces: `enum AppTheme { Noche, Dia }`; `readonly record struct PaletteEntry(string Key, string Hex)`; `ThemePalette.For(AppTheme) -> IReadOnlyList<PaletteEntry>`; `ThemePalette.Keys -> IReadOnlyList<string>`. Consumido por `ThemeManager` (Task 4) y `UiPrefs` (Task 3).

**Los valores del modo día y por qué:** no es un negativo automático. `#FFFFFF` de fondo con `#000000` de texto es correcto, pero los grises intermedios hay que elegirlos: sobre blanco, un panel necesita ser **más oscuro** que el fondo (al revés que sobre negro), y los tres colores de estado a plena saturación se leen mal sobre blanco, así que bajan de luminosidad lo justo para mantener contraste. Son los únicos colores de la app y solo codifican hechos del sistema.

- [ ] **Step 1: Link en el csproj**, junto a los demás `<Compile Include>`:

```xml
<Compile Include="..\HidusbfModernGui\ThemePalette.cs" Link="ThemePalette.cs" />
```

- [ ] **Step 2: Tests que fallan** — crear `HidusbfModernGui.Tests/ThemePaletteTests.cs`:

```csharp
using System;
using System.Globalization;
using System.Linq;
using HidusbfModernGui;
using Xunit;

public class ThemePaletteTests
{
    // Un hex "#RRGGBB" a su luminancia relativa aproximada (0..255). Sirve para afirmar
    // "esto es mas claro que aquello" sin depender de WPF.
    private static double Luma(string hex)
    {
        int r = int.Parse(hex.Substring(1, 2), NumberStyles.HexNumber);
        int g = int.Parse(hex.Substring(3, 2), NumberStyles.HexNumber);
        int b = int.Parse(hex.Substring(5, 2), NumberStyles.HexNumber);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    private static string Hex(AppTheme t, string key)
        => ThemePalette.For(t).Single(e => e.Key == key).Hex;

    [Fact]
    public void BothThemes_DefineExactlyTheSameKeys()
    {
        var noche = ThemePalette.For(AppTheme.Noche).Select(e => e.Key).OrderBy(k => k);
        var dia = ThemePalette.For(AppTheme.Dia).Select(e => e.Key).OrderBy(k => k);
        Assert.Equal(noche, dia);
        // Y esas claves son justo las que ThemeManager va a buscar en los recursos.
        Assert.Equal(ThemePalette.Keys.OrderBy(k => k), noche);
    }

    [Fact]
    public void NoThemeRepeatsAKey()
    {
        foreach (AppTheme t in Enum.GetValues<AppTheme>())
        {
            var keys = ThemePalette.For(t).Select(e => e.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
        }
    }

    [Fact]
    public void EveryColour_IsASixDigitHex()
    {
        foreach (AppTheme t in Enum.GetValues<AppTheme>())
            foreach (var e in ThemePalette.For(t))
            {
                Assert.StartsWith("#", e.Hex);
                Assert.Equal(7, e.Hex.Length);
                Assert.True(int.TryParse(e.Hex.Substring(1), NumberStyles.HexNumber,
                                         CultureInfo.InvariantCulture, out _),
                            $"{t}/{e.Key} = '{e.Hex}' no es un hex de 6 digitos");
            }
    }

    // Guarda de regresion: el modo Noche DEBE reproducir la paleta que la app ya tiene.
    // Cambiar de tema y volver a Noche no puede dejar la app de otro color que al abrir.
    [Fact]
    public void Noche_MatchesTheShippedPalette()
    {
        Assert.Equal("#000000", Hex(AppTheme.Noche, "BgBrush"));
        Assert.Equal("#0A0A0A", Hex(AppTheme.Noche, "SurfaceBrush"));
        Assert.Equal("#111111", Hex(AppTheme.Noche, "SurfaceAltBrush"));
        Assert.Equal("#1F1F1F", Hex(AppTheme.Noche, "BorderBrush"));
        Assert.Equal("#FFFFFF", Hex(AppTheme.Noche, "TextDataBrush"));
        Assert.Equal("#8A8A8A", Hex(AppTheme.Noche, "TextLabelBrush"));
        Assert.Equal("#4A4A4A", Hex(AppTheme.Noche, "TextMutedBrush"));
        Assert.Equal("#3A3A3A", Hex(AppTheme.Noche, "PadIdleBrush"));
    }

    [Fact]
    public void Dia_InvertsBackgroundAndText()
    {
        Assert.True(Luma(Hex(AppTheme.Dia, "BgBrush")) > 200, "el fondo de dia debe ser claro");
        Assert.True(Luma(Hex(AppTheme.Dia, "TextDataBrush")) < 60, "el texto de dia debe ser oscuro");
    }

    // Sobre negro un panel es MAS claro que el fondo; sobre blanco tiene que ser MAS oscuro,
    // o desaparece. Esto es lo que impide "invertir" la paleta a ciegas.
    [Fact]
    public void Surfaces_SeparateFromTheBackgroundInBothThemes()
    {
        Assert.True(Luma(Hex(AppTheme.Noche, "SurfaceBrush")) > Luma(Hex(AppTheme.Noche, "BgBrush")));
        Assert.True(Luma(Hex(AppTheme.Dia, "SurfaceBrush")) < Luma(Hex(AppTheme.Dia, "BgBrush")));
    }

    // La jerarquia del texto (dato > etiqueta > apagado) tiene que sobrevivir al cambio.
    [Fact]
    public void TextHierarchy_HoldsInBothThemes()
    {
        foreach (AppTheme t in Enum.GetValues<AppTheme>())
        {
            double data = Luma(Hex(t, "TextDataBrush"));
            double label = Luma(Hex(t, "TextLabelBrush"));
            double muted = Luma(Hex(t, "TextMutedBrush"));
            double bg = Luma(Hex(t, "BgBrush"));
            // El dato es el que mas contrasta con el fondo; el apagado el que menos.
            Assert.True(Math.Abs(data - bg) > Math.Abs(label - bg), $"{t}: dato vs etiqueta");
            Assert.True(Math.Abs(label - bg) > Math.Abs(muted - bg), $"{t}: etiqueta vs apagado");
        }
    }

    [Fact]
    public void StatusColours_StayReadableAgainstTheirBackground()
    {
        foreach (AppTheme t in Enum.GetValues<AppTheme>())
        {
            double bg = Luma(Hex(t, "BgBrush"));
            foreach (var key in new[] { "StatusOkBrush", "StatusWarnBrush", "StatusErrorBrush" })
                Assert.True(Math.Abs(Luma(Hex(t, key)) - bg) > 40,
                            $"{t}/{key} se confunde con el fondo");
        }
    }
}
```

- [ ] **Step 3: Verificar que fallan** — `dotnet test HidusbfModernGui.Tests\HidusbfModernGui.Tests.csproj --filter "FullyQualifiedName~ThemePaletteTests"`. Esperado: error de compilación, `'ThemePalette' does not exist`.

- [ ] **Step 4: Implementación** — crear `HidusbfModernGui/ThemePalette.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace HidusbfModernGui
{
    // Los dos modos de la app. Noche es el de siempre; Dia es la MISMA disciplina
    // monocroma invertida, no una paleta nueva de colores.
    public enum AppTheme { Noche, Dia }

    // Una entrada de paleta: la clave del recurso y su color. Cadena hex y no un tipo de
    // WPF a proposito, para que esto sea dato puro y se pueda probar sin arrancar WPF.
    public readonly record struct PaletteEntry(string Key, string Hex);

    // La paleta de cada modo. Es la UNICA lista de colores de la app: si un color no esta
    // aqui, o es uno de los tres pinceles fijos del diagrama, no deberia existir.
    public static class ThemePalette
    {
        // Las claves que ThemeManager buscara en los recursos de la aplicacion. Deben
        // coincidir exactamente con los x:Key de Theme.xaml.
        public static readonly IReadOnlyList<string> Keys = new[]
        {
            "BgBrush", "SurfaceBrush", "SurfaceAltBrush", "BorderBrush",
            "TextDataBrush", "TextLabelBrush", "TextMutedBrush", "PadIdleBrush",
            "StatusOkBrush", "StatusWarnBrush", "StatusErrorBrush",
        };

        public static IReadOnlyList<PaletteEntry> For(AppTheme theme) =>
            theme == AppTheme.Dia ? Dia : Noche;

        // Modo noche: exactamente los valores con los que la app se diseno.
        private static readonly PaletteEntry[] Noche =
        {
            new("BgBrush",          "#000000"),
            new("SurfaceBrush",     "#0A0A0A"),
            new("SurfaceAltBrush",  "#111111"),
            new("BorderBrush",      "#1F1F1F"),
            new("TextDataBrush",    "#FFFFFF"),
            new("TextLabelBrush",   "#8A8A8A"),
            new("TextMutedBrush",   "#4A4A4A"),
            new("PadIdleBrush",     "#3A3A3A"),
            new("StatusOkBrush",    "#00C853"),
            new("StatusWarnBrush",  "#FFAB00"),
            new("StatusErrorBrush", "#FF3D00"),
        };

        // Modo dia. Ojo con dos cosas que NO son una inversion mecanica:
        //
        // - Las superficies van hacia ABAJO. Sobre negro un panel se separa aclarandose;
        //   sobre blanco tiene que oscurecerse, o no se ve que hay un panel.
        // - Los tres colores de estado bajan de luminosidad. #00C853 sobre blanco es casi
        //   ilegible; #00A344 dice lo mismo y se lee. Siguen codificando solo hechos del
        //   sistema, que es la unica licencia que tiene el color en esta app.
        private static readonly PaletteEntry[] Dia =
        {
            new("BgBrush",          "#FFFFFF"),
            new("SurfaceBrush",     "#F4F4F4"),
            new("SurfaceAltBrush",  "#E9E9E9"),
            new("BorderBrush",      "#D4D4D4"),
            new("TextDataBrush",    "#0A0A0A"),
            new("TextLabelBrush",   "#5C5C5C"),
            new("TextMutedBrush",   "#9C9C9C"),
            new("PadIdleBrush",     "#C6C6C6"),
            new("StatusOkBrush",    "#00A344"),
            new("StatusWarnBrush",  "#A66A00"),
            new("StatusErrorBrush", "#C62828"),
        };
    }
}
```

- [ ] **Step 5: Verificar que pasan** — mismo filtro, esperado `Failed: 0`. Luego la suite entera: `dotnet test HidusbfModernGui.Tests\HidusbfModernGui.Tests.csproj`, esperado `Failed: 0, Passed: 378`.

- [ ] **Step 6: Commit**

```bash
git add HidusbfModernGui/ThemePalette.cs HidusbfModernGui.Tests/ThemePaletteTests.cs HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj
git commit -m "feat: ThemePalette - la paleta de los modos dia y noche (TDD)"
```

---

### Task 3: `UiPrefs` — el tema y el puerto, recordados (TDD)

**Files:**
- Create: `HidusbfModernGui/UiPrefs.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj`
- Test: `HidusbfModernGui.Tests/UiPrefsTests.cs`

**Interfaces:**
- Consumes: `AppTheme` (Task 2).
- Produces: `UiPrefs { AppTheme Theme; int ObsPort; }`; `UiPrefsStore.Load() -> UiPrefs`, `UiPrefsStore.Save(UiPrefs) -> OpResult`, `UiPrefsStore.Path`, `UiPrefsStore.OverrideDirectoryForTests(string?)`. Consumido por Task 4 (tema) y Task 8 (puerto).

**Por qué un archivo nuevo y no `active.json`:** `LightIntent` es el estado de la **luz del mando**, no de la interfaz. Meter ahí el tema haría que borrar la intención de luz (un caso normal) se llevara por delante la preferencia de apariencia.

- [ ] **Step 1: Link en el csproj**:

```xml
<Compile Include="..\HidusbfModernGui\UiPrefs.cs" Link="UiPrefs.cs" />
```

- [ ] **Step 2: Tests que fallan** — crear `HidusbfModernGui.Tests/UiPrefsTests.cs`:

```csharp
using System;
using System.IO;
using HidusbfModernGui;
using Xunit;

public class UiPrefsTests : IDisposable
{
    private readonly string _dir;

    public UiPrefsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "UltraPollingUi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        UiPrefsStore.OverrideDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        UiPrefsStore.OverrideDirectoryForTests(null);
        try { Directory.Delete(_dir, true); } catch { }
    }

    // Primer arranque: la app se abre como siempre se ha abierto.
    [Fact]
    public void Load_WithoutFile_DefaultsToNight()
    {
        var p = UiPrefsStore.Load();
        Assert.Equal(AppTheme.Noche, p.Theme);
        Assert.Equal(UiPrefsStore.DefaultObsPort, p.ObsPort);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        Assert.True(UiPrefsStore.Save(new UiPrefs { Theme = AppTheme.Dia, ObsPort = 9001 }).Success);
        var back = UiPrefsStore.Load();
        Assert.Equal(AppTheme.Dia, back.Theme);
        Assert.Equal(9001, back.ObsPort);
    }

    // Un archivo a medio escribir o editado a mano no puede impedir que la app abra.
    [Fact]
    public void Load_WithCorruptFile_FallsBackToDefaults()
    {
        File.WriteAllText(UiPrefsStore.Path, "{ esto no es json");
        var p = UiPrefsStore.Load();
        Assert.Equal(AppTheme.Noche, p.Theme);
        Assert.Equal(UiPrefsStore.DefaultObsPort, p.ObsPort);
    }

    // Un puerto imposible (0, negativo, fuera de rango) se corrige al leer, no al usar:
    // asi ningun consumidor tiene que volver a validarlo.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(70000)]
    public void Load_WithImpossiblePort_FallsBackToDefault(int bad)
    {
        File.WriteAllText(UiPrefsStore.Path, $"{{\"Theme\":\"Noche\",\"ObsPort\":{bad}}}");
        Assert.Equal(UiPrefsStore.DefaultObsPort, UiPrefsStore.Load().ObsPort);
    }

    [Fact]
    public void Theme_IsStoredByName_NotByNumber()
    {
        UiPrefsStore.Save(new UiPrefs { Theme = AppTheme.Dia, ObsPort = 8787 });
        Assert.Contains("\"Dia\"", File.ReadAllText(UiPrefsStore.Path));
    }
}
```

- [ ] **Step 3: Verificar que fallan** — `dotnet test ... --filter "FullyQualifiedName~UiPrefsTests"`. Esperado: error de compilación.

- [ ] **Step 4: Implementación** — crear `HidusbfModernGui/UiPrefs.cs`:

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace HidusbfModernGui
{
    // Preferencias de la INTERFAZ, separadas del estado del mando a proposito: borrar la
    // intencion de luz (active.json) es un caso normal y no puede llevarse por delante el
    // tema que el usuario eligio.
    public sealed class UiPrefs
    {
        public AppTheme Theme { get; set; } = AppTheme.Noche;
        public int ObsPort { get; set; } = UiPrefsStore.DefaultObsPort;
    }

    // Espejo de los demas stores: mismo %APPDATA%\UltraPolling, escritura con copia
    // .backup, enums por nombre.
    public static class UiPrefsStore
    {
        // 8787 no es especial: es alto, libre en una instalacion tipica y facil de teclear.
        // Si esta ocupado, StreamerServer prueba los siguientes (ver Task 7).
        public const int DefaultObsPort = 8787;

        private static string? _overrideDir;

        internal static void OverrideDirectoryForTests(string? dir) => _overrideDir = dir;

        private static string Directory_ => _overrideDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraPolling");

        public static string Path => System.IO.Path.Combine(Directory_, "ui.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        public static UiPrefs Load()
        {
            try
            {
                if (!File.Exists(Path)) return new UiPrefs();
                string json = File.ReadAllText(Path);
                if (string.IsNullOrWhiteSpace(json)) return new UiPrefs();

                var p = JsonSerializer.Deserialize<UiPrefs>(json, Options) ?? new UiPrefs();
                // El puerto se sanea AQUI, al leer, para que ningun consumidor tenga que
                // volver a validarlo. 1024 es el limite de los puertos privilegiados.
                if (p.ObsPort < 1024 || p.ObsPort > 65535) p.ObsPort = DefaultObsPort;
                return p;
            }
            catch (Exception ex)
            {
                // Un ui.json roto jamas puede impedir que la app abra: se abre como siempre.
                Debug.WriteLine($"UiPrefsStore.Load fallo, valores por defecto: {ex.Message}");
                return new UiPrefs();
            }
        }

        public static OpResult Save(UiPrefs prefs)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory_);
                if (File.Exists(Path)) File.Copy(Path, Path + ".backup", true);
                File.WriteAllText(Path, JsonSerializer.Serialize(prefs, Options));
                return OpResult.Ok();
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"No se pudieron guardar las preferencias: {ex.Message}");
            }
        }
    }
}
```

- [ ] **Step 5: Verificar que pasan** — mismo filtro PASS, después la suite entera PASS (`Passed: 385`).

- [ ] **Step 6: Commit**

```bash
git add HidusbfModernGui/UiPrefs.cs HidusbfModernGui.Tests/UiPrefsTests.cs HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj
git commit -m "feat: UiPrefs - el tema y el puerto de OBS, recordados entre sesiones (TDD)"
```

---

### Task 4: El cambio de tema en vivo, y el diagrama fuera de la paleta

**Files:**
- Create: `HidusbfModernGui/ThemeManager.cs`
- Modify: `HidusbfModernGui/Theme.xaml`
- Modify: `HidusbfModernGui/MainWindow.xaml` (pestaña Settings, ~línea 442) y `MainWindow.xaml.cs` (`Window_Loaded`, `BuildButtonDiagram`, `RefreshButtonPills`, `Ink`/`Paper`)

**Interfaces:**
- Consumes: `ThemePalette.For/Keys` (Task 2), `UiPrefsStore.Load/Save` (Task 3).
- Produces: `ThemeManager.Current -> AppTheme`; `ThemeManager.Apply(AppTheme) -> IReadOnlyList<string>` (las claves que **no** se pudieron aplicar; vacía = todo bien).

**Por qué funciona sin tocar 397 referencias:** `StaticResource` resuelve **una vez** y guarda la **instancia** del pincel. Si en vez de reemplazar el pincel se le cambia el `Color` (que es una `DependencyProperty`), todos los que lo comparten se repintan solos. El único enemigo es un pincel congelado (`Freeze`), que es inmutable: por eso `Apply` los detecta y los devuelve en vez de fallar en silencio.

- [ ] **Step 1: Los pinceles fijos del diagrama.** En `Theme.xaml`, justo debajo de `PadIdleBrush`:

```xml
<!-- El panel del diagrama de botones NO sigue el tema, a proposito. Su dibujo es un PNG
     de tinta oscura sobre papel blanco: si el papel siguiera a TextDataBrush, en modo dia
     el panel se volveria negro debajo de un dibujo que sigue siendo blanco. Estos dos son
     el papel y la tinta de ESE panel, y son fijos porque la imagen lo es. -->
<SolidColorBrush x:Key="DiagramPaperBrush" Color="#FFFFFF"/>
<SolidColorBrush x:Key="DiagramInkBrush"   Color="#000000"/>
```

- [ ] **Step 2: `PillButtonInk` usa la tinta fija.** En ese estilo, cambiar `Foreground` de `{StaticResource BgBrush}` a `{StaticResource DiagramInkBrush}`.

- [ ] **Step 3: Crear `HidusbfModernGui/ThemeManager.cs`**:

```csharp
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace HidusbfModernGui
{
    // Cambia el tema MUTANDO el Color de los pinceles que ya estan en los recursos de la
    // aplicacion, en vez de reemplazarlos.
    //
    // Por que: los ~400 StaticResource del XAML resuelven UNA vez y se quedan con la
    // INSTANCIA del pincel. Reemplazar el recurso no los tocaria (habria que reabrir cada
    // ventana); cambiarle el Color, que es una DependencyProperty, los repinta a todos al
    // instante. Es lo que permite tener modo dia sin convertir 397 referencias a
    // DynamicResource ni recargar la ventana debajo del usuario.
    //
    // El unico enemigo es un pincel congelado (Freeze): es inmutable y se quedaria del
    // color viejo sin decir nada. Por eso Apply los devuelve en vez de tragarselos.
    public static class ThemeManager
    {
        public static AppTheme Current { get; private set; } = AppTheme.Noche;

        // Devuelve las claves que NO se pudieron aplicar (ausentes de los recursos o
        // congeladas). Lista vacia = tema aplicado entero.
        public static IReadOnlyList<string> Apply(AppTheme theme)
        {
            var failed = new List<string>();
            var res = Application.Current?.Resources;
            if (res == null) return ThemePalette.Keys;

            foreach (var entry in ThemePalette.For(theme))
            {
                if (res[entry.Key] is not SolidColorBrush brush || brush.IsFrozen)
                {
                    failed.Add(entry.Key);
                    continue;
                }
                brush.Color = (Color)ColorConverter.ConvertFromString(entry.Hex);
            }

            Current = theme;
            return failed;
        }
    }
}
```

- [ ] **Step 4: El interruptor, en Ajustes.** En `MainWindow.xaml`, dentro de la pestaña `Settings`, **antes** del bloque `SERVICIO` (que empieza en `<TextBlock Text="SERVICIO" ...>`):

```xml
<TextBlock Text="APARIENCIA" Style="{StaticResource SectionHeading}"/>
<Border Background="{StaticResource SurfaceBrush}" BorderBrush="{StaticResource BorderBrush}"
        BorderThickness="1" Padding="18" Margin="0,8,0,20">
    <StackPanel>
        <CheckBox x:Name="DayThemeCheck" Content="Modo dia (fondo claro)"
                  Foreground="{StaticResource TextDataBrush}" FontSize="12"
                  Click="DayTheme_Click"/>
        <!-- Se queda: explica algo que NO se ve, que es por que hay una pagina que no
             cambia con el resto. Sin esta linea parece un fallo. -->
        <TextBlock Style="{StaticResource FieldLabel}" TextWrapping="Wrap" Margin="0,10,0,0"
                   Text="El diagrama de asignacion de botones no cambia con el tema: su dibujo es tinta oscura sobre papel blanco, y ese panel se queda claro en los dos modos."/>
    </StackPanel>
</Border>
```

- [ ] **Step 5: El handler y el arranque.** En `MainWindow.xaml.cs`, junto a los demás handlers de Ajustes:

```csharp
// El tema se aplica al pulsar y se guarda: sin boton "Aplicar", como todo en esta app.
private void DayTheme_Click(object sender, RoutedEventArgs e)
{
    var theme = DayThemeCheck.IsChecked == true ? AppTheme.Dia : AppTheme.Noche;
    var failed = ThemeManager.Apply(theme);
    if (failed.Count > 0)
        LogStatus($"Tema aplicado a medias: {string.Join(", ", failed)} no se pudieron cambiar.");
    else
        LogStatus(theme == AppTheme.Dia ? "Modo dia." : "Modo noche.");

    var prefs = UiPrefsStore.Load();
    prefs.Theme = theme;
    UiPrefsStore.Save(prefs);
}
```

Y en `Window_Loaded`, **lo primero de todo** (antes de `BuildHeaderSpectrum()`), para que la ventana no aparezca un instante con el tema equivocado:

```csharp
// El tema, antes que nada: aplicarlo despues de construir la cabecera haria que la
// ventana parpadease del tema viejo al nuevo delante del usuario.
var uiPrefs = UiPrefsStore.Load();
ThemeManager.Apply(uiPrefs.Theme);
DayThemeCheck.IsChecked = uiPrefs.Theme == AppTheme.Dia;
```

- [ ] **Step 6: El diagrama deja de seguir la paleta.** En `MainWindow.xaml.cs`, sustituir las dos propiedades `Ink`/`Paper` por:

```csharp
// Los dos pinceles del diagrama. Con imagen, el panel es claro y FIJO: su papel y su
// tinta no siguen el tema, porque el PNG del mando es tinta oscura sobre papel blanco y
// en modo dia se invertirian dejando papel negro bajo un dibujo blanco. Sin imagen, el
// respaldo es el mando VECTORIAL, que si se dibuja con los pinceles del tema, asi que
// ahi papel y tinta son los del tema y acompanan a dia/noche.
private Brush Ink => _diagramIsLight
    ? (Brush)FindResource("DiagramInkBrush")
    : (Brush)FindResource("TextDataBrush");

private Brush Paper => _diagramIsLight
    ? (Brush)FindResource("DiagramPaperBrush")
    : (Brush)FindResource("BgBrush");
```

En `BuildButtonDiagram`, cambiar las dos líneas que eligen fondo y color de línea:

```csharp
DiagramPanel.Background = (Brush)FindResource(_diagramIsLight ? "DiagramPaperBrush" : "SurfaceBrush");
```

```csharp
Stroke = _diagramIsLight ? (Brush)FindResource("DiagramInkBrush") : (Brush)FindResource("TextLabelBrush"),
```

Y en `RefreshButtonPills`, la línea del borde:

```csharp
pill.BorderBrush = _diagramIsLight
    ? (Brush)FindResource(remapped ? "DiagramInkBrush" : "TextLabelBrush")
    : (Brush)FindResource(remapped ? "TextLabelBrush" : "BorderBrush");
```

- [ ] **Step 7: El panel blanco necesita un borde.** En `MainWindow.xaml`, el `Border x:Name="DiagramPanel"` gana `BorderBrush="{StaticResource BorderBrush}" BorderThickness="1"`. Motivo: en modo día el fondo de la app es blanco y un panel blanco sin borde no se distingue de la página.

- [ ] **Step 8: Verificación** — build 0/0 y suite completa PASS. Manual, en este orden:
  1. Abrir la app, ir a Ajustes, marcar **Modo dia**: toda la interfaz debe cambiar **al instante**, sin parpadeos ni ventanas recargadas, y la barra de estado dice "Modo dia.". Si dice "Tema aplicado a medias", **hay un pincel congelado** y hay que quitarle el `Freeze` en `Theme.xaml`.
  2. Con modo día puesto, entrar en ASIGNACION DE BOTONES: el panel del mando sigue **blanco con tinta negra** y ahora tiene borde.
  3. Recorrer las tres pestañas (Dispositivos, Ajustes, Mando) y las tres sub-secciones del mando buscando texto que se haya quedado ilegible.
  4. **Cerrar y reabrir**: la app abre directamente en modo día.
  5. Desmarcar: vuelve a noche, y al reabrir sigue en noche.

- [ ] **Step 9: Commit**

```bash
git add HidusbfModernGui/ThemeManager.cs && git add -u
git commit -m "feat(ui): modo dia y noche en vivo, mutando los pinceles del tema"
```

---

### Task 5: La tuerca del mando en vivo, en una sola columna

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml` (el `Popup x:Name="LiveOptionsPopup"`)

- [ ] **Step 1: Reemplazar el contenido del `Border` del popup** por una columna. Cada control ocupa su propia línea y los botones se estiran, para que el bloque se lea de arriba abajo en vez de en dos filas de dos:

```xml
<Border Background="{StaticResource SurfaceBrush}" BorderBrush="{StaticResource BorderBrush}"
        BorderThickness="1" Padding="16" MinWidth="300">
    <StackPanel>
        <!-- Estado del skin (Task SK3): que se esta dibujando (skin instalado o el
             vectorial) y control para recargar tras instalar/actualizar uno. -->
        <TextBlock x:Name="SkinStatusText" Style="{StaticResource FieldLabel}" TextWrapping="Wrap"/>
        <Button Content="RECARGAR SKIN" Style="{StaticResource InstrumentButton}"
                HorizontalAlignment="Stretch" Margin="0,12,0,0" Click="ReloadSkin_Click"/>
        <CheckBox x:Name="CalibrationCheck" Content="Modo calibracion" Margin="0,12,0,0"
                  Foreground="{StaticResource TextDataBrush}" Click="Calibration_Click"/>

        <Border Height="1" Background="{StaticResource BorderBrush}" Margin="0,16,0,0"/>

        <!-- Modo streamer: overlay transparente para capturar por ventana, ver StreamerWindow. -->
        <Button x:Name="StreamerToggle" Content="MODO STREAMER" Style="{StaticResource InstrumentButton}"
                HorizontalAlignment="Stretch" Margin="0,16,0,0" Click="StreamerToggle_Click"/>
        <!-- Apaga el pasa-clic del overlay sin destruirlo: una vez ON desde la barra del
             propio streamer, el raton lo atraviesa entero y su ToggleButton deja de ser
             alcanzable, asi que esta ventana (nunca click-through) es el unico camino de
             vuelta. Solo usable con el streamer abierto. -->
        <CheckBox x:Name="StreamerClickThrough" Content="Overlay atraviesa clic" Margin="0,12,0,0"
                  IsEnabled="False" Foreground="{StaticResource TextDataBrush}"
                  Click="StreamerClickThrough_Click"/>
    </StackPanel>
</Border>
```

(El bloque de OBS entra en este mismo `StackPanel` en la Task 8; se deja fuera aquí para que esta tarea sea puramente de disposición y se pueda revisar sola.)

- [ ] **Step 2: Verificación** — build 0/0. Manual: abrir la tuerca del mando en vivo y comprobar que los cinco elementos caen en una columna, que RECARGAR SKIN y MODO STREAMER ocupan el ancho, y que **los tres controles siguen funcionando**: recargar skin actualiza la línea de estado, el modo calibración pinta los recuadros magenta sobre el mando, y MODO STREAMER abre el overlay y habilita el checkbox de pasa-clic.

- [ ] **Step 3: Commit**

```bash
git add -u && git commit -m "refactor(ui): la tuerca del mando en vivo, en una sola columna"
```

---

### Task 6: `PadWebModel` — el skin y el estado, en JSON para el navegador (TDD)

**Files:**
- Create: `HidusbfModernGui/PadWebModel.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj`
- Test: `HidusbfModernGui.Tests/PadWebModelTests.cs`

**Interfaces:**
- Consumes: `PadSkin`, `SkinPart`, `SkinRect`, `ControllerState`, `PadButton` (ya existen).
- Produces: `PadWebModel.SkinJson(PadSkin) -> string`; `PadWebModel.StateJson(ControllerState) -> string`; `PadWebModel.IsSafeFileName(string?) -> bool`. Consumido por `StreamerServer` (Task 7).

**El formato del estado**, corto porque va 60 veces por segundo:
`{"lx":0,"ly":0,"rx":0,"ry":0,"l2":0,"r2":0,"btn":["Cross","L1"]}`

**La guarda de nombres es lo importante de seguridad de esta tarea.** El servidor sirve archivos de la carpeta del skin a partir de un nombre que viene en la URL. Sin guarda, `GET /skin/..%2F..%2F..%2Fwindows%2Fwin.ini` leería fuera de la carpeta. La regla es la mínima que se sostiene: **solo nombres simples**, sin separadores, sin `..` y sin raíz.

- [ ] **Step 1: Link en el csproj**:

```xml
<Compile Include="..\HidusbfModernGui\PadWebModel.cs" Link="PadWebModel.cs" />
```

- [ ] **Step 2: Tests que fallan** — crear `HidusbfModernGui.Tests/PadWebModelTests.cs`:

```csharp
using System.Collections.Generic;
using System.Text.Json;
using HidusbfModernGui;
using Xunit;

public class PadWebModelTests
{
    private static PadSkin Sample() => new()
    {
        Name = "PS5 White",
        BaseFile = "base.png",
        BaseWidth = 1000,
        BaseHeight = 700,
        StickRadius = 18,
        Parts = new Dictionary<string, SkinPart>
        {
            ["stick.left"] = new()
            {
                File = "sheet.png",
                Src = new SkinRect { X = 10, Y = 20, W = 30, H = 40 },
                Src2 = new SkinRect { X = 50, Y = 20, W = 30, H = 40 },
                Dst = new SkinRect { X = 100, Y = 200, W = 60, H = 80 },
            },
            ["btn.cross"] = new()
            {
                File = "faces.png",
                Src = new SkinRect { X = 0, Y = 0, W = 24, H = 24 },
                Dst = new SkinRect { X = 700, Y = 300, W = 24, H = 24 },
            },
        },
    };

    [Fact]
    public void SkinJson_CarriesTheBaseAndItsSize()
    {
        using var doc = JsonDocument.Parse(PadWebModel.SkinJson(Sample()));
        var root = doc.RootElement;
        Assert.Equal("base.png", root.GetProperty("baseFile").GetString());
        Assert.Equal(1000, root.GetProperty("baseWidth").GetDouble());
        Assert.Equal(700, root.GetProperty("baseHeight").GetDouble());
        Assert.Equal(18, root.GetProperty("stickRadius").GetDouble());
    }

    // El navegador dibuja recorriendo "parts" en orden, igual que WPF recorre skin.Parts:
    // si el orden se perdiera, una capa taparia a otra que deberia ir encima.
    [Fact]
    public void SkinJson_KeepsEveryPartWithItsKeyAndOrder()
    {
        using var doc = JsonDocument.Parse(PadWebModel.SkinJson(Sample()));
        var parts = doc.RootElement.GetProperty("parts");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal("stick.left", parts[0].GetProperty("key").GetString());
        Assert.Equal("btn.cross", parts[1].GetProperty("key").GetString());
    }

    [Fact]
    public void SkinJson_CarriesSrcSrc2AndDstOfEachPart()
    {
        using var doc = JsonDocument.Parse(PadWebModel.SkinJson(Sample()));
        var stick = doc.RootElement.GetProperty("parts")[0];
        Assert.Equal("sheet.png", stick.GetProperty("file").GetString());
        Assert.Equal(10, stick.GetProperty("src").GetProperty("x").GetDouble());
        Assert.Equal(50, stick.GetProperty("src2").GetProperty("x").GetDouble());
        Assert.Equal(100, stick.GetProperty("dst").GetProperty("x").GetDouble());
        Assert.Equal(60, stick.GetProperty("dst").GetProperty("w").GetDouble());
    }

    // Src2 es opcional: la cruz no lo tiene y el navegador debe poder distinguirlo.
    [Fact]
    public void SkinJson_OmitsSrc2WhenThePartHasNone()
    {
        using var doc = JsonDocument.Parse(PadWebModel.SkinJson(Sample()));
        var cross = doc.RootElement.GetProperty("parts")[1];
        Assert.False(cross.TryGetProperty("src2", out var s2) && s2.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public void SkinJson_WithNoParts_EmitsAnEmptyArrayNotNull()
    {
        var skin = Sample();
        skin.Parts = new Dictionary<string, SkinPart>();
        using var doc = JsonDocument.Parse(PadWebModel.SkinJson(skin));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("parts").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("parts").GetArrayLength());
    }

    [Fact]
    public void StateJson_CarriesSticksTriggersAndPressedButtons()
    {
        var s = new ControllerState { Left = new StickInput(0.5, -0.25), R2 = 0.75 };
        s.Pressed.Add(PadButton.Cross);
        s.Pressed.Add(PadButton.L1);

        using var doc = JsonDocument.Parse(PadWebModel.StateJson(s));
        var root = doc.RootElement;
        Assert.Equal(0.5, root.GetProperty("lx").GetDouble(), 3);
        Assert.Equal(-0.25, root.GetProperty("ly").GetDouble(), 3);
        Assert.Equal(0.75, root.GetProperty("r2").GetDouble(), 3);

        var btn = root.GetProperty("btn");
        var names = new List<string>();
        foreach (var b in btn.EnumerateArray()) names.Add(b.GetString()!);
        Assert.Contains("Cross", names);
        Assert.Contains("L1", names);
        Assert.DoesNotContain("Circle", names);
    }

    [Fact]
    public void StateJson_WithNothingPressed_EmitsAnEmptyArray()
    {
        using var doc = JsonDocument.Parse(PadWebModel.StateJson(new ControllerState()));
        Assert.Equal(0, doc.RootElement.GetProperty("btn").GetArrayLength());
    }

    // El estado va por SSE, donde un salto de linea CORTA el mensaje. Si alguna vez se
    // colara uno, el flujo se desincronizaria en silencio.
    [Fact]
    public void StateJson_IsASingleLine()
    {
        var s = new ControllerState();
        s.Pressed.Add(PadButton.Triangle);
        Assert.DoesNotContain("\n", PadWebModel.StateJson(s));
        Assert.DoesNotContain("\r", PadWebModel.StateJson(s));
    }

    [Theory]
    [InlineData("base.png")]
    [InlineData("bumper_r.png")]
    [InlineData("11_face.PNG")]
    public void IsSafeFileName_AcceptsPlainNames(string name)
        => Assert.True(PadWebModel.IsSafeFileName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../secret.png")]
    [InlineData("..\\secret.png")]
    [InlineData("sub/dir.png")]
    [InlineData("sub\\dir.png")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("\\\\servidor\\share\\x.png")]
    [InlineData("..")]
    public void IsSafeFileName_RejectsAnythingThatCouldEscapeTheSkinFolder(string? name)
        => Assert.False(PadWebModel.IsSafeFileName(name));
}
```

- [ ] **Step 3: Verificar que fallan** — `dotnet test ... --filter "FullyQualifiedName~PadWebModelTests"`. Esperado: error de compilación.

- [ ] **Step 4: Implementación** — crear `HidusbfModernGui/PadWebModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HidusbfModernGui
{
    // El skin y el estado del mando, en la forma que entiende la pagina que se le da a OBS.
    //
    // El JSON del skin es un calco de PadSkin a proposito: la pagina dibuja con
    // drawImage(hoja, sx,sy,sw,sh, dx,dy,dw,dh), que es EXACTAMENTE (Src, Dst). Asi el
    // navegador y SkinnedPadVisual pintan lo mismo sin una segunda copia de la geometria
    // que se pueda desincronizar.
    public static class PadWebModel
    {
        private sealed record WebRect(double X, double Y, double W, double H);

        private sealed record WebPart(
            string Key, string File, WebRect Src,
            [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WebRect? Src2,
            WebRect Dst);

        private sealed record WebSkin(
            string Name, string BaseFile, double BaseWidth, double BaseHeight,
            double StickRadius, IReadOnlyList<WebPart> Parts);

        private sealed record WebState(
            double Lx, double Ly, double Rx, double Ry,
            double L2, double R2, IReadOnlyList<string> Btn);

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Sin indentar: el estado sale 60 veces por segundo y en SSE un salto de linea
            // CORTA el mensaje. Una sola linea es correccion, no ahorro.
            WriteIndented = false,
        };

        private static WebRect Of(SkinRect r) => new(r.X, r.Y, r.W, r.H);

        public static string SkinJson(PadSkin skin)
        {
            // El orden importa: la pagina dibuja recorriendo el array, igual que WPF recorre
            // skin.Parts al construir las capas. Cambiarlo cambiaria que tapa a que.
            var parts = skin.Parts
                .Select(kv => new WebPart(
                    kv.Key, kv.Value.File, Of(kv.Value.Src),
                    kv.Value.Src2 != null && kv.Value.Src2.IsValid ? Of(kv.Value.Src2) : null,
                    Of(kv.Value.Dst)))
                .ToList();

            return JsonSerializer.Serialize(
                new WebSkin(skin.Name, skin.BaseFile, skin.BaseWidth, skin.BaseHeight,
                            skin.StickRadius, parts),
                Options);
        }

        public static string StateJson(ControllerState s) => JsonSerializer.Serialize(
            new WebState(s.Left.X, s.Left.Y, s.Right.X, s.Right.Y, s.L2, s.R2,
                         s.Pressed.Select(b => b.ToString()).ToList()),
            Options);

        // El servidor sirve archivos de la carpeta del skin a partir de un nombre que viene
        // en la URL. Sin esta guarda, "/skin/../../windows/win.ini" leeria fuera de ella.
        // Solo nombres simples: sin separadores, sin "..", sin raiz ni unidad.
        public static bool IsSafeFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.Contains("..", StringComparison.Ordinal)) return false;
            if (name.Contains('/') || name.Contains('\\')) return false;
            if (name.Contains(':')) return false;
            if (System.IO.Path.IsPathRooted(name)) return false;
            // Y que el propio Windows lo considere un nombre y no una ruta.
            return System.IO.Path.GetFileName(name) == name;
        }
    }
}
```

- [ ] **Step 5: Verificar que pasan** — mismo filtro PASS, después la suite entera PASS (`Passed: 406`; esta tarea añade 21 casos: 5 de `SkinJson`, 3 de `StateJson` y 13 de `IsSafeFileName` entre las dos `[Theory]`).

- [ ] **Step 6: Commit**

```bash
git add HidusbfModernGui/PadWebModel.cs HidusbfModernGui.Tests/PadWebModelTests.cs HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj
git commit -m "feat: PadWebModel - el skin y el estado en JSON para el navegador (TDD)"
```

---

### Task 7: `StreamerServer` — el servidor local que ve OBS

**Files:**
- Create: `HidusbfModernGui/StreamerServer.cs`

**Interfaces:**
- Consumes: `PadWebModel.SkinJson/StateJson/IsSafeFileName` (Task 6), `PadSkin`, `ControllerState`, `OpResult`.
- Produces: `StreamerServer` con `bool IsRunning`, `string? Url`, `int Port`, `OpResult Start(PadSkin skin, int preferredPort)`, `void Stop()`, `void Push(ControllerState state)`, `void Dispose()`. Consumido por `MainWindow` (Task 8).

**Por qué SSE y no WebSocket:** el flujo va en una sola dirección (app → navegador) y `EventSource` son cuatro líneas de JavaScript sin handshake ni librería. Un WebSocket añadiría protocolo para una capacidad —hablar de vuelta— que esta página no usa.

**Por qué `127.0.0.1` y nunca `+`:** esto publica lo que hace tu mando. En `127.0.0.1` solo lo puede leer un programa de tu propio equipo (OBS). Con `+` o la IP de la LAN, cualquiera de la red lo vería, y no hay ninguna razón para eso: OBS corre en la misma máquina.

**Sin skin no hay enlace.** La página dibuja imágenes de skin; sin un skin instalado no hay nada que servir. `Start` devuelve un fallo con esa explicación en vez de publicar una página en blanco.

- [ ] **Step 1: Crear `HidusbfModernGui/StreamerServer.cs`**:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HidusbfModernGui
{
    // Servidor local que publica el mando en vivo como una pagina web, para meterla en OBS
    // como "Fuente de navegador". Sirve cuatro cosas:
    //
    //   GET /            la pagina (canvas + EventSource), incrustada abajo
    //   GET /skin.json   la geometria del skin (PadWebModel)
    //   GET /skin/<x>    un PNG de la carpeta del skin, con guarda de nombre
    //   GET /state       flujo SSE con el estado TRANSFORMADO, ~60 veces por segundo
    //
    // Escucha SOLO en 127.0.0.1. Esto publica lo que hace tu mando: en loopback solo lo
    // puede leer un programa de tu propio equipo, que es donde corre OBS. Nunca "+" ni "*".
    public sealed class StreamerServer : IDisposable
    {
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private PadSkin? _skin;

        // Clientes SSE conectados. Pocos a proposito: esto alimenta una fuente de OBS, no
        // es un servidor de verdad. El tope evita que una pagina recargada en bucle deje
        // flujos colgando para siempre.
        private const int MaxClients = 4;
        private readonly List<SseClient> _clients = new();
        private readonly object _lock = new();

        public bool IsRunning => _listener?.IsListening == true;
        public int Port { get; private set; }
        public string? Url => IsRunning ? $"http://127.0.0.1:{Port}/" : null;

        // Tamano exacto que hay que poner en la fuente de OBS para que salga sin escalar.
        public double Width => _skin?.BaseWidth ?? 0;
        public double Height => _skin?.BaseHeight ?? 0;

        // Prueba preferredPort y los 9 siguientes. Un puerto ocupado es lo normal (otra
        // copia de la app, otra herramienta), no un error que merezca rendirse.
        public OpResult Start(PadSkin skin, int preferredPort)
        {
            if (IsRunning) return OpResult.Ok();
            if (skin == null || string.IsNullOrWhiteSpace(skin.BaseFile))
                return OpResult.Fail("No hay skin instalado: el enlace de OBS dibuja el mando con las imagenes del skin. Instala uno y vuelve a intentarlo.");

            _skin = skin;

            for (int port = preferredPort; port < preferredPort + 10; port++)
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    Port = port;
                    _cts = new CancellationTokenSource();
                    _ = Task.Run(() => AcceptLoop(_cts.Token));
                    return OpResult.Ok();
                }
                catch (HttpListenerException)
                {
                    listener.Close();   // puerto ocupado: al siguiente
                }
                catch (Exception ex)
                {
                    listener.Close();
                    return OpResult.Fail($"No se pudo abrir el servidor: {ex.Message}");
                }
            }

            _skin = null;
            return OpResult.Fail($"Los puertos {preferredPort}-{preferredPort + 9} estan ocupados. Cambia el puerto en ui.json.");
        }

        public void Stop()
        {
            _cts?.Cancel();
            lock (_lock)
            {
                foreach (var c in _clients) c.Close();
                _clients.Clear();
            }
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;
            _cts = null;
            _skin = null;
        }

        public void Dispose() => Stop();

        // Llamado desde el tick del visualizador (hilo de UI). NO puede bloquear: si un
        // cliente va lento se le SALTA este cuadro en vez de encolarlo. Para una vista en
        // vivo, perder un cuadro es correcto; acumular retraso, no.
        public void Push(ControllerState state)
        {
            if (!IsRunning) return;

            List<SseClient> snapshot;
            lock (_lock)
            {
                if (_clients.Count == 0) return;
                snapshot = new List<SseClient>(_clients);
            }

            byte[] payload = Encoding.UTF8.GetBytes($"data: {PadWebModel.StateJson(state)}\n\n");
            var dead = new List<SseClient>();
            foreach (var c in snapshot)
                if (!c.TrySend(payload)) dead.Add(c);

            if (dead.Count == 0) return;
            lock (_lock) foreach (var d in dead) { _clients.Remove(d); d.Close(); }
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener?.IsListening == true)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }   // el listener se paro: fin del bucle, sin ruido

                // Cada peticion en su propia tarea: /state no termina nunca y bloquearia
                // el bucle de aceptacion si se atendiera aqui.
                _ = Task.Run(() => Handle(ctx, token));
            }
        }

        private void Handle(HttpListenerContext ctx, CancellationToken token)
        {
            try
            {
                string path = ctx.Request.Url?.AbsolutePath ?? "/";

                if (path == "/" || path == "/index.html") { SendText(ctx, Page, "text/html; charset=utf-8"); return; }
                if (path == "/skin.json") { SendText(ctx, PadWebModel.SkinJson(_skin!), "application/json"); return; }
                if (path == "/state") { StartSse(ctx, token); return; }
                if (path.StartsWith("/skin/", StringComparison.Ordinal)) { SendSkinFile(ctx, Uri.UnescapeDataString(path.Substring(6))); return; }

                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StreamerServer.Handle: {ex.Message}");
                try { ctx.Response.Abort(); } catch { }
            }
        }

        private void SendSkinFile(HttpListenerContext ctx, string name)
        {
            // La guarda es lo que impide que "/skin/../../windows/win.ini" salga de la
            // carpeta del skin. Se comprueba el NOMBRE, no la ruta resultante.
            if (_skin == null || !PadWebModel.IsSafeFileName(name))
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }

            string full = Path.Combine(_skin.Directory, name);
            if (!File.Exists(full))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            byte[] bytes = File.ReadAllBytes(full);
            ctx.Response.ContentType = "image/png";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        private static void SendText(HttpListenerContext ctx, string body, string contentType)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        private void StartSse(HttpListenerContext ctx, CancellationToken token)
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.Add("Cache-Control", "no-cache");
            // Sin KeepAlive el flujo se corta al primer hueco entre cuadros.
            ctx.Response.KeepAlive = true;
            ctx.Response.SendChunked = true;

            var client = new SseClient(ctx.Response);
            lock (_lock)
            {
                while (_clients.Count >= MaxClients)
                {
                    _clients[0].Close();
                    _clients.RemoveAt(0);
                }
                _clients.Add(client);
            }
            // No se cierra aqui: la respuesta queda abierta y Push() escribe en ella hasta
            // que el cliente se va (y entonces TrySend falla y se la retira de la lista).
        }

        // Un cliente SSE. Guarda la escritura en curso para poder SALTARSE un cuadro en vez
        // de encolarlo: si la anterior sigue en vuelo, este cuadro se descarta.
        private sealed class SseClient
        {
            private readonly HttpListenerResponse _res;
            private Task _pending = Task.CompletedTask;

            public SseClient(HttpListenerResponse res) => _res = res;

            public bool TrySend(byte[] payload)
            {
                try
                {
                    if (!_pending.IsCompleted) return true;   // va lento: se le salta el cuadro
                    if (_pending.IsFaulted) return false;
                    _pending = _res.OutputStream.WriteAsync(payload, 0, payload.Length)
                                   .ContinueWith(t => _res.OutputStream.FlushAsync(),
                                                 TaskContinuationOptions.OnlyOnRanToCompletion)
                                   .Unwrap();
                    return true;
                }
                catch { return false; }   // se desconecto
            }

            public void Close() { try { _res.Close(); } catch { } }
        }

        // La pagina. Va incrustada como constante y no como archivo suelto por la misma
        // razon que el resto de la app se publica en un solo exe: un archivo mas es un
        // archivo mas que puede faltar.
        //
        // Dibuja con canvas y drawImage(hoja, sx,sy,sw,sh, dx,dy,dw,dh), que es literalmente
        // (Src, Dst) del skin. El fondo queda transparente, que es lo que OBS espera de una
        // fuente de navegador.
        private const string Page = """
<!doctype html>
<html lang="es">
<meta charset="utf-8">
<title>UltraPolling - mando en vivo</title>
<style>
  html,body{margin:0;padding:0;background:transparent;overflow:hidden}
  canvas{display:block}
  #msg{position:fixed;left:10px;top:10px;font:14px system-ui,sans-serif;
       color:#fff;text-shadow:0 1px 3px #000}
</style>
<canvas id="pad"></canvas>
<div id="msg">Conectando...</div>
<script>
const cv = document.getElementById('pad'), cx = cv.getContext('2d'), msg = document.getElementById('msg');
const sheets = {};
let skin = null, state = null;

// Que capa corresponde a que boton. Las que no estan aqui (sticks y gatillos) se dibujan
// con su propia regla mas abajo.
const BUTTON_OF = {
  'btn.cross':'Cross','btn.circle':'Circle','btn.square':'Square','btn.triangle':'Triangle',
  'dpad.up':'DpadUp','dpad.down':'DpadDown','dpad.left':'DpadLeft','dpad.right':'DpadRight',
  'btn.l1':'L1','btn.r1':'R1','btn.share':'Share','btn.options':'Options',
  'btn.ps':'PS','btn.touchpad':'TouchpadClick'
};

// El mismo acotado que PadVisualMath.StickOffset: el pulgar no sale del pozo ni con el
// stick a fondo en diagonal (magnitud 1.41).
function stickOffset(x, y, r) {
  let dx = x * r, dy = -y * r;
  const m = Math.hypot(dx, dy);
  if (m > r && m > 0) { const k = r / m; dx *= k; dy *= k; }
  return [dx, dy];
}
const fill01 = v => v < 0 ? 0 : v > 1 ? 1 : v;

function blit(p, dx, dy, alpha, useSrc2) {
  const sheet = sheets[p.file];
  if (!sheet) return;
  const s = (useSrc2 && p.src2) ? p.src2 : p.src;
  cx.globalAlpha = alpha;
  cx.drawImage(sheet, s.x, s.y, s.w, s.h, dx, dy, p.dst.w, p.dst.h);
  cx.globalAlpha = 1;
}

function draw() {
  requestAnimationFrame(draw);
  if (!skin) return;
  cx.clearRect(0, 0, cv.width, cv.height);
  const base = sheets[skin.baseFile];
  if (base) cx.drawImage(base, 0, 0, skin.baseWidth, skin.baseHeight);
  if (!state) return;

  for (const p of skin.parts) {
    const d = p.dst;
    if (p.key === 'stick.left' || p.key === 'stick.right') {
      const left = p.key === 'stick.left';
      const [ox, oy] = stickOffset(left ? state.lx : state.rx, left ? state.ly : state.ry, skin.stickRadius);
      blit(p, d.x + ox, d.y + oy, 1, state.btn.includes(left ? 'L3' : 'R3'));
    } else if (p.key === 'btn.l2' || p.key === 'btn.r2') {
      // Gatillos: la opacidad sigue el recorrido, para ver CUANTO se aprieta.
      const a = fill01(p.key === 'btn.l2' ? state.l2 : state.r2);
      if (a > 0.01) blit(p, d.x, d.y, a, false);
    } else {
      const b = BUTTON_OF[p.key];
      if (b && state.btn.includes(b)) blit(p, d.x, d.y, 1, false);
    }
  }
}

fetch('skin.json').then(r => r.json()).then(async s => {
  skin = s;
  cv.width = s.baseWidth;
  cv.height = s.baseHeight;
  const files = new Set([s.baseFile, ...s.parts.map(p => p.file)]);
  await Promise.all([...files].map(f => new Promise(done => {
    const im = new Image();
    im.onload = () => { sheets[f] = im; done(); };
    im.onerror = () => done();   // una hoja que falte deja SU capa sin dibujar, nada mas
    im.src = 'skin/' + encodeURIComponent(f);
  })));
  msg.textContent = '';
  const es = new EventSource('state');
  es.onmessage = e => { state = JSON.parse(e.data); msg.textContent = ''; };
  es.onerror = () => { msg.textContent = 'UltraPolling desconectado'; };
  requestAnimationFrame(draw);
}).catch(() => { msg.textContent = 'No se pudo leer el skin'; });
</script>
</html>
""";
    }
}
```

- [ ] **Step 2: Verificación de compilación** — `dotnet build HidusbfModernGui\HidusbfModernGui.csproj -c Release`, esperado 0/0. (Este archivo no se linkea al proyecto de tests: `HttpListener` necesita una escucha real, y la verificación de verdad es la de la Task 8 con OBS delante.)

- [ ] **Step 3: Commit**

```bash
git add HidusbfModernGui/StreamerServer.cs
git commit -m "feat: StreamerServer - el mando en vivo como pagina local para OBS"
```

---

### Task 8: El enlace de OBS en la tuerca, y el feed que lo alimenta

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml` (el `StackPanel` de la tuerca, Task 5)
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (`VisualizerTick`, `UpdateVisualizerRunState`, `Window_Closing`, handlers nuevos)

**Interfaces:**
- Consumes: `StreamerServer` (Task 7), `UiPrefsStore` (Task 3), `PadSkinLoader.Load/FindFirstSkinDir/DefaultSkinsRoot`.

**El fallo que hay que evitar aquí es el mismo que ya mordió con el overlay:** el visualizador solo corre si alguien lo mira. Si el servidor publica pero nadie tiene abierta la página del configurador, `VisualizerTick` se para y OBS ve un mando congelado. `UpdateVisualizerRunState` tiene que contar también al servidor.

- [ ] **Step 1: El bloque de OBS en la columna.** Al final del `StackPanel` de la tuerca (Task 5), dentro del mismo `Border`:

```xml
<Border Height="1" Background="{StaticResource BorderBrush}" Margin="0,16,0,0"/>

<TextBlock Text="ENLACE PARA OBS" Style="{StaticResource SectionHeading}" Margin="0,16,0,0"/>
<!-- Se queda: dice donde va este enlace y hasta donde llega, dos cosas que el boton no
     puede mostrar. -->
<TextBlock Style="{StaticResource FieldLabel}" TextWrapping="Wrap" Margin="0,6,0,0"
           Text="En OBS: Fuentes -> + -> Navegador -> pega la URL. Solo escucha en este equipo; nadie de la red la ve."/>
<Button x:Name="ObsServerToggle" Content="PUBLICAR ENLACE" Style="{StaticResource InstrumentButton}"
        HorizontalAlignment="Stretch" Margin="0,10,0,0" Click="ObsServerToggle_Click"/>
<TextBox x:Name="ObsUrlBox" IsReadOnly="True" Visibility="Collapsed" Margin="0,10,0,0"
         Background="{StaticResource SurfaceAltBrush}" Foreground="{StaticResource TextDataBrush}"
         BorderBrush="{StaticResource BorderBrush}" BorderThickness="1" Padding="6,4"
         FontFamily="{StaticResource MonoFont}"/>
<Button x:Name="ObsCopyBtn" Content="COPIAR" Style="{StaticResource SecondaryButton}"
        HorizontalAlignment="Stretch" Margin="0,8,0,0" Visibility="Collapsed" Click="ObsCopy_Click"/>
<TextBlock x:Name="ObsSizeHint" Style="{StaticResource FieldLabel}" Visibility="Collapsed"
           TextWrapping="Wrap" Margin="0,8,0,0"/>
```

- [ ] **Step 2: El campo y los handlers.** En `MainWindow.xaml.cs`, junto a `_streamerWindow`:

```csharp
// Servidor del enlace de OBS. Independiente de la ventana overlay: se puede querer la
// fuente de navegador sin tener el overlay encima del escritorio, y al reves.
private readonly StreamerServer _obsServer = new();

private void ObsServerToggle_Click(object sender, RoutedEventArgs e)
{
    if (_obsServer.IsRunning)
    {
        _obsServer.Stop();
        ObsServerToggle.Content = "PUBLICAR ENLACE";
        ObsUrlBox.Visibility = Visibility.Collapsed;
        ObsCopyBtn.Visibility = Visibility.Collapsed;
        ObsSizeHint.Visibility = Visibility.Collapsed;
        LogStatus("Enlace de OBS cerrado.");
        UpdateVisualizerRunState();
        return;
    }

    var dir = PadSkinLoader.FindFirstSkinDir(PadSkinLoader.DefaultSkinsRoot);
    var (skin, error) = dir == null ? (null, "No hay ningun skin instalado.") : PadSkinLoader.Load(dir);
    if (skin == null)
    {
        LogStatus($"No se pudo publicar el enlace: {error}");
        return;
    }

    var result = _obsServer.Start(skin, UiPrefsStore.Load().ObsPort);
    if (!result.Success) { LogStatus(result.Error!); return; }

    ObsServerToggle.Content = "CERRAR ENLACE";
    ObsUrlBox.Text = _obsServer.Url;
    ObsUrlBox.Visibility = Visibility.Visible;
    ObsCopyBtn.Visibility = Visibility.Visible;
    // El tamano exacto de la fuente: con otro, OBS reescala y el mando sale borroso.
    ObsSizeHint.Text = $"En la fuente de OBS pon Ancho {_obsServer.Width:0} y Alto {_obsServer.Height:0}.";
    ObsSizeHint.Visibility = Visibility.Visible;
    LogStatus($"Enlace de OBS publicado en {_obsServer.Url}");
    UpdateVisualizerRunState();
}

private void ObsCopy_Click(object sender, RoutedEventArgs e)
{
    if (string.IsNullOrEmpty(ObsUrlBox.Text)) return;
    try { Clipboard.SetText(ObsUrlBox.Text); LogStatus("Enlace copiado."); }
    catch (Exception ex) { LogStatus($"No se pudo copiar: {ex.Message}"); }
}
```

- [ ] **Step 3: Alimentar el servidor.** En `VisualizerTick`, después de la línea `_streamerWindow?.Pad.Update(outState);`:

```csharp
_obsServer.Push(outState);   // lo mismo que ve el juego, a la fuente de OBS
```

- [ ] **Step 4: El feed no se puede parar con el servidor encendido.** En `UpdateVisualizerRunState`, cambiar la condición:

```csharp
// El servidor cuenta como espectador: si no, con el configurador cerrado el tick se
// para y OBS se queda con un mando congelado, sin ningun aviso de que paso.
if (ConfigPanel.IsVisible || _streamerWindow != null || _obsServer.IsRunning) StartVisualizer();
```

- [ ] **Step 5: Cerrar el servidor al cerrar la app.** El cierre se maneja en `protected override void OnClosing(...)` (`MainWindow.xaml.cs:74`), que ya hace `StopVisualizer(); _streamerWindow?.Close();`. Añadir justo después de esas dos líneas:

```csharp
_obsServer.Dispose();   // suelta el puerto: si no, reabrir la app lo encontraria ocupado
```

- [ ] **Step 6: Verificación** — build 0/0 y suite completa PASS. Manual, con el mando conectado:
  1. Abrir la tuerca del mando en vivo → **PUBLICAR ENLACE**. La barra de estado da una URL `http://127.0.0.1:8787/`.
  2. Pegar esa URL en un navegador: debe verse el mando sobre fondo transparente (a cuadros o del color de la página) y **moverse con el mando físico**, sticks incluidos, con los gatillos apareciendo gradualmente.
  3. **Irse a la pestaña Dispositivos** y comprobar que el navegador **sigue moviéndose**: eso es lo que verifica el Step 4.
  4. En OBS: Fuentes → + → Navegador → URL, con el Ancho y Alto que dice la app. El mando debe salir con fondo transparente.
  5. Cerrar el enlace: el navegador muestra "UltraPolling desconectado".
  6. Con el mando virtual **activo**, comprobar que lo que se ve en OBS es la salida **transformada** (por ejemplo, con L2 remapeado a Triángulo, apretar L2 enciende el triángulo en OBS).
  7. Pedir a mano `http://127.0.0.1:8787/skin/../../../windows/win.ini` y comprobar que responde **400**, no un archivo.
  8. Cerrar la app y volver a abrirla: publicar otra vez debe dar el **mismo puerto** (señal de que se soltó al cerrar).

- [ ] **Step 7: Commit**

```bash
git add -u && git commit -m "feat(ui): el modo streamer publica un enlace para la fuente de navegador de OBS"
```

---

### Task 9: Documentación y verificación integral

**Files:**
- Modify: `README.md`, `docs/DOCUMENTACION.md`

- [ ] **Step 1: README.** Añadir bajo la sección del modo streamer:

```markdown
### Enlace para OBS

El modo streamer publica ademas una pagina local que puedes meter en OBS como
**Fuente de navegador**: MANDO → CONFIGURAR EL MANDO → la tuerca del mando en vivo →
**PUBLICAR ENLACE**. Copia la URL y pegala en OBS con el ancho y el alto que indica la
app; el fondo es transparente.

La pagina dibuja **la salida transformada**, es decir, lo que recibe el juego con tus
ajustes aplicados. Necesita un skin instalado (es lo que dibuja) y escucha solo en
`127.0.0.1`: nadie de tu red puede verla. El puerto se guarda en
`%APPDATA%\UltraPolling\ui.json` por si el 8787 te estorba.

### Modo dia

Ajustes → APARIENCIA → **Modo dia** cambia toda la interfaz a fondo claro al instante, y
se recuerda al cerrar. El diagrama de asignacion de botones se queda claro en los dos
modos: su dibujo es tinta oscura sobre papel blanco.
```

- [ ] **Step 2: DOCUMENTACION.md.** Añadir al mapa de módulos:

```markdown
- **`ThemePalette.cs`** — los colores de los modos dia y noche, como datos puros.
- **`ThemeManager.cs`** — aplica una paleta **mutando el Color de los pinceles vivos**.
  Los ~400 `StaticResource` guardan la instancia del pincel, asi que cambiarle el color
  los repinta a todos sin recargar ventanas. Un pincel con `Freeze` romperia esto en
  silencio: `Apply` los devuelve para que se note.
- **`UiPrefs.cs`** — `ui.json`: tema y puerto de OBS. Separado de `active.json` porque
  borrar la intencion de luz no puede llevarse por delante la apariencia.
- **`PadWebModel.cs`** — el skin y el estado en JSON para el navegador, mas la guarda de
  nombres de archivo que impide salir de la carpeta del skin.
- **`StreamerServer.cs`** — `HttpListener` en `127.0.0.1` con la pagina, los sprites y el
  flujo SSE. La pagina dibuja en canvas con `drawImage(hoja, sx,sy,sw,sh, dx,dy,dw,dh)`,
  que es exactamente `(Src, Dst)` del skin: una sola geometria para WPF y para el navegador.
```

Y a la lista de lecciones:

```markdown
- **L7 — Un contenido que no hereda `Foreground` no puede sobrevivir a una inversion de
  fondo.** El hover de la etiqueta del diagrama invertia el fondo a negro contando con que
  el texto se aclarase; cuando el texto paso a ser formas con su pincel fijado en
  code-behind, la etiqueta se volvio un borron negro. Si el contenido no es texto, el
  hover se lava, no se invierte.
- **L8 — El visualizador solo corre si alguien lo mira, y "alguien" incluye a los que no
  se ven.** Paso con el overlay del streamer y vuelve a pasar con el servidor de OBS:
  cada consumidor nuevo del feed tiene que entrar en `UpdateVisualizerRunState`, o su
  vista se congela sin decir nada.
```

- [ ] **Step 3: Verificación integral.** Con todo aplicado:
  - `dotnet build HidusbfModernGui\HidusbfModernGui.csproj -c Release` → 0/0.
  - `dotnet test HidusbfModernGui.Tests\HidusbfModernGui.Tests.csproj` → `Failed: 0`.
  - Modo día **y** modo noche, cada uno con: las tres pestañas, las tres secciones del mando, las cuatro sub-páginas del configurador, el popup de la tuerca y el picker de destino de una etiqueta. Buscando una sola cosa: texto o icono que se haya quedado ilegible.
  - El diagrama de botones, en los dos modos: panel claro con tinta negra y hover que no traga el icono.
  - El enlace de OBS funcionando mientras se cambia de tema (el servidor no depende de la paleta, pero conviene comprobar que cambiar de tema no lo tumba).

- [ ] **Step 4: Commit**

```bash
git add -u && git commit -m "docs: modo dia/noche y el enlace de OBS en README y DOCUMENTACION"
```

---

## Self-review

- **Cobertura del pedido:** (1) hover que oscurece y traga el icono → Task 1, con la causa localizada (las formas no heredan `Foreground`); (2) modo día y noche → Tasks 2, 3 y 4, con el diagrama sacado de la paleta en la 4 porque su imagen es de polaridad fija; (3) la tuerca en columna vertical → Task 5; (4) enlace de OBS para el modo streamer → Tasks 6, 7 y 8. ✓
- **Placeholders:** ninguno. La página del navegador va entera, los colores del modo día van con su valor y su motivo, y la guarda de rutas tiene sus casos de prueba escritos. ✓
- **Tipos consistentes:** `AppTheme` (Task 2) lo consumen `UiPrefs` (3) y `ThemeManager` (4); `ThemePalette.Keys` es lo que `ThemeManager.Apply` recorre; `UiPrefsStore.DefaultObsPort` (3) lo consume `StreamerServer.Start` a través de `MainWindow` (8); `PadWebModel.SkinJson/StateJson/IsSafeFileName` (6) los consume `StreamerServer` (7). Las claves de capa del JavaScript (`btn.cross`, `dpad.up`, `stick.left`, `btn.l2`…) son literalmente las de `PadSkin.RequiredPartKeys`. ✓
- **Riesgo cubierto — el pincel congelado:** `ThemeManager.Apply` devuelve las claves que no pudo aplicar y la UI lo dice en la barra de estado. Sin eso, un `Freeze` añadido en el futuro dejaría medio tema sin cambiar y nadie sabría por qué. ✓
- **Riesgo cubierto — el mando congelado en OBS:** Task 8 Step 4 mete el servidor en `UpdateVisualizerRunState` y el Step 6.3 lo verifica a propósito, porque es exactamente el fallo que ya apareció con el overlay. ✓
- **Riesgo cubierto — salir de la carpeta del skin:** `IsSafeFileName` con sus ocho casos de rechazo (Task 6) y la comprobación manual del 400 (Task 8 Step 6.7). ✓
- **Sin colores nuevos sueltos:** los once de cada modo viven en `ThemePalette`; los tres fijos del diagrama (`DiagramPaperBrush`, `DiagramInkBrush`, `DiagramPillHoverBrush`) están declarados con su motivo escrito al lado. ✓
- **Alcance:** no se toca el motor, ni `RemapSettings`, ni la ruta de la luz. El servidor solo **lee** el estado que el visualizador ya calcula. ✓
