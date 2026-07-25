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

### Task 2: Iconos en las etiquetas (TDD)

**Files:**
- Create: `HidusbfModernGui/PadGlyphs.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj`
- Test: `HidusbfModernGui.Tests/PadGlyphsTests.cs`

**Interfaces:**
- Produces: `PadGlyphs.Of(PadButton) -> string` — el símbolo de cada botón: `✕ ○ □ △` para las caras, `▲ ▼ ◀ ▶` para la cruceta, y el propio nombre corto (`L1`, `R2`, `L3`, …) para hombros, gatillos y sticks, que ya son iconográficos de por sí. `Share`/`Options` usan `⧉` y `≡` (los símbolos que llevan serigrafiados). Consumido por `RefreshButtonPills`.

**Por qué una tabla y no texto:** `R2 -> R2` obliga a leer dos veces lo mismo para enterarte de que **no** hay remapeo. Con glifos, `△ → ✕` se entiende de un vistazo y sin idioma de por medio.

- [ ] **Step 1: Link en el csproj**: `<Compile Include="..\HidusbfModernGui\PadGlyphs.cs" Link="PadGlyphs.cs" />`
- [ ] **Step 2: Tests que fallan** — crear `PadGlyphsTests.cs`:

```csharp
using System;
using System.Linq;
using HidusbfModernGui;
using Xunit;

public class PadGlyphsTests
{
    [Fact]
    public void EveryButtonHasANonEmptyGlyph()
    {
        foreach (PadButton b in Enum.GetValues<PadButton>())
            Assert.False(string.IsNullOrWhiteSpace(PadGlyphs.Of(b)), $"{b} sin glifo");
    }

    [Fact]
    public void FaceButtons_UseTheirSymbols()
    {
        Assert.Equal("✕", PadGlyphs.Of(PadButton.Cross));
        Assert.Equal("○", PadGlyphs.Of(PadButton.Circle));
        Assert.Equal("□", PadGlyphs.Of(PadButton.Square));
        Assert.Equal("△", PadGlyphs.Of(PadButton.Triangle));
    }

    [Fact]
    public void Dpad_UsesArrows()
    {
        Assert.Equal("▲", PadGlyphs.Of(PadButton.DpadUp));
        Assert.Equal("▼", PadGlyphs.Of(PadButton.DpadDown));
        Assert.Equal("◀", PadGlyphs.Of(PadButton.DpadLeft));
        Assert.Equal("▶", PadGlyphs.Of(PadButton.DpadRight));
    }

    [Fact]
    public void ShouldersAndSticks_KeepTheirShortName()
    {
        Assert.Equal("L1", PadGlyphs.Of(PadButton.L1));
        Assert.Equal("R2", PadGlyphs.Of(PadButton.R2));
        Assert.Equal("L3", PadGlyphs.Of(PadButton.L3));
    }

    [Fact]
    public void GlyphsAreDistinctPerButton()
    {
        var all = Enum.GetValues<PadButton>().Where(b => b != PadButton.None)
                      .Select(PadGlyphs.Of).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
```

- [ ] **Step 3: Verificar que falla** (compilación).
- [ ] **Step 4: Implementación** — crear `HidusbfModernGui/PadGlyphs.cs`:

```csharp
namespace HidusbfModernGui
{
    // El simbolo de cada boton, para etiquetas que se leen de un vistazo. "R2 -> R2" obliga
    // a leer dos veces lo mismo para deducir que NO hay remapeo; un glifo se reconoce solo.
    // Hombros, gatillos y sticks se quedan con su nombre corto: L1/R2/L3 ya son iconos.
    public static class PadGlyphs
    {
        public static string Of(PadButton b) => b switch
        {
            PadButton.Cross         => "✕",
            PadButton.Circle        => "○",
            PadButton.Square        => "□",
            PadButton.Triangle      => "△",
            PadButton.DpadUp        => "▲",
            PadButton.DpadDown      => "▼",
            PadButton.DpadLeft      => "◀",
            PadButton.DpadRight     => "▶",
            PadButton.L1            => "L1",
            PadButton.R1            => "R1",
            PadButton.L2            => "L2",
            PadButton.R2            => "R2",
            PadButton.L3            => "L3",
            PadButton.R3            => "R3",
            PadButton.Share         => "⧉",
            PadButton.Options       => "≡",
            PadButton.PS            => "PS",
            PadButton.TouchpadClick => "▭",
            _                       => "—",   // None: "sin asignar"
        };
    }
}
```

- [ ] **Step 5: Verificar que pasan** + suite completa.
- [ ] **Step 6: Commit** — `git add -u && git add HidusbfModernGui/PadGlyphs.cs && git commit -m "feat: PadGlyphs - simbolo por boton para las etiquetas (TDD)"`

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
- [ ] **Step 5: Iconos en el texto.** `RefreshButtonPills` pasa a:

```csharp
// Glifo del origen -> glifo del destino. Sin remapeo se muestra solo el origen: repetir
// "R2 -> R2" era pedirle al usuario que comparase dos cadenas para deducir que no pasa nada.
pill.Content = remapped
    ? $"{PadGlyphs.Of(button)}  →  {PadGlyphs.Of(target)}"
    : PadGlyphs.Of(button);
```

El resalte de remapeado se mantiene, pero en versión tinta: remapeado = `FontWeight.Bold` y borde más grueso; sin remapear = normal. (Sobre blanco no vale jugar con `TextDataBrush`.)

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
