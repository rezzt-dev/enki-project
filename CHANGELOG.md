# Changelog

Todos los cambios notables de Enki se documentan en este fichero. El formato sigue a grandes rasgos
[Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/), y el versionado sigue
[SemVer](https://semver.org/lang/es/) (`MAJOR.MINOR.PATCH`) mientras la app se mantenga en `0.x` — es
decir, cualquier cambio puede romper compatibilidad entre versiones menores hasta llegar a `1.0.0`.

## SIN PUBLICAR / UNRELEASED

### CASTELLANO

#### ANADIDO
- captura rapida global desde el icono de bandeja, la paleta de comandos, un atajo local y el atajo global remapeable `ctrl+alt+espacio`: abre una mini-ventana independiente, conserva el borrador al cancelar y crea directamente una nota en `Inbox/` o una tarjeta en la columna Kanban elegida, sin traer al frente la ventana principal.
- backends nativos del atajo global para Windows (`RegisterHotKey`), Linux X11 (`XGrabKey`) y macOS (`RegisterEventHotKey`), con degradacion explicita en Wayland, conflictos y plataformas no compatibles; ajuste opcional para mantener Enki en la bandeja al cerrar la ventana principal.
- edicion markdown profesional sobre `MarkdownEditing`: alternar formato en linea y marcadores de bloque, aplicar acciones a selecciones multilínea, anidar y renumerar listas, continuar listas y citas con intro, inicio inteligente, borrado de marcadores, pares automaticos configurables, pegado de urls como enlaces y operaciones para mover, duplicar, borrar o ampliar la seleccion por lineas, todo integrado con atajos remapeables y acciones deshacibles completas.
- resaltado de sintaxis para bloques de codigo con lenguaje declarado mediante las gramaticas textmate y los temas claro/oscuro que ya usa el visor de archivos, cargado en segundo plano, almacenado por bloque y limitado por tiempo y numero de lineas para no bloquear la escritura.
- avisos compatibles con la sintaxis de obsidian (`> [!note]`, `tip`, `warning`, `danger` y sus alias), con icono, titulo, tono y fondo propios del tema, mas el comando `/aviso` para insertarlos sin memorizar la sintaxis.
- controles en ajustes > editor para elegir entre alineacion justificada o en bandera y activar o desactivar el cierre automatico de parentesis y comillas, persistidos por vault y traducidos a los siete idiomas soportados.
- plantillas de notas reutilizables en `<vault>/.enki/templates/`: cada plantilla es un archivo markdown con frontmatter opcional (`name`, `description`, `title`) y un cuerpo con variables `{{title}}`, `{{date}}`, `{{time}}`, `{{datetime}}`, `{{weekday}}`, `{{month}}`, `{{year}}`, `{{uuid}}` y `{{cursor}}`, con formato (`{{date:dd/MM/yyyy}}`) y desplazamiento (`{{date+1d}}`) opcionales; lo que no se reconoce se deja intacto en la nota.
- flujo "desde plantilla" en documentos —boton de la vista completa, fila de acciones de la vista simplificada, menu contextual de la rejilla y paleta de comandos— con selector para crear la nota, editar la plantilla en una pestana del editor, crear una plantilla nueva ya rellena con la sintaxis, abrir su carpeta y eliminarla respetando "preguntar antes de borrar".
- ajuste "plantilla de la nota diaria" en ajustes > editor > plantillas, guardado por vault en `<vault>/.enki/settings.json`: la plantilla decide el contenido y, si declara `title`, tambien el nombre de la nota; solo se aplica al crearla, nunca sobre la nota del dia que ya existe.
- siembra de dos plantillas de ejemplo traducidas (nota diaria y reunion) la primera vez que se abre cada vault, sin volver a reponerlas si el usuario las borra.
- navegacion de dia anterior y dia siguiente en la cabecera del editor cuando la nota abierta es una nota diaria, con la fecha escrita para leerse, vuelta a hoy y atajos remapeables `ctrl+alt+izquierda` y `ctrl+alt+derecha`; el dia destino se crea con su plantilla si aun no existe.
- reconocer la nota diaria por el nombre del archivo en los dos sentidos (fecha a nombre y nombre a fecha), respetando el `title` de la plantilla configurada y sin dejar de reconocer las notas `yyyy-MM-dd` anteriores a configurarla, de modo que un dia nunca acaba con dos notas diarias.
- accion "combinar con la plantilla" en la cabecera del editor que anade a la nota diaria solo las secciones que le faltan, al final y sin tocar lo ya escrito, con el detalle de lo que se va a anadir en el tooltip antes de pulsar y un punto de restauracion en el historial de versiones.
- calendario de constancia de la nota diaria en documentos (vista completa, vista simplificada y paleta de comandos): diez semanas alineadas a semanas naturales, una celda por dia, pulsar una celda abre o crea la nota de ese dia, y un unico dato descriptivo de cuantos dias tienen nota.
- fila de habitos dentro de ese mismo calendario, leida de los habitos reales del vault y con accesos a habitos y estadisticas, sin rachas, puntos ni objetivos de ningun tipo.
- historial local de versiones por nota, guardado dentro del propio vault (`<vault>/.enki/history/` o, para una nota de sesion, `.history/` junto a ella, heredando su cifrado si la sesion esta protegida): snapshots automaticos durante el autoguardado (limitados a uno cada 5 minutos y solo si el contenido cambio), pestana "Historial" en el editor para comparar linea a linea con la version actual y restaurar, y un snapshot de seguridad automatico antes de restaurar o antes de que un renombrado reescriba wikilinks en otras notas.
- anadir la seccion "Etiquetas" a la barra lateral para consultar y buscar en una sola vista las etiquetas activas de notas, tarjetas kanban y tareas polar, con recuentos por modulo y seleccion multiple para fusionarlas.
- permitir renombrar, fusionar y eliminar etiquetas en los tres modulos sin crear otro almacen: conservar el frontmatter y el historial de las notas, evitar duplicados por entidad, sincronizar los cambios de polar y actualizar las pestanas de notas abiertas.
- ampliar el grafo global del vault con filtros combinables por sesion y etiquetas para explorar las notas accesibles sin crear otro almacen ni sustituir el renderizador existente.
- anadir modos para descubrir notas huerfanas, conexiones entre sesiones y grupos de notas conectadas, calculados sobre el vault accesible completo para evitar falsos resultados al filtrar.
- deteccion de cambios hechos sobre el vault fuera de enki (syncthing, dropbox, otro editor): `VaultChangeWatcher` vigila la carpeta con un `FileSystemWatcher` filtrado a `*.md`, con rebote por ruta, filtro de temporales y archivos de conflicto, y degradacion a sondeo si el vigilante falla o el sistema de archivos no lo soporta.
- huella en disco por nota (`NoteDiskStamp`: fecha, tamano y hash del archivo) con la que se distingue una escritura propia de una ajena por comparacion exacta en vez de por ventanas de tiempo, de modo que el autoguardado nunca genera un falso conflicto.
- comprobacion previa a guardar en `VaultService.WriteContentChecked`, independiente del vigilante: si el archivo en disco ya no es el que se leyo, la escritura se aborta sin tocar nada en vez de pisar el cambio ajeno.
- politica de conflicto en el editor: recarga silenciosa conservando cursor y desplazamiento cuando no hay cambios locales, y dialogo de conflicto con "conservar la mia", "cargar la de disco" y "ver diferencias" cuando los hay; la version descartada se guarda antes en el historial local con el motivo "conflicto externo", asi que ninguna opcion destruye nada.
- pausa del autoguardado por nota mientras la decision siga pendiente, con aviso en la cabecera del editor que la reabre, y conservacion del texto sin guardar en el historial si la pestana se cierra sin decidir.
- manejo del borrado y del renombrado desde fuera: la nota borrada conserva su contenido en pantalla y se ofrece guardarla como nota nueva; la renombrada o movida hace que la pestana siga a su archivo a la ruta nueva.
- ajustes > editor "vigilar cambios externos en el vault" y "recargar automaticamente si no hay cambios locales", ambos activados por defecto y traducidos a los siete idiomas soportados; con la vigilancia apagada, la comprobacion previa a guardar sigue en pie.

#### CAMBIOS
- rehacer la justificacion de parrafos, listas, tareas y citas para distribuir primero los espacios y despues un interletraje limitado, conservar la geometria del cursor y la seleccion, respetar la ultima fila y el codigo, y mantener estable el ajuste de linea durante los repintados.
- unificar los botones de la barra de formato y los atajos sobre las mismas operaciones de edicion para que encabezados, listas, tareas, citas y formato en linea alternen o sustituyan el marcador existente en vez de acumular sintaxis.
- documentar el comportamiento y los limites del editor en `.docs/project-info/editor-markdown.md` y ampliar `MarkdownEditingTests`, `MarkdownCalloutsTests`, `EditorEditingUiTests`, `MarkdownCodeHighlightUiTests` y `MarkdownJustificationUiTests` con regresiones de seleccion, deshacer, saltos lf/crlf, cercas de codigo, avisos, resaltado y geometria de texto.
- establecer `.docs/project-info/` como ruta versionada de la documentacion del proyecto y aclarar en `README.md`, `README.en.md`, `AGENTS.md` y `.gitignore` que `docs/` no forma parte del checkout.
- anadir `.docs/project-info/qa-manual-release-candidate.md`, la checklist de qa manual de release candidate que consolida las verificaciones de lector de pantalla, render de wikilinks/enlaces rotos, reconexion de polar realtime y selector de recurrencia que los tests automaticos no cubren.
- anadir empaquetado real por sistema en `enki/packaging/`: paquete `.deb` y `.tar.gz` con integracion `.desktop`/iconos hicolor en Linux, instalador Inno Setup en Windows y bundle `.app`/`.dmg` firmado ad-hoc en macOS, todos generados a partir del mismo publish self-contained y sin arrastrar `local.properties` al paquete final.
- anadir `.github/workflows/release.yml`: al empujar un tag `v*.*.*` compila, empaqueta y publica los cinco paquetes de las tres plataformas como una GitHub Release, sin pasos manuales; tambien disponible como `workflow_dispatch` para probar el empaquetado sin cortar una version real.
- anadir el job `lint-packaging` a `.github/workflows/ci.yml`, que valida en cada push/pr la sintaxis de los scripts de `enki/packaging/` (`bash -n`, `shellcheck`), el `enki.desktop` de linux y el yaml de `release.yml`, para detectar errores de empaquetado antes de cortar una release real.
- ampliar el panel del grafo con seleccion de grupos conectados, etiquetas al pasar el cursor, recuentos visibles y globales, estados sin resultados y restablecimiento de filtros; conservar las selecciones disponibles al recargar y el modo de exploracion al cambiar de idioma.
- documentar las notas diarias en `.docs/project-info/notas-diarias.md` (nombrado en los dos sentidos, navegacion por dias, combinacion con la plantilla y calendario de constancia), enlazarlo desde `AGENTS.md`, `plantillas-de-notas.md` y el inventario de funcionalidades, y anadir su seccion a la checklist de qa manual.
- cubrir las notas diarias con `DailyNoteNamingTests`, `DailyNoteTemplateMergeTests`, `DailyNoteServiceTests` y `NotesViewModelDailyNoteTests` en `enki.Tests`, mas `DailyNoteUiTests` en `enki.UITests`, que monta el xaml real del calendario de constancia y de la cabecera del editor.
- cubrir el pegado de imagenes de punta a punta en `enki.UITests/EditorViewImagesUiTests.cs` contra el portapapeles real del `TopLevel` (adjunto guardado y referencia insertada, varias imagenes, imagen que ya vive en el vault, sustitucion de la seleccion y pegado de texto sin adjuntos), y documentar el comportamiento nuevo en `.docs/project-info/imagenes-en-notas.md`.

#### CORREGIDO
- conservar el texto seleccionado, la posicion del cursor y los saltos lf/crlf al insertar bloques de codigo o editar lineas, y agrupar cada transformacion compuesta y la renumeracion de listas en un unico paso de deshacer.
- impedir que la sangria, la renumeracion, inicio inteligente y retroceso interpreten como listas los ejemplos escritos dentro de una cerca de codigo, y permitir escribir tres acentos graves seguidos sin que el autocierre interrumpa la apertura del bloque.
- interpretar correctamente el estilo textmate no definido para evitar aplicar cursiva, negrita y subrayado a la vez, y aplicar el presupuesto de tokenizacion al bloque completo en vez de reiniciarlo en cada linea.
- convertir una url pegada sobre texto seleccionado en un enlace markdown y mantener el pegado normal cuando el portapapeles no contiene una url valida.
- corregir los enlaces de documentacion y los iconos de ambos README, ademas de las referencias y enlaces relativos de `AGENTS.md` y `.docs/project-info/`, para que apunten a archivos existentes.
- mostrar el grafo cuando el vault contiene notas sin ninguna sesion creada y evitar ocultarlo temporalmente durante una recarga.
- cancelar el extremo pendiente de un enlace manual al cambiar los filtros para impedir conexiones accidentales con notas ocultas.
- evitar que el recuento del grafo se solape con sus controles en ventanas estrechas y mantener accesible el boton de restablecer al desplazar el panel de filtros.
- sustituir el texto seleccionado al pegar una imagen en el editor, igual que hace cualquier otro pegado, en una sola operacion deshacible con un unico `ctrl+z`.
- descartar con aviso una imagen del portapapeles que no entrega ni un byte, en vez de guardar un adjunto vacio e insertar un `![](...)` que apunta a una imagen rota.
- exportar una nota que contiene un enlace a una imagen inexistente en vez de fallar entera: la comprobacion de enlaces simbolicos de `NoteExportRenderer` lanzaba al leer los atributos de una ruta que no existe, de modo que un unico `![](imagen-borrada.png)` tumbaba la exportacion a html y pdf de toda la nota.

#### SEGURIDAD
- proteger los tokens de sesion de polar con dpapi en windows, secret service mediante libsecret en linux y keychain en macos, fuera de `settings.json`.
- migrar los tokens heredados solo tras confirmar su escritura segura y avisar de los fallos sin guardar nuevas sesiones en texto plano.
- mantener la redaccion de tokens en la exportacion de ajustes y los backups de `settings.json` global y del vault, incluidos los archivos heredados.
- escribir todos los temporales de la app en un directorio propio por usuario con modo `0700` y comprobado en cada uso (`PrivateTempFile`), en vez de con un nombre fijo en la raiz compartida del temporal del sistema, donde otra cuenta local podia ocupar ese nombre con un enlace simbolico para que la app truncara un archivo ajeno.
- crear la copia descifrada temporal de `VaultService.ResolveReadablePath` con modo `0600` desde la propia llamada que la crea (`FileMode.CreateNew` mas `FileStreamOptions.UnixCreateMode`) y borrarla si la escritura falla a medias, cerrando la ventana en la que existia con la mascara heredada; el barrido de arranque y cierre limpia ahora tambien los temporales que dejaron versiones anteriores sueltos en la raiz del temporal.
- validar el manifiesto de cifrado de una sesion protegida antes de derivar nada en `SessionEncryptionService`: base64 decodificable, sal y clave envuelta con longitud coherente, `Iterations` dentro de rango y `FormatVersion` conocida.
- distinguir "contrasena incorrecta" de "manifiesto ilegible" con `SessionManifestCorruptException` y un mensaje propio en el dialogo de desbloqueo, para que una sesion danada no se presente como un error de tecleo.
- restringir las imagenes remotas del editor a direcciones publicas comprobando la ip realmente conectada en `SocketsHttpHandler.ConnectCallback` (`RemoteImagePolicy`), lo que cubre loopback, rangos privados, link-local, cgnat, multicast y reservados en ipv4 e ipv6, incluidas las ipv4 disfrazadas dentro de una ipv6.
- limitar a tres las redirecciones de una imagen remota, validar cada salto igual que el primero y rechazar el paso de `https` a `http`.
- cortar la descarga de una imagen remota por streaming al llegar a `MaxRemoteBytes`, en vez de traerse el cuerpo entero a memoria antes de medirlo, y acotar toda la descarga con un presupuesto de tiempo unico.
- decir en el dialogo de crear una sesion protegida que el cifrado cubre el contenido de las notas y sus adjuntos, y que los nombres de archivo, la estructura y las fechas siguen legibles en disco.

### ENGLISH

#### ADDED
- global quick capture from the tray icon, command palette, a local shortcut and the remappable `ctrl+alt+space` system-wide shortcut: it opens an independent mini-window, preserves the draft when cancelled and writes directly to an `Inbox/` note or the selected Kanban column without bringing the main window forward.
- native global-shortcut backends for Windows (`RegisterHotKey`), Linux X11 (`XGrabKey`) and macOS (`RegisterEventHotKey`), with explicit fallback for Wayland, conflicts and unsupported platforms; plus an opt-in setting to keep Enki in the tray when the main window closes.
- professional markdown editing through `MarkdownEditing`: toggle inline formatting and block markers, apply actions to multiline selections, indent and renumber lists, continue lists and quotes with enter, smart home, marker deletion, configurable automatic pairs, url-to-link pasting, and line move, duplicate, delete and expand operations, all connected to remappable shortcuts and complete undo actions.
- syntax highlighting for fenced code blocks with a declared language using the textmate grammars and light/dark themes already shared by the file viewer, loaded in the background, cached per block and bounded by time and line count so typing never blocks.
- obsidian-compatible callouts (`> [!note]`, `tip`, `warning`, `danger` and their aliases) with theme-aware icons, titles, tones and backgrounds, plus the `/aviso` command so they can be inserted without memorising the syntax.
- settings under settings > editor to choose justified or ragged text and enable or disable automatic bracket and quote closing, persisted per vault and translated into all seven supported languages.
- reusable note templates in `<vault>/.enki/templates/`: each template is a markdown file with optional front matter (`name`, `description`, `title`) and a body with `{{title}}`, `{{date}}`, `{{time}}`, `{{datetime}}`, `{{weekday}}`, `{{month}}`, `{{year}}`, `{{uuid}}` and `{{cursor}}` variables, with optional format (`{{date:dd/MM/yyyy}}`) and offset (`{{date+1d}}`); anything unrecognised is left untouched in the note.
- a "from template" flow in documents — full-view button, simplified-view action row, note grid context menu and command palette — with a picker to create the note, edit the template in an editor tab, create a new template pre-filled with the syntax, open its folder and delete it honouring "ask before deleting".
- a "daily note template" setting under settings > editor > templates, stored per vault in `<vault>/.enki/settings.json`: the template decides the content and, when it declares `title`, the note's name too; it only applies on creation, never over the day's existing note.
- two translated example templates (daily note and meeting) seeded the first time each vault is opened, never restored again if the user deletes them.
- previous-day / next-day navigation in the editor header whenever the open note is a daily note, with the date spelled out to be read, a jump back to today and remappable `ctrl+alt+left` / `ctrl+alt+right` shortcuts; the target day's note is created from its template when it doesn't exist yet.
- daily notes recognised by their file name in both directions (date to name and name to date), honouring the configured template's `title` while still recognising the `yyyy-MM-dd` notes that predate it, so a day never ends up with two daily notes.
- a "merge with template" action in the editor header that appends only the sections the daily note is missing, at the end and without touching anything already written, listing what it will add in the tooltip beforehand and leaving a restore point in the version history.
- a daily note consistency calendar in documents (full view, simplified view and command palette): ten calendar-aligned weeks, one cell per day, clicking a cell opens or creates that day's note, and a single descriptive figure of how many days have one.
- a habits row inside that same calendar, read from the vault's real habits and with jumps to habits and statistics, with no streaks, points or goals of any kind.
- local per-note version history, stored inside the vault itself (`<vault>/.enki/history/`, or `.history/` alongside a session note, inheriting its encryption when the session is protected): automatic snapshots during autosave (throttled to one every 5 minutes, and only when the content actually changed), a "History" tab in the editor to diff line-by-line against the current version and restore, and an automatic safety snapshot before restoring or before a rename rewrites wikilinks in other notes.
- add a "Tags" sidebar section to browse and search active tags from notes, kanban cards and polar tasks in one view, with per-module counts and multi-selection for merging.
- allow tags to be renamed, merged and removed across all three modules without a separate store: preserve note front matter and version history, remove duplicates within each item, sync polar changes and refresh open note tabs.
- extend the global vault graph with combined session and tag filters to explore accessible notes without creating another store or replacing the existing renderer.
- add modes to discover orphan notes, cross-session connections and connected note groups, computed over the entire accessible vault to prevent misleading results when filtering.
- detection of changes made to the vault outside enki (syncthing, dropbox, another editor): `VaultChangeWatcher` watches the folder with a `FileSystemWatcher` filtered to `*.md`, with per-path debouncing, filtering of temporary and sync-conflict files, and degradation to polling when the watcher fails or the file system does not support it.
- a per-note on-disk stamp (`NoteDiskStamp`: timestamp, size and file hash) used to tell an own write from a foreign one by exact comparison rather than time windows, so autosave never produces a false conflict.
- a pre-save check in `VaultService.WriteContentChecked`, independent of the watcher: if the file on disk is no longer the one that was read, the write is aborted without touching anything instead of overwriting the foreign change.
- conflict policy in the editor: silent reload preserving caret and scroll position when there are no local changes, and a conflict dialog offering "keep mine", "load from disk" and "show differences" when there are; the discarded version is saved to the local history first under the "external conflict" trigger, so neither option destroys anything.
- per-note autosave pause while the decision is still pending, with a header badge that reopens the dialog, and preservation of the unsaved text in the history if the tab is closed without deciding.
- handling of external deletes and renames: a deleted note keeps its content on screen and offers to be saved as a new note; a renamed or moved one makes the tab follow its file to the new path.
- settings > editor toggles "watch for external changes in the vault" and "reload automatically when there are no local changes", both on by default and translated into the seven supported languages; with watching off, the pre-save check still applies.

#### CHANGED
- rebuild paragraph, list, task and quote justification to distribute word spacing before limited character tracking, preserve caret and selection geometry, respect final rows and code, and keep wrapping stable across redraws.
- route formatting-toolbar buttons and shortcuts through the same editing operations so headings, lists, tasks, quotes and inline formatting toggle or replace existing markers instead of stacking syntax.
- document editor behaviour and limits in `.docs/project-info/editor-markdown.md` and expand `MarkdownEditingTests`, `MarkdownCalloutsTests`, `EditorEditingUiTests`, `MarkdownCodeHighlightUiTests` and `MarkdownJustificationUiTests` with regressions for selections, undo, lf/crlf line endings, code fences, callouts, highlighting and text geometry.
- establish `.docs/project-info/` as the versioned project documentation path and clarify in `README.md`, `README.en.md`, `AGENTS.md`, and `.gitignore` that `docs/` is not part of the checkout.
- add `.docs/project-info/qa-manual-release-candidate.md`, the release-candidate manual qa checklist covering screen reader, wikilink/broken-link rendering, polar realtime reconnection and recurrence selector checks that automated tests don't cover.
- add real per-platform packaging under `enki/packaging/`: a `.deb` and `.tar.gz` with `.desktop`/hicolor icon integration on Linux, an Inno Setup installer on Windows, and an ad-hoc signed `.app`/`.dmg` bundle on macOS, all built from the same self-contained publish and never carrying `local.properties` into the final package.
- add `.github/workflows/release.yml`: pushing a `v*.*.*` tag builds, packages and publishes all five packages across the three platforms as a GitHub Release with no manual steps; also available as a `workflow_dispatch` to test packaging without cutting a real release.
- add a `lint-packaging` job to `.github/workflows/ci.yml` that validates `enki/packaging/` scripts on every push/pr (`bash -n`, `shellcheck`), the linux `enki.desktop` file and `release.yml`'s yaml, to catch packaging errors before cutting a real release.
- expand the graph panel with connected group selection, tags on hover, visible and global counts, empty states and filter reset; preserve available selections when reloading and the exploration mode when switching languages.
- document daily notes in `.docs/project-info/notas-diarias.md` (two-way naming, day navigation, template merging and the consistency calendar), link it from `AGENTS.md`, `plantillas-de-notas.md` and the feature inventory, and add its section to the manual qa checklist.
- cover daily notes with `DailyNoteNamingTests`, `DailyNoteTemplateMergeTests`, `DailyNoteServiceTests` and `NotesViewModelDailyNoteTests` in `enki.Tests`, plus `DailyNoteUiTests` in `enki.UITests`, which mounts the real xaml of the consistency calendar and the editor header.
- cover image pasting end to end in `enki.UITests/EditorViewImagesUiTests.cs` against the `TopLevel`'s real clipboard (saved attachment and inserted reference, several images, an image already living in the vault, selection replacement and text pasting without attachments), and document the new behaviour in `.docs/project-info/imagenes-en-notas.md`.

#### FIXED
- preserve selected text, caret position and lf/crlf line endings while inserting code blocks or editing lines, and group each compound transformation and list renumbering into one undo step.
- prevent indentation, renumbering, smart home and backspace from treating examples inside a code fence as lists, and allow three consecutive backticks to be typed without automatic pairing interrupting the opening fence.
- interpret an undefined textmate font style correctly instead of applying italic, bold and underline together, and enforce the tokenisation budget across the complete block instead of resetting it for every line.
- turn a url pasted over selected text into a markdown link while retaining normal paste behaviour when the clipboard does not contain a valid url.
- fix documentation links and icons in both README files, along with references and relative links in `AGENTS.md` and `.docs/project-info/`, so they point to existing files.
- show the graph when the vault contains notes without any sessions and avoid temporarily hiding it during a reload.
- clear the pending endpoint of a manual link when filters change to prevent accidental connections to hidden notes.
- prevent graph counts from overlapping controls in narrow windows and keep the reset button accessible while scrolling the filter panel.
- replace the selected text when pasting an image into the editor, like any other paste does, as a single operation that one `ctrl+z` undoes.
- discard a clipboard image that yields no bytes, with a notification, instead of saving an empty attachment and inserting a `![](...)` pointing at a broken image.
- export a note containing a link to a missing image instead of failing outright: `NoteExportRenderer`'s symbolic-link check threw while reading the attributes of a path that does not exist, so a single `![](deleted-image.png)` brought down html and pdf export for the whole note.

#### SECURITY
- protect polar session tokens with dpapi on windows, secret service through libsecret on linux, and keychain on macos, outside `settings.json`.
- migrate legacy tokens only after secure storage succeeds and report failures without saving new sessions in plain text.
- keep tokens redacted in settings exports and backups of global and vault `settings.json`, including legacy files.
- write every temporary file the app creates into a per-user directory with mode `0700`, checked on each use (`PrivateTempFile`), instead of a fixed name in the shared root of the system temp directory, where another local account could claim that name with a symlink and make the app truncate someone else's file.
- create the decrypted temporary copy of `VaultService.ResolveReadablePath` with mode `0600` in the very call that creates it (`FileMode.CreateNew` plus `FileStreamOptions.UnixCreateMode`) and delete it if the write fails halfway, closing the window in which it existed with the inherited umask; the startup and shutdown sweep now also cleans up temporary files left loose in the temp root by earlier versions.
- validate a protected session's encryption manifest before deriving anything in `SessionEncryptionService`: decodable base64, coherent salt and wrapped key lengths, `Iterations` within range and a known `FormatVersion`.
- tell "wrong password" apart from "unreadable manifest" with `SessionManifestCorruptException` and a message of its own in the unlock dialog, so a damaged session is not presented as a typing mistake.
- restrict the editor's remote images to public addresses by checking the ip actually connected to in `SocketsHttpHandler.ConnectCallback` (`RemoteImagePolicy`), covering loopback, private ranges, link-local, cgnat, multicast and reserved ranges in ipv4 and ipv6, including ipv4 addresses disguised inside an ipv6 one.
- limit a remote image to three redirects, validate every hop like the first one and refuse a downgrade from `https` to `http`.
- cut a remote image download while streaming once it reaches `MaxRemoteBytes`, instead of pulling the whole body into memory before measuring it, and bound the whole download with a single time budget.
- state in the protected-session dialog that encryption covers the content of the notes and their attachments, and that file names, structure and dates stay readable on disk.

### ENTRADAS ANTERIORES

**Ajustes y preferencias propios de cada vault**. Las elecciones de apariencia, idioma, editor,
calendario, estadísticas, navegación, atajos y disposición de la interfaz se guardan en
`<vault>/.enki/settings.json`. Al cambiar de vault, Enki carga sus ajustes y actualiza la interfaz sin
reiniciar.

- Antes de elegir un vault, los ajustes usan el archivo global de forma temporal. Al seleccionar el
  primero, se migran junto con el logo y los temas personalizados, sin sobrescribir los valores ni los
  archivos que ya existan en el vault.
- `%AppData%/Enki/settings.json` conserva solo la ruta del vault, la sesión de Polar y los datos propios de
  la instalación; las credenciales y los tokens no viajan con el vault. Importar ajustes respeta estos
  datos locales y escribe las preferencias en el vault activo.
- Si el JSON de ajustes del vault está dañado, se conserva una copia `settings.json.corrupt-*` antes de
  continuar. Se añaden pruebas de carga, guardado, cambio de vault, migración, importación y respaldo.

**Polar: configuración de Supabase lista en este equipo**. Se ha preparado `enki/local.properties` con
la URL del proyecto y su clave publicable, tomados de `enki/.env`. Se ha comprobado que la configuración
llega al ejecutable y que los servicios Auth y REST de Supabase responden. Vincular una cuenta sigue
requiriendo iniciar sesión desde Ajustes.

- `enki/.env` deja de estar versionado, sin borrar la copia local, y pasa a estar ignorado por Git junto
  con `local.properties`. La clave secreta no se copia a la aplicación.
- Los README explican cómo trasladar `SUPABASE_PUBLISHABLE_KEY` a `SUPABASE_ANON_KEY` y advierten que
  `SUPABASE_SECRET_KEY` no debe utilizarse en el cliente.

**Gestión de tareas oculta sin cuenta de Polar vinculada**. El botón del tablero Kanban deja de aparecer
cuando Polar no está configurado o no se ha iniciado sesión en Enki; vuelve a mostrarse al vincular una
cuenta y se oculta de nuevo al cerrar sesión o caducar la sesión.

**Tareas y avisos guardados en el vault activo**. Kanban, las tareas de Polar, los recordatorios y sus
descartes se guardan ahora en `.tasks/` dentro del vault; las notificaciones se guardan en `.enki/`.
Cada vault conserva sus propios datos al cambiar de carpeta.

- Los ficheros de instalaciones anteriores se copian una sola vez al primer vault abierto, sin
  sobrescribir los datos que ya existan allí y conservando los originales como respaldo.
- Al cambiar de vault, Enki cierra los editores vinculados al anterior y recarga los datos del nuevo.
  La sincronización de Polar espera a que termine cualquier ciclo en curso y mantiene un cursor propio
  para cada vault.

**Corregida la exportación de temas duplicados**. Los duplicados quedan registrados al guardarlos, de
modo que vuelven a cargarse y pueden exportarse después de reiniciar. Exportar desde el editor escribe
los colores que se ven en ese momento, incluidos los cambios aún sin guardar, y avisa si ocurre un
error inesperado.

**La creación rápida del calendario, arreglada y puesta a punto**. El desplegable de fecha del popover
"Creación rápida" no pintaba los días del mes: en su lugar salían seis filas de texto interno repetido, así
que elegir otra fecha era imposible y había que crear el elemento y corregirlo después en su editor.

- **El calendario vuelve a verse**: la rejilla muestra los días del mes, atenúa los del mes anterior y el
  siguiente, marca hoy y resalta la fecha elegida, igual que el selector de fecha de Kanban y
  Recordatorios. Se puede navegar entre meses y pulsar cualquier día.
- **Botones del pie con jerarquía**: "Más opciones" era texto suelto sin caja propia, flotando a otra
  altura que sus vecinos y con un recuadro pegado a las letras al pasar el ratón. Ahora es un botón de
  contorno a la izquierda —el camino alternativo— frente al par "Cancelar / Crear" de la derecha, y los
  tres miden lo mismo. Una línea fina separa las acciones del formulario.
- **Se escribe y se confirma sin tocar el ratón**: el cursor entra solo en el título al abrir el popover e
  Intro crea el elemento. Escape cierra primero el calendario desplegado, si está abierto, y solo después
  el formulario: antes tiraba todo lo escrito de una vez.
- **El formulario ya no deja pasar los atajos de la página**: con el popover delante, pulsar D, S, M, A o
  T cambiaba la vista del calendario de detrás.
- El selector de tipo (Kanban / Tarea de Polar / Recordatorio) envuelve a la línea siguiente en vez de
  salirse del popover cuando las etiquetas del idioma activo son largas.

**Un color para cada origen en el calendario**. Hasta ahora el calendario pintaba todas las tareas del
mismo color y solo los recordatorios se distinguían, así que no había forma de ver de un vistazo si algo
venía de Kanban o de qué lista de Polar. Ahora cada origen tiene el suyo.

- **Columnas de Kanban**: "pendientes" en rojo, "en proceso" en ámbar y "realizadas" en verde. Las
  columnas que crees tú reciben un color propio automáticamente.
- **Listas de Polar**: cada lista estrena un color distinto, elegido entre azules, cianes, violetas y
  magentas — los rojos, amarillos y verdes quedan reservados para los estados de Kanban, para que no se
  confundan.
- **Cambiarlos**: en el panel "Capas" del calendario, la fila de Kanban despliega ahora una línea por
  columna. Pulsa el punto de color de cualquier capa y elige otro de la rejilla, escribe un hex, o vuelve
  al automático. Los colores se calculan para leerse bien tanto en el tema claro como en el oscuro.
- Es una personalización **solo del calendario**: no cambia cómo se ven Kanban ni "Gestión de tareas", y
  el color de una lista de Polar no se toca (ni se sincroniza), así que no afecta a tus otros
  dispositivos.

**Archivos, pulida en sus dos vistas**. La sección tenía partes a medio hacer y fallos que dejaban la pantalla
apuntando a archivos que ya no estaban donde decía; ahora el explorador, el árbol y la previsualización están
terminados.

- **Explorador**: miga de pan para volver a cualquier nivel de un clic, la carpeta actual resaltada en el
  panel, tarjetas de carpeta con el número de elementos (y que aceptan archivos arrastrados encima), orden
  por fecha, nombre, tamaño o tipo que se recuerda, y la búsqueda baja a las subcarpetas diciendo dónde está
  cada resultado. Una búsqueda sin resultados, una carpeta vacía y un almacén vacío ya no enseñan el mismo
  mensaje. Renombrar y borrar en el panel aparecen al pasar el cursor.
- **Árbol**: se recorre con el teclado (flechas, Intro, F2, Supr), el menú contextual depende de si es una
  carpeta o un archivo, el pie cuenta los archivos de todo el árbol, e importar un solo archivo lo abre.
- **Previsualización**: archivo anterior y siguiente (Alt + ← / →), Esc para cerrar, una barra con tipo,
  tamaño, fecha, dimensiones o líneas, y un menú con mostrar en el explorador, copiar ruta y eliminar. Borrar
  el archivo abierto pasa al siguiente en vez de cerrar.
- **PDF**: se abre ajustado al ancho, se ve nítido en pantallas HiDPI, el zoom va por escalones (Ctrl + rueda,
  Ctrl + 0 para volver a ajustar) y pasar de página vuelve arriba.
- **Imágenes**: las miniaturas se reducen al cargarlas, así que una carpeta de fotos ya no ocupa cientos de MB;
  la previsualización carga la imagen completa (los SVG ya no se ven borrosos), sin ampliar las pequeñas, y
  alterna entre ajustada y tamaño real.
- **Código y texto**: el visor ya no dibuja el archivo entero de golpe, sigue el tema claro u oscuro de la app,
  ajusta líneas largas y copia el contenido. Un archivo binario con extensión de texto ya no se vuelca como
  basura, se respeta UTF-16 y el aviso de archivo truncado ya no se cuela dentro del texto.
- **Audio, vídeo y demás tipos sin visor**: una ficha con el motivo y dos salidas, abrir con la aplicación del
  sistema o mostrar el archivo en su carpeta.
- **Carpetas**: borrar una carpeta pide confirmación y avisa de que va a `.trash`; renombrar la carpeta en la
  que se está ya no devuelve a la raíz, y renombrar o mover lo que está abierto no deja la previsualización
  leyendo de una ruta vieja. Nuevas acciones "Copiar ruta" e "Importar aquí…".
- **Errores de disco** al crear, renombrar, mover o borrar se avisan con una notificación en vez de
  interrumpir la sección. El aviso de borrar un archivo ya no dice que no se puede deshacer: va a la papelera.

**La fuente de la interfaz llega ahora a toda la app**. Ajustes › Apariencia › "Fuente de la interfaz" solo
cambiaba los títulos y el texto principal; el resto seguía con la fuente de siempre.

- **Micro-etiquetas**: subtítulos de accesos rápidos, fechas ("hace 5 días"), cabeceras espaciadas
  ("PENDIENTES", "NOTIFICACIONES"), contadores, ejes de las gráficas de Estadísticas, números del
  calendario, atajos de teclado y rutas del vault. Siguen en JetBrains Mono con la fuente por defecto.
- **Campos de texto y sus placeholders** (p. ej. "Buscar en todo…"), que salían en Inter, la fuente
  interna del framework, con cualquier ajuste. Con la fuente por defecto ahora usan Space Grotesk, como el
  resto de la app.
- El texto que necesita monoespaciado de verdad no cambia: la vista de código de "Archivos", los códigos de
  recuperación y el contenido en crudo de una skill.

**Fuente del código a elección del usuario**. Ajustes › Editor › "Fuente del código" permite escoger
cualquiera de las fuentes instaladas en el sistema, independientemente de las fuentes de la interfaz y del
texto de las notas. JetBrains Mono NF sigue siendo la opción predeterminada.

- El cambio se aplica a los bloques de código y al código en línea de las notas y las skills, al visor de
  archivos de código y al contenido en crudo de una skill. Las etiquetas y los códigos de recuperación
  mantienen su fuente monoespaciada.
- La fuente elegida se guarda con los ajustes del vault activo y aparece al momento en los editores abiertos;
  también se recupera al volver a abrir una vista o cambiar de vault. El selector está traducido a todos los
  idiomas disponibles.

**Logo propio en la barra lateral**. El cuadro de la esquina superior izquierda llevaba siempre el icono de
Enki. Ahora Ajustes › Apariencia › "Logo de la barra lateral" permite poner ahí una imagen del sistema.

- **Elegir entre el icono por defecto y una imagen propia**, con vista previa en los propios ajustes. Volver
  al icono por defecto no borra la imagen importada: sigue ahí para reactivarla cuando se quiera, y hay un
  botón aparte para quitarla del todo.
- **Forma cuadrada o circular**, la única forma redonda de toda la app. Solo se ofrece con imagen propia; el
  icono de Enki, que ya es una insignia cuadrada, se sigue dibujando cuadrado.
- **La imagen se guarda tal cual**, sin reducirla ni recodificarla. Enki guarda todo en local y el logo no
  viaja a ningún sitio, así que ahorrar unos kilobytes nunca compensa degradar la imagen que uno ha elegido
  a mano: se copia el archivo entero, con su formato, su transparencia y todos sus píxeles. Los ajustes
  dicen cuánto mide y cuánto ocupa.
- **Se encoge solo al dibujarla**, de una vez y con el remuestreo de calidad de Skia. Antes la imagen
  llegaba al cuadro de 32 px sin filtrar —quedarse con uno de cada N píxeles—, que es lo que la dejaba
  dentada; ahora se ve nítida, y guardar el original completo es lo que le deja aprovechar una pantalla
  HiDPI o el zoom de interfaz al 200 %.
- El cambio se ve en la barra lateral al momento, sin reiniciar.

**Imágenes menos dentadas en "Archivos"**. Las miniaturas de la cuadrícula, la vista previa grande y el rail
de páginas de un PDF encogían la imagen sin filtrar ninguno de los píxeles que descartaban. Ahora se dibujan
con filtrado de calidad, como el logo de la barra lateral y como ya hacía el editor de notas.

**Kanban: archivar tareas completadas**. La columna "Hecho" solo crecía, y la única forma de vaciarla era
borrar sus tarjetas —lo que las manda a la papelera, las saca del calendario y las purga a los 30 días—.
Archivar es la vía intermedia que faltaba: la tarea sale del tablero pero no desaparece de la app.

- **Clic derecho sobre una tarjeta de la columna de completadas → "archivar"**, y la tarjeta deja de
  ocupar sitio en el tablero. Sigue viéndose en el calendario, sigue contando en las estadísticas, se
  sigue encontrando desde la búsqueda global y sigue apareciendo entre las "tareas vinculadas" de la nota
  a la que estuviera enlazada: solo el tablero deja de dibujarla. El panel de inicio, que es un espejo del
  tablero, tampoco las cuenta.
- **La cabecera de la columna de completadas ofrece archivarlas todas de golpe**, con confirmación y un
  aviso al terminar.
- **Nueva ventana de archivadas**, a un clic desde la barra de herramientas del tablero, con el número de
  tareas guardadas en el propio botón. Dentro se busca por texto y se acota por prioridad y etiquetas, y
  se ordena por fecha de archivado, fecha límite, prioridad o título.
- **Un clic sobre una tarea archivada abre su ficha completa**, y el clic derecho ofrece desarchivarla,
  editarla o eliminarla. Desarchivar la devuelve a la columna de completadas, en la misma posición que
  ocupaba. Archivar y desarchivar están también en la propia ficha, para no depender del clic derecho.
- Restaurar desde la papelera una tarea que estaba archivada la devuelve al tablero a la vista, en vez de
  dejarla en una columna que no la dibuja.

**Archivos: soltar del sistema sobre una carpeta, y la sección recuerda el espacio de trabajo**.

- **Las filas de carpeta del modo explorador aceptan archivos del explorador del sistema**, y los importan
  dentro de esa carpeta — igual que la fila "TODOS", que los deja en la raíz. Hasta ahora esas filas solo
  aceptaban el arrastre interno de la propia sección, mientras que el modo árbol sí importaba a la carpeta
  soltada: la misma carpeta, arrastrada lo mismo desde el mismo sitio, aceptaba o rechazaba según la
  disposición desde la que se estuviera mirando. La carpeta se resalta mientras el arrastre pasa por encima,
  así que se ve dónde va a caer antes de soltar, y con una sesión protegida y bloqueada elegida el gesto
  sigue sin aceptarse en ninguna de las dos disposiciones.
- **El selector de sesión de Archivos recuerda su elección** entre arranques, como ya hacía el de Notas: la
  app reabre la sección en el espacio de trabajo en el que se cerró en vez de volver siempre al vault
  entero. Cada sección guarda la suya por separado, así que cambiar de espacio en una no mueve la otra. Una
  sesión que ya no exista cae al vault en silencio, y una protegida se vuelve a elegir aunque arranque
  bloqueada.
- **Corregido un cierre inesperado de la app** al borrar una sesión (o al desaparecer una carpeta por
  cualquier otra vía) justo mientras el recorrido en segundo plano que arma el grafo de Sesiones la estaba
  visitando: ese fallo de disco no tenía dónde ser recogido y se llevaba el proceso por delante. Ahora el
  recorrido devuelve lo que pudo leer y el siguiente ya ve el árbol como quedó.

**IA Skills: carpetas propias y dos formas de navegar la seccion**. La biblioteca era una unica lista
plana: con veinte o cincuenta skills importadas, encontrar la de un tema concreto pasaba por la busqueda
si o si, y no habia ninguna forma de decir que media docena de ellas van juntas.

- **Carpetas y subcarpetas** dentro de `<vault>/.enki/skills/`, con la profundidad que el usuario quiera.
  Se crean, renombran y eliminan desde la propia seccion, y las skills se mueven entre ellas arrastrando.
  No hay indice ni metadato aparte: las carpetas en disco son la organizacion, igual que el `.md` es la
  skill, asi que la biblioteca se sigue pudiendo tocar desde fuera de Enki.
- **Se elige como navegar la seccion**, con un conmutador en su propia barra de herramientas:
  - *Explorador* — panel de carpetas a la izquierda y rejilla de tarjetas a la derecha, la misma
    disposicion que la seccion Archivos, con miga de pan y tarjeta ".." para volver.
  - *Arbol* — panel lateral que entremezcla carpetas y skills en una sola lista jerarquica, con su propia
    busqueda y el detalle o el editor a la derecha, la misma disposicion que la vista simplificada de
    Notas.
  Las dos comparten estado: cambiar de modo reordena la pantalla, no reinicia lo que se estaba haciendo.
  La eleccion, el ancho del panel y que carpetas quedan abiertas se recuerdan entre arranques.
- **Buscar sigue mirando toda la biblioteca**, tambien desde dentro de una carpeta: con busqueda o filtro
  de etiquetas activos la rejilla pasa a resultados globales y cada tarjeta dice de que carpeta viene.
- **Importar respeta la organizacion**: lo importado aterriza en la carpeta que se esta navegando, y al
  importar una carpeta se recrean sus subcarpetas. Exportar hace lo simetrico, de modo que exportar y
  volver a importar devuelve la biblioteca tal cual estaba en vez de aplanarla. Ahora tambien se pueden
  soltar `.md` desde el explorador del sistema directamente sobre la rejilla.
- **Duplicar deja la copia junto al original**, no en la raiz.
- **Eliminar una carpeta pide confirmacion aparte**, diciendo que se lleva todo lo de dentro y que la
  biblioteca de skills no tiene papelera. Una carpeta que contenga una skill del sistema no se puede
  eliminar: si no, seria la puerta trasera para borrar justo lo que la app protege.

**Tablero Kanban rehecho de arriba abajo**: la pantalla dedicaba las tres primeras bandas —subtítulo fijo
de manual, rejilla de cuatro tarjetas de estadísticas y una barra de herramientas con cuatro segmentos de
ordenación siempre desplegados— a información que no cambia o que no se puede accionar, y las columnas
empezaban a ~250px del borde superior. Ahora esa altura la ocupa el tablero.

- **Cabecera**: bajo el título va una línea de estado en vivo ("3 columnas · 12 tarjetas · 2 vencidas") en
  lugar de la frase de manual, que solo vuelve a aparecer —con los atajos de teclado al lado— cuando el
  tablero está vacío y sirve de algo. La rejilla de cuatro estadísticas desaparece: sus cuatro cifras caben
  en esa línea.
- **Filtros rápidos**: "alta prioridad" y "vencidas" dejan de ser números pasivos y son chips de un clic
  que filtran el tablero entero, y solo existen cuando hay algo que filtrar. Un botón "LIMPIAR" apaga
  buscador, etiquetas y filtro rápido a la vez, y aparece solo si hay algo puesto.
- **Buscador** con lupa y aspa de limpiar dentro del propio campo. Los chips de etiquetas bajan a su propia
  fila y solo se dibujan si el tablero tiene etiquetas — antes ocupaban sitio incluso para anunciar "Sin
  etiquetas".
- **Ordenación** en un desplegable que enseña el orden activo en el botón, en vez de cuatro segmentos
  permanentes ocupando media barra.
- **Tarjetas**: editar y eliminar solo aparecen al pasar el ratón o al enfocar la tarjeta con el teclado, y
  se han mudado al hueco libre de la fila de prioridad, quitando una fila entera a cada tarjeta. La
  palabra de la prioridad solo se escribe cuando es "alta" (baja y media ya se distinguen por la intensidad
  del punto, y el tooltip dice siempre cuál es). Subtareas, fecha, nota vinculada y etiquetas van en una
  sola línea de metadatos, y la nota vinculada se ve por fin desde el tablero, sin abrir la tarjeta.
- **Columnas a lo ancho**: dejan de medir 300px fijos y quedarse centradas —lo que en una pantalla normal
  dejaba unos 120px muertos a cada lado— y se reparten a partes iguales todo el ancho del contenido, de
  borde a borde y alineadas al píxel con la barra de herramientas de arriba, para que el tablero se lea
  como un bloque. A partir de la sexta columna el reparto las estrecharía por debajo de lo que necesita la
  propia tarjeta, así que ahí vuelven a un ancho fijo cómodo y el tablero se desplaza en horizontal.
- **Nombres de columna siempre en mayúsculas**, escriba el usuario lo que escriba: la columna es
  estructura del tablero, no contenido, y en este sistema de diseño la estructura va en mayúsculas. Aplica
  en la cabecera de la columna, en el selector de columna del formulario de tarjeta, en el diálogo de
  detalle, en el de eliminar columna, en el buzón de recordatorios y en la Papelera. Solo cambia cómo se
  dibuja: el título se guarda tal cual se tecleó y es eso lo que aparece al renombrarlo.
- **Columnas**: cabecera, cuerpo desplazable y pie, separados por líneas de 1px. "Añadir tarjeta" vive en
  el pie fijo, así que sigue a un clic de distancia por larga que sea la columna, en vez de ser la última
  loseta de la pila. Renombrar y eliminar también se revelan al pasar el ratón, y el candado de las
  columnas del flujo base aparece justo donde el usuario va a buscar la papelera que no está.
- **Estados vacíos**: una columna sin tarjetas y una columna cuyo filtro lo esconde todo se veían iguales;
  ahora cada una lo dice, y el contador de la cabecera pasa a "2/5" mientras se filtra. Si ningún resultado
  sobrevive al filtro, el tablero avisa y ofrece limpiarlo ahí mismo.
- **Diálogo de detalle** de una tarjeta: dice en qué columna está y gana botones de editar y eliminar, así
  que esas dos acciones nunca dependen de pasar el ratón.
- **Teclado**: `Esc` cierra también el diálogo de borrar columna —era el único del que no sacaba— y, sin
  ningún diálogo abierto, limpia los filtros. `N` crea la tarjeta en la columna donde esté el foco, no
  siempre en la primera. Los atajos se anuncian en la interfaz en vez de vivir solo en el código.
- Se corrigen de paso dos fallos de medida: las tarjetas con etiquetas largas se salían 14px por la derecha
  de su columna, y una tarjeta escondida por el filtro dejaba un hueco fantasma que desalineaba unas
  columnas respecto a otras.

**Papelera rehecha**: las siete categorías (documentos, archivos, tareas, listas y tareas de Polar,
recordatorios y sesiones) usaban cada una tarjetas de un tamaño distinto —224x150, 224x120 y 224x100—, así
que las filas no cuadraban al pasar de una sección a otra y, en las tarjetas bajas, un título de dos líneas
no cabía y se recortaba a media letra. Ahora todas las tarjetas comparten una única caja de 224x150 —la
misma que ya usaban las del navegador de Notas y Archivos— con la misma estructura de tres bandas: icono de
tipo y un dato corto arriba, título y línea de detalle en el centro, y acciones de restaurar/borrar
alineadas abajo a la derecha, con espacio reservado para que nunca se pisen entre sí.

- **Cabecera de cada sección**: el nombre de la categoría lleva ahora al lado el número de elementos que
  contiene, una línea fina que llega hasta el botón, y un botón "VACIAR" realmente compacto — antes ese
  botón medía lo mismo que el "VACIAR PAPELERA" de la página con la letra más pequeña, porque el estilo
  base le fijaba la altura y el ajuste que traía puesto no hacía nada.
- **Cabecera de la página**: junto a "VACIAR PAPELERA" aparece el total de elementos de toda la papelera,
  el mismo número que muestra la insignia de la barra lateral.
- **Secciones vacías**: en vez de una línea de texto suelta, cada categoría vacía dibuja un recuadro
  propio, así que la papelera vacía se lee de un vistazo en lugar de como una página a medio cargar.
- **Tareas de Polar**: las listas y las tareas borradas van bajo su propio subtítulo con su recuento; antes
  se dibujaban una detrás de otra y el salto entre ambas parecía una fila rota.
- El ancho del contenido se limita como en Recordatorios y Calendario, para que en una pantalla ancha la
  página siga alineada con el resto de la app.

**Bloques de código más compactos y con botón de copiar**: el recuadro de un bloque ` ``` ` en una nota
pasa a ser cuadrado —esquinas rectas y borde fino de 1px ajustado al píxel, como el resto de la interfaz—,
las líneas de los delimitadores se encogen a una banda de pocos píxeles en vez de ocupar una línea de texto
entera (antes dejaban ~20px de hueco vacío entre el borde y el código, arriba y abajo), y en la esquina
superior derecha aparece un botón que copia al portapapeles todo el contenido del bloque, sin las comillas,
y confirma la copia con una marca. El botón se dibuja con el mismo fondo y borde que el bloque, así que se
lee como parte de él; poner el cursor en un delimitador lo sigue devolviendo a su forma de texto para
editarlo, igual que antes.

**Tablas dentro de las notas**: el editor dibuja ahora como tabla las tablas Markdown de una nota —
rejilla, encabezado destacado y columnas cuadradas—, en vez de dejar a la vista las barras verticales del
texto. Antes solo se veían como tabla al exportar la nota, y ni siquiera eso: el exportador tampoco las
convertía en `<table>`.

- **Sintaxis**: la de GitHub Flavored Markdown, la misma que usan Obsidian y GitHub, con barras exteriores
  opcionales, barras escapadas (`\|`) como contenido y alineación por columna con los dos puntos de la
  fila separadora (`:---`, `---:`, `:---:`). Dos líneas con barras no se convierten en tabla por accidente:
  hace falta una fila separadora válida con tantas columnas como el encabezado.
- **Renderizado**: la tabla ocupa siempre el ancho de la columna de texto —el espacio sobrante se reparte
  entre las columnas en proporción a su contenido— y las columnas se cuadran midiendo el texto real, así
  que una tabla escrita a mano y sin alinear en el archivo se ve perfectamente alineada sin tocar el
  archivo; la fila separadora desaparece
  (se pliega, no deja hueco); el encabezado va en seminegrita sobre fondo propio; y el formato en línea
  —negrita, cursiva, código, enlaces, imágenes— funciona dentro de las celdas sin descuadrar la columna. El
  cursor dentro de la tabla la devuelve entera a su forma de texto para editarla, igual que pasa con un
  enlace o una negrita. Una tabla demasiado ancha para la columna se deja como texto en vez de dibujarse
  partida.
- **Edición**: botón de tabla en la barra de formato (grupo de inserción) junto al comando `/tabla` que ya
  existía; `Tab` y `Mayús+Tab` saltan de celda en celda y `Tab` en la última celda añade otra fila; `Intro`
  añade una fila con las mismas columnas, y en una fila vacía la borra para salir de la tabla, igual que
  una viñeta vacía cierra una lista.
- **Exportar a HTML**: cada tabla sale como un `<table>` real, con `<thead>`/`<tbody>` y la alineación de
  cada columna.

Documentado en `docs/tablas-en-notas.md`.

**Imágenes dentro de las notas**: el editor dibuja ahora las imágenes de una nota en el propio texto, y hay
cuatro formas de meterlas sin salir de él. Antes el botón de imagen de la barra de formato solo insertaba
la plantilla `![texto](url)` para rellenarla a mano, y ninguna imagen se llegaba a ver hasta exportar la
nota a HTML.

- **Insertar**: `Ctrl+V` pega una captura o un archivo de imagen del portapapeles; arrastrar archivos sobre
  el editor los inserta en el punto donde se sueltan; el botón de imagen de la barra de formato y el
  comando `/imagen` abren el diálogo de archivos, con selección múltiple. El pegado de texto de siempre no
  cambia: solo se intercepta cuando lo que hay en el portapapeles es realmente una imagen.
- **Dónde acaban**: en `<nota>.attachments/`, carpeta hermana de la nota, con nombre por marca de tiempo
  (capturas) o el nombre original saneado (archivos). Un archivo que ya vive dentro del vault se **enlaza
  donde está** en vez de duplicarse. La ruta que se escribe en el Markdown es relativa, así que la nota
  sigue funcionando si el vault entero se mueve o se sincroniza a otra máquina. Un adjunto de una nota de
  una **sesión protegida queda cifrado** con la misma clave que la nota, y **renombrar o mover la nota se
  lleva su carpeta de adjuntos** (re-cifrando si el movimiento cruza la frontera de la sesión).
- **Renderizado**: la imagen sustituye al `![…](…)` en el editor, a su tamaño natural, nunca más ancha que
  la columna de texto; el cursor encima devuelve el Markdown en crudo para poder editarlo, igual que con un
  enlace. Se decodifica en segundo plano y ya reducida al ancho —y a la densidad de píxeles— con que se va
  a dibujar, y queda en una caché compartida con presupuesto de memoria: abrir una nota llena de capturas y
  desplazarse por ella cuesta lo mismo que por una de solo texto. Una imagen editada fuera de Enki se
  refresca sola, y una que falte deja un hueco con su nombre en vez de desaparecer sin dejar rastro.
- **Sintaxis**: `![alt](ruta)`, la incrustación de Obsidian `![[archivo.png]]`, y tamaño explícito con
  `![alt|300](ruta)` / `![alt|300x200](ruta)` / `![[archivo.png|300]]`. El tamaño explícito viaja también
  al HTML exportado como `width`/`height`.
- **Ctrl+clic** sobre una imagen la abre con el visor del sistema (con una copia temporal descifrada si
  vive en una sesión protegida) o en el navegador si es una URL remota.
- **Ajustes › Editor › Imágenes remotas** (activo por defecto) controla si se descargan y dibujan las
  imágenes escritas con una dirección `http(s)`. Desactivarlo deja el editor sin ninguna petición de red al
  abrir una nota; las imágenes del vault y las `data:` incrustadas se dibujan siempre.

Documentado en `docs/imagenes-en-notas.md`.

**Búsqueda de texto completo en las notas**: hasta ahora las cuatro superficies de búsqueda de la app
comparaban lo escrito contra `NoteFile.Preview` —los primeros 160 caracteres del cuerpo de cada nota,
aplanados—, así que cualquier cosa escrita más allá de ese punto era imposible de encontrar desde Enki. Las
cuatro buscan ahora en el **contenido completo** de cada nota:

- **Búsqueda global** (`Ctrl+Shift+F`): cada resultado de nota muestra como subtítulo el **fragmento de
  texto** donde apareció la coincidencia, en vez de la ruta del fichero, y abrirlo lleva el cursor a **esa
  línea** del editor en lugar de al principio de la nota. Los aciertos por título o por etiqueta siguen
  mostrando la ruta.
- **Quick Switcher** (`Ctrl+P`), **buscador de la rejilla de Notas** y **buscador de la vista
  simplificada**: mismo cambio de alcance. En el Quick Switcher un acierto de título sigue pesando más que
  uno de etiqueta, y ambos más que uno de contenido — sigue siendo, ante todo, un salto por nombre de nota.
- La búsqueda global y el Quick Switcher recorren ahora **todo el vault, sesiones incluidas**. Antes solo
  miraban la raíz, de modo que ninguna nota guardada dentro de una sesión aparecía nunca en ninguno de los
  dos. Las notas de una sesión bloqueada se siguen quedando fuera, como en el resto de la app.
- Sin índice nuevo ni almacén nuevo: se lee a través de la caché de notas que `VaultService` ya mantiene
  (invalidada por fecha de modificación y por cada escritura), así que buscar dos veces seguidas no vuelve
  a tocar el disco.

**Contraseña mínima de una sesión protegida: 4 caracteres** (antes 8), y sigue sin haber ninguna regla de
composición — sirve cualquier combinación de letras, dígitos, símbolos, espacios o emoji. La contraseña
llega tal cual a PBKDF2-SHA256 sobre sus bytes UTF-8, así que ninguna combinación vale más que otra, y Enki
es una app local de un solo usuario: exigir una contraseña larga solo conseguía que no se usasen sesiones
protegidas.

**Correcciones en las sesiones protegidas con contraseña**: cuatro fallos silenciosos que afectaban al
contenido del usuario.

- **Mover una nota o un adjunto cruzando la frontera de una sesión cifrada** movía los bytes sin
  convertirlos. Arrastrar una nota del vault a una sesión protegida dejaba texto plano dentro de una
  carpeta que se lee descifrando, así que la nota volvía a abrirse **en blanco**; arrastrarla hacia fuera
  dejaba el ciphertext en el vault, y la nota se abría como **basura binaria**. Ninguno de los dos casos
  avisaba de nada ni tenía arreglo desde la interfaz. Ahora el contenido se descifra y se vuelve a cifrar
  según corresponda —incluido el paso directo de una sesión protegida a otra, que se re-envuelve bajo la
  clave del destino— y el destino se escribe antes de borrar el origen, de modo que un fallo a mitad deja
  un duplicado y no un hueco. Mover algo hacia o desde una sesión bloqueada se rechaza y deja el fichero
  donde estaba, en vez de continuar adivinando.
- **Un `.session.json` ilegible o corrupto** hacía que la sesión se tratase como texto plano: la siguiente
  escritura guardaba la nota **sin cifrar** dentro de la carpeta protegida. Ahora falla cerrado — la sesión
  se comporta como una bloqueada (nombre y número de notas visibles en la barra lateral, contenido
  inaccesible) hasta que el manifiesto se repare, y el fallo no se cachea, así que repararlo devuelve el
  acceso sin reiniciar.
- **Enumerar las notas de una sesión bloqueada** lanzaba `SessionLockedException` desde dentro de un
  `Select` de LINQ en vez de excluirla, dejando un cierre inesperado latente en cualquier pantalla que
  listase notas sin comprobar antes el estado de bloqueo. Ahora se excluye, que es lo que ya hacía el resto
  de la app.
- **Borrar una sesión protegida** (moverla a la papelera) dejaba su clave desbloqueada colgando en memoria
  bajo una ruta que ya no existe; una sesión nueva que reutilizase ese nombre de carpeta nacía marcada como
  desbloqueada con una clave que no era la suya. Ahora se bloquea antes de moverla.

De paso, `VaultService` memoriza si una sesión está cifrada (validado contra la fecha del manifiesto) en
vez de deserializar `.session.json` en cada lectura y cada escritura de contenido, incluido el autoguardado
del editor cada 600 ms.

**Dos vistas para la página de Notas**: nuevo ajuste **Ajustes › Editor › Vista del editor** (Completa /
Simplificada, persistido en `NotesViewMode` y aplicado en caliente, sin cerrar ni reabrir nada). La vista
completa es la de siempre; la simplificada es una disposición mínima al estilo Obsidian, pensada para
escribir sin distracciones:

- Panel lateral con un **árbol de carpetas y notas** de la sesión activa (solo markdown), con renombrado en
  línea, menú contextual (nueva nota aquí, nueva subcarpeta, renombrar, eliminar) y arrastrar una nota
  sobre una carpeta —o sobre el fondo, que es la raíz de la sesión— para moverla. A la derecha, las mismas
  pestañas y el mismo editor de siempre; abrir una nota ya abierta salta a su pestaña en vez de duplicarla,
  y el árbol resalta y despliega la rama de la nota activa se abra desde donde se abra.
- Encima del árbol, los botones de **nueva nota**, **nueva carpeta** (ambos en la raíz de la sesión),
  **nota diaria** y **contraer todo**; encima del panel, los modos **árbol** y **buscar** —búsqueda
  recursiva acotada a la sesión activa— y el botón de mostrar/ocultar el panel; abajo, la **sesión
  seleccionada** con su desplegable. Con el panel oculto queda un raíl con solo esas dos salidas: volver a
  mostrarlo y cambiar de sesión.
- El panel se ensancha arrastrando su separador, y tanto su ancho como si está desplegado y qué carpetas
  quedan abiertas sobreviven a reiniciar la app.
- Esta vista deja fuera a propósito la **barra de formato** y el **panel lateral de esquema/enlaces/
  estadísticas**, junto con sus interruptores de la cabecera, el botón de volver (lo hace la "x" de la
  pestaña), la etiqueta de texto del indicador de guardado (la pestaña ya lleva su punto de "sin guardar")
  y la caja de etiquetas. Los interruptores del usuario no se pisan: se conservan y vuelven a aplicarse al
  volver a la vista completa.
- La cabecera del editor se queda ahí en **el nombre de la nota, en pequeño, y las acciones** —el nombre
  baja de 24px a 13px y sigue siendo donde se renombra—, para que todo el peso visual se lo lleve el
  espacio de edición.
- De paso, la cabecera aguanta anchos pequeños sin recortar nada que haga falta pulsar: lo que cede
  espacio es el nombre de la nota, que además está entero en la pestaña.

**HTML simple en el editor de notas**: el editor vuelve a interpretar un subconjunto fijo de HTML en vez
de imprimirlo como texto plano, para poder centrar, alinear a la derecha o justificar un bloque de la nota:

- Contenedores de bloque `<div>`, `<p>` y `<center>` con `align="left|center|right|justify"` (o el
  equivalente `style="text-align: …"`). La alineación se resuelve para todo el documento, así que se aplica
  a cada línea que el contenedor envuelva, no solo a la que lleva la etiqueta, y los bloques anidados
  restauran la alineación del que los contiene al cerrarse.
- Formato en línea: `<b>`/`<strong>`, `<i>`/`<em>`, `<u>`/`<ins>`, `<s>`/`<strike>`/`<del>`, `<mark>`,
  `<code>`, `<span>` y `<br>`, con el mismo aspecto que su equivalente Markdown (una negrita anidada dentro
  de una cursiva se pinta como ambas, no como la última que cierre).
- Los cierres cuentan igual que las aperturas, incluso a varias líneas de distancia: un `<i>` que abre en
  una línea sigue en cursiva hasta el `</i>`, esté donde esté, y un `</b>` cierra también lo que quedara
  abierto dentro de él (`<b><i>x</b>` acaba ahí, no arrastra la cursiva al resto de la nota). Un cierre sin
  apertura se ignora, y la exportación reabre y cierra cada etiqueta bloque a bloque para que el HTML
  resultante quede balanceado.
- Las etiquetas se ocultan como cualquier otro marcador Markdown y reaparecen —atenuadas— cuando el cursor
  entra en ellas. Lo que no está en esa lista (`<cualquiercosa>`), lo que va dentro de comillas invertidas
  o de un bloque ``` y lo escapado con `\<div>` se sigue viendo literal.
- La exportación a HTML/PDF emite los mismos contenedores con su `text-align` (HTML5 ya no reconoce el
  atributo `align`), saneados: de la etiqueta original solo sobrevive la alineación, nunca atributos
  arbitrarios de la nota.
- La alineación funciona fila visual a fila visual, no solo en la primera: un párrafo largo que se parta en
  cuatro filas se centra (o se pega a la derecha) fila por fila, y `justify` estira los espacios de cada
  fila hasta el borde de la columna dejando la última en bandera, igual que un navegador. AvaloniaEdit da
  la misma alineación (izquierda) a todas las filas visuales y no expone ningún ajuste, así que el editor
  lee del propio motor de texto dónde partió cada fila y cuánto mide, y reserva el hueco que falta; el
  cálculo converge en una pasada porque el hueco que añade nunca puede mover el corte de fila.

**Dashboard 100% personalizable**: la organización "Personalizada" pasa de repartir las secciones en un
`UniformGrid` de N columnas (auto-relleno por orden de lista, sin control real) a un editor de rejilla en
Ajustes › Dashboard donde cada sección tiene su celda y su tamaño:

- Rejilla configurable de 1 a 12 filas y columnas (antes el tope eran 6, y solo columnas). Cada ficha se
  arrastra a la celda que se quiera y se estira desde su esquina inferior derecha; mientras se arrastra,
  una silueta muestra el destino y se marca en rojo si ahí no cabe. Un movimiento inválido se rechaza en
  vez de empujar a las vecinas.
- Todo se puede hacer sin ratón: con una ficha seleccionada, las flechas la mueven, `Mayús`+flechas la
  redimensionan y `Supr` la saca de la rejilla; la barra bajo el lienzo repite esas acciones como botones
  con nombre accesible. Las secciones fuera de la rejilla quedan en una bandeja y vuelven de un clic, a su
  posición anterior si sigue libre.
- Pasar de una plantilla (Clásica, Pirámide invertida, Patrón lateral, Rejilla modular, Mosaico) a
  Personalizado parte de la geometría de esa plantilla en vez de un lienzo en blanco — botón
  "personalizar esta organización" junto a la vista previa. "Auto-organizar" compacta la rejilla
  conservando el tamaño de cada ficha.
- La geometría guardada se sanea siempre antes de pintarse: una sección nueva que no existía cuando se
  guardó el layout (p. ej. Hábitos), una rejilla que ha encogido o un `settings.json` editado a mano ya no
  pueden dejar dos secciones una encima de otra ni hacer desaparecer ninguna.
- El Dashboard y la vista previa de Ajustes dejan de verter las secciones en un `Grid` de 6 columnas
  escrito a mano y usan un panel propio (`Controls.DashboardGridPanel`) que mide las filas por su
  contenido y reparte columnas según el ajuste del vault.

Correcciones relacionadas en Ajustes › Dashboard:

- Al abrir Ajustes con una organización ya guardada, la vista previa dibujaba siempre la disposición
  Clásica aunque la tarjeta marcada fuera otra: la plantilla activa no se leía del vault hasta que se
  pulsaba una tarjeta.
- "Restablecer" devolvía el vault a la organización Clásica pero dejaba la página mostrando la anterior.
- En modo Personalizado la lista de secciones ocupa el ancho completo, en vez de dejar media página vacía
  donde iba la vista previa de plantilla.

Sistema de **temas personalizados** rediseñado para comportarse como un producto publicable:

- El editor de temas incluye selector de color (`ColorPicker`) junto al campo HEX, botón de duplicar en
  la lista y dentro del editor, y exportación con título y errores localizados.
- El normalizador HEX acepta `#RGB`, `#RRGGBB` y variantes sin `#`/en minúsculas, en lugar de forzar a
  negro cualquier formato que no fuera exactamente `#RRGGBB`.
- El resaltado de búsqueda en Markdown y el fondo de la línea actual del editor ahora leen del tema
  activo (`HighlightColor`/`InkBrush`) en vez de blanco/amarillo hardcodeados.
- El grafo de sesiones deja de usar blancos literales para sus texturas y trazados; sigue el color de
  tinta (`InkBrush`) del tema.
- La ventana ya no arranca forzosamente en oscuro: `App.axaml` aplica la variante guardada antes de que
  se muestre la ventana principal.
- Las etiquetas del editor de temas se recargan al cambiar el idioma de la UI.

Nueva funcionalidad: **paleta de comandos unificada** (propuesta 25 de
`docs/propuesta-nuevas-funcionalidades.md`):

- Atajo `Ctrl+Shift+K` abre un popup (mismo patrón de UI que el Quick Switcher y la búsqueda global) con
  búsqueda por substring sobre el nombre de cada acción — no solo notas: navegar a cualquier sección de la
  barra lateral, abrir Ajustes/Recordatorios/Notificaciones, alternar la barra lateral o el tema
  claro/oscuro, zoom de interfaz, nota nueva/diaria, importar notas, alternar modo enfoque, nueva columna
  Kanban, abrir la carpeta del vault, Skills de la comunidad y sincronizar Polar.
- La lista de comandos se reconstruye cada vez que se abre el popup en vez de fijarse una sola vez al
  arrancar, así que entradas que dependen de estado en vivo (qué secciones del sidebar están activas, si
  hay una pestaña de nota abierta, si la cuenta Polar está vinculada) nunca quedan obsoletas ni se ofrecen
  cuando no tendrían ningún efecto.
- Ctrl+K a secas se dejó libre para el editor de notas (ya lo usa para insertar un enlace); el atajo de la
  paleta sigue el mismo criterio que ya distingue Ctrl+F de Ctrl+Shift+F.

Nueva funcionalidad: **modo enfoque/zen en el editor** (propuesta 7 de
`docs/propuesta-nuevas-funcionalidades.md`):

- Botón dedicado en la cabecera del editor (junto al de mostrar/ocultar panel lateral) y atajo
  `Ctrl+Shift+Enter` que ocultan cabecera, barra de formato, tarjetas vinculadas y panel lateral de la nota
  activa, además de la barra de pestañas y el sidebar de la app — dejando solo el cuerpo de la nota
  centrado. `Escape` (con el foco en el editor) o el mismo atajo lo desactivan.
- Es un modo compartido entre pestañas (activarlo desde una nota lo aplica a cualquiera que se abra
  después) pero deliberadamente transitorio: no se persiste en disco y siempre arranca apagado al iniciar
  la app o cambiar de vault.

Nueva funcionalidad: **exportar notas a PDF/HTML y vista de impresión** (propuesta 6 de
`docs/propuesta-nuevas-funcionalidades.md`):

- "Exportar como HTML" (documento autónomo monocromo, con etiquetas incluidas) e "Imprimir / PDF" desde el
  menú contextual de cada nota y desde la cabecera del editor.
- La vista de impresión es un overlay a pantalla completa dentro de la ventana principal (no una ventana
  secundaria, para respetar el zoom de interfaz) con previsualización real del documento, opción de
  incluir o no las etiquetas, impresión vía diálogo del sistema y guardado como PDF — ambos a través del
  motor WebView ya integrado, sin dependencias nuevas.
- El render Markdown→HTML reutiliza el mismo parser que el live preview del editor e incrusta las imágenes
  locales referenciadas como data URI para que el HTML exportado sea autocontenido.

Cierra la Fase 10 (opcional) del roadmap de corrección de deficiencias (`docs/analisis-proyecto/roadmap.md`)
— deuda técnica menor y mejoras de producto derivadas de la auditoría, cajón de sastre deliberado:

- Recurrencia local nativa para Recordatorios (independiente de Polar): un recordatorio recurrente se
  reprograma solo a su siguiente ocurrencia en vez de tener que recrearse a mano cada vez.
- "Deshacer" al descartar un recordatorio de la bandeja del shell, con una ventana de unos segundos antes de
  que el descarte se vuelva permanente.
- Los descartes de recordatorios (`dismissed-reminders.json`) se purgan cuando la tarjeta asociada se borra
  para siempre, en vez de quedar huérfanos.
- El WebView del grafo de sesiones se dispone explícitamente al salir de la pestaña — evitaba un motor de
  navegador nativo huérfano por cada ciclo de entra/sale.
- Cancelación de una búsqueda global en vuelo cuando el usuario sigue tecleando, en vez de arriesgarse a que
  una búsqueda lenta pise con resultados obsoletos a una más reciente.
- Intervalo y retención del backup automático (Ajustes › General) ganan control propio en la UI — antes
  solo el interruptor de activación era visible.
- `AnalysisLevel` del analizador de .NET subido a `latest-recommended` en el proyecto principal, con los
  ~116 diagnósticos resultantes corregidos caso a caso (mayormente `CultureInfo` explícito en formateo de
  texto de UI vs. datos internos).
- Icono de aplicación real (`.ico`/`.icns`/PNG), sustituyendo el icono genérico del SO.
- `README.en.md` — versión en inglés, dado que la app ya soporta 6 idiomas de UI.
- Decisión documentada (sin implementar): mantener la composición manual de servicios del shell en vez de
  introducir un contenedor de DI — ver `docs/analisis-proyecto/decision-contenedor-di.md`.

Cierra también la Fase 9 (opcional) — robustez de sincronización Polar y notificaciones, ninguno de sus
hallazgos compromete la integridad de los datos:

- Reconexión automática de `PolarRealtimeService` tras una caída del socket (reutiliza el backoff del
  temporizador de sincronización en vez de un temporizador paralelo propio).
- Los tres botones de "Sync now" (Recordatorios, Gestión de tareas, Ajustes › Cuenta) distinguen un sync
  completado de uno descartado porque otro ya estaba en curso.
- Reintento periódico (cada hora) del aviso de fallo de sincronización mientras el problema persista, no
  solo en la transición inicial.
- Aviso al usuario cuando una edición local pierde un conflicto de sincronización (Last-Write-Wins) frente a
  una versión remota más reciente.

## [0.1.0] — 2026-08-21

Primera versión con número real (`enki/Directory.Build.props`, antes generaba `1.0.0.0` por defecto sin
que nadie lo hubiera decidido) y con este propio changelog. No es un "1.0" porque la app todavía no ha
tenido una release pública ni un empaquetado real para usuarios finales — ver
`docs/analisis-proyecto/roadmap.md`, Fase 8, para el resto de higiene de release pendiente
(instalador/paquete distribuible).

Cierra las Fases 0–8 del roadmap de corrección de deficiencias
(`docs/analisis-proyecto/roadmap.md`), sobre una auditoría completa del código existente:

- **Fase 0** — Red de seguridad mínima: logging a fichero, captura de excepciones no observadas.
- **Fase 1** — Persistencia atómica del contenido de usuario (notas, skills, sesiones, ajustes) y backup
  automático opcional.
- **Fase 2** — Recordatorios: corrección de notificación perdida tras reprogramar y de deriva horaria en
  tareas recurrentes de Polar.
- **Fase 3** — Borrado seguro: papelera y confirmaciones consistentes en Kanban/Notas/Sesiones.
- **Fase 4** — Seguridad de credenciales y aislamiento de cuentas Polar (tokens redactados en
  exportaciones/backups, filas `Dirty` aisladas por cuenta).
- **Fase 5** — Integridad de la red de enlaces (wikilinks): reescritura al renombrar, resolución
  unificada, distinción visual de enlaces rotos/ambiguos.
- **Fase 6** — Rendimiento del editor y del vault: debounce de backlinks/outline/stats, caché de
  metadatos de notas, tamaño de vault asíncrono.
- **Fase 7** — Accesibilidad: `AutomationProperties.Name` en las 17 vistas, vía alternativa a
  drag&drop para mover tarjetas Kanban, atajos de teclado.
- **Fase 8** — Higiene de documentación y operaciones de release: enlaces de documentación reparados,
  LICENSE, CI (`dotnet build`/`dotnet test` en cada push/PR), analizadores de warnings-como-errores,
  metadatos de build (`Company`/`Copyright`/`Version`), este changelog, y guía de empaquetado para
  usuarios no técnicos.

Detalle completo de cada punto (fichero:línea, tests añadidos, criterio de aceptación) en
`docs/analisis-proyecto/roadmap.md`.
