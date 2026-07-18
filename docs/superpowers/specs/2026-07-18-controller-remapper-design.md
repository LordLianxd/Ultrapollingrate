# Remapeador de mando (DS4/DualSense) — Diseño

**Fecha:** 2026-07-18
**Estado:** aprobado el diseño; pendiente escribir el plan de implementación
**Rama:** main (repo LordLianxd/Ultrapollingrate)

## Objetivo

Añadir a UltraPolling una pestaña de **personalización del mando** que lea el
DualSense/DS4 físico, transforme su entrada (curvas y zonas muertas de los sticks,
remapeo de botones, mapeo del touchpad por zonas, recorrido/umbral de los gatillos)
y la entregue a un **mando virtual DS4 (ViGEmBus)** que el juego ve en lugar del
físico. El físico se oculta con **HidHide** para que no haya doble input.

**Es una herramienta de configuración legítima** (misma categoría que DS4Windows,
reWASD, Steam Input o un DualSense Edge). **No** hay macros, **no** hay emulación
de teclado/ratón, **no** hay auto-aim, anti-retroceso ni nada que reaccione al juego.

## El principio rector: interfaz simple sobre datos complejos

Los valores reales del DS4 son técnicos y estresantes para el usuario (zonas muertas
en 0.0–1.0, puntos de control Bézier, bytes de gatillo 0–255). **El usuario nunca ve
esos números.** La UI expone controles con **nombre claro, rango en % y una vista
previa visual** que enseña qué hace cada cosa; lo raro se defaultea y se esconde tras
un "Avanzado". El núcleo interno sí trabaja en unidades precisas; una capa de
traducción convierte los controles amigables a esos parámetros.

Traducción concreta (UI amigable → parámetro interno):

| Control en la UI (lo que ve el usuario) | Parámetro interno |
|---|---|
| Sticks · **Zona muerta** (slider 0–30%, con anillo visual) | inner deadzone 0.0–0.30 |
| Sticks · **Respuesta**: Precisa / Normal / Rápida (3 botones, con gráfica de curva en vivo) | curva Bézier (punto de control preajustado por opción) |
| Sticks · *(Avanzado)* **Alcance** (slider 70–100%) | outer deadzone; por defecto 100%, oculto |
| Gatillos · **Punto de disparo** (slider 0–100%, con barra visual) | umbral de gatillo (byte) |
| Botones · diagrama del mando; tocas un botón → eliges su nueva función | tabla de remapeo botón→botón |
| Touchpad · rejilla de 4 zonas; asignas un botón a cada zona | límites de píxeles X/Y + tabla zona→botón |

Además: **perfiles con nombre** (guardar/cargar) y un "Por defecto". Vista previa en
vivo del estado del mando (sticks/gatillos/botones) para que el usuario vea el efecto
de sus ajustes sin abrir un juego.

## Arquitectura (en capas)

**1. Núcleo de transformación — lógica pura, sin WPF ni drivers (TDD).**
Toma un "estado de mando normalizado" (sticks como float −1..1, gatillos 0..1,
botones bool, touchpad x/y) y devuelve el estado transformado, aplicando en orden:
- `ApplyStick(x, y, innerDz, outerDz, curve)` → zona muerta radial + curva de respuesta.
- `ApplyTrigger(value, threshold)` → recorrido/umbral (hair trigger).
- `ApplyRemap(buttons, remapTable)` → remapeo.
- `ResolveTouchpad(x, y, touched, splits, zoneTable)` → zona → botón virtual.
Más una capa pura de **preajustes → parámetros** (p.ej. Respuesta "Precisa" → punto
Bézier concreto; "Zona muerta 15%" → 0.15). Todo esto es testeable sin hardware.

**2. Capa de E/S — toca hardware (verificación manual).**
- **Lector del físico:** abre el DualSense por HID, lee input reports en un hilo de
  fondo, parsea a estado normalizado (USB en v1; Bluetooth fuera, como en la luz).
- **Salida ViGEm:** `Nefarius.ViGEm.Client`, un **DS4 virtual**; empuja el estado
  transformado cada frame en su propio hilo/loop (sin pasar por la UI, para no meter lag).
- **HidHide:** oculta el DS físico del resto del sistema y pone a UltraPolling en su
  lista blanca (así seguimos controlando luz/LEDs y leyendo el físico).
- **Detección de drivers:** al abrir la pestaña, comprobar ViGEmBus + HidHide; si
  faltan, mostrar guía de instalación (no crashear). En la máquina del usuario ya
  están (ViGEmBus 1.22.0, HidHide 1.5.230).

**3. UI — pestaña nueva "MANDO VIRTUAL" en el sidebar.**
- Interruptor Activar/Desactivar (arranca el DS4 virtual + oculta el físico; al apagar,
  muestra el físico y quita el virtual — estado limpio).
- Secciones simples: Sticks (zona muerta + respuesta), Gatillos (punto de disparo),
  Botones (diagrama), Touchpad (4 zonas), Perfiles. Con "Avanzado" plegable para lo raro.

## Modelo de datos y persistencia

Un `RemapProfile` puro (sin WPF): parámetros **internos precisos** (no los valores de
UI), serializado a JSON en `%APPDATA%\UltraPolling\remap-profiles.json`, con el mismo
patrón atómico + `.backup` que `ProfileStore`/`IntentStore`. Se guarda el perfil
activo y los perfiles con nombre. La UI reconstruye sus controles amigables desde los
parámetros internos al cargar (la traducción es bidireccional y pura).

## Coexistencia con lo existente

- **Luz/LEDs y overclock intactos:** UltraPolling va en la lista blanca de HidHide, así
  que sigue escribiendo al físico (luz) y el overclock del físico no se toca. El mando
  físico a alta tasa → transformamos → virtual.
- El juego ve el **DS4 virtual**, no el físico: pierde la barra de luz nativa del juego
  (nosotros la seguimos controlando aparte) y los gatillos adaptativos.

## Flujo y rendimiento

Físico → lector (hilo de fondo) → núcleo transforma → ViGEm virtual → juego. El bucle
de input corre fuera del hilo de UI. La UI solo edita parámetros y muestra una vista
previa; nunca procesa input frame a frame.

## Manejo de errores

- Drivers faltantes → guía de instalación, sin crash.
- Mando desconectado con el virtual activo → parar limpio, avisar, reintentar al reconectar.
- Fallo al crear el virtual / al ocultar con HidHide → mensaje claro y volver a estado
  seguro (físico visible, sin virtual).

## Anti-cheat (copy honesto, obligatorio)

Aviso prominente en la pestaña y en el README: el **mecanismo** (mando virtual ViGEm +
HidHide) es **detectable** por anticheat de kernel (Ricochet, Vanguard, EAC, BattlEye),
**aunque no hagas trampa** — algunos banean ViGEm por su presencia. Ideal para un
jugador / juegos sin anticheat / accesibilidad; online es bajo tu riesgo por el
mecanismo, no por la intención. Sin garantías; los baneos van por oleadas. No hay
macros, KB/M, auto-aim ni anti-retroceso — es solo configuración del mando.

## Testing

- **Núcleo puro (TDD fuerte):** deadzone radial, curvas por preajuste, umbral de gatillo,
  remapeo, resolución de zonas del touchpad, y la traducción UI↔parámetros. Enlazado por
  ruta al proyecto de tests, como el resto de la lógica pura.
- **E/S y drivers:** verificación manual en hardware (leer físico, ver el virtual en el
  Test de mandos de Windows, confirmar que el físico queda oculto, que la luz sigue).

## Alcance

**v1 (lo que pediste):** ocultar físico + DS4 virtual + remapeo de botones + zona
muerta/curva de stick + punto de disparo de gatillos + **mapeo del touchpad por zonas**,
todo con la UI simplificada y perfiles guardados.

**v2 (después):** capas con botones modificadores (mantener un botón para un segundo
mapa), perfiles por juego (autocambio), y Bluetooth.

**Fuera para siempre:** macros, emulación de teclado/ratón, auto-aim, anti-retroceso,
evasión de anticheat.

## Restricciones globales (heredadas + nuevas)

- .NET 9, WPF, x64. Lógica pura sin WPF (tests la enlazan por ruta).
- Dependencias nuevas: `Nefarius.ViGEm.Client` (BSD-3) + una librería de lectura HID
  (p.ej. HidSharp) — ambas NuGets manejados, caben en el single-file. **No** copiar
  código de DS4Windows (GPLv3): reimplementar conceptos.
- Drivers externos (ViGEmBus, HidHide) los instala el usuario → **rompe el portable de
  un archivo**; la app detecta y guía. El README debe decirlo.
- Paleta de diez colores; UI en español, etiquetas claras.
- Congelados (no tocar): `DualSenseLight.cs`, `LightProfile.cs`, `SystemManager.cs`,
  `PollingCore.cs`, `ColourRamp.cs`, `ColourMath.cs`, `RainbowWalker.cs`,
  `PlayerLedWalker.cs`, `LightIntent.cs`, `Theme.xaml`.
- Commits sin `Co-Authored-By`. Push lo hace el usuario.
