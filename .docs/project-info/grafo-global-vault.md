# Grafo global del vault

El grafo de **Sesiones** reúne las notas del vault accesible: sesiones desbloqueadas y notas sin
sesión, incluidas sus subcarpetas. También se muestra cuando hay notas pero todavía no hay sesiones.
Reutiliza `SessionService.LoadGlobalGraph()` y `SessionGraphRenderer` (sigma.js y d3-force). No crea
un segundo índice persistido, no modifica el frontmatter al filtrar y no cambia los manifiestos.

## Explorar y filtrar

El botón **Filtrar** abre el popover existente, ampliado con estas opciones combinables:

- **Buscar**: combina palabras de título, ruta, sesión y etiquetas (también con `#`). Todas las
  palabras deben coincidir; se combina con los demás filtros y no vuelve a leer el vault.
- **Explorar**: todas las notas, notas huérfanas o conexiones entre sesiones.
- **Grupos conectados**: aislar una componente conexa. La etiqueta utiliza el título de una de sus
  notas con más conexiones, su sesión y el número de notas. Se ordenan por tamaño descendente.
- **Sesiones visibles**: conservar una o varias sesiones, incluido el grupo «Sin sesión».
- **Etiquetas**: proceden del frontmatter que ya lee `NoteTagHelper`. Sin selección se muestran todas
  las notas, incluidas las que no tienen etiquetas. Varias etiquetas se combinan con **O**; esa condición
  se combina con **Y** con el resto de filtros. No se distingue entre mayúsculas y minúsculas.
- **Restablecer filtros**: vuelve al vault completo con una sola actualización del grafo.

Las etiquetas también aparecen al pasar el cursor sobre una nota. El doble clic sigue abriendo la nota
original en su sesión. El zoom, el movimiento de nodos y los enlaces manuales conservan el renderizador
y los comandos existentes. Cambiar filtros cancela el primer extremo pendiente de un enlace manual,
para evitar enlazar accidentalmente una nota que acaba de ocultarse.

El recuento superior indica notas y enlaces **visibles**. La leyenda inferior, plegable, permite
ocultar o mostrar cada sesión y aislarla con el botón de objetivo. Su contador indica las notas de
cada sesión en el **vault accesible completo**, antes de los demás filtros. Las sesiones cifradas bloqueadas no aportan
notas, títulos, etiquetas ni grupos. Por ello, desbloquear una sesión puede cambiar esos totales.

## Qué significa cada descubrimiento

**Huérfana** significa que una nota no tiene ninguna otra nota vecina en el grafo global accesible,
considerando tanto wikilinks resueltos como enlaces manuales, entrantes o salientes. Un autoenlace no
crea un vecino. Ocultar una sesión o una etiqueta no convierte artificialmente otras notas en huérfanas.

**Conexiones entre sesiones** muestra exclusivamente aristas entre grupos distintos y sus extremos.
«Sin sesión» cuenta como un grupo. Los dos extremos deben superar los filtros activos; si uno queda
oculto, tampoco se muestra el otro como una supuesta conexión cruzada aislada.

**Grupo conectado** es una componente conexa de dos o más notas, incluso si atraviesa sesiones. No es
una clasificación semántica ni una detección de comunidades por IA. Los grupos se calculan antes de
filtrar: al ocultar algunas notas, el resto mantiene su pertenencia original. Las huérfanas no se cuentan
como grupos conectados. El color y la distribución por sesión siguen siendo los del grafo existente.

## Implementación y actualización

`SessionGraphAnalysisService.Analyze()` enriquece los nodos del grafo agregado con número de vecinos,
conexiones entre sesiones e identificador/tamaño de componente. Recorre el grafo de forma iterativa en
O(nodos + enlaces), deduplica vecinos y omite extremos ausentes. Se ejecuta dentro de la carga existente
en segundo plano. El identificador de componente es el menor `GlobalId`, independiente del orden de
recorrido mientras sus miembros no cambien.

`SessionsViewModel.GraphDiscovery.cs` deriva opciones y subconjuntos en memoria del último grafo.
Publica únicamente enlaces con ambos extremos visibles. Reutiliza `GraphUpdated` para enviar el
subconjunto al documento existente, sin releer archivos por cada cambio de filtro. El renderizador
conserva las posiciones de los nodos que siguen presentes y ajusta la cámara como ya hacía.

Entrar en Sesiones o pulsar actualizar vuelve a leer el vault. Crear o quitar enlaces manuales recalcula
la conectividad. Las sesiones/etiquetas seleccionadas se conservan mientras existan; las nuevas sesiones
entran visibles y las nuevas etiquetas no se seleccionan automáticamente. Un grupo seleccionado que
desaparece vuelve a «Todos los grupos». El idioma se actualiza sin perder el modo de exploración.

## Verificación

Pruebas en `SessionGraphAnalysisServiceTests`, `SessionsViewModelGraphFilterTests`,
`SessionGraphRendererTests` y `SessionsGraphFiltersUiTests`:

- Filtros combinados, etiquetas con diferente capitalización y ausencia de enlaces colgantes.
- Huérfanas reales frente a notas aisladas por un filtro y conexiones cruzadas con extremos visibles.
- Componentes entre sesiones, recálculo al eliminar enlaces y cadenas de 10.000 notas sin recursión.
- Vault sin sesiones, conservación de selección, retirada de etiquetas eliminadas y sesiones bloqueadas.
- Restablecimiento único, cancelación del enlace pendiente y serialización segura de etiquetas.
- Bindings del popover, temas claro/oscuro y conservación de modo al cambiar de idioma.

QA manual recomendado: abrir un vault con notas conectadas entre dos sesiones y notas sin sesión;
combinar sesión/etiqueta/modo/grupo; comprobar el estado sin resultados y restablecer; navegar mediante
doble clic; recorrer el popover con teclado y comprobar scroll con muchas sesiones/etiquetas.

## Compatibilidad del renderizado

Si el navegador del sistema no es utilizable, el grafo dispone de un renderizador Avalonia con las
mismas acciones y filtros. Ver [grafo-compatibilidad-nativa.md](grafo-compatibilidad-nativa.md).

## Presentación e interacción

- El grafo utiliza vínculos reales como estructura principal. La **malla de sesión**, activada de
  inicio, conecta todas las notas de cada sesión mediante vecinos espaciales; su explicación aclara que no representa enlaces. Puede ocultarse en Visualización.
- La distribución inicial comparte la recuperación determinista del visor nativo: reserva espacio
  proporcional a cada sesión y evita comenzar con todas las notas superpuestas. El visor web aplica
  repulsión, colisión y atracción por vínculos para estabilizar la red dentro de esos grupos.
- Las sesiones se agrupan por su identificador, incluso si sus nombres coinciden.
- El tamaño de cada nodo refleja sus vecinos visibles. Al señalar una nota, destacan ella, sus
  vecinos y sus enlaces; el resto pierde intensidad. Las etiquetas evitan notas y otras etiquetas,
  y aparecen con mayor detalle al acercarse. Los nombres de sesión son discretos y no crecen
  indefinidamente con el zoom.
- Los checkbox del panel y de **Visualización** usan una plantilla rectangular monocroma,
  con texto pulsable y foco de teclado. Los sliders muestran el multiplicador actual, avanzan en
  pasos de 0,1 y distinguen el tramo activo con un tirador rectangular. Ambas plantillas reutilizables
  (`RectangularCheckBox` y `RectangularSlider`) viven en `styles/ControlThemes.axaml`.
- **Visualización** permite ajustar nombres de notas, tamaño de nodos y grosor de enlaces. Estos
  controles y los botones de acercar, alejar y encuadrar funcionan también sin navegador.
- Ocultar y volver a mostrar una sesión conserva sus posiciones durante la vida del visor.
  Filtrar conserva la cámara; **aislar** encuadra explícitamente el resultado. Restablecer filtros
  elimina también la búsqueda, pero conserva los ajustes visuales.
- La escena deja de girar y no incorpora partículas decorativas sobre los enlaces. En el documento
  web, una vez estabilizada la física, se repinta al cambiar cámara, interacción, datos u opciones;
  los pulsos de enlace pendiente y rechazo mantienen su actualización mientras están activos.
- Las opciones visuales pertenecen al viewmodel de la pantalla; no crean archivos, almacenes ni
  propiedades nuevas en los manifiestos. Los enlaces y posiciones utilizan la persistencia existente.

## Comprobaciones del visor

`SessionsViewModelGraphFilterTests` cubre búsqueda combinada, aislamiento con una sola publicación,
contadores y preservación del Markdown. `GraphPresentationUiTests` comprueba recuperación de sesiones
ocultas, cámara y controles del documento real. Las pruebas de filtros y del visor nativo mantienen la
cobertura de temas, etiquetas, arrastre y grafos grandes.

Para QA manual: probar sesiones con nombres iguales, títulos largos, búsqueda sin resultados, ocultar
varias sesiones y recuperarlas después de arrastrar una nota, y los extremos de los sliders en ambos
temas. Confirmar apertura con doble clic y creación de enlaces en los dos renderizadores.
