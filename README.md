# UltraPolling

Interfaz moderna y gratuita para el driver [hidusbf](https://github.com/LordOfMice/hidusbf) de SweetLow: overclock de la tasa de sondeo (polling rate) USB en Windows, con medidor de tasa real integrado y control de la luz del DualSense.

<!-- capturas pendientes -->

## Qué hace

- **Overclock de polling rate USB.** Cambia la tasa de sondeo de ratones, teclados y mandos: desde 125 Hz hasta 8000 Hz, según el modo del driver y de lo que aguante el dispositivo. El techo real depende de la clase de velocidad USB (Low/Full/High Speed) y del firmware del dispositivo; ningún número está garantizado en un hardware concreto.
- **Medidor de tasa real.** La app no solo muestra la tasa que *pidió*: abre la interfaz HID del dispositivo seleccionado y mide la llegada de reportes en vivo, mostrando PEDIDA vs MEDIDA (mediana, min/max de los huecos). Es la respuesta con números a "¿se aplicó el overclock?". Limitación honesta: mide reportes HID, no el bus USB; un ratón quieto no genera datos y mide "sin datos", no 0 Hz mentiroso.

  La lista son tarjetas; la seleccionada se marca con borde blanco y un fondo algo
  más claro. Las que tienen una tasa puesta llevan una pastilla con la tasa
  **resuelta**.

  En el detalle, **MEDIDA** es lo que el dispositivo hace de verdad y **PEDIDA** lo
  que se le escribió: el punto entre las dos es la razón de ser de esta pantalla.
  La pastilla de al lado dice otra cosa distinta — cómo de **regular** llega el
  flujo (`REGULAR` / `IRREGULAR` / `SIN DATOS`).

  **ENTRE REPORTES** es el hueco mediano entre la llegada de un reporte y el
  siguiente. **No es latencia.** La latencia sería el retardo desde que mueves el
  stick hasta que el juego lo ve, y en esa cadena hay eslabones que esta app no
  puede ver: el firmware del mando, la pila HID de Windows y el motor del juego.
  El reporte del DualSense no trae marca de tiempo de origen, así que el instante
  del movimiento es desconocido y la resta es imposible. Subir la tasa acorta un
  eslabón de esa cadena — la espera al sondeo, de ~0,5 ms a ~0,06 ms al pasar de
  1000 a 8000 Hz —, no la cadena entera, que sigue midiéndose en decenas de ms.
- **Mando: Configurar + Luces (solo USB/cable).** El botón mando del sidebar abre un hub con dos partes:
  - **Luces del mando** — lo de siempre: color de la barra de luz con selector y hex, LED de jugador (las 5 luces bajo el touchpad) con brillo, un efecto arcoíris por pasos calculado en OKLab (velocidad ajustable hasta 360 colores/s), y animaciones de los LEDs de jugador (Carga, Estrellas, Respiración) con su propia barra de velocidad. La app recuerda tu último color/efecto y lo restaura al abrir y al reconectar el mando. Perfiles en JSON que guardan luz y, si quieres, la tasa.

    La pagina de **LUCES** ya no pide elegir el mando: lo resuelve sola, y sigue
    encontrandolo cuando el mando virtual esta encendido y HidHide oculta el fisico.

    El color se elige con tres barras (tono, saturacion y brillo del color) o
    escribiendo el hex. Ojo con dos "brillos" que no son lo mismo: la barra de
    **brillo del color** es cuanto se acerca al negro; los segmentos de **BRILLO** son la
    intensidad del LED del mando, una propiedad del hardware.

    Con **+** guardas el color actual en tu paleta (hasta 12, en
    `%APPDATA%\UltraPolling\palette.json`); con clic derecho lo quitas.
  - **Configurar el mando** — un remapeador robusto con **visualizador en vivo** del mando transformado (lo que ve el juego) y **modo streamer** (overlay transparente para OBS). El mando en vivo se dibuja con un diseño vectorial propio incluido en la app. Si prefieres una apariencia distinta, puedes instalar un **skin** propio en `%APPDATA%\UltraPolling\skins\<nombre>\` (una imagen base + sus sprites + un `skin.json`); la app lo detecta al abrir y el botón RECARGAR SKIN lo aplica en caliente. Los skins **no se distribuyen con la app**: el arte de un skin es de su autor, y usar o compartir arte ajeno es responsabilidad de quien lo instala.
    - **Curvas de respuesta v2:** Simplificado a modo **Lineal** (por defecto) y **Editor**. En el Editor puedes arrastrar 3 puntos de control con colores fijos con significado (Verde = Zona Baja/precisión, Ámbar = Zona Media/progresividad, Rojo = Zona Alta/límite).
    - **Biblioteca "MIS CURVAS":** Guarda, carga y borra tus curvas personalizadas con nombre de forma independiente al perfil de remapeo general.
    - **Ayuda "¿CÓMO FUNCIONA?":** Botón de documentación integrada que explica en detalle cómo influye cada punto de color en la respuesta del stick.
    - **Otras opciones:** Ajuste de zona muerta y alcance por stick, punto de disparo de los gatillos (hair trigger), remapeo de botones y de las zonas del touchpad.
    - **Drivers y Recursos:** Panel unificado en la pestaña de Ajustes con enlaces directos para instalar ViGEmBus, HidHide y .NET 9.0 Runtime.
    - **Aviso de seguridad:** El mando virtual + HidHide son detectables por un anticheat de kernel (Ricochet, Vanguard, EAC, BattlEye) aunque no hagas trampa; pensado para un jugador o para juegos sin anticheat; jugar online con esto activo es bajo tu propio riesgo. Sin macros, sin teclado/ratón, sin auto-aim.

  Por Bluetooth el mando usa otro protocolo y no se controla; la app solo lista mandos conectados por USB, así que un mando por Bluetooth directamente no aparece en la lista.
- **Gestión del driver.** Instala/desinstala el servicio `hidusbf`, cambia entre modos del driver, y aplica los cambios sin reiniciar el PC mediante un replug por software (quita el dispositivo del árbol PnP, lo re-enumera y lo reinicia).

## Requisitos

- Windows 10 u 11 x64.
- Ejecutar como **Administrador** (el ejecutable lo pide solo; modifica el registro y habla con drivers PnP).
- Para cualquier modo que no sea `NOPATCH` (`1kHz`, `2kHz-4kHz`, `4kHz-8kHz`): **Integridad de memoria DESACTIVADA** en Seguridad de Windows → Aislamiento del núcleo. Con ella activada, el driver con parche no puede cargar.
- **No hace falta instalar .NET**: el ejecutable es self-contained.

## Descarga y uso

1. Descarga `UltraPolling-v2.5-win-x64.zip` desde [Releases](../../releases).
2. Extrae el ZIP completo. **La carpeta `DRIVER` debe quedarse al lado de `UltraPolling.exe`**: la app la usa para identificar (por hash) qué driver tienes instalado y para copiar el `.sys` al cambiar de modo. Sin ella, la app arranca pero no puede instalar ni identificar nada.
3. Ejecuta `UltraPolling.exe`.
4. En la pestaña Sistema, instala el servicio si no está instalado y elige el modo del driver.
5. Selecciona tu dispositivo en la lista.
6. Elige una **TASA OBJETIVO**.
7. Pulsa **APLICAR CAMBIOS**. Un solo botón hace todo: activa el filtro, escribe la tasa y reconecta el dispositivo (replug por software). Ese replug es lo que aplica el cambio de verdad: un reinicio PnP a secas no basta, porque el dispositivo nunca abandona el bus USB y sus descriptores —donde hidusbf escribe la tasa— no se releen. Un enlace **Restablecer valores** deshace el filtro y lo deja por defecto.

### Modos del driver

El modo es el techo de velocidad de todo el sistema:

| Modo | Techo | Notas |
|---|---|---|
| `NOPATCH` | 1000 Hz | No toca el kernel. El modo para jugar online. |
| `1kHz` | 1000 Hz | Levanta el límite en dispositivos Low Speed. También parchea código de Windows en memoria (nivel 1k): mismo riesgo anticheat que los modos superiores. |
| `2kHz-4kHz` | 4000 Hz | Parchea código de Windows en memoria. |
| `4kHz-8kHz` | 8000 Hz | Parchea código de Windows en memoria. |

Cambiar de modo puede requerir reemplazar el archivo del driver (entre niveles con parche suele bastar el registro), y Windows bloquea el archivo de un driver cargado. Si el cambio falla, usa **Restablecer valores** para quitar el filtro de los dispositivos y reinícialos, o reinicia el PC.

Una tasa alta no crea datos: si el firmware del dispositivo genera reportes a 1000 Hz, sondearlo a 8000 Hz devuelve el mismo dato repetido.

## Juego online y anticheat

Sin adornos, esto es lo que hay:

- **El riesgo real es el modo del driver, no la app abierta.** Los modos con parche (`2kHz-4kHz`, `4kHz-8kHz`, y también `1kHz`, que en esta app siempre instala la variante con parche) reescriben código del kernel de Windows (`USBXHCI.SYS`/`USBPORT.SYS`) **en memoria**. Un anticheat de kernel (Ricochet, Vanguard, EAC, BattlEye) que encuentre código del kernel modificado no tiene forma de distinguir "overclock de ratón" de "cheat". Ese estado además vive en el `.sys` instalado y en el registro: está activo corra o no la interfaz. **Para jugar online, usa `NOPATCH`.**
- **La sola presencia del driver puede levantar banderas.** `hidusbf.sys` es un filter driver de kernel que se interpone en el tráfico de tus dispositivos de entrada. Incluso en `NOPATCH`, un escáner puede fichar su presencia. Si quieres riesgo cero, desinstala el servicio antes de jugar.
- **La luz es la parte menos arriesgada.** Cambiar el color o el LED de jugador es un output report HID estándar por USB — exactamente el mismo tráfico que generan Steam Input o DSX cada frame. No parchea nada. Aun así, un proceso sin firmar escribiendo al mando durante una partida puede parecerle sospechoso a un anticheat — la propia app lo avisa: cierra UltraPolling antes de jugar online y deja el color puesto.
- **Nada de lo anterior es una garantía.** Ningún anticheat publica sus reglas, las detecciones cambian sin aviso, los baneos por oleadas son retroactivos, y "a mí no me ha pasado nada" no es una prueba de seguridad. Usa los modos con parche bajo tu propio criterio y solo en juegos donde te dé igual la cuenta.

## Compilar desde el código

Necesitas el SDK de .NET 9.

```powershell
# Compilar la carpeta portable en dist\UltraPolling (comprímela tú para publicarla)
.\package.ps1

# Solo la suite de tests
dotnet test HidusbfModernGui.Tests\HidusbfModernGui.Tests.csproj
```

El resultado es un ejecutable self-contained de un solo archivo (`net9.0-windows`, x64).

## Créditos y licencia

Este repositorio contiene dos obras distintas, con autoría y licencia separadas:

- **El driver hidusbf** — la carpeta `DRIVER/`, los `README.*.TXT` originales y `SweetLow.CER` — es obra de **SweetLow** y sigue siendo suya. Se redistribuye aquí **sin modificar**, con atribución. Fuentes originales:
  - https://github.com/LordOfMice/hidusbf
  - Hilo original: https://www.overclock.net/threads/usb-mouse-hard-overclocking-2000-hz.1589644/
  - Firma de los drivers: [Battle Beaver Customs](https://www.battlebeavercustoms.com/)
- **El código propio de UltraPolling** — `HidusbfModernGui/` (la interfaz), `HidusbfModernGui.Tests/`, `VerifyState/`, `query/` y el tooling de empaquetado — se publica bajo licencia **MIT** (ver [LICENSE](LICENSE)). La licencia MIT cubre únicamente ese código; no cubre el driver de SweetLow.
