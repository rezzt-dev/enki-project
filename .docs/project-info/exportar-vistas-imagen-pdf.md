# Exportar vistas a imagen y PDF (T8)

Exportación del **Calendario**, del **tablero Kanban** y de una **nota** a PNG y a PDF, desde el menú de
cada vista y desde la paleta de comandos. Corresponde a T8 de
`.docs/funcionalidades-pendientes-priorizadas.md` §4.4.

Antes de esto, lo único exportable era una nota a HTML (`NoteExportService`). No había ni PDF ni
exportación de vistas.

## Decisiones de partida

**PDF sin dependencia nueva.** El documento de la funcionalidad dejaba abiertas tres opciones: (a) escribir
el PDF a mano a partir de las imágenes, (b) añadir una librería de PDF (QuestPDF, PdfSharp) o (c) quedarse
en PNG y delegar en el "imprimir a PDF" del sistema. Se eligió **(a)**. Lo que hay que exportar aquí ya es
una imagen rasterizada por Avalonia, así que lo único que un PDF aporta sobre el PNG es la paginación y el
tamaño de hoja — y eso cabe en un archivo (`services/export/PdfImageWriter.cs`, ~180 líneas con
comentarios). (b) traía condiciones de licencia que revisar (§9, riesgo 4) para no usar ni una décima parte
de sus capacidades, y (c) no existe: Avalonia no expone diálogo de impresión del sistema.

**Se exporta lo que se ve.** El documento proponía además un diálogo con rango de fechas, capas y columnas
configurables. Se descartó: duplicaría en un formulario los controles que la propia vista ya tiene, y
"exporta lo que veo" es más predecible que "exporta lo que dije en otro sitio". El rango, las capas y los
filtros del Calendario, y el orden y los filtros del Kanban, son los que estén activos en pantalla.

**Tema activo, no claro forzado.** El documento dejaba abierto "fondo según tema activo o forzar claro para
imprimir". Se usa el tema activo: el contenido (tarjetas, textos, colores de capa) se rasteriza con el tema
activo de todas formas, y un fondo claro forzado bajo un contenido oscuro sería peor que cualquiera de las
dos opciones puras.

## Piezas (`services/export/`)

| Archivo | Qué hace |
| --- | --- |
| `ExportSnapshot.cs` | `ExportFormat` (Png/Pdf), la lámina ya rasterizada (PNG codificado + RGB en crudo, de una sola pasada) y `ExportRenderException`, el fallo "ni siquiera se pudo rasterizar" con su texto ya localizable. |
| `VisualSnapshotRenderer.cs` | Rasteriza un control a PNG y a RGB, con cabecera (título + subtítulo) y márgenes. |
| `FullSizeLayout.cs` | Maqueta la región a su tamaño completo mientras dura el render (sin recortes de los `ScrollViewer` internos) y la restaura después. |
| `PdfImageWriter.cs` | PDF 1.7 con tabla `xref` clásica, una hoja por imagen, `/DeviceRGB` de 8 bits con `/FlateDecode`. |
| `NotePageComposer.cs` | Compone fuera del árbol visual la hoja A4 de una nota a partir de su Markdown, y la pagina. |
| `ViewExportService.cs` | El flujo completo: localizar la región, rasterizar, diálogo de guardado, escritura atómica y aviso. |

### Por qué el tablero entero y no solo lo que se ve

El criterio de aceptación pide "incluir todas las columnas y tarjetas visibles del tablero, aunque excedan
el área en pantalla". Tres detalles lo consiguen:

1. Se rasteriza el **contenido** del `ScrollViewer`, no el `ScrollViewer`: el contenido está dispuesto a su
   tamaño completo y es el visor quien lo recorta.
2. Los `ScrollViewer` de **dentro** de la región también recortan: las tarjetas de cada columna del Kanban
   vertical, las franjas del Kanban horizontal, la cuadrícula del mes y la rejilla de horas de Semana/Día.
   Mientras dura el render, `FullSizeLayout` mide la región con alto ilimitado y la dispone a
   `max(lo que pide, lo que ocupaba)`. El ancho solo se libera si algún visor sigue desbordando en
   horizontal (las franjas del Kanban horizontal): liberarlo siempre haría que el tablero vertical, que
   reparte el ancho entre columnas, pidiera para cada columna el ancho de su título más largo.
3. Todos los visores (el que contiene la región y los de dentro) se llevan al origen antes de maquetar,
   porque un desplazamiento vivo saldría en la imagen como un recorte. Al terminar se invalida la región
   y su padre, se fuerza una pasada de maquetado y se restauran los desplazamientos, incluso si el render
   falla.

### El contenido se pega en la lámina en píxeles

El contenido se rasteriza en un bitmap propio y luego se pega bajo la cabecera. `DrawImage` recibe el
rectángulo de origen **en píxeles del bitmap** (`PixelSize`), no su tamaño lógico: con la sobrecarga corta,
Skia toma `Bitmap.Size` (lógico) como si fueran píxeles, así que a 2× solo copiaba el cuadrante superior
izquierdo y lo estiraba al doble, y la lámina salía ampliada y recortada.

### Cómo encuentra el viewmodel su región sin tocar ningún control

Los viewmodels no ven controles (MVVM estricto). Describen la región por **nombre** (`ViewExportRequest`)
y `ViewExportService.FindExportRoot` la localiza en la ventana viva a través de `App.MainWindowRef` — el
mismo recurso que ya usan `ToastService` y `NoteExportService` para lo que necesita un `TopLevel`. Se
compara `IsEffectivelyVisible` y no `IsVisible`, porque las disposiciones alternativas conviven en el árbol
y solo se distinguen por el `IsVisible` de algún ancestro.

Nombres en uso:

- `KanbanExportRootVertical` / `KanbanExportRootHorizontal` — las dos disposiciones del tablero
  (Ajustes › Apariencia); se ofrecen las dos y gana la que esté visible.
- `CalendarExportRoot` — la cabecera de días de la semana más la vista activa, **sin** la barra de
  navegación: un PNG del calendario no debe llevar botones de interfaz. Por eso las filas 1 y 2 de la
  columna de contenido de `CalendarView.axaml` viven dentro de un `Grid` propio.

### La hoja de una nota

El editor no sirve como origen del render: `AvaloniaEdit` virtualiza las líneas, así que rasterizarlo daría
solo la parte visible. `NotePageComposer` compone la hoja aparte, apoyándose en `MarkdownLineParser` —el
mismo analizador por líneas que usa la vista previa del editor— para no estrenar una segunda idea de qué es
un encabezado o una cita. Es la única pieza de `services/` que mira a `views/`, y lo hace sobre lógica pura;
duplicar el analizador sería peor.

Todo el estilo se declara explícitamente (tipografías, tamaños, colores) en vez de dejarlo a los estilos de
la app: la hoja se mide y dispone sin estar unida a ninguna ventana, donde los selectores de `Styles.axaml`
no llegan.

El PNG sale en una sola lámina tan alta como haga falta; el PDF se pagina en A4 (794×1123 px lógicos, 96 ppp).
El corte entre páginas es por altura fija y puede partir una línea por la mitad: es la concesión aceptada de
no traer un motor de maquetado paginado por una exportación.

## Garantías

- **Escritura atómica.** Todo pasa por `DurableFile.WriteAllBytesAtomic`, así que un fallo a mitad no deja
  un PNG ni un PDF truncados en disco.
- **Sin datos del vault en riesgo.** La exportación solo lee y escribe fuera del vault, en el destino que
  elija el usuario en el diálogo del sistema.
- **Tope de tamaño.** Un tablero enorme a 2× puede dar una imagen desmesurada; la escala baja sola hasta
  caber en 80 millones de píxeles y, si ni a 1× cabe, se aborta con el motivo en pantalla en vez de intentar
  reservar el buffer. El PDF además reduce la hoja si supera el lado máximo del formato (200", 14400 puntos).
- **Idioma y fechas.** La cabecera usa la cultura activa (`LocalizationService.Instance.CurrentCulture`).

## Puntos de entrada

- **Kanban:** botón de icono con menú en la cabecera del tablero (solo en modo Tablero).
- **Calendario:** botón de icono con menú en la barra de navegación, junto a "crear".
- **Nota:** menú contextual de la tarjeta de nota, junto a "Exportar como HTML".
- **Paleta de comandos:** las de Calendario y Kanban solo se ofrecen estando en esa página —se rasteriza lo
  que está en pantalla, así que fuera no habría nada que rasterizar—; la de la nota basta con que haya una
  pestaña activa, porque la hoja se compone desde el Markdown y no desde la pantalla.

Tras exportar se revela el archivo en el explorador del sistema (`FolderOpener.RevealFile`).

## Pruebas

- `tests/enki.Tests/Services/PdfImageWriterTests.cs` — sobre los bytes del archivo: cabecera, número de
  objetos, `xref` con desplazamientos que apuntan de verdad a cada objeto, tráiler, y que el flujo
  comprimido descomprime exactamente a los píxeles de entrada. Más el tamaño de hoja: que 2× dé la misma
  hoja con más resolución y que una hoja gigante se recorte conservando la proporción.
- `tests/enki.UITests/ViewExportUiTests.cs` — con las vistas reales maquetadas: que la región exportable
  exista con el nombre que busca el servicio, que esté dispuesta a su tamaño completo dentro de un
  `ScrollViewer`, que la del calendario no incluya la barra de navegación, que `FullSizeLayout` haga
  crecer el tablero hasta su columna más larga y la vista Semana hasta las 24 horas (y que ambas vuelvan
  a su tamaño después), y que la hoja de una nota se
  componga y se pagine (incluida la nota vacía, que sigue dando una página: un PDF sin hojas no es válido).

No se rasteriza nada en los tests: la plataforma headless de Avalonia dibuja en el vacío. Que la imagen se
vea bien es comprobación visual y está en `qa-manual-release-candidate.md`.
