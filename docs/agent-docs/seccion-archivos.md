# Sección Archivos

Referencia técnica de la pantalla "Archivos" (`AppPage.Files`): navegación, tarjetas de archivo,
visor de PDF y su relación con el resto del vault. Documenta el comportamiento tal y como está
implementado hoy, no un plan futuro.

## 1. Qué es y qué no es

"Archivos" es la sección del vault para navegar, importar y previsualizar cualquier archivo que
**no** sea una nota Markdown (`.md`). Vive junto a "Documentos" (Notas) como una segunda vista sobre
el mismo almacén: mismo sistema de carpetas en disco, misma papelera, mismo selector de sesión — solo
cambia qué extensión de archivo enumera cada una.

Piezas principales:

- `enki/src/enki/viewmodels/files/FilesViewModel.cs` — estado de la pantalla (carpeta actual,
  búsqueda, sesión activa, colección de tarjetas, previsualización activa).
- `enki/src/enki/viewmodels/files/FileCardViewModel.cs` — una tarjeta de archivo en la rejilla
  (miniatura, icono, metadatos, rename inline).
- `enki/src/enki/viewmodels/files/FileFolderViewModel.cs` — una fila del árbol de carpetas.
- `enki/src/enki/viewmodels/files/PdfDocumentViewModel.cs` — estado del visor de PDF multipágina
  (página actual, zoom, rail de miniaturas de página).
- `enki/src/enki/viewmodels/files/PdfPageThumbnailViewModel.cs` — una miniatura de página dentro del
  rail del visor.
- `enki/src/enki/viewmodels/files/PdfRenderHelper.cs` — envoltorio sobre PDFtoImage/PDFium que
  rasteriza páginas de PDF a `Bitmap` de Avalonia.
- `enki/src/enki/views/files/FilesView.axaml` / `.axaml.cs` — la vista: rejilla de tarjetas, árbol de
  carpetas, modo previsualización, arrastrar-y-soltar.
- `enki/src/enki/services/VaultService.cs`, región "Archivos (binarios no-.md)" (línea ~714 en
  adelante) — el almacén real: enumerar, importar, renombrar, mover y borrar archivos sobre el
  sistema de archivos del vault.
- `enki/src/enki/models/VaultFile.cs` — el modelo POCO de un archivo (ruta, tamaño, fecha de
  modificación, tipo detectado por extensión).

No hay un `FilesService` separado: los métodos sobre archivos binarios viven en el mismo
`VaultService` que ya gestiona notas y carpetas, porque el almacén real es el propio sistema de
archivos del vault y gran parte de la infraestructura (`VaultPath`, `TrashPath`, `SanitizeFileName`,
`GetFolderTree`, `ImportFile`) ya era genérica pese a vivir junto a los métodos de notas. Solo la
lectura de metadatos difiere: `ReadFileMetadata` es `FileInfo` puro, sin parsear contenido como hace
`ReadNoteMetadata`.

## 2. Detección de tipo

`VaultFile.KindFromExtension` clasifica cada archivo en uno de tres `VaultFileKind`, por extensión
(case-insensitive, sin sniffing de magic bytes):

- **Image**: `.png`, `.jpg`, `.jpeg`, `.gif`, `.bmp`, `.webp`.
- **Pdf**: `.pdf`.
- **Other**: cualquier otra extensión — incluido `.svg` deliberadamente, porque el `Bitmap` de
  Avalonia no decodifica vectorial sin sumar una dependencia de renderizado SVG nueva.

`FileCardViewModel.IconKind` usa este tipo para elegir un icono fijo (`FilePdfBox` para PDF,
`FileOutline` para el resto) cuando no hay miniatura decodificada.

## 3. Navegación por carpetas y sesiones

El árbol de carpetas (`FilesViewModel.RootFolders`) es la **misma jerarquía de carpetas en disco**
que usa "Documentos": `FileFolderViewModel` envuelve el mismo modelo `NoteFolder` que
`NoteFolderViewModel`, no un concepto propio. Es una clase separada solo porque sus `[RelayCommand]`
y los bindings de la vista alrededor están acoplados por tipo a `FilesViewModel`, no porque el
concepto de carpeta difiera entre ambas pantallas.

El selector de sesión (`SessionOptions`, arriba de la rejilla) es el mismo que usa "Documentos": la
primera entrada es el vault completo (`SessionOptionViewModel(null)`), y cada sesión adicional acota
la vista a su propia carpeta raíz (`Session.RootPath`). Un archivo soltado dentro de una sesión "X" es
visible tanto desde Documentos como desde Archivos sin ningún concepto nuevo — comparten carpetas
físicas en disco.

`EffectiveRootPath` resuelve a `SelectedSessionOption.Session.RootPath` si hay una sesión elegida, o
al `VaultService.VaultPath` si la selección es "vault completo". `CurrentFolderPath` resuelve además
sobre `CurrentFolder`, que puede ser `null` (equivalente a "TODOS", la raíz efectiva).

Cambiar de sesión, de carpeta o de texto de búsqueda dispara `Reload()`, que llama a
`VaultService.GetFiles(carpetaActual)` — enumeración de un único nivel (`SearchOption.TopDirectoryOnly`),
no recursiva — y filtra en memoria por `FileName.Contains(SearchText, OrdinalIgnoreCase)`. `GetFiles`
excluye explícitamente cualquier archivo `.md`: esa es la frontera entre lo que ve "Documentos" y lo
que ve "Archivos" sobre el mismo directorio.

El texto de búsqueda usa un debounce de 200 ms (`_searchDebounceTimer`) antes de recargar, igual que
el resto de pantallas con búsqueda del proyecto.

## 4. Sin sistema de pestañas

A diferencia de "Documentos" (que mantiene varias notas abiertas en pestañas simultáneas), "Archivos"
no tiene tira de pestañas. Abrir un archivo (`OpenCard`) simplemente reemplaza
`FilesViewModel.ActivePreview` por la tarjeta elegida — un único slot de previsualización, no una
colección. La razón documentada en el propio código: los archivos aquí solo se **visualizan**, nunca
se editan, así que no existe "trabajo en curso" que preservar al cambiar de uno a otro, a diferencia
del editor de notas.

`ActivePreview == null` selecciona el modo "rejilla" (navegación); `ActivePreview != null` selecciona
el modo "previsualización" (`FilesView.axaml` alterna entre dos `Grid` raíz con
`IsVisible={Binding ActivePreview, Converter=...IsNull/IsNotNull}`).

## 5. Tarjetas de archivo y miniaturas

Cada `FileCardViewModel` envuelve un `VaultFile` y expone:

- `FileName`, `SizeLabel`, `ModifiedLabel` — metadatos formateados (tamaño humano, fecha relativa).
- `IsImage` / `IsPdf` / `IsOther` — atajos sobre `VaultFile.Kind`.
- `IconKind` — icono Material fijo cuando no hay miniatura.
- `Thumbnail` (`Bitmap?`) — la miniatura decodificada, `null` hasta que se carga.
- `PdfDocument` (`PdfDocumentViewModel?`) — solo para PDFs, `null` hasta que se abre la
  previsualización por primera vez.

**Carga perezosa, no eager**: `EnsureThumbnailLoaded()` se llama desde el evento `Initialized` de cada
tarjeta en `FilesView.axaml.cs` (`FileCard_Initialized`), es decir, la primera vez que la tarjeta se
realiza visualmente en pantalla — no para las cientos de tarjetas que podría haber en una carpeta
grande. Guardada por `_thumbnailLoadAttempted` para no repetir el intento.

La decodificación corre en un `Task.Run` fuera del hilo de UI: para imágenes es un `Bitmap` directo
desde el `FileStream`; para PDFs es la primera página rasterizada vía `PdfRenderHelper.RenderPageByWidth`
a un ancho fijo de 200px (`GridThumbnailWidth`), así que la rejilla de "Archivos" muestra una vista
real de la primera página en vez de un icono genérico también para PDFs. Cualquier excepción durante
la decodificación dejo la miniatura en `null` (vuelve a mostrarse el icono fijo), sin propagar el
error.

**Sin caché en disco**: cada apertura de carpeta vuelve a decodificar sus miniaturas desde cero — no
hay ningún archivo de caché de thumbnails persistido. Es una decisión deliberada documentada en el
código: decodificar al vuelo es barato mientras el número de archivos por carpeta se mantenga
razonable, y añadir una caché antes de que eso sea un problema real sería complejidad sin uso.

## 6. Visor de PDF

Abrir la previsualización de un PDF (`OpenCard` → `EnsurePdfDocumentLoaded`) crea un
`PdfDocumentViewModel` una única vez por tarjeta (se conserva mientras dure la sesión de la app, así
que reabrir el mismo PDF no vuelve a renderizarlo desde cero) y arranca `LoadAsync()`.

`LoadAsync`:

1. Obtiene el número de páginas vía `PdfRenderHelper.GetPageCount`.
2. Crea un `PdfPageThumbnailViewModel` por página (rail lateral).
3. Marca la primera página como actual y renderiza su imagen a tamaño completo.
4. Si algo falla en el camino (incluido un PDF con 0 páginas), marca `IsFaulted = true` en vez de
   propagar la excepción; la vista muestra entonces un estado de error con un botón para abrir el
   archivo con la aplicación del sistema.

**Render de la página actual** (`RenderCurrentPageAsync`): usa `PdfRenderHelper.RenderPageByDpi`, con
un DPI calculado como `130 (BaseDpi) * ZoomFactor`. El zoom real (0.5×–3.0×, pasos de 0.25) por tanto
no es un escalado de un bitmap ya renderizado — cada cambio de zoom vuelve a pedirle a PDFium más (o
menos) detalle, así que ampliar nunca produce una imagen borrosa por reescalado.

Cada render lleva un token incremental (`_renderToken`): si una página o zoom más reciente ya se
solicitó antes de que termine un render anterior, ese resultado obsoleto se descarta en vez de
parpadear brevemente en pantalla.

**Rail de miniaturas de página**: cada `PdfPageThumbnailViewModel` se renderiza también de forma
perezosa (`EnsurePageThumbnailLoaded`, llamado desde `PdfPageThumb_Initialized` en el code-behind), a
un ancho fijo de 120px. El `ItemsControl` del rail usa un `VirtualizingStackPanel`, así que un PDF de
cientos de páginas nunca renderiza más de un puñado de miniaturas a la vez — solo las que caen cerca
del viewport.

**Motor de renderizado**: `PdfRenderHelper` es un envoltorio fino sobre el paquete NuGet
`PDFtoImage` (PDFium + SkiaSharp; referenciado en `enki.csproj`). El propio código documenta por qué
no se usó la ruta `NativeWebView` de Avalonia como visor alternativo: el backend WebView de
Linux/WPE es software-only y propenso a fallos según la documentación de `SessionGraphRenderer.cs`
del propio proyecto, mientras que PDFium da un render determinista dentro de la app en todas las
plataformas que Enki soporta, sin necesitar comprobar disponibilidad de motor. Cada llamada
(`GetPageCount`, `RenderPageByWidth`, `RenderPageByDpi`) abre su propio `FileStream` de corta vida en
vez de cachear los bytes del PDF en memoria: PDFium serializa sus llamadas internamente (no soporta
paralelización según su propia documentación), así que no se gana nada manteniendo el archivo abierto
o sus bytes residentes, y un PDF escaneado grande nunca tiene que cargarse entero en memoria solo
para renderizar una página.

**Atajos de teclado del visor**: flechas izquierda/derecha y RePág/AvPág cambian de página
(`Grid.KeyBindings` en `FilesView.axaml`, atados a `PreviousPageCommand`/`NextPageCommand`); sin
efecto fuera del visor de PDF porque `PdfDocument` es `null` en cualquier otro caso y Avalonia trata
un `Command` nulo como deshabilitado.

**Fallback universal**: cualquier archivo que no sea imagen ni PDF (`IsOther`, además del propio PDF
si `IsFaulted`) muestra un icono, el nombre del archivo y un botón "abrir con la aplicación del
sistema" — nunca un error duro sin salida.

## 7. Estilos visuales

`enki/src/enki/styles/Styles.axaml` define las clases visuales de esta sección:

- `Button.file-card` — copia exacta de las reglas de `Button.note-card`, misma familia visual que las
  tarjetas de nota; solo cambia el contenido (miniatura/icono + nombre + tamaño + fecha en vez de
  título/preview/palabras). `CornerRadius="0"` como el resto del sistema de diseño.
- `Border.files-grid-panel.drag-over` — resalta el borde de la rejilla completa con `InkBrush` durante
  un arrastre de archivos válido desde el explorador del SO; mismo lenguaje visual que
  `Border.folder-row.drag-over`.
- `Button.pdf-page-thumb` — miniatura de página en el rail del visor; mismo lenguaje de "borde que se
  resalta al activarse" que `Border.folder-row.active`, sobre un `Button` (no un `Border`) porque cada
  miniatura es clicable.

## 8. Importar archivos

Hay dos caminos de entrada, ambos convergen en el mismo método:

- **Botón "IMPORTAR"** (`FilesViewModel.Import`, `[RelayCommand]`): abre un selector de archivos del
  sistema (`IStorageProvider.OpenFilePickerAsync`, multi-selección) y pasa las rutas locales
  resueltas a `ImportPaths`.
- **Arrastrar y soltar desde el explorador del SO**: `FilesGrid_DragEnter/DragOver/DragLeave/Drop` en
  `FilesView.axaml.cs`, usando el formato `DataFormat.File` que expone `DataTransfer` de Avalonia para
  ítems soltados desde fuera de la app (a diferencia de los formatos internos usados para
  reordenar/mover archivos ya presentes en la rejilla — ver §9). No hay precedente de este patrón en
  ninguna otra pantalla: Notas/Kanban/Calendario solo manejan arrastre *interno*.

`ImportPaths` (punto de entrada compartido) llama a `VaultService.ImportFile(ruta, carpetaActual)`
por cada archivo, y recarga la rejilla si al menos uno se importó. `VaultService.ImportFile` copia el
archivo (no lo mueve) a la carpeta destino, resolviendo colisiones de nombre con un sufijo numérico
(`nombre 2.ext`, `nombre 3.ext`, ...).

## 9. Organizar: carpetas, mover, renombrar, borrar

Todas las operaciones de escritura viven en `FilesViewModel` como `[RelayCommand]` que delegan en
`VaultService` y luego recargan el estado local:

- `NewFolder` → `VaultService.CreateFolder`, entra directamente en modo renombrar sobre la carpeta
  recién creada.
- `CommitRenameFolder` / `CommitRenameFile` → `VaultService.RenameFolder` / `RenameFile`; ambos
  añaden un sufijo numérico si el nombre destino ya existe, igual que el resto de operaciones de
  renombrado del vault (notas incluidas) — nunca hacen un "ghost rename" silencioso.
- `DeleteFolder` / `DeleteFile` → mueven a la papelera del vault (`VaultService.TrashPath`,
  `<vault>/.trash`), la misma papelera que usan las notas. Si la carpeta/archivo borrado afectaba a la
  carpeta o previsualización actualmente activa, ambas se limpian antes de recargar.
- `MoveFileToFolder` (llamado desde el drop de un archivo sobre una fila de carpeta o sobre "TODOS")
  → `VaultService.MoveFile`.

**Arrastrar para mover** (`FileCard_PointerPressed`/`PointerMoved` en `FilesView.axaml.cs`): un
arrastre interno de una tarjeta usa un formato de datos propio (`com.enki.file-path`,
`DataFormat.CreateStringApplicationFormat`) para transportar la ruta completa del archivo, distinto
del `DataFormat.File` usado para arrastres entrantes desde el SO (§8). El umbral de inicio de arrastre
es de ~4px de movimiento tras pulsar. Las filas de carpeta y la fila raíz "TODOS" son objetivos de
drop válidos únicamente cuando el `DataTransfer` contiene ese formato interno.

Los archivos borrados van a la misma papelera del vault que usa `TrashViewModel`
(`VaultService.GetTrashedFiles`), junto a notas y otros elementos borrables — no hay una papelera
separada por tipo de contenido.

## 10. Qué NO hace esta sección

Por completitud, verificado leyendo el código:

- **No hay edición de archivos.** La sección es de solo lectura/visualización sobre el archivo (salvo
  el nombre, que sí es editable). No hay editor de imágenes ni anotación de PDF.
- **No hay integración con wikilinks.** A diferencia de las notas (que sí resuelven `[[enlaces]]`
  entre sí vía `WikiLinkParser`), no se encontró ningún mecanismo que enlace una nota a un archivo
  binario del vault ni viceversa (ni incrustación de imágenes en notas, ni referencia inversa desde un
  archivo a las notas que lo mencionan). La única relación entre "Documentos" y "Archivos" es
  estructural: comparten el mismo árbol de carpetas en disco y el mismo selector de sesión (§3).
- **No hay caché de miniaturas persistente** (§5): se recalculan en cada visita a la carpeta.
