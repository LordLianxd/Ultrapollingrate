# UltraPolling

Interfaz moderna y gratuita para el driver [hidusbf](https://github.com/LordOfMice/hidusbf) de SweetLow: overclock de la tasa de sondeo (polling rate) USB en Windows, con medidor de tasa real integrado y control de la luz del DualSense.

<!-- capturas pendientes -->

## Qué hace

- **Overclock de polling rate USB.** Cambia la tasa de sondeo de ratones, teclados y mandos: desde 125 Hz hasta 8000 Hz, según el modo del driver y de lo que aguante el dispositivo. El techo real depende de la clase de velocidad USB (Low/Full/High Speed) y del firmware del dispositivo; ningún número está garantizado en un hardware concreto.
- **Medidor de tasa real.** La app no solo muestra la tasa que *pidió*: abre la interfaz HID del dispositivo seleccionado y mide la llegada de reportes en vivo, mostrando PEDIDA vs MEDIDA (mediana, min/max de los huecos). Es la respuesta con números a "¿se aplicó el overclock?". Limitación honesta: mide reportes HID, no el bus USB; un ratón quieto no genera datos y mide "sin datos", no 0 Hz mentiroso.
- **Luz del DualSense (solo USB/cable).** Color de la barra de luz con selector y hex, LED de jugador (las 5 luces bajo el touchpad, patrón simétrico) con brillo, y un efecto arcoíris por pasos calculado en OKLab para que las transiciones sean perceptualmente uniformes. Perfiles en JSON que guardan luz y, si quieres, la tasa. Por Bluetooth el mando usa otro protocolo y no se controla; la app solo lista mandos conectados por USB, así que un mando por Bluetooth directamente no aparece en la lista.
- **Gestión del driver.** Instala/desinstala el servicio `hidusbf`, cambia entre modos del driver, y aplica los cambios sin reiniciar el PC mediante RECONECTAR (replug por software: quita el dispositivo del árbol PnP, lo re-enumera y lo reinicia).

## Requisitos

- Windows 10 u 11 x64.
- Ejecutar como **Administrador** (el ejecutable lo pide solo; modifica el registro y habla con drivers PnP).
- Para cualquier modo que no sea `NOPATCH` (`1kHz`, `2kHz-4kHz`, `4kHz-8kHz`): **Integridad de memoria DESACTIVADA** en Seguridad de Windows → Aislamiento del núcleo. Con ella activada, el driver con parche no puede cargar.
- **No hace falta instalar .NET**: el ejecutable es self-contained.

## Descarga y uso

1. Descarga `UltraPolling-v1.0-beta-win-x64.zip` desde [Releases](../../releases).
2. Extrae el ZIP completo. **La carpeta `DRIVER` debe quedarse al lado de `UltraPolling.exe`**: la app la usa para identificar (por hash) qué driver tienes instalado y para copiar el `.sys` al cambiar de modo. Sin ella, la app arranca pero no puede instalar ni identificar nada.
3. Ejecuta `UltraPolling.exe`.
4. En la pestaña Sistema, instala el servicio si no está instalado y elige el modo del driver.
5. Selecciona tu dispositivo en la lista.
6. Activa **FILTRO**.
7. Elige una **TASA OBJETIVO**.
8. Pulsa **RECONECTAR (REPLUG)**. Este botón es el que aplica el cambio de verdad: un reinicio PnP a secas no basta, porque el dispositivo nunca abandona el bus USB y sus descriptores —donde hidusbf escribe la tasa— no se releen.

### Modos del driver

El modo es el techo de velocidad de todo el sistema:

| Modo | Techo | Notas |
|---|---|---|
| `NOPATCH` | 1000 Hz | No toca el kernel. El modo para jugar online. |
| `1kHz` | 1000 Hz | Levanta el límite en dispositivos Low Speed. También parchea código de Windows en memoria (nivel 1k): mismo riesgo anticheat que los modos superiores. |
| `2kHz-4kHz` | 4000 Hz | Parchea código de Windows en memoria. |
| `4kHz-8kHz` | 8000 Hz | Parchea código de Windows en memoria. |

Cambiar de modo puede requerir reemplazar el archivo del driver (entre niveles con parche suele bastar el registro), y Windows bloquea el archivo de un driver cargado. Si el cambio falla, quita el FILTRO de todos los dispositivos y reinícialos, o reinicia el PC.

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
