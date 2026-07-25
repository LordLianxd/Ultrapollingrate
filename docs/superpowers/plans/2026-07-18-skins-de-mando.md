# Sistema de skins del visualizador (mando PS5 fotorreal, opcional y local) — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el visualizador pueda dibujarse con un **skin de imágenes** (base fotorreal + sprites de estados) en vez del dibujo vectorial, sin meter arte de terceros en el repo público: la app trae el vectorial y, **si el usuario coloca un skin en `%APPDATA%\UltraPolling\skins\<nombre>\`, lo usa automáticamente**.

**Architecture:** Un `PadSkin` (modelo puro + cargador con validación, testeable) describe un skin: la imagen base y, por cada elemento (sticks, botones, gatillos, touchpad…), de qué archivo sale, qué región de ese archivo se recorta y dónde va sobre la base — todo en píxeles de la imagen base, que es el sistema de coordenadas del skin. Un control nuevo `SkinnedPadVisual` renderiza ese modelo con `Image` + `CroppedBitmap` y expone el MISMO `Update(ControllerState)` que `PadVisual`, así el feed no cambia. Un `PadVisualHost` elige en runtime: skin válido → `SkinnedPadVisual`; si no → el `PadVisual` vectorial de siempre. La geometría del skin PS5 se autora midiendo los assets, con un **modo calibración** en la app que dibuja las regiones encima para verificarlas de un vistazo.

**Tech Stack:** .NET 9 WPF (`BitmapImage`, `CroppedBitmap`), System.Text.Json, xUnit. Sin dependencias nuevas.

## Global Constraints

- **Legal (la razón de ser de este plan):** el arte de terceros (p. ej. el skin PS5 de Jayraydee, un producto de pago) **NUNCA** entra al repo ni al ZIP de release. Los skins viven **fuera** del repo, en `%APPDATA%\UltraPolling\skins\`. El `.gitignore` cubre cualquier carpeta `skins/` local. El README explica cómo instalar un skin propio y **advierte** que redistribuir arte ajeno es cosa del usuario, no de la app.
- La app debe funcionar **igual de bien sin ningún skin**: el vectorial es el default y el fallback ante cualquier error (archivo faltante, JSON inválido, PNG corrupto). Un skin roto **nunca** puede tumbar la app ni dejar el visualizador en blanco.
- UI en **español**, tema monocromo. Un skin a color es la excepción aceptada (es arte del usuario).
- El proyecto de tests **linkea fuentes individualmente** (`HidusbfModernGui.Tests.csproj`): **`PadSkin.cs` (nuevo, puro) debe añadirse ahí**. Nada de WPF/HidSharp/Nefarius en tests → `SkinnedPadVisual`/`PadVisualHost` se verifican a mano.
- El portable de un solo archivo debe seguir compilando (`package.ps1`): sin recursos embebidos nuevos, sin NuGets nuevos.
- Commits **sin** Co-Authored-By. El push lo hace el usuario.

## Contexto verificado

- `PadVisual` (UserControl) ya existe: `Update(ControllerState)`, `StreamerBackground`, `Viewbox`+`Canvas 360×260`. Se conserva **sin cambios** como default/fallback.
- Lo alimenta `VisualizerTick` en `MainWindow.xaml.cs`: `raw → RemapEngine.Transform(raw,_remap) → ConfigPadVisual.Update(outState)` y `_streamerWindow?.Pad.Update(outState)`.
- `PadVisualMath.StickOffset(x,y,radius)` (puro, testeado) devuelve el desplazamiento del pulgar con Y de pantalla y magnitud acotada.
- `StreamerWindow` expone `public PadVisual Pad => PadControl;` — **cambiará** para exponer el host.
- Assets de referencia del usuario en `C:\Users\Administrator\Downloads\work ultrapolling\scraped_controller_assets` (12 únicos): base `02` (1050×850), `03` desconectado (1050×850), `04` gatillos (241×114), `05` bumpers (133×48), `06` botones de cara pulsados (211×211, los 4 en diamante), `07`/`08` share/options (34×51), `09` sticks (234×116 = dos de 117×116: normal y pulsado), `10` PS pulsado (72×57), `11` face (71×58), `12` touchpad pulsado (391×214).

## Estructura de archivos

- Create: `HidusbfModernGui/PadSkin.cs` (modelo + cargador, puro)
- Create: `HidusbfModernGui/SkinnedPadVisual.xaml` + `.xaml.cs` (render por imágenes)
- Create: `HidusbfModernGui/PadVisualHost.xaml` + `.xaml.cs` (elige skin o vector)
- Modify: `HidusbfModernGui/MainWindow.xaml(.cs)`, `StreamerWindow.xaml(.cs)` (usar el host)
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj` (link de PadSkin)
- Test: `HidusbfModernGui.Tests/PadSkinTests.cs`
- Modify: `.gitignore`, `README.md`, `docs/DOCUMENTACION.md`

---

### Task 1: `PadSkin` — modelo y cargador con validación (TDD)

**Files:**
- Create: `HidusbfModernGui/PadSkin.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj`
- Test: `HidusbfModernGui.Tests/PadSkinTests.cs`

**Interfaces:**
- Produces: `SkinRect(double X,double Y,double W,double H)`; `SkinPart{ string File; SkinRect Src; SkinRect Dst; }`; `PadSkin{ string Name; string BaseFile; double BaseWidth/BaseHeight; double StickRadius; Dictionary<string,SkinPart> Parts; }`; `PadSkinLoader.Load(string dir) -> (PadSkin? Skin, string? Error)`; `PadSkinLoader.FindFirstSkinDir(string skinsRoot) -> string?`; `PadSkin.RequiredPartKeys` (las claves que la app entiende).
- Consumido por `SkinnedPadVisual` (Task 2) y `PadVisualHost` (Task 3).

**Claves de parte (contrato con el renderer):** `"stick.left"`, `"stick.right"` (Src = estado normal; `Src2` opcional = pulsado L3/R3), `"btn.cross"`, `"btn.circle"`, `"btn.square"`, `"btn.triangle"`, `"dpad.up"`, `"dpad.down"`, `"dpad.left"`, `"dpad.right"`, `"btn.l1"`, `"btn.r1"`, `"btn.l2"`, `"btn.r2"`, `"btn.share"`, `"btn.options"`, `"btn.ps"`, `"btn.touchpad"`. Todas **opcionales**: un skin que no define `dpad.up` simplemente no resalta esa parte (degradación elegante, no error).

- [ ] **Step 1: Link en el csproj** (antes de los tests):

```xml
<Compile Include="..\HidusbfModernGui\PadSkin.cs" Link="PadSkin.cs" />
```

- [ ] **Step 2: Tests que fallan** — crear `HidusbfModernGui.Tests/PadSkinTests.cs`:

```csharp
using System;
using System.IO;
using HidusbfModernGui;
using Xunit;

public class PadSkinTests : IDisposable
{
    private readonly string _dir;

    public PadSkinTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "UltraPollingSkin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string WriteSkin(string json, params string[] files)
    {
        File.WriteAllText(Path.Combine(_dir, "skin.json"), json);
        foreach (var f in files) File.WriteAllBytes(Path.Combine(_dir, f), new byte[] { 1, 2, 3 });
        return _dir;
    }

    private const string ValidJson = """
    {
      "Name": "Prueba",
      "BaseFile": "base.png",
      "BaseWidth": 1050,
      "BaseHeight": 850,
      "StickRadius": 22,
      "Parts": {
        "stick.left":  { "File": "sticks.png", "Src": { "X": 0, "Y": 0, "W": 117, "H": 116 }, "Dst": { "X": 310, "Y": 435, "W": 117, "H": 116 } },
        "btn.cross":   { "File": "faces.png",  "Src": { "X": 70, "Y": 140, "W": 71, "H": 71 }, "Dst": { "X": 795, "Y": 400, "W": 71, "H": 71 } }
      }
    }
    """;

    [Fact]
    public void Load_ValidSkin_ReturnsSkinWithParts()
    {
        var dir = WriteSkin(ValidJson, "base.png", "sticks.png", "faces.png");
        var (skin, err) = PadSkinLoader.Load(dir);

        Assert.Null(err);
        Assert.NotNull(skin);
        Assert.Equal("Prueba", skin!.Name);
        Assert.Equal(1050, skin.BaseWidth, 3);
        Assert.Equal(2, skin.Parts.Count);
        Assert.Equal(117, skin.Parts["stick.left"].Src.W, 3);
    }

    [Fact]
    public void Load_MissingManifest_ReturnsError()
    {
        var (skin, err) = PadSkinLoader.Load(_dir);   // directorio vacio
        Assert.Null(skin);
        Assert.NotNull(err);
    }

    [Fact]
    public void Load_MissingBaseImage_ReturnsError()
    {
        var dir = WriteSkin(ValidJson);   // sin PNGs
        var (skin, err) = PadSkinLoader.Load(dir);
        Assert.Null(skin);
        Assert.Contains("base.png", err!);
    }

    [Fact]
    public void Load_BrokenJson_ReturnsErrorNotException()
    {
        File.WriteAllText(Path.Combine(_dir, "skin.json"), "{ esto no es json ");
        var (skin, err) = PadSkinLoader.Load(_dir);
        Assert.Null(skin);
        Assert.NotNull(err);
    }

    [Fact]
    public void Load_ZeroBaseSize_IsRejected()
    {
        var bad = ValidJson.Replace("\"BaseWidth\": 1050", "\"BaseWidth\": 0");
        var dir = WriteSkin(bad, "base.png", "sticks.png", "faces.png");
        var (skin, err) = PadSkinLoader.Load(dir);
        Assert.Null(skin);
        Assert.NotNull(err);
    }

    [Fact]
    public void Load_PartWithMissingFile_IsDroppedNotFatal()
    {
        // Una parte que apunta a un PNG inexistente se descarta; el resto del skin sirve.
        var dir = WriteSkin(ValidJson, "base.png", "sticks.png");   // falta faces.png
        var (skin, err) = PadSkinLoader.Load(dir);

        Assert.Null(err);
        Assert.NotNull(skin);
        Assert.True(skin!.Parts.ContainsKey("stick.left"));
        Assert.False(skin.Parts.ContainsKey("btn.cross"));
    }

    [Fact]
    public void FindFirstSkinDir_PicksDirectoryContainingManifest()
    {
        string root = Path.Combine(_dir, "skins");
        string mine = Path.Combine(root, "ps5");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, "skin.json"), ValidJson);

        Assert.Equal(mine, PadSkinLoader.FindFirstSkinDir(root));
    }

    [Fact]
    public void FindFirstSkinDir_NoSkins_ReturnsNull()
    {
        Assert.Null(PadSkinLoader.FindFirstSkinDir(Path.Combine(_dir, "no-existe")));
    }
}
```

- [ ] **Step 3: Verificar que fallan** — `dotnet test HidusbfModernGui.Tests\HidusbfModernGui.Tests.csproj --filter "FullyQualifiedName~PadSkinTests"`. Esperado: error de compilación.

- [ ] **Step 4: Implementación** — crear `HidusbfModernGui/PadSkin.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HidusbfModernGui
{
    // Un skin del visualizador: una imagen BASE del mando y, por cada parte, de que archivo
    // sale, que region se recorta (Src) y donde se pega sobre la base (Dst). Todas las
    // coordenadas estan en PIXELES DE LA IMAGEN BASE - ese es el sistema de coordenadas del
    // skin, y el control lo escala entero con un Viewbox.
    //
    // El arte de un skin NO vive en el repo: los skins se instalan en
    // %APPDATA%\UltraPolling\skins\<nombre>\ (ver README). La app siempre funciona sin
    // ninguno: sin skin valido se dibuja el mando vectorial propio.
    public sealed class SkinRect
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public bool IsValid => W > 0 && H > 0 && X >= 0 && Y >= 0;
    }

    public sealed class SkinPart
    {
        public string File { get; set; } = "";
        public SkinRect Src { get; set; } = new();      // region en el archivo de sprites
        public SkinRect? Src2 { get; set; }             // opcional: 2do estado (p.ej. stick pulsado)
        public SkinRect Dst { get; set; } = new();      // destino en pixeles de la base
    }

    public sealed class PadSkin
    {
        public string Name { get; set; } = "";
        public string BaseFile { get; set; } = "";
        public double BaseWidth { get; set; }
        public double BaseHeight { get; set; }
        // Radio (en pixeles de la base) que recorre el centro del stick de tope a tope.
        public double StickRadius { get; set; } = 20;
        public Dictionary<string, SkinPart> Parts { get; set; } = new();

        // Ruta absoluta del directorio del skin; la rellena el cargador.
        public string Directory { get; set; } = "";

        // Claves que el renderer entiende. Un skin puede definir todas, algunas o ninguna:
        // lo que no defina, simplemente no se dibuja (degradacion elegante).
        public static readonly string[] RequiredPartKeys =
        {
            "stick.left", "stick.right",
            "btn.cross", "btn.circle", "btn.square", "btn.triangle",
            "dpad.up", "dpad.down", "dpad.left", "dpad.right",
            "btn.l1", "btn.r1", "btn.l2", "btn.r2",
            "btn.share", "btn.options", "btn.ps", "btn.touchpad",
        };
    }

    public static class PadSkinLoader
    {
        public const string ManifestName = "skin.json";

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        // Carga y VALIDA un skin. Nunca lanza: devuelve (null, motivo) ante cualquier
        // problema, porque un skin roto jamas puede tumbar la app - solo se cae al
        // visualizador vectorial.
        public static (PadSkin? Skin, string? Error) Load(string dir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !System.IO.Directory.Exists(dir))
                    return (null, $"No existe la carpeta del skin: {dir}");

                string manifest = Path.Combine(dir, ManifestName);
                if (!File.Exists(manifest))
                    return (null, $"Falta {ManifestName} en {dir}");

                var skin = JsonSerializer.Deserialize<PadSkin>(File.ReadAllText(manifest), Options);
                if (skin == null) return (null, "El manifiesto del skin esta vacio.");

                if (skin.BaseWidth <= 0 || skin.BaseHeight <= 0)
                    return (null, "BaseWidth/BaseHeight deben ser mayores que cero.");

                if (string.IsNullOrWhiteSpace(skin.BaseFile) ||
                    !File.Exists(Path.Combine(dir, skin.BaseFile)))
                    return (null, $"Falta la imagen base del skin: {skin.BaseFile}");

                // Partes: se descarta la que apunte a un archivo inexistente o traiga
                // rectangulos invalidos, en vez de invalidar el skin entero.
                skin.Parts = skin.Parts
                    .Where(kv => kv.Value != null
                              && !string.IsNullOrWhiteSpace(kv.Value.File)
                              && File.Exists(Path.Combine(dir, kv.Value.File))
                              && kv.Value.Src.IsValid && kv.Value.Dst.IsValid)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                if (skin.StickRadius <= 0) skin.StickRadius = 20;
                skin.Directory = dir;
                if (string.IsNullOrWhiteSpace(skin.Name)) skin.Name = Path.GetFileName(dir);
                return (skin, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PadSkinLoader.Load fallo: {ex.Message}");
                return (null, $"No se pudo leer el skin: {ex.Message}");
            }
        }

        // Primer subdirectorio de skinsRoot que contenga un manifiesto (orden alfabetico,
        // estable). null si no hay ninguno - el caso normal de una instalacion limpia.
        public static string? FindFirstSkinDir(string skinsRoot)
        {
            try
            {
                if (!System.IO.Directory.Exists(skinsRoot)) return null;
                return System.IO.Directory.GetDirectories(skinsRoot)
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, ManifestName)));
            }
            catch { return null; }
        }

        // Carpeta estandar de skins del usuario.
        public static string DefaultSkinsRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UltraPolling", "skins");
    }
}
```

- [ ] **Step 5: Verificar que pasan** — mismo filtro PASS; suite completa PASS.
- [ ] **Step 6: Commit** — `git add HidusbfModernGui/PadSkin.cs HidusbfModernGui.Tests/PadSkinTests.cs HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj && git commit -m "feat: PadSkin - modelo y cargador validado de skins del visualizador (TDD)"`

---

### Task 2: `SkinnedPadVisual` — render del skin con la misma interfaz

**Files:**
- Create: `HidusbfModernGui/SkinnedPadVisual.xaml` + `.xaml.cs`

**Interfaces:**
- Consumes: `PadSkin`, `ControllerState`, `PadVisualMath.StickOffset`, `PadButton`.
- Produces: `SkinnedPadVisual` con `bool Load(PadSkin skin)` (false si las imágenes no cargan), `void Update(ControllerState s)` (misma firma que `PadVisual`), `bool StreamerBackground`, y `bool ShowCalibration` (dibuja el contorno + la clave de cada parte encima, para verificar la geometría).

- [ ] **Step 1: XAML** — `SkinnedPadVisual.xaml`: `Border x:Name="RootSurface"` → `Viewbox Stretch="Uniform"` → `Canvas x:Name="Board"` (su `Width`/`Height` los fija `Load` con `BaseWidth`/`BaseHeight`). El `Canvas` empieza vacío: base y partes se crean en código, porque dependen del manifiesto.

- [ ] **Step 2: code-behind** — `SkinnedPadVisual.xaml.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace HidusbfModernGui
{
    // Dibuja el mando a partir de un PadSkin: una imagen base + una capa por parte, cada
    // una recortada de su hoja de sprites (CroppedBitmap) y colocada en pixeles de la base.
    // Expone el MISMO Update(ControllerState) que PadVisual, asi el feed no distingue cual
    // de los dos esta activo.
    public partial class SkinnedPadVisual : UserControl
    {
        private PadSkin? _skin;
        private readonly Dictionary<string, Image> _layers = new();   // clave -> capa
        private readonly Dictionary<string, ImageSource> _alt = new(); // clave -> 2do estado (Src2)
        private readonly Dictionary<string, ImageSource> _main = new();
        private readonly List<UIElement> _calibration = new();
        private Image? _baseImage;

        public SkinnedPadVisual() => InitializeComponent();

        private bool _streamerBackground;
        public bool StreamerBackground
        {
            get => _streamerBackground;
            set
            {
                _streamerBackground = value;
                // El skin ya trae su propio fondo transparente (PNG con alfa): en modo
                // streamer solo hay que quitar el fondo del contenedor.
                RootSurface.Background = value ? Brushes.Transparent : (Brush)FindResource("SurfaceBrush");
            }
        }

        private bool _showCalibration;
        public bool ShowCalibration
        {
            get => _showCalibration;
            set { _showCalibration = value; foreach (var e in _calibration) e.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
        }

        // Construye las capas. Devuelve false si la base no se puede decodificar (skin
        // inservible -> el host cae al vectorial).
        public bool Load(PadSkin skin)
        {
            Board.Children.Clear();
            _layers.Clear(); _main.Clear(); _alt.Clear(); _calibration.Clear();
            _skin = skin;

            Board.Width = skin.BaseWidth;
            Board.Height = skin.BaseHeight;

            var baseSrc = TryLoadBitmap(Path.Combine(skin.Directory, skin.BaseFile));
            if (baseSrc == null) return false;

            _baseImage = new Image { Source = baseSrc, Width = skin.BaseWidth, Height = skin.BaseHeight };
            Canvas.SetLeft(_baseImage, 0);
            Canvas.SetTop(_baseImage, 0);
            Board.Children.Add(_baseImage);

            foreach (var (key, part) in skin.Parts)
            {
                var sheet = TryLoadBitmap(Path.Combine(skin.Directory, part.File));
                if (sheet == null) continue;

                var main = TryCrop(sheet, part.Src);
                if (main == null) continue;

                var img = new Image
                {
                    Source = main,
                    Width = part.Dst.W,
                    Height = part.Dst.H,
                    Visibility = Visibility.Collapsed,   // los estados aparecen al pulsarse
                };
                Canvas.SetLeft(img, part.Dst.X);
                Canvas.SetTop(img, part.Dst.Y);
                Board.Children.Add(img);

                _layers[key] = img;
                _main[key] = main;
                if (part.Src2 != null && part.Src2.IsValid)
                {
                    var alt = TryCrop(sheet, part.Src2);
                    if (alt != null) _alt[key] = alt;
                }

                AddCalibrationBox(key, part.Dst);
            }

            // Los sticks son la unica capa SIEMPRE visible (se mueven, no parpadean).
            foreach (var k in new[] { "stick.left", "stick.right" })
                if (_layers.TryGetValue(k, out var s)) s.Visibility = Visibility.Visible;

            ShowCalibration = _showCalibration;
            return true;
        }

        // Contorno + etiqueta de una parte, oculto salvo en modo calibracion: permite ver
        // de un vistazo si un Dst del manifiesto esta bien puesto sobre la base.
        private void AddCalibrationBox(string key, SkinRect dst)
        {
            var box = new Rectangle
            {
                Width = dst.W, Height = dst.H,
                Stroke = Brushes.Magenta, StrokeThickness = 2,
                Fill = Brushes.Transparent, Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(box, dst.X); Canvas.SetTop(box, dst.Y);
            Board.Children.Add(box); _calibration.Add(box);

            var tag = new TextBlock
            {
                Text = key, Foreground = Brushes.Magenta, FontSize = 12,
                Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            };
            Canvas.SetLeft(tag, dst.X); Canvas.SetTop(tag, Math.Max(0, dst.Y - 14));
            Board.Children.Add(tag); _calibration.Add(tag);
        }

        public void Update(ControllerState s)
        {
            if (_skin == null) return;

            MoveStick("stick.left", s.Left, s.Pressed.Contains(PadButton.L3));
            MoveStick("stick.right", s.Right, s.Pressed.Contains(PadButton.R3));

            var p = s.Pressed;
            Show("btn.cross", p.Contains(PadButton.Cross));
            Show("btn.circle", p.Contains(PadButton.Circle));
            Show("btn.square", p.Contains(PadButton.Square));
            Show("btn.triangle", p.Contains(PadButton.Triangle));
            Show("dpad.up", p.Contains(PadButton.DpadUp));
            Show("dpad.down", p.Contains(PadButton.DpadDown));
            Show("dpad.left", p.Contains(PadButton.DpadLeft));
            Show("dpad.right", p.Contains(PadButton.DpadRight));
            Show("btn.l1", p.Contains(PadButton.L1));
            Show("btn.r1", p.Contains(PadButton.R1));
            Show("btn.share", p.Contains(PadButton.Share));
            Show("btn.options", p.Contains(PadButton.Options));
            Show("btn.ps", p.Contains(PadButton.PS));
            Show("btn.touchpad", p.Contains(PadButton.TouchpadClick));

            // Gatillos analogicos: la capa aparece y su opacidad sigue el recorrido, asi se
            // ve "cuanto" esta apretado y no solo si/no.
            Fade("btn.l2", PadVisualMath.Fill01(s.L2));
            Fade("btn.r2", PadVisualMath.Fill01(s.R2));
        }

        private void MoveStick(string key, StickInput stick, bool pressed)
        {
            if (_skin == null || !_layers.TryGetValue(key, out var img)) return;
            if (!_skin.Parts.TryGetValue(key, out var part)) return;

            var (dx, dy) = PadVisualMath.StickOffset(stick.X, stick.Y, _skin.StickRadius);
            Canvas.SetLeft(img, part.Dst.X + dx);
            Canvas.SetTop(img, part.Dst.Y + dy);

            // Si el skin trae un 2do recorte para el stick pulsado (L3/R3), se alterna.
            if (_alt.TryGetValue(key, out var altSrc) && _main.TryGetValue(key, out var mainSrc))
                img.Source = pressed ? altSrc : mainSrc;
        }

        private void Show(string key, bool on)
        {
            if (_layers.TryGetValue(key, out var img))
            {
                img.Opacity = 1.0;
                img.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void Fade(string key, double amount)
        {
            if (!_layers.TryGetValue(key, out var img)) return;
            img.Visibility = amount > 0.01 ? Visibility.Visible : Visibility.Collapsed;
            img.Opacity = amount;
        }

        private static BitmapImage? TryLoadBitmap(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                // Cargar entero en memoria: el archivo del usuario no queda bloqueado y el
                // skin sobrevive a que lo muevan o lo borren mientras la app corre.
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        // Recorte defensivo: un Src fuera de los limites de la hoja tirar�a una excepcion
        // de CroppedBitmap, asi que se acota al tama�o real antes de recortar.
        private static CroppedBitmap? TryCrop(BitmapSource sheet, SkinRect r)
        {
            try
            {
                int x = (int)Math.Round(r.X), y = (int)Math.Round(r.Y);
                int w = (int)Math.Round(r.W), h = (int)Math.Round(r.H);
                if (x >= sheet.PixelWidth || y >= sheet.PixelHeight) return null;
                w = Math.Min(w, sheet.PixelWidth - x);
                h = Math.Min(h, sheet.PixelHeight - y);
                if (w <= 0 || h <= 0) return null;
                var c = new CroppedBitmap(sheet, new Int32Rect(x, y, w, h));
                c.Freeze();
                return c;
            }
            catch { return null; }
        }
    }
}
```

- [ ] **Step 3: Verificación** — `dotnet build` 0/0 y suite completa PASS. No hay test unitario (WPF); se verifica visualmente en la Task 4.
- [ ] **Step 4: Commit** — `git add HidusbfModernGui/SkinnedPadVisual.xaml HidusbfModernGui/SkinnedPadVisual.xaml.cs && git commit -m "feat: SkinnedPadVisual - render del mando por skin de imagenes"`

---

### Task 3: `PadVisualHost` — elige skin o vector, y sustituye a `PadVisual` en los dos sitios

**Files:**
- Create: `HidusbfModernGui/PadVisualHost.xaml` + `.xaml.cs`
- Modify: `HidusbfModernGui/MainWindow.xaml(.cs)`, `HidusbfModernGui/StreamerWindow.xaml(.cs)`

**Interfaces:**
- Produces: `PadVisualHost` con `void Update(ControllerState)`, `bool StreamerBackground`, `bool ShowCalibration`, `string StatusText` (qué se está usando: nombre del skin o "vectorial"), `void ReloadSkin()`.
- Reemplaza a `PadVisual` como tipo de `ConfigPadVisual` y de `StreamerWindow.Pad`.

- [ ] **Step 1: XAML** — `PadVisualHost.xaml`: un `Grid` con los dos controles superpuestos, `local:PadVisual x:Name="Vector"` y `local:SkinnedPadVisual x:Name="Skinned" Visibility="Collapsed"`.

- [ ] **Step 2: code-behind**:

```csharp
using System.Windows;
using System.Windows.Controls;

namespace HidusbfModernGui
{
    // Decide en runtime con que se dibuja el mando: si hay un skin valido instalado en
    // %APPDATA%\UltraPolling\skins\, se usa; si no (el caso por defecto y el de cualquier
    // skin roto), el mando vectorial propio. El resto de la app habla solo con este host:
    // Update() es identico en ambos caminos.
    public partial class PadVisualHost : UserControl
    {
        public PadVisualHost()
        {
            InitializeComponent();
            ReloadSkin();
        }

        public string StatusText { get; private set; } = "Mando vectorial";

        public void ReloadSkin()
        {
            bool ok = false;
            var dir = PadSkinLoader.FindFirstSkinDir(PadSkinLoader.DefaultSkinsRoot);
            if (dir != null)
            {
                var (skin, err) = PadSkinLoader.Load(dir);
                if (skin != null && Skinned.Load(skin))
                {
                    ok = true;
                    StatusText = $"Skin: {skin.Name}";
                }
                else
                {
                    StatusText = $"Skin invalido ({err ?? "no se pudo dibujar"}), usando el vectorial";
                }
            }
            else
            {
                StatusText = "Mando vectorial";
            }

            Skinned.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            Vector.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        }

        public void Update(ControllerState s)
        {
            if (Skinned.Visibility == Visibility.Visible) Skinned.Update(s);
            else Vector.Update(s);
        }

        private bool _streamerBackground;
        public bool StreamerBackground
        {
            get => _streamerBackground;
            set { _streamerBackground = value; Vector.StreamerBackground = value; Skinned.StreamerBackground = value; }
        }

        public bool ShowCalibration
        {
            get => Skinned.ShowCalibration;
            set => Skinned.ShowCalibration = value;
        }
    }
}
```

- [ ] **Step 3: Sustituir en los dos hosts** — en `MainWindow.xaml`, `<local:PadVisual x:Name="ConfigPadVisual" .../>` pasa a `<local:PadVisualHost x:Name="ConfigPadVisual" .../>` (el nombre no cambia, así el resto del code-behind sigue igual). En `StreamerWindow.xaml`, `PadControl` pasa a `PadVisualHost` y la propiedad `public PadVisual Pad => PadControl;` cambia a `public PadVisualHost Pad => PadControl;`. Verificar que `PadControl.StreamerBackground = true` del constructor sigue compilando (el host expone la misma propiedad).
- [ ] **Step 4: Estado + recargar en la UI** — en la tarjeta "MANDO EN VIVO" de `MainWindow.xaml`, junto a `StreamerRow`, añadir un `TextBlock x:Name="SkinStatusText"` (estilo `FieldLabel`) y un botón `RECARGAR SKIN` (`Click="ReloadSkin_Click"`) más un `CheckBox x:Name="CalibrationCheck" Content="Modo calibracion"` (`Click="Calibration_Click"`). Code-behind:

```csharp
private void ReloadSkin_Click(object sender, RoutedEventArgs e)
{
    ConfigPadVisual.ReloadSkin();
    _streamerWindow?.Pad.ReloadSkin();
    SkinStatusText.Text = ConfigPadVisual.StatusText;
}

private void Calibration_Click(object sender, RoutedEventArgs e)
    => ConfigPadVisual.ShowCalibration = CalibrationCheck.IsChecked == true;
```

Y en `BuildRemapControls` (o donde se inicializa el configurador), fijar el texto inicial: `SkinStatusText.Text = ConfigPadVisual.StatusText;`.

- [ ] **Step 5: Verificación** — build 0/0; suite completa PASS. Manual: sin skin instalado, todo se comporta EXACTAMENTE como antes (vectorial) y el estado dice "Mando vectorial".
- [ ] **Step 6: Commit** — `git add -u && git add HidusbfModernGui/PadVisualHost.xaml HidusbfModernGui/PadVisualHost.xaml.cs && git commit -m "feat: PadVisualHost - usa el skin instalado o cae al mando vectorial"`

---

### Task 4: Autorar el skin PS5 del usuario (fuera del repo) y calibrarlo

**Files:** ninguno del repo. Se crea `%APPDATA%\UltraPolling\skins\ps5\` con los PNG **copiados** desde `scraped_controller_assets` y un `skin.json` nuevo.

**Nota legal:** esta tarea produce archivos **solo en la máquina del usuario**. Nada de esto se commitea (Task 5 blinda el `.gitignore`).

- [ ] **Step 1: Copiar los assets con nombres limpios** (PowerShell, una vez):

```powershell
$src = "C:\Users\Administrator\Downloads\work ultrapolling\scraped_controller_assets"
$dst = Join-Path $env:APPDATA "UltraPolling\skins\ps5"
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item "$src\02_*.png"                                  (Join-Path $dst "base.png")     -Force
Copy-Item "$src\09_class_custom_stick.png"                 (Join-Path $dst "sticks.png")   -Force
Copy-Item "$src\06_class_custom_class_button_class_pressed.png" (Join-Path $dst "faces.png") -Force
Copy-Item "$src\04_class_custom_class_trigger.png"         (Join-Path $dst "triggers.png") -Force
Copy-Item "$src\05_class_custom_class_bumper.png"          (Join-Path $dst "bumpers.png")  -Force
Copy-Item "$src\07_class_custom_class_back.png"            (Join-Path $dst "share.png")    -Force
Copy-Item "$src\08_class_custom_class_start.png"           (Join-Path $dst "options.png")  -Force
Copy-Item "$src\10_class_custom_class_meta_class_pressed.png" (Join-Path $dst "ps.png")    -Force
Copy-Item "$src\12_class_custom_class_touchpad_class_pressed.png" (Join-Path $dst "touchpad.png") -Force
Get-ChildItem $dst
```

- [ ] **Step 2: Medir la geometría.** Las dimensiones ya conocidas: base 1050×850; `sticks.png` 234×116 (dos mitades de 117×116: izquierda = normal, derecha = pulsado); `faces.png` 211×211 (los 4 botones en diamante: triángulo arriba, cuadrado izquierda, círculo derecha, cruz abajo — cada uno ~71×71 dentro de la hoja); `triggers.png` 241×114 (dos mitades ~120×114); `bumpers.png` 133×48; `share.png`/`options.png` 34×51; `ps.png` 72×57; `touchpad.png` 391×214.

  Para los **destinos** sobre la base hay que localizar cada elemento en `base.png`. Método (no adivinar): abrir `base.png` en Paint (o cualquier visor que muestre coordenadas del cursor) al 100% de zoom y anotar la esquina superior-izquierda donde debe quedar cada recorte. Alternativa programática para los sticks (los círculos negros grandes son inconfundibles): escanear la imagen y quedarse con el centroide de las dos manchas oscuras grandes de la mitad inferior.

  **Estos son los valores de partida** (medidos a ojo sobre el render 1050×850; el modo calibración de la Task 3 sirve para afinarlos en minutos):

  - `stick.left` Dst ≈ (312, 437) 117×116 — `stick.right` Dst ≈ (622, 437) 117×116
  - `btn.triangle` Dst ≈ (795, 263) 71×71 · `btn.square` ≈ (723, 333) · `btn.circle` ≈ (866, 333) · `btn.cross` ≈ (795, 404)
  - `dpad.up` ≈ (205, 283) · `dpad.left` ≈ (148, 340) · `dpad.right` ≈ (262, 340) · `dpad.down` ≈ (205, 396)
  - `btn.share` ≈ (287, 240) 34×51 · `btn.options` ≈ (742, 240) 34×51
  - `btn.ps` ≈ (489, 462) 72×57
  - `btn.touchpad` ≈ (330, 195) 391×214
  - `btn.l1` ≈ (150, 175) · `btn.r1` ≈ (770, 175) (recorte de `bumpers.png`)
  - `btn.l2` ≈ (155, 60) · `btn.r2` ≈ (765, 60) (mitades de `triggers.png`)
  - `StickRadius`: 18 (píxeles de la base que recorre el centro del stick)

- [ ] **Step 3: Escribir `skin.json`** en `%APPDATA%\UltraPolling\skins\ps5\skin.json` con esa estructura. Ejemplo de las partes clave (completar el resto con los valores de arriba):

```json
{
  "Name": "PS5 White (local)",
  "BaseFile": "base.png",
  "BaseWidth": 1050,
  "BaseHeight": 850,
  "StickRadius": 18,
  "Parts": {
    "stick.left":   { "File": "sticks.png",   "Src":  { "X": 0,   "Y": 0,   "W": 117, "H": 116 },
                                              "Src2": { "X": 117, "Y": 0,   "W": 117, "H": 116 },
                                              "Dst":  { "X": 312, "Y": 437, "W": 117, "H": 116 } },
    "stick.right":  { "File": "sticks.png",   "Src":  { "X": 0,   "Y": 0,   "W": 117, "H": 116 },
                                              "Src2": { "X": 117, "Y": 0,   "W": 117, "H": 116 },
                                              "Dst":  { "X": 622, "Y": 437, "W": 117, "H": 116 } },
    "btn.triangle": { "File": "faces.png",    "Src": { "X": 70,  "Y": 0,   "W": 71, "H": 71 },
                                              "Dst": { "X": 795, "Y": 263, "W": 71, "H": 71 } },
    "btn.square":   { "File": "faces.png",    "Src": { "X": 0,   "Y": 70,  "W": 71, "H": 71 },
                                              "Dst": { "X": 723, "Y": 333, "W": 71, "H": 71 } },
    "btn.circle":   { "File": "faces.png",    "Src": { "X": 140, "Y": 70,  "W": 71, "H": 71 },
                                              "Dst": { "X": 866, "Y": 333, "W": 71, "H": 71 } },
    "btn.cross":    { "File": "faces.png",    "Src": { "X": 70,  "Y": 140, "W": 71, "H": 71 },
                                              "Dst": { "X": 795, "Y": 404, "W": 71, "H": 71 } },
    "btn.l2":       { "File": "triggers.png", "Src": { "X": 0,   "Y": 0,   "W": 120, "H": 114 },
                                              "Dst": { "X": 155, "Y": 60,  "W": 120, "H": 114 } },
    "btn.r2":       { "File": "triggers.png", "Src": { "X": 121, "Y": 0,   "W": 120, "H": 114 },
                                              "Dst": { "X": 765, "Y": 60,  "W": 120, "H": 114 } },
    "btn.ps":       { "File": "ps.png",       "Src": { "X": 0,   "Y": 0,   "W": 72,  "H": 57 },
                                              "Dst": { "X": 489, "Y": 462, "W": 72,  "H": 57 } },
    "btn.touchpad": { "File": "touchpad.png", "Src": { "X": 0,   "Y": 0,   "W": 391, "H": 214 },
                                              "Dst": { "X": 330, "Y": 195, "W": 391, "H": 214 } }
  }
}
```

- [ ] **Step 4: Calibrar (el paso que hace que quede bien).** Abrir la app → Configurar el mando → el estado debe decir **"Skin: PS5 White (local)"**. Marcar **Modo calibración**: se dibujan recuadros magenta con la clave de cada parte. Comparar cada recuadro con el elemento real del render y corregir los `Dst` del JSON; **RECARGAR SKIN** aplica sin reiniciar. Repetir hasta que cada recuadro caiga sobre su botón. Después, con el mando conectado: pulsar cada botón y comprobar que se ilumina el correcto; mover los sticks y ajustar `StickRadius` hasta que el recorrido se vea natural (no se salga del hueco ni se quede corto).
- [ ] **Step 5:** No hay commit en esta tarea (nada del repo cambia). Anotar en el reporte los valores finales calibrados, para poder documentarlos.

---

### Task 5: Blindaje legal + documentación

**Files:**
- Modify: `.gitignore`, `README.md`, `docs/DOCUMENTACION.md`

- [ ] **Step 1: `.gitignore`** — añadir al final:

```gitignore
# Skins del visualizador: arte de terceros, NUNCA se redistribuye desde este repo.
skins/
**/skins/
*.skin.json
scraped_controller_assets/
```

- [ ] **Step 2: README** — en la viñeta **Configurar el mando**, tras lo del mando en vivo, añadir:

> El mando en vivo se dibuja con un diseño vectorial propio incluido en la app. Si prefieres una apariencia distinta, puedes instalar un **skin** propio en `%APPDATA%\UltraPolling\skins\<nombre>\` (una imagen base + sus sprites + un `skin.json`); la app lo detecta al abrir y el botón RECARGAR SKIN lo aplica en caliente. Los skins **no se distribuyen con la app**: el arte de un skin es de su autor, y usar o compartir arte ajeno es responsabilidad de quien lo instala.

- [ ] **Step 3: `docs/DOCUMENTACION.md`** — añadir `PadSkin.cs`, `SkinnedPadVisual`, `PadVisualHost` al mapa de módulos; una subsección "Skins del visualizador" con: el formato del manifiesto (coordenadas en píxeles de la base, Src/Src2/Dst), la degradación elegante (parte inválida se descarta, skin inválido → vectorial), el modo calibración, y **la razón legal** de que los skins vivan fuera del repo.
- [ ] **Step 4: Commit** — `git add -u && git commit -m "docs: sistema de skins del visualizador (instalacion, formato y limite legal)"`

---

### Task 6: Verificación integral

- [ ] **Step 1** — `dotnet test` completo: todo verde (incluye los ~8 tests nuevos de `PadSkinTests`).
- [ ] **Step 2** — `.\package.ps1`: termina en "Package ready", sin warnings nuevos, sin dependencias nuevas.
- [ ] **Step 3** — Prueba de regresión SIN skin: renombrar temporalmente `%APPDATA%\UltraPolling\skins` → la app debe arrancar igual, estado "Mando vectorial", visualizador y streamer funcionando como antes.
- [ ] **Step 4** — Prueba de skin roto: dejar un `skin.json` con JSON inválido → la app arranca, cae al vectorial y el estado explica el motivo (no se cuelga ni queda en blanco).
- [ ] **Step 5** — Prueba con hardware: skin PS5 calibrado, todos los botones/sticks/gatillos respondiendo, y el **modo streamer** mostrando el skin con fondo transparente.
- [ ] **Step 6: Commit** — `git add -u && git commit -m "chore: verificacion integral del sistema de skins"` (si algún archivo quedó por commitear; si no, omitir).

---

## Self-review

- **Cobertura del pedido:** usar los recursos PS5 que el usuario señaló (Task 4, con su geometría real medida sobre los assets), integrados en el visualizador (Tasks 2–3), sin el mando "de Nintendo" (el vectorial queda solo como fallback publicable). ✓
- **Restricción legal (motivo del diseño elegido):** el arte va a `%APPDATA%`, el `.gitignore` lo blinda, el README lo explica y la app nunca depende de él. ✓
- **Placeholders:** los valores de geometría de la Task 4 son estimaciones **declaradas como tales**, con un método de medición concreto y un modo calibración para afinarlas — no un "TBD". El resto del plan lleva código completo. ✓
- **Tipos consistentes:** `PadSkin`/`SkinPart`/`SkinRect` (Task 1) consumidos por `SkinnedPadVisual.Load` (Task 2) y `PadVisualHost.ReloadSkin` (Task 3); `Update(ControllerState)` idéntico en `PadVisual`, `SkinnedPadVisual` y `PadVisualHost`, así `VisualizerTick` no cambia; `PadVisualMath.StickOffset` reutilizado. ✓
- **Restricción de tests:** solo `PadSkin.cs` (puro) se linkea; los controles WPF no. ✓
- **Riesgo controlado:** cada punto de fallo del skin (manifiesto, base, sprite, recorte fuera de rango) tiene su camino de degradación probado o defensivo. ✓
