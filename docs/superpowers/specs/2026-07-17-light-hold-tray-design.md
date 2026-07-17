# Mantener el color del mando: bandeja + reafirmación — Diseño

**Fecha:** 2026-07-17
**Estado:** aprobado para escribir el plan de implementación
**Rama:** redesign/monochrome

## Problema

El DualSense no tiene memoria donde un tercero pueda grabar un color. La barra de luz
solo se fija con *output reports* HID (`DualSenseLight.Apply` manda el report `0x02` por USB
y lo cierra); **gana siempre el último que escribe**. Por eso Steam Input y los juegos
"pisan" el color que puso UltraPolling, y por eso al reconectar el mando (o tras nuestro
propio replug/overclock, que resetea el mando) el color se pierde.

El usuario pidió "guardar el color en el mando". Eso es físicamente imposible: no existe en
el protocolo de Sony. El objetivo real —que el color **sobreviva** a que lo pisen— sí tiene
solución: que UltraPolling mantenga el color y lo reafirme.

## Objetivo

UltraPolling guarda una única "intención de luz" (un color fijo **o** un rainbow), la
persiste en disco, la reaplica al arrancar y al reconectar el mando, y la reafirma de forma
continua mientras vive en la bandeja del sistema — para que el color aguante durante el juego.

## No-objetivos (YAGNI, decididos con el usuario)

- **Bluetooth.** v1 es solo USB/cable. Por BT el mando enumera bajo `BTHENUM` (no
  `USB\VID_054C`, así que ni la enumeración ni la reconexión lo verían) y requiere otro
  reporte (`0x31` con CRC-32). Queda para una posible v2. La UI debe decir con claridad
  cuando un mando está por BT y no se puede controlar, en vez de fallar en silencio.
- **Arranque con Windows.** Descartado: el `requireAdministrator` del manifiesto haría que un
  acceso en Inicio dispare UAC en cada login, o exigiría una tarea programada con privilegios.
  La promesa queda explícitamente acotada a "mientras UltraPolling esté corriendo".
- **Cadencia agresiva de reafirmación** (escribir al ritmo de Steam para dominar en vivo,
  aceptando parpadeo). Se defalta a la cadencia suave; solo se añade si el usuario la pide.

## Realidad honesta que el diseño no esconde

Contra Steam Input escribiendo el color en vivo hay dos mundos posibles, y no se puede saber
cuál aplica sin probar en el hardware del usuario (queda como paso de verificación):

1. **Steam abre el mando compartido** → podemos reescribir encima, pero el LED **parpadea**
   entre nuestro color y el de Steam.
2. **Steam abre el mando en exclusiva** → cada reafirmación nuestra **falla con Win32 error 5**
   y el efecto no puede ganar nada durante el juego.

En **ambos** casos el arreglo limpio es **apagar el control de luz de Steam Input**: sin
competidor, nuestro color simplemente se queda —sin parpadeo, sin bloqueo—. El diseño:

- Detecta el error 5 y, en vez de spamear el log, **surface una vez** el mensaje que manda al
  usuario a apagar el control de luz de Steam.
- Documenta ese ajuste de Steam como el acompañante recomendado (hint en la UI + README).

La reafirmación suave (~1 s) sí resuelve limpiamente los casos que **no** son un competidor
escribiendo en vivo: reconexión, nuestro propio replug/overclock, y cualquier pisada de una
sola vez (un menú, el arranque de un juego que fija la barra una vez).

## Arquitectura

Enfoque A: **una sola fuente de verdad** para "qué debe mostrar el mando" (la intención de
luz), persistida, que maneja un **único timer conductor**. Los controles de la página de luz
y el rainbow pasan a *fijar esa intención* en vez de escribir al mando directamente.

### Componentes nuevos (lógica pura, sin WPF)

**`HidusbfModernGui/LightIntent.cs`** — nuevo archivo, se enlaza por ruta al proyecto de tests.

- `enum LightIntentKind { Static, Rainbow }`
- `sealed class LightIntent` — clase mutable y **plana** (System.Text.Json quiere ctor sin
  parámetros y props con setter, la misma razón que documenta `LightProfile`):
  - `LightIntentKind Kind`
  - `byte R, G, B`
  - `PlayerLeds Player` (default `Player1`)
  - `LedBrightness Brightness` (default `High`)
  - `RainbowStyle Style` (default `Smooth`)
  - `int TicksPerColour` (default 3)
  - `string? BoundInstanceId` — **pista** de reselección tras reiniciar, nunca una llave dura
  - Helpers: `LightState ToLightState()` (usa R,G,B,Player,Brightness), `FromStatic(LightState,
    boundId)`, `FromRainbow(style, ticks, player, brightness, boundId)`.
  - **Los cuatro campos de LED (R,G,B,Player,Brightness) van siempre**, incluso en una
    intención rainbow, porque `Rainbow_Tick` construye su `LightState` con Player y Brightness,
    no solo con RGB. En modo rainbow, R,G,B son el último color mostrado (informativo); el
    color real lo deriva `RainbowWalker` y **nunca se persiste** (cambia hasta 64 veces/s).

**`IntentStore`** (en el mismo archivo o junto) — espejo de `ProfileStore`:
- Mismo directorio `%APPDATA%\UltraPolling`, archivo `active.json`.
- Mismas `JsonSerializerOptions` (`WriteIndented` + `JsonStringEnumConverter`, para que
  `Kind/Player/Brightness/Style` se guarden como nombres y sobrevivan a reordenar los enums).
- `OverrideDirectoryForTests` interno.
- `LightIntent? Load()` — devuelve `null` ante archivo ausente/corrupto (catch-all, como
  `ProfileStore.Load`).
- `OpResult Save(LightIntent)` — `CreateDirectory` + copiar `active.json` → `active.json.backup`
  + `WriteAllText`. Mismas semánticas de recuperación que `ProfileStore`.
- Lleva un comentario apuntando a `ProfileStore` como su hermano (la duplicación del patrón de
  escritura es el costo de mantener `ProfileStore`/`LightProfile.cs` congelados).

### Componente nuevo de resolución de destino (lógica pura)

La regla de "a qué mando le pega" se implementa como función pura y testeable (en
`LightIntent.cs` o un archivo hermano), separada del I/O:

Dado el conjunto de `InstanceId` de DualSense presentes + el `InstanceId` seleccionado (o
`null`) + el `BoundInstanceId` de la intención, devuelve el `InstanceId` destino o `null`:
1. Si hay un DualSense seleccionado y está presente → ese.
2. Si no, y hay exactamente uno presente → ese.
3. Si no, y hay varios presentes sin selección → el primero determinista (y la UI dice cuál).
4. Si no hay ninguno → `null` (esperar en silencio).

**Multi-mando:** el destino es **solo uno** (regla de arriba), no "todos los presentes". Esto
es predecible para el caso de un mando y evita pisar un segundo DualSense que el usuario haya
puesto a otro color, y evita mandar un report de DualSense a un DualShock 4.

### El timer conductor (WPF, en `MainWindow.xaml.cs`)

Se **recicla `_rainbowTimer`** en un único "conductor de luz" (no se añade un segundo timer,
para que nunca haya dos escritores desfasados pisándose):

- Cada tick lee la intención activa (`_activeIntent`, campo en memoria = fuente de verdad):
  - **Rainbow:** `_rainbowWalker.Step()` + resolver destino + `Apply` (comportamiento de hoy).
  - **Static:** resolver destino + `Apply(intent.ToLightState())`.
- **El `LightState` se construye desde la intención (datos), NUNCA desde los combos de la UI.**
  Esto es obligatorio: los combos (`PlayerLedList`, `BrightnessList`, `RainbowStyleList`) no
  tienen items hasta que se abre la pestaña de luz una vez (`BuildLightControls`), así que un
  tick que leyera `(ComboBoxItem)PlayerLedList.SelectedItem).Tag` lanzaría `NullReferenceException`
  si el conductor corre antes de abrir la pestaña (p. ej. reaplicando al arrancar).
- Al cambiar la intención (modo, velocidad, color) se retunea el intervalo y se reinicia:
  rainbow usa `RainbowWalker.IntervalFor(TicksPerColour)`; static usa fijo ~1000 ms.
- Prioridad `DispatcherPriority.Render` (el rainbow la necesita para no ralentizarse; un tick
  static de 1 s a esa prioridad no cuesta nada).
- **Resolución de destino cacheada:** resolver el `InstanceId` presente y su ruta HID se cachea
  y solo se re-sondea en cadencia lenta (~1 s) o tras un fallo de escritura — para que el tick
  rápido del rainbow (hasta 64/s) no camine el árbol de dispositivos en cada tick.
- **Destino ausente = no-op silencioso.** Si el destino resuelto no está presente
  (`FindHidPaths(id).Count == 0`), el tick no escribe y **no** loguea error. Un lazo apuntado a
  un mando que se fue no debe spamear el log.
- **Error 5 (exclusiva):** se traga/loguea una sola vez con el mensaje que manda a apagar el
  control de luz de Steam; no se repite cada tick.
- Al minimizar, el conductor **sigue vivo** (el dispatcher late aunque la ventana esté oculta).
  Solo se detiene en "Salir de verdad".
- Mientras la ventana está **oculta**, el tick del rainbow omite el trabajo de UI
  (`Picker.SelectedColor` + `UpdateSwatch`) —CPU desperdiciada contra un árbol visual oculto— y
  solo sincroniza el picker al restaurar.

### Bandeja del sistema

- Activar `<UseWindowsForms>true</UseWindowsForms>` en el csproj. **No añade NuGet ni cambia el
  empaquetado**: `System.Windows.Forms.dll` ya viene dentro del runtime `Microsoft.WindowsDesktop.App`
  que WPF arrastra al bundle self-contained. Los checks de `package.ps1` (>40MB, sin DLLs sueltas)
  siguen pasando.
- El código de bandeja va en un archivo/partial pequeño y aislado con **alias explícitos**
  (`using WinFormsNotifyIcon = System.Windows.Forms.NotifyIcon`, etc.), porque `UseWindowsForms`
  introduce colisiones de nombres (`Application`, `MessageBox`, `Color`, `Brush`, `Point`,
  `ContextMenu`) con `System.Windows(.Media)`, que `MainWindow.xaml.cs` usa mucho.
- `App.xaml`: `ShutdownMode="OnExplicitShutdown"` — esconder la ventana nunca mata el proceso;
  el único punto que termina la app es "Salir de verdad".
- **Minimizar a bandeja** (`HideToTray()`): `Hide()` la ventana, detiene `_meterTimer` y llama
  `_meter.Stop()` para **liberar el handle HID del medidor** (medir en bandeja no tiene sentido,
  y un handle abierto es justo lo que puede vetar un `CM_Query_And_Remove_SubTree` posterior).
  **Deja vivo el conductor de luz.**
- `CloseButton_Click` (la X) → `HideToTray()`. `OnClosing` override: `e.Cancel = true` +
  `HideToTray()` salvo que un flag `_reallyExit` esté puesto → así **Alt+F4 también minimiza**.
- Menú del ícono: **"Mostrar"** (muestra/activa la ventana; puede reanudar el medidor),
  **"Salir de verdad"** (`_reallyExit = true`, detiene todos los timers, `_meter.Dispose()`
  para liberar el handle, `Visible=false` + `Dispose()` del `NotifyIcon` para no dejar ícono
  fantasma, y `Application.Current.Shutdown()`).
- **Ícono:** `System.Drawing.Icon` desde el `app.ico` ya embebido como recurso.
- **Primera vez que se minimiza:** un balloon tip / hint una sola vez, para que el usuario
  aprenda que la app sigue en la bandeja y no crea que la X la cerró.

### Reconexión del mando

- Hook `WM_DEVICECHANGE` instalado con `HwndSource.AddHook` en `SourceInitialized`
  (`WindowInteropHelper.Handle`). Sobrevive minimizado (el HWND sigue vivo mientras la ventana
  esté **oculta**, no destruida).
- Se usa la notificación **coarse `DBT_DEVNODES_CHANGED` (0x0007)**, que llega sin
  `RegisterDeviceNotification`. Los mensajes finos `DBT_DEVICEARRIVAL/REMOVECOMPLETE` requieren
  registro y su payload es la ruta HID hija —no el `InstanceId` USB padre que `Apply` necesita—,
  así que no ahorran la re-enumeración de todas formas.
- En cada evento (con **debounce** ~300–500 ms) se corre una enumeración **barata** de solo-presentes
  `VID_054C` por CfgMgr (`CM_Get_Device_ID_List` con `CM_GETIDLIST_FILTER_PRESENT|ENUMERATOR`),
  **no** el escaneo de PowerShell (`RefreshDevicesList`), y se reaplica la intención al destino
  resuelto.
- **Reset auto-inflingido:** el flujo propio de replug/overclock termina en un PnP-restart cuyo
  *settle* final puede caer después de la ráfaga de eventos de dispositivo. Por eso se reaplica
  la intención **explícitamente** al final exitoso de `ReplugDevice`/`RestartDevice`/`ApplyRate`
  (en sus llamadores dentro de `MainWindow`, sin tocar `SystemManager`), no solo por el evento.
- Se **pausa la reafirmación** mientras dura un RECONECTAR del propio DualSense, para evitar que
  un tick abra un handle transitorio en la ventana del `CM_Query_And_Remove_SubTree`.

### Una sola instancia

Mutex con nombre al arrancar. Si ya hay una instancia (probablemente en la bandeja, invisible en
la barra de tareas), la segunda **trae al frente** la ventana existente y se cierra, en vez de
lanzar un segundo conductor que pelee por el mando y ponga un segundo ícono.

### Reaplicar al arrancar (timing correcto)

`Window_Loaded` llama `RefreshDevicesList`, que escanea en un `Task.Run` de fondo y **puebla
`_allDevices` más tarde**, dentro de un `Dispatcher.Invoke`. Así que `_allDevices` está **vacío**
justo cuando `Window_Loaded` retorna. La reaplicación al arrancar **debe** engancharse al callback
de fin-de-escaneo (donde ya hay dispositivos), no ir justo tras la llamada a `RefreshDevicesList`.

### Los controles pasan a ser setters de la intención

Cada punto que hoy escribe al mando pasa a **fijar `_activeIntent`** (y el conductor asserta):
`ApplyLightNow`, `Preset_Click`, `RestoreLight_Click`, los combos Player/Brightness
(`LightCombo_Changed`), `Rainbow_Toggled` (on), `RainbowStyle_Changed`, `RainbowSpeed_Changed`,
y `ApplyProfile_Click`. Enumerar **todos** los llamadores actuales de `Apply` es crítico: un
sitio que se escape seguiría escribiendo fuera de horario y reintroduciría un segundo escritor.

`ApplyProfile_Click` con un perfil rainbow hoy maneja los combos y llama `Rainbow_Toggled`;
aplicar un perfil desde la bandeja (pestaña nunca abierta) daría el mismo NRE de combos nulos.
Por eso el perfil **entra por la intención** (set-then-reassert), no leyendo combos en vivo.

`PlayStationList_SelectionChanged` (hoy no escribe, solo `UpdateSwatch`) pasa a **retargetear**
la resolución de la intención al cambiar la selección, sin escribir al mando recién seleccionado
hasta que el usuario lo pida (se mantiene la regla actual de "seleccionar no escribe").

### Persistencia — cuándo se escribe

`_activeIntent` se actualiza **sincrónicamente** en cada setter (fuente de verdad en memoria) y
se agenda un guardado a disco **con debounce** (un `DispatcherTimer` dedicado de ~750 ms, **no**
el debounce de luz de 50 ms y **nunca** el tick del rainbow). Así el disco no se martillea 64
veces/s por el rainbow.

## Texto anticheat — corregido

El copy actual está al revés: el único aviso de juego online (en la página de luz) culpa al
envío de color ("un proceso sin firmar escribiendo al mando") y **no hay aviso** donde se elige
el riesgo real (el selector de modo del driver).

La verdad, separada limpiamente:
- **Riesgo = el modo de parcheo del driver.** Con un build de parcheo y `PatchUSBXHCI > 0`,
  hidusbf reescribe `USBXHCI.SYS`/`USBPORT.SYS` en memoria del kernel. Ese estado vive en el
  `.sys` instalado + dos DWORDs del registro y está activo **corra o no la GUI**. Para online:
  **NOPATCH**.
- **Benigno = el lazo de color y que la app esté abierta.** `Apply` abre el mando en modo
  compartido (`FILE_SHARE_READ|WRITE`) y escribe un report `0x02` estándar tocando solo los
  bytes de LED — exactamente lo que hacen Steam Input y DSX cada frame.

Cambios de copy (parte de esta feature, no después — o la app se contradiría):
1. Mover el aviso anticheat al selector de modo del driver, redactado sobre el parche del kernel:
   los modos de parcheo reescriben el kernel; para online usa NOPATCH; **el riesgo es el modo,
   no que UltraPolling esté abierto**.
2. Reescribir el aviso de la página de luz para que **deje de llamar peligroso al envío de
   color**: el color es HID normal, igual que Steam/DSX, no añade riesgo de baneo; la app puede
   seguir en la bandeja durante la partida; lo único que importa para online es el modo del
   driver. **Quitar** la línea "cierra la app antes de jugar".
3. Arreglar dos frases que la bandeja vuelve mentira: "Rainbow activo. Se detiene al cerrar la
   app." y "Al cerrar la app el mando se queda en el último color." → describir el minimizar-a-bandeja.
4. Añadir sección "Juego online y anticheat" al `README_ULTRAPOLLING.md` con la misma separación
   limpia, sin prometer que parchear es seguro para ranked ni inventar miedo al envío de color.
5. Documentar el ajuste de Steam Input (apagar el control de luz del mando) como acompañante,
   descrito **por nombre e intención**, no por ruta exacta de clics (Steam mueve esos ajustes).

## Manejo de errores y bordes (todos del crítico)

| Caso | Comportamiento |
|---|---|
| Combos nulos hasta abrir la pestaña | El conductor arma `LightState` desde la intención (datos), nunca desde combos. Sin NRE. |
| `_allDevices` vacío al arrancar | Reaplicar en el callback de fin-de-escaneo, no tras lanzar el escaneo. |
| Perfil aplicado desde la bandeja | Entra por la intención, no por leer combos en vivo. |
| Destino ausente | No-op silencioso, sin log. |
| Error 5 (exclusiva) | Surface una vez → apaga el control de luz de Steam. No spamea. |
| Mando por Bluetooth | La UI dice que no se puede controlar por BT (v1 USB). No finge éxito. |
| RECONECTAR del propio mando | Pausar la reafirmación mientras dura, y con el medidor detenido. |
| Segunda instancia | El mutex trae al frente la existente y sale. |
| "Salir de verdad" a mitad de rainbow | El mando queda en el último color escrito (hue arbitrario); coincide con que no hay memoria en el mando. Aceptado. |
| DualShock 4 presente (VID_054C, no DualSense) | La resolución de destino apunta a un solo mando; un report de DualSense a un DS4 sería un fallo inofensivo, pero la regla de un-solo-destino lo evita en la práctica. |

## Pruebas

**Lógica pura (unit tests, enlazados por ruta como los demás):**
- `LightIntent`/`IntentStore`: ida y vuelta de ambos `Kind`; enums persistidos como nombre;
  archivo ausente/corrupto → `null`; backup creado al sobrescribir; helpers `ToLightState`/`From*`.
- Resolución de destino: seleccionado-presente; seleccionado-ausente con uno presente; varios
  presentes sin selección; ninguno presente → `null`.

**Verificación manual (WPF/bandeja/hardware):**
- Minimizar a bandeja mantiene el color/rainbow; "Mostrar" y "Salir de verdad" funcionan; sin
  ícono fantasma; Alt+F4 minimiza.
- Reconexión física reaplica el color; el propio replug/overclock reaplica.
- Segunda instancia trae al frente la primera.
- **Prueba de exclusividad (make-or-break):** con Steam Input activo en un juego, ¿puede
  UltraPolling reescribir el color (compartido, parpadea) o falla con error 5 (exclusiva)? El
  resultado decide qué tan prominente es la instrucción de apagar el control de luz de Steam.
- Sanity del backend: `VerifyState` sigue `NoPatch` por hash (esta feature no toca el driver).

## Archivos

**Nuevos:**
- `HidusbfModernGui/LightIntent.cs` — intención + `IntentStore` + resolución de destino (puro).
- `HidusbfModernGui.Tests/LightIntentTests.cs` — tests de lo anterior.
- Un archivo/partial de bandeja pequeño y aislado (con alias WinForms).

**Modificados:**
- `HidusbfModernGui/HidusbfModernGui.csproj` — `<UseWindowsForms>true</UseWindowsForms>`.
- `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj` — enlazar `LightIntent.cs`.
- `HidusbfModernGui/App.xaml` — `ShutdownMode="OnExplicitShutdown"`.
- `HidusbfModernGui/MainWindow.xaml.cs` — conductor unificado; setters de intención; hook
  `WM_DEVICECHANGE` + debounce + reaplicar; reaplicar tras replug/overclock; hide-to-tray +
  `OnClosing`; menú de bandeja; mutex de una-instancia; reaplicar al arrancar en el callback
  de fin-de-escaneo; guardado con debounce.
- `HidusbfModernGui/HidDeviceLocator.cs` — enumeración barata de solo-presentes `VID_054C`, y
  opcionalmente `IsPresent(instanceId)` para sondear liveness sin caminar todas las rutas HID.
- `HidusbfModernGui/MainWindow.xaml` — copy anticheat (mover al selector de modo, reescribir el
  aviso de luz, arreglar las dos frases stale), hint del ajuste de Steam.
- `README_ULTRAPOLLING.md` — sección "Juego online y anticheat" + ajuste de Steam.

**Congelados, NO se tocan** (se confirma que no hace falta):
- `HidusbfModernGui/DualSenseLight.cs` — `Apply` ya es exactamente la primitiva de reafirmación.
- `HidusbfModernGui/LightProfile.cs` — la intención vive en su propio archivo/JSON, aparte.
- `HidusbfModernGui/PollingCore.cs`, `HidusbfModernGui/ColourMath.cs`,
  `HidusbfModernGui/ColourRamp.cs`, `HidusbfModernGui/RainbowWalker.cs`,
  `HidusbfModernGui/ColourPicker.xaml*`.
- `HidusbfModernGui/SystemManager.cs` — la reconexión no reusa su escaneo ni su replug; solo se
  reaplica en sus **llamadores** dentro de `MainWindow`.

## Restricciones globales (heredadas del proyecto)

- Paleta: exactamente diez colores; el color no decora fuera del picker/presets/swatch.
- Archivos de lógica pura sin WPF (el proyecto de tests los enlaza por ruta).
- Objetivo `net9.0-windows`. Build self-contained single-file portable; cualquier dependencia
  nueva debe evaluarse contra ese publish (aquí: ninguna, `UseWindowsForms` no añade NuGet).
- UI en español, mayúsculas sin acento para etiquetas de campo.
- Identidad git no configurada globalmente; commits con
  `git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com"`, terminando en
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.
