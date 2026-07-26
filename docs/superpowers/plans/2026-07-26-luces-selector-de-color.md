# Luces: fuera el selector de mando, y un selector de color de verdad — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dos cosas en la página LUCES: (1) **quitar el desplegable MANDO** — la app resuelve el mando sola y está pensada para uno conectado; (2) **rehacer el selector de color** con hex editable, modo HSB con sus tres valores numéricos, y una paleta propia a la que el usuario añade colores.

**Architecture:** El resolutor del mando ya existe y ya se usa dos veces (`HidHideControl.FindPhysicalGamepadInstanceId()`, el camino en-proceso que sobrevive a HidHide). Esta vez pasa a ser el **único**: un campo `_lightPadId` que se refresca con el escaneo y con el replug, y las cuatro llamadas que hoy leen `PlayStationList.SelectedItem` lo leen a él. El selector de color se parte en dos: **aritmética pura y testeable** (`ColourMath` ya tiene HSV↔RGB; se le añade el parseo/formateo de hex) y **presentación** (`ColourPicker` gana barras y campos, y `PalettePreset` guarda los colores del usuario en disco).

**Tech Stack:** .NET 9 WPF, xUnit. Sin dependencias nuevas.

## Contexto verificado (lo que ya existe)

- `ColourPicker` (`ColourPicker.xaml`, 58 líneas + 149 de code-behind) es hoy un cuadrado saturación/valor y una tira de tono. Expone `SelectedColor` (DependencyProperty) y `ColorChanged`. Mantiene `_h/_s/_v` internos y un guard `_internal` contra la reentrada.
- `ColourMath` ya existe y está linkeado en el proyecto de tests.
- El desplegable `PlayStationList` se lee en **cuatro** sitios de `MainWindow.xaml.cs`: `ApplyLightNow()` (2387), `Effect_Tick()` (2578), `ApplyGameProfile()` (2814) y su propio `SelectionChanged` (2508). Se rellena en `RefreshPlayStationDevices()` (2498-2499), que además **inyecta una entrada sintética** cuando HidHide oculta el mando y el escaneo no lo ve.
- `LightEmptyState` / `LightPanel` ya alternan visibilidad según haya o no mando.
- Los presets de color se construyen en code-behind (`MainWindow.xaml.cs` ~2160-2185) como ocho `Button` con estilo `PresetSwatchButton` y `Tag = byte[]{r,g,b}`.

## Global Constraints

- UI en **español**, tema monocromo. El selector de color es la **única** excepción declarada: ahí el color **es el dato**, no un adorno.
- Comentarios de código **en español y sin tildes**.
- **Aplicación en vivo**: mover cualquier control escribe la luz al momento, con el mismo antirrebote que hoy (`_lightDebounce`, 50 ms). Ningún botón "Aplicar".
- El proyecto de tests linkea fuentes individualmente; `PalettePreset.cs` va al csproj. Nada que toque WPF se puede linkear.
- **Sin cuentagotas de pantalla.** Ver la nota al final: leer píxeles de la pantalla del usuario es una capacidad de captura, y no entra sin que él la pida.
- Commits **sin** Co-Authored-By. El push lo hace el usuario.

## Estructura de archivos

| Archivo | Responsabilidad |
|---|---|
| `HidusbfModernGui/ColourMath.cs` | + `TryParseHex` / `ToHex`. Aritmética pura. |
| `HidusbfModernGui/PalettePreset.cs` (nuevo) | Los colores que el usuario guarda, en `palette.json`. Sin WPF. |
| `HidusbfModernGui/ColourPicker.xaml(.cs)` | Barras H/S/B con lectura numérica, campo hex, vista previa. |
| `HidusbfModernGui/MainWindow.xaml(.cs)` | Fuera el desplegable; `_lightPadId`; fila de paleta con "+". |

---

### Task 1: Hex a color y color a hex (TDD)

**Files:**
- Modify: `HidusbfModernGui/ColourMath.cs`
- Test: `HidusbfModernGui.Tests/ColourMathTests.cs`

**Interfaces:**
- Produces: `ColourMath.ToHex(byte r, byte g, byte b) -> string` (siempre `"RRGGBB"`, mayúsculas, **sin** almohadilla); `ColourMath.TryParseHex(string? text, out byte r, out byte g, out byte b) -> bool` (acepta con y sin `#`, 3 o 6 dígitos, espacios alrededor, mayúsculas o minúsculas). Consumido por `ColourPicker` (Task 3).

**Por qué sin almohadilla al formatear:** el campo es editable y el usuario va a escribir encima. Una almohadilla que él no puso y que no puede borrar sin romper el valor es un estorbo; al leer se acepta igual.

- [ ] **Step 1: Tests que fallan** — añadir a `ColourMathTests.cs`:

```csharp
[Fact]
public void ToHex_IsSixUppercaseDigitsWithoutHash()
{
    Assert.Equal("FF0000", ColourMath.ToHex(255, 0, 0));
    Assert.Equal("0A0B0C", ColourMath.ToHex(10, 11, 12));
    Assert.Equal("000000", ColourMath.ToHex(0, 0, 0));
}

[Theory]
[InlineData("F83E64", 248, 62, 100)]
[InlineData("#F83E64", 248, 62, 100)]
[InlineData("  f83e64  ", 248, 62, 100)]
[InlineData("#FFF", 255, 255, 255)]
[InlineData("0f0", 0, 255, 0)]
public void TryParseHex_AcceptsTheFormsAUserActuallyTypes(string text, byte r, byte g, byte b)
{
    Assert.True(ColourMath.TryParseHex(text, out byte pr, out byte pg, out byte pb));
    Assert.Equal(r, pr);
    Assert.Equal(g, pg);
    Assert.Equal(b, pb);
}

[Theory]
[InlineData(null)]
[InlineData("")]
[InlineData("   ")]
[InlineData("GGGGGG")]
[InlineData("12345")]
[InlineData("1234567")]
[InlineData("#12")]
public void TryParseHex_RejectsWhatIsNotAColour(string? text)
    => Assert.False(ColourMath.TryParseHex(text, out _, out _, out _));

// Ida y vuelta: lo que se formatea se vuelve a leer igual. Sin esto, el campo podria
// mostrar un valor que el mismo no acepta al reescribirlo.
[Fact]
public void ToHex_RoundTripsThroughTryParseHex()
{
    foreach (var (r, g, b) in new[] { ((byte)0, (byte)0, (byte)0), ((byte)255, (byte)255, (byte)255),
                                      ((byte)248, (byte)62, (byte)100), ((byte)1, (byte)128, (byte)254) })
    {
        Assert.True(ColourMath.TryParseHex(ColourMath.ToHex(r, g, b), out byte pr, out byte pg, out byte pb));
        Assert.Equal(r, pr); Assert.Equal(g, pg); Assert.Equal(b, pb);
    }
}
```

- [ ] **Step 2: Verificar que fallan** — `dotnet test HidusbfModernGui.Tests\HidusbfModernGui.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ColourMathTests"`. Esperado: error de compilación.

- [ ] **Step 3: Implementación** — añadir a `ColourMath.cs`:

```csharp
// Hex SIN almohadilla al escribir: el campo es editable y el usuario teclea encima. Una
// almohadilla que el no puso y que no puede borrar sin invalidar el valor estorba. Al leer
// se acepta igual, con o sin ella, en 3 o 6 digitos, con espacios y en cualquier caja.
public static string ToHex(byte r, byte g, byte b) => $"{r:X2}{g:X2}{b:X2}";

public static bool TryParseHex(string? text, out byte r, out byte g, out byte b)
{
    r = g = b = 0;
    if (string.IsNullOrWhiteSpace(text)) return false;

    string s = text.Trim().TrimStart('#');
    if (s.Length == 3)
    {
        // La forma corta duplica cada digito: "0f0" es "00ff00", no "0f0000".
        s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
    }
    if (s.Length != 6) return false;

    if (!int.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                      System.Globalization.CultureInfo.InvariantCulture, out int v)) return false;

    r = (byte)((v >> 16) & 0xFF);
    g = (byte)((v >> 8) & 0xFF);
    b = (byte)(v & 0xFF);
    return true;
}
```

- [ ] **Step 4: Verificar que pasan** — mismo filtro PASS, después la suite entera PASS (`Passed: 393`; esta tarea añade 16 casos: 1 + 5 + 7 + 1, con las dos `[Theory]` contando por caso).

- [ ] **Step 5: Commit**

```bash
git add -u && git commit -m "feat: ColourMath sabe leer y escribir hex (TDD)"
```

---

### Task 2: `PalettePreset` — los colores que el usuario guarda (TDD)

**Files:**
- Create: `HidusbfModernGui/PalettePreset.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj`
- Test: `HidusbfModernGui.Tests/PalettePresetTests.cs`

**Interfaces:**
- Produces: `PaletteStore.Load() -> List<string>` (hex sin almohadilla), `PaletteStore.Save(IEnumerable<string>) -> OpResult`, `PaletteStore.Add(List<string> current, string hex) -> bool` (pura: normaliza, rechaza repetidos y recorta a `MaxColours`, devuelve si añadió), `PaletteStore.MaxColours` (12), `PaletteStore.Path`, `PaletteStore.OverrideDirectoryForTests(string?)`. Consumido por `MainWindow` (Task 5).

**Por qué un tope de 12:** la fila es horizontal y vive dentro de una tarjeta. Sin tope, el número 30 empuja la tarjeta fuera de la ventana. Al llegar al tope entra el nuevo y sale el más viejo, que es lo que espera cualquiera que use una paleta como historial.

- [ ] **Step 1: Link en el csproj**:

```xml
<Compile Include="..\HidusbfModernGui\PalettePreset.cs" Link="PalettePreset.cs" />
```

- [ ] **Step 2: Tests que fallan** — crear `HidusbfModernGui.Tests/PalettePresetTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HidusbfModernGui;
using Xunit;

public class PalettePresetTests : IDisposable
{
    private readonly string _dir;

    public PalettePresetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "UltraPollingPal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        PaletteStore.OverrideDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        PaletteStore.OverrideDirectoryForTests(null);
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Load_WithoutFile_ReturnsEmpty() => Assert.Empty(PaletteStore.Load());

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        Assert.True(PaletteStore.Save(new[] { "FF0000", "00FF00" }).Success);
        Assert.Equal(new[] { "FF0000", "00FF00" }, PaletteStore.Load());
    }

    [Fact]
    public void Add_NormalisesToSixUppercaseDigits()
    {
        var list = new List<string>();
        Assert.True(PaletteStore.Add(list, "#f83e64"));
        Assert.Equal("F83E64", Assert.Single(list));
    }

    // Guardar dos veces el mismo color llenaria la paleta de duplicados que el usuario no
    // puede distinguir de un vistazo.
    [Fact]
    public void Add_RejectsAColourAlreadyInThePalette()
    {
        var list = new List<string> { "F83E64" };
        Assert.False(PaletteStore.Add(list, "f83e64"));
        Assert.Single(list);
    }

    [Fact]
    public void Add_RejectsWhatIsNotAColour()
    {
        var list = new List<string>();
        Assert.False(PaletteStore.Add(list, "no soy un color"));
        Assert.Empty(list);
    }

    // Al llegar al tope entra el nuevo y sale el mas viejo: una paleta es un historial.
    [Fact]
    public void Add_AtTheCap_DropsTheOldest()
    {
        var list = new List<string>();
        for (int i = 0; i < PaletteStore.MaxColours; i++)
            Assert.True(PaletteStore.Add(list, $"{i:X2}0000"));

        Assert.Equal(PaletteStore.MaxColours, list.Count);
        string oldest = list[0];

        Assert.True(PaletteStore.Add(list, "ABCDEF"));
        Assert.Equal(PaletteStore.MaxColours, list.Count);
        Assert.DoesNotContain(oldest, list);
        Assert.Equal("ABCDEF", list[^1]);
    }

    [Fact]
    public void Load_WithCorruptFile_ReturnsEmptyInsteadOfThrowing()
    {
        File.WriteAllText(PaletteStore.Path, "{ esto no es json");
        Assert.Empty(PaletteStore.Load());
    }

    // Un archivo editado a mano puede traer basura mezclada con colores validos. Se queda
    // lo que sea un color y se tira el resto, en vez de perder la paleta entera.
    [Fact]
    public void Load_KeepsOnlyTheEntriesThatAreColours()
    {
        File.WriteAllText(PaletteStore.Path, "[\"FF0000\",\"zzz\",\"#00FF00\",\"\"]");
        Assert.Equal(new[] { "FF0000", "00FF00" }, PaletteStore.Load());
    }
}
```

- [ ] **Step 3: Verificar que fallan** (compilación).

- [ ] **Step 4: Implementación** — crear `HidusbfModernGui/PalettePreset.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HidusbfModernGui
{
    // Los colores que el usuario guarda con el "+", en %APPDATA%\UltraPolling\palette.json.
    // Se guardan como hex de 6 digitos y no como objetos {R,G,B}: el archivo es legible y
    // editable a mano, que es media gracia de tenerlo en JSON.
    public static class PaletteStore
    {
        // Tope de 12. La fila es horizontal y vive dentro de una tarjeta: sin tope, el color
        // numero 30 empuja la tarjeta fuera de la ventana.
        public const int MaxColours = 12;

        private static string? _overrideDir;

        internal static void OverrideDirectoryForTests(string? dir) => _overrideDir = dir;

        private static string Directory_ => _overrideDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraPolling");

        public static string Path => System.IO.Path.Combine(Directory_, "palette.json");

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static List<string> Load()
        {
            try
            {
                if (!File.Exists(Path)) return new List<string>();
                string json = File.ReadAllText(Path);
                if (string.IsNullOrWhiteSpace(json)) return new List<string>();

                var raw = JsonSerializer.Deserialize<List<string>>(json, Options) ?? new List<string>();
                // Un archivo editado a mano puede traer basura mezclada. Se queda lo que sea
                // un color y se tira el resto, en vez de perder la paleta entera por una linea.
                return raw
                    .Where(s => ColourMath.TryParseHex(s, out _, out _, out _))
                    .Select(s => { ColourMath.TryParseHex(s, out byte r, out byte g, out byte b); return ColourMath.ToHex(r, g, b); })
                    .Take(MaxColours)
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PaletteStore.Load fallo, paleta vacia: {ex.Message}");
                return new List<string>();
            }
        }

        public static OpResult Save(IEnumerable<string> colours)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory_);
                if (File.Exists(Path)) File.Copy(Path, Path + ".backup", true);
                File.WriteAllText(Path, JsonSerializer.Serialize(colours, Options));
                return OpResult.Ok();
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"No se pudo guardar la paleta: {ex.Message}");
            }
        }

        // Pura, para poder probarla: normaliza, rechaza lo que no sea color y lo que ya este,
        // y al llegar al tope tira el mas viejo. Devuelve si el color entro.
        public static bool Add(List<string> current, string? hex)
        {
            if (current == null) return false;
            if (!ColourMath.TryParseHex(hex, out byte r, out byte g, out byte b)) return false;

            string norm = ColourMath.ToHex(r, g, b);
            if (current.Contains(norm, StringComparer.OrdinalIgnoreCase)) return false;

            current.Add(norm);
            while (current.Count > MaxColours) current.RemoveAt(0);
            return true;
        }
    }
}
```

- [ ] **Step 5: Verificar que pasan** + suite entera (`Passed: 401`).

- [ ] **Step 6: Commit**

```bash
git add HidusbfModernGui/PalettePreset.cs HidusbfModernGui.Tests/PalettePresetTests.cs HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj
git commit -m "feat: PaletteStore - los colores que el usuario guarda (TDD)"
```

---

### Task 3: `ColourPicker` con barras, valores y hex editable

**Files:**
- Modify: `HidusbfModernGui/ColourPicker.xaml(.cs)`

**Interfaces:**
- Consumes: `ColourMath.ToHex/TryParseHex` (Task 1) y el `ColourMath` HSV↔RGB que ya existe.
- Produces: `ColourPicker` mantiene `SelectedColor` y `ColorChanged` **con la misma firma** — `MainWindow` no cambia por esta tarea.

**La forma, de arriba abajo** (imagen de referencia del usuario):

1. **Vista previa**: banda del color actual, alto 64, ancho completo.
2. **Fila de hex**: `TextBox` con el hex (sin almohadilla, mono) y a la derecha la etiqueta `HSB` y un botón de copiar.
3. **Tres barras**, cada una con su número a la derecha:
   - **Tono** 0-360, fondo el arcoíris de siempre.
   - **Saturación** 0-100, fondo de blanco al tono puro.
   - **Brillo** 0-100, fondo de negro al tono puro.
4. **Sin barra de alfa.** La de la referencia es la cuarta; aquí no existe porque **la barra de luz del mando no tiene transparencia**. Una barra que no puede hacer nada es peor que no tenerla.

**El brillo de aquí no es el BRILLO del LED.** Esta barra es la V de HSB, o sea cuánto se acerca el color al negro; el desplegable BRILLO de la página es `LedBrightness` (Alto/Medio/Bajo), una propiedad del hardware. Son dos cosas distintas y deben poder verse a la vez, así que esta barra se rotula **"Brillo del color"**.

- [ ] **Step 1: El XAML.** Sustituir el contenido de `ColourPicker.xaml` por:

```xml
<UserControl x:Class="HidusbfModernGui.ColourPicker"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <UserControl.Resources>
        <!-- Una barra = fondo degradado + un pulsador redondo que se arrastra. Las tres
             comparten plantilla; lo unico que cambia es el degradado de detras. -->
        <Style x:Key="ColourSlider" TargetType="Slider">
            <Setter Property="Height" Value="22"/>
            <Setter Property="IsMoveToPointEnabled" Value="True"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Slider">
                        <Grid>
                            <Border x:Name="Track" CornerRadius="11" Background="{TemplateBinding Background}"
                                    BorderBrush="{StaticResource BorderBrush}" BorderThickness="1"/>
                            <Track x:Name="PART_Track">
                                <Track.Thumb>
                                    <Thumb>
                                        <Thumb.Template>
                                            <ControlTemplate TargetType="Thumb">
                                                <!-- Aro blanco con reborde oscuro: tiene que verse tanto sobre
                                                     el amarillo como sobre el negro de las propias barras. -->
                                                <Grid Width="18" Height="18">
                                                    <Ellipse Stroke="{StaticResource BgBrush}" StrokeThickness="3"/>
                                                    <Ellipse Stroke="{StaticResource TextDataBrush}" StrokeThickness="2"/>
                                                </Grid>
                                            </ControlTemplate>
                                        </Thumb.Template>
                                    </Thumb>
                                </Track.Thumb>
                            </Track>
                        </Grid>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key="ColourValue" TargetType="TextBlock" BasedOn="{StaticResource DataText}">
            <Setter Property="Width" Value="38"/>
            <Setter Property="TextAlignment" Value="Right"/>
            <Setter Property="VerticalAlignment" Value="Center"/>
            <Setter Property="Margin" Value="12,0,0,0"/>
        </Style>
    </UserControl.Resources>

    <StackPanel Width="300">
        <!-- Vista previa. El unico sitio de la app donde el color ES el dato. -->
        <Border x:Name="PreviewBand" Height="64" CornerRadius="8"
                BorderBrush="{StaticResource BorderBrush}" BorderThickness="1"/>

        <Grid Margin="0,12,0,0">
            <TextBox x:Name="HexBox" Width="110" HorizontalAlignment="Left"
                     Background="{StaticResource SurfaceAltBrush}" Foreground="{StaticResource TextDataBrush}"
                     BorderBrush="{StaticResource BorderBrush}" BorderThickness="1" Padding="8,5"
                     FontFamily="{StaticResource MonoFont}" CharacterCasing="Upper" MaxLength="7"
                     AutomationProperties.Name="Color en hexadecimal"
                     KeyDown="Hex_KeyDown" LostFocus="Hex_LostFocus"/>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Center">
                <TextBlock Text="HSB" Style="{StaticResource FieldLabel}" VerticalAlignment="Center"/>
                <Button x:Name="CopyHexBtn" Style="{StaticResource RoundIconButton}" Margin="12,0,0,0"
                        Click="CopyHex_Click" ToolTip="Copiar el hex al portapapeles"
                        AutomationProperties.Name="Copiar el color">
                    <Path Data="{StaticResource CopyIconPath}" Fill="{StaticResource TextDataBrush}"
                          Width="12" Height="12" Stretch="Uniform"/>
                </Button>
            </StackPanel>
        </Grid>

        <Grid Margin="0,14,0,0">
            <Slider x:Name="HueBar" Style="{StaticResource ColourSlider}" Minimum="0" Maximum="360"
                    Margin="0,0,50,0" AutomationProperties.Name="Tono"
                    ValueChanged="Bar_ValueChanged"/>
            <TextBlock x:Name="HueValue" Style="{StaticResource ColourValue}" HorizontalAlignment="Right"/>
        </Grid>

        <Grid Margin="0,10,0,0">
            <Slider x:Name="SatBar" Style="{StaticResource ColourSlider}" Minimum="0" Maximum="100"
                    Margin="0,0,50,0" AutomationProperties.Name="Saturacion"
                    ValueChanged="Bar_ValueChanged"/>
            <TextBlock x:Name="SatValue" Style="{StaticResource ColourValue}" HorizontalAlignment="Right"/>
        </Grid>

        <Grid Margin="0,10,0,0">
            <Slider x:Name="ValBar" Style="{StaticResource ColourSlider}" Minimum="0" Maximum="100"
                    Margin="0,0,50,0" AutomationProperties.Name="Brillo del color"
                    ValueChanged="Bar_ValueChanged"/>
            <TextBlock x:Name="ValValue" Style="{StaticResource ColourValue}" HorizontalAlignment="Right"/>
        </Grid>
    </StackPanel>
</UserControl>
```

- [ ] **Step 2: El icono de copiar.** En `Theme.xaml`, junto a los demás:

```xml
<Geometry x:Key="CopyIconPath">M19,21H8V7H19M19,5H8A2,2 0 0,0 6,7V21A2,2 0 0,0 8,23H19A2,2 0 0,0 21,21V7A2,2 0 0,0 19,5M16,1H4A2,2 0 0,0 2,3V17H4V3H16V1Z</Geometry>
```

- [ ] **Step 3: El code-behind.** Sustituir el cuerpo de `ColourPicker.xaml.cs` (se conservan `SelectedColor`, `ColorChanged`, `_h/_s/_v` y el guard `_internal`; se cambian los manejadores del ratón por los de las barras):

```csharp
// Redibuja barras, numeros, hex y vista previa a partir de _h/_s/_v. Bajo _internal para
// que mover una barra por codigo no se lea como una edicion del usuario.
private void Redraw()
{
    _internal = true;
    try
    {
        HueBar.Value = _h;
        SatBar.Value = _s * 100;
        ValBar.Value = _v * 100;

        HueValue.Text = ((int)Math.Round(_h)).ToString();
        SatValue.Text = ((int)Math.Round(_s * 100)).ToString();
        ValValue.Text = ((int)Math.Round(_v * 100)).ToString();

        var (r, g, b) = ColourMath.HsvToRgb(_h, _s, _v);
        PreviewBand.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        HexBox.Text = ColourMath.ToHex(r, g, b);

        // Los fondos de saturacion y brillo se recalculan con el tono: una barra de
        // saturacion que sigue mostrando el rojo mientras el color es azul miente sobre
        // lo que va a pasar al arrastrarla.
        var (pr, pg, pb) = ColourMath.HsvToRgb(_h, 1, 1);
        var puro = Color.FromRgb(pr, pg, pb);
        SatBar.Background = Horizontal(Colors.White, puro);
        ValBar.Background = Horizontal(Colors.Black, puro);
    }
    finally { _internal = false; }
}

private static LinearGradientBrush Horizontal(Color from, Color to) =>
    new(from, to, new Point(0, 0.5), new Point(1, 0.5));

private void Bar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
{
    if (_internal) return;
    _h = HueBar.Value;
    _s = SatBar.Value / 100.0;
    _v = ValBar.Value / 100.0;
    Redraw();
    Emit();
}

// El hex se aplica al pulsar Enter o al salir del campo, NO en cada tecla: aplicando por
// tecla, escribir "F83E64" mandaria al mando los seis colores intermedios.
private void Hex_KeyDown(object sender, KeyEventArgs e)
{
    if (e.Key != Key.Enter) return;
    CommitHex();
    e.Handled = true;
}

private void Hex_LostFocus(object sender, RoutedEventArgs e) => CommitHex();

private void CommitHex()
{
    if (!ColourMath.TryParseHex(HexBox.Text, out byte r, out byte g, out byte b))
    {
        // Texto invalido: se devuelve el campo al color que SI esta puesto, en vez de
        // dejarlo con algo que no corresponde a nada.
        Redraw();
        return;
    }
    SelectedColor = Color.FromRgb(r, g, b);   // la DependencyProperty recalcula _h/_s/_v y redibuja
    Emit();
}

private void CopyHex_Click(object sender, RoutedEventArgs e)
{
    try { Clipboard.SetText(HexBox.Text); } catch { /* el portapapeles lo puede tener otro proceso */ }
}
```

Y en `HueBar.Background`, fijado una sola vez en el constructor tras `InitializeComponent()`:

```csharp
// El arcoiris del tono no depende de nada, asi que se construye una vez y no en cada Redraw.
var arcoiris = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
foreach (var (offset, colour) in new (double, Color)[]
         {
             (0.000, Color.FromRgb(255, 0, 0)),   (0.167, Color.FromRgb(255, 255, 0)),
             (0.333, Color.FromRgb(0, 255, 0)),   (0.500, Color.FromRgb(0, 255, 255)),
             (0.667, Color.FromRgb(0, 0, 255)),   (0.833, Color.FromRgb(255, 0, 255)),
             (1.000, Color.FromRgb(255, 0, 0)),
         })
    arcoiris.GradientStops.Add(new GradientStop(colour, offset));
HueBar.Background = arcoiris;
```

- [ ] **Step 4: Verificación** — build 0/0, suite completa PASS (esta tarea no añade tests). Manual: arrastrar cada barra y ver que el número, la vista previa y el hex se mueven juntos; escribir `F83E64` + Enter y ver que las tres barras saltan a 348/75/97; escribir basura y ver que el campo vuelve al color puesto; comprobar que **la barra del mando cambia en vivo** al arrastrar.

- [ ] **Step 5: Commit**

```bash
git add -u && git commit -m "feat(ui): selector de color con barras HSB, valores y hex editable"
```

---

### Task 4: Fuera el desplegable de mando

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml` (bloque `MANDO`, líneas ~1341-1345)
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (`RefreshPlayStationDevices`, `ApplyLightNow`, `Effect_Tick`, `ApplyGameProfile`)

**Interfaces:**
- Produces: `MainWindow._lightPadId` (`string?`), el instance id del mando al que van las luces. Lo fija `RefreshPlayStationDevices()`.

**Por qué se puede quitar:** el resolutor en-proceso (`HidHideControl.FindPhysicalGamepadInstanceId()`) ya encuentra el mando **aunque HidHide lo esté ocultando**, que era el caso difícil; de hecho `RefreshPlayStationDevices()` ya lo llama para inyectar una entrada sintética en el desplegable. Con un solo mando, el desplegable era una lista de un elemento que el usuario tenía que confirmar.

- [ ] **Step 1: Quitar el bloque MANDO** del XAML — el `StackPanel Grid.Row="0"` con el `TextBlock` "MANDO" y el `ComboBox x:Name="PlayStationList"`. La fila 0 del Grid se queda vacía; **no** se borra la definición de fila (el resto del layout cuelga de los índices que hay).

- [ ] **Step 2: El campo y su resolución.** En `MainWindow.xaml.cs`, sustituir el final de `RefreshPlayStationDevices()`:

```csharp
// El mando de las luces se resuelve SOLO. Antes habia un desplegable, pero con un unico
// mando conectado era una lista de un elemento que el usuario tenia que confirmar.
//
// El orden importa: primero el escaneo (nombre bonito, y coincide con lo que se ve en
// Dispositivos) y si no, el resolutor en-proceso, que es el que sigue encontrando el mando
// cuando HidHide lo oculta y el escaneo por PowerShell ya no lo ve.
private string? _lightPadId;

// ... dentro de RefreshPlayStationDevices(), en lugar de rellenar el ComboBox:
_lightPadId = _allDevices.FirstOrDefault(DualSenseLight.IsPlayStation)?.InstanceId
              ?? HidHideControl.FindPhysicalGamepadInstanceId();

bool hayMando = _lightPadId != null;
LightEmptyState.Visibility = hayMando ? Visibility.Collapsed : Visibility.Visible;
LightPanel.Visibility = hayMando ? Visibility.Visible : Visibility.Collapsed;
UpdateSwatch();
```

- [ ] **Step 3: Los cuatro consumidores.** Sustituir en cada uno la lectura del desplegable por el campo:

  - `ApplyLightNow()`: `if (PlayStationList.SelectedItem is not UsbDeviceModel model) return;` → `if (_lightPadId == null) return;` y `DualSenseLight.Apply(_lightPadId, CurrentLight())`.
  - `Effect_Tick()`: igual, y `DualSenseLight.Apply(_lightPadId, new LightState(...))`.
  - `ApplyGameProfile()`: `if (PlayStationList.SelectedItem is UsbDeviceModel)` → `if (_lightPadId != null)`.
  - Borrar `PlayStationList_SelectionChanged` entero (su único trabajo era `UpdateSwatch()`, que ahora hace el Step 2).

- [ ] **Step 4: Verificación** — build 0/0, suite completa PASS. Manual, con el mando conectado: la página de luces abre **sin** desplegable y el color se aplica igual; **con el mando virtual encendido** (HidHide ocultando el físico) las luces siguen funcionando — este es el caso que el resolutor en-proceso existe para cubrir y el que hay que probar de verdad; desconectar el mando y ver el estado vacío.

- [ ] **Step 5: Commit**

```bash
git add -u && git commit -m "refactor(ui): las luces resuelven el mando solas, sin desplegable"
```

---

### Task 5: La paleta del usuario, con "+"

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml(.cs)` (la fila `PresetRow`)

**Interfaces:**
- Consumes: `PaletteStore.Load/Save/Add/MaxColours` (Task 2).

**Los ocho presets de fábrica se quedan.** Son el atajo para quien llega queriendo "azul", no un color que guardó. La paleta del usuario es una **segunda** fila, debajo, con su rótulo y su "+".

- [ ] **Step 1: La segunda fila** en `MainWindow.xaml`, justo debajo de `PresetRow`:

```xml
<TextBlock Text="MI PALETA" Style="{StaticResource FieldLabel}" Margin="0,4,0,8"/>
<StackPanel x:Name="PaletteRow" Orientation="Horizontal" Margin="0,0,0,16"/>
```

- [ ] **Step 2: Construirla en code-behind**, junto a donde se construyen los presets:

```csharp
private List<string> _palette = new();

// La paleta del usuario: sus colores guardados, mas el "+" que anade el actual. Se
// reconstruye entera en cada cambio - son 13 elementos como mucho y asi no hay dos caminos
// para dejarla desincronizada del archivo.
private void RefreshPalette()
{
    PaletteRow.Children.Clear();

    foreach (string hex in _palette)
    {
        if (!ColourMath.TryParseHex(hex, out byte r, out byte g, out byte b)) continue;

        var swatch = new Button
        {
            Style = (Style)FindResource("PresetSwatchButton"),
            Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
            Tag = new byte[] { r, g, b },
            ToolTip = $"#{hex}  (clic derecho para quitarlo)",
        };
        swatch.Click += Preset_Click;                 // el mismo handler que los de fabrica
        swatch.MouseRightButtonUp += PaletteSwatch_Remove;
        PaletteRow.Children.Add(swatch);
    }

    if (_palette.Count < PaletteStore.MaxColours)
    {
        var add = new Button
        {
            Style = (Style)FindResource("PresetSwatchButton"),
            Background = (Brush)FindResource("SurfaceAltBrush"),
            Content = "+",
            Foreground = (Brush)FindResource("TextDataBrush"),
            ToolTip = "Guardar el color actual en tu paleta",
        };
        add.Click += PaletteAdd_Click;
        PaletteRow.Children.Add(add);
    }
}

private void PaletteAdd_Click(object sender, RoutedEventArgs e)
{
    var c = Picker.SelectedColor;
    if (!PaletteStore.Add(_palette, ColourMath.ToHex(c.R, c.G, c.B)))
    {
        LogStatus("Ese color ya esta en tu paleta.");
        return;
    }
    var saved = PaletteStore.Save(_palette);
    if (!saved.Success) { LogStatus(saved.Error!); return; }
    RefreshPalette();
}

// Clic derecho para quitar: un aspa sobre cada muestra ensuciaria una fila cuyo contenido
// ES el color, y borrar un color de la paleta no destruye nada que no se pueda volver a
// guardar con el "+".
private void PaletteSwatch_Remove(object sender, MouseButtonEventArgs e)
{
    if (sender is not Button b || b.Tag is not byte[] rgb) return;
    _palette.RemoveAll(h => string.Equals(h, ColourMath.ToHex(rgb[0], rgb[1], rgb[2]), StringComparison.OrdinalIgnoreCase));
    PaletteStore.Save(_palette);
    RefreshPalette();
}
```

Y en `BuildLightControls()`, tras construir los presets de fábrica: `_palette = PaletteStore.Load(); RefreshPalette();`

- [ ] **Step 3: Verificación** — build 0/0, suite completa PASS. Manual: elegir un color, pulsar "+", ver que aparece; pulsarlo y ver que el mando cambia; volver a pulsar "+" con el mismo color y ver el aviso; clic derecho para quitarlo; cerrar y reabrir la app y comprobar que la paleta sigue; llenar hasta 12 y ver que el "+" desaparece.

- [ ] **Step 4: Commit**

```bash
git add -u && git commit -m "feat(ui): paleta propia con boton de guardar el color actual"
```

---

### Task 5B: LED de jugador y efecto, en segmentos con icono

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml` (las dos tarjetas, líneas ~1378-1406)
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (`BuildLightControls`, `LightCombo_Changed`, `PlayerEffect_Changed`)
- Modify: `HidusbfModernGui/Theme.xaml` (estilo `MiniSegment`, iconos)

**Interfaces:**
- Consumes: `SegmentButton`/`SegmentGroup` ya existen (los usa la sub-nav del mando); aquí se añade `MiniSegment`, la versión cuadrada y pequeña para un número o un icono suelto.

**Dos choques entre la maqueta y el hardware.** Copiarla al pie de la letra daría un control que miente sobre el mando:

1. **No hay "jugador 5".** `PlayerLeds` tiene seis valores: `Off, Player1..Player4, All`. Los cinco huecos numerados de la maqueta son en realidad **cuatro jugadores más "todas encendidas"**. El sexto segmento va con un icono de las cinco barras y el rótulo accesible "Todas", no con un "5" que prometería un jugador que la consola no tiene.
2. **El brillo tiene tres niveles, no cuatro.** `LedBrightness` es `High/Medium/Low`. La maqueta enseña cuatro soles; el cuarto no podría hacer nada. Van tres, de menos a más lleno.

**Y un choque de nombres.** La maqueta llama "EFECTO PRINCIPAL" a lo que en el código es el efecto de los **LED** (Ninguno/Carga/Estrellas/Respiración). La página ya tiene otra tarjeta llamada **EFECTO**, que es la del **color** (rainbow). Dos tarjetas llamadas casi igual, gobernando cosas distintas, es peor que un rótulo largo: se rotulan **EFECTO DE LOS LED** y **EFECTO DEL COLOR**.

- [ ] **Step 1: El estilo del segmento pequeño.** En `Theme.xaml`, junto a `SegmentButton`:

```xml
<!-- Segmento cuadrado para un numero o un icono suelto: el mismo lenguaje que
     SegmentButton (activo = pastilla clara con tinta oscura) pero al tamano de una casilla.
     Se usa para elegir jugador y brillo, donde la etiqueta es un simbolo y no una palabra. -->
<Style x:Key="MiniSegment" TargetType="RadioButton">
    <Setter Property="FontFamily" Value="{StaticResource UiFont}"/>
    <Setter Property="FontSize" Value="12"/>
    <Setter Property="Foreground" Value="{StaticResource TextLabelBrush}"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Width" Value="34"/>
    <Setter Property="Height" Value="30"/>
    <Setter Property="Margin" Value="0,0,6,0"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="RadioButton">
                <Border x:Name="Seg" CornerRadius="7" Background="{StaticResource SurfaceAltBrush}"
                        BorderBrush="{StaticResource BorderBrush}" BorderThickness="1">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsChecked" Value="True">
                        <Setter TargetName="Seg" Property="Background" Value="{StaticResource TextDataBrush}"/>
                        <Setter TargetName="Seg" Property="BorderBrush" Value="{StaticResource TextDataBrush}"/>
                        <Setter Property="Foreground" Value="{StaticResource BgBrush}"/>
                        <Setter Property="FontWeight" Value="SemiBold"/>
                    </Trigger>
                    <MultiTrigger>
                        <MultiTrigger.Conditions>
                            <Condition Property="IsMouseOver" Value="True"/>
                            <Condition Property="IsChecked" Value="False"/>
                        </MultiTrigger.Conditions>
                        <Setter TargetName="Seg" Property="BorderBrush" Value="{StaticResource TextLabelBrush}"/>
                        <Setter Property="Foreground" Value="{StaticResource TextDataBrush}"/>
                    </MultiTrigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

- [ ] **Step 2: Los iconos que faltan.** En `Theme.xaml`, junto a `ProfileIconPath`:

```xml
<!-- Las cinco barras del LED de jugador todas encendidas: el sexto valor de PlayerLeds
     (All), que NO es un "jugador 5". -->
<Geometry x:Key="AllLedsIconPath">M3,10H5V14H3V10M7,10H9V14H7V10M11,10H13V14H11V10M15,10H17V14H15V10M19,10H21V14H19V10Z</Geometry>
<!-- Brillo: tres soles con el nucleo cada vez mayor. El rayo lo pone el mismo path; lo que
     cambia entre niveles es el tamano al que se dibuja, no la silueta. -->
<Geometry x:Key="StarIconPath">M12,17.27L18.18,21L16.54,13.97L22,9.24L14.81,8.62L12,2L9.19,8.62L2,9.24L7.45,13.97L5.82,21L12,17.27Z</Geometry>
<Geometry x:Key="ClockIconPath">M12,20A8,8 0 0,1 4,12A8,8 0 0,1 12,4A8,8 0 0,1 20,12A8,8 0 0,1 12,20M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M12.5,7H11V13L15.75,15.85L16.5,14.62L12.5,12.25V7Z</Geometry>
```

(`SunIconPath` ya existe, del interruptor de día/noche.)

- [ ] **Step 3: La tarjeta LED DE JUGADOR.** Sustituir en `MainWindow.xaml` el `Border` de LED DE JUGADOR entero por:

```xml
<Border Background="{StaticResource SurfaceBrush}" BorderBrush="{StaticResource BorderBrush}"
        BorderThickness="1" CornerRadius="10" Padding="16,14" Margin="0,8,0,0">
    <StackPanel>
        <StackPanel Orientation="Horizontal" Margin="0,0,0,14">
            <Path Data="{StaticResource ProfileIconPath}" Stretch="Uniform" Width="16" Height="16"
                  VerticalAlignment="Center" Margin="0,0,10,0" Fill="{StaticResource TextDataBrush}"/>
            <TextBlock Text="LUCES DE JUGADOR" Style="{StaticResource SectionHeading}"
                       FontSize="12" VerticalAlignment="Center"/>
        </StackPanel>

        <!-- Jugador y brillo, construidos en code-behind: son seis y tres segmentos que
             cargan cada uno su valor del enum, y escribirlos a mano aqui invitaria a que
             la lista y el enum se separasen. -->
        <StackPanel Orientation="Horizontal">
            <StackPanel x:Name="PlayerLedRow" Orientation="Horizontal" VerticalAlignment="Center"/>
            <Border Width="1" Background="{StaticResource BorderBrush}" Margin="10,2,16,2"/>
            <StackPanel x:Name="BrightnessRow" Orientation="Horizontal" VerticalAlignment="Center"/>
        </StackPanel>
    </StackPanel>
</Border>

<Border Background="{StaticResource SurfaceBrush}" BorderBrush="{StaticResource BorderBrush}"
        BorderThickness="1" CornerRadius="10" Padding="16,14" Margin="0,12,0,0">
    <StackPanel>
        <StackPanel Orientation="Horizontal" Margin="0,0,14,0">
            <Path Data="{StaticResource StarIconPath}" Stretch="Uniform" Width="16" Height="16"
                  VerticalAlignment="Center" Margin="0,0,10,0" Fill="{StaticResource TextDataBrush}"/>
            <!-- "DE LOS LED", no "PRINCIPAL": mas abajo hay otra tarjeta de efecto, la del
                 COLOR. Dos rotulos casi iguales gobernando cosas distintas confunden mas
                 que un rotulo largo. -->
            <TextBlock Text="EFECTO DE LOS LED" Style="{StaticResource SectionHeading}"
                       FontSize="12" VerticalAlignment="Center"/>
        </StackPanel>

        <StackPanel Orientation="Horizontal" Margin="0,14,0,0">
            <ComboBox x:Name="PlayerEffectList" Width="150" VerticalAlignment="Center"
                      SelectionChanged="PlayerEffect_Changed"/>
            <Path Data="{StaticResource ClockIconPath}" Stretch="Uniform" Width="15" Height="15"
                  VerticalAlignment="Center" Margin="18,0,10,0" Fill="{StaticResource TextLabelBrush}"/>
            <Slider x:Name="PlayerSpeed" Minimum="2" Maximum="20" Value="6" Width="140"
                    IsSnapToTickEnabled="True" TickFrequency="1"
                    VerticalAlignment="Center" ValueChanged="PlayerSpeed_Changed"
                    AutomationProperties.Name="Velocidad del efecto de los LED"/>
            <TextBlock x:Name="PlayerSpeedText" Text="" Style="{StaticResource DataText}"
                       VerticalAlignment="Center" Margin="10,0,0,0"/>
        </StackPanel>
    </StackPanel>
</Border>
```

Y la tarjeta de rainbow que hay debajo cambia su rótulo de `EFECTO` a **`EFECTO DEL COLOR`**.

La frase *"Las 5 luces bajo el touchpad. El patron es simetrico, como en la consola."* se va: los segmentos numerados y el icono ya lo enseñan.

- [ ] **Step 4: Construir los segmentos en code-behind.** En `BuildLightControls()`, sustituyendo el relleno de `PlayerLedList` y `BrightnessList`:

```csharp
// Seis segmentos, no cinco: PlayerLeds trae Off, cuatro jugadores y All. El ultimo lleva
// el icono de las cinco barras y no un "5", porque un jugador 5 no existe en el mando.
foreach (var (contenido, valor, nombre) in new (object, PlayerLeds, string)[]
         {
             (Icono("ProfileIconPath"), PlayerLeds.Off,    "Ninguna"),
             ("1", PlayerLeds.Player1, "Jugador 1"),
             ("2", PlayerLeds.Player2, "Jugador 2"),
             ("3", PlayerLeds.Player3, "Jugador 3"),
             ("4", PlayerLeds.Player4, "Jugador 4"),
             (Icono("AllLedsIconPath"), PlayerLeds.All, "Todas encendidas"),
         })
{
    var seg = new RadioButton
    {
        Style = (Style)FindResource("MiniSegment"),
        GroupName = "PlayerLed",
        Content = contenido,
        Tag = valor,
    };
    System.Windows.Automation.AutomationProperties.SetName(seg, nombre);
    seg.ToolTip = nombre;
    seg.Checked += PlayerLed_Checked;
    PlayerLedRow.Children.Add(seg);
}

// Tres niveles, no cuatro: LedBrightness es High/Medium/Low. El sol crece con el nivel.
foreach (var (tamano, valor, nombre) in new (double, LedBrightness, string)[]
         {
             (11, LedBrightness.Low,    "Brillo bajo"),
             (14, LedBrightness.Medium, "Brillo medio"),
             (17, LedBrightness.High,   "Brillo alto"),
         })
{
    var seg = new RadioButton
    {
        Style = (Style)FindResource("MiniSegment"),
        GroupName = "LedBrightness",
        Content = Icono("SunIconPath", tamano),
        Tag = valor,
    };
    System.Windows.Automation.AutomationProperties.SetName(seg, nombre);
    seg.ToolTip = nombre;
    seg.Checked += Brightness_Checked;
    BrightnessRow.Children.Add(seg);
}
```

con el helper y los dos handlers:

```csharp
// El Fill se ata al Foreground del segmento: un Path no lo hereda, y sin esto el icono del
// segmento activo se quedaria gris sobre la pastilla clara (la leccion L7, otra vez).
private System.Windows.Shapes.Path Icono(string clave, double tamano = 15)
{
    var p = new System.Windows.Shapes.Path
    {
        Data = (Geometry)FindResource(clave),
        Stretch = Stretch.Uniform,
        Width = tamano,
        Height = tamano,
    };
    p.SetBinding(System.Windows.Shapes.Shape.FillProperty,
        new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(RadioButton), 1) });
    return p;
}

private void PlayerLed_Checked(object sender, RoutedEventArgs e)
{
    if (_updatingLight) return;
    ApplyLightNow();
}

private void Brightness_Checked(object sender, RoutedEventArgs e)
{
    if (_updatingLight) return;
    ApplyLightNow();
}
```

- [ ] **Step 5: Los lectores del valor.** `CurrentLight()` y `RememberLight()` leen hoy `((ComboBoxItem)PlayerLedList.SelectedItem).Tag`. Sustituir por dos ayudantes que recorren la fila, para que el valor salga de un solo sitio:

```csharp
// null si aun no se ha construido la fila (el arranque llama a esto antes de tiempo por
// varios caminos); los llamadores ya saben salirse cuando no hay valor.
private PlayerLeds? CurrentPlayerLed()
    => PlayerLedRow.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as PlayerLeds?;

private LedBrightness? CurrentBrightness()
    => BrightnessRow.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as LedBrightness?;
```

y `SelectComboByTag(PlayerLedList, ...)` / `SelectComboByTag(BrightnessList, ...)` pasan a marcar el segmento cuyo `Tag` coincide, **bajo `_updatingLight`** para que restaurar la intención guardada no escriba al mando.

- [ ] **Step 6: Verificación** — build 0/0, suite completa PASS. Manual, con el mando: pulsar cada uno de los seis segmentos de jugador y **mirar el mando**, comprobando que el patrón de LED coincide (el de "todas" enciende las cinco, el del icono de persona las apaga); los tres de brillo, notando la diferencia; elegir Estrellas y ver que la barra del reloj cambia la cadencia; cerrar y reabrir y comprobar que vuelve lo elegido.

- [ ] **Step 7: Commit**

```bash
git add -u && git commit -m "feat(ui): LED de jugador y efecto, en segmentos con icono"
```

---

### Task 6: Documentación y verificación integral

**Files:**
- Modify: `README.md`, `docs/DOCUMENTACION.md`

- [ ] **Step 1: README**, en la sección de luces:

```markdown
La pagina de **LUCES** ya no pide elegir el mando: lo resuelve sola, y sigue
encontrandolo cuando el mando virtual esta encendido y HidHide oculta el fisico.

El color se elige con tres barras (tono, saturacion y brillo del color) o
escribiendo el hex. Ojo con dos "brillos" que no son lo mismo: la barra de
**brillo del color** es cuanto se acerca al negro; el desplegable **BRILLO** es la
intensidad del LED del mando, una propiedad del hardware.

Con **+** guardas el color actual en tu paleta (hasta 12, en
`%APPDATA%\UltraPolling\palette.json`); con clic derecho lo quitas.
```

- [ ] **Step 2: DOCUMENTACION.md**, al mapa de módulos:

```markdown
- **`PalettePreset.cs`** — los colores que el usuario guarda (`palette.json`). `Add` es
  pura y testeable: normaliza, rechaza repetidos y al llegar al tope tira el mas viejo.
- **`ColourPicker`** — tres barras HSB con su valor, hex editable y vista previa. Los
  fondos de saturacion y brillo se recalculan con el tono: una barra que muestra el rojo
  mientras el color es azul miente sobre lo que hara al arrastrarla.
```

Y a las lecciones:

```markdown
- **L9 — Un control que solo puede tener un valor no es una eleccion.** El desplegable de
  mando de la pagina de luces era una lista de un elemento que habia que confirmar. El
  resolutor que lo sustituye ya existia y ya se llamaba desde ahi mismo, para el caso
  dificil (HidHide ocultando el mando); solo faltaba hacerlo el unico camino.
```

- [ ] **Step 3: Verificación integral** — build 0/0; `dotnet test` PASS; recorrer la página de luces con el mando conectado **y** con el mando virtual encendido; comprobar que los perfiles siguen aplicando su mitad de luz (Task 4 les cambio el camino).

- [ ] **Step 4: Commit**

```bash
git add -u && git commit -m "docs: luces sin selector de mando y con paleta propia"
```

---

## Lo que este plan NO hace, y por qué

**Cuentagotas de pantalla.** La referencia lleva uno. Leer el color de un píxel cualquiera de la pantalla obliga a capturar la pantalla del usuario, y eso es una capacidad de captura, no un detalle del selector de color. No entra por iniciativa mía; si lo quieres, se pide aparte y se explica en la interfaz qué hace.

**Barra de alfa.** Es la cuarta de la referencia. La barra de luz del mando no tiene transparencia: una barra que no puede hacer nada es peor que no tenerla.

**Los ocho presets de fábrica.** Se quedan. Son el atajo para quien llega queriendo "azul"; la paleta propia es para quien ya encontró su color.

## Self-review

- **Cobertura del pedido:** quitar el desplegable de mando → Task 4; rehacer el apartado de color con barras y valores → Task 3; paleta propia con "+" → Tasks 2 y 5; LED de jugador y efecto en segmentos con icono → Task 5B. ✓
- **Dos correcciones a la maqueta, no omisiones:** seis segmentos de jugador y no cinco (`PlayerLeds` trae `Off..Player4` y `All`, y no existe un jugador 5), y tres de brillo y no cuatro (`LedBrightness` es `High/Medium/Low`). Copiar la maqueta al pie de la letra habría dejado dos controles prometiendo estados que el mando no tiene. ✓
- **Placeholders:** ninguno; el hex, la paleta y el selector van con su código y sus casos de prueba completos. ✓
- **Tipos consistentes:** `ColourMath.ToHex/TryParseHex` (Task 1) los consumen `PaletteStore` (2), `ColourPicker` (3) y `MainWindow` (5); `_lightPadId` (Task 4) lo consumen los cuatro sitios enumerados; `ColourPicker` conserva `SelectedColor`/`ColorChanged`, así que Task 3 no arrastra cambios a `MainWindow`. ✓
- **Riesgo cubierto — perder el mando oculto:** Task 4 conserva el resolutor en-proceso como respaldo y lo pone en la verificación manual como el caso que sí hay que probar. ✓
- **Riesgo cubierto — dos "brillos":** rotulado explícito y escrito en el README, porque la barra HSB y `LedBrightness` son cosas distintas que ahora se ven juntas. ✓
- **Riesgo cubierto — escribir al mando por cada tecla:** el hex se aplica en Enter o al salir del campo, no en `TextChanged`. ✓
