# UltraPolling v2.5

Primera versión estable desde la v1.0-beta. Cambia casi toda la interfaz y añade
medición en vivo del mando.

## Lo más importante

**El mando virtual ya no añade retardo.** El passthrough al DS4 virtual se hacía desde
un temporizador de interfaz cada 8 ms — 125 Hz — mientras el mando entregaba 8000. Se
tiraba el 98 % de los reportes y se sumaban hasta 8 ms de espera, más de lo que ahorra
todo el overclock. Ahora el reenvío sale del hilo del lector, en el instante en que
llega el reporte. Medido: enviar al DS4 virtual cuesta 4,4 µs de media y 16,3 µs en el
percentil 99, contra un presupuesto de 125 µs a 8000 Hz.

**La app vive en la bandeja.** La X esconde en vez de cerrar, para que el mando virtual
y el color sigan aplicándose mientras juegas. Se sale por el menú del icono. Instancia
única: abrir el acceso directo otra vez trae al frente la que ya corre.

## Dispositivos

- Lista en tarjetas, con una pastilla que dice `ACTIVO - <tasa>` sólo cuando la tasa
  está realmente resuelta, y `POR DEFECTO` en cualquier otro caso.
- **MEDIDA** es el titular: lo que el dispositivo hace de verdad, no lo que se le pidió.
  Debajo, **PEDIDA** y un punto que compara las dos.
- Distintivo de regularidad del flujo: `REGULAR` / `IRREGULAR` / `SIN DATOS`, calculado
  con el percentil 95 y un tope de magnitud sobre el percentil 99.
- **ENTRE REPORTES** en vez de "latencia". No es lo mismo y la app no puede medir la
  segunda; ver más abajo.
- Especificaciones técnicas plegadas tras un ojo, con el instance id.

## Mando

- **Gatillos:** arco de 44 guiones por gatillo que sigue el recorrido real, con una
  marca en el punto de disparo configurado.
- **Sticks:** los dos a la vez, cada uno con su monitor 2D en vivo, su curva con el
  punto de salida real, y métricas — `TASA`, `ENTRE REPORTES`, `VALORES NUEVOS/s` y
  `DERIVA`. `SINCRONIZAR L/R` copia los ajustes de un stick al otro.
- **Perfiles** en la barra superior en vez de una página: desplegable, nuevo, renombrar,
  guardar, borrar, importar y exportar. Un perfil guarda luz y configuración del mando.

## Luces

- Selector de color con barras de tono, saturación y brillo, hex editable y paleta
  propia de hasta 12 colores.
- LED de jugador y brillo en segmentos; efectos de LED y de color en una sola fila.
- El mando se detecta solo, sin desplegable.

## Lo que esta versión NO mide, y por qué

**Latencia.** La app marca la hora a la que **llega** cada reporte. La latencia sería el
retardo desde que mueves el stick hasta que el juego lo ve, y el reporte del DualSense
no trae marca de tiempo de origen: el instante del movimiento no es un dato que la app
posea. Falta el dato, no el código. Lo que sí mide es **ENTRE REPORTES**, el hueco entre
llegadas.

**Error de posición del stick.** No existe una posición "verdadera" contra la que
comparar la reportada, así que no hay error que calcular.

En su lugar hay dos medidas que sí se pueden hacer: **DERIVA**, cuánto se separa del
centro el stick en reposo, y **VALORES NUEVOS/s**, cada cuánto cambia de verdad el valor
del eje frente a cuántos reportes llegan — la diferencia entre la tasa de transporte y
la de muestreo del propio mando.

## Notas de calibración

Los umbrales de deriva (`Ok` por debajo del 5 %, `Leve` hasta el 10 %) vienen de **un**
mando medido más criterio, no de una población. Si tu stick sano marca `LEVE` en reposo,
el umbral es lo que hay que ajustar.

## Requisitos

Windows x64. No hace falta instalar .NET: el paquete es autocontenido. Ejecutar como
administrador. El mando virtual necesita ViGEmBus y HidHide (de Nefarius), instalados
aparte.

## Aviso sobre juego online

El riesgo de anticheat está en el **modo de parcheo del driver**: con un build de parcheo
activo, hidusbf reescribe `USBXHCI.SYS` / `USBPORT.SYS` en memoria del kernel, y ese
estado sigue activo corra o no esta aplicación. Para jugar online, usa **NOPATCH**.

Cambiar el color del mando no es ese riesgo: abre el mando en modo compartido y escribe
un report HID estándar tocando sólo los bytes del LED, exactamente lo que hacen Steam
Input y DSX en cada cuadro.

## Sin trampas, a propósito

Sin macros, sin emulación de teclado y ratón, sin auto-aim, sin evasión de detección.
El remapeador sólo reconfigura el mando: curvas, zonas muertas, botones y zonas del
touchpad. Es política del proyecto, no una limitación técnica.
