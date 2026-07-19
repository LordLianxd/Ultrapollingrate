# Editor de curvas v2: solo Lineal+Editor, puntos de colores, biblioteca de curvas y documentación — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** El selector RESPUESTA queda con solo **Lineal** (default) y **Editor**; los 3 puntos del editor pasan a tener **colores fijos con significado** (verde/ámbar/rojo); el usuario puede **guardar sus curvas con nombre** en una biblioteca propia ("MIS CURVAS") y reaplicarlas a cualquier stick; y un botón **"¿CÓMO FUNCIONA?"** despliega la documentación en cristiano: qué hace la curva, por qué cada color, cómo se construye y cómo influye.

**Architecture:** Cero cambios en el núcleo de transformación (InputTransform/RemapEngine quedan como están — siguen soportando los presets viejos para no romper nada compilado/testeado). La poda es solo de UI + una **coerción al cargar** (`RemapSettings.Sanitize()`, pura y testeada): un perfil viejo con Precisa/Rápida/Dinámica/Digital/Personalizada se degrada honestamente a Lineal. La biblioteca es un store nuevo (`CurveLibraryStore`, espejo de `RemapProfileStore`) con su JSON propio. Colores y documentación son XAML/code-behind.

**Tech Stack:** .NET 9 WPF, xUnit, System.Text.Json. Sin libs nuevas.

## Global Constraints

- UI en **español**. Tema monocromo — **excepción explícita pedida por el usuario:** los 3 puntos del editor y sus viñetas en la documentación usan color (verde/ámbar/rojo).
- El proyecto de tests **linkea archivos fuente individualmente** (`HidusbfModernGui.Tests.csproj`): **`CurveLibraryStore.cs` (nuevo) debe añadirse ahí**. Como los tests compilan el fuente dentro del propio ensamblado de tests, los miembros `internal` SÍ son accesibles desde los tests.
- El enum `ResponseCurve` **no cambia** (los perfiles guardan nombres): los valores retirados siguen existiendo; solo desaparecen del combo y se degradan al cargar.
- Nada que toque Nefarius/HidSharp/WPF puede linkearse a tests.
- Commits **sin** Co-Authored-By. El push lo hace el usuario. Identidad git ya configurada.
- Estado actual relevante: `CurvePresets` (7 entradas) en `MainWindow.xaml.cs` ~L772; puntos del editor = lista de 5 (`RemapSettings.LeftCurvePoints`, extremos fijos, índices 1..3 arrastrables, `EnsureCurveDots`/`RefreshCurveDots`/`CurveCanvas_*` en MainWindow); el JSON del usuario ya contiene curvas retiradas (p. ej. `"RightCurve": "Digital"`), así que la coerción se ejercita de verdad.

## Estructura de archivos

- Modify: `HidusbfModernGui/RemapSettings.cs` (`Sanitize()` + `SanitizePoints` internal)
- Create: `HidusbfModernGui/CurveLibraryStore.cs` (`SavedCurve` + store)
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (poda del combo, coerción al cargar, colores de puntos, MIS CURVAS, ayuda)
- Modify: `HidusbfModernGui/MainWindow.xaml` (quitar panel CURVATURA, filas MIS CURVAS, botón/panel de ayuda)
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj` (link del store nuevo)
- Test: `HidusbfModernGui.Tests/RemapSettingsTests.cs`, Create: `HidusbfModernGui.Tests/CurveLibraryStoreTests.cs`
- Modify: `README.md`

---

### Task 1: Poda a Lineal+Editor con coerción de perfiles viejos (TDD)

**Files:**
- Modify: `HidusbfModernGui/RemapSettings.cs`
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (`CurvePresets` ~L772, dos sitios de carga, restos de CURVATURA)
- Modify: `HidusbfModernGui/MainWindow.xaml` (paneles CURVATURA)
- Test: `HidusbfModernGui.Tests/RemapSettingsTests.cs`

**Interfaces:**
- Produces: `RemapSettings.Sanitize()` (público, muta la instancia) y `RemapSettings.SanitizePoints(List<CurvePoint>?)` (`internal static`, devuelve lista válida de 5). Consumidos por MainWindow (2 sitios de carga) y por `CurveLibraryStore.Load` (Task 2).

- [ ] **Step 1: Tests que fallan** — añadir a `RemapSettingsTests.cs`:

```csharp
[Fact]
public void Sanitize_DegradesRetiredCurvesToLineal()
{
    var s = new RemapSettings { LeftCurve = ResponseCurve.Precisa, RightCurve = ResponseCurve.Digital };
    s.Sanitize();
    Assert.Equal(ResponseCurve.Normal, s.LeftCurve);
    Assert.Equal(ResponseCurve.Normal, s.RightCurve);
}

[Fact]
public void Sanitize_KeepsLinealAndEditor()
{
    var s = new RemapSettings { LeftCurve = ResponseCurve.Normal, RightCurve = ResponseCurve.Propia };
    s.Sanitize();
    Assert.Equal(ResponseCurve.Normal, s.LeftCurve);
    Assert.Equal(ResponseCurve.Propia, s.RightCurve);
}

[Fact]
public void SanitizePoints_WrongCountOrNull_ResetsToDefault()
{
    Assert.Equal(RemapSettings.DefaultCurvePoints(), RemapSettings.SanitizePoints(null));
    Assert.Equal(RemapSettings.DefaultCurvePoints(),
        RemapSettings.SanitizePoints(new List<CurvePoint> { new(0, 0), new(1, 1) }));
}

[Fact]
public void SanitizePoints_AnchorsEndpointsAndOrdersX()
{
    var messy = new List<CurvePoint> { new(0.9, 0.8), new(0.5, 2.0), new(0.1, -1.0), new(0.7, 0.6), new(0.2, 0.3) };
    var r = RemapSettings.SanitizePoints(messy);
    Assert.Equal(5, r.Count);
    Assert.Equal(new CurvePoint(0, 0), r[0]);
    Assert.Equal(new CurvePoint(1, 1), r[4]);
    for (int i = 1; i < 5; i++) Assert.True(r[i].X > r[i - 1].X);          // X estrictamente creciente
    for (int i = 1; i <= 3; i++) Assert.InRange(r[i].Y, 0.0, 1.0);         // Y acotada
}
```

- [ ] **Step 2: Verificar que fallan** — `dotnet test HidusbfModernGui.Tests\HidusbfModernGui.Tests.csproj --filter "FullyQualifiedName~RemapSettingsTests"`. Esperado: error de compilación (Sanitize no existe).

- [ ] **Step 3: Implementación** — en `RemapSettings.cs`, añadir `using System.Linq;` y:

```csharp
// Desde la v2 el combo de RESPUESTA solo ofrece Lineal y Editor. Los perfiles guardados
// con los presets retirados (Precisa/Rapida/Dinamica/Digital/Personalizada) se degradan a
// Lineal AL CARGAR - visible y honesto, en vez de dejar un combo sin seleccion o un motor
// aplicando una curva que la UI ya no puede mostrar. El enum conserva los valores viejos
// (los perfiles serializan nombres) y el motor sigue sabiendo aplicarlos; esta coercion es
// la unica frontera.
public void Sanitize()
{
    if (LeftCurve != ResponseCurve.Normal && LeftCurve != ResponseCurve.Propia)
        LeftCurve = ResponseCurve.Normal;
    if (RightCurve != ResponseCurve.Normal && RightCurve != ResponseCurve.Propia)
        RightCurve = ResponseCurve.Normal;
    LeftCurvePoints = SanitizePoints(LeftCurvePoints);
    RightCurvePoints = SanitizePoints(RightCurvePoints);
}

// Devuelve SIEMPRE una lista valida de 5 puntos para el editor: extremos anclados en
// (0,0)/(1,1), X estrictamente creciente (separacion minima 0.03, la misma que impone el
// arrastre en la UI), Y en 0..1. Cualquier cosa irreparable (null, otro tamano) vuelve a
// la diagonal por defecto. Internal para que CurveLibraryStore tambien la use al cargar
// curvas de un JSON editado a mano.
internal static List<CurvePoint> SanitizePoints(List<CurvePoint>? pts)
{
    if (pts == null || pts.Count != 5) return DefaultCurvePoints();
    var s = pts.OrderBy(p => p.X).ToList();
    s[0] = new CurvePoint(0.0, 0.0);
    s[4] = new CurvePoint(1.0, 1.0);
    for (int i = 1; i <= 3; i++)
        s[i] = new CurvePoint(Math.Max(s[i].X, s[i - 1].X + 0.03), Math.Clamp(s[i].Y, 0.0, 1.0));
    for (int i = 3; i >= 1; i--)
        s[i] = new CurvePoint(Math.Min(s[i].X, s[i + 1].X - 0.03), s[i].Y);
    return s;
}
```

- [ ] **Step 4: Verificar que pasan** — mismo filtro, PASS.

- [ ] **Step 5: Poda de la UI** — en `MainWindow.xaml.cs`:

`CurvePresets` (~L772) queda en dos entradas (el mini-ícono de Editor ya tiene su caso especial `IconPropiaPoints`):

```csharp
private static readonly (string Label, ResponseCurve Curve)[] CurvePresets =
{
    ("Lineal", ResponseCurve.Normal),
    ("Editor", ResponseCurve.Propia),
};
```

Coerción en los DOS sitios que asignan `_remap` desde disco — justo después de cada `_remap = CloneRemapSettings(...)` (en `BuildRemapControls`, ~L698, y en `LoadRemapProfile_Click`, ~L1178), añadir:

```csharp
_remap.Sanitize();   // perfiles viejos con presets retirados -> Lineal (ver RemapSettings)
```

Quitar los restos de **Personalizada** (ya inalcanzable): en `MainWindow.xaml` eliminar los paneles `LeftCurvaturaPanel`/`RightCurvaturaPanel` completos (con sus sliders `LeftCurvaturaSlider`/`RightCurvaturaSlider` y textos); en `MainWindow.xaml.cs` eliminar todo lo que los referencie: las líneas de `ApplyRemapSettingsToControls` que los tocan (asignación de sliders y visibilidad), los toggles de visibilidad dentro de los handlers de cambio de curva, los handlers `ValueChanged` de esos sliders y `UpdateCurvaturaText` con sus llamadas. **Localizar por grep de los x:Name — el compilador es la red: tras borrar, `dotnet build` debe dar 0 errores 0 warnings.** `RemapSettings.LeftCurvaturePct`/`RightCurvaturePct` se QUEDAN (compat JSON; el motor aún los lee para perfiles degradados a mitad de camino).

- [ ] **Step 6: Verificación** — `dotnet build HidusbfModernGui\HidusbfModernGui.csproj -c Debug` (0/0) y suite completa `dotnet test` (las 322 = 318 + 4 nuevas). Nota: los tests del núcleo de Precisa/Rápida/Dinámica/Digital/Personalizada en `InputTransformCurveTests`/`InputTransformStickTests` SIGUEN pasando — el núcleo no se toca.
- [ ] **Step 7: Commit** — `git add -u && git commit -m "feat: RESPUESTA queda en Lineal+Editor; perfiles viejos degradan a Lineal (Sanitize, TDD)"`

---

### Task 2: `CurveLibraryStore` — biblioteca de curvas con nombre (TDD)

**Files:**
- Create: `HidusbfModernGui/CurveLibraryStore.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj` (¡link del archivo nuevo!)
- Create: `HidusbfModernGui.Tests/CurveLibraryStoreTests.cs`

**Interfaces:**
- Consumes: `CurvePoint`, `RemapSettings.DefaultCurvePoints()`, `RemapSettings.SanitizePoints` (Task 1), `OpResult`.
- Produces: `SavedCurve { string Name, List<CurvePoint> Points }`; `CurveLibraryStore.Load() -> List<SavedCurve>`, `Save(IEnumerable<SavedCurve>) -> OpResult`, `Path`, `OverrideDirectoryForTests(string?)`. Consumido por la UI de Task 4.

- [ ] **Step 1: Añadir el link al csproj** (antes de escribir tests, o no compilan):

```xml
<Compile Include="..\HidusbfModernGui\CurveLibraryStore.cs" Link="CurveLibraryStore.cs" />
```

- [ ] **Step 2: Tests que fallan** — crear `CurveLibraryStoreTests.cs` (mismo patrón de aislamiento que `RemapProfileStoreTests`: directorio temporal + `OverrideDirectoryForTests`, restaurado en un `finally`/`Dispose` igual que haga ese archivo — copiar su patrón exacto):

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using HidusbfModernGui;
using Xunit;

public class CurveLibraryStoreTests : IDisposable
{
    private readonly string _dir;

    public CurveLibraryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "UltraPollingTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        CurveLibraryStore.OverrideDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        CurveLibraryStore.OverrideDirectoryForTests(null);
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Load_WithoutFile_ReturnsEmpty()
    {
        Assert.Empty(CurveLibraryStore.Load());
    }

    [Fact]
    public void SaveAndLoad_RoundTripsNamedCurves()
    {
        var curva = new SavedCurve
        {
            Name = "Mi curva de franco",
            Points = new() { new(0, 0), new(0.2, 0.1), new(0.5, 0.3), new(0.8, 0.7), new(1, 1) },
        };
        Assert.True(CurveLibraryStore.Save(new[] { curva }).Success);

        var back = CurveLibraryStore.Load();
        Assert.Single(back);
        Assert.Equal("Mi curva de franco", back[0].Name);
        Assert.Equal(0.3, back[0].Points[2].Y, 3);
    }

    [Fact]
    public void Load_SanitizesHandEditedPoints()
    {
        // Un JSON editado a mano con 3 puntos no puede romper la UI (que asume 5).
        var rota = new SavedCurve { Name = "rota", Points = new() { new(0, 0), new(0.5, 0.9), new(1, 1) } };
        Assert.True(CurveLibraryStore.Save(new[] { rota }).Success);

        var back = CurveLibraryStore.Load();
        Assert.Equal(5, back[0].Points.Count);   // reseteada a la valida por defecto
    }
}
```

- [ ] **Step 3: Verificar que fallan** — compile error (`SavedCurve`/`CurveLibraryStore` no existen).

- [ ] **Step 4: Implementación** — crear `HidusbfModernGui/CurveLibraryStore.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace HidusbfModernGui
{
    // Una curva del Editor guardada con nombre por el usuario: los 5 puntos tal como los
    // dibujo. Independiente de los perfiles del remapeo: una curva se puede aplicar a
    // cualquier stick en cualquier momento desde "MIS CURVAS".
    public sealed class SavedCurve
    {
        public string Name { get; set; } = "";
        public List<CurvePoint> Points { get; set; } = RemapSettings.DefaultCurvePoints();
    }

    // Espejo de RemapProfileStore: mismo %APPDATA%\UltraPolling, misma escritura atomica
    // con copia .backup, mismos Options. Archivo propio (curves.json) para que borrar un
    // perfil nunca arrastre una curva y viceversa.
    public static class CurveLibraryStore
    {
        private static string? _overrideDir;

        internal static void OverrideDirectoryForTests(string? dir) => _overrideDir = dir;

        private static string Directory_ => _overrideDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraPolling");

        public static string Path => System.IO.Path.Combine(Directory_, "curves.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        public static List<SavedCurve> Load()
        {
            try
            {
                if (!File.Exists(Path)) return new List<SavedCurve>();

                string json = File.ReadAllText(Path);
                if (string.IsNullOrWhiteSpace(json)) return new List<SavedCurve>();

                var list = JsonSerializer.Deserialize<List<SavedCurve>>(json, Options) ?? new List<SavedCurve>();
                // Un JSON editado a mano no puede romper la UI del editor (asume 5 puntos
                // ordenados con extremos fijos): se sanea con la misma regla que el resto.
                foreach (var c in list)
                    c.Points = RemapSettings.SanitizePoints(c.Points);
                return list;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CurveLibraryStore.Load failed, starting empty: {ex.Message}");
                return new List<SavedCurve>();
            }
        }

        public static OpResult Save(IEnumerable<SavedCurve> curves)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory_);
                if (File.Exists(Path)) File.Copy(Path, Path + ".backup", true);
                File.WriteAllText(Path, JsonSerializer.Serialize(curves, Options));
                return OpResult.Ok();
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"No se pudieron guardar las curvas: {ex.Message}");
            }
        }
    }
}
```

- [ ] **Step 5: Verificar que pasan** — filtro `CurveLibraryStoreTests` PASS y suite completa PASS.
- [ ] **Step 6: Commit** — `git add HidusbfModernGui/CurveLibraryStore.cs HidusbfModernGui.Tests/CurveLibraryStoreTests.cs HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj && git commit -m "feat: biblioteca de curvas con nombre (CurveLibraryStore, TDD)"`

---

### Task 3: Puntos de colores con significado

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (`EnsureCurveDots`)

**Interfaces:**
- Produces: `CurveDotColors` (3 colores fijos, índice = punto), usados también por la documentación (Task 5) — el orden ES el contrato: 0=verde (zona baja), 1=ámbar (zona media), 2=rojo (zona alta).

- [ ] **Step 1: Implementación** — junto a los campos del editor de curva:

```csharp
// Colores fijos de los 3 puntos del editor - la UNICA excepcion de color del tema
// monocromo, pedida explicitamente: cada punto tiene identidad propia y la ayuda
// ("¿COMO FUNCIONA?") los nombra por color. El indice es el contrato:
//   0 = VERDE  zona baja  (movimientos finos, punteria)
//   1 = AMBAR  zona media (transicion apuntar<->girar)
//   2 = ROJO   zona alta  (giros rapidos, tope)
private static readonly Color[] CurveDotColors =
{
    Color.FromRgb(0x66, 0xBB, 0x6A),
    Color.FromRgb(0xFF, 0xCA, 0x28),
    Color.FromRgb(0xEF, 0x53, 0x50),
};
```

En `EnsureCurveDots`, el `Ellipse` pasa de `Fill = (Brush)FindResource("TextDataBrush")` a:

```csharp
Fill = new SolidColorBrush(CurveDotColors[i]),
Stroke = Brushes.White,
StrokeThickness = 1,
```

(El borde blanco de 1px mantiene el contraste sobre la línea de la curva en el fondo oscuro.)

- [ ] **Step 2: Verificación** — build 0/0; abrir la app: STICKS → Editor → los 3 puntos se ven verde/ámbar/rojo (en ambos sticks, mismo orden izquierda→derecha).
- [ ] **Step 3: Commit** — `git add -u && git commit -m "feat: puntos del editor con colores fijos (verde/ambar/rojo = zona baja/media/alta)"`

---

### Task 4: "MIS CURVAS" — guardar/cargar/borrar curvas con nombre

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml` (una fila por stick, bajo el bloque de CURVA de cada uno)
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `CurveLibraryStore`/`SavedCurve` (Task 2), `_remap.LeftCurvePoints`/`RightCurvePoints`, `SelectComboByTag`, `RedrawLeftCurve`/`RedrawRightCurve`, `RememberRemap()`.
- Produces: por stick: ComboBox `LeftSavedCurveList`/`RightSavedCurveList`, TextBox `LeftCurveName`/`RightCurveName`, botones CARGAR/GUARDAR/BORRAR.

- [ ] **Step 1: XAML** — dentro de la tarjeta de cada stick, inmediatamente después del bloque `CURVA (entrada -> salida)` (canvas) y antes del botón AVANZADO, insertar (versión LEFT; la RIGHT es idéntica con prefijo `Right` y handlers `Right*`):

```xml
<TextBlock Text="MIS CURVAS" Style="{StaticResource FieldLabel}" Margin="0,12,0,6"/>
<StackPanel Orientation="Horizontal">
    <ComboBox x:Name="LeftSavedCurveList" Width="140" DisplayMemberPath="Name" Margin="0,0,8,0"/>
    <Button Content="CARGAR" Style="{StaticResource InstrumentButton}" Click="LoadLeftCurve_Click" Margin="0,0,8,0"/>
    <Button Content="BORRAR" Style="{StaticResource InstrumentButton}" Click="DeleteLeftCurve_Click"/>
</StackPanel>
<StackPanel Orientation="Horizontal" Margin="0,8,0,0">
    <TextBox x:Name="LeftCurveName" Width="140" Background="{StaticResource SurfaceAltBrush}"
             Foreground="{StaticResource TextDataBrush}" BorderBrush="{StaticResource BorderBrush}"
             BorderThickness="1" Padding="6,4" FontFamily="{StaticResource UiFont}" Margin="0,0,8,0"/>
    <Button Content="GUARDAR CURVA" Style="{StaticResource InstrumentButton}" Click="SaveLeftCurve_Click"/>
</StackPanel>
```

- [ ] **Step 2: code-behind** — motor genérico + wrappers (el mismo patrón que el arrastre de puntos):

```csharp
// ===== MIS CURVAS: biblioteca de curvas del Editor (CurveLibraryStore) =====
// Una sola lista compartida entre ambos sticks: guardas la curva que dibujaste en un
// stick y la aplicas al que quieras. CARGAR ademas cambia la RESPUESTA de ese stick a
// Editor: cargar una curva y no verla actuar seria un boton mentiroso.
private List<SavedCurve> _savedCurves = new();

private void RefreshSavedCurveLists()
{
    var items = _savedCurves.ToList();
    LeftSavedCurveList.ItemsSource = items;
    RightSavedCurveList.ItemsSource = items.ToList();   // copia: dos combos, dos listas
    if (LeftSavedCurveList.SelectedItem == null && items.Count > 0) LeftSavedCurveList.SelectedIndex = 0;
    if (RightSavedCurveList.SelectedItem == null && items.Count > 0) RightSavedCurveList.SelectedIndex = 0;
}

private void SaveCurve(bool left)
{
    var box = left ? LeftCurveName : RightCurveName;
    var pts = left ? _remap.LeftCurvePoints : _remap.RightCurvePoints;

    string name = (box.Text ?? "").Trim();
    if (name.Length == 0) name = $"Curva {_savedCurves.Count + 1}";

    _savedCurves.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    _savedCurves.Add(new SavedCurve { Name = name, Points = new List<CurvePoint>(pts) });

    var r = CurveLibraryStore.Save(_savedCurves);
    LogStatus(r.Success ? $"Curva '{name}' guardada." : r.Error!);
    RefreshSavedCurveLists();
}

private void LoadCurve(bool left)
{
    var combo = left ? LeftSavedCurveList : RightSavedCurveList;
    if (combo.SelectedItem is not SavedCurve sel) return;

    if (left)
    {
        _remap.LeftCurvePoints = new List<CurvePoint>(sel.Points);
        if (_remap.LeftCurve != ResponseCurve.Propia)
        {
            _remap.LeftCurve = ResponseCurve.Propia;
            try { _updatingRemap = true; SelectComboByTag(LeftCurveList, ResponseCurve.Propia); }
            finally { _updatingRemap = false; }
        }
        RedrawLeftCurve();
    }
    else
    {
        _remap.RightCurvePoints = new List<CurvePoint>(sel.Points);
        if (_remap.RightCurve != ResponseCurve.Propia)
        {
            _remap.RightCurve = ResponseCurve.Propia;
            try { _updatingRemap = true; SelectComboByTag(RightCurveList, ResponseCurve.Propia); }
            finally { _updatingRemap = false; }
        }
        RedrawRightCurve();
    }
    RememberRemap();
    LogStatus($"Curva '{sel.Name}' aplicada al stick {(left ? "izquierdo" : "derecho")}.");
}

private void DeleteCurve(bool left)
{
    var combo = left ? LeftSavedCurveList : RightSavedCurveList;
    if (combo.SelectedItem is not SavedCurve sel) return;

    _savedCurves.RemoveAll(c => string.Equals(c.Name, sel.Name, StringComparison.OrdinalIgnoreCase));
    var r = CurveLibraryStore.Save(_savedCurves);
    LogStatus(r.Success ? $"Curva '{sel.Name}' borrada." : r.Error!);
    RefreshSavedCurveLists();
}

private void SaveLeftCurve_Click(object sender, RoutedEventArgs e) => SaveCurve(true);
private void LoadLeftCurve_Click(object sender, RoutedEventArgs e) => LoadCurve(true);
private void DeleteLeftCurve_Click(object sender, RoutedEventArgs e) => DeleteCurve(true);
private void SaveRightCurve_Click(object sender, RoutedEventArgs e) => SaveCurve(false);
private void LoadRightCurve_Click(object sender, RoutedEventArgs e) => LoadCurve(false);
private void DeleteRightCurve_Click(object sender, RoutedEventArgs e) => DeleteCurve(false);
```

En `BuildRemapControls`, tras `RefreshRemapProfileList();`, añadir:

```csharp
_savedCurves = CurveLibraryStore.Load();
RefreshSavedCurveLists();
```

(Si `LogStatus` no existe con esa firma, usar el mecanismo de estado que ya use esa zona del archivo — verificar por grep; el resto no cambia.)

- [ ] **Step 3: Verificación** — build 0/0, suite completa PASS. Manual: dibujar curva en el izquierdo → NOMBRE "prueba" → GUARDAR CURVA → aparece en ambos combos → en el derecho CARGAR → el derecho salta a Editor con la misma curva → BORRAR la quita de ambos → reabrir la app y la biblioteca persiste (menos la borrada).
- [ ] **Step 4: Commit** — `git add -u && git commit -m "feat: MIS CURVAS - guardar/cargar/borrar curvas con nombre por stick"`

---

### Task 5: Botón "¿CÓMO FUNCIONA?" + documentación del editor

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml` (botón + panel al inicio de `TabSticks`)
- Modify: `HidusbfModernGui/MainWindow.xaml.cs` (un toggle)

- [ ] **Step 1: XAML** — como PRIMER hijo del `StackPanel` dentro de `TabSticks` (antes de la tarjeta STICK IZQUIERDO):

```xml
<Button x:Name="CurveHelpBtn" Content="¿COMO FUNCIONA EL EDITOR DE CURVAS?"
        Style="{StaticResource InstrumentButton}" HorizontalAlignment="Left"
        Click="ToggleCurveHelp" Margin="0,0,0,12"/>
<Border x:Name="CurveHelpPanel" Visibility="Collapsed" Background="{StaticResource SurfaceBrush}"
        BorderBrush="{StaticResource BorderBrush}" BorderThickness="1" Padding="18" Margin="0,0,0,16">
    <StackPanel>
        <TextBlock Style="{StaticResource FieldLabel}" TextWrapping="Wrap"
                   Text="La grafica traduce lo que TU mueves (eje horizontal: cuanto empujas el stick) a lo que el juego recibe (eje vertical: cuanto se mueve en pantalla). La linea recta 'Lineal' significa tal cual: empujas 30%, sale 30%. Con 'Editor' dibujas tu propia traduccion arrastrando los 3 puntos de colores."/>

        <StackPanel Orientation="Horizontal" Margin="0,12,0,0">
            <Ellipse Width="10" Height="10" Fill="#66BB6A" VerticalAlignment="Center" Margin="0,0,8,0"/>
            <TextBlock Style="{StaticResource FieldLabel}" TextWrapping="Wrap" Width="620"
                       Text="PUNTO VERDE - zona baja: los movimientos pequenos del stick (apuntar fino, micro-correcciones). Bajalo para mas precision al apuntar; subelo si quieres que el mando reaccione antes."/>
        </StackPanel>
        <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
            <Ellipse Width="10" Height="10" Fill="#FFCA28" VerticalAlignment="Center" Margin="0,0,8,0"/>
            <TextBlock Style="{StaticResource FieldLabel}" TextWrapping="Wrap" Width="620"
                       Text="PUNTO AMBAR - zona media: el paso entre apuntar y girar. Ajustalo para que la transicion no se sienta como un salto."/>
        </StackPanel>
        <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
            <Ellipse Width="10" Height="10" Fill="#EF5350" VerticalAlignment="Center" Margin="0,0,8,0"/>
            <TextBlock Style="{StaticResource FieldLabel}" TextWrapping="Wrap" Width="620"
                       Text="PUNTO ROJO - zona alta: cerca del tope del stick (giros rapidos, 180 grados). Subelo para llegar antes a la velocidad maxima; bajalo si te pasas de largo en los giros."/>
        </StackPanel>

        <TextBlock Style="{StaticResource FieldLabel}" TextWrapping="Wrap" Margin="0,12,0,0"
                   Text="Como se construye: la curva pasa EXACTAMENTE por tus puntos, unida con una interpolacion suave que nunca rebota ni se pasa de lo que dibujaste entre punto y punto (interpolacion monotona). Los extremos estan fijos: quieto es quieto y a fondo es a fondo. La ZONA MUERTA y el ALCANCE se aplican antes de la curva, con sus propias barras."/>
        <TextBlock Style="{StaticResource FieldLabel}" TextWrapping="Wrap" Margin="0,8,0,0"
                   Text="Como influye: con el MANDO VIRTUAL activo, cada arrastre se aplica al instante - puedes afinar con el juego abierto. Guarda tu dibujo con GUARDAR CURVA y aplicalo a cualquier stick desde MIS CURVAS; los perfiles del remapeo tambien lo incluyen."/>
    </StackPanel>
</Border>
```

- [ ] **Step 2: code-behind** — junto a `ToggleLeftAdvanced`:

```csharp
private void ToggleCurveHelp(object sender, RoutedEventArgs e)
{
    CurveHelpPanel.Visibility = CurveHelpPanel.Visibility == Visibility.Visible
        ? Visibility.Collapsed : Visibility.Visible;
}
```

- [ ] **Step 3: Verificación** — build 0/0; manual: el botón abre/cierra el panel, las 3 viñetas muestran su color junto al texto, nada se desborda (el `Width="620"` de los textos de viñeta evita que el StackPanel horizontal se coma el wrap — si la columna real es más angosta, ajustar ese ancho al de los otros textos de la pestaña).
- [ ] **Step 4: Commit** — `git add -u && git commit -m "feat: boton ¿COMO FUNCIONA? con la documentacion del editor de curvas"`

---

### Task 6: README + verificación integral

**Files:**
- Modify: `README.md` (línea del configurador)

- [ ] **Step 1** — en la viñeta **Configurar el mando** del README, reemplazar el paréntesis "(preajustes o una curva propia dibujada punto a punto)" por "(lineal o tu propia curva dibujada punto a punto, con puntos de colores explicados en la app y una biblioteca de curvas guardadas)".
- [ ] **Step 2** — `dotnet test` completo (todo verde) y `.\package.ps1` (termina en "Package ready", sin warnings nuevos).
- [ ] **Step 3** — Prueba integral (usuario, con hardware): combo con solo Lineal/Editor; su perfil viejo (que tenía Digital en el stick derecho) carga como Lineal sin drama; puntos de colores; guardar/cargar/borrar curvas; panel de ayuda.
- [ ] **Step 4: Commit** — `git add -u && git commit -m "docs: README con el editor de curvas v2 (lineal+editor, colores, biblioteca)"`

---

## Self-review

- **Cobertura del pedido:** quitar todo excepto Lineal (default) y Editor (Task 1, con coerción honesta de perfiles viejos — el propio JSON del usuario tiene `"RightCurve": "Digital"`); guardar curvas creadas (Tasks 2+4, biblioteca con nombre, elegida por el usuario); cada punto de diferente color (Task 3, con significado); botón de documentación explicando qué hace, el porqué de cada color, cómo se construye y cómo influye (Task 5, copy completo). ✓
- **Placeholders:** ninguno. Los dos puntos deliberadamente delegados al implementador llevan instrucción de localización exacta (restos de CURVATURA por grep de x:Name con el build como red; firma de `LogStatus` por grep). ✓
- **Tipos consistentes:** `SavedCurve.Points : List<CurvePoint>` (Task 2) ↔ `_remap.*CurvePoints` (existente); `RemapSettings.SanitizePoints` `internal static` (Task 1) usada por `CurveLibraryStore.Load` (Task 2) — accesible porque los tests y el store compilan el fuente linkeado en el mismo ensamblado; `CurveDotColors[i]` (Task 3) ↔ los hex de las viñetas de ayuda (Task 5: #66BB6A/#FFCA28/#EF5350). ✓
- **Restricción de tests:** `CurveLibraryStore.cs` linkeado en Step 1 de Task 2 (antes de los tests); nada de WPF/Nefarius en tests. ✓
- **Compat:** enum intacto; `LeftCurvaturePct` se conserva en JSON; perfiles y curvas viejas sanean al cargar, nunca al guardar a espaldas del usuario (la degradación ocurre en memoria y solo persiste cuando él guarda algo). ✓
