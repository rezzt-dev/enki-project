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
- La distribución inicial es estática y determinista, agrupada por sesión, con coste lineal. Respeta
  las posiciones guardadas. No reproduce la simulación física ni la rotación ambiental del navegador.
- Al salir se cancelan temporizadores y captura del puntero. Volver a adjuntar la vista restaura sus
  suscripciones al viewmodel y reconstruye el grafo.

## Verificación

`NativeSessionGraphUiTests` comprueba dibujo, temas, clic/doble clic, arrastre, posiciones, zoom,
actualizaciones y 2.000 nodos con coordenadas inválidas. `SessionsGraphFiltersUiTests` fuerza la ausencia
de navegador y verifica el grafo real ligado al viewmodel, apertura de una nota, filtros y reentrada.
La CI ejecuta estos casos en Linux, Windows y macOS sin instalar motores web adicionales.

Antes de publicar, comprobar también una ventana real en cada sistema: abrir Sesiones en un equipo
sin motor web utilizable, aplicar filtros, enlazar notas, arrastrar, cerrar y volver a entrar. Las
pruebas headless no validan los adaptadores de navegador ni los controladores gráficos del equipo.
