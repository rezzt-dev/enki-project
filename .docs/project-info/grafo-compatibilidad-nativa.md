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
- Arrastrar un nodo guarda las posiciones a través de `HandlePositionsReported`, usando `GlobalId`
  para distinguir rutas iguales en sesiones diferentes. Arrastrar el fondo desplaza la cámara;
  la rueda hace zoom alrededor del puntero.
- Utiliza los recursos de tema y las fuentes empaquetadas. El título aparece al pasar por un nodo.
- La distribución inicial es estática y determinista. Conserva las coordenadas guardadas cuando las
  notas están separadas y los límites de las sesiones no se cruzan. Si hay coordenadas inválidas,
  notas apiladas o sesiones mezcladas, reconstruye grupos orgánicos de densidad uniforme y coloca las sesiones en un
  anillo circular. Cada sesión reserva un sector según su tamaño, con margen para el nombre y las sesiones vecinas. La distribución por ángulo áureo
  evita filas rígidas y mantiene una silueta equilibrada.
  Esta recuperación solo afecta a la vista: los manifiestos se escriben mediante el flujo existente
  al arrastrar una nota. No reproduce la física del navegador; ambos visores mantienen la escena estable después del layout.
- La opción **Malla de sesión**, activada de inicio, dibuja una malla visual por sesión: triangulación de Delaunay de sus nodos vecinos.
  No hay puentes decorativos entre sesiones; las sesiones de una sola nota quedan sin malla.
  Los enlaces reales entre notas de distintas sesiones siguen visibles.
  Usa las posiciones visibles, también al filtrar o recuperar coordenadas manuales. Su topología
  se calcula al actualizar el grafo, con un número lineal de aristas; no se recalcula
  durante el repintado. Estas líneas no son enlaces, no modifican notas, filtros ni contadores.
- La malla usa `Ink20` y 0,65 px; los wikilinks usan `Ink60` y 0,9 px; los enlaces manuales,
  `Ink80` y 1,4 px, modulados por el control de grosor. Al pasar sobre una nota, sus enlaces reales destacan con `Ink90` y 2,4 px.
  Los grosores se mantienen en unidades de pantalla para conservar la jerarquía al hacer zoom.
- El radio de los nodos acompaña al zoom. Los nombres de sesión evitan nodos, otras etiquetas y los
  límites de la vista; cuando no caben se omiten hasta que haya espacio. Una única ficha al pasar el ratón muestra título y sesión,
  recorta textos largos y permanece dentro de la vista.
- Cambiar enlaces conserva la cámara y las posiciones; las notas ocultas conservan una caché de
  coordenadas para volver a mostrarse sin recolocar el resto. Al reducir un filtro, las notas restantes
  conservan sus coordenadas. Una distribución guardada legible sigue respetándose, incluido el
  arrastre manual de notas; la forma circular se aplica a las distribuciones nuevas o recuperadas.
- Al salir se cancelan temporizadores y captura del puntero. Volver a adjuntar la vista restaura sus
  suscripciones al viewmodel y reconstruye el grafo.

## Verificación

`NativeSessionGraphUiTests` comprueba dibujo, temas, clic/doble clic, arrastre, posiciones, zoom,
actualizaciones y 2.000 nodos con coordenadas inválidas. También verifica que las etiquetas de 150
notas con sesiones mezcladas no se solapen en vistas de 616×508 y 360×300, incluso tras hacer zoom.
`NativeSessionGraphLayoutTests` verifica separación, determinismo, conservación de coordenadas legibles
y ausencia de cambios sobre el modelo original. También comprueba centros circulares, separación
entre sesiones de tamaños desiguales, conectividad de la malla dentro de cada sesión y ausencia de puentes entre sesiones, ausencia de aristas duplicadas,
vecindad espacial y estabilidad al cambiar enlaces. `NativeSessionGraphMeshTests` verifica que
la malla no se cruce con posiciones irregulares y que los nodos colineales o coincidentes sigan
conectados. Las pruebas UI cubren además que editar enlaces no reinicie el zoom.
`SessionsGraphFiltersUiTests` fuerza la ausencia de navegador y verifica el grafo real ligado al viewmodel, apertura de una nota, filtros y reentrada.
La CI ejecuta estos casos en Linux, Windows y macOS sin instalar motores web adicionales.

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
