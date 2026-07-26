# Página de dispositivos: tarjetas, y una medida que no exagere — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rehacer la página de dispositivos según la maqueta: la lista pasa de filas planas a **tarjetas** con icono y distintivo de estado, y el panel de detalle se parte en tres tarjetas (**MEDIDA**, **ESPECIFICACIONES TÉCNICAS** con el instance id, **CONFIGURACIÓN DE TASA**). Sin morado y sin afirmar nada que no se mida.

**Architecture:** El contenido ya existe casi entero —`VELOCIDAD`, `bINTERVAL`, `FILTRO`, `INTERVALO`, la medida grande, el combo de tasa y los dos botones están en `MainWindow.xaml` desde hace tiempo—, así que esto es sobre todo **presentación**. Lo único nuevo de verdad es el distintivo de regularidad, que exige medir algo que hoy no se mide: `PollingCore.Summarise` gana el percentil 95 de los huecos (con tests, porque ese archivo sí está enlazado en el proyecto de pruebas) y un clasificador puro decide qué palabra mostrar.

**Tech Stack:** .NET 9 WPF, xUnit. Sin dependencias nuevas.

## Contexto verificado (leído en el código, no supuesto)

- `RateSample` es `readonly record struct RateSample(double MedianGapMs, double MinGapMs, double MaxGapMs, int Count)` con `MedianHz => PollingCore.RateFromGapMs(MedianGapMs)`. **Ya trae `Count`, `MinGapMs` y `MaxGapMs`** — sólo falta el percentil.
- Lo construye `PollingCore.Summarise(IReadOnlyList<double> gapsMs)` en `PollingCore.cs`, que ordena los huecos en `sorted` y devuelve `new RateSample(median, sorted[0], sorted[^1], sorted.Length)`. Devuelve `null` con la lista vacía.
- **`PollingCore.cs` está enlazado en `HidusbfModernGui.Tests.csproj`** (línea 19), así que el percentil se prueba de verdad. `PollingMeter.cs` también está enlazado (línea 22).
- `PollingMeter.ReadLoop` marca `_clock.Elapsed.TotalMilliseconds` en cada reporte y encola `now - _lastReportMs`. Ventana de `WindowSize = 1024` huecos.
- `Snapshot(int quietMs = 2000)` devuelve `null` si no ha llegado nada o si el flujo lleva más de 2 s callado.
- El panel de detalle ya tiene `MEDIDA` (`MeasuredText`, estilo `RateDisplay` 46 px), `MeasuredGapText`, la fila `PEDIDA` con `MeasuredDot`, el aviso `MatchHintText` en ámbar, la rejilla de cuatro especificaciones, `DetailRateCombo` y los dos botones.
- La lista `DevicesListBox` usa un `ItemContainerStyle` con `Border x:Name="Row"` de `BorderThickness="0,0,0,1"` y una barra izquierda de 2 px al seleccionar.
- Paleta de `Theme.xaml`: `BgBrush #000000`, `SurfaceBrush #0A0A0A`, `SurfaceAltBrush #111111`, `BorderBrush #1F1F1F`, `TextDataBrush #FFFFFF`, `TextLabelBrush #8A8A8A`, `TextMutedBrush #4A4A4A`, y los de estado `StatusOkBrush #00C853`, `StatusWarnBrush #FFAB00`, `StatusErrorBrush #FF3D00`.
- Suite hoy: **`Passed: 402`**.

## Tres decisiones ya tomadas por el usuario

**1. Nada de morado.** La maqueta era morada; la app se queda monocroma, como manda la regla escrita en `Theme.xaml`: el color sólo codifica un hecho del sistema, y todo lo demás es negro, gris o blanco. La selección y el botón primario se resuelven con **contraste**, no con color (Task 3). Verde, ámbar y rojo siguen siendo exclusivamente estado.

**2. La palabra "ESTABLE" no se usa.** El distintivo dice **`REGULAR`** / **`IRREGULAR`** / **`SIN DATOS`**, que además es más exacto: lo que se mide es la regularidad del flujo de reportes, no una propiedad del mando.

**3. `LATENCIA` no se usa, porque no se mide.** El medidor marca la hora de llegada de cada reporte y guarda el hueco entre uno y el siguiente. La latencia sería el retardo desde que se mueve el stick hasta que el juego lo ve, y esa cadena tiene eslabones que esta app no puede ver: el firmware del mando, la espera al sondeo, la pila HID, y el motor del juego. El reporte del DualSense **no trae marca de tiempo de origen**, así que el instante del movimiento es desconocido y la resta es imposible — no falta código, falta el dato. La tasa de sondeo sólo acota **un** eslabón (la espera): de 1000 a 8000 Hz esa espera media baja de ~0,5 ms a ~0,06 ms, mientras el retardo total de mando a pantalla sigue siendo de decenas de milisegundos. Rotular `0,125 ms` como latencia afirmaría un retardo total falso por dos órdenes de magnitud. Se rotula **ENTRE REPORTES**.

**Y algo que la maqueta perdía y aquí no se pierde:** la maqueta enseña sólo MEDIDA. La comparación **medida contra pedida** —el punto y el aviso ámbar que explica que escribir `bInterval` no reconfigura nada hasta re-enumerar— es la razón de ser de esta pantalla y se conserva entera dentro de la tarjeta de MEDIDA.

## Global Constraints

- UI en **español**. Comentarios de código **en español y sin tildes**.
- **Monocromo.** Ningún pincel de color nuevo. Los únicos colores siguen siendo `StatusOk/Warn/Error`, y sólo para estado.
- La tasa sigue necesitando `APLICAR CAMBIOS` y su replug, como hoy. Los handlers existentes **no se renombran**.
- El proyecto de tests enlaza fuentes una a una; `RateStability.cs` hay que añadirlo al csproj. Nada que toque WPF se puede enlazar.
- Commits **sin** Co-Authored-By. El push lo hace el usuario.

## Estructura de archivos

| Archivo | Responsabilidad |
|---|---|
| `HidusbfModernGui/RateStability.cs` (nuevo) | Clasifica una tanda en regular / irregular / sin datos. Sin WPF. |
| `HidusbfModernGui/PollingCore.cs` | `RateSample` gana `P95GapMs`; `Summarise` lo calcula. |
| `HidusbfModernGui/Theme.xaml` | `PrimaryButton`, `DeviceCard`, `StatusChip`, `ChipText`. Sin colores nuevos. |
| `HidusbfModernGui/MainWindow.xaml(.cs)` | Lista en tarjetas, detalle en tres tarjetas, distintivo. |

---

### Task 1: El percentil 95 en `RateSample` (TDD)

**Files:**
- Modify: `HidusbfModernGui/PollingCore.cs`
- Test: `HidusbfModernGui.Tests/PollingCoreTests.cs` (existe; añadir casos)

**Interfaces:**
- Produces: `RateSample` gana un quinto campo posicional `double P95GapMs`. Consumido por Task 2 (para clasificar) y Task 5 (para mostrar).

**Por qué el percentil y no el máximo que ya hay.** `RateSample` ya trae `MaxGapMs`, pero el máximo no sirve para juzgar el flujo: un solo hueco —un pico del planificador de Windows, una pausa del GC— dispara el máximo aunque los otros 1023 huecos sean perfectos. El percentil 95 responde a la pregunta que importa, "¿casi todos llegan a tiempo?", y es inmune a un puñado de valores extremos.

**Aviso al implementador:** `RateSample` es un `record struct` **posicional**. Añadir un parámetro cambia el constructor primario, así que **toda** construcción existente deja de compilar. Antes de tocar nada, `grep -rn "new RateSample" .` y arregla cada sitio. Ponlo **al final** de la lista de parámetros para no reordenar los que ya hay.

- [ ] **Step 1: Tests que fallan** — añadir a `HidusbfModernGui.Tests/PollingCoreTests.cs`:

```csharp
// Con huecos perfectamente iguales, el p95 tiene que coincidir con la mediana: si no,
// el calculo esta metiendo dispersion donde no la hay.
[Fact]
public void P95_OnAFlatRun_EqualsTheMedian()
{
    var gaps = Enumerable.Repeat(0.125, 200).ToList();
    var s = PollingCore.Summarise(gaps);
    Assert.NotNull(s);
    Assert.Equal(0.125, s!.Value.P95GapMs, 6);
}

// 100 huecos: 95 buenos y 5 malos. El p95 debe caer en la frontera, no arrastrado por
// los 5 malos (eso seria el maximo) ni ciego a ellos (eso seria la mediana).
[Fact]
public void P95_IgnoresTheWorstFivePercentButSeesTheRest()
{
    var gaps = Enumerable.Repeat(1.0, 95).Concat(Enumerable.Repeat(9.0, 5)).ToList();
    var s = PollingCore.Summarise(gaps);
    Assert.NotNull(s);
    Assert.Equal(1.0, s!.Value.MedianGapMs, 6);
    Assert.Equal(9.0, s.Value.MaxGapMs, 6);   // el maximo si los ve
    Assert.Equal(1.0, s.Value.P95GapMs, 6);   // el p95 no se deja arrastrar
}

// Un solo hueco: el p95 no puede salirse del array. Es el caso que revienta si el
// indice se calcula sin acotar.
[Fact]
public void P95_WithASingleGap_IsThatGap()
{
    var s = PollingCore.Summarise(new List<double> { 0.5 });
    Assert.NotNull(s);
    Assert.Equal(0.5, s!.Value.P95GapMs, 6);
}

// El p95 nunca puede quedar por debajo de la mediana ni por encima del maximo.
// Invariante que tiene que aguantar con una tanda irregular de verdad.
[Fact]
public void P95_SitsBetweenTheMedianAndTheMax()
{
    var gaps = new List<double> { 0.1, 0.2, 0.15, 3.0, 0.12, 0.9, 0.13, 0.11, 2.2, 0.14 };
    var s = PollingCore.Summarise(gaps);
    Assert.NotNull(s);
    Assert.True(s!.Value.P95GapMs >= s.Value.MedianGapMs);
    Assert.True(s.Value.P95GapMs <= s.Value.MaxGapMs);
}
```

Añade `using System.Linq;` al archivo de tests si no está.

- [ ] **Step 2: Verificar que fallan** (no compila: `P95GapMs` no existe).

- [ ] **Step 3: Implementación** — en `PollingCore.cs`, añadir el campo al final del constructor posicional:

```csharp
public readonly record struct RateSample(double MedianGapMs, double MinGapMs, double MaxGapMs, int Count, double P95GapMs)
```

y calcularlo en `Summarise`, reutilizando el `sorted` que ya existe:

```csharp
// El percentil 95 sale del MISMO array ya ordenado que da la mediana: ordenar otra vez
// seria repetir trabajo sobre una tanda que puede ser de 1024 huecos.
//
// Rango mas cercano (ceil), NO truncado. Con 100 muestras 100*0.95 vale exactamente 95.0
// en coma flotante, y truncar apuntaria al elemento 96 - o sea, al primero de los MALOS.
// ceil(n*0.95)-1 da 94, el ultimo de los buenos, que es lo que "el 95% llega dentro de"
// significa. El indice nunca se sale: ceil(0.95n) <= n para todo n >= 1.
int p95Index = (int)Math.Ceiling(sorted.Length * 0.95) - 1;

return new RateSample(median, sorted[0], sorted[sorted.Length - 1], sorted.Length, sorted[p95Index]);
```

**Comprueba el nombre real del array ordenado** en el método; si no se llama `sorted`, usa el que haya, y **no** introduzcas una segunda ordenación.

- [ ] **Step 4: Verificar que pasan** — suite completa PASS. De `402` a **`406`** (4 casos nuevos). Si sale otro número, revisa.

- [ ] **Step 5: Commit**

```bash
git add -u && git commit -m "feat: RateSample expone el percentil 95 de los huecos (TDD)"
```

---

### Task 2: `RateStability` — la palabra sólo cuando hay con qué (TDD)

**Files:**
- Create: `HidusbfModernGui/RateStability.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj`
- Test: `HidusbfModernGui.Tests/RateStabilityTests.cs`

**Interfaces:**
- Consumes: nada de Task 1 en tiempo de compilación (recibe `double`s sueltos, a propósito: así el clasificador se prueba sin construir un `RateSample`).
- Produces: `enum RateSteadiness { NoData, Regular, Irregular }`; `RateStability.Classify(double medianGapMs, double p95GapMs, int sampleCount) -> RateSteadiness`; constantes `MinSamples` (30) y `JitterCeiling` (1.35). Consumido por Task 5.

**El criterio.** Se compara el hueco del percentil 95 contra la mediana. Si el 95 % de los reportes llega dentro de un 35 % del intervalo mediano, el flujo es regular; por encima de eso hay huecos lo bastante grandes como para notarse. Por debajo de 30 muestras no se clasifica: afirmar regularidad con cuatro reportes es adivinar, y esta pantalla existe para no adivinar.

- [ ] **Step 1: Enlazar en el csproj**, junto a las demás líneas `<Compile Include=...>`:

```xml
<Compile Include="..\HidusbfModernGui\RateStability.cs" Link="RateStability.cs" />
```

- [ ] **Step 2: Tests que fallan** — crear `HidusbfModernGui.Tests/RateStabilityTests.cs`:

```csharp
using HidusbfModernGui;
using Xunit;

public class RateStabilityTests
{
    [Fact]
    public void TooFewSamples_IsNoData()
        => Assert.Equal(RateSteadiness.NoData,
                        RateStability.Classify(0.125, 0.130, RateStability.MinSamples - 1));

    [Fact]
    public void AtTheSampleFloor_ItDoesClassify()
        => Assert.Equal(RateSteadiness.Regular,
                        RateStability.Classify(0.125, 0.130, RateStability.MinSamples));

    // Un mando a 8000 Hz cuyo p95 se sale un 4% de la mediana: eso es ir fino.
    [Fact]
    public void TightSpread_IsRegular()
        => Assert.Equal(RateSteadiness.Regular, RateStability.Classify(0.125, 0.130, 500));

    // El p95 al doble de la mediana: uno de cada veinte reportes llega tardisimo.
    [Fact]
    public void WideSpread_IsIrregular()
        => Assert.Equal(RateSteadiness.Irregular, RateStability.Classify(0.125, 0.250, 500));

    // El limite es inclusivo: justo en el techo todavia cuenta como regular, para que un
    // dispositivo que roza el umbral no parpadee entre los dos estados en cada refresco.
    [Fact]
    public void ExactlyAtTheCeiling_IsRegular()
        => Assert.Equal(RateSteadiness.Regular,
                        RateStability.Classify(1.0, RateStability.JitterCeiling, 500));

    [Fact]
    public void JustOverTheCeiling_IsIrregular()
        => Assert.Equal(RateSteadiness.Irregular,
                        RateStability.Classify(1.0, RateStability.JitterCeiling + 0.01, 500));

    // Numeros imposibles no se interpretan: una mediana cero o negativa significa que la
    // medida no sirve, no que el mando vaya infinitamente rapido.
    [Theory]
    [InlineData(0.0, 0.130)]
    [InlineData(-1.0, 0.130)]
    [InlineData(0.125, -0.1)]
    public void ImpossibleNumbers_AreNoData(double median, double p95)
        => Assert.Equal(RateSteadiness.NoData, RateStability.Classify(median, p95, 500));

    // Un p95 por DEBAJO de la mediana es imposible por definicion; si llegara, no debe
    // leerse como "muy regular": es una medida rota.
    [Fact]
    public void P95BelowTheMedian_IsNoData()
        => Assert.Equal(RateSteadiness.NoData, RateStability.Classify(0.125, 0.100, 500));
}
```

- [ ] **Step 3: Verificar que fallan** (no compila).

- [ ] **Step 4: Implementación** — crear `HidusbfModernGui/RateStability.cs`:

```csharp
namespace HidusbfModernGui
{
    // Que tan regular llega el flujo de reportes. NoData no es un fallo: es no haber medido
    // lo suficiente para decir nada, y decirlo es mejor que adivinar.
    //
    // Se habla de REGULARIDAD DEL FLUJO, no de estabilidad del mando: lo que se observa son
    // las horas de llegada de los reportes, y eso puede irregularizarse por el planificador
    // de Windows o por el hub USB sin que al mando le pase nada.
    public enum RateSteadiness { NoData, Regular, Irregular }

    // Compara el hueco del percentil 95 contra la mediana. Si el 95% de los reportes cae
    // dentro de un 35% del intervalo mediano, el flujo es regular.
    //
    // p95 y no el maximo -que RateSample ya trae-: un unico hueco, un pico del planificador
    // o una pausa del GC, dispara el maximo aunque los otros mil huecos sean perfectos.
    // Y no desviacion tipica: un punado de huecos enormes la disparan, y aqui lo que importa
    // es "casi todos llegan a tiempo", no la forma de la distribucion.
    public static class RateStability
    {
        // Por debajo de esto no se clasifica.
        public const int MinSamples = 30;

        // Inclusivo a proposito: justo en el techo cuenta como regular, para que un
        // dispositivo que roza el umbral no parpadee entre los dos estados en cada refresco.
        public const double JitterCeiling = 1.35;

        public static RateSteadiness Classify(double medianGapMs, double p95GapMs, int sampleCount)
        {
            if (sampleCount < MinSamples) return RateSteadiness.NoData;
            if (medianGapMs <= 0 || p95GapMs <= 0) return RateSteadiness.NoData;

            // Imposible por definicion: si llega, la medida esta rota y no se interpreta.
            if (p95GapMs < medianGapMs) return RateSteadiness.NoData;

            return p95GapMs <= medianGapMs * JitterCeiling
                ? RateSteadiness.Regular
                : RateSteadiness.Irregular;
        }
    }
}
```

- [ ] **Step 5: Verificar que pasan** — filtro `RateStabilityTests` PASS y suite completa PASS. De `406` a **`416`** (7 `[Fact]` + 3 casos de la `[Theory]`).

- [ ] **Step 6: Commit**

```bash
git add HidusbfModernGui/RateStability.cs HidusbfModernGui.Tests/RateStabilityTests.cs HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj
git commit -m "feat: RateStability - clasificar la regularidad del flujo (TDD)"
```

---

### Task 3: Estilos de tarjeta y distintivo, en monocromo

**Files:**
- Modify: `HidusbfModernGui/Theme.xaml`

**Interfaces:**
- Produces: `PrimaryButton`, `DeviceCard` (`Style` de `Border`), `StatusChip` (`Style` de `Border`), `ChipText` (`Style` de `TextBlock`). Consumidos por Tasks 4 y 5.

**Cómo se resuelve la jerarquía sin color.** La maqueta usaba morado para dos cosas: marcar la tarjeta seleccionada y destacar el botón de aplicar. En monocromo eso se hace con **contraste**, que es más fuerte que el color y no rompe la regla:

- **Seleccionada:** borde blanco (`TextDataBrush`) y fondo un punto más claro (`SurfaceAltBrush`). Sin cambiar el *grosor*, para que el texto no salte 1 px al cambiar de fila — el error clásico de este patrón.
- **Botón primario:** invertido, fondo blanco y letra negra. Es el mayor énfasis disponible en una paleta monocroma.

- [ ] **Step 1: `PrimaryButton`**, junto a los demás estilos de botón:

```xml
<!-- Accion primaria de una pagina, invertida: fondo blanco, letra negra. Es el mayor
     enfasis que permite una paleta monocroma, y no gasta un color de estado para decorar.
     Solo debe haber UNA visible a la vez: si dos botones gritan igual, ninguno destaca. -->
<Style x:Key="PrimaryButton" TargetType="Button" BasedOn="{StaticResource InstrumentButton}">
    <Setter Property="Background" Value="{StaticResource TextDataBrush}"/>
    <Setter Property="Foreground" Value="{StaticResource BgBrush}"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
</Style>
```

**Comprueba** que la plantilla de `InstrumentButton` toma `Background`/`Foreground` por `TemplateBinding` y que su disparador de `IsMouseOver` no fija un fondo que pise al nuestro. Si los tiene fijos, no heredes: escribe la plantilla completa con un `IsMouseOver` que aclare a `#E0E0E0`. Dilo en el informe.

- [ ] **Step 2: `DeviceCard`** — el `Border` de cada fila de la lista:

```xml
<Style x:Key="DeviceCard" TargetType="Border">
    <Setter Property="Background" Value="{StaticResource SurfaceBrush}"/>
    <Setter Property="BorderBrush" Value="{StaticResource BorderBrush}"/>
    <!-- Grosor constante 1 a proposito. Engordar el borde al seleccionar movería el
         contenido 1 px cada vez que cambias de fila; la seleccion se marca con el COLOR
         del borde y el fondo, no con el grosor. -->
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="CornerRadius" Value="10"/>
    <Setter Property="Padding" Value="14,12"/>
    <Setter Property="Margin" Value="0,0,0,8"/>
</Style>
```

- [ ] **Step 3: `StatusChip` y `ChipText`** — la pastilla (`● ACTIVO - 8000 Hz`, `REGULAR`):

```xml
<!-- Pastilla de estado: punto de color + texto corto. El punto lleva el color del estado
     -verde, ambar o gris-; el fondo y el borde son neutros, para que el unico color de la
     pastilla siga significando exactamente una cosa. -->
<Style x:Key="StatusChip" TargetType="Border">
    <Setter Property="Background" Value="{StaticResource SurfaceAltBrush}"/>
    <Setter Property="BorderBrush" Value="{StaticResource BorderBrush}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="CornerRadius" Value="9"/>
    <Setter Property="Padding" Value="8,3"/>
    <Setter Property="HorizontalAlignment" Value="Left"/>
</Style>

<Style x:Key="ChipText" TargetType="TextBlock" BasedOn="{StaticResource DataText}">
    <Setter Property="FontSize" Value="10"/>
    <Setter Property="VerticalAlignment" Value="Center"/>
</Style>
```

**Comprueba que `DataText` existe** con ese nombre exacto antes de basarte en él.

- [ ] **Step 4: Verificación** — build 0/0, suite completa PASS (`Passed: 416`). Nada cambia en pantalla todavía: nadie consume estos estilos aún.

- [ ] **Step 5: Commit**

```bash
git add -u && git commit -m "feat(ui): estilos de tarjeta, pastilla de estado y boton primario invertido"
```

---

### Task 4: La lista, en tarjetas

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml` (encabezado y `DevicesListBox`)
- Modify: `HidusbfModernGui/UsbDeviceModel.cs`

**Interfaces:**
- Consumes: `DeviceCard`, `StatusChip`, `ChipText` (Task 3).
- Produces: `UsbDeviceModel.HasRate` y `UsbDeviceModel.ActiveChipText`. Sólo los usa esta tarea.

- [ ] **Step 1: El encabezado.** `DISPOSITIVOS` pasa a `DISPOSITIVOS CONECTADOS` con el contador entre paréntesis. El botón de refrescar se queda a la derecha, sin tocar su handler.

- [ ] **Step 2: Las dos propiedades** en `UsbDeviceModel.cs`:

```csharp
// El distintivo solo aparece si el dispositivo tiene una tasa puesta: en los que van por
// defecto seria una pastilla repitiendo "Default" en cada fila, ruido en toda la lista.
public bool HasRate => SelectedRate is not null and not 0;

// "ACTIVO - 8000 Hz". Se muestra la tasa RESUELTA, no la ranura cruda: en Low/Full Speed
// con el driver parcheado la ranura 31 vale 2000 Hz, y ensenar "31 Hz" seria mentir.
public string ActiveChipText => HasRate ? $"ACTIVO - {DisplayRate}" : "";
```

**Comprueba el nombre y tipo reales** de la propiedad de tasa seleccionada en la clase antes de escribir `SelectedRate`; si es otra, usa la que haya. Y comprueba que `DisplayRate` ya incluye el sufijo `Hz` — si lo incluye, no lo dupliques en la cadena.

- [ ] **Step 3: El contenedor de fila.** En el `ItemContainerStyle`, cambiar el `Border x:Name="Row"` por uno con `Style="{StaticResource DeviceCard}"`, y el disparador de selección a esto:

```xml
<Trigger Property="IsSelected" Value="True">
    <Setter TargetName="Row" Property="BorderBrush" Value="{StaticResource TextDataBrush}"/>
    <Setter TargetName="Row" Property="Background"  Value="{StaticResource SurfaceAltBrush}"/>
</Trigger>
```

**Quita** cualquier `Margin` que el `ListBoxItem` tuviera: la separación ahora la da el `Margin` del `DeviceCard`, y sumadas dejarían un hueco doble. La barra izquierda de 2 px que había puede quedarse, en `TextDataBrush`, o irse — el borde blanco ya marca la selección; decide y dilo en el informe.

- [ ] **Step 4: El contenido de la fila.** El `DataTemplate` mantiene icono + nombre + `ChildrenSummary` y añade debajo:

```xml
<Border Style="{StaticResource StatusChip}" Margin="0,8,0,0"
        Visibility="{Binding HasRate, Converter={StaticResource BoolToVisibility}}">
    <StackPanel Orientation="Horizontal">
        <Ellipse Width="6" Height="6" Margin="0,0,6,0" VerticalAlignment="Center"
                 Fill="{Binding StatusDot, Converter={StaticResource StatusToBrush}}"/>
        <TextBlock Text="{Binding ActiveChipText}" Style="{StaticResource ChipText}"/>
    </StackPanel>
</Border>
```

**Antes de escribirlo, comprueba en los recursos de la ventana si ya existen** un `BooleanToVisibilityConverter` y un convertidor de `StatusDot` a pincel, y usa los nombres reales. Si el de estado no existe, mira cómo pinta hoy `MeasuredDot` el code-behind y haz lo mismo en vez de inventar un convertidor nuevo.

- [ ] **Step 5: Verificación** — build 0/0, suite completa PASS (`Passed: 416`). Manual: las filas se ven como tarjetas separadas; la seleccionada lleva borde blanco y fondo más claro **sin que el texto salte**; el distintivo sale sólo en las que tienen tasa puesta; el contador del encabezado cuadra con el número de filas.

- [ ] **Step 6: Commit**

```bash
git add -u && git commit -m "feat(ui): la lista de dispositivos, en tarjetas con pastilla de estado"
```

---

### Task 5: El detalle, en tres tarjetas

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml` (panel de detalle)
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (el método que escribe `MeasuredText` / `MeasuredGapText`)

**Interfaces:**
- Consumes: `RateSample.P95GapMs` y `.Count` (Task 1), `RateStability.Classify` y `RateSteadiness` (Task 2), los estilos de Task 3.

- [ ] **Step 1: Tarjeta MEDIDA.** De arriba abajo: el rótulo `MEDIDA` con el distintivo de regularidad **a la derecha**; el número grande (`MeasuredText`, se queda como está); la línea **`ENTRE REPORTES`** con `MeasuredGapText`; y —conservados enteros, sin recortar— la fila `PEDIDA` con `MeasuredDot` y el aviso `MatchHintText`.

El rótulo de la sub-línea es **`ENTRE REPORTES`**. No `LATENCIA`: ese número es el hueco entre llegadas de reportes, y la latencia —el retardo desde que se mueve el stick— la app no la mide ni puede medirla, porque el reporte no trae marca de tiempo de origen.

- [ ] **Step 2: El distintivo.** En el code-behind, donde hoy se escriben `MeasuredText`/`MeasuredGapText` con el `sample` en la mano:

```csharp
// El distintivo dice como de REGULAR llega el flujo, no si la tasa es la pedida - eso lo
// dice el punto de PEDIDA, justo debajo. Son dos preguntas distintas: un mando puede ir a
// la tasa correcta con tirones, y uno a tasa equivocada puede ir finisimo.
var firmeza = RateStability.Classify(
    sample.Value.MedianGapMs, sample.Value.P95GapMs, sample.Value.Count);

(SteadinessChipText.Text, var puntoFirmeza) = firmeza switch
{
    RateSteadiness.Regular   => ("REGULAR",   StatusLevel.Ok),
    RateSteadiness.Irregular => ("IRREGULAR", StatusLevel.Warn),
    _                        => ("SIN DATOS", StatusLevel.Unknown),
};
SteadinessChipDot.Fill = StatusBrush(puntoFirmeza);
SteadinessChip.Visibility = Visibility.Visible;
```

**Comprueba los nombres reales** de `StatusLevel` y de la función que convierte nivel en pincel (`StatusBrush` es una suposición: mira cómo se pinta hoy `MeasuredDot` y usa esa misma vía). Si no hay un valor "desconocido" en el enum, usa `TextMutedBrush` directamente para el punto de `SIN DATOS`.

Y en **todos** los caminos donde hoy se pone `"--"`, `"sin datos"` o el texto de `Unavailable` —incluido el `Snapshot()` que devuelve `null` cuando el flujo lleva 2 s callado—, pon `SteadinessChip.Visibility = Visibility.Collapsed`. Dejar el distintivo con un valor viejo mientras el número de al lado dice "sin datos" es peor que no tenerlo.

- [ ] **Step 3: Tarjeta ESPECIFICACIONES TÉCNICAS.** La rejilla de cuatro filas que ya existe (`VELOCIDAD`, `bINTERVAL`, `FILTRO` con su punto, `INTERVALO`), metida en una tarjeta, más una fila nueva al final:

```xml
<TextBlock Text="INSTANCE ID" Style="{StaticResource FieldLabel}" Margin="0,14,0,4"/>
<!-- Es largo y no cabe: se recorta y el completo vive en el ToolTip. Sirve para pegarlo en
     un reporte de fallo, asi que tiene que poder leerse entero de algun modo. -->
<TextBlock Text="{Binding InstanceId}" Style="{StaticResource DataText}" FontSize="11"
           Foreground="{StaticResource TextLabelBrush}" TextTrimming="CharacterEllipsis"
           ToolTip="{Binding InstanceId}"/>
```

**Comprueba** que `FieldLabel` es el nombre real del estilo de rótulo que usan las cuatro filas; si no, usa el que usen ellas.

- [ ] **Step 4: Tarjeta CONFIGURACIÓN DE TASA.** `DetailRateCombo` a lo ancho, y debajo los dos botones en fila: `APLICAR CAMBIOS` con `Style="{StaticResource PrimaryButton}"` y `RESTABLECER VALORES` con el estilo secundario que ya tenga. **Los handlers no se renombran ni se reconectan.**

- [ ] **Step 5: Verificación** — build 0/0, suite completa PASS (`Passed: 416`). Manual con el DualSense conectado:
  - el número grande y `ENTRE REPORTES` cuadran entre sí (1000 Hz ↔ ~1,0 ms; 8000 Hz ↔ ~0,125 ms);
  - el distintivo dice `REGULAR` con el mando quieto, y **hay que comprobar que llega a decir `IRREGULAR`** — moviendo mucho los sticks, o con el equipo cargado. Si nunca cambia, el umbral está mal calibrado: ajusta `JitterCeiling` con la medida real, no a ojo, y actualiza los tests;
  - desconectar el mando oculta el distintivo en vez de dejarlo colgado;
  - el instance id se recorta y el ToolTip lo enseña entero;
  - `APLICAR CAMBIOS` y `RESTABLECER VALORES` siguen haciendo lo de siempre.

- [ ] **Step 6: Commit**

```bash
git add -u && git commit -m "feat(ui): el detalle del dispositivo, en tres tarjetas"
```

---

### Task 6: Documentación y verificación integral

**Files:**
- Modify: `README.md`, `docs/DOCUMENTACION.md`

- [ ] **Step 1: README**, en la sección de la página de dispositivos:

```markdown
La lista son tarjetas; la seleccionada se marca con borde blanco y un fondo algo
mas claro. Las que tienen una tasa puesta llevan una pastilla con la tasa
**resuelta**.

En el detalle, **MEDIDA** es lo que el dispositivo hace de verdad y **PEDIDA** lo
que se le escribio: el punto entre las dos es la razon de ser de esta pantalla.
La pastilla de al lado dice otra cosa distinta — como de **regular** llega el
flujo (`REGULAR` / `IRREGULAR` / `SIN DATOS`).

**ENTRE REPORTES** es el hueco mediano entre la llegada de un reporte y el
siguiente. **No es latencia.** La latencia seria el retardo desde que mueves el
stick hasta que el juego lo ve, y en esa cadena hay eslabones que esta app no
puede ver: el firmware del mando, la pila HID de Windows y el motor del juego.
El reporte del DualSense no trae marca de tiempo de origen, asi que el instante
del movimiento es desconocido y la resta es imposible. Subir la tasa acorta un
eslabon de esa cadena — la espera al sondeo, de ~0,5 ms a ~0,06 ms al pasar de
1000 a 8000 Hz —, no la cadena entera, que sigue midiendose en decenas de ms.
```

- [ ] **Step 2: DOCUMENTACION.md**, al mapa de módulos:

```markdown
- **`RateStability.cs`** — clasifica el flujo en regular / irregular / sin datos comparando
  el hueco del percentil 95 con la mediana. p95 y no el maximo -que RateSample ya trae-,
  porque un unico pico del planificador dispara el maximo aunque los otros mil huecos sean
  perfectos. Por debajo de 30 muestras no clasifica: decir "sin datos" es mejor que adivinar.
```

Y a las lecciones (ajusta la numeración a la última que haya):

```markdown
- **L10 — Lo que se afirma hay que poder medirlo.** La maqueta traia una pastilla verde que
  decia "STABLE"; el medidor solo exponia mediana, minimo y maximo, asi que no habia con que
  respaldarla. Se anadio la medida -el percentil 95- ANTES que la pastilla, y cuando no
  alcanza para decidir, la pastilla dice "SIN DATOS" en vez de verde.
- **L11 — El rotulo tiene que decir lo que el numero es.** La maqueta llamaba "LATENCIA" al
  hueco entre reportes. Son cosas distintas y la app no mide la segunda: no tiene el instante
  de origen. Rotularlo asi habria afirmado un retardo total falso por dos ordenes de
  magnitud. Se rotula "ENTRE REPORTES".
- **L12 — En monocromo, la jerarquia es contraste.** La maqueta usaba morado para la fila
  seleccionada y el boton de aplicar. Se resolvio con borde blanco + fondo mas claro, y con
  un boton invertido: mas fuerte que el color, y sin gastar un color de estado en decorar.
```

- [ ] **Step 3: Verificación integral** — build 0/0; `dotnet test` PASS (`Passed: 416`); recorrer la página con el mando conectado, con y sin tasa puesta, con un dispositivo sin filtro, con la lista vacía durante el escaneo, y desconectando el mando con el detalle abierto.

- [ ] **Step 4: Commit**

```bash
git add -u && git commit -m "docs: pagina de dispositivos en tarjetas, y por que ENTRE REPORTES no es latencia"
```

---

## Self-review

- **Cobertura de la maqueta:** encabezado con contador → T4 S1; tarjetas con icono, nombre, sub-línea y pastilla → T4; marca de selección → T3 S2 + T4 S3; tarjeta MEDIDA con número grande, pastilla y sub-línea → T5 S1-S2; ESPECIFICACIONES con las cuatro filas y el instance id → T5 S3; CONFIGURACIÓN DE TASA con combo y los dos botones → T5 S4. ✓
- **Las tres decisiones del usuario, aplicadas:** cero pinceles de color nuevos y el morado sustituido por contraste (T3, L12); la palabra "estable" no aparece en ningún sitio — el enum es `Regular`/`Irregular` y el texto también; `LATENCIA` sustituido por `ENTRE REPORTES` con la explicación en el README. ✓
- **Lo que la maqueta perdía y aquí no se pierde:** MEDIDA vs PEDIDA con su punto y su aviso ámbar. ✓
- **Tipos consistentes:** `P95GapMs` se añade en T1 y lo consumen T5 (mostrar) y T2 (vía `double`); `Count` ya existía y no se duplica; `RateSteadiness.Regular/Irregular/NoData`, `Classify`, `MinSamples`, `JitterCeiling` se definen en T2 y sólo los usa T5; `HasRate`/`ActiveChipText` se definen y consumen en T4; `DeviceCard`/`StatusChip`/`ChipText`/`PrimaryButton` se definen en T3 y los consumen T4 y T5. ✓
- **Cuentas de tests:** 402 hoy → 406 tras T1 (4 casos) → 416 tras T2 (10 casos) → 416 hasta el final. ✓
- **Riesgo cubierto — el `record struct` posicional:** T1 avisa de que añadir un campo rompe **todas** las construcciones de `RateSample` y obliga a un grep previo. Es el fallo más probable de este plan.
- **Riesgo cubierto — el salto de 1 px:** T3 S2 fija el grosor del borde en 1 constante y explica por qué; la selección va por color de borde y fondo.
- **Riesgo cubierto — el índice del percentil:** T1 tiene un test con una sola muestra, que es justo el caso que se sale del array si el índice no se acota.
- **Riesgo cubierto — un umbral que nunca se cruza:** T5 S5 obliga a comprobar que `IRREGULAR` llega a aparecer. Una pastilla que siempre dice que sí no es una medida, es un adorno.
- **Riesgo cubierto — nombres supuestos:** cada sitio donde no leí el código exacto (`SelectedRate`, `StatusLevel.Unknown`, `StatusBrush`, `FieldLabel`, `DataText`, los convertidores) lleva una instrucción explícita de comprobar el nombre real antes de escribirlo.
