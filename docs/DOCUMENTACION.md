# UltraPolling — Documentación técnica

Este documento explica cómo está construida la app por dentro: los módulos, los flujos de datos, las decisiones de diseño y las lecciones que dejaron los bugs reales. El [README](../README.md) cubre el uso; esto cubre el **cómo y el porqué**.

Última actualización: 2026-07-18 (tras el editor de curvas v2: RESPUESTA en Lineal+Editor, puntos de colores, biblioteca MIS CURVAS y botón "¿CÓMO FUNCIONA?").

---

## 1. Visión general: dos mundos, un solo exe

UltraPolling junta **dos sistemas independientes** que comparten interfaz pero no se tocan entre sí:

| Mundo | Drivers | Qué hace | Estado |
|---|---|---|---|
| **Polling + Luces** | `hidusbf` (SweetLow) | Overclock de la tasa de sondeo USB, medidor de tasa real, control de la barra de luz/LEDs del DualSense | Terminado, publicado |
| **Mando virtual (remapeador)** | `ViGEmBus` + `HidHide` (Nefarius, se instalan aparte) | Lee el DualSense físico, transforma la entrada (curvas, deadzones, remapeo, gatillos, touchpad) y la emite por un DS4 virtual, con el físico oculto | Motor completo; editor de curvas v1 |

Regla de oro del diseño: **si el mando virtual está apagado (default), la app no toca nada** — el juego ve el DualSense nativo tal cual.

## 2. Mapa de módulos

Todo el código propio vive en `HidusbfModernGui/` (WPF, .NET 9, x64). Núcleo puro = sin WPF, sin hardware, testeable.

### Núcleo puro (testeado con xUnit)

| Archivo | Responsabilidad |
|---|---|
| `PollingCore.cs` | Aritmética del polling: slots↔Hz según modo del driver y velocidad del bus, latencias, niveles de estado. |
| `ColourMath.cs` / `ColourRamp.cs` / `RainbowWalker.cs` | Color en OKLab, rampas y el arcoíris por pasos (velocidad en colores/s con avance fraccional). |
| `PlayerLedWalker.cs` | Animaciones de los LEDs de jugador (Carga, Estrellas, Respiración). |
| `LightIntent.cs` / `LightProfile.cs` | La "intención" de luz del usuario (qué color/efecto/velocidades quiere) y los perfiles con nombre. |
| `ControllerState.cs` | Estado normalizado del mando: `StickInput`, `ControllerState`, enums `ResponseCurve`/`PadButton`/`TouchZone`, `CurvePoint`. |
| `InputTransform.cs` | Toda la matemática del remapeo: deadzone radial, curvas (exponente, sigmoide, escalón, **PCHIP** para la curva del Editor), hair-trigger, remapeo de botones, zonas del touchpad. |
| `RemapSettings.cs` | Los valores AMIGABLES que ve el usuario (%) y su conversión a parámetros del motor (getters derivados). Incluye los 5 puntos de la curva Editor por stick. |
| `RemapEngine.cs` | `Transform(estado, settings)` — la función pura que enchufa todo lo anterior: entra el estado físico, sale el estado que se empuja al virtual. |
| `RemapProfileStore.cs` / `ProfileStore` / `IntentStore` | Persistencia JSON en `%APPDATA%\UltraPolling` (escritura atómica + `.backup`). |
| `OpResult.cs` | Resultado uniforme `(Success, Error)` de toda operación que puede fallar. |

### Capa de E/S (verificada a mano, con hardware)

| Archivo | Responsabilidad |
|---|---|
| `DualSenseReader.cs` | Lee el DualSense **físico** por USB (HidSharp, hilo propio). Filtra por `VID_054C&PID_0CE6` — **nunca** puede abrir el virtual. Parsea el reporte 0x01 (64 bytes) a `ControllerState`. Reintenta ~4 s al abrir (el devnode tarda tras un reinicio PnP) y se reconecta solo si el stream cae. |
| `VirtualPad.cs` | El DS4 **virtual** vía ViGEm (`VID_054C&PID_05C4`). `Push(state)` mapea un `ControllerState` ya transformado y envía un reporte por frame (`AutoSubmitReport=false`). |
| `HidHideControl.cs` | Envuelve HidHide: whitelistea nuestro exe, resuelve el instance id del **físico** en tiempo real (nunca confía en un id cacheado), bloquea el nodo HID + su padre USB `MI_03`, y reinicia el devnode para expulsar handles ya abiertos. `Revert()` deshace exactamente lo que se añadió. `IsHiding` lee el estado REAL del driver, no una bandera. |
| `DriverCheck.cs` | ¿Están ViGEmBus y HidHide instalados? Pregunta al SCM, sin efectos secundarios. |
| `DualSenseLight.cs` | Escribe el output report 0x02 (color/LEDs/brillo) al físico. `IsPlayStation` excluye `PID_05C4` (el virtual jamás es objetivo de luz). |
| `HidDeviceLocator.cs` | De un instance id (USB compuesto **o** el propio nodo HID) a las rutas de interfaz HID (cfgmgr32). |
| `PollingMeter.cs` | Mide la llegada real de reportes HID del dispositivo seleccionado (mediana/min/max de los huecos). |
| `MainWindow.xaml(.cs)` | Toda la UI (una sola ventana WPF): dashboard, sistema/driver, hub del mando (Configurar + Luces). |

### Persistencia (todo en `%APPDATA%\UltraPolling`)

| Archivo | Contenido |
|---|---|
| `intents.json` | La última luz que pidió el usuario (se reaplica al abrir y al reconectar el mando). |
| `profiles.json` | Perfiles de luz (+ tasa opcional). |
| `remap-profiles.json` | Perfiles del remapeador (`RemapSettings` completo) + el pseudo-perfil `__ultimo_usado__`. |
| `curves.json` | Biblioteca de curvas del Editor con nombre (MIS CURVAS). |

## 3. Flujos clave

### 3.1 Overclock de polling (APLICAR CAMBIOS)

Un botón hace todo: activa el filtro hidusbf del dispositivo → escribe la tasa en el registro → **replug por software** (quitar del árbol PnP + re-enumerar). El replug importa: un reinicio PnP a secas no basta porque el dispositivo nunca abandona el bus y sus descriptores — donde vive la tasa — no se releen. El medidor luego responde con números si el cambio aplicó de verdad.

### 3.2 Luces

La UI edita una `LightIntent` (color/LED/efecto/velocidades) → se aplica al instante por HID output report 0x02 → se persiste con debounce. Al abrir la app o reconectar el mando, la intención se reaplica. Los efectos (arcoíris OKLab, Carga/Estrellas/Respiración) corren en un motor unificado con timers en el hilo de UI.

**Con el mando virtual activo**, el escaneo de dispositivos (PowerShell, proceso externo) no ve el físico oculto — así que la página de luces lo resuelve **en-proceso** (nuestro exe está whitelisteado) y lo inyecta en la lista como "DualSense (oculto por HidHide)". Ver lección L3.

### 3.3 Motor del mando virtual (interruptor maestro)

```
DualSenseReader (físico, ~hasta 8 kHz)
        │  Snapshot()
        ▼
RemapEngine.Transform(estado, _remap)     ← _remap: el MISMO objeto que edita la UI
        │                                    (cambio de slider = efecto inmediato)
        ▼
VirtualPad.Push()  →  DS4 virtual (lo que ve el juego)
```

**Orden de arranque (importa muchísimo):**
1. **Virtual primero** — el juego nunca ve cero mandos.
2. **Ocultar el físico** (HidHide + reinicio del devnode) — ANTES de abrir nuestro lector, para que el reinicio no expulse nuestro propio handle (lección L1).
3. **Recién entonces abrir el lector** — contra el devnode re-enumerado; nosotros sí podemos porque estamos whitelisteados.

**Orden de parada (inverso, por seguridad):** mostrar el físico → parar el lector → desconectar el virtual. Nunca hay una ventana sin ningún mando, y la app **jamás cierra con el físico oculto** (guard en `OnClosing` + guard de arranque en `Window_Loaded` que des-oculta cualquier DualSense abandonado por un crash).

Todo el trabajo pesado (ViGEm connect, HidHide, reinicios PnP) corre en hilo de fondo (`Task.Run`); la bandera `_engineBusy` evita que nuestro propio `WM_DEVICECHANGE` dispare el rescan de PowerShell contra el hilo de UI.

**Deuda conocida:** el push al virtual va por `DispatcherTimer` de 8 ms en el hilo de UI (~125 Hz efectivos, con jitter si la UI está ocupada), aunque el físico entregue 8 kHz. Si la respuesta en juego se siente rara tras afinar curvas, la siguiente palanca es mover el push a un hilo propio.

### 3.4 Editor de curvas

- Los **5 puntos** por stick viven en `RemapSettings.LeftCurvePoints`/`RightCurvePoints`. Extremos (0,0)/(1,1) fijos; índices 1..3 arrastrables sobre el canvas CURVA.
- **Dos dominios, una conversión:** `CurvePoint.X` vive en el dominio **post-deadzone** (0..1 entre inner y outer); el eje X del canvas es la entrada **cruda** del stick. `DomainToRaw`/`RawToDomain` convierten en la frontera de la UI, así los puntos caen exactamente sobre la línea dibujada con cualquier zona muerta/alcance (lección L4).
- **Interpolación PCHIP** (Fritsch–Carlson, `InputTransform.ShapeCustom`): pasa exactamente por cada punto, suave, y **sin sobreimpulso** — entre dos puntos la salida nunca se sale del rango de esos puntos. Crítico para que la mira no haga nada que el usuario no dibujó.
- El orden completo de un stick en el motor: deadzone radial (por magnitud, preserva el ángulo) → reescala [inner,outer]→[0,1] → curva (preset o puntos) → dirección unitaria × magnitud.

## 4. Tests

- `HidusbfModernGui.Tests/` (xUnit, `net9.0` **sin** WPF). **Linkea los archivos fuente individualmente** en el csproj — todo archivo nuevo del núcleo hay que añadirlo ahí o los tests no lo ven.
- Por eso mismo, nada que toque Nefarius/HidSharp/WPF puede linkearse: la capa de E/S se verifica a mano con hardware.
- Correr: `dotnet test HidusbfModernGui.Tests\HidusbfModernGui.Tests.csproj` (318 tests al escribir esto).

## 5. Compilar y empaquetar

```powershell
dotnet build HidusbfModernGui\HidusbfModernGui.csproj -c Debug   # exe de desarrollo
.\package.ps1                                                     # portable en dist\UltraPolling
```

El paquete es un exe self-contained de un solo archivo + la carpeta `DRIVER` (verificada byte a byte contra el original de SweetLow). Ojo del single-file: `Assembly.Location` devuelve cadena vacía ahí — la ruta del exe se obtiene con `Environment.ProcessPath` (lección L5).

## 6. Lecciones aprendidas (bugs reales con causa raíz)

- **L1 — El reinicio del devnode expulsa TODOS los handles, incluido el tuyo.** HidHide solo niega aperturas NUEVAS; para expulsar a los que ya tenían el mando abierto se reinicia el devnode (`RemoveAndSetup`). Primera versión: lector abierto → ocultar → el reinicio mataba nuestro propio lector (reportes congelados). Arreglo: ocultar ANTES de abrir el lector + reintentos al abrir + reconexión automática.
- **L2 — El virtual comparte VID con el físico: filtra SIEMPRE por PID.** El DS4 virtual es `VID_054C` y también expone un reporte de 64 bytes. Buscar "un mando Sony de 64 bytes" enganchó al lector con **nuestro propio virtual** en un lazo cerrado (con offsets desfasados: el byte del analog L2 del DS4 leído como cruceta = DpadUp fantasma clavado). El mismo error, en espejo, hacía que la página de luces le escribiera el color al virtual. Arreglo: el lector exige `PID_0CE6`; las luces excluyen `PID_05C4`.
- **L3 — La whitelist de HidHide es POR PROCESO.** Nuestro exe ve el físico oculto; el escaneo por PowerShell (proceso hijo distinto) NO. Cualquier función que dependa de la lista escaneada muere con el motor activo. Arreglo: resolver el físico en-proceso e inyectarlo en la lista.
- **L4 — Dos dominios de coordenadas = conversión explícita en la frontera.** Los puntos de la curva viven post-deadzone; el canvas dibuja entrada cruda. Colocar puntos sin convertir los despegaba de la curva en cuanto la zona muerta dejaba de ser 0. Arreglo: `DomainToRaw`/`RawToDomain` leyendo inner/outer en vivo.
- **L5 — Single-file ≠ app normal.** `Assembly.Location` = "" en el publish de un solo archivo; como esa ruta era la whitelist de HidHide, el portable se habría ocultado el mando a sí mismo. `Environment.ProcessPath` siempre.
- **L6 — El trabajo pesado de dispositivos NUNCA en el hilo de UI.** Reinicios PnP, HidHide, `Thread.Join` del lector y los ~1 s del escaneo PowerShell congelaban la ventana. Todo va por `Task.Run`, con banderas (`_engineBusy`, `_overclockBusy`) para no auto-dispararse rescans.

## 7. Seguridad y límites deliberados

- **Sin macros, sin teclado/ratón, sin auto-aim, sin evasión.** El remapeador solo reconfigura el mando (curvas, deadzones, botones, zonas). Es política del proyecto, no una limitación técnica.
- El aviso de anticheat va **en la UI y en el README**, sin adornos: ViGEm+HidHide son detectables por anticheats de kernel aunque no hagas trampa; pensado para un jugador o juegos sin anticheat.
- El interruptor maestro **no persiste**: la app siempre arranca en mando nativo (el estado seguro).
- La degradación de datos viejos (perfiles con curvas retiradas, JSON editado a mano) ocurre **al cargar, en memoria** — nunca se reescribe el archivo del usuario a sus espaldas.

## 8. Historia y planes

Los specs y planes de implementación viven en `docs/superpowers/`:

- `plans/2026-07-18-controller-remapper.md` — el motor completo (Fases 1–3, ejecutado).
- `plans/2026-07-18-stick-curves.md` — las curvas Dinámica/Digital (ejecutado; esas curvas se retiran en v2).
- `plans/2026-07-18-editor-de-curvas.md` — editor de curvas v1 + arreglo de luces con HidHide (ejecutado).
- `plans/2026-07-18-editor-curvas-v2.md` — editor v2 (ejecutado): RESPUESTA queda en Lineal+Editor con degradación honesta de perfiles viejos, puntos de colores con significado (verde=zona baja, ámbar=media, rojo=alta), biblioteca "MIS CURVAS" con nombre (`CurveLibraryStore`), y botón "¿CÓMO FUNCIONA?" con la documentación para el usuario.
