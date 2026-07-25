# Perfiles como sección propia, luz instantánea al arrancar y limpieza de texto — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cinco arreglos pedidos por el usuario: (1) la luz del mando se aplica **al abrir la app**, no un segundo después; (2) **un solo perfil** para luz y mando — se confirma y se ejecuta; (3) **PERFILES** sube a ser una sección propia, al mismo nivel que "Configurar el mando" y "Luces del mando"; (4) seleccionar un perfil **lo aplica**, sin botón "Aplicar" — solo GUARDAR y BORRAR, más nombre y tasa; (5) el botón **refrescar** baja del sidebar a la página de dispositivos, que es lo único que refresca.

**Sustituye** a la Task 8 del plan `2026-07-25-ui-modelo-playstation.md` (perfiles), ampliándola.

---

## Diagnóstico verificado del punto (1)

`ReapplyIntent()` se llama **al final de `RefreshDevicesList()`** (`MainWindow.xaml.cs:2592`), es decir, después de un escaneo por PowerShell de ~1 s. Por eso el color aparece con retraso visible al abrir. No es un problema de la luz: es que **está esperando a un escaneo que no necesita**.

La app ya sabe encontrar el mando **en-proceso y en milisegundos** — es lo que se construyó para que las luces funcionaran con HidHide (`HidHideControl.FindPhysicalGamepadInstanceId()`, y `DualSenseReader.FindUsbDualSense()` por HidSharp). El arreglo es usar ese camino al arrancar y dejar el escaneo para lo que sí lo necesita: la lista de dispositivos.

## Global Constraints

- UI en **español**, tema monocromo.
- **Aplicación en vivo** en todo: seleccionar perfil aplica; ningún "Aplicar" nuevo.
- **Nunca perder perfiles**: la migración de los dos archivos viejos no los borra ni los modifica (ver Task 2).
- El proyecto de tests linkea fuentes individualmente; `GameProfile.cs` va al csproj.
- Commits **sin** Co-Authored-By. El push lo hace el usuario.

---

### Task 1: La luz se aplica al abrir, sin esperar al escaneo

**Files:** `HidusbfModernGui/MainWindow.xaml.cs`

- [ ] **Step 1: Resolver el mando sin PowerShell.** Añadir un camino directo que no dependa de `_allDevices`:

```csharp
// Instance id del DualSense fisico SIN pasar por el escaneo de PowerShell (~1 s): enumera
// HID en-proceso, que tarda milisegundos. Es el mismo camino que ya usan las luces cuando
// HidHide oculta el mando, reutilizado aqui para no hacer esperar al arranque.
private static string? FindPadInstanceIdFast()
{
    try { return HidHideControl.FindPhysicalGamepadInstanceId(); }
    catch { return null; }
}
```

- [ ] **Step 2: Aplicar la intención en `Window_Loaded`**, antes (y con independencia) de `RefreshDevicesList()`:

```csharp
// La luz guardada se aplica de inmediato al abrir: el usuario ve su color al mismo tiempo
// que la ventana, no un segundo despues. Antes esto colgaba del final del escaneo de
// dispositivos, que no aporta nada a encender una luz.
ApplySavedLightNow();
```

`ApplySavedLightNow()` carga `IntentStore.Load()`, resuelve el mando con `FindPadInstanceIdFast()` y llama a `DualSenseLight.Apply(...)`. Si no hay mando todavía, no pasa nada: el camino existente (reaplicar al reconectar, vía `WM_DEVICECHANGE`) sigue cubriéndolo.

- [ ] **Step 3: `ReapplyIntent()` deja de ser el camino principal.** Se conserva **solo** como red para la reconexión (ya usa `_intentReapplied`), y su llamada al final del escaneo se mantiene por si el arranque rápido no encontró el mando. Marcar ambos con un comentario que diga cuál es cuál, para que nadie los vuelva a fusionar.
- [ ] **Step 4: Verificación** — abrir la app con el mando conectado y un color guardado: **la barra de luz debe encenderse a la vez que aparece la ventana**, sin el retraso de antes.
- [ ] **Step 5: Commit** — `git add -u && git commit -m "fix: la luz guardada se aplica al abrir la app, sin esperar al escaneo de dispositivos"`

---

### Task 2: Un solo perfil (luz + mando + tasa), con migración

Es la Task 1 del plan `2026-07-25-rediseno-ui-mando.md`, **sin cambios**: `GameProfile { Name, Rate?, Light?, Remap? }` + `GameProfileStore` (`game-profiles.json`) + `Migrate(...)` con TDD, fusionando por nombre y excluyendo el pseudo-perfil `__ultimo_usado__`. Los archivos viejos (`profiles.json`, `remap-profiles.json`) **se quedan intactos** como respaldo.

- [ ] **Step 1** — ejecutar esa tarea tal cual está escrita (código y tests completos allí).
- [ ] **Step 2: Commit** — el de esa tarea.

---

### Task 3: PERFILES, sección propia con aplicar-al-seleccionar

**Files:** `HidusbfModernGui/MainWindow.xaml(.cs)`

**Interfaces:**
- La sub-nav del hub del mando pasa de dos botones a **tres**: `ConfigTabBtn` ("CONFIGURAR EL MANDO"), `LucesTabBtn` ("LUCES DEL MANDO") y **`PerfilesTabBtn`** ("PERFILES"), cada uno mostrando su panel (`ConfigPanel` / `LucesPanel` / **`PerfilesPanel`**).

- [ ] **Step 1: El tercer botón y su panel.** Añadir `PerfilesTabBtn` junto a los otros dos y un `Grid x:Name="PerfilesPanel"` hermano de los otros dos paneles. `ShowPerfilesPanel` sigue el mismo patrón que `ShowConfigPanel`/`ShowLucesPanel` (los tres se alternan por visibilidad en un solo sitio).
- [ ] **Step 2: Contenido del panel.** Una lista de perfiles (uno por fila, no un desplegable: la lista es la sección entera, hay sitio de sobra) y, por cada fila: el **nombre** y su **tasa** si la lleva. Debajo: caja de **NOMBRE**, selector de **TASA**, y los botones **GUARDAR** y **BORRAR**. **No hay botón CARGAR ni APLICAR.**
- [ ] **Step 3: Seleccionar = aplicar.**

```csharp
// Seleccionar un perfil lo APLICA. No hay boton "Cargar" ni "Aplicar": un perfil que esta
// seleccionado pero no aplicado es un estado que solo sirve para confundir - la lista
// muestra lo que esta puesto ahora mismo, no una intencion pendiente.
private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (_updatingProfiles) return;                       // refrescos internos no aplican
    if (ProfileList.SelectedItem is not GameProfile p) return;
    ApplyGameProfile(p);
}
```

`ApplyGameProfile(p)` aplica **solo las mitades que el perfil traiga**: `p.Light != null` → aplicar la luz por el camino que ya existe; `p.Remap != null` → `_remap = CloneRemapSettings(p.Remap); _remap.Sanitize(); ApplyRemapSettingsToControls();`; `p.Rate != null` → seleccionar esa tasa en el dispositivo elegido. Un perfil que no toca la luz **no la cambia**.

- [ ] **Step 4: GUARDAR y BORRAR.** GUARDAR toma el nombre de la caja (si está vacía, reutiliza el del perfil seleccionado) y guarda **luz actual + `_remap` actual + tasa elegida** en un solo `GameProfile`. Si el nombre ya existe, **lo sobrescribe** (con un aviso en la barra de estado, no un diálogo). BORRAR quita el seleccionado. Ambos escriben con `GameProfileStore.Save`.
- [ ] **Step 5: Retirar las barras viejas.** Quitar "PERFILES DEL REMAPEO" del configurador y la sección de perfiles de la página de luces: ahora viven en la sección propia. `RemapProfileStore` se conserva **solo** para el pseudo-perfil `__ultimo_usado__` (el estado en vivo del configurador), que no es un perfil del usuario.
- [ ] **Step 6: Verificación** — build 0/0, suite completa PASS. Manual: los perfiles viejos aparecen ya migrados; seleccionar uno cambia luz y configuración **sin pulsar nada más**; GUARDAR con un nombre existente lo sobrescribe; BORRAR lo quita; al reabrir la app siguen ahí.
- [ ] **Step 7: Commit** — `git add -u && git commit -m "feat(ui): PERFILES como seccion propia; seleccionar un perfil lo aplica"`

---

### Task 4: Menos texto en el configurador

**Files:** `HidusbfModernGui/MainWindow.xaml`

- [ ] **Step 1: Fuera el encabezado y la explicación del mando en vivo.** Eliminar el `TextBlock` "MANDO EN VIVO (salida al juego)" y el de "Muestra lo que el juego recibe tras tus ajustes. Con el mando virtual apagado tambien se mueve (para configurar antes de activar)." **El mando dibujado no necesita que le pongan encima un cartel diciendo que es un mando.** Lo que la frase explicaba (que se mueve con el motor apagado) el usuario lo descubre en un segundo moviendo el stick.
- [ ] **Step 2: Barrido del resto.** Revisar el `ConfigPanel` en busca de textos que describan lo que el propio control ya dice, y quitarlos. Regla para decidir: **si el texto explica lo que se ve, sobra; si explica lo que NO se ve —un riesgo, un requisito, una consecuencia— se queda** (por eso el aviso de anticheat se mudó a Ajustes en vez de borrarse, y el "?" del mando virtual sigue existiendo).
- [ ] **Step 3: Verificación** — build 0/0. Manual: la tarjeta del mando en vivo queda solo con el dibujo y su tuerca de opciones.
- [ ] **Step 4: Commit** — `git add -u && git commit -m "refactor(ui): fuera los textos que describen lo que el control ya muestra"`

---

### Task 5: El botón refrescar, dentro de dispositivos

**Files:** `HidusbfModernGui/MainWindow.xaml`

- [ ] **Step 1: Quitarlo del sidebar.** Eliminar el cuarto botón de la barra lateral (`RefreshDevicesBtn_Click`, icono `RefreshIconPath`). El sidebar queda con tres: inicio, mando, sistema.
- [ ] **Step 2: Ponerlo donde actúa.** En la página de **DISPOSITIVOS** (la del dashboard/overclock), junto al encabezado "DISPOSITIVOS" y su contador, añadir un botón con el mismo icono y el **mismo handler** (`Click="RefreshDevicesBtn_Click"`, sin renombrar nada). Tooltip: "Volver a escanear los dispositivos USB".
- [ ] **Step 3: Por qué** — dejarlo en el sidebar sugería que refresca *la app*; solo vuelve a escanear la lista de dispositivos. Un botón global que hace algo local es una promesa que no cumple.
- [ ] **Step 4: Verificación** — build 0/0. Manual: el sidebar tiene tres iconos; el botón de la página de dispositivos vuelve a escanear (el contador se actualiza y la barra de estado lo dice).
- [ ] **Step 5: Commit** — `git add -u && git commit -m "refactor(ui): el boton refrescar vive en la pagina de dispositivos, que es lo que refresca"`

---

## Self-review

- **Cobertura del pedido:** (1) luz al abrir → Task 1, con la causa localizada (`ReapplyIntent` colgaba del escaneo de PowerShell); (2) un solo perfil → Task 2; (3) PERFILES al mismo nivel → Task 3 Step 1; (4) seleccionar aplica, solo GUARDAR/BORRAR + nombre + tasa → Task 3 Steps 2-4; (5) refrescar dentro de dispositivos → Task 5. ✓
- **Placeholders:** ninguno; Task 2 reutiliza código y tests ya escritos en otro plan, referenciado explícitamente. ✓
- **Tipos consistentes:** `GameProfile`/`GameProfileStore` (Task 2) consumidos por Task 3; `RefreshDevicesBtn_Click` se **mueve sin renombrarse** (Task 5) para que el compilador cace cualquier referencia rota; `FindPadInstanceIdFast` (Task 1) reutiliza el resolutor que ya existe para HidHide. ✓
- **Riesgo cubierto:** Task 1 no elimina `ReapplyIntent`, lo degrada a red de reconexión — si el arranque rápido no encuentra el mando (apagado, por Bluetooth), el camino viejo sigue ahí. ✓
- **Criterio para borrar texto declarado** (Task 4 Step 2), para que la limpieza no se lleve por delante un aviso que sí importa. ✓
