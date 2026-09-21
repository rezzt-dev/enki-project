# Funcionalidades pendientes priorizadas

Análisis de las funcionalidades que faltan en Enki para el avatar de usuario definido abajo, ordenadas por
valor para el usuario frente a viabilidad técnica, con la especificación necesaria para implementarlas.

- **Fecha del análisis:** 2026-09-21
- **Rama de referencia:** `alfa-version`
- **Método:** cruce de `propuesta-nuevas-funcionalidades.md`, el `CHANGELOG.md` y búsquedas en el código
  (por nombre de clase y palabra clave). **No se ha ejecutado la aplicación**: todo lo que aquí se afirma
  como "no existe" significa "no aparece en el código", y debe verificarse antes de empezar cada tarea
  (ver [§9 Dudas abiertas](#9-dudas-abiertas-y-verificaciones-previas)).
- **Relación con otros documentos:** complementa a `propuesta-nuevas-funcionalidades.md` y a
  `analisis-proyecto/roadmap.md`; no los sustituye. Si algo de aquí ya no coincide con el código real,
  manda el código.

---

## Lista de tareas

Cada punto se entiende por sí solo; el detalle, los criterios de aceptación y las pruebas están en las
secciones indicadas.

### Prioridad alta — confianza en los datos y captura

- [ ] **T1 · Cambios externos en el vault** — vigilar el vault con `FileSystemWatcher` y comprobar `LastWriteTimeUtc` antes de guardar, de modo que el autoguardado nunca pise un cambio hecho fuera de Enki: recarga silenciosa si no hay ediciones locales y diálogo de conflicto (conservar mía / cargar la de disco / ver diferencias) si las hay. → [§3.1](#31-t1--detección-de-cambios-externos-en-el-vault)
- [ ] **T2 · Captura rápida global** — icono de bandeja y atajo global que abren una mini-ventana para escribir una idea y enviarla a `Inbox/` (nota) o a una tarjeta Kanban sin abrir la ventana principal. → [§3.2](#32-t2--captura-rápida-global)
- [ ] **T3 · Web clipper simple** — pegar una URL y crear una nota en `Inbox/` con título, enlace y texto principal extraído; si el sitio bloquea la extracción, la nota se crea igualmente solo con título y enlace. → [§3.3](#33-t3--web-clipper-simple-pegar-url--nota)
- [ ] **T4 · Multi-vault sin reiniciar** — guardar una lista de vaults recientes y ofrecer un selector para cambiar de vault en caliente (reutilizando la recarga de servicios que ya existe en `SwitchVault`). → [§3.4](#34-t4--multi-vault-sin-reiniciar)

### Prioridad media — cerrar el flujo de planificación

- [ ] **T5 · Snooze de recordatorios** — añadir "Aplazar" (10 min, 1 h, mañana, personalizado) a la notificación y a la bandeja de recordatorios para reprogramar `NotifyAtUtc` sin cerrarlo. → [§4.1](#41-t5--aplazar-recordatorios-snooze)
- [ ] **T6 · Tiempo real y Pomodoro** — registrar el tiempo real trabajado en tarjetas Kanban y tareas Polar (junto al `DurationMinutes` estimado) con un temporizador Pomodoro que avisa por `NotificationService`. → [§4.2](#42-t6--registro-de-tiempo-real-y-pomodoro)
- [ ] **T7 · Notas relacionadas y menciones sin enlazar** — añadir al panel lateral del editor un bloque "Relacionadas" (por etiquetas y enlaces compartidos) y "Menciones sin enlazar" (notas cuyo título aparece en el texto sin `[[ ]]`) con enlazado en un clic. → [§4.3](#43-t7--notas-relacionadas-y-menciones-sin-enlazar)
- [ ] **T8 · Exportar vistas a imagen/PDF** — exportar el Calendario y el tablero Kanban (y, de paso, una nota) a PNG y PDF desde la paleta de comandos y los menús de cada vista. → [§4.4](#44-t8--exportar-calendario-y-kanban-a-imagen-o-pdf)
- [ ] **T9 · Aviso de versión nueva** — comprobar al arrancar (y manualmente en Ajustes) si hay una release más reciente en GitHub y mostrar un aviso con enlace de descarga; sin auto-actualización. → [§4.5](#45-t9--aviso-de-actualizaciones)

### Prioridad baja — más adelante

- [ ] **T10 · Transclusión `![[Nota]]`** — renderizar el contenido de otra nota (o sección) embebido en el visor en lugar de degradarlo a wikilink. → [§5.1](#51-t10--transclusión-nota)
- [ ] **T11 · Fórmulas y diagramas** — soportar LaTeX (`$…$`, `$$…$$`) y bloques ` ```mermaid ` en el visor; solo si el avatar es estudiante/técnico. → [§5.2](#52-t11--fórmulas-latex-y-diagramas-mermaid)
- [ ] **T12 · Captura móvil vía Polar** — aceptar en Enki notas/ideas capturadas desde el móvil a través de Supabase/Polar como paso intermedio barato hacia la sincronización de notas. → [§5.3](#53-t12--captura-móvil-vía-polar-y-sincronización-de-notas)
- [ ] **T13 · Importación desde otras herramientas** — verificar qué formatos cubre el código actual (Obsidian, Notion, `.ics`) y, si el avatar viene de otra app, subirla a prioridad alta. → [§5.4](#54-t13--importación-y-exportación-ics-obsidian-notion)

### Fuera de alcance (por ahora)

Automatizaciones, notas-tabla con tipos, notas de voz y sistema de plugins: alto coste o ya marcados como no
recomendados en la propuesta. No se añaden módulos grandes nuevos. Ver [§7](#7-lo-que-no-se-prioriza).

---

## 1. Contexto y avatar

### 1.1 Avatar utilizado

El repositorio no define un cliente potencial explícito. Se ha **inferido** del README y de la documentación:

> Una sola persona, con un vault de Markdown local y privacidad como requisito, que quiere sustituir cinco
> aplicaciones por una. Los datos son suyos, no quiere gamificación y usa Enki como segundo cerebro y
> planificador personal (estudiante, profesional o *knowledge worker* individual).

Consecuencias de diseño que se derivan de él y que todas las tareas deben respetar:

| Principio | Implicación práctica |
| --- | --- |
| Los datos son del usuario | Todo funciona sin cuenta ni red. Lo que use red (T3, T9, T12) es opt-in y degrada con elegancia. |
| Markdown en disco | El vault se puede tocar desde fuera (Syncthing, Dropbox, otro editor). Motivo de T1. |
| Sin gamificación | Nada de rachas, puntos ni objetivos en T6 (el tiempo se registra, no se puntúa). Mismo criterio que el calendario de constancia ya existente. |
| Un cerebro, muchas apps | Reducir fricción de captura (T2, T3) y de descubrimiento (T7). |

Si el avatar real fuese otro (freelance, equipo pequeño), el orden cambia: T6 y T8 subirían (facturación por
tiempo, informes), y T4 pasaría a ser prioritaria.

### 1.2 Lo que ya está hecho (la propuesta lo lista como pendiente)

No se vuelve a especificar; se cita para evitar trabajo duplicado.

- Plantillas de notas y notas diarias mejoradas (`NoteTemplateService`, `DailyNoteService`).
- Historial de versiones (`VaultService.NoteHistory.cs`) y gestor global de etiquetas (`TagManagerService`).
- Grafo global (`SessionGraphAnalysisService`).
- Pegar imágenes en el editor (`NoteImageService`, `EditorViewModel.Images.cs`).
- Exportar una nota a HTML (`NoteExportService`, `NoteExportRenderer`).
- Modo zen y paleta de comandos (`viewmodels/commandpalette/`).
- Atajos remapeables (`ShortcutRegistry`, `ShortcutCatalog`).
- Hábitos y estadísticas (`HabitService`, `StatisticsAggregationService`).
- Dashboard configurable (`DashboardLayoutEngine`).
- Backup y restauración (`VaultBackupService`).

### 1.3 Convenciones obligatorias del proyecto (`AGENTS.md`)

Toda tarea de este documento debe cumplirlas; no se repiten en cada una.

- **MVVM estricto y sin DI:** modelos POCO en `models/`, persistencia en `services/`, estado y comandos en
  `viewmodels/` (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`), vistas `.axaml` con
  code-behind mínimo. Composición manual en `viewmodels/shell/MainWindowViewModel.Composition.cs` y cableado
  de callbacks en `MainWindowViewModel.Callbacks.cs`. No introducir interfaces genéricas, repositorios ni
  capas de mapeo.
- **Unificar sin duplicar:** leer y escribir sobre la entidad real (`NoteFile`, `KanbanCard`, `PolarTask`,
  `Reminder`), nunca crear un segundo almacén. Referencia: `CalendarAggregationService`.
- **Persistencia:** JSON vía `JsonFileStore`/`AppDataDirectory.Resolve()`; constructor con
  `storageDirectory` opcional para tests; `Save()` que dispara un evento `Action?` de cambio. Los ajustes por
  vault viven en `<vault>/.enki/settings.json` (`VaultSettingsService`).
- **Notificaciones:** cualquier evento relevante para el usuario se anuncia con
  `_notificationService.Push(...)`; ver `notificaciones-y-recordatorios.md`, sección "Cómo extender".
- **Diseño visual:** `CornerRadius="0"` salvo indicadores circulares; reutilizar `Border.calendar-popover` y
  `Border.dropdown-popover`; tipografías Space Grotesk / JetBrains Mono ya empaquetadas
  (`sistema-diseno-web.md`).
- **i18n:** toda cadena visible va a los `.resx` (`localization/Strings*.resx`) y se consume con
  `LocalizationService`/`TrExtension`; ninguna cadena visible hardcodeada. Mantener paridad entre idiomas
  (`internacionalizacion-i18n.md`).
- **Idioma del código:** comentarios XML-doc y documentación en español; identificadores en inglés.
- **Seguridad de datos:** escritura atómica y durable (`DurableFile`, `VaultService.WriteFileAtomic`),
  permisos privados (`PrivateDataPermissions`), sesiones cifradas (`SessionEncryptionService`) y política de
  enlaces (`ExternalLinkPolicy`, `RemoteImagePolicy`). Cualquier función nueva que lea/escriba notas debe
  respetar sesiones bloqueadas (`SessionLockedException`, `IsPathInLockedSession`).
- **Tests:** xUnit en `enki/tests/enki.Tests/` (`Services/`, `ViewModels/`, `Views/`, `Models/`,
  `Localization/`). Ejecutar `dotnet test enki/tests/enki.Tests/enki.Tests.csproj`.
- **Documentación y CHANGELOG:** cada tarea cerrada añade su entrada en `CHANGELOG.md` (sección
  *SIN PUBLICAR*, en CASTELLANO e INGLÉS) y su documento en `.docs/project-info/`.

### 1.4 Definición de "hecho" común

Una tarea se considera terminada cuando:

1. Cumple todos sus criterios de aceptación EARS.
2. Tiene pruebas xUnit del servicio y del viewmodel afectados; la suite completa pasa.
3. Todas las cadenas nuevas están en todos los `.resx` y hay test de paridad de claves (ya existente en
   `tests/enki.Tests/Localization/`, verificar).
4. Funciona con lector de pantalla y solo con teclado (`qa-manual-release-candidate.md`).
5. Respeta sesiones cifradas/bloqueadas y no escribe en `%AppData%/Enki` real durante los tests.
6. `CHANGELOG.md` y la documentación del módulo están actualizados.

### 1.5 Formato de los requisitos (EARS)

Los criterios de aceptación usan la sintaxis EARS (*Easy Approach to Requirements Syntax*), que es la que
pide el enfoque `feature-forge`:

- **Ubicuo:** «El sistema deberá …»
- **Dirigido por evento:** «Cuando <evento>, el sistema deberá …»
- **Dirigido por estado:** «Mientras <estado>, el sistema deberá …»
- **Comportamiento no deseado:** «Si <condición anómala>, entonces el sistema deberá …»
- **Opcional:** «Donde <característica configurada>, el sistema deberá …»

---

## 2. Resumen de priorización

| ID | Tarea | Valor | Complejidad | Riesgo si falta | Depende de |
| --- | --- | --- | --- | --- | --- |
| T1 | Cambios externos en el vault | Muy alto | Media | **Pérdida de datos** | Historial (hecho) |
| T2 | Captura rápida global | Alto | Media-alta (bandeja y atajo global multiplataforma) | Fricción diaria | — |
| T3 | Web clipper | Alto | Media | Abandono en la 1.ª semana | T2 (reutiliza el destino Inbox) |
| T4 | Multi-vault | Medio-alto | Media | Bloquea a quien separa trabajo/personal | — |
| T5 | Snooze | Alto | Baja | Recordatorios poco útiles | — |
| T6 | Tiempo real + Pomodoro | Medio | Baja-media | Estimación sin contraste | — |
| T7 | Relacionadas / sin enlazar | Medio-alto | Media | Descubrimiento pobre | `EditorBacklinksService` |
| T8 | Exportar vistas | Medio | Media | Compartir difícil | `NoteExportService` |
| T9 | Aviso de versión | Medio | Baja | Usuarios desactualizados | Pipeline de release |
| T10–T13 | Prioridad baja | Variable | Media-alta | Bajo | T1 (T12) |

**Orden de ejecución recomendado:** T1 → T5 → T2 → T3 → T4 → T7 → T9 → T6 → T8. T5 va temprano porque es
barata y cierra un hueco muy visible; T1 va primero porque es la única con riesgo de pérdida de datos.

---

## 3. Prioridad alta — confianza en los datos y captura

### 3.1 T1 · Detección de cambios externos en el vault

**Problema.** No hay `FileSystemWatcher` ni comprobación de modificación externa al abrir o guardar. El
usuario guarda Markdown en disco, por lo que casi seguro usa Syncthing, Dropbox u otro editor sobre la misma
carpeta. `EditorViewModel` autoguarda con un `DispatcherTimer` (`_saveTimer`, línea ~35/398) y llama a
`VaultService.WriteContent`, que escribe de forma atómica pero **sin comparar** con lo que hay en disco: si la
nota cambió fuera mientras estaba abierta, el siguiente autoguardado la pisa.

**Riesgo.** Pérdida silenciosa de datos. Es el único punto de esta lista con impacto en integridad. El
historial local (`CaptureAutosaveSnapshotBeforeWrite`) ya captura el contenido previo antes de escribir, así
que hoy el dato pisado sería recuperable, pero solo si el usuario sabe que ocurrió.

**Base existente que se reutiliza.**

- `VaultNoteCache` ya compara `FileInfo.LastWriteTimeUtc` para invalidar entradas: la comprobación de mtime
  en escaneos ya está resuelta; falta hacerla en el **editor abierto**.
- `VaultService.CacheAfterWrite` / `Put` / `Invalidate` registran las escrituras propias.
- `NoteVersion`/`CaptureSnapshot` para el punto de restauración de seguridad.
- `NotificationService` y `ToastService` para avisos.
- `TextDiffService` y `EditorVersionHistoryViewModel` para mostrar diferencias línea a línea.

**Diseño propuesto.**

1. **Servicio `VaultChangeWatcher`** (nuevo, `services/VaultChangeWatcher.cs`, mismo estilo que el resto:
   constructor con dependencias explícitas, evento `Action<VaultExternalChange>? ExternalChangeDetected`).
   - Envuelve un `FileSystemWatcher` sobre la raíz del vault (`IncludeSubdirectories = true`, filtro `*.md`,
     `NotifyFilters.LastWrite | FileName | Size`).
   - **Debounce** de ~300–500 ms por ruta (patrón ya presente en `services/editor/EditorDebounce.cs`) porque
     Syncthing/Dropbox emiten ráfagas y ficheros temporales.
   - **Ignora las escrituras propias**: `VaultService.WriteContent` registra `(ruta, mtime, hash)` de lo que
     acaba de escribir; el watcher descarta eventos que coincidan. Sin esto, cada autoguardado dispararía un
     falso conflicto.
   - Ignora `.enki/`, `.history/`, temporales privados (`PrivateTempFile`) y los `.tmp` de escritura atómica.
   - Reinicia el watcher en `SwitchVault` / `SetVaultPath` y lo libera al cerrar.
   - Si el watcher falla o desborda el buffer (`Error` / `InternalBufferOverflowException`) o el sistema de
     archivos no lo soporta (unidades de red), degrada a **sondeo periódico** de las notas abiertas.
2. **Comprobación previa a guardar (red de seguridad independiente del watcher).** En
   `VaultService.WriteContent` (o en un método hermano `WriteContentChecked`), antes de escribir, comparar
   el mtime/tamaño actual en disco con el de la última lectura/escritura conocida de esa nota. Si difiere,
   lanzar un resultado tipificado `ExternalModificationDetected` en vez de escribir. Esto cubre los casos en
   que el watcher no llegó (buffer, red, suspensión del equipo).
3. **Política en el editor** (`EditorViewModel`):

   | Estado local | Cambio externo | Acción |
   | --- | --- | --- |
   | Sin cambios sin guardar | Sí | Recarga silenciosa, conserva posición del cursor y scroll, `Push` de notificación discreta («Nota actualizada desde fuera»). |
   | Con cambios sin guardar | Sí | Detiene el autoguardado de esa nota y muestra diálogo de conflicto. |
   | Nota eliminada fuera | — | Aviso; conservar el buffer en pantalla y ofrecer «Guardar como nota nueva». |
   | Nota renombrada/movida fuera | — | Aviso; intentar localizar por contenido/mtime o dejar el buffer y ofrecer guardar. |

4. **Diálogo de conflicto** con tres acciones y estilo `Border.dropdown-popover`/diálogos existentes:
   - **Conservar la mía** — guarda el buffer local; antes crea snapshot con el contenido externo
     (`NoteVersionTrigger` nuevo `ExternalConflict`, ver `NoteVersion.cs`) para que sea recuperable.
   - **Cargar la de disco** — descarta el buffer local tras snapshot del contenido local.
   - **Ver diferencias** — reutiliza `TextDiffService`/vista del historial para comparar antes de decidir.
   - Ninguna opción es por defecto destructiva; `Esc` equivale a «decidir luego» y mantiene el autoguardado
     pausado para esa nota.
5. **Sesiones cifradas.** Para notas de sesión protegida, comparar tras descifrar (o comparar mtime del
   `.enc` si es lo que se escribe). Una sesión bloqueada no se recarga ni se diffea; se marca «pendiente de
   revisar al desbloquear».
6. **Actualizar cachés y vistas.** Al aceptar un cambio externo: `InvalidateCacheUnderPath`,
   `InvalidateVaultSizeCache`, refresco de backlinks, búsqueda, etiquetas y grafo. Idealmente vía un evento
   `VaultService.ExternalChangeApplied` para no acoplar pantallas (patrón `BoardChanged`).
7. **Ajuste** en Ajustes > Editor: «Vigilar cambios externos en el vault» (activado por defecto) y, en
   consecuencia, «Recargar automáticamente si no hay cambios locales».

**Criterios de aceptación (EARS).**

- Cuando un archivo `.md` abierto en el editor sin cambios locales se modifica fuera de Enki, el sistema
  deberá recargar su contenido en el editor sin diálogo, manteniendo posición del cursor y desplazamiento.
- Cuando un archivo `.md` abierto con cambios locales sin guardar se modifica fuera de Enki, el sistema
  deberá pausar su autoguardado y mostrar un diálogo con las opciones «Conservar la mía», «Cargar la de
  disco» y «Ver diferencias».
- Cuando el usuario elige cualquiera de las dos versiones, el sistema deberá guardar antes un snapshot de la
  versión descartada en el historial local.
- Cuando Enki escribe una nota por autoguardado, el sistema no deberá interpretar esa escritura como un
  cambio externo.
- Si el mtime o tamaño en disco difiere del último conocido en el momento de guardar, entonces el sistema
  deberá abortar la escritura y tratar el caso como cambio externo, sin sobrescribir.
- Si el `FileSystemWatcher` falla o desborda su buffer, entonces el sistema deberá degradar a comprobación
  por sondeo de las notas abiertas y registrar la incidencia en `LogService`.
- Si una nota abierta es eliminada fuera de Enki, entonces el sistema deberá conservar el contenido en
  pantalla y ofrecer guardarlo como nota nueva.
- Mientras una sesión cifrada esté bloqueada, el sistema no deberá leer ni comparar su contenido y deberá
  posponer la revisión hasta el desbloqueo.
- Cuando el vault cambia, el sistema deberá liberar el watcher anterior y crear uno nuevo para el vault
  activo sin filtrar recursos.
- Donde el ajuste «Vigilar cambios externos» esté desactivado, el sistema deberá mantener únicamente la
  comprobación previa a guardar.

**Pruebas.** Unitarias sobre un directorio temporal (`storageDirectory`/vault temporal): escritura propia no
genera evento; escritura externa sí; ráfaga de N eventos → 1 notificación; mtime distinto en
`WriteContent` → no escribe; nota eliminada; sesión bloqueada. Test de viewmodel para la tabla de política.
Manual: Syncthing/Dropbox real y edición simultánea en otro editor (añadir al checklist QA).

**Riesgos y mitigaciones.**

- *Falsos positivos por escrituras propias* → registro `(ruta, mtime, hash)` de escrituras recientes.
- *Ruido de sincronizadores (archivos `.sync-conflict`, `.tmp`)* → filtro por extensión y patrones.
- *Diferencias de plataforma* (`inotify` límites en Linux, granularidad de mtime en FAT/exFAT y en NTFS
  frente a ext4) → comparar mtime **y** tamaño **y** hash cuando haya duda; sondeo como plan B.
- *Coste de vigilar vaults enormes* → vigilar solo `*.md` y no procesar eventos de carpetas ignoradas.

**Complejidad:** media. **Estimación orientativa:** 4–6 días con pruebas y QA manual.

---

### 3.2 T2 · Captura rápida global

**Problema.** No hay icono de bandeja ni atajo global. «Sé exactamente dónde está lo que necesito» empieza
por poder **soltar una idea** sin abrir la ventana principal ni decidir dónde va.

**Base existente.** `SingleInstanceGuard` (instancia única), `KanbanService`, `VaultService.CreateNote`,
`NotificationService`, `ShortcutRegistry`/`ShortcutCatalog` (atajos remapeables) y `App.axaml.cs`
(`MainWindowRef`).

**Diseño propuesto.**

1. **Icono de bandeja** (`TrayIcon` de Avalonia en `App.axaml`) con menú: *Captura rápida*, *Abrir Enki*,
   *Salir*. Ajuste «Mantener Enki en la bandeja al cerrar la ventana» (desactivado por defecto para no
   sorprender).
2. **Atajo global** del sistema operativo. Avalonia no lo ofrece de forma nativa; requiere una capa por
   plataforma (`RegisterHotKey` en Windows, `CGEventTap`/`MASShortcut`-like en macOS, en Linux depende de
   X11/portal y **no es fiable en Wayland**). Diseño:
   - Interfaz interna `IGlobalHotkey`-like **solo si** hay más de una implementación real (AGENTS.md prohíbe
     interfaces genéricas sin necesidad); implementaciones por `#if`/clase por plataforma junto a
     `DpapiPolarCredentialStore`/`KeychainPolarCredentialStore`/`LibsecretPolarCredentialStore` (mismo patrón
     ya existente para credenciales).
   - Si el registro falla (Wayland, conflicto de atajo), `Push` de notificación y la captura sigue siendo
     accesible desde la bandeja y desde dentro de la app (paleta de comandos y atajo local).
   - El atajo por defecto se define en `ShortcutCatalog` (p. ej. `Ctrl+Alt+Espacio`) y es remapeable.
3. **Mini-ventana de captura** (`views/QuickCaptureWindow.axaml` + `QuickCaptureViewModel`): sin marco
   pesado, siempre encima, un campo multilínea, selector de destino y `Ctrl+Enter` para enviar, `Esc` para
   cancelar. Estilo del sistema de diseño (`CornerRadius="0"`).
4. **Destinos** (opción recordada en `VaultSettings`):
   - **Nota en `Inbox/`** — `VaultService.CreateNote(title, "Inbox", content)`; título = primera línea o
     marca de tiempo; la carpeta se crea si no existe. Alternativa: anexar a la nota diaria
     (`DailyNoteService`) como línea con hora.
   - **Tarjeta Kanban** en una columna configurable (por defecto la primera) vía `KanbanService`.
   - Sin duplicar: crea las entidades reales existentes (`NoteFile`, `KanbanCard`).
5. **Sin vault o con sesión bloqueada.** Si no hay vault configurado, la captura se rechaza con mensaje claro.
   Si el destino cae en una sesión cifrada bloqueada, se usa `Inbox/` fuera de sesión.
6. Tras enviar, mostrar confirmación breve (toast) y no robar el foco a la aplicación en la que estaba el
   usuario.

**Criterios de aceptación (EARS).**

- Cuando el usuario pulsa el atajo global configurado, el sistema deberá mostrar la mini-ventana de captura
  con el foco en el campo de texto en menos de 300 ms, sin abrir ni traer al frente la ventana principal.
- Cuando el usuario envía la captura con `Ctrl+Enter`, el sistema deberá crear la nota en `Inbox/` o la
  tarjeta Kanban según el destino elegido y cerrar la mini-ventana.
- Cuando el destino es `Inbox/` y la carpeta no existe, el sistema deberá crearla.
- Cuando el usuario pulsa `Esc` con texto sin enviar, el sistema deberá cerrar la ventana conservando el
  borrador hasta la siguiente captura.
- Si el sistema operativo no permite registrar el atajo global, entonces el sistema deberá notificarlo,
  seguir ofreciendo la captura desde la bandeja y la paleta de comandos, y no fallar al arrancar.
- Si no hay vault disponible, entonces el sistema deberá informar del motivo y no descartar el texto.
- Donde «Mantener en la bandeja» esté activo, el sistema deberá seguir en ejecución al cerrar la ventana
  principal y liberar la instancia al elegir «Salir».
- El sistema deberá ser operable solo con teclado y anunciar el resultado a los lectores de pantalla.

**Pruebas.** Servicio/VM: creación de nota en `Inbox/`, tarjeta en columna configurada, borrador conservado,
vault ausente, sesión bloqueada. Manual por plataforma (Windows, macOS, Linux X11 y Wayland).

**Riesgos.** Atajo global inconsistente entre plataformas (especialmente Wayland) → tratarlo como mejora
progresiva, con la bandeja como vía garantizada. Interacción con `SingleInstanceGuard` (la mini-ventana debe
vivir en la instancia principal). **Complejidad:** media-alta; **estimación:** 5–8 días.

---

### 3.3 T3 · Web clipper simple (pegar URL → nota)

**Problema.** Lo primero que busca un usuario de segundo cerebro en la primera semana es guardar páginas web.
Hoy solo se puede pegar una URL como enlace en el editor.

**Base existente.** `ExternalLinkPolicy` y `RemoteImagePolicy` (políticas de seguridad de red y enlaces),
`UrlOpener`, `NoteExportRenderer` (conversiones HTML), `YamlDotNet` (frontmatter) y `NoteTemplateRenderer`.

**Diseño propuesto.**

1. **Servicio `WebClipService`** (`services/`): `Task<WebClipResult> ClipAsync(Uri url, CancellationToken)`.
   - Solo `http`/`https`; rechazar esquemas ejecutables o locales y **direcciones privadas/loopback**
     (mitigación SSRF) reutilizando la política de `ExternalLinkPolicy`.
   - `HttpClient` con tiempo de espera corto, límite de tamaño (p. ej. 2 MB), límite de redirecciones y
     `User-Agent` identificable; respetar `Content-Type` (solo HTML).
   - **Extracción**: título (`og:title` → `<title>`), descripción, autor, fecha, imagen destacada (opcional),
     y cuerpo principal con una heurística de legibilidad → Markdown. Evaluar un paquete ligero (HtmlAgilityPack
     + convertidor) frente a una implementación mínima; **decisión pendiente** por peso de dependencias y
     licencia (ver §9).
   - **Fallback**: si la petición falla, hay bloqueo (403/paywall/JS-only), tiempo excedido o extracción
     vacía, crear igualmente la nota con **título (o dominio) y enlace**, y avisar con `Push`.
2. **Formato de la nota** (frontmatter + cuerpo):

   ```markdown
   ---
   title: <título de la página>
   source: <url>
   clipped: 2026-09-21T10:15:00Z
   tags: [clip]
   ---

   # <título de la página>

   > Fuente: <url> · Guardado el 21/09/2026

   <contenido extraído en Markdown>
   ```

3. **Puntos de entrada:** paleta de comandos («Guardar página web…»), botón en documentos, la mini-ventana de
   T2 (si el texto es una URL, ofrecer «Guardar como página web») y, opcionalmente, detectar URL en el
   portapapeles.
4. **Privacidad:** es opt-in y explícito; nunca ejecuta scripts, no carga recursos remotos al renderizar
   (coherente con `RemoteImagePolicy`); no envía contenido a terceros. Imágenes remotas: por defecto **no**
   se descargan; opción posterior para guardarlas junto a la nota con `NoteImageService`.
5. Destino: `Inbox/` por defecto, configurable; evitar colisión de nombres (`CreateNote` ya lo resuelve).

**Criterios de aceptación (EARS).**

- Cuando el usuario introduce una URL `http`/`https` válida, el sistema deberá crear una nota con título,
  enlace de origen, fecha de captura y el texto principal convertido a Markdown.
- Si el sitio responde con error, bloquea la extracción, supera el tiempo de espera o no contiene texto
  extraíble, entonces el sistema deberá crear igualmente una nota con título (o dominio) y enlace, y avisar
  al usuario de que la extracción fue parcial.
- Si la URL usa un esquema distinto de `http`/`https` o resuelve a una dirección local o privada, entonces
  el sistema deberá rechazarla sin realizar la petición.
- Si la respuesta supera el tamaño máximo, entonces el sistema deberá interrumpir la descarga y aplicar el
  fallback.
- El sistema no deberá ejecutar scripts de la página ni cargar recursos remotos al mostrar la nota creada.
- Mientras la captura está en curso, el sistema deberá mostrar progreso y permitir cancelarla.
- Cuando el usuario cancela, el sistema no deberá crear ninguna nota.

**Pruebas.** Servidor HTTP local de pruebas (o `HttpMessageHandler` simulado): HTML normal, 403, redirección,
timeout, cuerpo enorme, tipo no HTML, URL privada, título vacío, caracteres especiales en el nombre de
archivo. Test de paridad de cadenas.

**Riesgos.** Calidad heterogénea de la extracción (sitios con JS) → el fallback es la red de seguridad.
Nueva dependencia → revisar licencia y tamaño de publicación. **Complejidad:** media; **estimación:** 4–6 días.

---

### 3.4 T4 · Multi-vault sin reiniciar

**Problema.** Existe la lógica de recarga al cambiar de vault (`VaultService.SetVaultPath`,
`SettingsService.SwitchVault`, `VaultSetupViewModel`), pero no hay lista de vaults recientes ni selector.
Quien separa vida personal y trabajo necesita cambiar rápido.

**Base existente.** `AppSettings.VaultPath`, `VaultSettingsService` (ajustes por vault en
`<vault>/.enki/settings.json`), `VaultDataStorage`, `AppDataDirectory`.

**Diseño propuesto.**

1. **Modelo:** `AppSettings.RecentVaults` (lista de `{ Path, DisplayName, LastOpenedUtc }`, máx. 10), en los
   ajustes globales (no por vault, pues son independientes del vault activo).
2. **Servicio:** ampliar `SettingsService`/`VaultService` con `OpenVault(path)` que: valida que la ruta
   exista y sea legible, hace flush de guardados pendientes de editores y cierra pestañas de forma ordenada,
   detiene watcher/temporizadores/sync de Polar, llama a `SetVaultPath`, recarga servicios y actualiza
   `RecentVaults`.
3. **UI:** selector en la cabecera de la barra lateral y en la paleta de comandos («Cambiar de vault…»,
   «Abrir otra carpeta como vault…»); acción «Quitar de recientes» sin borrar datos.
4. **Estado por vault:** las pestañas abiertas, el historial de la paleta y la posición se guardan por vault.
5. **Rutas no disponibles** (unidad desmontada, carpeta movida): mostrarlas atenuadas con motivo
   (`VaultFailureReasons`, `VaultUnavailableException`) y no perder la entrada.
6. **Interacción con Polar/Supabase y con las credenciales:** mantener el vínculo Polar como ajuste global;
   confirmar en el diseño si debe ser por vault (ver §9).
7. **Sesiones cifradas:** al cambiar de vault se bloquean las sesiones abiertas y se limpian los temporales
   descifrados (`SessionEncryptionService`, `VaultService.CleanupStaleDecryptedTempFiles`).

**Criterios de aceptación (EARS).**

- Cuando el usuario elige un vault de la lista de recientes, el sistema deberá cambiar a él sin reiniciar la
  aplicación y con todas las secciones mostrando datos del nuevo vault.
- Antes de cambiar de vault, el sistema deberá guardar los cambios pendientes de los editores abiertos y, si
  el guardado falla, deberá cancelar el cambio e informar al usuario.
- Cuando se abre un vault, el sistema deberá situarlo el primero de la lista de recientes y limitar la lista a
  10 entradas.
- Si la ruta de un vault reciente ya no existe o no es legible, entonces el sistema deberá mostrarla como no
  disponible con el motivo y no deberá eliminarla automáticamente.
- Cuando se cambia de vault, el sistema deberá bloquear las sesiones cifradas del vault anterior y limpiar
  los temporales descifrados.
- El sistema no deberá mezclar datos entre vaults (etiquetas, historial, plantillas, hábitos, caché).
- Cuando el usuario quita un vault de recientes, el sistema no deberá modificar ni borrar sus archivos.

**Pruebas.** Servicio: cambio A→B→A, lista máxima, ruta inexistente, orden. VM: guardado pendiente falla y
cancela. Test de aislamiento de cachés entre vaults (`VaultNoteCache`). **Complejidad:** media; **estimación:**
3–5 días. **Riesgo principal:** estado residual de un vault en cachés/temporizadores (mitigar con lista de
comprobación de "todo lo que se instancia en `MainWindowViewModel.Composition.cs`").

---

## 4. Prioridad media — cerrar el flujo de planificación

### 4.1 T5 · Aplazar recordatorios (snooze)

**Problema.** No hay `Snooze` en el código. Un recordatorio que solo se puede cerrar o ignorar no sirve como
agenda diaria.

**Base existente.** `Reminder.NotifyAtUtc`, `ReminderService.PopDueReminders` (temporizador de 30 s en
`MainWindowViewModel.Composition.cs`), `DismissedReminderService` (descarte separado de la fuente),
`ReminderInboxViewModel`/`ReminderItemViewModel` (bandeja por `ReminderBucket`), `NotificationService`,
`DoNotDisturbSchedule`, y `KanbanCard`/`PolarTask` (los recordatorios también nacen de tarjetas y tareas).

**Diseño propuesto.**

1. **Comportamiento:** aplazar = mover el instante de aviso, **no** crear otro recordatorio. Para un
   `Reminder` standalone se reescribe `NotifyAtUtc`; para tarjetas/tareas con fecha, se registra un
   aplazamiento en un almacén ligero junto a `DismissedReminderService` (p. ej. `SnoozedReminderService`
   con `{ SourceId, SnoozedUntilUtc }`) para **no alterar la fecha de vencimiento real** de la entidad (el
   snooze no es reprogramar la tarea).
2. **Recurrencia:** aplazar una ocurrencia recurrente no debe desplazar la serie ni avanzar la recurrencia
   antes de tiempo (`PopDueReminders` ya avanza la recurrencia al dispararse; el snooze debe aplicarse sobre
   la ocurrencia ya emitida).
3. **Opciones:** 10 min, 1 h, «Esta tarde» (hoy 18:00), «Mañana» (mañana 09:00) y «Personalizado…»; valores
   rápidos configurables en Ajustes > Notificaciones. Tener en cuenta **DST** y zona horaria (mismo cuidado
   que `PolarTaskService.CalculateNextDueDate`) y respetar `DoNotDisturbSchedule` (si el nuevo instante cae en
   No molestar, se entrega al terminar).
4. **UI:** acciones en el aviso emergente (toast/notificación), en el centro de notificaciones y en cada
   fila de la bandeja; atajo de teclado; estados visibles («Aplazado hasta 15:40») y ordenación coherente en
   buckets.
5. **Límites:** número máximo de aplazamientos consecutivos opcional (evitar el aplazamiento infinito), o sin
   límite pero visible el contador. Decisión en §9.
6. **Sincronización Polar/Supabase:** `Reminder.NotifyAtUtc` sí es campo del contrato; su reescritura por
   snooze se empuja como cualquier edición. Para no ensuciar el wire con aplazamientos efímeros, considerar
   no sincronizar el snooze de tarjetas/tareas (solo local). Decisión documentada en §9.

**Criterios de aceptación (EARS).**

- Cuando el usuario elige «Aplazar» en un aviso de recordatorio, el sistema deberá volver a lanzarlo en el
  instante elegido y retirarlo de la lista de vencidos.
- Cuando se aplaza un recordatorio derivado de una tarjeta o tarea, el sistema no deberá modificar la fecha
  de vencimiento de la entidad de origen.
- Cuando se aplaza una ocurrencia de un recordatorio recurrente, el sistema no deberá alterar las
  ocurrencias futuras de la serie.
- Si el nuevo instante cae dentro de la franja «No molestar», entonces el sistema deberá diferir el aviso
  hasta que termine.
- El sistema deberá conservar los aplazamientos tras cerrar y reabrir la aplicación.
- Si el elemento de origen se elimina o se completa, entonces el sistema deberá descartar su aplazamiento.
- El sistema deberá mostrar «Aplazado hasta <hora>» en la bandeja y en el centro de notificaciones.
- El sistema deberá permitir aplazar con teclado y ser anunciado por lectores de pantalla.

**Pruebas.** `ReminderService`/nuevo servicio con reloj inyectado (`PopDueReminders(DateTime)` ya lo permite):
aplazar, reaparecer, recurrentes, DST, No molestar, persistencia, origen eliminado. VM de la bandeja.
**Complejidad:** baja; **estimación:** 2–3 días.

---

### 4.2 T6 · Registro de tiempo real y Pomodoro

**Problema.** Existe `DurationMinutes` como **estimación** (`Reminder`, `KanbanCard`), pero no el tiempo
**real** trabajado.

**Base existente.** `KanbanCard.DurationMinutes`, `PolarTask`, `NotificationService`,
`StatisticsAggregationService`, `HabitService` (patrón de servicio con persistencia y estadísticas).

**Diseño propuesto.**

1. **Modelo (sobre la entidad real, sin segundo almacén):** en `KanbanCard` y `PolarTask`, añadir
   `List<TimeEntry> TimeEntries` (`StartUtc`, `EndUtc`, `Kind` = manual/pomodoro) o, más simple,
   `int ActualMinutes` + entradas opcionales. Preferible lista de entradas: permite editar y corregir.
   Para Polar, el campo es local (como `DurationMinutes` en `Reminder`, fuera del contrato de sincronización)
   salvo decisión en contrario (§9).
2. **Servicio `TimeTrackingService`:** iniciar/pausar/detener, un único cronómetro activo global,
   recuperación tras cierre inesperado (guardar `StartUtc` en `%AppData%/Enki/timer.json` y ofrecer «Retomar
   / descartar» al arrancar), y evento de cambio `Action?`.
3. **Pomodoro:** ciclos configurables (25/5/15 por defecto), avisos por `NotificationService.Push` con sonido
   opcional (`NotificationSoundPlayer`) y respeto a `DoNotDisturbSchedule`. Cada foco completado crea una
   `TimeEntry` en la tarjeta o tarea vinculada.
4. **UI:** botón ▶ en el detalle de tarjeta/tarea, indicador persistente del cronómetro en la cabecera o
   barra lateral, y comparativa estimado/real en la tarjeta. Estadísticas: sumar tiempo real por etiqueta y
   por semana en `StatisticsAggregationService` (**sin** rachas, puntos ni objetivos; principio del avatar).
5. **Accesibilidad:** el temporizador no debe leer cada segundo a lectores de pantalla; solo anunciar hitos.

**Criterios de aceptación (EARS).**

- Cuando el usuario inicia el cronómetro sobre una tarjeta o tarea, el sistema deberá acumular el tiempo y
  mostrar un indicador visible en toda la aplicación.
- Mientras hay un cronómetro activo, el sistema no deberá permitir iniciar otro sin detener o pausar el
  primero.
- Cuando termina un intervalo de foco Pomodoro, el sistema deberá notificarlo mediante `NotificationService`
  y registrar la entrada de tiempo en el elemento vinculado.
- Si la aplicación se cierra con un cronómetro activo, entonces el sistema deberá ofrecer al reabrir retomar
  o descartar el intervalo, sin perder el tiempo ya acumulado.
- El sistema deberá mostrar el tiempo real junto a la estimación (`DurationMinutes`) cuando ambos existan.
- Si la tarjeta o tarea se elimina, entonces el sistema deberá detener y descartar su cronómetro.
- El sistema no deberá mostrar rachas, puntuaciones ni metas asociadas al tiempo registrado.

**Pruebas.** Servicio con reloj inyectado: iniciar/pausar/retomar, cierre inesperado, cambio de zona horaria,
DST, borrado del elemento, Pomodoro completo. Migración de JSON existente sin el campo (compatibilidad
hacia atrás en `KanbanService`/`PolarTaskService`). **Complejidad:** baja-media; **estimación:** 4–5 días.

---

### 4.3 T7 · Notas relacionadas y menciones sin enlazar

**Problema.** El panel lateral no tiene «Relacionadas» (0 coincidencias en el código). Serviría para
descubrir conexiones **sin IA**, apoyándose en las etiquetas y los backlinks que ya existen.

**Base existente.** `EditorBacklinksService`/`EditorBacklinksViewModel`, `WikiLinkParser`, `NoteTagHelper`,
`VaultService.GetAllNotesAcrossVaultWithContent`, `VaultNoteCache`, `NoteSearchService` y
`SessionGraphAnalysisService`.

**Diseño propuesto.**

1. **`RelatedNotesService`** (`services/editor/`): dada una nota, devuelve una lista ordenada con motivo.
   Puntuación transparente y determinista (sin modelos): peso por etiquetas compartidas (ponderadas por
   rareza, estilo IDF), enlaces salientes/entrantes comunes (vecinos en el grafo, co-citación), misma carpeta
   y, opcionalmente, coincidencia de términos del título. Excluye la propia nota, las ya enlazadas
   directamente (esas están en backlinks) y sesiones bloqueadas.
2. **Menciones sin enlazar:** buscar el título (y alias del frontmatter) de otras notas dentro del texto de
   la actual, fuera de bloques de código, enlaces existentes y wikilinks. Acción «Enlazar» que envuelve la
   coincidencia en `[[ ]]` mediante una edición deshacible del editor. Análogamente, opción inversa: notas
   que mencionan el título de la actual sin enlazarla.
3. **Rendimiento:** usar la caché (`VaultNoteCache`) y cálculo en segundo plano con debounce
   (`EditorDebounce`); índice incremental de títulos/etiquetas en memoria invalidado por
   `InvalidateCacheUnderPath`. Límite de resultados (p. ej. 10) y de tamaño de vault para evitar bloqueos.
   `RegexSafety` para cualquier expresión construida con títulos de usuario.
4. **UI:** dos secciones plegables en el panel lateral existente junto a Backlinks, con el motivo visible
   («2 etiquetas en común: #proyecto, #idea») y navegación por teclado.

**Criterios de aceptación (EARS).**

- Cuando se abre una nota, el sistema deberá mostrar hasta 10 notas relacionadas ordenadas por relevancia y
  el motivo de cada una.
- El sistema deberá excluir de «Relacionadas» la propia nota y las que ya la enlazan o son enlazadas por ella.
- Cuando el texto de la nota contiene el título de otra nota sin enlace, el sistema deberá listarlo en
  «Menciones sin enlazar» con la posibilidad de enlazar en un clic.
- Cuando el usuario enlaza una mención, el sistema deberá insertar el wikilink como una acción deshacible y
  actualizar los backlinks.
- El sistema no deberá proponer menciones dentro de bloques de código, wikilinks o enlaces existentes.
- Mientras se calcula, el sistema no deberá bloquear la escritura en el editor.
- Si una nota pertenece a una sesión bloqueada, entonces el sistema no deberá leer su contenido ni mostrarla.

**Pruebas.** Servicio con vault temporal: puntuación, exclusiones, menciones con títulos parecidos,
acentos/mayúsculas, títulos con caracteres especiales de regex, código/wikilinks ignorados, sesión bloqueada,
rendimiento con N notas. **Complejidad:** media; **estimación:** 4–6 días.

---

### 4.4 T8 · Exportar Calendario y Kanban a imagen o PDF

**Problema.** Solo se exporta una nota a HTML (`NoteExportService`). No hay PDF ni exportación de vistas.

**Base existente.** `NoteExportRenderer`, `NoteExportService`, `CalendarAggregationService`,
`KanbanService`, y en las dependencias `PDFtoImage` (solo **lectura** de PDF), `Svg.Skia` y Avalonia
(`RenderTargetBitmap`).

**Diseño propuesto.**

1. **Imagen (PNG):** renderizar el control visual con `RenderTargetBitmap` a resolución configurable (1×, 2×);
   para tableros anchos, renderizar el tablero completo, no solo la parte visible; fondo según tema activo o
   forzar claro para imprimir.
2. **PDF:** **decisión pendiente** — `PDFtoImage` no genera PDF. Opciones: (a) generar PDF a partir de las
   imágenes con un escritor mínimo, (b) añadir una librería de PDF (p. ej. QuestPDF/PdfSharp; revisar
   licencias — QuestPDF tiene condiciones de licencia comunitaria) o (c) ofrecer solo PNG + «Imprimir a PDF»
   del sistema desde HTML (`NoteExportRenderer`). Recomendación: empezar por (c)/PNG y evaluar (b) después.
3. **Contenido configurable:** rango del calendario (día/semana/mes), capas visibles
   (`CalendarLayerColorService`), columnas y etiquetas del Kanban, incluir o no tarjetas archivadas.
4. **Privacidad:** advertir antes de exportar contenido de sesiones cifradas; no incluir por defecto notas de
   sesiones bloqueadas.
5. **Puntos de entrada:** menú de cada vista y paleta de comandos; guardado con selector de archivo y
   `DurableFile`; abrir carpeta (`FolderOpener`) tras exportar.

**Criterios de aceptación (EARS).**

- Cuando el usuario elige exportar el Calendario, el sistema deberá generar un archivo con el rango y las
  capas seleccionadas.
- Cuando el usuario exporta el Kanban, el sistema deberá incluir todas las columnas y tarjetas visibles del
  tablero, aunque excedan el área en pantalla.
- El sistema deberá escribir el archivo de forma atómica y no dejar archivos parciales si la exportación falla.
- Si la exportación falla o el destino no es escribible, entonces el sistema deberá mostrar el motivo y no
  modificar datos del vault.
- Si el contenido incluye elementos de una sesión cifrada, entonces el sistema deberá pedir confirmación
  explícita antes de exportarlo.
- El sistema deberá respetar el idioma y el formato de fecha configurados.

**Pruebas.** Generación con datos de ejemplo, tablero vacío, tablero muy ancho, fallo de escritura,
resolución alta. Comparación visual manual. **Complejidad:** media; **estimación:** 3–6 días según la opción
de PDF.

---

### 4.5 T9 · Aviso de actualizaciones

**Problema.** Hay instaladores y pipeline de release, pero no se ve comprobación de versión nueva. Con
instaladores por sistema, el usuario necesita saber que existe una versión más reciente. **Solo aviso, no
auto-actualización.**

**Base existente.** `CHANGELOG.md` con versiones SemVer, `Directory.Build.props` (versión), pipeline de
release, `UrlOpener`, `ExternalLinkPolicy`, `NotificationService`, `LogService`.

**Diseño propuesto.**

1. **`UpdateCheckService`:** consulta la API pública de releases del repositorio (endpoint
   `releases/latest`), compara SemVer con la versión del ensamblado (ignorar *prereleases* salvo que el
   usuario esté en un canal alfa/beta), y devuelve `UpdateInfo { Version, Url, Notes }`.
2. **Privacidad y control:** se hace **como máximo una vez cada 24 h**, sin enviar identificadores ni
   telemetría (solo la petición HTTP estándar). Ajuste en Ajustes > General: «Buscar actualizaciones al
   iniciar» (**decisión de valor por defecto pendiente**: por coherencia con el enfoque «local y privado»
   se recomienda **desactivado hasta que el usuario lo active en el primer arranque**, con un botón manual
   «Buscar ahora» siempre disponible).
3. **UI:** entrada en el centro de notificaciones con la versión y las notas; botón «Descargar» que abre la
   página de la release en el navegador mediante `UrlOpener`; «Recordármelo más tarde» y «Omitir esta versión».
4. **Robustez:** timeout corto; si falla (sin red, límite de la API), silencio en el arranque y mensaje solo
   en la comprobación manual.
5. **Seguridad:** solo abrir URLs cuyo host/patrón coincida con el repositorio esperado; nunca descargar ni
   ejecutar binarios.

**Criterios de aceptación (EARS).**

- Cuando existe una versión estable más reciente que la instalada, el sistema deberá mostrar un aviso con el
  número de versión, las notas y un enlace de descarga.
- Cuando el usuario pulsa «Omitir esta versión», el sistema no deberá volver a avisar de esa versión.
- Si no hay conexión o el servicio no responde, entonces el sistema no deberá interrumpir el arranque ni
  mostrar errores en la comprobación automática.
- Donde «Buscar actualizaciones al iniciar» esté desactivado, el sistema no deberá realizar ninguna petición
  de red salvo la comprobación manual.
- El sistema no deberá descargar ni ejecutar archivos y solo abrirá enlaces del repositorio oficial.
- El sistema no deberá comprobar más de una vez cada 24 horas de forma automática.

**Pruebas.** Comparación SemVer (`0.9.0` < `0.10.0`, *prerelease*), respuesta simulada, fallo de red, versión
omitida, límite de frecuencia con reloj inyectado. **Complejidad:** baja; **estimación:** 1–2 días.

---

## 5. Prioridad baja o más adelante

### 5.1 T10 · Transclusión `![[Nota]]`

`MarkdownLineParser` degrada explícitamente estas transclusiones a wikilink normal. Implementarlas implica:
resolver la nota (y opcionalmente `#sección` o `^bloque`) mediante `WikiLinkParser`, renderizar su contenido
embebido en el visor con **límite de profundidad y detección de ciclos** (A embebe B que embebe A), refrescar
el embebido al cambiar la nota origen, respetar sesiones bloqueadas y mostrar un marcador claro cuando la
nota no existe. La edición sigue siendo sobre la nota origen (el embebido es de solo lectura).
Complejidad media-alta por el parser y el renderizado. No priorizar hasta cerrar T1–T9.

**Criterios EARS clave:** cuando la nota destino existe, el sistema deberá mostrar su contenido embebido;
si hay un ciclo, entonces el sistema deberá cortar la recursión y mostrar un aviso; si la nota está en una
sesión bloqueada, entonces el sistema no deberá mostrar su contenido.

### 5.2 T11 · Fórmulas LaTeX y diagramas Mermaid

No se encontró Mermaid ni LaTeX. Solo importa si el avatar es estudiante o técnico. Requiere un motor de
render local (nunca CDN, por privacidad y offline): el proyecto ya incluye `Avalonia.Controls.WebView`,
`Svg.Skia` y TextMate para código. Evaluar el peso de empaquetar KaTeX/Mermaid frente al valor; renderizar
por bloque con caché y límite de tiempo (mismo criterio que el resaltado de código existente) y degradar a
bloque de código si el render falla. Añadir a la exportación HTML (`NoteExportRenderer`) solo si el render es
estático (SVG).

### 5.3 T12 · Captura móvil vía Polar y sincronización de notas

La sincronización de notas entre dispositivos es de **alto riesgo** y depende de T1 (detección de cambios y
conflictos) y del historial (ya hecho). Un paso intermedio más barato es aceptar **capturas** desde móvil a
través de la infraestructura Polar/Supabase (`services/supabase/`, `services/polar/`) hacia `Inbox/`, sin
sincronizar el vault entero. Requiere: definir el contrato de datos (documentar en `supabase-sync/`),
cifrado extremo a extremo si el contenido son notas (hoy Polar sincroniza tareas y recordatorios), y cuota y
retención. No empezar sin resolver T1.

### 5.4 T13 · Importación y exportación `.ics`, Obsidian, Notion

**No se verificó** si existe soporte `.ics`: las coincidencias vistas eran ambiguas. Sí hay referencias a
importación en el código (p. ej. `VaultService.ImportFile`), que **no se abrieron** para saber qué formatos
cubren. **Acción previa (½ día):** auditar el código y documentar qué se importa hoy. Si el avatar viene de
otra herramienta (Obsidian, Notion, Google Calendar), esta tarea **sube a prioridad alta**: un importador de
Notion (Markdown+CSV) y de Obsidian (ya compatible en gran parte por wikilinks y avisos `> [!note]`) reduce
la barrera de entrada; `.ics` (importar/exportar) enlaza con `CalendarAggregationService`. Requiere manejo
seguro de archivos externos (límites de tamaño, rutas relativas, `RegexSafety`, sin ejecución).

---

## 6. Transversal: pruebas, i18n, accesibilidad y documentación

| Aspecto | Requisito común |
| --- | --- |
| **Pruebas** | xUnit en `enki/tests/enki.Tests/`; relojes inyectados (patrón `PopDueReminders(DateTime)`); vault y `storageDirectory` temporales; nunca `%AppData%/Enki` ni `.devdata/` reales. |
| **i18n** | Claves nuevas en `Strings.resx` y en `es-ES`, `es-419`, `en-US`, `en-GB`, `de-DE`, `fr-FR`; formatos con `Plural`, `RelativeTimeFormatter` y `LocalizedTextFormatter`. |
| **Accesibilidad** | Navegación por teclado, `AutomationProperties` en controles nuevos, anuncios no ruidosos (T6) y comprobación con lector de pantalla. Añadir casos a `qa-manual-release-candidate.md` (T1 con sincronizador real, T2 por plataforma, T9). |
| **Diseño** | `sistema-diseno-web.md`: `CornerRadius="0"`, popovers existentes, tipografías empaquetadas. |
| **Seguridad** | Escritura atómica/durable, permisos privados, sin ejecutar contenido de terceros, sesiones cifradas respetadas (T1, T3, T7, T8, T10). |
| **CHANGELOG** | Entrada por tarea en CASTELLANO e INGLÉS bajo *SIN PUBLICAR*. |
| **Documentación** | Un `.md` por tarea grande en `.docs/project-info/` (p. ej. `cambios-externos-vault.md`, `captura-rapida.md`, `web-clipper.md`) y enlazarlo desde `AGENTS.md`. |

---

## 7. Lo que no se prioriza

- **Más módulos grandes.** El roadmap de publicación ya dice que el siguiente salto es la **comodidad
  diaria**, no la superficie nueva.
- **Automatizaciones, notas-tabla con tipos, notas de voz y plugins.** La propia propuesta los marca como de
  alto coste o no recomendados por ahora.
- **Sincronización completa de notas entre dispositivos** hasta cerrar T1 (ver T12).

---

## 8. Plan por hitos

| Hito | Contenido | Objetivo |
| --- | --- | --- |
| **H1 — Datos seguros** | T1, T5 | Ninguna pérdida silenciosa de datos; agenda diaria usable. |
| **H2 — Captura** | T2, T3 | Soltar una idea o una página en segundos, sin abrir la ventana. |
| **H3 — Organización** | T4, T7 | Varios vaults sin reiniciar y descubrimiento de conexiones. |
| **H4 — Planificación y cierre** | T9, T6, T8 | Avisos de versión, tiempo real y exportación de vistas. |
| **H5 — Exploración** | T10–T13 según avatar | Solo tras validar con usuarios reales. |

Cada hito termina con: suite de tests verde, actualización del `CHANGELOG.md`, ejecución de
`qa-manual-release-candidate.md` en los casos afectados y revisión de accesibilidad.

---

## 9. Dudas abiertas y verificaciones previas

Deben resolverse antes de empezar la tarea indicada.

| # | Duda | Tarea | Cómo resolverla |
| --- | --- | --- | --- |
| 1 | ¿Existe soporte de importación/exportación `.ics`? ¿Qué formatos cubre `ImportFile` y otras referencias a importación? | T13 | Auditar el código; probar con archivos reales de Obsidian y Notion. |
| 2 | ¿Es el avatar el inferido (individual, privacidad) u otro (freelance, equipo pequeño)? | Todas | Decisión de producto; cambia el orden de la lista. |
| 3 | ¿Algún mecanismo existente detecta cambios externos que se pasara por alto (por nombre distinto)? | T1 | Buscar usos de `LastWriteTimeUtc`, `FileSystemWatcher`, `Changed` y sondeos en `viewmodels/`. |
| 4 | Librería de PDF y de extracción HTML: licencia, tamaño y soporte multiplataforma. | T3, T8 | Prototipo y revisión de licencias antes de añadir dependencias. |
| 5 | Atajo global en Linux/Wayland: ¿se acepta funcionar solo desde la bandeja y la app? | T2 | Decisión de producto y prueba en las distribuciones objetivo. |
| 6 | ¿El vínculo Polar debe ser por vault o global al cambiar de vault? | T4 | Revisar `SettingsService`/`VaultSettingsService` y el contrato de `supabase-sync/`. |
| 7 | ¿El snooze de tarjetas/tareas se sincroniza con Polar o es solo local? ¿Límite de aplazamientos? | T5 | Decisión de producto; por defecto solo local y sin límite. |
| 8 | ¿El tiempo real se sincroniza con Polar o queda en local (como `DurationMinutes` de `Reminder`)? | T6 | Decisión de contrato con Supabase. |
| 9 | ¿La comprobación de actualizaciones va activada por defecto? | T9 | Decisión de producto; recomendado opt-in. |
| 10 | Base de la afirmación «no existe X»: solo búsqueda textual, sin ejecutar la app. | Todas | Verificación manual y `grep` dirigido al inicio de cada tarea. |

---

## 10. Próximo paso sugerido

Convertir T1 a T4 en especificaciones formales con EARS y criterios de aceptación en
`.docs/project-info/specs/` (un archivo por tarea, siguiendo el método `feature-forge`), tomando las
secciones 3.1 a 3.4 de este documento como borrador. Empezar por **T1**, por ser la única con riesgo de
pérdida de datos.
