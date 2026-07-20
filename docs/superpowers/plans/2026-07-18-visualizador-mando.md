# Visualizador de mando PS5 en vivo + modo streamer + arreglo del feedback de la curva — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Un mando DualSense dibujado nativamente en WPF, en el centro del configurador, que se mueve en tiempo real reflejando la **salida transformada** (lo que recibe el juego). Un **modo streamer** que lo saca en una ventana overlay de fondo transparente, siempre-encima, con opciones. Y el arreglo del bug de feedback de la curva: un punto vivo recorriendo la gráfica + una línea de referencia lineal, para que por fin se VEA qué hacen los 3 puntos y en qué se diferencia Editor de Lineal.

**Architecture:** El visualizador se alimenta de un **feed único** (`VisualizerFeed`): un `DispatcherTimer` a ~60 fps toma un `ControllerState` físico y le aplica `RemapEngine.Transform(_remap)` — la MISMA función pura del motor, así lo que ves es exactamente lo que emite el DS4 virtual. La fuente del estado físico es el lector del motor si está activo, o un lector propio de solo-lectura si no (el físico está visible cuando el motor está apagado). El dibujo es un `UserControl` nativo (`PadVisual`) con partes nombradas que un método `Update(state)` mueve; la aritmética fina (posición del pulgar acotada al radio, relleno de gatillo) vive en un helper puro y testeado. El modo streamer reusa el mismo `PadVisual` y el mismo feed en una `Window` transparente. Cero WebView2, cero internet, cero dependencias nuevas: encaja en el portable de un solo archivo y en el tema monocromo.

**Tech Stack:** .NET 9 WPF, xUnit. Sin librerías nuevas.

## Global Constraints

- UI en **español**, tema **monocromo**. Excepción de color ya existente: los 3 puntos del editor (verde/ámbar/rojo) y ahora el resaltado de botones pulsados del visualizador (un gris claro/blanco, no color nuevo).
- El proyecto de tests **linkea fuentes individualmente** (`HidusbfModernGui.Tests.csproj`): **`PadVisualMath.cs` (nuevo, puro) debe añadirse ahí**. Nada que toque WPF/HidSharp/Nefarius puede linkearse a tests — por eso `PadVisual`, `VisualizerFeed` y la ventana streamer se verifican a mano.
- El feed reutiliza `RemapEngine.Transform` y `DualSenseReader` tal como están (no se tocan). `DualSenseReader.FindUsbDualSense` ya filtra por `PID_0CE6` (nunca el virtual).
- **Lección L1 (crítica):** el arranque del motor oculta el físico y **reinicia el devnode**, lo que expulsa TODO handle abierto. Si el visualizador tiene su propio lector abierto cuando el motor arranca, ese handle muere. Por eso el feed **cede la fuente** al lector del motor mientras el motor está activo, y solo abre lector propio cuando el motor está apagado.
- El visualizador y el streamer corren a ~60 fps SOLO cuando son visibles (página de configurar visible y/o ventana streamer abierta): nada de CPU en segundo plano.
- Commits **sin** Co-Authored-By. El push lo hace el usuario. Identidad git ya configurada.

## Contexto del código actual (verificado)

- `ControllerState` (sticks -1..1 con Y=arriba positivo; `L2`/`R2` 0..1; `HashSet<PadButton> Pressed`; `TouchActive/TouchX/TouchY`).
- `RemapEngine.Transform(ControllerState, RemapSettings) -> ControllerState` — puro.
- Motor en `MainWindow.xaml.cs`: `_padReader` (DualSenseReader), `_engineRunning`, `EngineTick` (DispatcherTimer 8 ms que ya hace `virt.Push(RemapEngine.Transform(reader.Snapshot(), _remap))`), `StartEngine`/`StopEngine`, `CleanupEngine`.
- Configurador: `ConfigPanel` con la tarjeta "MANDO VIRTUAL" arriba, luego los botones de pestaña STICKS/GATILLOS/TOUCHPAD/BOTONES y sus `Grid` `TabSticks`/`TabGatillos`/`TabTouchpad`/`TabBotones`.
- Curva: por stick, `LeftCurveCanvas`/`RightCurveCanvas` (220×100) con `LeftCurveLine`/`RightCurveLine` (Polyline), `RedrawLeftCurve`/`RedrawRightCurve`, `DrawCurve`, `RefreshCurveDots`, `DomainToRaw`/`RawToDomain`. La curva Editor por defecto son 5 puntos en diagonal → se dibuja idéntica a Lineal (raíz de la confusión).

## Estructura de archivos

- Create: `HidusbfModernGui/PadVisualMath.cs` (puro, testeable)
- Create: `HidusbfModernGui/PadVisual.xaml` + `.xaml.cs` (UserControl del mando)
- Create: `HidusbfModernGui/VisualizerFeed.cs` (feed ~60 fps con coordinación de fuente)
- Create: `HidusbfModernGui/StreamerWindow.xaml` + `.xaml.cs` (overlay transparente)
- Modify: `HidusbfModernGui/MainWindow.xaml` (host del visualizador en ConfigPanel + botón/opciones streamer + referencia diagonal en los canvas de curva)
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (arranque/parada del feed, coordinación con el motor, punto vivo en la curva, streamer)
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj` (link de PadVisualMath)
- Test: `HidusbfModernGui.Tests/PadVisualMathTests.cs`
- Modify: `README.md`, `docs/DOCUMENTACION.md`

---

### Task 1: `PadVisualMath` — aritmética pura del visualizador (TDD)

**Files:**
- Create: `HidusbfModernGui/PadVisualMath.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj`
- Test: `HidusbfModernGui.Tests/PadVisualMathTests.cs`

**Interfaces:**
- Produces: `PadVisualMath.StickOffset(double x, double y, double radius) -> (double Dx, double Dy)` (desplazamiento en píxeles del pulgar dentro del pozo del stick; Y en convención de PANTALLA: +Dy hacia abajo; magnitud acotada a `radius` para que el pulgar nunca salga del pozo). `PadVisualMath.Fill01(double v) -> double` (clamp 0..1, para barras de gatillo). Consumido por `PadVisual` (Task 2) y el punto vivo de la curva (Task 4).

- [ ] **Step 1: Añadir el link al csproj** (antes de escribir tests):

```xml
<Compile Include="..\HidusbfModernGui\PadVisualMath.cs" Link="PadVisualMath.cs" />
```

- [ ] **Step 2: Tests que fallan** — crear `PadVisualMathTests.cs`:

```csharp
using HidusbfModernGui;
using Xunit;

public class PadVisualMathTests
{
    [Fact]
    public void StickOffset_Centered_IsZero()
    {
        var (dx, dy) = PadVisualMath.StickOffset(0, 0, 30);
        Assert.Equal(0.0, dx, 3);
        Assert.Equal(0.0, dy, 3);
    }

    [Fact]
    public void StickOffset_FullRight_IsRadiusRight()
    {
        var (dx, dy) = PadVisualMath.StickOffset(1, 0, 30);
        Assert.Equal(30.0, dx, 3);
        Assert.Equal(0.0, dy, 3);
    }

    [Fact]
    public void StickOffset_FullUp_IsRadiusUp_ScreenYInverted()
    {
        // Y=+1 es "arriba" en ControllerState; en pantalla arriba es -Dy.
        var (dx, dy) = PadVisualMath.StickOffset(0, 1, 30);
        Assert.Equal(0.0, dx, 3);
        Assert.Equal(-30.0, dy, 3);
    }

    [Fact]
    public void StickOffset_DiagonalOverMagnitude_ClampedToRadius()
    {
        // (1,1) tiene magnitud 1.414; el pulgar no puede salir del pozo: se acota a radius.
        var (dx, dy) = PadVisualMath.StickOffset(1, 1, 30);
        double mag = System.Math.Sqrt(dx * dx + dy * dy);
        Assert.Equal(30.0, mag, 2);
        Assert.Equal(dx, -dy, 3);   // 45° se mantiene (Dy invertido)
    }

    [Fact]
    public void Fill01_Clamps()
    {
        Assert.Equal(0.0, PadVisualMath.Fill01(-0.5), 3);
        Assert.Equal(1.0, PadVisualMath.Fill01(1.5), 3);
        Assert.Equal(0.4, PadVisualMath.Fill01(0.4), 3);
    }
}
```

- [ ] **Step 3: Verificar que fallan** — `dotnet test HidusbfModernGui.Tests\HidusbfModernGui.Tests.csproj --filter "FullyQualifiedName~PadVisualMathTests"`. Esperado: compile error.

- [ ] **Step 4: Implementación** — crear `HidusbfModernGui/PadVisualMath.cs`:

```csharp
using System;

namespace HidusbfModernGui
{
    // Aritmetica pura del visualizador del mando: sin WPF, sin hardware. Convierte el estado
    // normalizado (-1..1 / 0..1) a numeros de dibujo. El control PadVisual y el punto vivo de
    // la curva la comparten; testeada aqui para no depender de inspeccion visual.
    public static class PadVisualMath
    {
        // Desplazamiento en pixeles del pulgar dentro del pozo del stick. Entrada en
        // convencion de ControllerState (Y=arriba positivo); salida en convencion de PANTALLA
        // (Dy positivo = hacia abajo), por eso Dy = -y*radius. La magnitud se acota a radius:
        // un stick a fondo en diagonal (magnitud 1.414) no puede sacar el pulgar del pozo.
        public static (double Dx, double Dy) StickOffset(double x, double y, double radius)
        {
            double dx = x * radius, dy = -y * radius;
            double mag = Math.Sqrt(dx * dx + dy * dy);
            if (mag > radius && mag > 0)
            {
                double k = radius / mag;
                dx *= k; dy *= k;
            }
            return (dx, dy);
        }

        // Relleno 0..1 para barras de gatillo / cualquier medidor lineal, acotado.
        public static double Fill01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
```

- [ ] **Step 5: Verificar que pasan** — mismo filtro PASS + suite completa PASS.
- [ ] **Step 6: Commit** — `git add HidusbfModernGui/PadVisualMath.cs HidusbfModernGui.Tests/PadVisualMathTests.cs HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj && git commit -m "feat: PadVisualMath - aritmetica pura del visualizador (TDD)"`

---

### Task 2: `PadVisual` — el mando PS5 dibujado nativo

**Files:**
- Create: `HidusbfModernGui/PadVisual.xaml` + `.xaml.cs`

**Interfaces:**
- Consumes: `ControllerState`, `PadVisualMath`, `PadButton`.
- Produces: `PadVisual` (UserControl) con `void Update(ControllerState s)` que refleja el estado; propiedad `bool StreamerBackground` (true = fondo transparente para overlay; false = fondo del tema). Consumido por MainWindow (Task 3) y StreamerWindow (Task 5).

**Nota de dibujo:** esto es trabajo visual; el plan fija la ESTRUCTURA (partes nombradas + cómo las mueve `Update`), no la geometría exacta de cada `Path`. Criterio de aceptación: se reconoce como un DualSense (dos sticks, cruceta, 4 caras △○✕□, L1/R1, L2/R2 con relleno, Share/Options, PS, touchpad), en estética monocroma coherente con el tema, tamaño base ~360×260 escalable por `Viewbox`.

- [ ] **Step 1: XAML** — `PadVisual.xaml` como `UserControl` cuyo raíz es un `Viewbox` (escala sin deformar) que contiene un `Canvas` de tamaño base fijo (p. ej. 360×260) con:
  - Cuerpo del mando: `Path`/`Border` con esquinas redondeadas, relleno `SurfaceBrush` (o transparente cuando `StreamerBackground`), borde `BorderBrush`.
  - Pozos de stick: dos `Ellipse` fijas (`LeftStickWell`/`RightStickWell`) y dentro dos pulgares `Ellipse` nombrados `LeftThumb`/`RightThumb` que `Update` reposiciona con `Canvas.SetLeft/Top`.
  - Cruceta: 4 `Path`/`Rectangle` (`DpadUp/Down/Left/Right`) que cambian de `Fill` al pulsarse.
  - Caras: 4 `Ellipse`/`Path` (`BtnCross/Circle/Square/Triangle`) con su símbolo; cambian de `Fill`/`Stroke` al pulsarse.
  - Hombros: `BtnL1`/`BtnR1` (resaltan) y gatillos `L2Fill`/`R2Fill` (barras cuya altura/opacidad sigue a L2/R2).
  - `BtnShare`/`BtnOptions`/`BtnPS`, y `TouchpadArea` (un `Rectangle`; opcional: un punto `TouchDot` que aparece en `TouchX/Y` cuando `TouchActive`).
  - Un `Border` raíz `x:Name="RootSurface"` cuyo `Background` alterna con `StreamerBackground`.

- [ ] **Step 2: code-behind** — `PadVisual.xaml.cs`:

```csharp
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media;

namespace HidusbfModernGui
{
    public partial class PadVisual : UserControl
    {
        // Radios de los pozos (deben coincidir con el XAML). El pulgar se mueve dentro.
        private const double StickRadius = 26;

        private static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        private static readonly Brush Active = Brushes.White;

        public PadVisual()
        {
            InitializeComponent();
            Idle.Freeze();
        }

        private bool _streamerBackground;
        public bool StreamerBackground
        {
            get => _streamerBackground;
            set { _streamerBackground = value; RootSurface.Background = value ? Brushes.Transparent
                     : (Brush)FindResource("SurfaceBrush"); }
        }

        // Refleja un ControllerState (ya transformado) en el dibujo. Llamado por el feed a ~60fps.
        public void Update(ControllerState s)
        {
            var (lx, ly) = PadVisualMath.StickOffset(s.Left.X, s.Left.Y, StickRadius);
            Canvas.SetLeft(LeftThumb, LeftStickCenterX + lx - LeftThumb.Width / 2);
            Canvas.SetTop(LeftThumb, LeftStickCenterY + ly - LeftThumb.Height / 2);
            var (rx, ry) = PadVisualMath.StickOffset(s.Right.X, s.Right.Y, StickRadius);
            Canvas.SetLeft(RightThumb, RightStickCenterX + rx - RightThumb.Width / 2);
            Canvas.SetTop(RightThumb, RightStickCenterY + ry - RightThumb.Height / 2);

            var p = s.Pressed;
            Set(BtnCross, p.Contains(PadButton.Cross));
            Set(BtnCircle, p.Contains(PadButton.Circle));
            Set(BtnSquare, p.Contains(PadButton.Square));
            Set(BtnTriangle, p.Contains(PadButton.Triangle));
            Set(DpadUp, p.Contains(PadButton.DpadUp));
            Set(DpadDown, p.Contains(PadButton.DpadDown));
            Set(DpadLeft, p.Contains(PadButton.DpadLeft));
            Set(DpadRight, p.Contains(PadButton.DpadRight));
            Set(BtnL1, p.Contains(PadButton.L1));
            Set(BtnR1, p.Contains(PadButton.R1));
            Set(BtnShare, p.Contains(PadButton.Share));
            Set(BtnOptions, p.Contains(PadButton.Options));
            Set(BtnPS, p.Contains(PadButton.PS));
            Set(TouchpadArea, p.Contains(PadButton.TouchpadClick));

            // Gatillos: la barra crece con el valor analogico.
            L2Fill.Height = TriggerMaxHeight * PadVisualMath.Fill01(s.L2);
            R2Fill.Height = TriggerMaxHeight * PadVisualMath.Fill01(s.R2);
        }

        private static void Set(System.Windows.Shapes.Shape shape, bool on)
            => shape.Fill = on ? Active : Idle;

        // Centros de los pozos y alto maximo de gatillo: constantes que reflejan el XAML.
        private const double LeftStickCenterX = 132, LeftStickCenterY = 150;
        private const double RightStickCenterX = 228, RightStickCenterY = 150;
        private const double TriggerMaxHeight = 34;
    }
}
```

(Los `Btn*`/`Dpad*` deben ser `Shape` para que `Set` asigne `Fill`; si algún control es `Path`, sirve — `Path : Shape`. Ajustar las constantes de centro a la geometría real dibujada en el XAML.)

- [ ] **Step 3: Verificación** — `dotnet build`. No hay test unitario (es dibujo). Se verifica en Task 3 al conectarlo al feed.
- [ ] **Step 4: Commit** — `git add HidusbfModernGui/PadVisual.xaml HidusbfModernGui/PadVisual.xaml.cs && git commit -m "feat: PadVisual - mando DualSense dibujado nativo en WPF"`

---

### Task 3: `VisualizerFeed` + el mando en vivo en el centro del configurador

**Files:**
- Create: `HidusbfModernGui/VisualizerFeed.cs`
- Modify: `HidusbfModernGui/MainWindow.xaml` (host del `PadVisual` en `ConfigPanel`)
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `DualSenseReader`, `RemapEngine`, `RemapSettings`, `ControllerState`.
- Produces: `VisualizerFeed` con `ControllerState? Latest()`; el feed decide la fuente: un `DualSenseReader` propio (solo cuando `UseOwnReader` está activo) o `null` cuando el motor cede su snapshot. Métodos `StartOwnReader()`/`StopOwnReader()`. La transformación la aplica MainWindow (tiene `_remap`).

**Diseño de la fuente (lección L1):**
- Motor APAGADO + configurador visible → el feed abre lector propio (físico visible).
- Motor ENCENDIDO → el feed cierra su lector (antes de que el motor oculte/reinicie el devnode) y MainWindow usa `_padReader.Snapshot()`.
- Un solo `DispatcherTimer` de UI (~60 fps, 16 ms) en MainWindow: obtiene el snapshot físico (de `_padReader` si `_engineRunning`, si no de `_visualFeed`), le aplica `RemapEngine.Transform(_remap)` y llama `Update` en el `PadVisual` del configurador (y el del streamer si abierto).

- [ ] **Step 1: `VisualizerFeed.cs`** — un envoltorio delgado del lector propio:

```csharp
namespace HidusbfModernGui
{
    // Fuente de estado fisico para el visualizador cuando el MOTOR esta apagado (el fisico
    // esta visible y lo podemos abrir de solo-lectura). Cuando el motor esta encendido NO se
    // usa: MainWindow lee el snapshot del propio lector del motor, porque abrir un segundo
    // handle competiria con el reinicio de devnode del arranque (leccion L1). Envoltorio
    // delgado de DualSenseReader para aislar ese ciclo de vida del motor.
    public sealed class VisualizerFeed
    {
        private DualSenseReader? _reader;
        public bool OwnReaderActive => _reader != null;

        public void StartOwnReader()
        {
            if (_reader != null) return;
            var r = new DualSenseReader();
            if (r.Start().Success) _reader = r;
        }

        public void StopOwnReader()
        {
            try { _reader?.Stop(); } catch { }
            _reader = null;
        }

        // Snapshot fisico crudo o null si no hay lector propio vivo.
        public ControllerState? PhysicalSnapshot() => _reader?.Snapshot();
    }
}
```

- [ ] **Step 2: XAML** — en `ConfigPanel`, insertar el visualizador de forma que quede SIEMPRE visible bajo la tarjeta "MANDO VIRTUAL" y sobre los botones de pestaña (así se ve con cualquier pestaña abierta). Un `Border` monocromo con título "MANDO EN VIVO", el `PadVisual` centrado, y una fila de controles del streamer (Task 5 los cablea):

```xml
<Border Background="{StaticResource SurfaceBrush}" BorderBrush="{StaticResource BorderBrush}"
        BorderThickness="1" Padding="18" Margin="0,0,0,20">
    <StackPanel>
        <TextBlock Text="MANDO EN VIVO (salida al juego)" Style="{StaticResource SectionHeading}"/>
        <local:PadVisual x:Name="ConfigPadVisual" Height="240" Margin="0,10,0,0"/>
        <TextBlock Style="{StaticResource FieldLabel}" TextWrapping="Wrap" Margin="0,8,0,0"
                   Text="Muestra lo que el juego recibe tras tus ajustes. Con el mando virtual apagado tambien se mueve (para configurar antes de activar)."/>
        <!-- Fila de modo streamer: la cablea la Task 5 -->
        <StackPanel x:Name="StreamerRow" Orientation="Horizontal" Margin="0,10,0,0"/>
    </StackPanel>
</Border>
```

(Requiere el namespace `xmlns:local="clr-namespace:HidusbfModernGui"` en el `Window` raíz — verificar si ya existe; si no, añadirlo.)

- [ ] **Step 3: code-behind — el timer del feed y la coordinación con el motor.** Campos + arranque/parada ligados a la visibilidad de la página del mando y al estado del motor:

```csharp
private readonly VisualizerFeed _visualFeed = new();
private DispatcherTimer? _visualTimer;

// Arranca el visualizador: timer ~60fps + (si el motor esta apagado) lector propio.
// Idempotente. Llamar cuando se entra a la pagina del mando y al cerrar el streamer no.
private void StartVisualizer()
{
    if (!_engineRunning) _visualFeed.StartOwnReader();
    if (_visualTimer == null)
    {
        _visualTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _visualTimer.Tick += VisualizerTick;
    }
    _visualTimer.Start();
}

private void StopVisualizer()
{
    _visualTimer?.Stop();
    _visualFeed.StopOwnReader();
}

private void VisualizerTick(object? sender, EventArgs e)
{
    // Fuente: el lector del motor si esta activo (no abrimos segundo handle), si no el propio.
    ControllerState? raw = _engineRunning ? _padReader?.Snapshot() : _visualFeed.PhysicalSnapshot();
    if (raw == null) return;
    var outState = RemapEngine.Transform(raw, _remap);
    ConfigPadVisual.Update(outState);
    _streamerWindow?.Pad.Update(outState);   // Task 5
    UpdateCurveLiveDot(outState);             // Task 4
}
```

Ganchos de ciclo de vida:
- **Al mostrar la página del mando** (donde ya se llama `BuildRemapControls`/`ShowConfigPanel` — localizar por grep): llamar `StartVisualizer()`. Al salir del hub del mando a otra sección del sidebar: `StopVisualizer()`.
- **En `StartEngine`**, ANTES del `Task.Run(StartEngineDevices)` (que oculta + reinicia el devnode): `_visualFeed.StopOwnReader();` — el feed pasará a usar `_padReader` en cuanto `_engineRunning` sea true.
- **En `StopEngine`**, tras `CleanupEngine()`: si la página del mando sigue visible, `_visualFeed.StartOwnReader();` para recuperar la fuente propia.
- **En `OnClosing`**: `StopVisualizer()`.

(Verificar los nombres reales de los métodos de navegación del hub por grep — `ShowConfigPanel`, y donde el sidebar cambia de sección. El principio: feed vivo solo mientras el configurador es visible.)

- [ ] **Step 4: Prueba manual** — abrir como admin: entrar a Configurar el mando → mover el DualSense **físico** (motor apagado) → los sticks/botones/gatillos del dibujo se mueven en vivo. Subir ZONA MUERTA izquierda a 30% → el stick izquierdo del dibujo deja de moverse cerca del centro (se VE el efecto). Activar MANDO VIRTUAL → sigue moviéndose sin congelarse ni duplicar lectura. Detener → sigue vivo.
- [ ] **Step 5: Commit** — `git add -u && git commit -m "feat: mando en vivo en el configurador (VisualizerFeed + PadVisual, salida transformada)"`

---

### Task 4: Punto vivo en la curva + línea de referencia lineal (arreglo del bug de feedback)

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml` (los dos canvas de curva)
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `PadVisualMath`, `_remap`, `RawToDomain`/`DomainToRaw` (existentes), `InputTransform.Shape`, el `outState` del `VisualizerTick` (Task 3).
- Produces: `void UpdateCurveLiveDot(ControllerState outState)` (llamado por `VisualizerTick`); una `Line` de referencia diagonal fija en cada canvas; un `Ellipse` "punto vivo" por canvas.

**Por qué esto arregla el bug:** hoy una curva Editor por defecto es diagonal → se ve idéntica a Lineal, y arrastrar puntos no muestra efecto. Con (a) una **diagonal de referencia** siempre visible detrás de la curva editable, la desviación respecto a lineal es obvia; y (b) un **punto vivo** que recorre la curva según la magnitud actual del stick, se VE exactamente qué salida da cada zona — que es lo que "no se entendía".

- [ ] **Step 1: XAML** — en cada canvas de curva, añadir (bajo la Polyline de la curva, encima del fondo) una línea diagonal tenue y un punto vivo, p. ej. en `LeftCurveCanvas`:

```xml
<Line x:Name="LeftCurveRef" X1="0" Y1="100" X2="220" Y2="0"
      Stroke="{StaticResource BorderBrush}" StrokeThickness="1" StrokeDashArray="3 3" Opacity="0.5"/>
<!-- LeftCurveLine (la curva) ya existe aqui, debe quedar DESPUES de la referencia -->
<Ellipse x:Name="LeftCurveLiveDot" Width="7" Height="7" Fill="White" Visibility="Collapsed"/>
```

(Ídem `RightCurveRef`/`RightCurveLiveDot`. La referencia va de (0,100) a (220,0): la diagonal lineal en un canvas 220×100 con Y invertido.)

- [ ] **Step 2: code-behind** — el punto vivo sigue la magnitud del stick sobre la curva de CADA lado:

```csharp
// Mueve el punto vivo de cada canvas a (entrada, salida) segun el stick actual. La entrada
// es la MAGNITUD cruda del stick (0..1); la salida, la misma curva que dibuja DrawCurve
// (via InputTransform.ApplyStick), asi el punto cae exactamente sobre la polilinea. Solo se
// muestra si el stick esta fuera de la zona muerta (si no, no hay nada que ver en el centro).
private void UpdateCurveLiveDot(ControllerState outState)
{
    // Nota: outState ya esta transformado; para el punto necesitamos la ENTRADA cruda. La
    // reconstruimos desde el raw del tick — se pasa por parametro en la version final. Aqui
    // se usa la magnitud de la SALIDA como Y y la de la entrada como X (ver Step 3).
    // (Implementacion concreta en Step 3.)
}
```

Ajuste: `VisualizerTick` (Task 3) ya tiene el `raw` (entrada) y el `outState` (salida). Cambiar la firma para pasar ambos:

```csharp
// En VisualizerTick, sustituir la llamada por:
UpdateCurveLiveDot(raw, outState);
```

y la implementación real:

```csharp
private void UpdateCurveLiveDot(ControllerState raw, ControllerState outState)
{
    PlaceLiveDot(LeftCurveCanvas, LeftCurveLiveDot, raw.Left, outState.Left,
        _remap.LeftInnerDeadzone, _remap.LeftOuterDeadzone);
    PlaceLiveDot(RightCurveCanvas, RightCurveLiveDot, raw.Right, outState.Right,
        _remap.RightInnerDeadzone, _remap.RightOuterDeadzone);
}

// La curva mapea magnitud de entrada -> magnitud de salida. X del canvas = entrada cruda
// (0..1), Y = salida (0..1, invertida). Coincide con DrawCurve, que muestrea ApplyStick
// sobre un stick horizontal. Se oculta dentro de la zona muerta (magnitud de salida 0).
private void PlaceLiveDot(Canvas canvas, System.Windows.Shapes.Ellipse dot,
    StickInput inRaw, StickInput outT, double inner, double outer)
{
    double inMag = Math.Min(1.0, Math.Sqrt(inRaw.X * inRaw.X + inRaw.Y * inRaw.Y));
    double outMag = Math.Min(1.0, Math.Sqrt(outT.X * outT.X + outT.Y * outT.Y));
    if (outMag <= 0.0001)
    {
        dot.Visibility = Visibility.Collapsed;
        return;
    }
    Canvas.SetLeft(dot, inMag * canvas.Width - dot.Width / 2);
    Canvas.SetTop(dot, (1 - outMag) * canvas.Height - dot.Height / 2);
    dot.Visibility = Visibility.Visible;
}
```

- [ ] **Step 3: Verificación** — build 0/0. Manual: STICKS, mover el stick físico → un punto blanco recorre la gráfica de la curva mostrando entrada→salida; con Lineal va sobre la diagonal; con Editor y puntos arrastrados, se ve claramente separarse de la diagonal de referencia. Esto demuestra en pantalla qué hacen los 3 puntos.
- [ ] **Step 4: Commit** — `git add -u && git commit -m "feat: punto vivo + diagonal de referencia en la curva (se VE que hacen los 3 puntos)"`

---

### Task 5: Modo streamer — ventana overlay transparente con opciones

**Files:**
- Create: `HidusbfModernGui/StreamerWindow.xaml` + `.xaml.cs`
- Modify: `HidusbfModernGui/MainWindow.xaml` (fila `StreamerRow`)
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `PadVisual`.
- Produces: `StreamerWindow` con `PadVisual Pad { get; }` (para que `VisualizerTick` lo alimente) y sus opciones internas. En MainWindow: `StreamerWindow? _streamerWindow` + el toggle.

- [ ] **Step 1: `StreamerWindow.xaml`** — ventana sin borde, fondo transparente, arrastrable, siempre-encima, con el `PadVisual` y una barra de opciones que aparece al pasar el ratón:

```xml
<Window x:Class="HidusbfModernGui.StreamerWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="clr-namespace:HidusbfModernGui"
        Title="Mando (streamer)" WindowStyle="None" AllowsTransparency="True"
        Background="Transparent" Topmost="True" ShowInTaskbar="False"
        SizeToContent="WidthAndHeight" MouseLeftButtonDown="Drag">
    <Grid>
        <local:PadVisual x:Name="Pad" Width="360" Height="260"/>
        <!-- Barra de opciones: visible al pasar el raton por encima (Trigger IsMouseOver). -->
        <StackPanel x:Name="Toolbar" Orientation="Horizontal" VerticalAlignment="Top"
                    HorizontalAlignment="Right" Background="#AA000000" Opacity="0">
            <StackPanel.Style>
                <Style TargetType="StackPanel">
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding IsMouseOver, RelativeSource={RelativeSource AncestorType=Window}}" Value="True">
                            <Setter Property="Opacity" Value="1"/>
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </StackPanel.Style>
            <Slider x:Name="ScaleSlider" Minimum="0.5" Maximum="2.0" Value="1.0" Width="90" ValueChanged="Scale_Changed"/>
            <ToggleButton x:Name="TopmostToggle" Content="Encima" IsChecked="True" Click="Topmost_Changed"/>
            <ToggleButton x:Name="ClickThroughToggle" Content="Pasa clic" Click="ClickThrough_Changed"/>
            <Button Content="Cerrar" Click="CloseClick"/>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 2: `StreamerWindow.xaml.cs`** — arrastre, escala, topmost, click-through (extendido con WS_EX_TRANSPARENT), y fondo transparente del pad:

```csharp
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace HidusbfModernGui
{
    public partial class StreamerWindow : Window
    {
        public PadVisual Pad => PadControl;   // expuesto para que el feed lo alimente

        public StreamerWindow()
        {
            InitializeComponent();
            PadControl.StreamerBackground = true;   // fondo transparente del mando
        }

        private void Drag(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
        private void Scale_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
            => PadControl.LayoutTransform = new ScaleTransform(ScaleSlider.Value, ScaleSlider.Value);
        private void Topmost_Changed(object s, RoutedEventArgs e) => Topmost = TopmostToggle.IsChecked == true;
        private void CloseClick(object s, RoutedEventArgs e) => Close();

        // Click-through: la ventana deja pasar el raton al juego/OBS de abajo (WS_EX_TRANSPARENT).
        private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        private void ClickThrough_Changed(object s, RoutedEventArgs e)
        {
            var h = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            if (ClickThroughToggle.IsChecked == true) SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT);
            else SetWindowLong(h, GWL_EXSTYLE, ex & ~WS_EX_TRANSPARENT);
        }
    }
}
```

(El `PadVisual` del XAML debe llamarse `PadControl` para casar con `Pad => PadControl`; ajustar el `x:Name` si se prefiere otro.)

- [ ] **Step 3: MainWindow — el toggle.** En `StreamerRow` (Task 3) añadir por XAML un `ToggleButton x:Name="StreamerToggle"` con `Content="MODO STREAMER"` y `Click="StreamerToggle_Click"`. Code-behind:

```csharp
private StreamerWindow? _streamerWindow;

private void StreamerToggle_Click(object sender, RoutedEventArgs e)
{
    if (_streamerWindow == null)
    {
        _streamerWindow = new StreamerWindow { Owner = this };
        _streamerWindow.Closed += (_, _) => { _streamerWindow = null; StreamerToggle.IsChecked = false; };
        _streamerWindow.Show();
        StartVisualizer();   // asegura el feed vivo aunque el foco cambie
    }
    else
    {
        _streamerWindow.Close();   // el handler Closed limpia la referencia
    }
}
```

En `OnClosing`, cerrar el streamer si sigue abierto: `_streamerWindow?.Close();`.

- [ ] **Step 4: Prueba manual** — MODO STREAMER abre una ventana flotante con solo el mando y fondo transparente; se mueve en vivo igual que el del configurador; se puede arrastrar, redimensionar (slider), fijar siempre-encima y activar "pasa clic" (el ratón atraviesa la ventana). En OBS se puede capturar como ventana con transparencia. Cerrar la app cierra también el streamer.
- [ ] **Step 5: Commit** — `git add -u && git commit -m "feat: modo streamer - overlay transparente del mando, arrastrable, topmost y click-through"`

---

### Task 6: README + documentación + verificación integral

**Files:**
- Modify: `README.md`, `docs/DOCUMENTACION.md`

- [ ] **Step 1** — README: en la viñeta **Configurar el mando**, añadir que muestra un **mando en vivo** (la salida transformada) y un **modo streamer** (overlay transparente para OBS).
- [ ] **Step 2** — `docs/DOCUMENTACION.md`: añadir `PadVisual`/`PadVisualMath`/`VisualizerFeed`/`StreamerWindow` al mapa de módulos; documentar el flujo del feed y la coordinación con el motor (lección L1 aplicada: el feed cede la fuente al arrancar el motor); registrar el arreglo del bug de feedback de la curva.
- [ ] **Step 3** — `dotnet test` completo (todo verde) + `.\package.ps1` (termina en "Package ready" sin warnings nuevos; confirmar que el visualizador no arrastra dependencias nuevas y el portable de un solo archivo sigue).
- [ ] **Step 4** — Prueba integral (usuario, hardware): mando en vivo con motor on/off, el punto de la curva demostrando los 3 puntos, y el modo streamer en OBS.
- [ ] **Step 5: Commit** — `git add -u && git commit -m "docs: visualizador de mando y modo streamer en README + documentacion"`

---

## Self-review

- **Cobertura del pedido:** mando PS5 dibujado nativo y local (Task 2), en el centro del configurador moviéndose en vivo (Task 3), reflejando la salida transformada (Task 3, `RemapEngine.Transform` en el feed), modo streamer con fondo transparente y opciones (Task 5). El bug ("es como si Lineal no estuviera activa / los 3 puntos no se entienden") se ataca de frente con la diagonal de referencia + el punto vivo (Task 4) y con el propio mando en vivo que muestra el efecto de cada ajuste (Task 3). ✓
- **Placeholders:** el dibujo del mando (Task 2) queda como estructura + mapping (apropiado para trabajo visual, con criterio de aceptación claro); el resto lleva código real. Los puntos de "localizar por grep" (namespace `local`, métodos de navegación del hub, nombres de canvas) van con instrucción concreta y el build/manual como red. ✓
- **Tipos consistentes:** `PadVisualMath.StickOffset`/`Fill01` (Task 1) usados por `PadVisual.Update` (Task 2) y `PlaceLiveDot` (Task 4); `PadVisual.Update(ControllerState)` (Task 2) alimentado por `VisualizerTick` (Task 3) y por el streamer (Task 5); `VisualizerFeed.PhysicalSnapshot()` (Task 3) ↔ el `raw` del tick; `RemapEngine.Transform` reutilizado sin cambios. ✓
- **Lección L1 respetada:** el feed nunca mantiene un lector propio abierto mientras el motor arranca (cede la fuente a `_padReader`); documentado y cableado en `StartEngine`/`StopEngine`. ✓
- **Restricción de tests:** solo `PadVisualMath.cs` (puro) se linkea a tests; `PadVisual`/`VisualizerFeed`/`StreamerWindow` (WPF/HidSharp) no. ✓
- **Portable intacto:** cero dependencias nuevas (nada de WebView2); el single-file y el tema monocromo se conservan. ✓
