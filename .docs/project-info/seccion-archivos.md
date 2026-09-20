# Sección Archivos

Referencia técnica de la pantalla "Archivos" (`AppPage.Files`): las dos disposiciones, navegación,
tarjetas, previsualización (imágenes, PDF, código y panel de información) y su relación con el resto del
vault. Documenta el comportamiento tal y como está implementado hoy, no un plan futuro.

## 1. Qué es y qué no es

"Archivos" es la sección del vault para navegar, importar, organizar y previsualizar cualquier archivo que
**no** sea una nota Markdown (`.md`). Vive junto a "Documentos" (Notas) como una segunda vista sobre el
mismo almacén: mismo sistema de carpetas en disco, misma papelera, mismo selector de sesión — solo cambia
qué extensión de archivo enumera cada una.

Piezas principales:

- `viewmodels/files/FilesViewModel.cs` — estado de la sección: sesión, carpeta actual, miga de pan,
  búsqueda, filtro, orden, tarjetas, previsualización activa, anterior/siguiente y todas las operaciones de
  escritura (importar, crear, renombrar, mover, borrar, copiar ruta).
- `viewmodels/files/FilesViewModel.Tree.cs` — parcial del modo árbol: forma del árbol, selección, teclado,
  búsqueda del panel y estado del panel lateral.
- `viewmodels/files/FileCardViewModel.cs` — un archivo: metadatos, miniatura, y lo que la previsualización
  carga bajo demanda (imagen completa, PDF, texto).
- `viewmodels/files/FileFolderViewModel.cs` — una fila del panel de carpetas del modo explorador.
- `viewmodels/files/FileFolderCardViewModel.cs` — una carpeta dibujada como tarjeta en la rejilla (incluida
  la ".."); también es la carga del modal de borrar carpeta.
- `viewmodels/files/FileTreeItemViewModel.cs` — un nodo del modo árbol (carpeta o archivo).
- `viewmodels/files/FileBreadcrumbViewModel.cs`, `FileSortOption.cs`, `FileKindFilterOption.cs` — miga de
  pan, opciones de orden y de filtro.
- `viewmodels/files/PdfDocumentViewModel.cs`, `PdfPageThumbnailViewModel.cs`, `PdfRenderHelper.cs`,
  `SvgRenderHelper.cs` — visor de PDF y rasterización de PDF/SVG.
- `views/files/FilesView.axaml` — contenedor: monta una de las dos disposiciones y aloja los modales de
  borrado (archivo y carpeta).
- `views/files/FilesBrowserView.axaml` — modo explorador (panel de carpetas + rejilla).
- `views/files/FilesTreeView.axaml` — modo árbol (árbol de carpetas y archivos + previsualización).
- `views/files/FilePreviewView.axaml` — la previsualización, compartida por las dos disposiciones.
- `services/VaultService.cs`, región "Archivos (binarios no-.md)" — el almacén real: enumerar (un nivel,
  `GetFiles`, o recursivo, `GetFilesRecursive`), contar entradas de una carpeta (`CountFolderEntries`),
  importar, renombrar, mover y borrar.
- `models/VaultFile.cs` — el modelo POCO de un archivo (ruta, tamaño, fecha, tipo por extensión).

No hay un `FilesService` separado: los métodos sobre archivos binarios viven en el mismo `VaultService`
que ya gestiona notas y carpetas, porque el almacén real es el propio sistema de archivos del vault y gran
parte de la infraestructura (`VaultPath`, `TrashPath`, `SanitizeFileName`, `GetFolderTree`, `ImportFile`)
ya era genérica.

## 2. Detección de tipo

`VaultFile.KindFromExtension` clasifica cada archivo por extensión (sin distinguir mayúsculas, sin mirar el
contenido) en un `VaultFileKind`:

- **Image**: `.png`, `.jpg`, `.jpeg`, `.gif`, `.bmp`, `.webp`, `.ico`, `.tiff`, `.tif` y `.svg` (este último
  rasterizado con `SvgRenderHelper`).
- **Pdf**: `.pdf`.
- **Audio** y **Video**: las extensiones habituales (`.mp3`, `.flac`, `.mp4`, `.mkv`…).
- **Code**: código y texto plano (`.cs`, `.py`, `.json`, `.yaml`, `.txt`, `.log`, `.csv`…).
- **Other**: todo lo demás.

`FileCardViewModel.IconKind` da un icono de archivo distinto por tipo (`FileImageOutline`, `FilePdfBox`,
`FileMusicOutline`, `FileVideoOutline`, `FileCodeOutline`, `FileOutline`), y `ExtensionLabel` la extensión en
mayúsculas que se pinta como etiqueta sobre la miniatura.

Que la extensión diga "texto" no garantiza que lo sea: el visor de código comprueba el contenido antes de
enseñarlo (§6.3).

## 3. Las dos disposiciones

La sección se navega de dos formas, elegidas con el conmutador (rejilla / árbol) y guardadas en
`AppSettings.FilesViewMode`:

- **Explorador** (`FilesBrowserView`): encabezado, barra de herramientas (importar, sesión, filtro por tipo,
  orden, búsqueda, contador, conmutador), panel de carpetas a la izquierda y rejilla de tarjetas con miga de
  pan. Abrir un archivo sustituye la pantalla entera por la previsualización.
- **Árbol** (`FilesTreeView`), la disposición "simplificada": un panel lateral que entremezcla carpetas y
  archivos, y la previsualización a la derecha, siempre al lado del árbol.

`FilesView` monta **una sola** de las dos (no las dos con `IsVisible`): cada una lleva la previsualización,
que instala TextMate y abre PDFium, y dos vivas duplicarían ambas cosas. El viewmodel es el mismo, así que
cambiar de modo no reinicia nada: la sesión, la carpeta actual y el archivo abierto siguen ahí.

El conmutador está en la barra del explorador, en la cabecera del panel derecho del árbol (sin archivo
abierto), en la barra de la previsualización (en las dos disposiciones) y en el raíl del panel contraído,
para que ningún estado de la pantalla se quede sin salida.

## 4. Navegación en el modo explorador

### 4.1 Sesiones

El selector de sesión es el mismo que usa "Documentos": la primera entrada es el vault completo
(`SessionOptionViewModel(null)`) y cada sesión acota la vista a su carpeta raíz. `EffectiveRootPath`
resuelve a la raíz de la sesión elegida o al vault; `CurrentFolderPath`, además, sobre `CurrentFolder`
(`null` equivale a la raíz efectiva).

La elección **se recuerda entre arranques** (`AppSettings.FilesSessionId`), con clave propia e independiente
de la de Notas. Una sesión guardada que ya no exista cae al vault en silencio; una cifrada se vuelve a elegir
aunque arranque bloqueada. Con una sesión protegida y bloqueada elegida la sección no lista ni nombres de
archivo, en ninguna de las dos disposiciones.

### 4.2 Carpetas, miga de pan y panel

- El **panel de carpetas** (`RootFolders`) es la misma jerarquía en disco que usa Documentos. La fila de la
  carpeta actual se marca (`FileFolderViewModel.IsSelected`) y las carpetas que llevan hasta ella se
  despliegan, venga de donde venga la navegación. Renombrar y borrar aparecen al pasar el cursor por la fila.
- La **miga de pan** (`Breadcrumbs`) enseña la raíz efectiva (el nombre de la sesión, o "Todos los
  archivos") y cada carpeta hasta la actual; cualquier tramo que no sea el último navega hasta él. El botón
  de subir un nivel (y Retroceso) llama a `NavigateUpCommand`.
- La **rejilla** (`BrowserItems`) enseña la tarjeta "..", las subcarpetas —cada una con cuántos elementos
  contiene (`VaultService.CountFolderEntries`: subcarpetas visibles más archivos que no son notas)— y los
  archivos.
- Al entrar en la sección, el shell llama a `FilesViewModel.Refresh()` (panel y rejilla), para que una
  carpeta creada desde Notas o desde el explorador del sistema aparezca también en el panel.

### 4.3 Búsqueda, filtro y orden

- **Búsqueda** (debounce de 200 ms): busca por nombre en la carpeta actual **y en todas sus subcarpetas**
  (`VaultService.GetFilesRecursive`, que excluye lo mismo que la navegación: notas, ocultos, `.trash`,
  `.sessions`). Cada resultado dice en qué subcarpeta vive (`FileCardViewModel.LocationLabel`) y la miga de
  pan se antecede de "Buscando en". Con búsqueda activa no hay tarjetas de carpeta.
- **Filtro por tipo**: sus opciones se reconstruyen con los tipos presentes en lo que se está viendo.
- **Orden** (`SortOptions`, guardado en `AppSettings.FilesSortMode`): más recientes (el de siempre),
  nombre, tamaño (más grandes primero) o tipo. Las carpetas van siempre delante y por nombre.

### 4.4 Estados vacíos

Cuatro, que no se confunden entre sí: vault o sesión sin nada (`ShowEmptyState`, con botones de importar y
de nueva carpeta), subcarpeta vacía (`ShowFolderEmptyState`), búsqueda sin resultados
(`ShowSearchEmptyState`, con el texto buscado) y sesión bloqueada (`IsSelectedSessionLocked`).

## 5. Tarjetas y miniaturas

Cada `FileCardViewModel` expone `FileName`, `ExtensionLabel`, `MetaLabel` (tamaño · fecha relativa),
`LocationLabel`, `KindLabel`, `IconKind` y `Thumbnail`.

**Carga perezosa**: `EnsureThumbnailLoaded()` se llama cuando la tarjeta se realiza en pantalla
(`FileCard_Initialized`) —nunca para todos los archivos de golpe— y corre fuera del hilo de UI, con como
mucho 2–4 decodificaciones a la vez (`ThumbnailGate`). Las imágenes rasterizadas se **reducen al
decodificar** a 320 px de ancho (`Bitmap.DecodeToWidth`, tras leer el tamaño de la cabecera con `SKCodec`):
decodificarlas a tamaño completo retenía cientos de MB en una carpeta de fotos. Los SVG pasan por
`SvgRenderHelper` y los PDF enseñan su primera página. Cualquier fallo deja el icono por tipo.

`UpdateFile` (renombrar, mover) incrementa una generación que descarta las decodificaciones en curso del
archivo anterior. No hay caché de miniaturas en disco: se recalculan en cada visita a la carpeta.

## 6. Previsualización

`OpenCard` reemplaza `ActivePreview` —un único hueco, sin pestañas: los archivos se visualizan, no se
editan— y carga bajo demanda lo que haga falta (`LoadPreviewContent`). La tarjeta abierta se **reutiliza**
en cada recarga de la rejilla y del árbol, para que importar, mover o cambiar el orden no cierre lo que hay
abierto.

**Barra** (58 px, alineada con la del panel del árbol): cerrar, archivo anterior/siguiente
(`PreviewPreviousCommand`/`PreviewNextCommand`, sobre los archivos de la rejilla con su orden y filtro, o
sobre los del árbol en orden de lectura), nombre y una línea de datos (tipo · tamaño · fecha, dimensiones de
la imagen o líneas del texto, y "3 / 12"), herramientas del tipo de archivo, "abrir con el visor del
sistema", un menú de más acciones (mostrar en el explorador, copiar ruta, eliminar) y el conmutador de
navegación.

**Foco**: en el modo explorador la previsualización toma el foco al abrirse, para que sus atajos funcionen
sin pulsar antes dentro; en el modo árbol el foco se queda en el árbol, que se recorre con las mismas
flechas (§8).

### 6.1 Imágenes

`EnsurePreviewImageLoaded` decodifica la imagen fuera del hilo de UI, con un tope de 4096 px de ancho (los
SVG, a 1600 px); mientras tanto se enseña la miniatura (`DisplayImage`). Se ajusta a la ventana **sin
ampliar** por encima de su tamaño (`StretchDirection="DownOnly"`), o a tamaño real con desplazamiento
(`IsImageActualSize`). Una imagen que no se puede decodificar va al panel de información.

### 6.2 PDF

Ver §7.

### 6.3 Código y texto

`EnsureCodeContentLoaded` lee el archivo y, antes de dárselo al editor:

- descarta los de más de 32 MB (panel de información, "demasiado grande");
- detecta binarios con extensión de texto (`LooksBinary`: un byte nulo en los primeros 8000 bytes, salvo
  con marca de orden de bytes UTF-16) — panel de información;
- decodifica respetando la marca de orden de bytes (UTF-8/UTF-16);
- trunca a 512 000 caracteres (`IsCodeTruncated`), avisándolo en una franja sobre el editor — antes se
  añadía una línea inventada al final del propio texto, que se copiaba con él.

El editor (AvaloniaEdit + TextMate, solo lectura, con números de línea) va **sin** `ScrollViewer`
alrededor: trae el suyo y virtualiza las líneas. El tema de resaltado sigue al de la app (LightPlus o
DarkPlus, también al cambiarlo en caliente) y la gramática se resuelve por extensión, también cuando un
renombrado cambia la extensión del archivo abierto; una extensión sin gramática queda sin resaltado. La barra
trae el lenguaje, ajuste de líneas largas y copiar el contenido (el icono pasa a un check un momento).

### 6.4 Panel de información

Audio, vídeo, tipos sin visor, texto binario/ilegible e imágenes que no se pueden decodificar enseñan una
ficha (`ShowsInfoPanel`): icono y extensión, nombre, tipo y datos, el motivo (`PreviewHint`) y dos salidas —
reproducir/abrir con la aplicación del sistema y mostrar en la carpeta. Nunca un error sin salida.

"Abrir con el sistema" (`OpenWithSystem`) entrega una copia descifrada temporal cuando el archivo vive en una
sesión protegida, y avisa si el sistema no tiene aplicación asociada. Esa copia
(`VaultService.ResolveReadablePath`) no se escribe en la raíz del directorio temporal del sistema, que es
compartido por todas las cuentas de la máquina, sino en el directorio privado por usuario de
`PrivateTempFile` (`services/PrivateTempFile.cs`), creado con modo `0700`; y el archivo nace ya con modo
`0600` —`FileMode.CreateNew` más `FileStreamOptions.UnixCreateMode`— antes del primer byte de contenido, no
con un `chmod` posterior. El barrido de arranque y cierre (`CleanupStaleDecryptedTempFiles`) mira los dos
sitios, para seguir limpiando lo que dejaran versiones anteriores sueltas en la raíz del temporal.

## 7. Visor de PDF

`EnsurePdfDocumentLoaded` crea un `PdfDocumentViewModel` por tarjeta y arranca `LoadAsync()`, que resuelve
una sola vez la ruta legible (copia descifrada si hace falta), lee el número de páginas y **el tamaño de cada
página** (`PdfRenderHelper.GetPageSizes`, en puntos) y renderiza la primera. Cualquier fallo marca
`IsFaulted` y la vista ofrece abrir el archivo con el sistema.

**Zoom**: se mide contra el tamaño físico de la página (100 % = tamaño en papel a 96 DIP por pulgada). El
documento se abre **ajustado al ancho** del panel (`IsFitWidth`), que la vista informa con
`SetViewport(ancho, escala)` —con un debounce de 120 ms al redimensionar— y que se recalcula por página (un
anexo apaisado se ajusta a su propio ancho). Elegir un zoom concreto apaga el ajuste; los escalones son fijos
(25 %–400 %), así que desde un ajuste de 83 % se llega a un valor redondo. Ctrl + rueda, Ctrl + +/−, el
botón del porcentaje (100 %) y el de ajustar al ancho (Ctrl + 0).

**Render**: cada cambio de página o de zoom pide a PDFium la página al DPI que corresponde **multiplicado
por la escala de la pantalla**, así que en HiDPI no se ve borrosa. El tamaño con que se dibuja
(`CurrentPageDisplayWidth/Height`) es independiente de los píxeles del bitmap: mientras llega el render nuevo
se estira el anterior, y el lado largo de un render se limita a 6000 px para no reservar cientos de MB en
aumentos extremos. Un token (`_renderToken`) descarta renders adelantados por otro cambio.

**Página**: al cambiar de página el visor vuelve arriba y la miniatura actual del raíl se trae a la vista
(`CurrentPageChanged`). El raíl usa `VirtualizingStackPanel` y renderiza cada miniatura perezosamente a 220 px.

Cada llamada a `PdfRenderHelper` abre su propio `FileStream` de corta vida: PDFium serializa sus llamadas, así
que no se gana nada manteniendo el archivo abierto, y un PDF escaneado grande nunca se carga entero en memoria.
No se usa `NativeWebView`: su backend en Linux es software-only y propenso a fallos.

## 8. Modo árbol

- **Forma**: carpetas y archivos en una sola lista jerárquica, subcarpetas primero y archivos por nombre.
  Cuelga de la raíz efectiva entera; ni el filtro, ni el orden, ni la carpeta actual del explorador lo podan.
  Solo se mantiene al día mientras el modo está activo y se reconstruye al entrar.
- **Selección**: un único nodo seleccionado (`FileTreeItemViewModel.IsSelected`) — el archivo abierto o la
  última carpeta pulsada. Abrir un archivo desde cualquier sitio lo selecciona y despliega sus carpetas.
- **Teclado** (con el foco en el cuerpo del árbol): ↑/↓, RePág/AvPág, Inicio/Fin mueven la selección entre
  los nodos visibles —un archivo se abre al llegar a él—; → despliega o baja al primer hijo; ← pliega o sube
  a la carpeta padre; Intro abre o pliega; F2 renombra; Supr borra. Pulsar una fila le da el foco al árbol.
- **Búsqueda del panel**: filtra en memoria lo ya montado, con la carpeta de cada resultado; Intro abre el
  primero y Esc vuelve al árbol.
- **Pie**: selector de sesión y el número de archivos de **todo el árbol** (`TreeFilesCountLabel`).
- **Menú contextual por tipo de nodo**: en archivos, abrir con el sistema, renombrar, mostrar en el
  explorador, copiar ruta y eliminar; en carpetas, crear subcarpeta, importar aquí, renombrar, mostrar en el
  explorador, copiar ruta y eliminar.
- **Panel lateral**: plegable (`AppSettings.FilesTreeSidebarVisible`), redimensionable
  (`FilesTreeSidebarWidth`) y con su propio estado de carpetas expandidas (`FilesTreeExpandedFolderPaths`),
  independiente del panel del explorador.

## 9. Importar archivos

Todo converge en `FilesViewModel.ImportPaths(rutas, destino)`, que llama a `VaultService.ImportFile` por
archivo (copia, no mueve; colisiones con sufijo numérico; cifra si el destino es una sesión protegida). Cada
archivo va en su propio `try`/`catch` y se avisa una vez por gesto si alguno falla, sin dejar de intentar el
resto. En el modo árbol, importar un único archivo lo abre directamente en la previsualización.

Entradas:

- **Botón o menú "Importar"** (`ImportCommand`), con destino la carpeta actual o la carpeta del menú
  ("Importar aquí…" en filas, tarjetas de carpeta y nodos del árbol).
- **Arrastrar desde el explorador del SO** (`DataFormat.File`). En el modo explorador: la rejilla importa en la
  carpeta que se ve; una fila de carpeta, una tarjeta de carpeta (incluida la "..") o la fila "TODOS", ahí
  dentro. En el modo árbol: una fila de carpeta, ahí dentro; el fondo del árbol y el resto de la vista
  (incluida la previsualización), en la raíz efectiva.

Los destinos se encadenan por burbujeo —el más específico que acepta marca el evento como manejado— y el
realce sigue el mismo criterio. Con una sesión bloqueada elegida ningún destino acepta el gesto.

## 10. Organizar: carpetas y archivos

Todas las escrituras pasan por `TryVaultOperation`: un fallo de disco (permisos, archivo en uso, sesión
bloqueada, vault desaparecido) se avisa con `NotifyFilesFailure` en vez de escapar como excepción desde un
comando o un manejador de arrastre.

- **Crear carpeta**: "Nueva carpeta" del explorador crea en la carpeta actual y abre el renombrado en su
  tarjeta; "Crear subcarpeta" de una fila despliega la fila padre para que el cuadro de renombrar sea visible.
- **Renombrar** (en línea en filas, tarjetas y nodos; F2): colisiones con sufijo numérico. Al renombrar un
  archivo se selecciona solo el nombre, sin la extensión. `RenameFolderAt` traslada a la ruta nueva todo lo
  que apuntaba a la vieja (`_folderRemap`): la carpeta actual, las carpetas expandidas de los dos árboles, la
  selección del árbol y el archivo abierto, que se recarga desde su ruta nueva.
- **Borrar carpeta**: siempre pasa por `RequestDeleteFolder` (fila, tarjeta o nodo), que pide confirmación si
  `ConfirmBeforeDelete` está activo. La carpeta se mueve a `<vault>/.trash`; como la sección Papelera solo
  lista archivos sueltos, el modal lo dice. Si contenía el archivo abierto o la carpeta actual, se cierran.
- **Borrar archivo**: confirmación igual, a la papelera del vault (restaurable desde Papelera). Si era el
  archivo abierto, se abre el siguiente (o el anterior) en su lugar.
- **Mover** (arrastrar una tarjeta o fila sobre una carpeta; formato interno `com.enki.file-path`, el mismo
  en las dos disposiciones): `MoveFileToFolder`. Si el archivo movido es el que está abierto, la
  previsualización se recarga desde la ruta nueva — antes el visor de PDF seguía leyendo la vieja y las
  páginas dejaban de pasar.
- **Copiar ruta** (archivos y carpetas) y **mostrar en el explorador del sistema**.

Qué destino acepta qué vive en el code-behind de las vistas; lo cubre `FilesDropTargetUiTests`.

## 11. Atajos de teclado

| Dónde | Tecla | Acción |
| --- | --- | --- |
| Rejilla | Retroceso | Subir un nivel |
| Tarjeta con foco | F2 / Supr | Renombrar / eliminar |
| Árbol con foco | ↑ ↓ RePág AvPág Inicio Fin | Mover la selección |
| Árbol con foco | → / ← | Desplegar o bajar / plegar o subir |
| Árbol con foco | Intro / F2 / Supr | Abrir o plegar / renombrar / eliminar |
| Búsqueda del árbol | Intro / Esc | Abrir el primer resultado / volver al árbol |
| Previsualización | Esc | Cerrar |
| Previsualización | Alt + ← / Alt + → | Archivo anterior / siguiente |
| PDF | ← → RePág AvPág / Inicio Fin | Página anterior o siguiente / primera o última |
| PDF | Ctrl + + / Ctrl + − / Ctrl + 0 / Ctrl + rueda | Acercar / alejar / ajustar al ancho / zoom |

## 12. Estilos visuales

En `styles/Styles.axaml`:

- `Button.file-card` — misma familia visual que `Button.note-card`; con `:focus-visible` y `.drag-over`
  (tarjeta de carpeta bajo un arrastre válido) el borde pasa a `InkBrush`.
- `Border.file-ext-badge` — etiqueta monoespaciada de extensión (tarjetas, ficha, lenguaje del visor).
- `Border.folder-row Button.row-action` — acciones de fila visibles al pasar el cursor, en la fila activa o
  con el foco.
- `Button.breadcrumb` (y `.current`) — tramos de la miga de pan.
- `Border.files-grid-panel.drag-over` y `Button.pdf-page-thumb` — rejilla bajo un arrastre del SO y
  miniaturas del raíl del PDF.

Todo con `CornerRadius="0"`, según `docs/sistema-diseno-web.md`.

## 13. Tests

- `enki.Tests/ViewModels/FilesViewModelBrowserTests.cs` — miga de pan y panel, búsqueda recursiva, orden,
  estados vacíos, crear/renombrar/borrar carpetas, mover y borrar con la previsualización abierta,
  anterior/siguiente, contador y teclado del árbol.
- `enki.Tests/ViewModels/FilesViewModelTreeTests.cs` — forma del árbol, búsqueda del panel, estado persistido.
- `enki.Tests/ViewModels/FileCardViewModelTests.cs` — binarios, codificación, truncado, panel de información.
- `enki.Tests/ViewModels/FilesViewModel{DeleteConfirmation,Encryption,SessionPersistence}Tests.cs`.
- `enki.Tests/Services/VaultServiceFilesTests.cs` — enumeración recursiva y contador de carpeta.
- `enki.UITests/Files{View,TreeView,DropTarget,SectionPolish}UiTests.cs` — XAML compilado de verdad: las dos
  disposiciones, destinos de arrastre, miga de pan, modal de carpeta y visores.

## 14. Qué NO hace esta sección

- **No edita archivos**: solo el nombre. No hay editor de imágenes ni anotación de PDF.
- **No reproduce audio ni vídeo** dentro de la app: los entrega al reproductor del sistema.
- **No hay selección múltiple**: cada operación actúa sobre un archivo o una carpeta.
- **No enlaza notas con archivos** vía wikilinks; la relación con Documentos es estructural (mismas carpetas,
  mismo selector de sesión).
- **No hay caché de miniaturas persistente** (§5).
- **La sección Papelera no lista carpetas borradas**: quedan en `<vault>/.trash` y se recuperan a mano (§10).
