# Análisis detallado — Enki alfa-3

> Ver [00-README.md](00-README.md) para alcance, metodología y relación con la auditoría anterior ya
> resuelta. Cada hallazgo indica **perspectiva de origen** (Dev / UX / Usuario), **categoría** (bug / gap
> / mejora / verificado-correcto), **severidad** (Crítica/Alta/Media/Baja), **esfuerzo** estimado
> (Alto/Medio/Bajo) y evidencia `archivo:línea`. Las rutas son relativas a `enki/src/enki/` salvo que se
> indique lo contrario.

---

## 1. Resumen ejecutivo

Enki alfa-3 parte de una base notablemente sólida: la auditoría anterior (fases 0-10, todas cerradas)
resolvió con cuidado real los problemas más graves de una primera pasada (atomicidad de escritura,
recordatorios que dejaban de notificar, borrados irreversibles, aislamiento de cuentas Polar,
accesibilidad, CI). Esta auditoría confirma que **ninguna de esas correcciones se ha regresionado**.

Sin embargo, el trabajo más reciente y menos probado — sesiones con contraseña (cifrado de sobre AES-GCM),
el filtro de grupos del grafo, la reorganización del vault, y varias funcionalidades de producto añadidas
sin tests propios (comandos de barra oblicua, exportación de notas, hábitos/estadísticas) — introduce
**tres bugs críticos nuevos** y un patrón transversal de inconsistencia (`ModalOverlayHost`) que cuatro de
los cinco frentes de la auditoría detectaron de forma independiente.

### Los 3 bugs críticos nuevos

| # | Problema | Módulo | Impacto |
|---|---|---|---|
| 1 | `SessionLockedException` se propaga sin capturar desde el autoguardado del editor y desde la restauración de pestañas al arrancar | Sesiones/Notas | Crash reproducible de toda la app |
| 2 | El comando de barra oblicua `/tabla` inserta el literal `Editor_SlashCommand_Table_InsertText` en la nota (clave de recurso inexistente en los 7 `.resx`) | Editor | Funcionalidad anunciada, completamente rota, silenciosa |
| 3 | La heurística de "esto ya es un vault" en la configuración inicial no reconoce un vault cuyas notas viven solo en subcarpetas | Vault/Onboarding | Un vault real y existente parece desaparecer (los datos no se pierden, pero quedan invisibles) |

### El hallazgo transversal más repetido

**`controls/ModalOverlayHost.cs`** es un control nuevo, bien diseñado, que corrige un bug real: los
diálogos modales antiguos (`Border Classes="modal-overlay"`) no podían escapar visualmente de los límites
de su propia página, así que la barra lateral y los botones flotantes de la title bar seguían siendo
clicables debajo del "modal". Se migró **una sola vista** (`NotificationsView.axaml`). Las auditorías de
Sesiones, Kanban/Papelera, Calendario/Recordatorios/Hábitos y Archivos/Ajustes detectaron, cada una por su
cuenta, que sus vistas respectivas siguen con el patrón antiguo — **entre 8 y 9 vistas sin migrar**,
incluidos precisamente los diálogos más sensibles del release (contraseña de sesión, códigos de
recuperación, borrado permanente).

### Otros patrones que se repiten en más de un módulo

- **Confirmación de borrado inconsistente**: Kanban/Skills/Calendario/Hábitos respetan
  `ConfirmBeforeDelete`; Archivos y Notas no lo comprueban; Polar (tareas en la nube) no tiene papelera en
  absoluto, a diferencia de todo lo demás en la app.
- **Paridad Kanban↔Polar incompleta**: mismo concepto de producto ("tablero de tareas"), pero Polar carece
  de reordenar por *drag&drop*, de papelera, y de cambiar prioridad con un clic — todo lo cual sí existe en
  Kanban.
- **Tarjetas completadas que se siguen contando como vencidas**: `CalendarAggregationService` fija
  `IsCompleted = false` para todo ítem de Kanban, así que una tarjeta terminada sigue apareciendo
  "vencida" en Calendario, Dashboard y la bandeja de vencimientos del shell.
- **Funcionalidades de producto nuevas sin ningún test**: filtro de grupos del grafo, comandos de barra
  oblicua, `PolarTaskManagementViewModel` (más de 500 líneas, ~30 comandos, solo 2 tests).

---

## 2. Metodología y qué NO se repite aquí

Se comprobó puntualmente (no exhaustivamente) que las correcciones de la auditoría anterior siguen
vigentes: escritura atómica (`VaultService.WriteFileAtomic`), `PathComparison`/`PathsEqual` para Linux,
`NoteTagHelper.SetTags` preservando frontmatter ajeno, resolución unificada de wikilinks, reseteo de
`Reminder.IsTriggered` al reprogramar, guarda de saltos de reloj en `PopDueReminders`,
`DismissedReminderService` con tope y purga, aislamiento de cuentas Polar por `UserId`, redacción de
tokens en exportación/backup, validación HTTPS de `SupabaseConfig`, reconexión de `PolarRealtimeService`,
distinción de resultados de "Sync now", accesibilidad (`AutomationProperties` presente y consistente en
Kanban/Calendario/Trash/Settings), `CornerRadius="0"` sin excepciones, CI verde con `TreatWarningsAsErrors`.
**Ninguna de estas está regresionada.** No se listan como hallazgos nuevos salvo excepción explícita.

El riesgo ya documentado y aceptado por decisión del propietario del proyecto —tokens de sesión de
Supabase en texto plano en `settings.json`— tampoco se repite aquí como hallazgo nuevo: sigue siendo una
decisión consciente, registrada en
`docs/agent-docs/analisis-proyecto/decision-almacenamiento-credenciales-polar.md`.

---

## 3. Inventario de funcionalidades (delta desde la auditoría anterior)

El inventario completo por módulo ya está documentado en
`docs/agent-docs/inventario-funcionalidades-y-mejora-continua.md` y sigue siendo estructuralmente preciso.
Esta auditoría confirma que, desde entonces, se han añadido las siguientes funcionalidades (todas
verificadas en el código, todas cubiertas por los hallazgos de las secciones siguientes):

- **Sesiones con contraseña**: cifrado de sobre (clave de datos AES-GCM envuelta con una clave derivada
  por PBKDF2-SHA256/210k iteraciones desde la contraseña), 5 códigos de recuperación de un solo uso
  generados en la creación, flujo de bloqueo/desbloqueo, exclusión de sesiones bloqueadas del grafo/lista
  de archivos/vista previa de PDF, descifrado bajo demanda con limpieza "automática" de temporales.
- **Filtro de grupos en el grafo de sesiones** (`GraphGroupFilterItemViewModel`, en desarrollo, sin commit).
- **Personalización de la barra lateral**: mostrar/ocultar cada sección de forma independiente
  (Dashboard, Kanban, Notas, Archivos, Sesiones, Papelera, Hábitos, Estadísticas, Notificaciones,
  Recordatorios).
- **"Mostrar en el sistema"** (revelar en el explorador de archivos del SO) para notas, vía
  `FolderOpener` — no implementado aún para Archivos.
- **Reorganización de la carpeta del vault**: en vez de convertir cualquier carpeta general en el vault
  raíz, ahora anida una subcarpeta `enki-documents/`.
- **`AppDataDirectory`**: aísla los datos de una compilación Debug (`.devdata/`) de una instalación
  Release real.
- **Paleta de comandos unificada** (`Ctrl+Shift+K`).
- **Modo enfoque/zen** en el editor (`Ctrl+Shift+Enter`).
- **Exportación de notas a HTML** (el PDF/impresión vía WebView mencionado en comentarios de código y en
  `docs/propuesta-nuevas-funcionalidades.md` **no está implementado** — ver hallazgo §4.2-H8).
- **Editor de temas personalizado** con selector de color (`ColorPicker`), duplicar, exportar.
- **Recurrencia nativa en Recordatorios** (sin necesitar cuenta Polar).
- **`ModalOverlayHost`**: control de overlay que cubre toda la ventana, migrado solo a Notificaciones.

---

## 4. Hallazgos por módulo

### 4.1 Sesiones, protección con contraseña y grafo

**Contexto**: el commit más reciente (`f40e6db`) añadió cifrado de sobre completo para sesiones
protegidas. Es la superficie de código más nueva y más sensible en seguridad de todo el proyecto — se le
dedicó la revisión más profunda.

#### Crítico

- **[Dev/Usuario] Crítica** — `SessionLockedException` (`models/SessionLockedException.cs`) se deja
  propagar deliberadamente desde `SessionEncryptionService.Decrypt`/`VaultService.SafeReadContent`
  (`services/VaultService.cs:547-575`), pero **solo `FilesViewModel` la comprueba de forma proactiva**
  (`viewmodels/files/FilesViewModel.cs:42-48,157`). El camino de Notas/Editor no la captura en ningún
  punto:
  - `EditorViewModel.Save()` (`viewmodels/editor/EditorViewModel.cs:395-413`) solo atrapa
    `IOException`/`UnauthorizedAccessException`, invocado sin protección adicional desde el
    `_saveTimer.Tick` (línea 304).
  - `NotesViewModel.RestoreOpenTabs()` (`viewmodels/notes/NotesViewModel.cs:229-240`) llama a
    `GetNote(path)` sin ningún `try/catch`, invocado desde el constructor de la shell
    (`MainWindowViewModel.Composition.cs:342`).
  - `NotesViewModel.TryReadContentForExport` (`NotesViewModel.cs:451-465`) tiene el mismo hueco.

  **Escenario reproducible (crash al arrancar)**: desbloquear una sesión protegida → abrir una nota suya
  en una pestaña → cerrar la app (la pestaña persiste vía `OpenNotePaths`). En el siguiente arranque toda
  sesión protegida empieza bloqueada (la caché de claves en memoria es por proceso) — `RestoreOpenTabs()`
  intenta leer esa nota y lanza la excepción sin nada que la capture, en plena inicialización de la shell.

  **Escenario reproducible (crash en caliente)**: abrir una nota de una sesión protegida, dejar la pestaña
  abierta, ir a Sesiones y pulsar "Bloquear" sobre esa sesión, seguir escribiendo en la pestaña (o
  simplemente cerrar la app — `MainWindow.axaml.cs:66-74` fuerza el volcado de todos los editores). El
  autoguardado o el volcado al cerrar llaman a `Save()` → `WriteFileAtomic` → `Encrypt` → lanza, sin captura.

  Es exactamente la clase de bug que la Fase 1 de la auditoría anterior ya cerró para errores de E/S — la
  nueva excepción de cifrado reabre el mismo agujero por un camino que aquella corrección no anticipaba.

#### Alta

- **[UX/Usuario] Alta** — Bloquear una sesión no toca las pestañas de editor ya abiertas sobre sus notas.
  `SessionsViewModel.LockSession` (`viewmodels/sessions/SessionsViewModel.cs:489-495`) solo llama a
  `SessionService.Lock` y actualiza el ítem de la lista; no inspecta `NotesViewModel.OpenEditors`. El
  candado se ve cerrado en Sesiones, pero cualquier pestaña ya abierta sigue mostrando y dejando editar el
  contenido en texto plano — y, combinado con el hallazgo anterior, seguir escribiendo ahí revienta la app
  en el siguiente autoguardado.
- **[UX/Dev] Alta** — `ModalOverlayHost` (control nuevo, corrige que los diálogos no podían escapar de los
  límites de su página) se aplicó solo a Notificaciones. Los cuatro diálogos nuevos de Sesiones —crear
  sesión, mostrar códigos de recuperación, desbloquear/olvidé mi contraseña, borrar sesión— siguen con el
  patrón antiguo `Border Classes="modal-overlay"` (`views/sessions/SessionsView.axaml:242,283,332,399`).
  Son, precisamente, los diálogos de contraseña y recuperación los que quedan con la barra lateral y los
  botones de la title bar seguir siendo clicables debajo.
- **[Dev/Seguridad] Alta** — Los archivos temporales descifrados pueden sobrevivir indefinidamente a un
  cierre o crash de la app. `VaultService.ResolveReadablePath` (`services/VaultService.cs:289-298`) escribe
  una copia en claro en `Path.GetTempPath()` (`enki-dec-{guid}{ext}`); la limpieza es responsabilidad del
  llamador y best-effort. Para la vista previa de PDF, solo ocurre en `Close()`, que está documentado como
  "no se llama en cada cierre de vista previa, el documento se mantiene deliberadamente"
  (`viewmodels/files/PdfDocumentViewModel.cs:225-232`). No hay ningún gancho de apagado: ni `Program.cs` ni
  `MainWindow.axaml.cs:66-74` llaman a limpieza de temporales ni a `SessionEncryptionService.LockAll()` —
  que, pese a que su propio comentario dice que "se llama al apagar la app para que nada descifrado
  sobreviva al proceso", **no se invoca en ningún punto del código** (confirmado por grep). Abrir un PDF de
  una sesión protegida y cerrar la app (o que crashee) deja el PDF descifrado en el `/tmp` compartido del
  sistema, sin limpieza ni al reiniciar.

#### Media

- **[Dev/Seguridad] Media** — Los temporales descifrados se crean con permisos por defecto del SO, sin
  endurecerlos (`File.WriteAllBytes`, sin `UnixFileMode` restrictivo) — en una máquina Linux compartida,
  otro usuario local podría leerlos según el `umask`.
- **[Dev] Media** — Código muerto: `VaultService.WriteTextFile` (`services/VaultService.cs:681-686`) es un
  duplicado exacto de `WriteFileAtomic`, con el mismo comentario XML-doc copiado, sin ningún llamador en
  todo el repo — resto de un refactor abortado.
- **[Dev] Media** — El filtro de grupos del grafo (`GraphGroupFilterItemViewModel`,
  `SessionsViewModel.RebuildGraphGroupFilters`/`PublishGraph`/`OnGraphFilterToggled`,
  `viewmodels/sessions/SessionsViewModel.cs:314-390`) no tiene ningún test — es la funcionalidad menos
  revisada de todo el árbol de trabajo, y contiene lógica no trivial (preservar selección al reconstruir,
  ocultar aristas cruzadas cuando solo un extremo queda filtrado).
- **[Dev] Media** — `SessionService.ComputeGlobalWikiLinkEdges` (`services/SessionService.cs:660-700`)
  generaliza la resolución de títulos/nombres ambiguos a todo el vault, incluyendo una rama de
  "nombre de archivo ambiguo" que no podía darse antes a escala de una sola sesión — sin ningún test que
  ejercite dos notas de distintas sesiones compartiendo título o nombre de archivo.
- **[Dev/Seguridad] Media** — Sin límite de intentos ni retraso progresivo en desbloqueo o reseteo por
  código de recuperación (`SessionsViewModel.ConfirmUnlock`/`ConfirmPasswordReset`,
  líneas 429-480) — la única fricción ante fuerza bruta es el coste de PBKDF2 (210k iteraciones).
- **[UX/Dev] Media** — Las operaciones de cifrado se ejecutan de forma síncrona en el hilo de UI sin
  ningún indicador de carga: `ConfirmCreateSession` hace 6 derivaciones PBKDF2-210k (contraseña + 5
  códigos) dentro de un `[RelayCommand] private void` normal
  (`viewmodels/sessions/SessionsViewModel.cs:511-539`), sin `Task.Run` ni spinner — inconsistente con el
  resto del código, que sí mueve trabajo de disco fuera del hilo de UI.
- **[UX/Usuario] Media** — Los códigos de recuperación copiados al portapapeles
  (`CopyRecoveryCodesToClipboard`, `SessionsViewModel.cs:579-591`) no se limpian automáticamente después de
  un tiempo — quedan legibles por cualquier otra app con acceso al portapapeles indefinidamente.

#### Baja

- **[Accesibilidad] Baja** — Los nuevos campos de contraseña/código de recuperación
  (`views/sessions/SessionsView.axaml:262-265,344-345,371-376`) no tienen `AutomationProperties.Name`,
  solo `PlaceholderText` — inconsistente con el resto de la app (53 usos en Kanban, 53 en Ajustes).
- **[Seguridad] Baja** — Las cadenas de contraseña y el array de bytes intermedio de
  `Encoding.UTF8.GetBytes(secret)` (`services/SessionEncryptionService.cs:219-220`) no se limpian de
  memoria administrada (limitación conocida de .NET con `string`, no un bug corregible sin cambiar el tipo
  de dato de entrada).
- **[UX] Baja** — Ninguno de los cuatro diálogos nuevos de Sesiones cierra con `Escape` (consistente con el
  resto de la app, no es una regresión, pero tampoco se aprovechó la ocasión de arreglarlo en la
  superficie más sensible).

#### Verificado correcto

- Uso de AES-GCM correcto: nonce CSPRNG único por llamada, disposición `nonce‖ciphertext‖tag`.
- Parámetros de KDF sólidos: PBKDF2-SHA256/210k iteraciones (por encima del mínimo OWASP 2023), sal de 16
  bytes única por `KeySlot`.
- Ciclo de vida de la DEK correcto donde de verdad se invoca: generada por CSPRNG, puesta a cero tras uso y
  al bloquear.
- Códigos de recuperación generados con `RandomNumberGenerator.GetInt32` (CSPRNG), alfabeto de 31 símbolos
  sin caracteres ambiguos (0/O/1/I/l), ~98 bits de entropía.
- Contraseña incorrecta vs. ciphertext corrupto se distinguen limpiamente vía el fallo de autenticación de
  AEAD, nunca una excepción sin tipar.
- `SessionService.LoadGraph`/`LoadGlobalGraph` comprueban `IsUnlocked` antes de tocar disco para una sesión
  bloqueada — sin ningún patrón "descifrar y capturar el fallo".
- El payload hacia el WebView del grafo se serializa siempre con `JsonSerializer.Serialize`, sin
  concatenación insegura — no hay inyección posible en el JS embebido.
- `FilesViewModel.IsSelectedSessionLocked` es el patrón correcto (comprobar antes de leer) — el que debería
  haberse replicado en Notas/Editor.
- Suite de tests de cifrado sustancial (`SessionEncryptionServiceTests`, `SessionServiceEncryptionTests`,
  `VaultServiceEncryptionTests`, `FilesViewModelEncryptionTests`, `SessionsViewModelEncryptionTests`) — los
  79 tests relacionados con sesiones pasan en verde con el árbol de trabajo actual.

---

### 4.2 Notas, Editor, Vault y Wikilinks

#### Crítico

- **[Dev/Usuario] Crítica** — El comando de barra oblicua `/tabla` está completamente roto por una clave
  de localización inexistente. `SlashCommandsViewModel.cs:30` referencia
  `InsertTextKey = "Editor_SlashCommand_Table_InsertText"`, clave que **no existe en ninguno de los 7
  `.resx`** (verificado, cero coincidencias). `SlashCommand.InsertText`
  (`models/SlashCommand.cs:24`) resuelve la clave vía `LocalizationService`, que en Release devuelve la
  propia clave como texto plano si no la encuentra. **Escribir `/tabla` y seleccionar "Insertar tabla"
  inserta el literal `Editor_SlashCommand_Table_InsertText` en la nota del usuario**, y el
  `CursorOffset = 53` hardcodeado cae mucho más allá del final de ese texto de repuesto (sin verificar si
  `TextEditor.CaretOffset` lo recorta o lanza). `SlashCommandsViewModel`/`SlashCommandItemViewModel` no
  tienen ningún test — un test que comprobara que `InsertText` resuelve a texto no-clave para cada entrada
  de `AllCommands` habría atrapado esto de inmediato.

#### Alta

- **[Dev/Usuario] Alta** — Sin ninguna comprobación de conflicto con cambios externos al archivo. No existe
  ningún `FileSystemWatcher` en todo el código, y `Save`/`WriteContent`
  (`viewmodels/editor/EditorViewModel.cs:395-420`, `services/VaultService.cs:627-632`) nunca comparan la
  fecha de modificación en disco contra la que tenía el archivo al cargarse. Si una nota abierta en una
  pestaña se modifica externamente (sincronización con Dropbox/Syncthing, edición desde otra app,
  restauración de un backup) mientras la pestaña sigue abierta, el siguiente autoguardado (600ms de
  debounce) sobrescribe esos cambios externos en silencio — sin aviso, sin fusión, sin "la nota cambió en
  disco, ¿recargar?". Para una app cuya promesa central es "tus notas son solo archivos", es una vía de
  pérdida de datos real y verosímil.
- **[Dev] Alta** — Los caminos de borrado de notas/carpetas se saltan el patrón de manejo de excepciones ya
  establecido para guardar/renombrar/mover:
  - `NotesViewModel.DeleteNote` (`viewmodels/notes/NotesViewModel.cs:692-702`) y
    `EditorViewModel.Delete` (`viewmodels/editor/EditorViewModel.cs:547-554`) llaman a
    `VaultService.DeleteNote` **sin ningún try/catch**, a diferencia de `NewNote`/`CommitRenameNote`/
    `MoveNoteToFolder` en el mismo fichero.
  - `VaultService.DeleteFolder` (`services/VaultService.cs:452-466`) hace `Directory.Move` directo, **sin
    try/catch en absoluto** — a diferencia de `MoveWithUniqueSuffix`, que envuelve la operación equivalente.
  - El mismo hueco en `RenameFolder` (`VaultService.cs:436-450`) y `RenameFile` (`VaultService.cs:1041-1054`).

  **Escenario**: vault en una unidad desconectada o carpeta sin permisos, el usuario borra una nota o
  carpeta (o renombra una carpeta/archivo) — la excepción se propaga sin nada que la capture, exactamente
  la clase de bug ("Fase 1, top 10 #2") que la auditoría anterior cerró para autoguardado y renombrado,
  pero que no llegó a cubrir el borrado ni el renombrado de carpetas/archivos.
- **[Usuario] Alta** — Las carpetas borradas son un callejón sin salida permanente e invisible en la
  Papelera. `VaultService.DeleteFolder` mueve la carpeta entera (con las notas dentro) a `.trash/`, pero no
  existe `GetTrashedFolders`, `RestoreFolder` ni `PermanentlyDelete(NoteFolder)` en ningún sitio de
  `VaultService`. `TrashViewModel` solo rastrea Notas/Tareas/Archivos/Recordatorios/Sesiones — las carpetas
  no son un concepto en absoluto en la pantalla de Papelera. Además, `GetTrashedNotes`/`GetTrashedFiles`
  usan `SearchOption.TopDirectoryOnly`, así que ni siquiera las notas individuales dentro de una carpeta
  borrada aparecen — son completamente invisibles. "Vaciar papelera" tampoco toca estas carpetas huérfanas,
  que además inflan permanentemente el tamaño de vault reportado en el Dashboard sin ninguna vía de
  recuperar ese espacio desde la app. Único camino de recuperación: navegar a mano a `<vault>/.trash/`
  desde el explorador del sistema operativo.

#### Media

- **[Dev] Media (rendimiento)** — `EditorView.axaml.cs:545-551` (`OnEditorTextChanged`) llama a
  `RecomputeFences()` **sin condición ni debounce** en cada pulsación de tecla, y esta a su vez hace un
  `Split('\n')` y recorrido lineal completo del documento
  (`views/editor/Markdown/MarkdownLineParser.cs:19-67`). Reintroduce exactamente el antipatrón que
  `services/editor/EditorDebounce.cs` se creó para eliminar (Fase 6 de la auditoría anterior) — pero el
  recómputo de fences para el resaltado de bloques de código en vivo nunca se enrutó por ese mecanismo.
- **[Accesibilidad] Media-Alta** — Solo 8 de ~40 elementos interactivos del editor tienen
  `AutomationProperties.Name`. En concreto, **las 15 herramientas de la barra de formato** (negrita,
  cursiva, tachado, H1-H3, cita, listas, enlace, imagen, código, regla horizontal —
  `views/editor/EditorView.axaml:121-219`) y **los 5 botones de Buscar/Reemplazar** (líneas 279-293) solo
  tienen `ToolTip.Tip`, sin nombre accesible — un lector de pantalla no obtiene ninguna etiqueta para toda
  la barra de formato ni el panel de búsqueda, pese a que el patrón correcto ya existe y se usa en otros
  botones del mismo editor (visibilidad de panel, modo zen).
- **[Dev] Media** — La exportación a HTML descarta imágenes con URIs `data:` preexistentes o rutas
  absolutas de Windows con letra de unidad. `NoteExportRenderer.IsSafeUrl`/`LooksLikeRelativePath`
  (`services/NoteExportRenderer.cs:453-460`) clasifican `data:image/...` y `C:\Users\...` como "esquema no
  seguro" (por el regex de detección de esquema) y descartan la imagen en silencio, mostrando solo el
  texto alternativo. Sin ningún test que cubra ninguno de los dos casos.
- **[Usuario/Dev] Media** — El export a PDF y la vista de impresión, mencionados en comentarios de código
  (`NoteExportRenderer.cs:15-21`, `Program.cs:19-24`) y en `docs/propuesta-nuevas-funcionalidades.md`
  (propuesta 6), **no están implementados**: solo existe "Exportar como HTML". El `WebViewHost` que esos
  comentarios describen para notas solo se usa realmente en el grafo de sesiones — documentación
  desfasada describiendo una funcionalidad inexistente.
- **[Dev] Media** — El analizador de énfasis Markdown no respeta los límites de palabra para guiones bajos:
  `MarkdownLineParser.ScanInline`/`FindClosingMarker` (`views/editor/Markdown/MarkdownLineParser.cs:253-278,
  380-392`) tratan `_` igual que `*`, sin la regla CommonMark de "flanking" intra-palabra. Escribir
  `my_variable_name` o `a_file_name.txt` en una nota pone en cursiva el fragmento intermedio, tanto en la
  vista previa en vivo como en la exportación HTML (comparten el mismo parser) — molesto para cualquiera
  que escriba notas técnicas. Sin ningún test de énfasis/negrita en `MarkdownLineParserTests.cs`.

#### Baja

- **[Dev] Baja** — El conteo de palabras/tiempo de lectura (`services/editor/EditorStatsService.cs:54-72`)
  es puramente delimitado por espacios en blanco — un párrafo en chino/japonés/coreano cuenta como "1
  palabra", con un tiempo de lectura completamente erróneo.
- **[Dev] Baja** — Cobertura de tests ausente en la tubería de vista previa en vivo:
  `MarkdownLivePreviewColorizer`, `MarkdownElementGenerator`, `MarkdownBackgroundRenderer`,
  `LivePreviewState`, `SlashCommandsViewModel`, `SlashCommandItemViewModel` y `FindReplaceViewModel` no
  tienen ningún test dedicado.
- **[Dev] Baja (rendimiento)** — `FindReplaceViewModel` hace un `IndexOf` sobre el documento completo en
  cada carácter tecleado en el cuadro de búsqueda, sin debounce — mismo patrón que el hallazgo de fences,
  de menor impacto por el tamaño típico de una nota.
- **[UX] Baja** — Buscar/Reemplazar no resalta todas las coincidencias a la vez, solo la actual —
  convención habitual en la mayoría de editores de texto que aquí falta.
- **[UX] Baja** — El modo zen es un flag global compartido por todas las pestañas
  (`EditorPreferences.IsZenMode`) — activar el modo enfoque en una nota y cambiar a otra pestaña ya abierta
  arrastra a esa otra pestaña al mismo modo, sin aviso. Comportamiento intencional y documentado en el
  código, pero potencialmente sorprendente para el usuario ("la UI se reseteó sola").
- **[Dev] Baja** — Código muerto: `VaultService.WriteTextFile` duplicado (ver también §4.1).

#### Verificado correcto

- Escritura atómica, comparación de rutas y preservación de frontmatter ajeno: sin regresiones.
- Resolución de wikilinks (nombre de archivo primero, título como respaldo, distinción roto/ambiguo)
  compartida de forma consistente entre Ctrl+click, backlinks, coloreado en vivo y reescritura al
  renombrar.
- `NoteExportRenderer` es sólido para el caso común: encabezados, todos los spans inline, listas/tareas/
  tablas anidadas, bloques de código con escape correcto, filtrado de esquemas seguros contra XSS,
  incrustación de imágenes locales como data URI con límite de tamaño — 25 tests dedicados cubriendo bien
  los caminos felices.
- El sistema de diseño (`CornerRadius="0"`, monocromo) se mantiene sin desviaciones en todas las vistas del
  editor y notas.

---

### 4.3 Kanban, Polar (gestión de tareas en la nube) y Papelera

#### Alta

- **[UX/Usuario] Alta** — No existe ningún camino de recuperación para una tarea/lista/subtarea Polar
  borrada, a diferencia de todo lo demás en la app. `DeleteTask`/`DeleteList`/`DeleteSubtask`
  (`services/polar/PolarTaskService.cs:96-244`) son soft-deletes filtrados de **todas** las rutas de
  lectura (`PolarTaskManagementViewModel`, `PolarTaskEditorViewModel`, `PolarTaskDetailViewModel`,
  `GlobalSearchViewModel`, `DashboardViewModel`, `CalendarViewModel`), pero nunca se muestran en ningún
  sitio de la UI otra vez. El único freno es el modal de confirmación previo al borrado — confirmado eso,
  desaparece para siempre desde la perspectiva del usuario, pese a que la categoría "Tareas" de la propia
  Papelera existe justo al lado y solo cubre tarjetas Kanban.
- **[UX/Usuario] Alta** — Sin ningún mecanismo de reordenar listas o tareas Polar. Kanban tiene
  drag&drop completo con retroalimentación visual (`views/kanban/KanbanView.axaml.cs:115-240`); Polar fija
  `OrderIndex` una sola vez en la creación y ningún comando lo vuelve a tocar — el usuario no puede
  reordenar un tablero Polar en absoluto, mientras que el "otro tablero" que la app presenta como parte del
  mismo producto sí lo permite libremente.

#### Media

- **[Dev] Media (duplicación)** — `PolarTaskEditorViewModel.cs:238-255` reimplementa a mano el mismo
  cálculo de rejilla de mes de 42 celdas que ya existe en `viewmodels/common/MonthGridCalculator.cs:19-28`,
  reutilizado por `KanbanCardEditorViewModel`, `CalendarViewModel` y `CalendarQuickCreateViewModel` — un
  bug en la matemática de la rejilla ahora habría que arreglarlo dos veces.
- **[Dev] Media** — Los tombstones de Polar (`IsDeleted=true`) nunca se purgan localmente, a diferencia de
  Kanban (`PurgeOldTrashedCards`, 30 días) y Recordatorios (`PurgeOldTombstones`) — `polar-tasks.json`
  crece sin límite con filas que nunca volverán a mostrarse.
- **[Dev/UX] Media** — El selector de hora límite es dos implementaciones distintas para el mismo concepto:
  Kanban usa un stepper propio "para encajar con el lenguaje visual del resto del formulario"
  (comentario propio en `KanbanCardEditorViewModel.cs:276-278`); Polar sigue con dos `NumericUpDown` de
  stock, y como consecuencia directa `DueHour`/`DueMinute` son `decimal` en uno y `int` en el otro. Mismo
  concepto, dos lenguajes visuales, dos tipos de dato.
- **[Dev] Media (sin confirmar en vivo)** — El `OrderIndex` de una lista de dependencias
  (`PolarTaskService.CreateTask`, línea 128) se asigna por un conteo puramente local — dos dispositivos
  creando tareas offline en la misma cadena de dependencias antes de sincronizar podrían producir el mismo
  `OrderIndex`, y `IsTaskBlocked` (empate) dejaría ambas tareas "desbloqueadas" simultáneamente, rompiendo
  la garantía de "una tarea cada vez" de la cadena.
- **[UX] Media** — Cambiar la prioridad de una tarjeta Kanban es un clic sobre el punto de color
  (`CyclePriorityCommand`); el mismo punto en una tarea Polar es un `Ellipse` no interactivo — cambiar la
  prioridad ahí exige abrir el editor completo.
- **[UX] Media** — Los diálogos de confirmación de Kanban/Papelera siguen con el patrón antiguo de overlay
  acotado a la página, no migrados a `ModalOverlayHost` (ver hallazgo transversal, §6).

#### Baja

- **[UX] Baja** — La categoría "Recordatorios" de la Papelera carece del botón "borrar para siempre" que
  todas las demás sí tienen, por una razón real (un recordatorio recién borrado suele seguir `Dirty` y no
  ha llegado aún al tombstone de Polar) — pero nada en pantalla explica por qué, y puede leerse como un bug.
- **[UX] Baja** — Los atajos de teclado de Kanban (`N`/`C`/`Delete`) no se extienden a la vista de
  "Gestión de tareas" Polar, pese a compartir la misma pestaña.

#### Cobertura de tests

- **[Dev] Media** — `PolarTaskManagementViewModel` (528 líneas, ~30 comandos) tiene solo 2 tests, ambos
  sobre el aviso de sync omitido — ninguno de crear/editar/borrar lista o tarea, completar tarea,
  seleccionar lista, filtro de tags, o `OpenTaskEditorById` tiene ningún test a nivel de ViewModel. Contraste
  directo con `KanbanViewModelTests` (20 `[Fact]`) sobre una superficie análoga.
- **[Dev] Baja** — `KanbanCardEditorViewModelTests` no cubre la lógica de fecha límite/duración
  (`CombinedDueDate`, clamping de `MaxDurationMinutes`).
- **[Dev] Baja** — Sin ningún test para una respuesta de Postgrest que confirme menos filas de las
  enviadas (rechazo parcial por RLS) — el código parece correcto por inspección, pero no está verificado.

#### Verificado correcto

- UX de Papelera consistente en sus 5 categorías (mismo patrón de tarjeta/tooltip/diálogo).
- Botones icon-only con `ToolTip.Tip` + `AutomationProperties.Name` consistentes en Kanban/Polar/Papelera.
- Paginación de sincronización correcta en los límites de página, incluido el caso de timestamps
  empatados, con test dedicado.
- Manejo de conflicto LWW ("editado mientras un push está en vuelo") correctamente protegido y probado.
- Motor de recurrencia consciente de DST probado ampliamente más allá del caso mínimo original.
- Aislamiento de cuentas (`UserId`) bien probado, no superficial.

---

### 4.4 Calendario, Recordatorios, Notificaciones, Dashboard, Hábitos y Estadísticas

#### Alta

- **[Dev/Usuario] Alta** — Las tarjetas Kanban completadas nunca dejan de contarse como "vencidas" en
  Calendario, Dashboard y la bandeja de vencimientos. `KanbanCard.CompletedAtUtc` es la señal real de
  "esto está hecho", pero `CalendarAggregationService.BuildItems`/`BuildUnscheduledItems`
  (`services/CalendarAggregationService.cs:59,261`) fijan `IsCompleted = false` para todo ítem de Kanban
  sin comprobar `CompletedAtUtc`, y `ReminderInboxViewModel.Reload`
  (`viewmodels/shell/ReminderInboxViewModel.cs:86-91`) recorre todas las columnas sin excluir "Hecho".
  **Terminar una tarea con fecha límite y moverla a "Hecho" hace que siga apareciendo, con estilo de
  vencida (borde rojo), en el Calendario, en el "HOY" del Dashboard y en la insignia de la bandeja de
  vencimientos de la title bar, para siempre**, salvo que el usuario la descarte manualmente. El indicador
  pensado para decir "tienes cosas pendientes" se vuelve permanentemente incorrecto. Sin ningún test que
  cubra este caso.
- **[Usuario] Alta** — El widget "HOY" del Dashboard puede quedar completamente desplazado por backlog
  vencido antiguo. `DashboardViewModel.LoadTodayItems` (línea 532-544) recorre desde `DateTime.MinValue`
  sin límite inferior y toma los primeros 5 tras ordenar del más antiguo al más reciente. Si el vault
  acumula más de 5 ítems vencidos (muy plausible tras meses de uso, agravado por el hallazgo anterior), el
  backlog más viejo llena todo el cupo y **lo que realmente vence hoy nunca aparece** — el resumen "qué
  necesito hoy" puede mostrar solo escombros antiguos. Sin ningún test de `LoadTodayItems`.

#### Media

- **[Dev/Usuario] Media** — Los Recordatorios recurrentes no tienen previsualización de "ocurrencia
  fantasma" en el Calendario, a diferencia de las tareas Polar recurrentes —
  `CalendarAggregationService.BuildGhostOccurrences` está tipado solo para `PolarTask` y nunca se llama
  para recordatorios. Paridad de funcionalidad incompleta entre los dos tipos de ítem recurrente que la app
  ya soporta de forma nativa.
- **[Dev] Media** — El límite de iteraciones de ocurrencias fantasma (`MaxGhostOccurrenceIterations = 366`,
  `CalendarAggregationService.cs:24,247`) puede ocultar en silencio del Calendario una tarea Polar
  recurrente incompleta y muy antigua: como `RollRecurringTasks` solo avanza la fecha real al completarse,
  una tarea diaria dejada sin marcar durante más de un año agota el límite del bucle antes de alcanzar el
  rango visible, y desaparece del Calendario (aunque sigue siendo localizable en la lista de tareas Polar).
- **[Usuario] Media** — El estado vacío de Estadísticas ("vuelve cuando tengas más actividad") ignora los
  hábitos completados: `StatisticsViewModel.Reload` solo comprueba `NotesCreated`/`NotesEdited`/
  `TasksCompleted`, nunca `HabitCompletionPercent`. Un usuario que solo usa Hábitos (sin notas ni tareas)
  nunca ve el gráfico de consistencia de hábitos que existe específicamente para él.
- **[Dev/Usuario] Media** — `ReminderListItemViewModel.IsOverdue` (`viewmodels/reminders/
  ReminderListItemViewModel.cs:30`) no excluye recordatorios completados, a diferencia de su equivalente en
  Calendario (`CalendarItemViewModel.IsPastDue`, que sí comprueba `!IsCompleted` explícitamente con un
  comentario dedicado a ese criterio). Marcar un recordatorio como completado tras su hora no le quita la
  etiqueta "VENCIDO" en la pantalla de Recordatorios.
- **[UX/Dev] Media** — Los diálogos modales de Calendario (2), Recordatorios (3) y Hábitos (2) siguen con
  el overlay antiguo acotado a la página, no migrados a `ModalOverlayHost` (ver §6).
- **[UX/Dev] Media** — Sin ninguna gestión de colisión para ítems con hora solapada en las vistas Semana/
  Día: `CalendarWeekColumnViewModel.TimedItems` solo posiciona verticalmente; dos ítems a la misma hora se
  superponen exactamente, y el que quede encima en el z-order tapa por completo al otro (clics incluidos)
  — justo en las dos vistas construidas para precisión de hora.

#### Baja

- **[Dev/Usuario] Baja** — La métrica "notas creadas" de Estadísticas depende de
  `FileInfo.CreationTimeUtc`, que no sobrevive a una copia/restauración/clonado del vault — una
  restauración de backup puede mostrar un pico falso de "notas creadas" en una semana sin actividad real.
- **[Dev] Baja** — `HabitService.HabitsChanged` no tiene ningún suscriptor (código muerto inofensivo por
  ahora, porque `DashboardViewModel` recarga por otra vía).
- **[Dev/i18n] Baja** — `ReminderEditorViewModel.NotifyDateDisplayLabel` sigue con un formato de fecha
  europeo hardcodeado (`dd/MM/yyyy`), a diferencia de `CalendarQuickCreateViewModel.DateDisplayLabel`, que
  ya se corrigió para usar `CultureInfo.CurrentCulture` citando el propio documento de i18n del proyecto.
- **[Dev] Baja (rendimiento)** — El cuadro de búsqueda del Calendario dispara una reconstrucción completa
  de 3 pasadas en cada pulsación, sin debounce.
- **[Dev] Baja** — Comentario desfasado: `CalendarQuickCreateViewModel._hasDuration` sigue documentado como
  "solo Kanban" pese a que ahora aplica también a Recordatorio y tarea Polar.

#### Verificado correcto

- El diseño de hábitos sin rachas punitivas (`models/Habit.cs`) responde por construcción a "¿un día
  perdido resetea injustamente?" — no hay racha que resetear, solo una rejilla plana hecho/no-hecho/no
  aplica. Decisión de producto deliberada y bien resuelta.
- "No molestar" separa correctamente "registrado en la campana" de "mostrado como toast/sonido" — nunca se
  pierde el registro aunque se silencie la interrupción.
- `DashboardLayoutEngine` limpio, bien probado, degrada con gracia ante configuraciones vacías o
  desconocidas.
- `CalendarItemFilter`/`CalendarAgendaGroupViewModel.Build` puros, bien documentados, alineados con sus
  tests.

---

### 4.5 Archivos/PDF, Skills, Ajustes, Temas, Shell/Navegación e Internacionalización

#### Crítico

- **[Dev/Usuario] Crítica** — La heurística de "esto ya es un vault" en la configuración inicial no
  reconoce un vault existente cuyas notas viven solo en subcarpetas. `VaultService
  .LooksLikeExistingVaultRoot` (`services/VaultService.cs:143-152`), usada por `ResolveVaultFolder`
  (líneas 135-141) desde `VaultSetupViewModel.cs:62`, solo reconoce un vault si la carpeta se llama
  literalmente `enki-documents`, o contiene `.sessions`/`.trash`, o **archivos `.md` de nivel superior**
  (`SearchOption.TopDirectoryOnly`). Un vault real organizado enteramente en subcarpetas (p. ej. "Trabajo/",
  "Personal/", sin haber usado nunca sesiones ni papelera) falla las tres comprobaciones y se clasifica
  como "carpeta de propósito general" — la app anida entonces un `enki-documents/` nuevo y vacío dentro,
  en vez de usarla tal cual. **Escenario**: el usuario sincroniza su carpeta de vault a otra máquina (o
  reinstala) y elige "Abrir vault existente" apuntando exactamente a esa carpeta. Enki crea y abre un vault
  anidado vacío; los datos no se borran, pero se vuelven invisibles hasta que el usuario navegue un nivel
  más profundo por su cuenta. `VaultServiceTests.cs:674-727` cubre 4 casos, ninguno de ellos "vault cuyo
  contenido está exclusivamente en subcarpetas".

#### Alta

- **[Usuario/UX] Alta** — El editor de temas personalizados no ofrece ninguna vía de escape desde un tema
  guardado ilegible. Ni `ThemeEditorViewModel` ni `CustomThemeService.TryParseColors`
  (`services/CustomThemeService.cs:169-185`) validan contraste, solo sintaxis hexadecimal. Además, la
  acción "Alternar tema" de la paleta de comandos (`SettingsViewModel.ToggleTheme`, líneas 770-784) es un
  no-op visual silencioso mientras un tema personalizado está activo, y no existe ninguna entrada de
  "Desactivar tema personalizado" en la paleta de comandos — ambas rutas plausibles de escape "solo con
  teclado" fallan. Un usuario que fije el color de texto igual al de fondo y guarde queda con una interfaz
  completamente ilegible, sin ningún atajo para revertirlo salvo navegar (a ciegas) hasta Ajustes ›
  Apariencia o borrar archivos a mano en `%AppData%/Enki/themes/`.
- **[UX/Dev] Alta** — El rollout de `ModalOverlayHost` cubre 1 de 9 vistas con diálogos modales (ver
  hallazgo transversal §6) — confirmado también desde este frente para Hábitos, Skills, Papelera, Sesiones,
  Recordatorios, Calendario, Ajustes y Kanban.
- **[UX/Usuario] Alta** — "Preguntar antes de borrar" se salta en silencio para Archivos y Notas.
  `FilesViewModel.DeleteFile` y `NotesViewModel.DeleteNote` llaman directamente al servicio sin comprobar
  `ConfirmBeforeDelete`, a diferencia de Hábitos, Polar, Skills, Kanban y Calendario, que sí lo respetan.
  Un usuario con la confirmación activada (el valor por defecto) obtiene un borrado sin confirmar, de un
  solo clic, precisamente en los dos tipos de contenido más usados. Mitigado por ser soft-delete a
  Papelera, pero rompe una expectativa explícita del usuario en dos de las secciones más frecuentes.

#### Media

- **[Dev/Usuario] Media** — El guardarraíl de "al menos una sección visible" de la barra lateral cuenta
  sobre 12 flags, pero `NavItems` (la lista que realmente aparece) solo se puebla desde 9 de ellos —
  Recordatorios/Notificaciones/Estadísticas quedan fuera de esa cuenta. Es posible ocultar las 9 secciones
  de `NavItems` una a una (el guardarraíl solo bloquea la última de las 12) y dejar la barra lateral
  primaria completamente vacía, aunque Ajustes siga siendo alcanzable por su propio botón fijo.
- **[UX/Dev] Media** — La búsqueda global y la paleta de comandos ignoran por completo la visibilidad de
  secciones de la barra lateral: buscar una tarjeta Kanban oculta y seleccionarla navega igualmente a
  Kanban, sin ninguna vía de volver a esa sección desde la barra lateral una vez el usuario navega a otro
  sitio.
- **[Usuario] Media** — "Mostrar en el sistema" (revelar en el explorador de archivos) solo se añadió a
  Notas, no a Archivos — pese a que la vista previa de PDF/imagen en Archivos es el lugar más natural para
  querer localizar el archivo subyacente.
- **[Dev] Media (documentación desfasada)** — El comentario de clase de `HexColorUtility` afirma soportar
  `#ARGB`/`#AARRGGBB`, pero el código solo maneja 3 y 6 caracteres — contradice su propio comentario de
  método dos líneas más abajo. Impacto funcional bajo (el comportamiento real es el más seguro), pero
  puede costarle tiempo a quien confíe en el comentario equivocado.

#### Baja

- **[Dev] Baja (duplicación)** — La lógica de puntuación difusa (`Score`) está triplicada, casi idéntica,
  entre `CommandPaletteViewModel`, `QuickSwitcherViewModel` y una versión más simple en
  `GlobalSearchViewModel` — el propio comentario del código lo reconoce ("ya resuelta tres veces").
- **[Dev] Baja** — Dos archivos `PdfRenderHelperTests.cs` (uno en `Services/`, otro en `ViewModels/`) con
  un test solapado — duplicación accidental, no una separación intencional documentada.
- **[Dev] Baja-Media (mantenibilidad)** — `SettingsViewModel.cs` ha crecido a 2139 líneas, 74 propiedades
  observables y 37 comandos — el propio proyecto ya demostró el patrón de solución (dividir en archivos
  parciales, como se hizo con `MainWindowViewModel`) pero no se aplicó aquí. No afecta al usuario; sí a la
  navegabilidad del código para futuros cambios.

#### Verificado correcto

- La corrección de zoom/scroll de vista previa de PDF (nuevo `ScrollViewer` con scroll horizontal) resuelve
  un bug real de Avalonia (scrollbar horizontal deshabilitado por defecto).
- `ShortcutRegistry.FindConflict`, `HexColorUtility`/`ThemeCssParser` (validación estricta de temas
  importados), y el propio diseño de `ModalOverlayHost` (donde se aplicó) son sólidos.
- Los diffs de ~5000 líneas en los `.resx` son exclusivamente reformateo — verificado por diff de claves,
  ninguna clave nueva salvo las 4 de `Sessions_Graph_*`, correctamente traducidas en los 7 idiomas.
- Spot-check de calidad de traducción en de-DE/fr-FR/es-ES: traducciones genuinas, no copias literales del
  inglés.
- `CornerRadius="0"` sin excepciones en todo `.axaml` revisado; `AutomationProperties` presente y
  consistente en los botones icon-only de Kanban/Calendario/Papelera y en los dos popups nuevos (paleta de
  comandos, quick switcher).
- Arquitectura de información de Ajustes (10 secciones, lista persistente) razonable, no abrumadora.

---

## 5. Transversal: arquitectura, build y tests (pasada propia)

- **Sin deuda de proceso**: cero `TODO`/`FIXME`/`HACK`/`XXX` en código de producción (grep exhaustivo sobre
  `.cs`/`.axaml`), cero `NotImplementedException` real (la única mención es en un comentario histórico
  sobre por qué se abandonó el renderizado WebGL del grafo).
- **Composición manual coherente**: `MainWindowViewModel.Composition.cs` sigue el mismo patrón documentado
  en `AGENTS.md` — sin contenedor de DI, cableado explícito y comentado de cada servicio/evento, decisión
  ya evaluada y documentada (`decision-contenedor-di.md`).
- **Sin `.sln`** en el repo — los tres proyectos (`enki`, `enki.Tests`, `enki.UITests`) se restauran/
  compilan/testean por separado, tanto en CI como en la documentación de build. Es una decisión, no un
  olvido, pero complica abrir el repo de una sola vez en algunos IDEs.
- **`TreatWarningsAsErrors` + `EnableNETAnalyzers`** activos a nivel de solución;
  `AnalysisLevel=latest-recommended` activo solo en `enki.csproj` (deliberadamente no en los proyectos de
  test, decisión documentada).
- **CI mínimo pero funcional** (`.github/workflows/ci.yml`): build + test de los tres proyectos por
  separado en cada push/PR a `main`, con cancelación de ejecuciones redundantes.
- **Cobertura de tests desigual entre capas**: 742 casos `[Fact]`/`[Theory]` en `enki.Tests` (fuerte en
  `Services/`), pero solo **10** en `enki.UITests` (Avalonia headless) para 20 vistas `.axaml` — la capa de
  interacción visual real (drag&drop, popups, foco de teclado) está, en la práctica, solo probada por
  inspección manual documentada como pendiente en el propio roadmap anterior.
- **`LICENSE`**: todos los derechos reservados, decisión explícita del propietario — no es un hallazgo,
  se registra aquí para que el inventario quede completo.
- **`global.json` fija SDK 9.0.316** mientras `enki.csproj` apunta a `net8.0` con `RollForward
  LatestMajor` — coherente con que CI instale explícitamente ambos SDKs 8.0.x y 9.0.x (documentado en el
  propio `ci.yml`, no es un hallazgo).

---

## 6. Hallazgo transversal: `ModalOverlayHost` con rollout incompleto

Se agrupa aquí porque **cuatro de los cinco frentes de la auditoría lo señalaron de forma independiente**,
sin coordinación entre ellos — la señal más consistente de todo este trabajo.

`controls/ModalOverlayHost.cs` (nuevo, sin commitear) resuelve un bug documentado en su propio comentario:
el patrón antiguo (`Border Classes="modal-overlay"` anidado en el `Grid` de cada página) no podía escapar
visualmente de los límites de esa página, así que la barra lateral y los botones flotantes de la title bar
personalizada seguían siendo clicables por debajo del "modal". Se migró **una sola vista**:
`NotificationsView.axaml`.

Vistas que **siguen** con el patrón antiguo (confirmado por grep en las cinco auditorías, sin duplicar
hallazgos): `SessionsView.axaml` (4 diálogos, incluidos contraseña/recuperación), `KanbanView.axaml`,
`TrashView.axaml`, `HabitsView.axaml`, `SkillsView.axaml`, `RemindersView.axaml`, `CalendarView.axaml`,
`SettingsView.axaml`.

**Impacto concreto**: mientras cualquiera de esos diálogos está abierto (crear/borrar sesión, contraseña,
borrar tarjeta o columna, vaciar papelera, editor de hábito, nuevo recordatorio, confirmar borrado en
Ajustes...), la barra lateral y los botones de la title bar (búsqueda, notificaciones, minimizar/cerrar)
siguen siendo clicables debajo — se puede navegar fuera de la página, o cerrar la ventana, con el diálogo
aún "abierto" en un estado indefinido.

Esto no es una regresión de una corrección anterior: es una migración a medio terminar de una corrección
en curso. Se recomienda completarla como un único bloque de trabajo mecánico (ver roadmap, Fase 2).

---

## 7. No se pudo verificar con certeza

Consolidado de las limitaciones declaradas por cada frente de la auditoría — ninguno de estos puntos se
trata como hallazgo confirmado, sino como área que requiere una pasada manual o en un entorno con GUI real
antes de darla por cerrada:

- Ninguno de los escenarios de crash (§4.1, `SessionLockedException`) se reprodujo en una ejecución gráfica
  real — se derivan de trazar tipos de excepción contra bloques `catch` existentes, no de un stack trace
  observado.
- No se pudo medir el tiempo real de PBKDF2-210k en la máquina objetivo para cuantificar cuán perceptible
  es el bloqueo de UI durante la creación de una sesión protegida.
- No se inspeccionaron los permisos reales en disco de un archivo temporal descifrado (`enki-dec-*`) — la
  recomendación de endurecerlos se basa en la API usada (`File.WriteAllBytes`), no en una comprobación
  `stat`/`ls -l`.
- No se determinó si `TextEditor.CaretOffset` (AvaloniaEdit) recorta o lanza cuando el offset excede la
  longitud del documento — relevante para la severidad exacta del bug de `/tabla`.
- No se determinó si un comando `[RelayCommand]` de CommunityToolkit.Mvvm absorbe o propaga una excepción
  síncrona lanzada dentro del cuerpo del comando — relevante para cómo se manifiestan al usuario final
  varios de los hallazgos de excepciones no capturadas.
- No se pudo confirmar si `https://skillsclaude.org/skills` (enlazado desde la sección de Skills) es un
  dominio real y activo — sin acceso a red desde el entorno de auditoría.
- No se verificó en hardware Windows/macOS real el comportamiento de `FolderOpener.RevealFile`
  (`explorer.exe /select,`/`open -R`) — correcto por contrato de API, no verificado en ejecución.
- No se determinó si el rollout parcial de `ModalOverlayHost` es una migración por fases ya planificada
  (con seguimiento en otro sitio) o un olvido — vale la pena confirmarlo directamente con quien lo introdujo
  antes de tratarlo como bug puro.
