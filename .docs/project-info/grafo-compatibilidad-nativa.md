# Grafo de sesiones sin dependencias de navegador

El grafo sigue siendo utilizable aunque no haya un motor web compatible. `SessionsView` usa el
renderizador web existente cuando `WebViewHost` permite inicializarlo y cambia a `NativeSessionGraph`
en los siguientes casos:

- No hay WebView2 en Windows o WKWebView disponible en macOS.
- En Linux, WPE no está instalado o no expone la API de backend que requiere Avalonia.
- La creación o navegación del navegador falla de forma síncrona, falla una invocación de JavaScript,
  o el documento no confirma su carga en seis segundos.

La alternativa se dibuja con Avalonia y las dependencias que ya usa el resto de la aplicación. No
requiere instalar WebKitGTK, WPE ni WebView2 para consultar el grafo. Se mantiene durante la vida de
esa vista; al volver a entrar se comprueba de nuevo la selección del renderizador (la detección del
motor se almacena durante el proceso). No se cambia el motor de las vistas ajenas al grafo.

## Causa del fallo observado en Linux

En XeroLinux las bibliotecas `libWPEWebKit-2.0.so.1`, `libwpe-1.0.so.1`,
`libWPEBackend-fdo-1.0.so.1` y `libwebkit2gtk-4.1.so.0` estaban instaladas, pero la primera no exportaba
`webkit_web_view_backend_new`. Avalonia.Controls.WebView 12.1.0 importa esa función. No es un símbolo
de WebKitGTK y tener ambos paquetes instalados no resuelve esa incompatibilidad de ABI.

Se conserva la comprobación previa: quitarla puede provocar una excepción durante la inicialización
asíncrona interna de Avalonia, fuera del `try/catch` del constructor. En vez de mostrar «instala las
librerías», el grafo se dibuja directamente con Avalonia. En Windows solo se considera WebView2:
Edge Legacy no es una alternativa adecuada para el JavaScript del documento.

## Comportamiento del renderizador de compatibilidad

- Lee el mismo `SessionGraph` que el navegador. Los filtros y la exclusión de sesiones bloqueadas
  siguen perteneciendo a `SessionsViewModel`; no hay almacén ni escaneo alternativo.
- Dibuja nodos, colores de sesión y enlaces, incluidos los enlaces manuales.
- Doble clic abre la nota. Los clics del modo enlace, la selección pendiente y los enlaces rechazados
  se comunican por los mismos métodos y eventos del viewmodel.
- Dibuja la misma escena de esferas 3D que el documento web (`grafo-sesiones-3d.md`): consume la
  `SessionSphereScene` de la instancia de `SessionSphereLayout` que posee `SessionsView`, así que cambiar
  del navegador al visor nativo conserva la forma, la posición y la orientación de cada sesión.
- Proyección en perspectiva por esfera, orden del pintor por profundidad, niebla en cuatro capas y notas
  como impostores esféricos (`ImmutableRadialGradientBrush` cacheado por color, capa y énfasis).
- Rotación automática con un único bucle `TopLevel.RequestAnimationFrame` a 30 Hz y delta time. Solo pide
  fotogramas mientras gira, y el botón global de la barra inferior la detiene conservando la orientación.
- Arrastrar una nota la mueve en el plano de la pantalla a su profundidad, dentro de su esfera, y guarda
  las posiciones normalizadas de su sesión con `HandlePositionsReported`, usando `GlobalId` para
  distinguir rutas iguales en sesiones distintas. Arrastrar el volumen de una esfera la gira a mano y un
  clic sobre él selecciona la sesión. Arrastrar el fondo desplaza la cámara y la rueda hace zoom
  alrededor del puntero.
- Utiliza los recursos de tema y las fuentes empaquetadas. El título aparece al pasar por un nodo.
- La opción **Malla de sesión**, activada de inicio, dibuja la trama de vecinos espaciales en 3D de cada
  esfera (`SessionSphereMesh`). No hay puentes decorativos entre sesiones, y los enlaces reales entre
  sesiones siguen visibles. Estas líneas no son enlaces y no modifican notas, filtros ni contadores.
- La malla usa `Ink` al 24 %; los wikilinks, `Ink` al 60 % y 0,9 px; los enlaces manuales, al 90 % y
  1,4 px. Todo ello se modula por la profundidad y por el control de grosor. Al pasar sobre una nota,
  sus enlaces reales destacan al 70 % y 2,2 px. Los grosores se mantienen en unidades de pantalla.
- Los nombres de sesión se colocan desde el centro y el radio de su esfera, así que no giran. Evitan
  nodos, otras etiquetas y los límites de la vista, y si no caben se omiten. Una única ficha al pasar
  el ratón muestra título y sesión.
- Cambiar enlaces o filtros conserva cámara, posiciones y orientación. Al salir se cancelan la captura
  del puntero y el temporizador del enlace rechazado. Volver a adjuntar la vista restaura sus
  suscripciones al viewmodel y reconstruye el grafo.

## Verificación

`NativeSessionGraphUiTests` comprueba:

- Dibujo en temas claro y oscuro, clic, doble clic, arrastre en profundidad con posiciones
  normalizadas, filtrado, color, zoom y redimensionado.
- Rotación a velocidad constante, pausa sin cambios y reanudación desde la misma orientación.
- Etiquetas inmóviles, giro manual de una esfera, clic de selección de sesión y paneo.
- 2.000 notas con coordenadas inválidas, y etiquetas sin solapes en vistas de 616×508 y 360×300.

`SessionSphereLayoutTests` cubre el layout volumétrico y la malla (ver `grafo-sesiones-3d.md`).
`SessionsGraphFiltersUiTests` fuerza la ausencia de navegador y verifica el grafo real ligado al
viewmodel: apertura de una nota, filtros y reentrada. La CI ejecuta estos casos en Linux, Windows y
macOS sin instalar motores web adicionales.

Antes de publicar, comprobar también una ventana real en cada sistema: abrir Sesiones en un equipo
sin motor web utilizable, aplicar filtros, enlazar notas, arrastrar, cerrar y volver a entrar. Las
pruebas headless no validan los adaptadores de navegador ni los controladores gráficos del equipo.

## Controles y estabilidad

El panel de sesiones ocupa una columna propia y deja libre el lienzo. Por debajo de 900 px de
ancho del visor se oculta; el popover **Filtrar** mantiene búsqueda, visibilidad y ajustes. La barra
inferior contiene las acciones de cámara y una guía breve de ratón. En el lienzo enfocado, `+`/`−`
controlan el zoom y `Inicio` encuadra; en el visor nativo las flechas desplazan la cámara.
Redimensionar el visor nativo conserva el zoom y el punto central. Al terminar un arrastre se
recalcula la triangulación para que las líneas decorativas vuelvan a unir vecinos espaciales.

Para generar una captura de la vista real con Skia, ejecutar los tests `GraphPresentationUiTests`
con `ENKI_GRAPH_PREVIEW` apuntando a una carpeta temporal. El test crea su propio vault aislado,
exporta `workspace.png` y el documento web `graph.html`; no utiliza datos del usuario.
