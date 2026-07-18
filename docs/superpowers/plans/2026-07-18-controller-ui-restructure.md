# Reorganización de la UI del mando (hub Configurar / Luces) — Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development o superpowers:executing-plans. Steps con checkbox.

**Goal:** Que el botón "Mando" del sidebar abra **"Configurar el mando"** (el remapeador) por defecto, con un sub-botón **"Luces del mando"** que lleva a lo que hoy es la página de color/LED/efectos (movida intacta). Y darle al configurador un shell visual rico (diagrama del mando + panel con pestañas + comparación Físico vs Ajustado), en el tema monocromo propio.

**Architecture:** Dentro de la pestaña "Mando" actual se añade una sub-navegación de dos vistas: "Configurar el mando" (nueva) y "Luces del mando" (el contenido de luz actual, envuelto y mostrado por visibilidad — SIN mover su lógica, para no romper nada). El configurador edita `RemapSettings` (ya existe del núcleo) y guarda `RemapProfile`; la vista previa en vivo (diagrama que reacciona + gauges Físico/Ajustado) depende del motor de la Fase 2 del plan del remapeador (hardware) y se conecta después.

**Tech Stack:** .NET 9, WPF, C#. Sin NuGets nuevos para este plan de UI.

## Global Constraints

- .NET 9, WPF, x64. Tema monocromo (diez colores de `Theme.xaml`); nada de clonar DSX.
- La UI NUNCA muestra valores crudos al usuario (se mantiene la simplificación del spec: %, preajustes, vistas visuales; lo confuso en "Avanzado").
- **Mover la página de luz debe preservar TODA su funcionalidad** (color, hex, presets, LED de jugador, brillo, rainbow con estilos/velocidad, efectos de LED con velocidad, perfiles, persistencia). Es relocalización visual, no reescritura.
- Este plan **reemplaza** las tareas de UI (Fase 3, Tasks 8–12) del plan `2026-07-18-controller-remapper.md`: la UI del remapeador vive aquí. La Fase 1 (núcleo, ya hecha) y la Fase 2 (motor E/S, pendiente de hardware) de ese plan siguen igual.
- Fuera de alcance (YAGNI): datos de movimiento/giroscopio, "green screen", capas de modificadores, perfiles por juego.
- Congelados (no tocar la lógica): `DualSenseLight.cs`, `LightIntent.cs`, `PlayerLedWalker.cs`, `RainbowWalker.cs`, `ColourMath.cs`, `ColourRamp.cs`, `SystemManager.cs`, `PollingCore.cs`, `LightProfile.cs`, `Theme.xaml`. (La UI de luz se mueve, pero sus handlers en MainWindow.xaml.cs se conservan tal cual.)
- Commits SIN `Co-Authored-By`. git: `git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com"`. Push lo hace el usuario.
- Carpeta: `C:\Users\Administrator\Downloads\work ultrapolling\UltraPolling`. Build de prueba: `bin\Debug\net9.0-windows\UltraPolling.exe`.

## Dependencias entre tareas

- Tareas 1–2 (sub-nav + mover luces): **construibles y verificables ya**, sin hardware.
- Tareas 3–5 (shell + controles del configurador editando RemapSettings + perfiles): **construibles ya**; los controles editan/guardan aunque el motor no esté (no aplican a un juego hasta la Fase 2).
- Tarea 6 (vista previa en vivo Físico/Ajustado + diagrama reactivo): **depende de la Fase 2** (el motor produciendo `ControllerState`). Se hace después de que el spike de hardware funcione.

---

### Task 1: Sub-navegación en la pestaña "Mando" (Configurar | Luces)

**Files:** Modify `HidusbfModernGui/MainWindow.xaml`, `MainWindow.xaml.cs`

Contexto: hoy el botón gamepad del sidebar (`LightNavBtn_Click`) abre la pestaña índice 2, cuyo contenido ES la página de luz. Vamos a: (a) poner una fila de dos botones-segmento arriba de esa pestaña ("Configurar el mando" | "Luces del mando"); (b) envolver TODO el contenido de luz actual en un contenedor `LucesPanel` (Visibility gestionada); (c) añadir un contenedor vacío `ConfigPanel` (visible por defecto). El botón del sidebar sigue abriendo la pestaña, ahora mostrando Configurar.

- [ ] **Step 1: Envolver el contenido de luz + añadir la sub-nav** — en `MainWindow.xaml`, dentro del `TabItem` "Light" (~L453), justo dentro de su `Grid` raíz, insertar arriba una fila con dos botones y envolver el contenido existente (el `Grid x:Name="LightPanel"` y el `LightEmptyState`) en un `Grid x:Name="LucesPanel"`. Añadir un `Grid x:Name="ConfigPanel"` hermano (vacío por ahora, se llena en Task 3), visible por defecto; `LucesPanel` colapsado. La fila de sub-nav:
```xml
<StackPanel Orientation="Horizontal" Margin="24,16,24,0">
    <Button x:Name="ConfigTabBtn" Content="CONFIGURAR EL MANDO" Style="{StaticResource InstrumentButton}"
            Click="ShowConfigPanel" Margin="0,0,10,0"/>
    <Button x:Name="LucesTabBtn" Content="LUCES DEL MANDO" Style="{StaticResource InstrumentButton}"
            Click="ShowLucesPanel"/>
</StackPanel>
```
(Colocar el contenido — sub-nav arriba, luego un `Grid` que contenga `ConfigPanel` y `LucesPanel` superpuestos por visibilidad. Ajustar filas del Grid del TabItem según haga falta.)

- [ ] **Step 2: Handlers de sub-nav** — en `MainWindow.xaml.cs`:
```csharp
        private void ShowConfigPanel(object sender, RoutedEventArgs e)
        {
            ConfigPanel.Visibility = Visibility.Visible;
            LucesPanel.Visibility = Visibility.Collapsed;
        }

        private void ShowLucesPanel(object sender, RoutedEventArgs e)
        {
            ConfigPanel.Visibility = Visibility.Collapsed;
            LucesPanel.Visibility = Visibility.Visible;
            RefreshPlayStationDevices();   // igual que hoy al entrar a la luz
        }
```
Y en `LightNavBtn_Click`, tras `MainTabControl.SelectedIndex = 2;`, mostrar Configurar por defecto: `ShowConfigPanel(this, null);` (quitar el `RefreshPlayStationDevices()` de ahí; ahora vive en ShowLucesPanel). Verificar que `BuildLightControls`/`RefreshPlayStationDevices` sigan corriendo cuando se entra a Luces.

- [ ] **Step 3: Build + verificación manual** — el botón gamepad abre "Configurar el mando" (panel vacío por ahora); el botón "LUCES DEL MANDO" muestra toda la página de luz de siempre, **funcionando igual** (color, rainbow, efectos, perfiles). Volver a "Configurar" la oculta.

- [ ] **Step 4: Commit** — `git ... commit -m "feat: sub-nav del mando (Configurar | Luces); la luz pasa a subpagina"`

### Task 2: Verificar que la luz quedó intacta tras el movimiento

**Files:** (sin cambios de código; es una tarea de verificación + posibles ajustes menores)

- [ ] **Step 1** — probar exhaustivo en `bin\Debug`: seleccionar mando, cambiar color/hex/presets, LED de jugador, brillo, rainbow (estilos + velocidad), efectos de LED (con su barra), guardar/cargar perfil, cerrar y reabrir (persistencia). Todo debe seguir funcionando desde "Luces del mando".
- [ ] **Step 2** — si algo se rompió por el reanidado (bindings/visibility), corregir el XAML mínimamente. Commit solo si hubo cambios: `git ... commit -m "fix: ajustes tras mover la pagina de luz a subpagina"`

### Task 3: Shell del configurador (diagrama + panel con pestañas)

**Files:** Modify `HidusbfModernGui/MainWindow.xaml` (llenar `ConfigPanel`)

- [ ] **Step 1: Layout del ConfigPanel** — dos columnas: izquierda un **diagrama del mando** (por ahora estático: un dibujo del DualSense con `Path`/formas en el tema monocromo), derecha un panel con una fila de pestañas propias (botones-segmento) **STICKS · GATILLOS · TOUCHPAD · BOTONES** que alternan contenedores por visibilidad (como la sub-nav). Cada contenedor vacío por ahora (los llena Task 4). Incluir arriba un aviso honesto de anticheat (texto del spec) y, si `DriverCheck` reporta drivers faltantes, una tarjeta de guía en vez de los controles.
- [ ] **Step 2: Handlers de las pestañas** — un método por pestaña que muestra su contenedor y oculta los otros (mismo patrón que ShowConfigPanel/ShowLucesPanel).
- [ ] **Step 3: Build + verificación** — se ve el diagrama a la izquierda y las 4 pestañas a la derecha, alternando (contenido vacío).
- [ ] **Step 4: Commit** — `git ... commit -m "feat: shell del configurador (diagrama + pestañas Sticks/Gatillos/Touchpad/Botones)"`

### Task 4: Controles del configurador (editan RemapSettings + perfiles)

**Files:** Modify `HidusbfModernGui/MainWindow.xaml`, `MainWindow.xaml.cs`

Consume el núcleo ya hecho: `RemapSettings`, `ResponseCurve`, `PadButton`, `TouchZone`, `RemapProfile`/`RemapProfileStore`, `InputTransform` (para dibujar la curva).

- [ ] **Step 1: Pestaña STICKS** — por stick: slider "Zona muerta" (0–30%), 3 botones "Respuesta" (Precisa/Normal/Rápida), y una gráfica de la curva dibujada con `InputTransform.ApplyStick` sobre 0..1 en un `Canvas`/`Path`. "Alcance" (70–100%) en un expander "Avanzado". Cada cambio escribe en un `RemapSettings _remap` (campo nuevo en MainWindow).
- [ ] **Step 2: Pestaña GATILLOS** — dos sliders "Punto de disparo" (0–100%) con barra visual → `_remap.L2PointPct/R2PointPct`.
- [ ] **Step 3: Pestaña BOTONES** — lista/diagrama de botones; cada uno con un desplegable de destino (`PadButton`) → `_remap.ButtonRemap`.
- [ ] **Step 4: Pestaña TOUCHPAD** — rejilla 2×2; cada zona con desplegable de `PadButton` → `_remap.TouchZoneMap`.
- [ ] **Step 5: Perfiles** — barra con desplegable + GUARDAR/CARGAR/BORRAR sobre `RemapProfileStore`; al cargar, reconstruir los controles desde `RemapSettings` (bajo un guard `_updatingRemap` para no disparar escrituras). Guardar el perfil activo con debounce.
- [ ] **Step 6: Build + verificación** — mover los controles actualiza la gráfica de curva y se puede guardar/cargar un perfil; cerrar y reabrir mantiene el perfil activo. (Aún no afecta a ningún juego — eso es la Fase 2 del motor.)
- [ ] **Step 7: Commit** — `git ... commit -m "feat: controles del configurador (sticks/gatillos/botones/touchpad + perfiles)"`

### Task 5: Ícono/copy del hub + README

**Files:** Modify `HidusbfModernGui/MainWindow.xaml` (tooltip del sidebar), `README.md`

- [ ] **Step 1** — actualizar el tooltip del botón gamepad del sidebar de "Color del mando (PlayStation)" a "Mando: configurar y luces". En el README, reflejar que la sección Mando ahora tiene Configurar (remapeador) y Luces, con el aviso honesto de anticheat del configurador.
- [ ] **Step 2: Commit** — `git ... commit -m "docs: hub del mando (configurar + luces) en tooltip y README"`

### Task 6 (DESPUÉS de la Fase 2 del motor): Vista previa en vivo Físico vs Ajustado

**Files:** Modify `HidusbfModernGui/MainWindow.xaml.cs` (+ el diagrama)

> Requiere que el motor del remapeador (Fase 2, hardware) esté produciendo `ControllerState` del físico y el ajustado. No hacer antes del spike.

- [ ] **Step 1** — un timer de UI (~60/s) lee del motor el estado físico y el transformado; el diagrama del mando resalta botones pulsados y posiciona los sticks; cada pestaña muestra dos gauges ("Físico" gris, "Ajustado" con el color de dato) para gatillos/sticks. Igual que la referencia, pero monocromo y propio.
- [ ] **Step 2: Verificación manual (hardware)** — mover el mando anima el diagrama; cambiar un ajuste cambia el gauge "Ajustado" en vivo.
- [ ] **Step 3: Commit** — `git ... commit -m "feat: vista previa en vivo Fisico vs Ajustado en el configurador"`

---

## Self-Review

**Cobertura:** botón Mando → Configurar por defecto + sub-botón Luces → Task 1; luz preservada → Task 2; shell visual (diagrama + pestañas) → Task 3; controles del remapeador en la UI simple → Task 4; copy/README → Task 5; vista previa Físico/Ajustado como la referencia → Task 6 (post-Fase 2). Fuera: motion, green-screen. ✓

**Placeholders:** los pasos de estructura tienen XAML/handlers concretos; el diagrama del mando (Task 3) se especifica como formas WPF en el tema — el dibujo exacto lo compone el implementador siguiendo `Theme.xaml` (no es un placeholder de lógica, es diseño visual acotado).

**Consistencia:** `RemapSettings`/`ResponseCurve`/`PadButton`/`TouchZone`/`RemapProfileStore`/`InputTransform` (Fase 1, ya en el repo) son lo que consumen las Tasks 4 y 6. La sub-nav y las pestañas usan el mismo patrón de visibilidad. La luz no cambia de lógica, solo de contenedor.

**Riesgos:** (1) mover la página de luz puede romper bindings/visibility → Task 2 es una verificación dedicada. (2) La vista previa en vivo depende de la Fase 2; por eso es la última y está marcada. (3) No clonar DSX: diseño monocromo propio.

## Execution Handoff

Plan en `docs/superpowers/plans/2026-07-18-controller-ui-restructure.md`. Tasks 1–5 son construibles ya (sin hardware); la Task 6 espera al motor de la Fase 2. Opciones: (1) Subagentes (recomendado), (2) Inline.
