# UltraPolling — Rediseño monocromo

**Fecha:** 2026-07-15
**Estado:** Aprobado, pendiente de plan de implementación

## Contexto

UltraPolling es un panel de control sobre el driver `hidusbf` de SweetLow. Su
backend (`PollingCore`, `SystemManager`) fue reescrito el 2026-07-15 y hoy
reporta el estado real del sistema, con 71 tests cubriendo la lógica pura.

La interfaz, en cambio, salió de una plantilla de dashboard administrativo. Dos
comentarios en el XAML lo delatan: `<!-- Bottom coverage card (Plan for 2020
layout) -->` y `<!-- Smooth stability line graph (Simulated spline path) -->`.
De ahí vienen la gráfica falsa, el "coverage card" y la paleta índigo sobre azul
marino.

El resultado se ve bien pero se siente como un panel de contabilidad, no como
una herramienta de overclocking de USB.

## Problema

1. `#0F172A` no es negro, es azul marino. Con el acento índigo `#6366F1`, toda
   la app flota en azul.
2. El layout de dashboard reserva espacio para una gráfica que no tiene datos.
3. El color se usa como decoración, no como información.

## Objetivos

- Identidad de instrumento de medición, no de dashboard.
- Negro real como base, monocromo, con color exclusivamente para estado.
- Que cada píxel con color signifique algo verificable.

## No-objetivos

- **`SystemManager.cs` no cambia en absoluto.** Ni el registro, ni el servicio,
  ni el escaneo, ni la identificación del driver por hash.
- **`PollingCore.cs` solo recibe una función pura nueva** (`DeviceStatusLevel`).
  Nada de lo existente se modifica, y sus 71 tests deben seguir verdes sin
  tocarlos.
- No se implementa medición real del polling rate. Es un proyecto aparte; hasta
  entonces no hay gráfica.
- No se implementa búsqueda. Con 7-8 dispositivos no hace falta.

## Principio rector

**El color nunca decora. Si algo tiene color, significa algo.**

Un dashboard pinta de índigo porque queda bonito. Un instrumento pinta de ámbar
porque algo está capado. Todo lo demás es negro, gris y blanco.

Corolario operativo: antes de asignar un color a un elemento, hay que poder
responder "¿qué hecho del sistema comunica?". Si no hay respuesta, va en gris.

## Paleta

| Token | Valor | Uso |
|---|---|---|
| `Bg` | `#000000` | Fondo de ventana |
| `Surface` | `#0A0A0A` | Paneles |
| `SurfaceAlt` | `#111111` | Fila alternada / hover |
| `Border` | `#1F1F1F` | Separadores de 1px |
| `TextData` | `#FFFFFF` | El dato (números) |
| `TextLabel` | `#8A8A8A` | Etiquetas |
| `TextMuted` | `#4A4A4A` | Inactivo / deshabilitado |
| `StatusOk` | `#00C853` | Overclock vivo / filtro ON |
| `StatusWarn` | `#FFAB00` | Funciona pero capado a 1000 Hz |
| `StatusError` | `#FF3D00` | Driver caído / velocidad desconocida |

Cero índigo. Cero azul. El tema claro se elimina (ver Decisiones).

## Tipografía

Los números son el producto. Van en monoespaciada tabular; el texto de interfaz
en sans.

- **Datos** (tasas, `bInterval`, latencia, InstanceId):
  `FontFamily="Cascadia Mono, Consolas, Courier New"`
  El fallback importa: Consolas está garantizada en Windows, Cascadia Mono no.
- **Interfaz** (etiquetas, botones, títulos): `Segoe UI Variable, Segoe UI`

Motivo: con mono tabular, `1000 Hz` → `500 Hz` no desplaza el layout, y la lista
se lee como instrumento en vez de como hoja de cálculo.

## Estructura — master-detail

```
┌──┬───────────────────────────────────────────────────┐
│  │ DRIVER  No Patch ●        SERVICIO  Running       │
│▪ ├──────────────────────┬────────────────────────────┤
│  │ DISPOSITIVOS      7  │ DualSense Wireless         │
│▫ │                      │                            │
│  │ ▸ DualSense          │      1000 Hz               │
│▫ │   1000 Hz         ●  │      1.0 ms                │
│  │                      │                            │
│  │   Mouse              │ VELOCIDAD    High Speed    │
│  │   Default            │ bINTERVAL    4             │
│  │                      │ FILTRO       ON        ●   │
│  │   Teclado            │ MODO         No Patch  ●   │
│  │   Default            │ INSTANCE ID  USB\VID_...   │
│  │                      │                            │
│  │                      │ [ FILTRO ]  [ REINICIAR ]  │
│  ├──────────────────────┴────────────────────────────┤
│  │ [19:27] Scan completed. 7 devices.    ADMIN ✓     │
└──┴───────────────────────────────────────────────────┘
```

El panel derecho ya estaba atado a `DevicesListBox.SelectedItem` — era un panel
de detalle disfrazado de gráfica. El rediseño lo vuelve honesto.

## Componentes

### Chrome de ventana
Barra de título custom (`WindowStyle=None`, `AllowsTransparency=True`), drag,
minimizar, cerrar. Se conserva el comportamiento; se restyliza a negro.

### Sidebar
Tres destinos: **Dispositivos**, **Sistema**, **Refrescar**. Minimizar y cerrar
abajo. Mismo patrón que hoy, repintado.

### Header de estado
Sustituye al `CardDarkBrush` + anillo. Dos hechos en texto, cada uno con su punto.

`DRIVER <DriverState.ModeText> ●`

| Condición | Punto |
|---|---|
| `Build == Missing` o servicio no instalado | `StatusError` |
| `Build == Unrecognised` o `EffectiveMode == null` | `StatusError` |
| `MemoryIntegrityEnabled && Build == Patching` | `StatusError` (no cargará) |
| `CanOverclockBeyond1k == true` | `StatusOk` |
| Resto (NoPatch / 1k: funciona pero capado) | `StatusWarn` |

`SERVICIO <DriverState.ServiceStatus>` — `StatusOk` si `Running`, `StatusError`
en cualquier otro caso.

Este es el único sitio donde se comunica que el driver está capado. La fila del
dispositivo no lo repite (ver Lógica testeable).

### Lista maestra
Fila = icono + nombre + `ChildrenSummary` + tasa (mono) + punto de estado.
El punto se resuelve con `DeviceStatusLevel` (ver Lógica testeable).

### Panel de detalle
- Nombre del dispositivo
- Tasa grande en mono (~48px) y latencia debajo
- Tabla de hechos: velocidad, `bInterval`, filtro, modo, InstanceId
- Selector de tasa (**se consolida aquí**: hoy está duplicado en un ComboBox por
  fila y en el menú contextual)
- Acciones: filtro, reiniciar

### Vista Sistema
Instalar/desinstalar servicio, selector de modo, filtros de lista
(`OnlyControllersCheck`, `OnlyFilteredCheck`), aviso de Memory Integrity.

### Barra de estado
Log de una línea + indicador de privilegio admin. Se conserva.

## Lógica testeable

El color del estado no se decide en el XAML. Se añade una función pura a
`PollingCore`:

```csharp
public enum StatusLevel { Idle, Ok, Warn, Error }

public static StatusLevel DeviceStatusLevel(bool filterActive, UsbSpeed speed, int? resolvedRate)
```

Reglas, evaluadas en este orden:
1. `speed == Unknown` → `Error`. No se puede calcular el intervalo con seguridad,
   así que los controles de tasa se bloquean.
2. `!filterActive` → `Idle`. Gris: el dispositivo existe pero no lo gestionamos.
3. `resolvedRate == null` → `Warn`. Filtro puesto pero sin tasa fijada: no hace
   nada.
4. En cualquier otro caso → `Ok`. El dispositivo está haciendo lo que se le pidió.

**El nivel del dispositivo no juzga si la tasa es "alta".** Bajar un mouse a
31 Hz es downclocking deliberado, un caso de uso documentado en el README de
SweetLow, y funciona perfectamente: es `Ok`, no una advertencia.

Que el driver esté capado a 1000 Hz es un hecho **del driver**, no del
dispositivo, y se comunica en el header con `DriverState.CanOverclockBeyond1k`.
Mezclar ambas cosas en el punto de la fila haría que el color mintiera, que es
justo lo que este rediseño existe para evitar.

Esto mantiene la promesa del principio rector verificable por tests, no por
inspección visual.

## Estados y bordes

| Situación | Comportamiento |
|---|---|
| Ningún dispositivo seleccionado | Panel de detalle en estado vacío explícito, no `--` en cada campo |
| `SpeedKnown == false` | Punto rojo, selector de tasa deshabilitado, motivo visible |
| `DriverState.Warning != null` | Se muestra en la barra de estado |
| Build NoPatch instalado | Header en ámbar: funciona, pero capado |
| Servicio detenido / no instalado | Header en rojo |

## Qué se elimina

| Elemento | Motivo |
|---|---|
| Gráfica Bezier (`Path` con datos fijos) | Es un dibujo, no un gráfico |
| Etiquetas "10s ago / 5s ago / Now" | Eje de una gráfica inexistente |
| Card inferior "Active Overclock" | Duplica el header |
| `ServiceProgressRing` / `OverclockProgressRing` | Precisión falsa (ver Decisiones) |
| `SearchIconPath` | Nunca tuvo caja de búsqueda |
| `searchText` en `ApplyFilters()` | Código muerto: siempre `""` |
| Tema claro + toggle + 9 recursos duplicados | Ver Decisiones |

## Decisiones

**Los anillos de dona se eliminan.** Fueron conectados el mismo día (antes eran
decoración hardcodeada). Expresar "cuál de 4 modos" como porcentaje es precisión
falsa: que 4kHz-8kHz sea "100%" es un número inventado. El modo se comunica con
texto y un punto de color.

**El tema claro se elimina.** Contradice el acuerdo previo de mantenerlo con
persistencia. Motivo: "negro total monocromo" no tiene versión clara coherente;
un instrumento blanco no es el mismo producto. Al eliminarlo desaparece también
la necesidad de `AppSettings` y `ThemeManager` que se habían propuesto. Decisión
asumida por el implementador ante un "procede" sin respuesta explícita; es
reversible si el usuario la revoca.

## Testing

- Los 71 tests de `PollingCore` deben seguir verdes: el backend no cambia.
- Se añaden tests para `DeviceStatusLevel` cubriendo las cinco reglas.
- El build de WPF debe compilar limpio.
- **Verificación visual: solo el usuario.** No hay forma automática de comprobar
  que esto se ve bien. Hay que ejecutar la app y mirarla.

## Riesgos

1. **Alto churn.** `MainWindow.xaml` son 75 KB y esto reescribe la mayoría. Hay
   riesgo de romper bindings silenciosamente (WPF falla en runtime, no en build).
   Mitigación: la ventana de Output de WPF lista los errores de binding; hay que
   revisarla al ejecutar.
2. **Fuente mono no garantizada.** Cascadia Mono no está en todas las
   instalaciones. Mitigación: cadena de fallback a Consolas.
3. **Consolidar el selector de tasa** cambia el flujo que el usuario conoce
   (hoy hay uno por fila). Es intencional, pero es un cambio de comportamiento.

## Fuera de alcance (futuro)

- Medición real del polling rate. Es lo que convertiría el hueco de la gráfica
  en el argumento de venta del producto.
- Búsqueda / filtrado avanzado.
- Perfiles por dispositivo.
