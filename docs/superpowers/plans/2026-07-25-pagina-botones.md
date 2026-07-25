# Página "Asignación de botones": diagrama del mando con líneas guía — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sustituir las 16 listas desplegables de la página de botones por el modelo de *PlayStation Accessories*: el **mando dibujado quieto** en el centro, **una etiqueta por botón** en dos columnas, cada una unida a su botón por una **línea guía**, y la asignación se cambia pulsando la etiqueta. Sustituye la Task 4 del plan `2026-07-25-ui-modelo-playstation.md`, que describía el mismo objetivo sin la imagen ni las coordenadas.

**Architecture:** El diagrama es una **imagen** (line art del DualSense, 2400×1792) dibujada en un `Canvas` de coordenadas fijas dentro de un `Viewbox`. Cada botón tiene un **ancla** en píxeles de esa imagen (tabla medida más abajo); una `Line` va del ancla a su etiqueta, y la etiqueta es un `Button` con estilo píldora. Como el `Canvas` usa el mismo sistema de coordenadas que la imagen, líneas y etiquetas se colocan con números directos y el `Viewbox` escala el conjunto sin que nada se descuadre.

**Tech Stack:** .NET 9 WPF. Sin dependencias nuevas. Sin cambios de núcleo (`RemapSettings.ButtonRemap` ya existe y es lo único que se escribe).

---

## Decisión pendiente de confirmar: dónde vive la imagen

El diagrama es un archivo de imagen, y este repo es **público y MIT**. Mismo criterio que con el skin de Jayraydee: **no metemos arte de terceros al repo**. Dos caminos, y hay que elegir antes de la Task 3:

- **(A) La imagen es del usuario / libre de derechos → se envía con la app.** Va a `HidusbfModernGui/Assets/pad-diagram.png` como `Resource` del proyecto y la página funciona siempre, para todo el mundo.
- **(B) No está claro el origen → se trata como un skin.** La imagen vive fuera del repo, en `%APPDATA%\UltraPolling\skins\<nombre>\diagram.png`, declarada como una parte más del `skin.json`. Si no está, la página cae a un **diagrama vectorial** dibujado con el `PadVisual` que ya existe (puesto en estático), con las mismas anclas escaladas.

**El plan asume (B)** porque es el único camino que no puede salir mal: si luego confirmas que la imagen es tuya, pasar a (A) es mover el archivo y cambiar la ruta. La Task 3 implementa el fallback en cualquier caso — una página que se queda en blanco porque falta un archivo no es aceptable.

## Tabla de anclas (medida sobre la imagen 2400×1792)

Coordenadas del centro de cada botón, en píxeles de la imagen. Verificadas dibujando una cruz sobre cada una y comprobándolas visualmente contra el dibujo.

| Botón (`PadButton`) | Ancla X | Ancla Y | Columna |
|---|---:|---:|---|
| `L2` | 600 | 395 | izquierda |
| `L1` | 600 | 470 | izquierda |
| `Share` | 668 | 710 | izquierda |
| `DpadUp` | 505 | 822 | izquierda |
| `DpadLeft` | 385 | 940 | izquierda |
| `DpadRight` | 645 | 940 | izquierda |
| `DpadDown` | 515 | 1055 | izquierda |
| `L3` | 840 | 1190 | izquierda |
| `R2` | 1780 | 395 | derecha |
| `R1` | 1780 | 470 | derecha |
| `Options` | 1700 | 705 | derecha |
| `Triangle` | 1878 | 768 | derecha |
| `Square` | 1704 | 894 | derecha |
| `Circle` | 2028 | 894 | derecha |
| `Cross` | 1866 | 1032 | derecha |
| `R3` | 1520 | 1190 | derecha |

Son exactamente los 16 botones que `RemappableButtons` ya admite como origen (PS y click del touchpad quedan fuera: el touchpad tiene su propia página).

**Columnas de etiquetas:** las de la izquierda se alinean con su borde derecho en **x = 300**; las de la derecha con su borde izquierdo en **x = 2100**. La Y de cada etiqueta es la de su ancla, salvo que dos queden a menos de 70 px, en cuyo caso se separan (ver Task 2).

---

## Global Constraints

- UI en **español**, tema **monocromo**. La línea guía usa `BorderBrush`; la etiqueta, el estilo píldora nuevo; una etiqueta **con remapeo activo** se resalta con `TextDataBrush` (blanco) para que se vea de un vistazo cuáles están cambiadas.
- **Aplicación en vivo**: elegir un destino escribe en `_remap.ButtonRemap` y llama a `RememberRemap()`. **No hay botón "Aplicar"** (ver la crítica del plan del modelo PlayStation). Sí hay **RESTABLECER**.
- El proyecto de tests linkea fuentes individualmente; lo puro de esta página (`PadDiagram`) se añade al csproj.
- Commits **sin** Co-Authored-By. El push lo hace el usuario.

## Estructura de archivos

- Create: `HidusbfModernGui/PadDiagram.cs` (anclas + colocación de etiquetas, puro)
- Create: `HidusbfModernGui/ButtonPickerPopup.xaml(.cs)` (elegir destino)
- Modify: `HidusbfModernGui/MainWindow.xaml(.cs)` (`PageBotones`)
- Modify: `HidusbfModernGui/Theme.xaml` (estilo `PillButton`)
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj` (+1 link)
- Test: `HidusbfModernGui.Tests/PadDiagramTests.cs`

---

### Task 1: `PadDiagram` — anclas y colocación de etiquetas (TDD)

**Files:**
- Create: `HidusbfModernGui/PadDiagram.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj`
- Test: `HidusbfModernGui.Tests/PadDiagramTests.cs`

**Interfaces:**
- Produces: `readonly record struct PadAnchor(PadButton Button, double X, double Y, bool Left)`; `PadDiagram.Anchors` (los 16, tabla de arriba); `PadDiagram.DiagramWidth`/`DiagramHeight` (2400/1792); `PadDiagram.LabelColumnLeft`/`LabelColumnRight` (300/2100); `PadDiagram.LayoutLabels(IEnumerable<PadAnchor>, double minGap) -> IReadOnlyList<(PadButton Button, double X, double Y)>` — reparte las Y de las etiquetas de una columna evitando solapes.

**Por qué `LayoutLabels` es código y no números a mano:** cuatro anclas de la izquierda caen a menos de 70 px unas de otras (`DpadUp` 822, `DpadLeft` 940, `DpadRight` 940 — dos idénticas) y sus etiquetas se pisarían. Repartirlas es una regla, no una tabla: si mañana se mueve un ancla, el reparto se recalcula solo.

- [ ] **Step 1: Link en el csproj**: `<Compile Include="..\HidusbfModernGui\PadDiagram.cs" Link="PadDiagram.cs" />`

- [ ] **Step 2: Tests que fallan** — crear `PadDiagramTests.cs`:

```csharp
using System.Linq;
using HidusbfModernGui;
using Xunit;

public class PadDiagramTests
{
    [Fact]
    public void Anchors_CoverTheSixteenRemappableButtons()
    {
        Assert.Equal(16, PadDiagram.Anchors.Count);
        Assert.Equal(8, PadDiagram.Anchors.Count(a => a.Left));
        Assert.Equal(8, PadDiagram.Anchors.Count(a => !a.Left));
        Assert.Contains(PadDiagram.Anchors, a => a.Button == PadButton.Cross);
        Assert.Contains(PadDiagram.Anchors, a => a.Button == PadButton.L3);
        // PS y el click del touchpad no se remapean desde aqui.
        Assert.DoesNotContain(PadDiagram.Anchors, a => a.Button == PadButton.PS);
        Assert.DoesNotContain(PadDiagram.Anchors, a => a.Button == PadButton.TouchpadClick);
    }

    [Fact]
    public void Anchors_AreInsideTheImage()
    {
        Assert.All(PadDiagram.Anchors, a =>
        {
            Assert.InRange(a.X, 0, PadDiagram.DiagramWidth);
            Assert.InRange(a.Y, 0, PadDiagram.DiagramHeight);
        });
    }

    [Fact]
    public void Anchors_HaveNoDuplicateButtons()
        => Assert.Equal(PadDiagram.Anchors.Count,
                        PadDiagram.Anchors.Select(a => a.Button).Distinct().Count());

    [Fact]
    public void LayoutLabels_KeepsTheMinimumGap()
    {
        var left = PadDiagram.Anchors.Where(a => a.Left);
        var placed = PadDiagram.LayoutLabels(left, 70).OrderBy(p => p.Y).ToList();

        for (int i = 1; i < placed.Count; i++)
            Assert.True(placed[i].Y - placed[i - 1].Y >= 70 - 0.001,
                        $"{placed[i - 1].Button} y {placed[i].Button} se pisan");
    }

    [Fact]
    public void LayoutLabels_KeepsTheVerticalOrderOfTheAnchors()
    {
        var left = PadDiagram.Anchors.Where(a => a.Left).OrderBy(a => a.Y).Select(a => a.Button).ToList();
        var placed = PadDiagram.LayoutLabels(PadDiagram.Anchors.Where(a => a.Left), 70)
                               .OrderBy(p => p.Y).Select(p => p.Button).ToList();
        Assert.Equal(left, placed);
    }

    [Fact]
    public void LayoutLabels_PutsEachColumnOnItsSide()
    {
        Assert.All(PadDiagram.LayoutLabels(PadDiagram.Anchors.Where(a => a.Left), 70),
                   p => Assert.Equal(PadDiagram.LabelColumnLeft, p.X, 3));
        Assert.All(PadDiagram.LayoutLabels(PadDiagram.Anchors.Where(a => !a.Left), 70),
                   p => Assert.Equal(PadDiagram.LabelColumnRight, p.X, 3));
    }

    [Fact]
    public void LayoutLabels_Empty_ReturnsEmpty()
        => Assert.Empty(PadDiagram.LayoutLabels(System.Array.Empty<PadAnchor>(), 70));
}
```

- [ ] **Step 3: Verificar que fallan** (compilación).

- [ ] **Step 4: Implementación** — crear `HidusbfModernGui/PadDiagram.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace HidusbfModernGui
{
    // Un boton del mando y el punto EXACTO del dibujo donde termina su linea guia, en
    // pixeles de la imagen del diagrama. 'Left' dice a que columna de etiquetas pertenece.
    public readonly record struct PadAnchor(PadButton Button, double X, double Y, bool Left);

    // Geometria de la pagina "Asignacion de botones": donde esta cada boton en el dibujo y
    // donde va su etiqueta. Las anclas se midieron sobre la imagen (2400x1792) dibujando una
    // cruz en cada una y comprobandolas contra el trazo; no son estimaciones a ojo.
    public static class PadDiagram
    {
        public const double DiagramWidth = 2400;
        public const double DiagramHeight = 1792;

        // Borde interior de cada columna de etiquetas, en pixeles del dibujo.
        public const double LabelColumnLeft = 300;
        public const double LabelColumnRight = 2100;

        public static readonly IReadOnlyList<PadAnchor> Anchors = new[]
        {
            new PadAnchor(PadButton.L2,        600,  395,  true),
            new PadAnchor(PadButton.L1,        600,  470,  true),
            new PadAnchor(PadButton.Share,     668,  710,  true),
            new PadAnchor(PadButton.DpadUp,    505,  822,  true),
            new PadAnchor(PadButton.DpadLeft,  385,  940,  true),
            new PadAnchor(PadButton.DpadRight, 645,  940,  true),
            new PadAnchor(PadButton.DpadDown,  515,  1055, true),
            new PadAnchor(PadButton.L3,        840,  1190, true),

            new PadAnchor(PadButton.R2,        1780, 395,  false),
            new PadAnchor(PadButton.R1,        1780, 470,  false),
            new PadAnchor(PadButton.Options,   1700, 705,  false),
            new PadAnchor(PadButton.Triangle,  1878, 768,  false),
            new PadAnchor(PadButton.Square,    1704, 894,  false),
            new PadAnchor(PadButton.Circle,    2028, 894,  false),
            new PadAnchor(PadButton.Cross,     1866, 1032, false),
            new PadAnchor(PadButton.R3,        1520, 1190, false),
        };

        // Reparte las etiquetas de UNA columna en vertical. Cada una quiere estar a la altura
        // de su ancla, pero varias anclas caen casi juntas (DpadLeft y DpadRight comparten Y
        // exacta), asi que se empujan hacia abajo hasta respetar minGap. Se conserva el orden
        // vertical de las anclas: una etiqueta nunca adelanta a otra, o las lineas guia se
        // cruzarian y el dibujo dejaria de leerse.
        public static IReadOnlyList<(PadButton Button, double X, double Y)> LayoutLabels(
            IEnumerable<PadAnchor> anchors, double minGap)
        {
            var sorted = (anchors ?? Enumerable.Empty<PadAnchor>()).OrderBy(a => a.Y).ToList();
            var result = new List<(PadButton, double, double)>(sorted.Count);

            double last = double.NegativeInfinity;
            foreach (var a in sorted)
            {
                double y = Math.Max(a.Y, last + minGap);
                double x = a.Left ? LabelColumnLeft : LabelColumnRight;
                result.Add((a.Button, x, y));
                last = y;
            }
            return result;
        }
    }
}
```

- [ ] **Step 5: Verificar que pasan** (filtro + suite completa).
- [ ] **Step 6: Commit** — `git add -u && git add HidusbfModernGui/PadDiagram.cs && git commit -m "feat: PadDiagram - anclas del mando y reparto de etiquetas (TDD)"`

---

### Task 2: La página BOTONES — diagrama, líneas y etiquetas

**Files:**
- Modify: `HidusbfModernGui/Theme.xaml` (estilo `PillButton`)
- Modify: `HidusbfModernGui/MainWindow.xaml(.cs)`
- Create: `HidusbfModernGui/ButtonPickerPopup.xaml(.cs)`

- [ ] **Step 1: `PillButton`** en `Theme.xaml`: `Button` con `CornerRadius` alto (16), fondo `SurfaceAltBrush`, borde `BorderBrush`, padding 12,6, `FontSize` 11. Estado **remapeado**: se aplica desde código cambiando `Foreground` a `TextDataBrush` y el borde a `TextLabelBrush` (no hay trigger porque el estado no es del control, es del modelo).

- [ ] **Step 2: El lienzo.** Dentro de `PageBotones`, bajo la cabecera que ya existe:

```xml
<Viewbox Stretch="Uniform" MaxHeight="700">
    <Canvas x:Name="DiagramCanvas" Width="2400" Height="1792"/>
</Viewbox>
```

Todo lo demás (imagen, líneas, etiquetas) se crea en código: son 16 grupos y escribirlos a mano en XAML sería ilegible y se desincronizaría de la tabla de anclas.

- [ ] **Step 3: Construir el diagrama** (code-behind), una vez, al entrar a la página por primera vez:

```csharp
// Se construye una sola vez: la imagen y las 16 lineas no cambian, solo el texto de las
// etiquetas (RefreshButtonPills) y su resalte.
private readonly Dictionary<PadButton, Button> _pills = new();
private bool _diagramBuilt;

private void BuildButtonDiagram()
{
    if (_diagramBuilt) return;
    _diagramBuilt = true;

    // Fondo: la imagen del mando si esta disponible; si no, el vectorial en estatico.
    var bg = TryLoadDiagramImage();
    if (bg != null)
    {
        var img = new Image { Source = bg, Width = PadDiagram.DiagramWidth, Height = PadDiagram.DiagramHeight };
        Canvas.SetLeft(img, 0); Canvas.SetTop(img, 0);
        DiagramCanvas.Children.Add(img);
    }
    else
    {
        // Sin imagen no se deja la pagina en blanco: se dibuja el mando vectorial, escalado
        // al lienzo del diagrama, y las anclas siguen valiendo porque son proporcionales.
        var fallback = new PadVisual { Width = PadDiagram.DiagramWidth, Height = PadDiagram.DiagramHeight };
        Canvas.SetLeft(fallback, 0); Canvas.SetTop(fallback, 0);
        DiagramCanvas.Children.Add(fallback);
    }

    foreach (var side in new[] { true, false })
    {
        var anchors = PadDiagram.Anchors.Where(a => a.Left == side).ToList();
        var placed = PadDiagram.LayoutLabels(anchors, 70);

        foreach (var (button, lx, ly) in placed)
        {
            var a = anchors.First(z => z.Button == button);

            // Linea guia: del ancla al borde interior de la columna, a la altura repartida.
            var line = new System.Windows.Shapes.Line
            {
                X1 = a.X, Y1 = a.Y, X2 = lx, Y2 = ly,
                Stroke = (Brush)FindResource("BorderBrush"), StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            DiagramCanvas.Children.Add(line);

            var pill = new Button
            {
                Style = (Style)FindResource("PillButton"),
                Tag = button,
                FontSize = 26,          // el lienzo mide 2400 px: la tipografia va a esa escala
            };
            pill.Click += ButtonPill_Click;
            DiagramCanvas.Children.Add(pill);
            _pills[button] = pill;

            // La etiqueta se ancla por su borde interior: la columna izquierda crece hacia la
            // izquierda, la derecha hacia la derecha. Se centra en vertical sobre su Y.
            pill.Loaded += (_, _) =>
            {
                Canvas.SetLeft(pill, side ? lx - pill.ActualWidth : lx);
                Canvas.SetTop(pill, ly - pill.ActualHeight / 2);
            };
        }
    }

    RefreshButtonPills();
}
```

`TryLoadDiagramImage()` busca `diagram.png` en la carpeta del skin instalado (camino B de la decisión de arriba) y devuelve `null` si no está; nunca lanza.

- [ ] **Step 4: Texto y resalte de las etiquetas.**

```csharp
// Cada etiqueta dice A QUE envia su boton: su propio nombre si no esta remapeado, el
// destino si lo esta (y entonces se resalta). Asi se ve el mapa entero de un vistazo, sin
// pasar el raton por encima ni abrir nada - que es la ventaja del diagrama sobre la lista.
private void RefreshButtonPills()
{
    foreach (var (button, pill) in _pills)
    {
        bool remapped = _remap.ButtonRemap.TryGetValue(button, out var target) && target != button;
        pill.Content = $"{FriendlyName(button)}  ->  {FriendlyName(remapped ? target : button)}";
        pill.Foreground = (Brush)FindResource(remapped ? "TextDataBrush" : "TextLabelBrush");
        pill.BorderBrush = (Brush)FindResource(remapped ? "TextLabelBrush" : "BorderBrush");
    }
}
```

`FriendlyName` reutiliza las etiquetas que ya existen en `RemapTargets` (Cruz, Circulo, Cuadrado, Triangulo, Cruceta arriba…), para no inventar un segundo juego de nombres.

- [ ] **Step 5: Elegir destino.** `ButtonPill_Click` abre `ButtonPickerPopup` anclado a la píldora, con los mismos destinos de `RemapTargets` (incluido "Ninguno" para limpiar). Al elegir:

```csharp
if (target == PadButton.None || target == source) _remap.ButtonRemap.Remove(source);
else _remap.ButtonRemap[source] = target;
RememberRemap();
RefreshButtonPills();
```

- [ ] **Step 6: RESTABLECER.** Un `SecondaryButton` bajo el diagrama: `_remap.ButtonRemap.Clear(); RememberRemap(); RefreshButtonPills();`.
- [ ] **Step 7: Retirar lo viejo.** Eliminar `BuildButtonRemapRows`, el contenedor `BotonRows` y `ButtonRemapCombo_Changed`. El compilador señala lo que quede colgando.
- [ ] **Step 8: Verificación** — build 0/0, suite completa PASS. Manual: las 16 etiquetas salen a los lados sin pisarse y cada línea termina en su botón; cambiar Cruz→Cuadrado resalta esa etiqueta; con el mando virtual activo, pulsar Cruz en el físico dispara Cuadrado en joy.cpl; RESTABLECER deja las 16 en su nombre propio.
- [ ] **Step 9: Commit** — `git add -u && git commit -m "feat(ui): pagina de botones con diagrama del mando y lineas guia"`

---

### Task 3: La imagen y su respaldo

**Files:** `HidusbfModernGui/MainWindow.xaml.cs` (`TryLoadDiagramImage`), `.gitignore`, `docs/DOCUMENTACION.md`

- [ ] **Step 1: Colocar la imagen** (camino B): copiar el archivo a `%APPDATA%\UltraPolling\skins\ps5\diagram.png` y añadir la clave `"diagram"` al `skin.json` como una parte más (solo `File`; no necesita `Src`/`Dst` porque se dibuja entera).
- [ ] **Step 2: `TryLoadDiagramImage`** — resuelve esa ruta con el cargador de skins ya existente; `null` si falta.
- [ ] **Step 3: Probar el respaldo** — renombrar la imagen y comprobar que la página **sigue funcionando** con el mando vectorial: líneas y etiquetas en su sitio, ninguna excepción.
- [ ] **Step 4: Documentar** en `docs/DOCUMENTACION.md`: la tabla de anclas, cómo se midió (rejilla + cruces), y por qué la imagen vive fuera del repo.
- [ ] **Step 5: Commit** — `git add -u && git commit -m "docs: diagrama de botones - anclas, imagen del skin y respaldo vectorial"`

---

## Self-review

- **Cobertura del pedido:** la imagen del usuario es el diagrama (Task 3); las cruces medidas se convierten en la tabla de anclas verificada (Task 1); cada botón tiene su etiqueta unida por una línea al punto exacto (Task 2). ✓
- **Placeholders:** la tabla de anclas son números reales medidos, no estimaciones pendientes; el código puro va completo con tests; lo visual va con estructura y criterio de aceptación. ✓
- **Tipos consistentes:** `PadAnchor`/`PadDiagram.Anchors`/`LayoutLabels` (Task 1) consumidos por `BuildButtonDiagram` (Task 2); `RemapTargets` y `FriendlyName` reutilizan lo que ya existe; `_remap.ButtonRemap` es el único estado que se escribe. ✓
- **Riesgo cubierto:** sin imagen la página no se rompe (respaldo vectorial, Task 2 Step 3 y Task 3 Step 3); el reparto de etiquetas está probado contra el caso real que lo motiva (dos anclas con la misma Y). ✓
- **Decisión abierta y señalada:** de dónde sale la imagen (A o B). El plan asume el camino que no puede salir mal y explica cómo pasar al otro. ✓
