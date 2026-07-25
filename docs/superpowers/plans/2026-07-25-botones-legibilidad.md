# Página de botones, pasada de legibilidad: etiquetas fuera del mando, panel claro e iconos — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Arreglar los tres problemas que el usuario ve en la página de "Asignación de botones" ya construida: (1) las etiquetas y sus líneas caen **encima del mando** y son pequeñas — deben salir fuera de la silueta y ser más grandes; (2) el diagrama es **oscuro sobre un fondo oscuro** y no se despega — debe ser un panel **claro con tinta negra**; (3) las etiquetas dicen `R2 -> R2`, que es redundante y poco intuitivo — deben usar **iconos** del botón.

**Architecture:** Los tres cambios son de presentación; el motor y `RemapSettings` no se tocan. (1) se resuelve ensanchando el lienzo más allá de la imagen y moviendo las columnas de etiquetas a ese margen nuevo — geometría pura, así que va con tests en `PadDiagram`. (2) invierte la imagen (line art: el negativo da trazo oscuro sobre claro) y pinta el panel con colores **que ya están en la paleta** (`TextDataBrush` blanco de fondo, `BgBrush` negro de tinta), sin inventar ningún hex. (3) es una tabla `PadButton -> glifo` pura y testeable.

**Tech Stack:** .NET 9 WPF, xUnit, System.Drawing (solo para generar la imagen invertida, una vez, fuera de la app). Sin dependencias nuevas.

## Contexto verificado (lo que ya existe)

- `PadDiagram`: `Anchors` (16 `PadAnchor(Button,X,Y,Left)` en píxeles de la imagen 2400×1792), `DiagramWidth/Height`, `LabelColumnLeft=300`, `LabelColumnRight=2100`, `LayoutLabels(anchors, minGap)`. Con tests.
- `MainWindow.BuildButtonDiagram()` dibuja: imagen de fondo (o `PadVisual` de respaldo) + una `Line` y un `Button` (estilo `PillButton`, `FontSize=26`) por ancla, dentro de `DiagramCanvas` (2400×1792) en un `Viewbox`.
- `RefreshButtonPills()` pone el texto `origen -> destino` y resalta los remapeados.
- La imagen vive en `%APPDATA%\UltraPolling\skins\ps5\diagram.png` (fuera del repo).
- **Diagnóstico del problema (1):** la silueta del mando ocupa de x≈200 a x≈2200 dentro de la imagen de 2400 de ancho. Las columnas de etiquetas están en x=300 y x=2100, o sea **dentro de la silueta**. No hay sitio: hay que dárselo.

## Global Constraints

- UI en **español**. El panel del diagrama es la **única superficie clara** de la app: es una inversión deliberada para que el dibujo se despegue del fondo negro. **No se inventan colores**: fondo `TextDataBrush` (#FFFFFF) y tinta `BgBrush` (#000000), ambos ya en la paleta.
- El proyecto de tests linkea fuentes individualmente; `PadGlyphs.cs` (nuevo, puro) va al csproj.
- **Aplicación en vivo**: no aparece ningún "Aplicar".
- Commits **sin** Co-Authored-By. El push lo hace el usuario.

---

### Task 1: Geometría — lienzo más ancho y etiquetas fuera del mando (TDD)

**Files:**
- Modify: `HidusbfModernGui/PadDiagram.cs`
- Test: `HidusbfModernGui.Tests/PadDiagramTests.cs`

**Interfaces:**
- Produces: `PadDiagram.CanvasWidth` (3600) y `CanvasHeight` (1792); `PadDiagram.ImageOffsetX` (600, la imagen centrada en el lienzo); `PadDiagram.AnchorX(PadAnchor)` → la X del ancla **en coordenadas del lienzo** (`a.X + ImageOffsetX`). `LabelColumnLeft` pasa de 300 a **540** y `LabelColumnRight` de 2100 a **3060**, ya en coordenadas del lienzo.
- Las `Anchors` **no cambian**: siguen en coordenadas de la imagen, que es donde se midieron. La traducción a lienzo es una suma, en un solo sitio.

**Los números y por qué:** lienzo 3600 = imagen 2400 + 600 de margen a cada lado. Con la imagen a partir de x=600, la silueta del mando ocupa 800..2800 del lienzo. Columna izquierda con su borde derecho en 540 → **260 px de aire** hasta el mando. Columna derecha en 3060 → otros 260. Antes eran **cero** (las etiquetas caían encima de los mangos).

- [ ] **Step 1: Tests que fallan** — añadir a `PadDiagramTests.cs`:

```csharp
[Fact]
public void Canvas_IsWiderThanTheImage_SoLabelsHaveRoom()
{
    Assert.True(PadDiagram.CanvasWidth > PadDiagram.DiagramWidth);
    // La imagen queda centrada en el lienzo.
    Assert.Equal((PadDiagram.CanvasWidth - PadDiagram.DiagramWidth) / 2, PadDiagram.ImageOffsetX, 3);
}

[Fact]
public void LabelColumns_AreOutsideThePadSilhouette()
{
    // La silueta ocupa x 200..2200 DE LA IMAGEN; en el lienzo, +ImageOffsetX.
    double padLeft = 200 + PadDiagram.ImageOffsetX;
    double padRight = 2200 + PadDiagram.ImageOffsetX;
    Assert.True(PadDiagram.LabelColumnLeft < padLeft - 100,
        "la columna izquierda debe quedar bien fuera del mando");
    Assert.True(PadDiagram.LabelColumnRight > padRight + 100,
        "la columna derecha debe quedar bien fuera del mando");
}

[Fact]
public void LabelColumns_AreInsideTheCanvas()
{
    Assert.InRange(PadDiagram.LabelColumnLeft, 0, PadDiagram.CanvasWidth);
    Assert.InRange(PadDiagram.LabelColumnRight, 0, PadDiagram.CanvasWidth);
}

[Fact]
public void AnchorX_TranslatesImageCoordsToCanvas()
{
    var a = PadDiagram.Anchors.First(z => z.Button == PadButton.Cross);
    Assert.Equal(a.X + PadDiagram.ImageOffsetX, PadDiagram.AnchorX(a), 3);
}
```

Añadir `using System.Linq;` al fichero de tests si no está.

- [ ] **Step 2: Verificar que fallan** (compilación).
- [ ] **Step 3: Implementación** — en `PadDiagram.cs`:

```csharp
// El lienzo es MAS ANCHO que la imagen a proposito: la silueta del mando ocupa casi todo
// el ancho de la imagen (x 200..2200 de 2400), asi que las etiquetas no caben dentro sin
// pisarla. El margen extra es donde viven las dos columnas.
public const double CanvasWidth = 3600;
public const double CanvasHeight = 1792;

// La imagen se dibuja centrada en el lienzo; todo lo medido sobre ella se desplaza por aqui.
public const double ImageOffsetX = (CanvasWidth - DiagramWidth) / 2;   // 600

// Borde interior de cada columna, YA en coordenadas del lienzo. Deja ~260 px de aire entre
// la etiqueta y el borde del mando: antes eran 0 y las etiquetas caian sobre los mangos.
public const double LabelColumnLeft = 540;
public const double LabelColumnRight = 3060;

// Las anclas se midieron sobre la IMAGEN; el dibujo vive en el LIENZO. Una sola suma, en un
// solo sitio, para que no haya dos sistemas de coordenadas sueltos por el code-behind.
public static double AnchorX(PadAnchor a) => a.X + ImageOffsetX;
```

- [ ] **Step 4: Verificar que pasan** — filtro `PadDiagramTests` + suite completa.
- [ ] **Step 5: Commit** — `git add -u && git commit -m "feat: el diagrama gana margen lateral para que las etiquetas salgan del mando (TDD)"`

---

### Task 2: Iconos vectoriales de botón (TDD)

**Files:**
- Create: `HidusbfModernGui/PadIcons.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj`
- Test: `HidusbfModernGui.Tests/PadIconsTests.cs`

**Interfaces:**
- Produces: `PadIcons.PathOf(PadButton) -> string?` (los datos de un `Geometry` en un lienzo de 24×24, o `null` si ese botón se representa con texto); `PadIcons.TextOf(PadButton) -> string?` (`"L1"`, `"R2"`, `"L3"`… o `null` si tiene icono); `PadIcons.IsFilledBadge(PadButton) -> bool` (true en las cuatro caras: van dentro de un círculo relleno con el símbolo calado, como la referencia). Consumido por `RefreshButtonPills`.

**Por qué vectores y no caracteres Unicode:** `✕ ○ □ △` dependen de la fuente instalada, se ven de tamaños distintos entre sí y no se parecen a los del mando. Un `Geometry` se dibuja igual en cualquier equipo, escala sin perder filo y se pinta con el color del tema. Las referencias que dio el usuario son formas, no texto.

**Reparto:** caras (`✕ ○ □ △`) y cruceta (4 direcciones) llevan **icono**; hombros, gatillos y sticks (`L1 R1 L2 R2 L3 R3`) llevan **texto**, porque en el mando real están serigrafiados así — dibujarlos como forma sería inventar un icono que nadie reconoce. `Share`/`Options` llevan icono (sus símbolos serigrafiados).

**Los de texto no son texto normal: son "keycaps".** Un `L1` escrito con la tipografía de la interfaz se leería como una palabra más de la pantalla, mientras que a su lado las caras son insignias redondas. Para que **pesen lo mismo**, los de texto van en **mono, en negrita, con espaciado entre letras y dentro de un recuadro de esquinas redondeadas** — como la serigrafía de un mando o la tecla de un teclado. Así la fila entera se lee como una hilera de botones, no como iconos mezclados con frases:

- Fuente: `MonoFont` (la que el tema ya usa para datos), `FontWeight="Black"`, `FontSize` 30.
- Recuadro: `CornerRadius` 8, borde de 2.5 px en el color de tinta, `Padding` 12,5.
- Nunca hereda el tamaño ni el peso del resto de la página: se fija aquí.

- [ ] **Step 1: Link en el csproj**: `<Compile Include="..\HidusbfModernGui\PadIcons.cs" Link="PadIcons.cs" />`
- [ ] **Step 2: Tests que fallan** — crear `PadIconsTests.cs`:

```csharp
using System;
using System.Linq;
using HidusbfModernGui;
using Xunit;

public class PadIconsTests
{
    [Fact]
    public void EveryButton_HasEitherAPathOrText()
    {
        foreach (PadButton b in Enum.GetValues<PadButton>())
        {
            if (b == PadButton.None) continue;
            bool hasPath = !string.IsNullOrWhiteSpace(PadIcons.PathOf(b));
            bool hasText = !string.IsNullOrWhiteSpace(PadIcons.TextOf(b));
            Assert.True(hasPath ^ hasText, $"{b} debe tener icono O texto, no ambos ni ninguno");
        }
    }

    [Fact]
    public void FaceAndDpad_UseIcons()
    {
        foreach (var b in new[] { PadButton.Cross, PadButton.Circle, PadButton.Square, PadButton.Triangle,
                                  PadButton.DpadUp, PadButton.DpadDown, PadButton.DpadLeft, PadButton.DpadRight })
            Assert.False(string.IsNullOrWhiteSpace(PadIcons.PathOf(b)), $"{b} sin icono");
    }

    [Fact]
    public void ShouldersTriggersAndSticks_UseTheirPrintedText()
    {
        Assert.Equal("L1", PadIcons.TextOf(PadButton.L1));
        Assert.Equal("R2", PadIcons.TextOf(PadButton.R2));
        Assert.Equal("L3", PadIcons.TextOf(PadButton.L3));
        Assert.Null(PadIcons.PathOf(PadButton.L1));
    }

    [Fact]
    public void OnlyFaceButtons_AreFilledBadges()
    {
        foreach (var b in new[] { PadButton.Cross, PadButton.Circle, PadButton.Square, PadButton.Triangle })
            Assert.True(PadIcons.IsFilledBadge(b), $"{b} deberia ir en circulo relleno");
        foreach (var b in new[] { PadButton.DpadUp, PadButton.L1, PadButton.Share })
            Assert.False(PadIcons.IsFilledBadge(b), $"{b} no lleva circulo");
    }

    [Fact]
    public void Dpad_DirectionsAreFourDistinctShapes()
    {
        var paths = new[] { PadButton.DpadUp, PadButton.DpadDown, PadButton.DpadLeft, PadButton.DpadRight }
                    .Select(PadIcons.PathOf).ToList();
        Assert.Equal(4, paths.Distinct().Count());
    }

    [Fact]
    public void None_HasNeither()
    {
        Assert.Null(PadIcons.PathOf(PadButton.None));
        Assert.Null(PadIcons.TextOf(PadButton.None));
    }
}
```

- [ ] **Step 3: Verificar que falla** (compilación).
- [ ] **Step 4: Implementación** — crear `HidusbfModernGui/PadIcons.cs`:

```csharp
namespace HidusbfModernGui
{
    // El icono de cada boton, como datos de Geometry sobre un lienzo de 24x24. Vectores y no
    // caracteres Unicode: los glifos tipo "△" dependen de la fuente instalada, salen de
    // tamanos distintos entre si y no se parecen a los del mando. Un Geometry se dibuja igual
    // en cualquier equipo, escala sin perder filo y toma el color del tema.
    //
    // Reparto: caras y cruceta llevan forma; hombros, gatillos y sticks llevan TEXTO, porque
    // en el mando real estan serigrafiados "L1"/"R2"/"L3" - dibujarlos como simbolo seria
    // inventar un icono que nadie reconoce.
    public static class PadIcons
    {
        // Petalo de la cruceta apuntando ARRIBA, en 24x24: rectangulo de esquinas redondeadas
        // que termina en punta hacia el centro del mando. Las otras tres direcciones son la
        // misma forma girada 90/180/270 grados, escrita ya rotada para no depender de
        // transformaciones en la vista.
        private const string DpadUp    = "M9,2 H15 A2,2 0 0 1 17,4 V13 L12,18 L7,13 V4 A2,2 0 0 1 9,2 Z";
        private const string DpadDown  = "M9,22 H15 A2,2 0 0 0 17,20 V11 L12,6 L7,11 V20 A2,2 0 0 0 9,22 Z";
        private const string DpadLeft  = "M2,9 V15 A2,2 0 0 0 4,17 H13 L18,12 L13,7 H4 A2,2 0 0 0 2,9 Z";
        private const string DpadRight = "M22,9 V15 A2,2 0 0 1 20,17 H11 L6,12 L11,7 H20 A2,2 0 0 1 22,9 Z";

        // Simbolos de las caras. Van CALADOS sobre un circulo relleno (IsFilledBadge), como
        // en el mando: el trazo es el hueco, no la figura.
        private const string Cross    = "M7,7 L17,17 M17,7 L7,17";
        private const string Circle   = "M12,12 m-5,0 a5,5 0 1,0 10,0 a5,5 0 1,0 -10,0";
        private const string Square   = "M7.5,7.5 H16.5 V16.5 H7.5 Z";
        private const string Triangle = "M12,6.5 L17.5,16.5 H6.5 Z";

        // Share (dos rectangulos superpuestos) y Options (tres lineas), sus serigrafias.
        private const string Share   = "M5,9 H14 V19 H5 Z M10,5 H19 V15 H16";
        private const string Options = "M5,8 H19 M5,12 H19 M5,16 H19";

        public static string? PathOf(PadButton b) => b switch
        {
            PadButton.Cross     => Cross,
            PadButton.Circle    => Circle,
            PadButton.Square    => Square,
            PadButton.Triangle  => Triangle,
            PadButton.DpadUp    => DpadUp,
            PadButton.DpadDown  => DpadDown,
            PadButton.DpadLeft  => DpadLeft,
            PadButton.DpadRight => DpadRight,
            PadButton.Share     => Share,
            PadButton.Options   => Options,
            PadButton.TouchpadClick => "M3,7 H21 V17 H3 Z",
            _ => null,
        };

        public static string? TextOf(PadButton b) => b switch
        {
            PadButton.L1 => "L1",
            PadButton.R1 => "R1",
            PadButton.L2 => "L2",
            PadButton.R2 => "R2",
            PadButton.L3 => "L3",
            PadButton.R3 => "R3",
            PadButton.PS => "PS",
            _ => null,
        };

        // Las cuatro caras se dibujan como simbolo calado dentro de un circulo relleno; el
        // resto va suelto sobre el panel.
        public static bool IsFilledBadge(PadButton b) =>
            b is PadButton.Cross or PadButton.Circle or PadButton.Square or PadButton.Triangle;
    }
}
```

- [ ] **Step 5: Verificar que pasan** + suite completa.
- [ ] **Step 6: Revisión visual de las formas.** Los datos de arriba están escritos a mano y **no** se comprueban con un test (un test puede decir que la cadena existe, no que la flecha se vea como una flecha). Renderizar los 16 botones en una fila temporal dentro de la página, capturar y comparar con las referencias del usuario. Dos cosas que hay que mirar expresamente:
  - cada forma se reconoce (la flecha parece flecha, el triángulo va centrado en su círculo);
  - **los keycaps y las insignias redondas tienen el mismo peso visual** — si los `L1`/`R2` se ven más flojos que las caras, subir el grosor del borde o el tamaño hasta emparejarlos. Ese equilibrio es el objetivo del cambio, y solo se juzga mirándolo.

  **Este paso no se salta**: es el único que valida la forma.
- [ ] **Step 7: Commit** — `git add -u && git add HidusbfModernGui/PadIcons.cs && git commit -m "feat: PadIcons - iconos vectoriales por boton (TDD + revision visual)"`

---

### Task 3: Panel claro, etiquetas grandes y con iconos

**Files:**
- Modify: `HidusbfModernGui/Theme.xaml` (estilo `PillButtonInk`)
- Modify: `HidusbfModernGui/MainWindow.xaml(.cs)`
- Nuevo asset local: `%APPDATA%\UltraPolling\skins\ps5\diagram.png` **invertido**

- [ ] **Step 1: Invertir la imagen.** Script de una sola vez (scratchpad, no entra al repo) que aplica el negativo fotométrico con `ColorMatrix` y guarda encima de `diagram.png` (haciendo antes copia `.orig`). Es line art: el negativo convierte "trazo gris claro sobre casi negro" en "trazo gris oscuro sobre casi blanco", que es justo lo que pide el panel claro.

```powershell
Add-Type -AssemblyName System.Drawing
$d = Join-Path $env:APPDATA "UltraPolling\skins\ps5"
$src = Join-Path $d "diagram.png"
Copy-Item $src (Join-Path $d "diagram.orig.png") -Force
$img = [System.Drawing.Image]::FromFile($src)
$bmp = New-Object System.Drawing.Bitmap $img.Width, $img.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$m = New-Object System.Drawing.Imaging.ColorMatrix
$m.Matrix00 = -1; $m.Matrix11 = -1; $m.Matrix22 = -1; $m.Matrix33 = 1
$m.Matrix40 = 1;  $m.Matrix41 = 1;  $m.Matrix42 = 1
$attr = New-Object System.Drawing.Imaging.ImageAttributes
$attr.SetColorMatrix($m)
$rect = New-Object System.Drawing.Rectangle 0, 0, $img.Width, $img.Height
$g.DrawImage($img, $rect, 0, 0, $img.Width, $img.Height, [System.Drawing.GraphicsUnit]::Pixel, $attr)
$g.Dispose(); $img.Dispose()
$bmp.Save($src, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
```

- [ ] **Step 2: El panel claro.** En `MainWindow.xaml`, envolver el `Viewbox` del diagrama en un `Border` con `Background="{StaticResource TextDataBrush}"` (blanco, ya en la paleta), `CornerRadius="12"`, `Padding="12"`. **No se añade ningún color nuevo al tema**: es el blanco que ya se usa para los datos, aquí como superficie.
- [ ] **Step 3: `PillButtonInk`** en `Theme.xaml`: igual que `PillButton` pero para fondo claro — fondo transparente, borde `BgBrush` 2px, `Foreground` `BgBrush`, `CornerRadius` 20, `Padding` 16,8; al pasar el ratón, fondo `BgBrush` y texto `TextDataBrush` (se invierte, que sobre blanco es el resalte natural).
- [ ] **Step 4: Etiquetas más grandes y fuera.** En `BuildButtonDiagram`:
  - `DiagramCanvas` pasa a `Width="{x:Static local:PadDiagram.CanvasWidth}"`… o más simple: fijar `DiagramCanvas.Width = PadDiagram.CanvasWidth; DiagramCanvas.Height = PadDiagram.CanvasHeight;` al construir.
  - La imagen se dibuja en `Canvas.SetLeft(img, PadDiagram.ImageOffsetX)`.
  - Las líneas parten de `PadDiagram.AnchorX(a)` (no de `a.X`).
  - `FontSize` de la píldora: **26 → 44**; estilo `PillButtonInk`.
  - `LayoutLabels(..., minGap)` sube de **70 → 110** (la letra es más grande, necesita más aire).
  - Las líneas guía pasan a `Stroke = BgBrush` (negro sobre el panel blanco), `StrokeThickness = 4`.
- [ ] **Step 5: Iconos dentro de la etiqueta.** El contenido de la píldora deja de ser texto y pasa a ser un `StackPanel` horizontal construido por un helper:

```csharp
// Contenido de una etiqueta: [icono origen]  (->  [icono destino])
// Sin remapeo se muestra SOLO el origen: repetir "R2 -> R2" era pedirle al usuario que
// comparase dos cadenas para deducir que no pasa nada.
private UIElement BuildPillContent(PadButton source, PadButton? target)
{
    var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    row.Children.Add(BuildIcon(source));
    if (target != null)
    {
        row.Children.Add(new TextBlock { Text = "→", Margin = new Thickness(10, 0, 10, 0),
                                         FontSize = 32, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(BuildIcon(target.Value));
    }
    return row;
}

// Un icono: la forma vectorial, o el texto serigrafiado si ese boton no tiene forma. Las
// cuatro caras van CALADAS sobre un circulo relleno, como en el mando: dentro del circulo
// el simbolo se dibuja con el color del panel, no con el de la tinta.
private UIElement BuildIcon(PadButton b)
{
    string? path = PadIcons.PathOf(b);
    if (path == null)
    {
        // "Keycap": L1/R2/L3 no son texto corrido, son la serigrafia de un boton. Mono +
        // Black + espaciado + recuadro para que pesen lo mismo que las insignias redondas de
        // las caras; con la tipografia de la interfaz se leerian como una palabra mas.
        var cap = new TextBlock
        {
            Text = PadIcons.TextOf(b) ?? "—",
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = 30,
            FontWeight = FontWeights.Black,
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Espaciado entre letras: separa la "L" del "1" sin tocar el ancho del recuadro.
        System.Windows.Documents.TypographyProperties.SetStandardLigatures(cap, false);
        cap.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);

        return new Border
        {
            Child = cap,
            BorderBrush = Ink,
            BorderThickness = new Thickness(2.5),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 5, 12, 5),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    var shape = new System.Windows.Shapes.Path
    {
        Data = Geometry.Parse(path),
        Stretch = Stretch.Uniform, Width = 34, Height = 34,
        Stroke = PadIcons.IsFilledBadge(b) ? Paper : Ink,
        StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
    };
    if (!PadIcons.IsFilledBadge(b)) { shape.Fill = Ink; shape.Stroke = null; }

    if (!PadIcons.IsFilledBadge(b)) return shape;

    // Cara: circulo relleno + simbolo calado encima.
    var grid = new Grid { Width = 48, Height = 48 };
    grid.Children.Add(new System.Windows.Shapes.Ellipse { Fill = Ink });
    grid.Children.Add(shape);
    return grid;
}
```

`Ink` y `Paper` son los dos pinceles que ya decide la bandera `light` del Step 6 (`BgBrush`/`TextDataBrush` sobre panel claro, y al revés sobre el oscuro), así que los iconos funcionan igual en los dos casos sin código duplicado.

El resalte de remapeado se mantiene: remapeado = borde más grueso en la píldora; sin remapear = borde fino. (Sobre panel claro no vale jugar con el blanco.)

- [ ] **Step 6: El respaldo vectorial sigue vivo.** `PadVisual` está pensado para fondo oscuro; sobre el panel blanco se vería mal. Si no hay imagen, **el panel se pinta oscuro** (`SurfaceBrush`) y las píldoras usan el `PillButton` de siempre. Una sola bandera (`bool light = bg != null`) decide fondo, estilo de píldora y color de línea, para que no haya combinaciones imposibles.
- [ ] **Step 7: Verificación** — build 0/0, suite completa PASS. Manual: las etiquetas quedan **fuera** de la silueta, se leen a tamaño cómodo, el panel es claro con tinta negra, y una etiqueta sin remapeo muestra solo su glifo. Renombrar `diagram.png` y comprobar que el respaldo oscuro sigue siendo coherente.
- [ ] **Step 8: Commit** — `git add -u && git commit -m "feat(ui): diagrama sobre panel claro, etiquetas fuera del mando, mas grandes y con iconos"`

---

## Self-review

- **Cobertura del pedido:** etiquetas más separadas y más grandes (Tasks 1 y 3: margen de 260 px y fuente 26→44); panel claro con letras negras (Task 3, invirtiendo la imagen y usando blanco/negro **de la paleta existente**); iconos en vez de `R2 -> R2` (Task 2 + Task 3 Step 5). ✓
- **Placeholders:** ninguno; la geometría son números justificados, los glifos una tabla cerrada, y el script de inversión va completo. ✓
- **Tipos consistentes:** `PadDiagram.CanvasWidth/ImageOffsetX/AnchorX` (Task 1) consumidos en Task 3; `PadGlyphs.Of` (Task 2) consumido en `RefreshButtonPills` (Task 3). Las `Anchors` medidas **no se tocan**. ✓
- **Sin colores nuevos:** el panel claro reutiliza `TextDataBrush`/`BgBrush`. La disciplina del tema se mantiene. ✓
- **Riesgo cubierto:** el respaldo sin imagen no queda en un estado mixto (panel claro con mando oscuro) porque una sola bandera gobierna las tres decisiones (Task 3 Step 6). ✓
