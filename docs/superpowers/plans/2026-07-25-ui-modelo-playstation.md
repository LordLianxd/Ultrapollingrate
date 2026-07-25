# Rediseño del configurador siguiendo el modelo de PlayStation Accessories — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reorganizar "Configurar el mando" con el patrón de la app oficial *PlayStation Accessories*: un **hub de tarjetas grandes** (icono + título + descripción) y una **sub-página por apartado** con botón atrás; el mapeo de botones sobre un **diagrama estático con líneas guía**; los sticks con **columna izquierda / mando central / columna derecha** y el punto de entrada en vivo mostrando *crudo vs ajustado*; y los gatillos con **rango Desde/Hasta** en vez de un único umbral.

**Architecture:** Un único cambio de núcleo (el rango de gatillo, con TDD y migración desde el umbral actual); el resto es presentación. `ConfigPanel` pasa de una columna larga a un **shell de navegación**: `ConfigHub` (tarjetas) + cuatro sub-páginas (`PageBotones`, `PageSticks`, `PageGatillos`, `PageTouchpad`), alternadas por visibilidad como ya se hace en el resto de la app, con una cabecera común (atrás + título + "?"). El mando en vivo (`PadVisualHost`) sigue siendo nuestro y se reutiliza en el hub y en la página de sticks.

**Tech Stack:** .NET 9 WPF, xUnit, System.Text.Json. Sin dependencias nuevas.

---

## Crítica del modelo de referencia (qué copiamos y qué NO)

El plan anterior (`2026-07-25-rediseno-ui-mando.md`) queda **superado por este** en la parte de layout e interacción. Lo que se conserva de él está listado al final.

**Lo que la app de Sony hace mejor que nuestro plan anterior — se adopta:**

1. **Hub de tarjetas grandes.** Cuatro cuadrados con icono, título y una frase. Es autoexplicativo y reduce la carga: la pantalla no te enseña 40 controles a la vez. Mi propuesta anterior (todo en columnas simultáneas) tenía razón en aprovechar el ancho, pero equivocaba la prioridad: **primero entender, luego ajustar**.
2. **Diagrama estático con líneas guía para el mapeo.** Enseña **todas** las asignaciones a la vez, sin pasar el ratón ni hacer clic. Mi idea anterior —pulsar sobre el mando en vivo y poner insignias encima— era **peor**: el mando en vivo se mueve, y las etiquetas encima de un dibujo que vibra se leen mal. Consecuencia práctica: **`PadHitZones` y el hit-testing dejan de hacer falta** (Tasks 2 y 3 del plan anterior se descartan). Se gana simplicidad.
3. **Punto de entrada en vivo con dos colores** (entrada del mando vs entrada ajustada) sobre el mando central de la página de sticks. Nosotros ya tenemos el punto vivo en la gráfica de la curva, pero **uno solo**; mostrar los dos superpuestos enseña de un vistazo *qué le está haciendo tu configuración a tu pulgar*. Estrictamente mejor.
4. **Gatillos como rango "Desde/Hasta" (0–100) con cajas numéricas + / −**, y un interruptor **"aplicar la misma configuración a L2 y R2"**. Nuestro "punto de disparo" único es un caso particular de eso. El rango permite a la vez *ignorar el primer tramo* y *saturar antes del final*, que es lo que de verdad pide un jugador.

**Lo que NO se copia, a propósito:**

1. **El botón "Aplicar".** Sony **acumula** cambios y los aplica al pulsar. UltraPolling los aplica **en vivo** (mueves el slider y lo sientes en el juego). Para ajustar curvas, en vivo es superior: el ciclo *tocar → sentir → corregir* dura un segundo, no tres pasos. Copiar "Aplicar" sería un retroceso. **Mantenemos aplicación inmediata y solo "Restablecer".**
2. **Su editor de curva** (un desplegable + un slider de ajuste). El nuestro es un editor de puntos con interpolación monótona: más potente. No se degrada.
3. **La rueda decorativa de profundidad** alrededor del mando en la página de gatillos: es adorno; los números y la barra hacen el trabajo. Ponemos una **barra de profundidad real** en su lugar.
4. **Ocultar el estado.** Su app no tiene medidor de tasa real, ni skins, ni overlay de streaming. Eso es nuestro y no se sacrifica por parecernos a ellos.

**Riesgo que asumo y acoto:** el hub añade un clic para llegar a cada ajuste. Se compensa (a) manteniendo el **mando en vivo y el interruptor maestro en el hub**, que es lo que se consulta constantemente, y (b) con **atrás** siempre en el mismo sitio.

---

## Global Constraints

- UI en **español**, tema **monocromo** (excepciones vigentes: los 3 puntos del editor de curvas y el arte de un skin). El "crudo vs ajustado" de la Task 5 usa **blanco y gris**, no dos colores nuevos.
- El proyecto de tests **linkea fuentes individualmente**: todo archivo nuevo del núcleo va al csproj. Nada de WPF/HidSharp/Nefarius en tests.
- **Aplicación en vivo**: ningún control nuevo introduce un paso "Aplicar". `RememberRemap()` (debounce) sigue siendo el camino de guardado.
- **Compatibilidad de perfiles**: `L2PointPct`/`R2PointPct` se conservan en el JSON y se migran al rango; un perfil viejo no puede quedar inservible.
- El motor (`RemapEngine`, `VisualizerFeed`, `PadSkin`, `DualSenseReader`) solo cambia en lo que exige el rango de gatillo (Task 1).
- Commits **sin** Co-Authored-By. El push lo hace el usuario.

## Estructura de archivos

- Modify: `HidusbfModernGui/InputTransform.cs` (rango de gatillo)
- Modify: `HidusbfModernGui/RemapSettings.cs` (From/To + migración en `Sanitize`)
- Modify: `HidusbfModernGui/RemapEngine.cs` (pasar el rango)
- Create: `HidusbfModernGui/ConfigNav.cs` (enum de páginas + navegación, puro)
- Modify: `HidusbfModernGui/MainWindow.xaml(.cs)` (hub + 4 sub-páginas)
- Modify: `HidusbfModernGui/Theme.xaml` (estilos `HubCard`, `Stepper`, `PageHeader`)
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj` (+1 link)
- Test: `HidusbfModernGui.Tests/InputTransformMapTests.cs`, `RemapSettingsTests.cs`, `RemapEngineTests.cs`, `ConfigNavTests.cs`

---

### Task 1: Gatillos por rango Desde/Hasta, con migración del umbral actual (TDD)

**Files:**
- Modify: `HidusbfModernGui/InputTransform.cs`, `RemapSettings.cs`, `RemapEngine.cs`
- Test: `HidusbfModernGui.Tests/InputTransformMapTests.cs`, `RemapSettingsTests.cs`, `RemapEngineTests.cs`

**Interfaces:**
- Produces: `InputTransform.ApplyTrigger(double value, double from, double to)` (sobrecarga nueva; **la de un solo argumento se conserva** porque tiene tests propios y expresa el escalón); `RemapSettings.L2FromPct/L2ToPct/R2FromPct/R2ToPct` con derivados `L2From/L2To/R2From/R2To`; `RemapSettings.Sanitize()` migra `L2PointPct>0` a `From=To=punto`.

**Semántica:** salida 0 por debajo de `from`, 1 por encima de `to`, lineal entre medias. `from==to` reproduce **exactamente** el escalón actual (hair trigger). `from=0,to=1` es passthrough. Esto hace del rango un superconjunto del comportamiento de hoy.

- [ ] **Step 1: Tests que fallan** — añadir a `InputTransformMapTests.cs`:

```csharp
[Fact]
public void ApplyTriggerRange_BelowFrom_IsZero()
    => Assert.Equal(0.0, InputTransform.ApplyTrigger(0.10, 0.20, 0.80), 3);

[Fact]
public void ApplyTriggerRange_AboveTo_IsFull()
    => Assert.Equal(1.0, InputTransform.ApplyTrigger(0.90, 0.20, 0.80), 3);

[Fact]
public void ApplyTriggerRange_Midpoint_IsHalf()
    => Assert.Equal(0.5, InputTransform.ApplyTrigger(0.50, 0.20, 0.80), 3);

[Fact]
public void ApplyTriggerRange_FullRange_IsPassthrough()
    => Assert.Equal(0.37, InputTransform.ApplyTrigger(0.37, 0.0, 1.0), 3);

[Fact]
public void ApplyTriggerRange_FromEqualsTo_IsTheOldStep()
{
    // Un rango degenerado es exactamente el hair-trigger de siempre.
    Assert.Equal(0.0, InputTransform.ApplyTrigger(0.29, 0.30, 0.30), 3);
    Assert.Equal(1.0, InputTransform.ApplyTrigger(0.30, 0.30, 0.30), 3);
}

[Fact]
public void ApplyTriggerRange_InvertedRange_DoesNotExplode()
{
    // to < from es un JSON editado a mano: se trata como escalon en 'from', no NaN.
    Assert.Equal(0.0, InputTransform.ApplyTrigger(0.10, 0.60, 0.20), 3);
    Assert.Equal(1.0, InputTransform.ApplyTrigger(0.70, 0.60, 0.20), 3);
}
```

a `RemapSettingsTests.cs`:

```csharp
[Fact]
public void TriggerRange_DefaultsToFullTravel()
{
    var s = new RemapSettings();
    Assert.Equal(0, s.L2FromPct);
    Assert.Equal(100, s.L2ToPct);
    Assert.Equal(0.0, s.L2From, 3);
    Assert.Equal(1.0, s.L2To, 3);
}

[Fact]
public void Sanitize_MigratesOldTriggerPointToADegenerateRange()
{
    // Perfil viejo: solo tenia punto de disparo. Debe seguir comportandose igual.
    var s = new RemapSettings { L2PointPct = 30, R2PointPct = 45 };
    s.Sanitize();
    Assert.Equal(30, s.L2FromPct);
    Assert.Equal(30, s.L2ToPct);
    Assert.Equal(45, s.R2FromPct);
    Assert.Equal(45, s.R2ToPct);
}

[Fact]
public void Sanitize_DoesNotTouchAnExplicitRange()
{
    var s = new RemapSettings { L2PointPct = 30, L2FromPct = 10, L2ToPct = 90 };
    s.Sanitize();
    Assert.Equal(10, s.L2FromPct);
    Assert.Equal(90, s.L2ToPct);
}

[Fact]
public void Sanitize_ClampsAndOrdersTheRange()
{
    var s = new RemapSettings { L2FromPct = 120, L2ToPct = -5 };
    s.Sanitize();
    Assert.InRange(s.L2FromPct, 0, 100);
    Assert.InRange(s.L2ToPct, 0, 100);
    Assert.True(s.L2ToPct >= s.L2FromPct);
}
```

y a `RemapEngineTests.cs`:

```csharp
[Fact]
public void TriggerRange_ReachesTheOutput()
{
    var s = new RemapSettings { L2FromPct = 20, L2ToPct = 80 };
    var outp = RemapEngine.Transform(new ControllerState { L2 = 0.5 }, s);
    Assert.Equal(0.5, outp.L2, 2);          // mitad del rango
    Assert.DoesNotContain(PadButton.L2, outp.Pressed);   // aun no esta a fondo
}

[Fact]
public void TriggerRange_AtTheTop_PressesTheButton()
{
    var s = new RemapSettings { L2FromPct = 20, L2ToPct = 80 };
    var outp = RemapEngine.Transform(new ControllerState { L2 = 0.95 }, s);
    Assert.Equal(1.0, outp.L2, 3);
    Assert.Contains(PadButton.L2, outp.Pressed);
}
```

- [ ] **Step 2: Verificar que fallan** — `dotnet test ... --filter "FullyQualifiedName~InputTransformMapTests|FullyQualifiedName~RemapSettingsTests|FullyQualifiedName~RemapEngineTests"`.

- [ ] **Step 3: Implementación** — en `InputTransform.cs` (junto a la sobrecarga existente, que **no se toca**):

```csharp
// Gatillo por RANGO: 0 por debajo de 'from', 1 por encima de 'to', lineal entre medias.
// Generaliza el hair trigger de la sobrecarga de un solo punto: from==to es exactamente
// ese escalon, y from=0/to=1 es passthrough. El rango permite lo que el umbral no: tirar
// el primer tramo muerto del gatillo Y llegar a fondo antes del tope fisico.
public static double ApplyTrigger(double value, double from, double to)
{
    double f = Math.Clamp(from, 0.0, 1.0);
    double t = Math.Clamp(to, 0.0, 1.0);
    // Rango invertido o nulo (JSON editado a mano): se comporta como escalon en 'f', que
    // es lo unico sensato y nunca divide por cero.
    if (t <= f) return value < f ? 0.0 : 1.0;
    return Math.Clamp((value - f) / (t - f), 0.0, 1.0);
}
```

en `RemapSettings.cs`:

```csharp
// Rango de recorrido util del gatillo, en % (0..100). Sustituye al punto de disparo
// unico: 'Desde' tira el primer tramo, 'Hasta' hace que llegue a fondo antes del tope.
// L2PointPct/R2PointPct se conservan SOLO para leer perfiles antiguos (ver Sanitize).
public int L2FromPct { get; set; } = 0;
public int L2ToPct { get; set; } = 100;
public int R2FromPct { get; set; } = 0;
public int R2ToPct { get; set; } = 100;

public double L2From => Math.Clamp(L2FromPct, 0, 100) / 100.0;
public double L2To   => Math.Clamp(L2ToPct, 0, 100) / 100.0;
public double R2From => Math.Clamp(R2FromPct, 0, 100) / 100.0;
public double R2To   => Math.Clamp(R2ToPct, 0, 100) / 100.0;
```

y dentro de `Sanitize()`, antes de los puntos de curva:

```csharp
// Perfiles anteriores al rango solo traen el punto de disparo. Un punto > 0 con el rango
// aun por defecto (0..100) significa "esto viene del formato viejo": se convierte en el
// rango degenerado from=to=punto, que da EXACTAMENTE el mismo escalon de antes.
if (L2PointPct > 0 && L2FromPct == 0 && L2ToPct == 100) { L2FromPct = L2PointPct; L2ToPct = L2PointPct; }
if (R2PointPct > 0 && R2FromPct == 0 && R2ToPct == 100) { R2FromPct = R2PointPct; R2ToPct = R2PointPct; }

L2FromPct = Math.Clamp(L2FromPct, 0, 100);
L2ToPct   = Math.Clamp(L2ToPct, 0, 100);
R2FromPct = Math.Clamp(R2FromPct, 0, 100);
R2ToPct   = Math.Clamp(R2ToPct, 0, 100);
if (L2ToPct < L2FromPct) L2ToPct = L2FromPct;
if (R2ToPct < R2FromPct) R2ToPct = R2FromPct;
```

en `RemapEngine.Transform`, las dos llamadas y los dos botones pasan al rango:

```csharp
double l2 = InputTransform.ApplyTrigger(input.L2, s.L2From, s.L2To);
double r2 = InputTransform.ApplyTrigger(input.R2, s.R2From, s.R2To);
...
// El bit del boton sigue al analog transformado siempre que el rango recorte algo; con
// el rango completo (0..100) se respeta el boton fisico, como hacia el passthrough.
ApplyTriggerButton(effective, PadButton.L2, s.L2FromPct != 0 || s.L2ToPct != 100, l2);
ApplyTriggerButton(effective, PadButton.R2, s.R2FromPct != 0 || s.R2ToPct != 100, r2);
```

con la firma de `ApplyTriggerButton` cambiada de `double point` a `bool ranged` (mismo cuerpo, `if (!ranged) return;`).

- [ ] **Step 4: Verificar que pasan** — filtros + suite completa. Los tests viejos de `ApplyTrigger(value, point)` **siguen pasando** (esa sobrecarga no cambió); `Trigger_PassthroughWhenPointZero_KeepsPhysicalButton` debe seguir verde porque el rango por defecto es 0..100.
- [ ] **Step 5: Commit** — `git add -u && git commit -m "feat: gatillos por rango Desde/Hasta, con migracion del punto de disparo (TDD)"`

---

### Task 2: `ConfigNav` — el shell de navegación del configurador (TDD)

**Files:**
- Create: `HidusbfModernGui/ConfigNav.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj`
- Test: `HidusbfModernGui.Tests/ConfigNavTests.cs`

**Interfaces:**
- Produces: `enum ConfigPage { Hub, Botones, Sticks, Gatillos, Touchpad }`; `ConfigNav` con `Current`, `Go(ConfigPage)`, `Back()`, `bool CanGoBack`, `string TitleOf(ConfigPage)`. Lógica pura (sin WPF) para poder probar la navegación sin abrir ventanas.

- [ ] **Step 1: Link en el csproj**: `<Compile Include="..\HidusbfModernGui\ConfigNav.cs" Link="ConfigNav.cs" />`

- [ ] **Step 2: Tests que fallan** — crear `ConfigNavTests.cs`:

```csharp
using HidusbfModernGui;
using Xunit;

public class ConfigNavTests
{
    [Fact]
    public void StartsAtHub()
    {
        var nav = new ConfigNav();
        Assert.Equal(ConfigPage.Hub, nav.Current);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void Go_MovesAndAllowsBack()
    {
        var nav = new ConfigNav();
        nav.Go(ConfigPage.Sticks);
        Assert.Equal(ConfigPage.Sticks, nav.Current);
        Assert.True(nav.CanGoBack);
    }

    [Fact]
    public void Back_FromAnyPage_ReturnsToHub()
    {
        var nav = new ConfigNav();
        nav.Go(ConfigPage.Gatillos);
        nav.Back();
        Assert.Equal(ConfigPage.Hub, nav.Current);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void Back_AtHub_IsANoOp()
    {
        var nav = new ConfigNav();
        nav.Back();
        Assert.Equal(ConfigPage.Hub, nav.Current);
    }

    [Fact]
    public void TitleOf_EveryPage_HasText()
    {
        foreach (ConfigPage p in System.Enum.GetValues<ConfigPage>())
            Assert.False(string.IsNullOrWhiteSpace(ConfigNav.TitleOf(p)));
    }
}
```

- [ ] **Step 3: Verificar que falla** (compilación).
- [ ] **Step 4: Implementación** — crear `HidusbfModernGui/ConfigNav.cs`:

```csharp
namespace HidusbfModernGui
{
    // Las paginas del configurador del mando. El hub muestra tarjetas; cada tarjeta abre
    // una pagina con su cabecera (atras + titulo + ayuda).
    public enum ConfigPage { Hub, Botones, Sticks, Gatillos, Touchpad }

    // Navegacion de un solo nivel: del hub se entra a una pagina y de una pagina se vuelve
    // al hub. Deliberadamente NO es una pila: no hay caminos de pagina a pagina, asi que
    // una pila solo podria desincronizarse. Pura (sin WPF) para poder probarla.
    public sealed class ConfigNav
    {
        public ConfigPage Current { get; private set; } = ConfigPage.Hub;
        public bool CanGoBack => Current != ConfigPage.Hub;

        public void Go(ConfigPage page) => Current = page;
        public void Back() => Current = ConfigPage.Hub;

        public static string TitleOf(ConfigPage page) => page switch
        {
            ConfigPage.Botones  => "Asignacion de botones",
            ConfigPage.Sticks   => "Sensibilidad y zona muerta de los sticks",
            ConfigPage.Gatillos => "Recorrido de los gatillos",
            ConfigPage.Touchpad => "Zonas del touchpad",
            _                   => "Configurar el mando",
        };
    }
}
```

- [ ] **Step 5: Verificar que pasan** + suite completa.
- [ ] **Step 6: Commit** — `git add -u && git add HidusbfModernGui/ConfigNav.cs && git commit -m "feat: ConfigNav - navegacion hub/paginas del configurador (TDD)"`

---

### Task 3: El hub de tarjetas + la cabecera común

**Files:**
- Modify: `HidusbfModernGui/Theme.xaml` (estilos `HubCard`, `PageHeader`)
- Modify: `HidusbfModernGui/MainWindow.xaml(.cs)`

**Depende de:** Task 2 (`ConfigNav`). **Absorbe** la limpieza de textos y el botón circular de opciones del plan anterior (su Task 4), que sigue siendo válida y aquí encuentra su sitio natural.

- [ ] **Step 1: Estilos.** En `Theme.xaml`, `HubCard` (Button): borde del tema, fondo `SurfaceBrush`, padding 20, `MinHeight` 120, hover → fondo `SurfaceAltBrush`; contenido libre para meter icono + título + descripción. `PageHeader` no es un estilo sino un bloque reutilizado (ver Step 3).
- [ ] **Step 2: El hub.** `ConfigHub` (Grid) contiene, en este orden:
  1. La tarjeta **MANDO VIRTUAL**: `ACTIVAR MANDO VIRTUAL` + botón redondo `?` con la explicación en `Popup` (texto actual, movido tal cual) + `MasterStatusText` **solo cuando el motor está encendido**.
  2. El **mando en vivo grande** (`ConfigPadVisual`, `MinHeight="360"`), con el botón circular de opciones (tuerca) anclado arriba a la derecha que despliega `RECARGAR SKIN`, `Modo calibracion`, `MODO STREAMER` y `Overlay atraviesa clic` (mismos handlers, solo se mueven).
  3. Un `UniformGrid Columns="2"` con las **cuatro tarjetas**: Botones, Sticks, Gatillos, Touchpad. Cada una: icono (los `Geometry` que ya hay en `Window.Resources`), título y una frase corta. `Click` → `GoToPage(ConfigPage.X)`.

  El **aviso de anticheat se muda a Ajustes** bajo el encabezado `RIESGO Y ANTICHEAT` (texto sin cambios).

- [ ] **Step 3: Cabecera de página.** Cada sub-página empieza con la misma fila: botón `‹` (atrás, `Click="ConfigBack_Click"`), el título (`ConfigNav.TitleOf`), y a la derecha el botón `?` de esa página. Se escribe una vez por página (cuatro veces en total) con los mismos estilos: repetir cuatro filas de XAML es más legible aquí que un control nuevo, y cada página necesita su propio texto de ayuda.
- [ ] **Step 4: Alternar.** En el code-behind:

```csharp
private readonly ConfigNav _configNav = new();

private void GoToPage(ConfigPage page)
{
    _configNav.Go(page);
    UpdateConfigPages();
}

private void ConfigBack_Click(object sender, RoutedEventArgs e)
{
    _configNav.Back();
    UpdateConfigPages();
}

// Un solo sitio decide que se ve: el resto de la app solo llama a Go/Back. Asi no hay
// dos caminos que puedan dejar dos paginas visibles a la vez.
private void UpdateConfigPages()
{
    ConfigHub.Visibility      = Vis(ConfigPage.Hub);
    PageBotones.Visibility    = Vis(ConfigPage.Botones);
    PageSticks.Visibility     = Vis(ConfigPage.Sticks);
    PageGatillos.Visibility   = Vis(ConfigPage.Gatillos);
    PageTouchpad.Visibility   = Vis(ConfigPage.Touchpad);

    Visibility Vis(ConfigPage p) => _configNav.Current == p ? Visibility.Visible : Visibility.Collapsed;
}
```

**Ojo con el feed:** `ConfigPadVisual` vive en el hub, así que al entrar a una sub-página deja de ser visible y `UpdateVisualizerRunState()` pararía el visualizador. La página de sticks (Task 5) tiene **su propio** `PadVisualHost`, así que la condición del feed pasa a ser: *"algún host del configurador visible O ventana streamer abierta"*. Ajustar `UpdateVisualizerRunState` para consultar ambos hosts (`ConfigPadVisual.IsVisible || SticksPadVisual.IsVisible || _streamerWindow != null`) y `VisualizerTick` para actualizar el que esté visible.

- [ ] **Step 5: Verificación** — build 0/0, suite completa PASS. Manual: el hub muestra las 4 tarjetas; cada una entra a su página; `‹` vuelve; el aviso está en Ajustes; el `?` abre la explicación; la tuerca despliega las 4 opciones y todas funcionan; el mando en vivo se mueve en el hub.
- [ ] **Step 6: Commit** — `git add -u && git commit -m "feat(ui): hub de tarjetas del configurador, cabecera con atras y opciones en un boton circular"`

---

### Task 4: Página BOTONES — diagrama estático con líneas guía

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml(.cs)`

**Modelo:** el mando **quieto** al centro; a cada lado, una columna de etiquetas tipo píldora unidas al botón correspondiente por una línea fina. La píldora muestra **a qué está asignado** ese botón (su propio símbolo si no está remapeado). Pulsar una píldora abre la lista de destinos. Abajo, **RESTABLECER** (vacía `ButtonRemap`). **No hay "Aplicar": se aplica al elegir.**

- [ ] **Step 1: El dibujo.** Un `Canvas` de tamaño base fijo dentro de un `Viewbox`, con: la silueta del mando (reutilizar `PadVisual` en modo estático — un `PadVisualHost` con `InteractiveRemap=false` al que **no se le llama `Update`**, así queda quieto), las `Line` guía y las píldoras (`Button` con estilo `PillButton`). Las coordenadas son fijas y se ajustan una vez contra el dibujo; criterio de aceptación: cada línea acaba visualmente en su botón.
- [ ] **Step 2: Las píldoras.** Una por botón remapeable (los 16 de `RemapTargets` menos PS/touchpad-click si se decide dejarlos fuera). `Tag` = `PadButton` origen. Su texto se refresca desde `_remap.ButtonRemap` en `RefreshButtonPills()`, llamado al entrar a la página y tras cada cambio.
- [ ] **Step 3: Elegir destino.** Al pulsar una píldora se abre un `Popup` anclado a ella con la lista de destinos (los mismos `RemapTargets` de hoy, incluido "Ninguno" para limpiar). Al elegir: escribir en `_remap.ButtonRemap` (o `Remove` si es identidad/Ninguno), `RememberRemap()`, `RefreshButtonPills()`.
- [ ] **Step 4: Restablecer.** Botón `RESTABLECER` que hace `_remap.ButtonRemap.Clear(); RememberRemap(); RefreshButtonPills();`.
- [ ] **Step 5: Retirar lo viejo.** Eliminar la sección de filas de desplegables (`BuildButtonRemapRows` y su contenedor `BotonRows`) — el compilador señala referencias supervivientes.
- [ ] **Step 6: Verificación** — build 0/0, suite PASS. Manual: cambiar Cruz→Cuadrado desde la píldora; con el mando virtual activo, pulsar Cruz en el físico dispara Cuadrado en joy.cpl; RESTABLECER deja todas las píldoras con su símbolo propio.
- [ ] **Step 7: Commit** — `git add -u && git commit -m "feat(ui): pagina de botones con diagrama estatico y etiquetas guia"`

---

### Task 5: Página STICKS — izquierda / mando en vivo / derecha, con entrada cruda vs ajustada

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml(.cs)`

**Modelo:** tres columnas. Izquierda y derecha son los controles de cada stick (zona muerta, alcance, RESPUESTA, la gráfica de curva con sus puntos, MIS CURVAS) — **el contenido actual, movido, sin cambios funcionales**. En el centro, un `PadVisualHost` en vivo (`SticksPadVisual`) con **dos marcadores superpuestos** sobre el stick seleccionado: **entrada del mando** (gris) y **entrada ajustada** (blanco).

- [ ] **Step 1: Las tres columnas.** `Grid` con `ColumnDefinitions` `*`,`Auto`,`*`; el centro con `MinWidth="360"`. Mover dentro los dos bloques de stick que ya existen.
- [ ] **Step 2: Los dos marcadores.** Sobre el `SticksPadVisual`, un `Canvas` transparente con dos `Ellipse` (`RawDot` gris, `AdjDot` blanca). En `VisualizerTick`, cuando la página de sticks esté visible, colocarlas con la misma aritmética que ya usa el visualizador:

```csharp
// Dos puntos sobre el pozo del stick: donde esta TU pulgar (crudo) y lo que recibe el
// juego (ajustado). La distancia entre ambos es, literalmente, lo que hace tu
// configuracion - es la explicacion que ningun texto consigue dar.
private void UpdateStickDots(ControllerState raw, ControllerState outState)
{
    if (RawDot == null || !PageSticks.IsVisible) return;
    PlaceDot(RawDot, raw.Left,      StickDotCenterX, StickDotCenterY, StickDotRadius);
    PlaceDot(AdjDot, outState.Left, StickDotCenterX, StickDotCenterY, StickDotRadius);
}

private static void PlaceDot(System.Windows.Shapes.Ellipse dot, StickInput s,
                             double cx, double cy, double r)
{
    var (dx, dy) = PadVisualMath.StickOffset(s.X, s.Y, r);
    Canvas.SetLeft(dot, cx + dx - dot.Width / 2);
    Canvas.SetTop(dot, cy + dy - dot.Height / 2);
}
```

Una leyenda debajo: "Entrada del mando" (gris) / "Entrada ajustada" (blanca). Con el stick izquierdo/derecho: mostrar el par de puntos del stick cuya columna tiene el foco, o ambos pares a la vez si resulta legible — decidir en la prueba manual del Step 4 y dejarlo comentado.

- [ ] **Step 3: El punto vivo de la gráfica se queda.** Ya existe y sigue siendo útil (entrada→salida sobre la curva). No se duplica trabajo: `UpdateCurveLiveDot` no cambia.
- [ ] **Step 4: Verificación** — build 0/0, suite PASS. Manual: mover el stick mueve los dos puntos; con zona muerta al 30% el punto blanco **no se mueve** hasta pasar el umbral mientras el gris sí — esa diferencia es la demostración visual del ajuste.
- [ ] **Step 5: Commit** — `git add -u && git commit -m "feat(ui): pagina de sticks en tres columnas con entrada cruda vs ajustada"`

---

### Task 6: Página GATILLOS — rango Desde/Hasta con steppers y profundidad en vivo

**Files:**
- Modify: `HidusbfModernGui/Theme.xaml` (estilo `Stepper`)
- Modify: `HidusbfModernGui/MainWindow.xaml(.cs)`

**Depende de:** Task 1 (el rango ya existe en el núcleo).

- [ ] **Step 1: Interruptor "misma configuración para L2 y R2".** Un `CheckBox` arriba, centrado. Cuando está marcado, cualquier cambio en un lado se copia al otro (y se aplica al entrar).
- [ ] **Step 2: Los steppers.** Por gatillo, dos campos numéricos `Desde` y `Hasta` (0–100) con botones `+` y `−` (estilo `Stepper`: `TextBox` de solo dígitos + dos botones cuadrados pequeños). Validación: recortar a 0–100 y mantener `Hasta ≥ Desde` (misma regla que `Sanitize`, aplicada al escribir para que la UI no pueda crear un estado que el núcleo tenga que arreglar).
- [ ] **Step 3: Profundidad en vivo.** Por gatillo, una barra vertical (o horizontal) que muestra el recorrido **físico** actual, con dos marcas: `Desde` y `Hasta`. Alimentada desde `VisualizerTick` con el `raw` (no el transformado): es la referencia contra la que el usuario coloca el rango.
- [ ] **Step 4: Retirar lo viejo.** Los sliders `L2PointSlider`/`R2PointSlider` y sus textos desaparecen; `L2PointPct`/`R2PointPct` permanecen en el modelo solo para compatibilidad (Task 1) y ya no se editan desde la UI.
- [ ] **Step 5: Verificación** — build 0/0, suite PASS. Manual: apretar el gatillo mueve la barra; poner Desde=20/Hasta=80 y comprobar en joy.cpl que el eje llega a fondo antes del tope físico y que el primer tramo no hace nada; el interruptor "ambos" replica los valores.
- [ ] **Step 6: Commit** — `git add -u && git commit -m "feat(ui): pagina de gatillos con rango Desde/Hasta y profundidad en vivo"`

---

### Task 7: Página TOUCHPAD — cuatro zonas visuales

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml(.cs)`

- [ ] **Step 1: El dibujo.** Un rectángulo con la proporción del touchpad dividido en 4 cuadrantes por dos líneas finas. Cada cuadrante es un `Button` (estilo transparente con borde al pasar el ratón) que muestra el botón asignado a esa zona, o "Sin asignar".
- [ ] **Step 2: Asignar.** Pulsar un cuadrante abre el mismo `Popup` de destinos de la Task 4; escribe en `_remap.TouchZoneMap`, `RememberRemap()`, refresca las etiquetas.
- [ ] **Step 3: Toque en vivo.** Si `raw.TouchActive`, marcar el cuadrante tocado (borde resaltado) usando `InputTransform.ResolveTouchZone` — así el usuario ve qué zona está pulsando de verdad antes de asignarla.
- [ ] **Step 4: Retirar lo viejo.** Eliminar `BuildTouchZoneCombos` y `TouchZoneGrid`.
- [ ] **Step 5: Verificación** — build 0/0, suite PASS. Manual: tocar cada esquina del touchpad resalta el cuadrante correcto; asignar Triángulo a Arriba-Izquierda y comprobarlo en joy.cpl.
- [ ] **Step 6: Commit** — `git add -u && git commit -m "feat(ui): pagina de touchpad con las cuatro zonas visuales y toque en vivo"`

---

### Task 8: Perfiles unificados (del plan anterior)

Se ejecutan **sin cambios** las Tasks 1 y 7 del plan `2026-07-25-rediseno-ui-mando.md`: `GameProfile` + `GameProfileStore` con migración (TDD) y la `ProfilesBar` compartida por el configurador y las luces. Encajan aquí sin tocar: la barra de perfiles va en el **hub**, bajo las tarjetas.

- [ ] **Step 1** — ejecutar Task 1 de ese plan (`GameProfile`, TDD).
- [ ] **Step 2** — ejecutar Task 7 de ese plan (`ProfilesBar` + migración al arrancar), colocando la barra en el hub.
- [ ] **Step 3: Commit** — el de esas tareas.

---

### Task 9: Documentación y verificación integral

**Files:** `README.md`, `docs/DOCUMENTACION.md`

- [ ] **Step 1: README** — el configurador se describe como hub + páginas; los gatillos se ajustan por rango; el remapeo se hace sobre un diagrama.
- [ ] **Step 2: `docs/DOCUMENTACION.md`** — añadir `ConfigNav.cs` al mapa de módulos; documentar el **rango de gatillo y su migración** (punto → rango degenerado) y la decisión de **no** copiar el "Aplicar" de Sony (aplicación en vivo, con el porqué).
- [ ] **Step 3** — `dotnet test` completo verde; `.\package.ps1` termina en "Package ready" sin warnings nuevos.
- [ ] **Step 4: Prueba integral (usuario, hardware)** — recorrer las 4 páginas; remapear; ajustar un rango de gatillo y sentirlo en un juego; guardar y cargar un perfil unificado.
- [ ] **Step 5: Commit** — `git add -u && git commit -m "docs: configurador con modelo hub/paginas y gatillos por rango"`

---

## Self-review

- **Cobertura del pedido:** tarjetas grandes con icono/título/descripción (Task 3); diagrama estático con líneas guía y restablecer (Task 4); página de sticks con columnas y mando central resaltando la entrada (Task 5); gatillos con "misma config para ambos" y Desde/Hasta 0–100 (Tasks 1 y 6); botón atrás en todas las páginas (Tasks 2 y 3); crítica y mejoras respecto al modelo (sección "Crítica"). ✓
- **Crítica pedida, entregada:** se adoptan cuatro cosas del modelo, se rechazan cuatro con motivo — la más importante, **no copiar "Aplicar"**, porque la aplicación en vivo es una ventaja real de UltraPolling para ajustar curvas. ✓
- **Placeholders:** el núcleo (Task 1) y la navegación (Task 2) llevan código completo y tests; las páginas llevan estructura, contrato y criterio de aceptación manual — el patrón ya usado para `PadVisual` y `SkinnedPadVisual`. ✓
- **Tipos consistentes:** `ApplyTrigger(value, from, to)` (Task 1) consumido por `RemapEngine` y por la página de gatillos (Task 6); `ConfigNav`/`ConfigPage` (Task 2) consumidos por el shell (Task 3) y por la condición del feed; `PadVisualMath.StickOffset` reutilizado en la Task 5. ✓
- **Compatibilidad:** los perfiles viejos migran solos y siguen comportándose igual (rango degenerado); `L2PointPct` no se borra del JSON. ✓
- **Riesgo del hub (un clic más) reconocido y compensado:** interruptor maestro y mando en vivo se quedan en el hub. ✓
- **Simplificación ganada:** adoptar el diagrama estático **elimina** la necesidad de `PadHitZones` y del hit-testing del plan anterior. Menos código que mantener. ✓
