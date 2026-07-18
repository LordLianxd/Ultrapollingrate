# Curvas de stick nuevas (Dinámica + Digital) y selector con íconos — Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps con checkbox.

**Goal:** Añadir dos curvas de respuesta de stick nuevas — **Dinámica** (curva S: precisa en el centro, rápida en medio, tope controlado) y **Digital** (escalón: on/off, stick como d-pad) — y presentar la RESPUESTA como una lista de preajustes con mini-ícono de curva (Lineal, Precisa, Rápida, Dinámica, Digital, Personalizada), manteniendo el slider de Curvatura para Personalizada.

**Architecture:** El núcleo puro gana una función de forma `Shape(t, curve, curvaturePct)` que cubre las 6 curvas, y un `ApplyStick` que la usa. La UI reemplaza los 4 botones de RESPUESTA por un ComboBox cuyos ítems se construyen en código con un mini-`Polyline` que dibuja cada curva. Sin datos crudos para el usuario. Sin macros/KB-M/evasión.

**Tech Stack:** .NET 9, WPF, C#, xUnit.

## Global Constraints
- .NET 9, x64, WPF. Tema monocromo (Theme.xaml); lógica pura sin WPF.
- La matemática de curvas es libre; NO clonar nombres/íconos de DSX — presentación propia.
- **Constante** queda FUERA de este plan (definición no aclarada por el usuario).
- Congelados salvo los que este plan toca (InputTransform.cs, ControllerState.cs, RemapSettings.cs son del núcleo del remapeador y sí se extienden). No tocar: DualSenseLight.cs, LightIntent.cs, PlayerLedWalker.cs, RainbowWalker.cs, ColourMath.cs, ColourRamp.cs, SystemManager.cs, PollingCore.cs, LightProfile.cs, Theme.xaml.
- Commits SIN `Co-Authored-By`. git: `git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com"`. Push lo hace el usuario.
- Carpeta: `C:\Users\Administrator\Downloads\work ultrapolling\UltraPolling`. Build de prueba: `bin\Debug\net9.0-windows\UltraPolling.exe`.

---

### Task 1: Núcleo — función de forma con las 6 curvas (TDD)

**Files:**
- Modify: `HidusbfModernGui/ControllerState.cs` (enum), `HidusbfModernGui/InputTransform.cs`
- Test: `HidusbfModernGui.Tests/InputTransformCurveTests.cs` (extender)

**Interfaces:**
- `enum ResponseCurve` gana `Dinamica` y `Digital` (quedan: Precisa, Normal, Rapida, Personalizada, Dinamica, Digital).
- `public static double InputTransform.Shape(double t, ResponseCurve curve, int curvaturePct)` — t en 0..1 → salida 0..1.
- `public static (double X, double Y) InputTransform.ApplyStick(StickInput s, double innerDeadzone, double outerDeadzone, ResponseCurve curve, int curvaturePct)` — deadzone radial + rescale + `Shape`.

- [ ] **Step 1: Tests que fallan** — añadir a `InputTransformCurveTests.cs`:

```csharp
    [Fact]
    public void Shape_Normal_IsLinear()
    {
        Assert.Equal(0.25, InputTransform.Shape(0.25, ResponseCurve.Normal, 50), 3);
        Assert.Equal(0.80, InputTransform.Shape(0.80, ResponseCurve.Normal, 50), 3);
    }

    [Fact]
    public void Shape_Dinamica_IsAnSCurve()
    {
        // S: pasa por 0.5 en el centro, suave abajo, empinada pasando el medio, simetrica.
        Assert.Equal(0.5, InputTransform.Shape(0.5, ResponseCurve.Dinamica, 50), 3);
        Assert.True(InputTransform.Shape(0.25, ResponseCurve.Dinamica, 50) < 0.25);  // suave cerca del centro
        Assert.True(InputTransform.Shape(0.75, ResponseCurve.Dinamica, 50) > 0.75);  // empinada hacia el borde
        double a = InputTransform.Shape(0.30, ResponseCurve.Dinamica, 50);
        double b = InputTransform.Shape(0.70, ResponseCurve.Dinamica, 50);
        Assert.Equal(1.0, a + b, 3);   // simetrica: f(t) + f(1-t) = 1
        Assert.Equal(0.0, InputTransform.Shape(0.0, ResponseCurve.Dinamica, 50), 3);
        Assert.Equal(1.0, InputTransform.Shape(1.0, ResponseCurve.Dinamica, 50), 3);
    }

    [Fact]
    public void Shape_Digital_Steps()
    {
        Assert.Equal(0.0, InputTransform.Shape(0.49, ResponseCurve.Digital, 50), 3);
        Assert.Equal(1.0, InputTransform.Shape(0.50, ResponseCurve.Digital, 50), 3);
        Assert.Equal(1.0, InputTransform.Shape(0.90, ResponseCurve.Digital, 50), 3);
        Assert.Equal(0.0, InputTransform.Shape(0.0, ResponseCurve.Digital, 50), 3);
    }

    [Fact]
    public void Shape_PowerPresets_MatchExponents()
    {
        Assert.Equal(Math.Pow(0.5, 1.8), InputTransform.Shape(0.5, ResponseCurve.Precisa, 50), 3);
        Assert.Equal(Math.Pow(0.5, 0.6), InputTransform.Shape(0.5, ResponseCurve.Rapida, 50), 3);
        // Personalizada usa la curvatura: 50 -> exponente 1.0 -> lineal.
        Assert.Equal(0.5, InputTransform.Shape(0.5, ResponseCurve.Personalizada, 50), 3);
    }

    [Fact]
    public void ApplyStick_WithCurve_AppliesDeadzoneThenShape()
    {
        // Sin deadzone, Digital: magnitud 0.6 -> full en la direccion del stick.
        var (x, _) = InputTransform.ApplyStick(new StickInput(0.6, 0.0), 0.0, 1.0, ResponseCurve.Digital, 50);
        Assert.Equal(1.0, x, 2);
        var (x2, _) = InputTransform.ApplyStick(new StickInput(0.3, 0.0), 0.0, 1.0, ResponseCurve.Digital, 50);
        Assert.Equal(0.0, x2, 2);   // 0.3 < 0.5 -> 0
    }
```

- [ ] **Step 2: Correr y ver fallar** — `dotnet test HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj -v q`.

- [ ] **Step 3: Implementar** en `InputTransform.cs`:

```csharp
        // Funcion de forma: recibe la magnitud normalizada t (0..1, ya sin deadzone) y devuelve
        // la salida 0..1 segun la curva. Un solo lugar para las 6 curvas.
        public static double Shape(double t, ResponseCurve curve, int curvaturePct)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            switch (curve)
            {
                case ResponseCurve.Normal:   return t;
                case ResponseCurve.Precisa:  return Math.Pow(t, PresetExponent(ResponseCurve.Precisa));
                case ResponseCurve.Rapida:   return Math.Pow(t, PresetExponent(ResponseCurve.Rapida));
                case ResponseCurve.Personalizada: return Math.Pow(t, CurvatureExponent(curvaturePct));
                case ResponseCurve.Digital:  return t < 0.5 ? 0.0 : 1.0;
                case ResponseCurve.Dinamica:
                {
                    // Sigmoide simetrica: suave en el centro y al borde, empinada en medio.
                    if (t <= 0.0) return 0.0;
                    if (t >= 1.0) return 1.0;
                    const double k = 2.2;
                    double a = Math.Pow(t, k), b = Math.Pow(1.0 - t, k);
                    return a / (a + b);
                }
                default: return t;
            }
        }

        // Deadzone radial + rescale [inner,outer]->[0,1] + Shape por curva. La via que usa la app.
        public static (double X, double Y) ApplyStick(StickInput s, double innerDeadzone,
                                                      double outerDeadzone, ResponseCurve curve, int curvaturePct)
        {
            double mag = Math.Sqrt(s.X * s.X + s.Y * s.Y);
            if (mag <= innerDeadzone || mag <= 0.0) return (0.0, 0.0);
            double outer = Math.Max(outerDeadzone, innerDeadzone + 1e-6);
            double t = Math.Clamp((mag - innerDeadzone) / (outer - innerDeadzone), 0.0, 1.0);
            t = Shape(t, curve, curvaturePct);
            double ux = s.X / mag, uy = s.Y / mag;
            return (ux * t, uy * t);
        }
```
(`PresetExponent` y `CurvatureExponent` ya existen del paso anterior. Añadir `Dinamica`, `Digital` al enum en `ControllerState.cs`.)

- [ ] **Step 4: Correr y ver pasar** — reportar el conteo real (sube ~5).
- [ ] **Step 5: Build GUI** — `dotnet build HidusbfModernGui/HidusbfModernGui.csproj -v q` → 0 errores.
- [ ] **Step 6: Commit** — `git ... commit -m "feat: curvas de stick Dinamica (S) y Digital en el nucleo (TDD)"`

---

### Task 2: UI — RESPUESTA como lista con mini-íconos de curva

**Files:** Modify `HidusbfModernGui/MainWindow.xaml`, `MainWindow.xaml.cs`

Contexto: hoy la RESPUESTA de cada stick son 4 botones (Precisa/Normal/Rápida/Personalizada) + el slider de Curvatura + una gráfica grande. Se reemplazan los 4 botones por un ComboBox con 6 ítems, cada uno con un mini-`Polyline` que dibuja su curva + el nombre. El slider de Curvatura sigue apareciendo solo para Personalizada. La gráfica grande y (futuro) el motor usan `ApplyStick(..., curve, curvaturePct)`.

**Interfaces:** Consume `ResponseCurve` (con Dinamica/Digital), `InputTransform.Shape`/`ApplyStick(...,curve,curvaturePct)`, `RemapSettings.LeftCurve`/`LeftCurvaturePct` (ya existen).

- [ ] **Step 1: ComboBox de RESPUESTA** — reemplazar en el XAML los 4 botones de cada stick por `ComboBox x:Name="LeftCurveList"` (y Right), `SelectionChanged="LeftCurve_Changed"`. Quitar los handlers de botón viejos (`LeftCurve_Click` etc.) o dejarlos sin uso para borrarlos en la limpieza.

- [ ] **Step 2: Poblar los ítems con mini-curva** — en `BuildRemapControls` (bajo `_updatingRemap`), llenar cada ComboBox con un `ComboBoxItem` por curva, cuyo `Content` es un `StackPanel` horizontal con un `Canvas`+`Polyline` (48x24) dibujando la curva vía `InputTransform.Shape(t, curve, 50)` para t en 0..1, y un `TextBlock` con el nombre. Tag = la `ResponseCurve`. Orden: Lineal(Normal), Precisa, Rápida, Dinámica, Digital, Personalizada. Método helper `AddCurveItem(ComboBox combo, string label, ResponseCurve curve)`.

- [ ] **Step 3: Handler + curvatura + redibujo** — `LeftCurve_Changed`/`RightCurve_Changed`: si `_updatingRemap` return; setear `_remap.LeftCurve` = (ResponseCurve)item.Tag; mostrar el panel de Curvatura solo si es Personalizada; `RedrawLeftCurve()`; `RememberRemap()`. Cambiar `DrawCurve` para que use `InputTransform.ApplyStick(new StickInput(t,0), inner, outer, curve, curvaturePct)` (o `Shape` directo) en vez del exponente — así dibuja bien la S y la Digital. `RedrawLeftCurve` pasa `_remap.LeftCurve` + `_remap.LeftCurvaturePct`.

- [ ] **Step 4: Restaurar selección al cargar perfil** — en `ApplyRemapSettingsToControls`, seleccionar en el ComboBox el ítem cuyo Tag == `_remap.LeftCurve`, y ajustar visibilidad/valor del slider de Curvatura.

- [ ] **Step 5: Build + tests** — 0 errores; tests siguen (mismo conteo que Task 1).
- [ ] **Step 6: Verificación manual** — elegir Dinámica dibuja una S; Digital dibuja un escalón; Personalizada muestra el slider y dobla la curva; cerrar/reabrir mantiene la elección.
- [ ] **Step 7: Commit** — `git ... commit -m "feat: selector de RESPUESTA con mini-iconos de curva (Lineal/Precisa/Rapida/Dinamica/Digital/Personalizada)"`

---

## Self-Review
- Cobertura: Dinámica (S) + Digital en el núcleo → Task 1; lista con íconos + wiring + persistencia → Task 2. Constante excluida (documentado). ✓
- Placeholders: código completo en Task 1; Task 2 especifica los controles y el helper de ítems (el dibujo exacto del mini-Polyline lo compone el implementador con `Shape`, acotado).
- Consistencia: `ResponseCurve` (con Dinamica/Digital), `Shape`, `ApplyStick(...,curve,curvaturePct)` de Task 1 son lo que consume Task 2. `RemapSettings.LeftCurve`/`LeftCurvaturePct` ya existen. `DrawCurve` pasa de exponente a curve+curvaturePct.
- Riesgo: reemplazar los 4 botones por ComboBox toca la sección de sticks recién hecha; verificar que el slider de Curvatura y el redibujo sigan bien (Task 2 Step 6). Borrar los handlers de botón viejos en la limpieza si quedan sin uso.

## Execution Handoff
Plan en `docs/superpowers/plans/2026-07-18-stick-curves.md`. Opciones: (1) Subagentes (recomendado), (2) Inline.
