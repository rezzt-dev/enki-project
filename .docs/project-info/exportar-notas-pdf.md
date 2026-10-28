# Exportar notas a PDF

Exportación de cualquier nota de la sección Notas a un PDF maquetado como un documento de procesador de
textos: hoja A4 o Carta con los márgenes «Normal» de Word, texto vectorial seleccionable y buscable,
paginación que nunca parte una línea, tablas que repiten su encabezado, imágenes nítidas y enlaces que se
pueden pulsar.

Sustituye a la exportación de nota de T8 (`exportar-vistas-imagen-pdf.md`), que componía una imagen larga
de la nota y la cortaba en hojas a altura fija: el texto era un bitmap, el corte podía partir una línea por
la mitad, las tablas e imágenes no se dibujaban y el documento heredaba el tema oscuro.

## Puntos de entrada

- **Editor:** botón de exportar de la cabecera, ahora un menú (PDF, HTML, PNG). Mientras se compone el PDF
  el icono gira y el menú no admite otra exportación.
- **Tarjeta de nota:** menú contextual, «Exportar como PDF…».
- **Paleta de comandos:** «Exportar nota como PDF» con una pestaña activa.

Siempre se exporta el texto en vivo de la pestaña si la nota está abierta (cambios sin guardar incluidos),
igual que la exportación a HTML. Tras guardar se avisa en el centro de notificaciones y se revela el
archivo en el explorador del sistema; los fallos se anuncian con su motivo.

## El editor manda

El documento no tiene una segunda idea de qué es cada cosa: sale de los mismos analizadores del live
preview (`MarkdownLineParser`, `MarkdownTableParser`, `MarkdownHtmlParser`, `MarkdownCallouts`,
`MarkdownSpecialBlocks`). Lo que se ve al editar es lo que sale impreso:

| En el editor | En el PDF |
| --- | --- |
| Cada línea del documento es una línea visible | Igual: un salto simple no se funde con la línea siguiente |
| Justificado (Ajustes › Editor) | Justificado con las mismas excepciones (tabuladores, imágenes, sangría de código) |
| `<div align="center">`, `<p style="text-align:…">`, `<center>` | Misma alineación, también para imágenes |
| `<b>`, `<i>`, `<u>`, `<s>`, `<mark>`, `<code>`, `<br>`, también abiertas en una línea y cerradas en otra | Mismo formato; las etiquetas no se ven |
| Negrita, cursiva, tachado, `==resaltado==`, código en línea, escapes `\*` | Igual, combinables |
| Listas anidadas por sangría, numeración escrita por el usuario, tareas | Viñetas por nivel (dibujadas, no glifos), números tal cual, casillas cuadradas; tarea hecha tachada |
| Citas anidadas (`>>`) | Una barra por nivel, continuas |
| Avisos `> [!warning] Título` | Caja con barra y velo del tono, icono y título |
| Bloques de código con lenguaje | Coloreados con las mismas gramáticas TextMate (tema claro) |
| Tablas con alineación por columna | Rejilla del editor, anchos como un navegador, encabezado repetido |
| `![alt|300](img.png)`, `![[img.png]]`, `data:`, URL | Mismo tamaño (px del editor → pt), misma resolución de rutas |
| Fórmulas `$…$` y `$$…$$` | Compuestas con CSharpMath, a resolución de impresión |
| `![[Otra nota]]` solo en su línea | El contenido de la nota embebida, tras una barra y su nombre |
| Enlaces externos | Subrayados y clicables en el PDF |
| Wikilinks | Subrayado discontinuo (no llevan a ningún sitio fuera del vault) |

La tipografía y el cuerpo de letra salen de Ajustes › Editor (fuente del editor y de código; el tamaño del
editor en px pasado a pt, ~11 pt por defecto). Los diagramas Mermaid salen como su bloque de código, igual
que en HTML: necesitan el WebView del editor.

## Piezas (`services/export/`)

| Archivo | Qué hace |
| --- | --- |
| `NoteDocumentStyle.cs` | `NotePageSetup` (A4/Carta con márgenes de Word, elegida por la cultura) y el estilo del documento: `ForPrint` (papel blanco siempre) y `ForScreen` (paleta del editor en el tema activo, para el PNG). |
| `NoteDocumentAssets.cs` | `NoteExportSource` y la precarga en segundo plano de imágenes (a ~300 ppp), fórmulas y resaltado de código. |
| `NoteDocumentComposer.cs` | Markdown → bloques de controles de Avalonia maquetados al ancho de la caja de texto; `DocumentBreaks` (dónde se puede cortar cada bloque) y `DocumentLinks`. |
| `DocumentPaginator.cs` | Reparto en páginas con las reglas de composición. Lógica pura, sin Avalonia. |
| `NoteDocumentExporter.cs` | Orquesta y dibuja: PDF con el backend PDF de Skia, PNG con `VisualSnapshotRenderer`. |

Fuera de `services/export/`, dos extracciones para no duplicar lógica del editor:
`views/editor/Markdown/CodeTokenizer.cs` (gramáticas TextMate, compartido con `MarkdownCodeHighlighter`) y
`MarkdownImageCache.LoadUncached` (resolver, leer —descifrando sesiones protegidas— y decodificar una
imagen con los mismos topes que el editor, sin pasar por la caché de pantalla).

## Paginación

`DocumentBreaks` recorre el árbol visual de cada bloque y reúne los intervalos que no admiten un corte por
dentro: cada línea maquetada de cada `TextBlock`, cada imagen y cada control marcado como
`export-keep-together` (una fila de tabla). Los intervalos que se solapan forman una unidad; los cortes son
el principio de cada unidad. `DocumentPaginator` aplica después:

- nunca corta dentro de una unidad;
- viudas y huérfanas: al partir un bloque deja al menos dos unidades a cada lado;
- un encabezado no se queda al pie sin el arranque del bloque siguiente;
- la fila de encabezados de una tabla se repite en cada página por la que continúa;
- el aire de encima de un bloque desaparece en lo alto de una página;
- una imagen nunca supera el alto de la caja de texto (se reduce), así que nunca se parte.

Las reglas solo se relajan si un bloque no cabe ni en una página vacía (una fila más alta que la hoja):
primero viudas y huérfanas, luego se corta entre las líneas de dentro de la fila y, como último recurso, a
la altura de la página.

## Cómo se dibuja

`SKDocument.CreatePdf` (SkiaSharp ya viene con Avalonia: sin dependencias nuevas) y
`DrawingContextHelper.RenderAsync` para dibujar los controles sobre el lienzo de cada página. Tres
particularidades de `RenderAsync` que condicionan el diseño:

1. **Ignora la matriz del lienzo.** Todo se coloca dentro del árbol visual: cada página es un `Canvas` del
   tamaño de la hoja con cada trozo en su sitio.
2. **No dibuja nada si el lienzo ya trae un recorte.** Cada trozo va dentro de un contenedor con
   `ClipToBounds` que lo recorta a su franja; el bloque se dibuja entero desplazado hacia arriba, así que
   una tabla o un bloque de código partidos se cortan exactamente por el corte elegido.
3. **Su rectángulo no es un recorte**, sino el origen del dibujo: se le pasan siempre los límites propios.

Además, `SKDocumentPdfMetadata.RasterDpi` se aplica como escala inicial del lienzo, que `RenderAsync` pisa:
no se usa (no hay efectos que rasterizar). Un bloque que aparece dos veces en una página (tabla partida:
encabezado repetido + filas) se dibuja en dos capas, porque un control no puede tener dos padres.

Detalles de calidad:

- **Sin ligaduras** (`-liga`): «fi» como un solo glifo se copia y se busca como «ﬁ».
- **Enlaces clicables:** cada enlace seguro (`ExternalLinkPolicy.IsSafeUrl`) se registra con su posición
  en el texto y se anota sobre el rectángulo que ocupa tras maquetar (`DrawUrlAnnotation`).
- **Metadatos:** título de la nota, etiquetas como palabras clave, Enki como creador.
- **Imágenes:** Skia guarda como JPEG (calidad 92) las opacas, como Word al guardar como PDF.
- **Fuentes:** Skia incrusta las que se usan. El SkiaSharp empaquetado no recorta fuentes, así que la de
  código del editor (JetBrains Mono Nerd Font, con todos sus iconos) añade ~1,4 MB a un PDF que tenga
  código; por eso las etiquetas de la cabecera van en la fuente del cuerpo.

## Pruebas

- `tests/enki.Tests/Services/DocumentPaginatorTests.cs` — reglas de paginación sobre geometría pura y
  la hoja/márgenes por cultura.
- `tests/enki.UITests/ViewExportUiTests.cs` — composición con los bloques del editor, marcadores ocultos,
  título no repetido, transclusión, cortes solo entre líneas, encabezado de tabla repetido y nota vacía.
  Con `ENKI_GRAPH_PREVIEW=<carpeta>` (Skia real) genera además un PDF y un PNG con todo lo anterior,
  imagen adjunta incluida, y los deja en esa carpeta para revisarlos a ojo.

La comprobación visual en visores reales está en `qa-manual-release-candidate.md` §8.7.
