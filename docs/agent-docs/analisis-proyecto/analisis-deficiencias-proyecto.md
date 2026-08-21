# Análisis de deficiencias — Enki

Auditoría completa del proyecto (arquitectura, seguridad, correctitud y experiencia de usuario), realizada
sobre el **estado actual del árbol de trabajo** (con cambios extensos sin commitear: nuevos módulos de
i18n, editor, skills, etc.), no solo sobre `HEAD`. El objetivo es servir de inventario de partida para
priorizar correcciones, en la misma línea que el roadmap por fases ya iniciado (`Fase 0`, `Fase 1`).

Cada hallazgo indica archivo:línea verificado, severidad (**Crítica** / **Alta** / **Media** / **Baja**) y
si es un problema técnico [Técnico], de seguridad [Seguridad], de experiencia de usuario [UX] o de
accesibilidad [Accesibilidad]. Los puntos marcados como "verificado correcto" se incluyen porque el
alcance de la auditoría los pedía explícitamente, no porque sean un defecto.

> Nota de contexto: `AGENTS.md` documenta decisiones deliberadas del proyecto (sin contenedor de DI,
> composición manual en `MainWindowViewModel`, JSON plano en `%AppData%/Enki`). Ninguno de esos patrones
> se reporta aquí como defecto en sí mismo; solo se señalan las consecuencias concretas y verificables que
> generan.

---

## 1. Los 10 problemas más graves (orden de prioridad recomendado)

| # | Problema | Módulo | Severidad | Impacto |
|---|---|---|---|---|
| 1 | `VaultService.WriteContent` escribe el Markdown de las notas sin atomicidad ni manejo de errores (`File.WriteAllText` directo) | Notas | Crítica | Corrupción/pérdida del contenido de notas ante un corte de escritura |
| 2 | El autoguardado del editor (`EditorViewModel.Save`, cada 600ms) no captura excepciones; un fallo de E/S puede tumbar toda la app con todas las pestañas abiertas | Notas/Arquitectura | Crítica | Pérdida de trabajo no guardado en todas las pestañas |
| 3 | `Reminder.IsTriggered` no se resetea al reprogramar (arrastre en calendario o sync remoto) → el recordatorio deja de notificar **para siempre**, mostrándose igualmente como "Programado" | Recordatorios | Crítica | El usuario cree que se le avisará y nunca ocurre |
| 4 | Borrar una columna Kanban no-core destruye permanentemente todas sus tarjetas, sin papelera ni confirmación, con un botón a 2px del de renombrar | Kanban | Crítica | Pérdida irreversible de tareas por un clic equivocado |
| 5 | Filas `Dirty` no sincronizadas de una cuenta Polar se empujan a la **siguiente cuenta vinculada** en el mismo dispositivo (el store local no tiene concepto de propietario) | Polar/Seguridad | Alta | Fuga real de datos entre cuentas Supabase distintas |
| 6 | `Backlinks.Refresh()` relee el vault completo dos veces en cada pulsación de tecla, sin debounce | Notas | Alta (escala mal) | El editor se vuelve inservible con vaults grandes |
| 7 | No existe ningún logging ni crash reporting en toda la app (0 usos de `ILogger`/fichero de log) | Arquitectura | Alta | Imposible diagnosticar fallos reportados por usuarios |
| 8 | Tokens de sesión Supabase en texto plano en `settings.json`, y "Exportar ajustes" los copia sin redactar a cualquier destino elegido por el usuario | Polar/Seguridad | Alta | Robo de sesión completa vía backup/USB/soporte técnico |
| 9 | Cero uso de `AutomationProperties` en las 17 vistas .axaml; cero `TabIndex` | Accesibilidad | Alta | App inutilizable con lector de pantalla |
| 10 | Sesiones borradas (soft-delete real en disco) no tienen ninguna vía de restauración en la UI, y borrar una sesión no pide confirmación | Sesiones | Alta | Borrado de facto irreversible de conjuntos de notas |

---

## 2. Arquitectura, persistencia y operaciones (transversal)

### Arquitectura y composición
- **[Técnico] Media** — `MainWindowViewModel` construye sus 15 servicios directamente (`new SettingsService()`,
  `new KanbanService()`, etc.) sin usar el parámetro `storageDirectory` que cada servicio expone justo
  para testear sin tocar disco real (`viewmodels/shell/MainWindowViewModel.Composition.cs:20-33`).
  Consecuencia verificable: no existe ni un solo test sobre `MainWindowViewModel`.
- **[Técnico] Baja** — `Program.Main` no envuelve `SingleInstanceGuard.Acquire()` en try/catch; un fallo
  ahí abortaría el arranque sin ningún mensaje al usuario.
- Verificado: no hay leaks de eventos en el shell (suscripciones de por vida del proceso, correcto para
  un singleton de facto).

### Persistencia
- **[Técnico] Crítica** — `VaultService.WriteContent` (`services/VaultService.cs:287`) usa
  `File.WriteAllText` directo, sin fichero temporal + rename ni manejo de excepciones — a diferencia de
  `JsonFileStore.Save` (`services/JsonFileStore.cs:61-68`), que sí es atómico (temp file + `File.Move`) y
  usa un mutex nombrado por hash de ruta. El patrón "seguro" ya existe en el repo pero nunca se aplicó al
  contenido más valioso: las notas del usuario.
- **[Técnico] Alta** — `services/SkillService.cs:121` (`SaveSkill`) tiene el mismo defecto estructural
  para las skills.
- **[Técnico] Media** — `SessionService.WriteManifest` (`services/SessionService.cs:463-480`) tampoco es
  atómico, aunque sí captura `IOException`/`UnauthorizedAccessException` y las reporta.
- **[Técnico] Media** — `JsonFileStore.TryLoad<T>` traga cualquier excepción con un `catch { }` genérico
  (`services/JsonFileStore.cs:49-52`); un `settings.json` corrupto resetea silenciosamente todos los
  ajustes (incluido el vínculo con Polar) sin avisar al usuario, salvo en `KanbanService`, el único que
  implementa un flag `LoadedFromCorruptData`.
- **[UX] Baja** — `VaultBackupService.CreateBackup` es tolerante a ficheros bloqueados pero no informa al
  usuario cuántos/qué ficheros fallaron en el backup.
- Verificado: `SettingsService.Save()` resuelve bien la concurrencia entre dos ventanas de Enki abiertas
  simultáneamente (no pisa el `VaultPath` de la otra instancia).

### Manejo de errores y logging
- **[Técnico] Alta** — No existe ningún sistema de logging: 0 resultados para
  `ILogger|Serilog|NLog|Console.WriteLine|Debug.WriteLine` en `services/`/`viewmodels/`. El único punto es
  `Program.cs:16-18`, que solo imprime a una consola que normalmente no existe (app `WinExe`) y no persiste
  a disco. **No hay ningún log ni siquiera para excepciones fatales.**
- **[Técnico] Alta** — No hay crash reporting: el handler de `UnhandledException` no genera volcado ni
  guarda estado; no hay handler de `TaskScheduler.UnobservedTaskException` ni del `Dispatcher` de Avalonia.
- El manejo de excepciones puntual (no el logging) está mayoritariamente bien hecho: de 68 bloques `catch`
  revisados, la mayoría son específicos y están documentados en comentarios — el problema es la ausencia
  total de rastro para depuración posterior, no el tratamiento en el momento.

### Seguridad
- Ver sección 5 (Polar/Supabase) para el detalle de credenciales — se resume aquí por ser transversal:
  **[Seguridad] Alta** — tokens de sesión de Supabase en texto plano en `settings.json`, incluidos sin
  cifrar en los backups (`.zip`) y en "Exportar ajustes".
- `local.properties` (credenciales de proyecto) está correctamente en `.gitignore` y no está trackeado —
  sin claves hardcodeadas en el código fuente.
- `HttpClient` usa validación de certificado por defecto (sin bypass) y timeouts explícitos de 20s en vez
  del default de 100s — correcto.

### Concurrencia/async
- No se encontró ningún `.Result`/`.Wait()` bloqueante (sin riesgo de deadlock clásico).
- **[Técnico] Media** — Dos usos de `async void` bien justificados (`viewmodels/sessions/SessionsViewModel.cs:186,207`),
  pero el trabajo interno (`Directory.EnumerateDirectories`) no está protegido con try/catch; una excepción
  ahí se propaga sin capturar posible y con el peor mecanismo de fallo para `async void`.
- **[Técnico] Baja** — Ningún viewmodel cancela una operación async en curso al navegar fuera
  (`GlobalSearchViewModel.SearchAsync` no cancela búsquedas anteriores, mitigado por el debounce de 200ms).

### Operaciones / release
- **Confirmado**: no hay ningún pipeline CI/CD, no hay `LICENSE`, no hay configuración de
  empaquetado/instaladores (MSI/AppImage/dmg/`.desktop`).
- **[Técnico] Media** — `Directory.Build.props:8` tiene el comentario literal
  `<!-- Fill in before shipping a build: <Company>...<Version>... -->` — la infraestructura de
  versión/copyright existe pero está vacía; el proyecto nunca ha hecho un build de release real.
- **[Técnico] Media** — Sin `TreatWarningsAsErrors`/analizadores estáticos activos, y sin CI que ejecute
  `dotnet build`/`dotnet test`, nada bloquea un commit con warnings o tests rotos.
- `<Nullable>enable</Nullable>` está activo y se respeta razonablemente bien (solo 2 supresiones de
  warning en todo el repo, ninguna sobre nulabilidad; ~39 usos de `!` casi todos sobre invariantes de
  navegación correctas pero no verificadas por el compilador).

---

## 3. Notas / Editor / Vault / Sesiones (documentos)

### Pérdida de datos y estabilidad
- **[Técnico] Crítica** — `VaultService.WriteContent` (ver §2) — el dato más importante de la app usa el
  camino menos seguro de persistencia disponible en el repo.
- **[Técnico] Crítica** — `EditorViewModel.Save` (`viewmodels/editor/EditorViewModel.cs:332-340`), invocado
  desde el `DispatcherTimer.Tick` de autoguardado cada 600ms, no tiene try/catch. Si `WriteContent` lanza
  (disco lleno, vault en unidad desconectada, antivirus bloqueando el fichero), la excepción sube sin
  capturar desde el timer y, al no existir un manejador global efectivo (§2), puede tumbar el proceso
  completo con todas las pestañas abiertas.
- **[Técnico] Alta** — El editor de Skills (`SkillEditorViewModel`, debounce de 600ms idéntico) nunca se
  fuerza a guardar (`FlushPendingSave`) al cerrar la ventana (`views/shell/MainWindow.axaml.cs:42-46` solo
  hace flush de Notas y Ajustes) — cerrar la app justo tras editar una skill pierde los últimos ~600ms.
- **[Técnico] Alta** — Ningún watcher detecta que el vault fue movido/borrado/desmontado mientras la app
  está abierta; el primer síntoma es una excepción no capturada en el siguiente autoguardado.
- **[UX] Alta** — Solo hay backup manual (zip, todo-o-nada, disparado por el usuario desde Ajustes); no
  hay backup automático ni versionado incremental. Una edición destructiva (autocompletado accidental,
  "reemplazar todo" sin el resultado esperado) no tiene ninguna versión anterior a la que volver.

### Integridad de la red de enlaces (wikilinks)
- **[Técnico][UX] Alta** — Renombrar una nota (`VaultService.RenameNote`, `NotesViewModel.CommitRenameNote`)
  **no** actualiza los `[[wikilinks]]` que apuntan a ella en otras notas; solo se relinca la tarjeta Kanban
  vinculada. Cualquier enlace a la nota renombrada queda roto sin aviso, ni al renombrar ni después.
- **[Técnico] Alta** — El panel de Backlinks (`EditorBacklinksService.FindBacklinks`) busca por **título**,
  mientras que la resolución de Ctrl+click (`WikiLinkParser.ResolveTarget`) prioriza el **nombre de
  fichero**. Título y filename divergen con facilidad (colisiones de nombre añaden sufijo solo al fichero,
  no al `# título`), y en cuanto ocurre, ambos mecanismos dejan de coincidir entre sí.
- **[UX] Media** — Un título ambiguo (dos notas con el mismo título) resuelve el enlace a "no encontrado"
  sin ningún indicio visual de que la causa es ambigüedad y no un enlace roto de verdad.
- **[UX] Media** — Borrar una nota no advierte cuántas otras notas la referencian antes de mandarla a la
  papelera.

### Otros defectos de datos
- **[Técnico][UX] Alta** — `NoteTagHelper.SetTags` (`services/NoteTagHelper.cs:69-70`) borra **todo** el
  frontmatter (no solo las tags) cuando la lista de etiquetas queda vacía. Una nota importada con
  frontmatter ajeno (`aliases:`, `created:`, etc.) pierde esas claves si el usuario simplemente quita la
  última etiqueta desde el editor.
- **[UX][Técnico] Alta** — Colisión de nombre al renombrar (`RenameNote`/`RenameFile`/`RenameFolder`) es un
  no-op silencioso si ya existe un fichero con ese nombre; el `Title` del ViewModel ya cambió (binding
  `LostFocus`) pero el fichero en disco sigue con el nombre antiguo, sin ningún aviso de que el renombrado
  falló. Sin test que cubra este camino.
- **[Técnico] Media** — Comparaciones de ruta `case-insensitive` (`StringComparison.OrdinalIgnoreCase`) en
  un filesystem case-sensitive (Linux): dos ficheros como `Todo.md`/`todo.md` se tratan como la misma
  nota, reutilizando la pestaña equivocada.

### Rendimiento
- **[Técnico] Crítica (escala mal)** — `EditorViewModel.OnTextChanged` llama a `Backlinks.Refresh()` **sin
  debounce** en cada pulsación de tecla; internamente relee el contenido completo de cada nota del vault
  **dos veces** (una para metadatos, otra para buscar el wikilink). Con vaults de cientos/miles de notas,
  el editor se vuelve perceptiblemente lento al escribir.
- **[Técnico] Media** — Sin índice de búsqueda: `GlobalSearchViewModel`, `QuickSwitcherViewModel` y
  `NotesViewModel.Reload` releen y reparsean todo el vault en cada invocación (la búsqueda global sí lo
  hace fuera del hilo de UI; el quick switcher y el listado, no).
- **[Técnico] Baja-Media** — Outline y Stats recalculan sobre el documento completo en cada tecla, sin
  debounce (en memoria, no golpea disco, pero puede introducir jank en notas muy grandes).
- **[Técnico] Media** — `VaultService.GetVaultSizeBytes` recorre síncronamente todo el árbol del vault
  (usado por el Dashboard), sin variante asíncrona.

### Cobertura de tests del módulo
Ningún test ejercita rutas de excepción de E/S en `WriteContent`/`Save` (coincide con los hallazgos #1-2),
ninguno verifica que renombrar actualice o no los wikilinks, ninguno cubre la colisión de nombre al
renombrar, y ninguno cubre el caso "frontmatter con clave ajena + tags vacías" que dispara la pérdida de
frontmatter. Punto positivo: `EditorViewModelUndoStackTests` sí verifica correctamente que cada pestaña
mantiene su propio undo/redo independiente.

---

## 4. Kanban / Papelera / Sesiones

### Borrado destructivo (el bloque más grave del módulo)
- **[Técnico] Crítica** — `KanbanViewModel.DeleteColumn` (`viewmodels/kanban/KanbanViewModel.cs:215-224`)
  hace `Board.Columns.Remove(...)` directo; las tarjetas de esa columna **nunca** pasan por
  `TrashedCards` (a diferencia de `DeleteCard`, que sí las mueve a papelera). El botón no tiene diálogo de
  confirmación y está a 2px del icono de renombrar (`views/kanban/KanbanView.axaml:135-140`). El test
  existente (`KanbanViewModelTests.cs:74-86`) borra una columna **vacía**, por lo que nunca detecta la
  pérdida real de tarjetas.
- **[Técnico] Media** — `TrashViewModel.RestoreTask` (`viewmodels/trash/TrashViewModel.cs:168-193`) quita
  la tarjeta de `TrashedCards` **antes** de resolver la columna destino; si no hay ninguna columna, la
  tarjeta queda fuera de la papelera y fuera de cualquier columna, perdida en silencio.
- **[UX] Media** — Borrado permanente individual (tarea/nota/fichero) se dispara al primer clic sin
  confirmación, mientras que "vaciar papelera" sí pasa por un modal — inconsistente, y la acción más fácil
  de disparar por error es la que menos protección tiene.
- **[UX] Baja-Media** — La papelera de Kanban nunca purga automáticamente; las tarjetas se acumulan hasta
  vaciado manual.

### Sesiones
- **[UX] Alta** — `SessionService.Delete` hace soft-delete real moviendo la carpeta a
  `<vault>/.trash/sessions/`, pero `TrashViewModel`/`TrashView.axaml` solo cubren Notas, Tareas, Archivos y
  Recordatorios — **no Sesiones**. Esa carpeta es inalcanzable desde la UI: una sesión borrada (que puede
  contener decenas de notas) no tiene ningún camino de restauración salvo tocar el sistema de archivos a
  mano.
- **[UX] Alta** — `DeleteSessionCommand` se ejecuta sin ningún diálogo de confirmación. Combinado con el
  punto anterior, un clic accidental sobre el icono de papelera de una sesión es, en la práctica,
  irreversible desde la app.
- Positivo: la migración del grafo de sesiones (sigma.js/d3-force, fuera de WebGL) no deja código muerto de
  la implementación anterior y ya atiende el rendimiento con grafos grandes (caché de sprites de glow).
- **[Técnico] Baja** — El `WebView` del grafo no se dispone/desuscribe explícitamente al salir de la
  pestaña; posible acumulación de recursos si se entra/sale repetidamente (no confirmado el comportamiento
  interno de `NativeWebView`).

### Accesibilidad y UX de producto
- **[UX][Accesibilidad] Alta** — No existe ninguna forma de mover una tarjeta entre columnas sin
  drag&drop: `TargetColumn` se fija al construir el editor de tarjeta y no hay selector de columna en el
  formulario. Un usuario que dependa del teclado o cuyo gesto de arrastre falle no tiene alternativa salvo
  borrar y recrear la tarjeta en otra columna.
- **[UX] Baja** — Enlace roto nota↔tarjeta (nota borrada/movida) es invisible de un vistazo al tablero; solo
  se descubre al hacer clic (aunque abrir el enlace sí notifica correctamente si falla).
- Ausencia de límites WIP: no es un bug, es una omisión de producto coherente con un tablero deliberadamente
  simple — se documenta porque el alcance de la auditoría lo pedía explícitamente.

---

## 5. Polar / Supabase (sincronización cloud) — seguridad y correctitud

El motor de sincronización en sí (LWW servidor-autoritativo, cola offline vía `Dirty`, paginación con
cursor compuesto, distinción error-de-red vs error-de-servidor, backoff exponencial, respeto real a la
convención de `NotificationService` para fallos) está **implementado con notable cuidado** y coincide con
el diseño documentado. Los problemas serios están en la gestión de identidad de cuenta y credenciales, no
en el protocolo de sync.

### Identidad de cuenta — el hallazgo más grave del módulo
- **[Seguridad][Correctitud] Alta** — El store local de Polar (`polar-tasks.json`) **no tiene ningún campo
  `UserId`**; es un store único y global independiente de qué cuenta esté vinculada.
  `PolarAuthService.Unlink()` (`services/supabase/PolarAuthService.cs:106-118`) solo borra los campos de
  sesión en `AppSettings`, nunca los datos cacheados.
- **[Seguridad] Alta** — Consecuencia directa: `PolarSyncService` (`PushTaskListsAsync`/`PushTasksAsync`/
  `PushSubtasksAsync`/`PushRemindersAsync`, `services/polar/PolarSyncService.cs:176-358`) selecciona
  **todas** las filas `Dirty` del store local y las envía con `UserId = _auth.UserId` — la cuenta
  *actualmente* vinculada, sin importar bajo qué sesión se crearon. Escenario concreto: el usuario crea una
  tarea offline con la cuenta A, se desvincula antes de sincronizar, vincula la cuenta B, y esa tarea de A
  se sube al proyecto Supabase de B con `user_id = B`. **Es fuga de datos real entre cuentas distintas**,
  no solo un bug de sincronización, y ningún test lo cubre.

### Credenciales
- **[Seguridad] Alta** — `PolarAccessToken`/`PolarRefreshToken` en texto plano en `settings.json`
  (`models/AppSettings.cs:73-78`), sin cifrado ni almacén seguro del SO (DPAPI/Secret Service/Keychain).
  El refresh token es de larga duración: cualquier proceso con acceso de lectura al fichero puede
  reutilizarlo para impersonar la cuenta.
- **[Seguridad] Alta** — Ese mismo fichero se incluye sin cifrar en el `.zip` de backup
  (`services/VaultBackupService.cs:48-52`).
- **[Seguridad] Alta** — "Exportar ajustes" (`viewmodels/settings/SettingsViewModel.cs:1082-1104`) hace
  `File.Copy` literal de `settings.json` a cualquier destino elegido por el usuario, **sin redactar los
  tokens** ni advertir de que el fichero exportado permite suplantar la sesión — convierte una función de
  backup legítima en un vector de exfiltración de credenciales.
- **[Seguridad] Media** — El esquema HTTPS no se fuerza explícitamente: `SupabaseConfig.Load` no valida que
  `SUPABASE_URL` empiece por `https://`; si degradara a `http://`, tanto REST como el websocket de Realtime
  (que hace `.Replace("https://","wss://")`) caerían en texto plano.

### Sincronización, Realtime y UX de fallos
- Verificado correcto: LWW real (compara `updated_at`, no "el último que llega gana sin comparar"),
  protección contra edición local mientras un push está en vuelo (no se limpia `Dirty` si el valor local
  cambió durante el envío, cubierto por test), y ninguna ruta de red se ejecuta si
  `SupabaseConfig.IsConfigured=false` (verificado con test que asegura cero llamadas HTTP).
- **[Correctitud] Media** — `SyncNowAsync` usa `SemaphoreSlim(1,1).WaitAsync(0, ct)`: un sync manual que
  coincide con uno automático se descarta silenciosamente, sin distinguir "completado" de "descartado" en
  el botón "Sync now".
- **[UX] Media** — Solo se notifica en la *transición* a fallo (`_polarSyncWasFailing`); si el usuario
  descarta el aviso inicial y el servidor sigue caído, no habrá otro aviso salvo que vuelva a abrir la
  pestaña con el indicador de estado.
- **[Correctitud] Media** — `PolarRealtimeService` no reintenta la conexión si el socket cae tras conectar;
  degrada silenciosamente a solo-polling (hasta 5 min de latencia en el peor caso) sin avisar al usuario.
- **[UX] Baja** — Cuando el cliente pierde un conflicto LWW frente a una edición remota (p. ej. desde
  Android), el cambio local se sobrescribe sin ningún aviso al usuario.
- Verificado correcto: sin interpolación insegura en las queries REST, sin logging de tokens, aislamiento
  por usuario correctamente delegado a RLS de Supabase (un token robado solo expone los datos de esa
  cuenta, no de otros usuarios — aunque eso ya es un radio de explosión alto para la propia víctima, dado
  que incluye recordatorios geolocalizados).

---

## 6. Calendario / Recordatorios / Notificaciones

### Fechas y recurrencia — el bloque más grave del módulo
- **[Técnico] Crítica** — `Reminder.IsTriggered` (`models/Reminder.cs:32`) **nunca se resetea** al
  reprogramar: ni `ReminderService.Reschedule` (`services/ReminderService.cs:152-160`, verificado: solo
  actualiza `NotifyAtUtc` y `Touch()`) ni `PolarSyncService.ApplyReminderDto` (al recibir un cambio remoto
  desde Polar/móvil) tocan ese flag. `PopDueReminders` (`services/ReminderService.cs:215-221`) solo dispara
  recordatorios con `!r.IsTriggered`. Escenario: un recordatorio vence, se marca disparado; el usuario lo
  arrastra a otra fecha en el calendario (o llega reprogramado desde el móvil) — la pantalla de
  Recordatorios lo muestra correctamente como "Programado", pero **no volverá a notificar jamás**, porque
  el flag de "ya notificado" sigue en `true`. Es exactamente el escenario de "el usuario pierde una fecha
  límite sin saberlo".
- **[Técnico] Alta** — `PolarTaskService.CalculateNextDueDate` (`services/polar/PolarTaskService.cs:279-296`)
  calcula la siguiente ocurrencia de tareas recurrentes con aritmética **UTC pura**
  (`currentDueDate.AddDays/AddMonths` sobre el `DateTime` UTC almacenado, sin reconvertir a hora local).
  Cada cambio de horario de verano/invierno desplaza ~1h (a veces de día) tanto el avance real de la tarea
  como sus ocurrencias fantasma en el calendario. Sin ningún test de DST.
- **[Técnico] Media** — `PopDueReminders` no tiene protección contra saltos de reloj del sistema; adelantar
  el reloj manualmente dispara de golpe todos los recordatorios pendientes, y por el hallazgo anterior
  quedarían mudos para siempre al corregir la hora.
- **[UX] Media** — Los `Reminder` propios de Enki (los no vinculados a Polar) no admiten recurrencia; solo
  las tareas Polar (que requieren cuenta vinculada) la tienen.

### Agregación del calendario
- Verificado correcto: no hay duplicación de ítems entre Kanban/Polar/Recordatorios (cada fuente se
  etiqueta con `SourceType`/`SourceId` propios), y los elementos sin fecha se manejan bien
  (`BuildUnscheduledItems`) aunque sin ningún test que lo cubra.
- **[Técnico] Baja** (tradeoff ya documentado en el diseño, no un bug nuevo) — cada `Rebuild()` dispara al
  menos 3 recorridos completos de Kanban+Polar+expansión de ocurrencias fantasma en cada cambio de
  cualquiera de las tres fuentes, incluso si el cambio no toca fechas.
- Por diseño, ningún ítem del calendario puede cruzar medianoche (duración capada al mismo día) —
  limitación de producto conocida, no un bug.

### Recordatorios y notificaciones
- **[UX] Media** — No existe forma de deshacer un "Dismiss" accidental en la bandeja de recordatorios del
  shell; la única recuperación es cambiar la fecha de la tarjeta y volver a ponerla igual.
- **[Técnico] Baja** — `DismissedReminderService` no purga entradas de tarjetas ya eliminadas (crecimiento
  sin límite), a diferencia de `NotificationService`, que sí capa a 50 elementos.
- **[UX] Baja** — No hay botón de "probar sonido" en Ajustes para verificar el volumen/sonido elegido antes
  de que llegue una notificación real.
- Verificado correcto y bien testeado: el gating de "no molestar" (`DoNotDisturbSchedule`, incluida ventana
  que cruza medianoche) es consistente en toda la app, y la cadena de fallback de sonido está bien
  documentada y probada.

### Cobertura de tests
Buena cobertura de los casos propios de DND y sonido; **cero tests de DST/zona horaria** en todo
`enki.Tests` pese a ser el hallazgo #2 del módulo; `Reschedule` no tiene ningún test que verifique el
re-armado de `IsTriggered`; los tests de UI del calendario (`CalendarViewUiTests.cs`) se limitan a 2 casos
de render, cobertura mínima para un módulo de ~2900 líneas de viewmodels.

---

## 7. UX transversal, internacionalización y accesibilidad

### Accesibilidad — la deficiencia más grave de esta sección
- **[Accesibilidad] Alta** — Cero resultados para `AutomationProperties` en las 17 vistas `.axaml`
  (verificado con grep exhaustivo). Ningún botón icon-only, popover, toggle o campo de formulario tiene
  nombre accesible: **la app es inutilizable con un lector de pantalla** (NVDA/Orca/VoiceOver).
- **[Accesibilidad] Media** — Cero uso de `TabIndex` en toda la app; el orden de tabulación depende
  enteramente del árbol visual por defecto, sin auditoría deliberada de foco en layouts complejos (Kanban,
  Calendario, Ajustes).
- **[UX] Baja-Media** — Atajos de teclado (`KeyBinding`) concentrados en solo 3 de 17 vistas (`MainWindow`,
  `FilesView`, `NotesView`); Kanban, Calendario, Recordatorios, Papelera, Sesiones y Skills no tienen
  atajos propios más allá de los globales.

### Internacionalización — mejor de lo que sugiere la propia documentación del proyecto
- Verificado: la implementación de i18n está **completa y madura** — 962 claves con paridad exacta en 7
  idiomas (`enki/src/enki/localization/`, con `ResourceKeyParityTests` verificándolo), manejo de
  excepciones del SO por tipo específico en el onboarding, y **cero fugas reales de idioma** tras grep
  exhaustivo de literales en las 17 vistas y ~86 viewmodels/servicios.
- **[Deuda técnica] Media** — `docs/agent-docs/internacionalizacion-i18n.md` describe la i18n como trabajo
  **pendiente por completo** ("hoy Enki es una app 100% hardcodeada en español"), cuando el código ya la
  tiene implementada — el documento quedó desfasado respecto al propio código que dice describir; puede
  confundir a cualquiera que lo lea como estado actual.
- **[i18n] Baja** — Un único mensaje de excepción interno en español
  (`viewmodels/shell/ReminderItemViewModel.cs:38`, invariante de constructor que no debería dispararse en
  uso normal).

### Documentación desfasada respecto al código
- **[UX/Proceso] Media** — `README.md` enlaza a `docs/analisis-proyecto/`, `docs/calendar-feature/`,
  `docs/supabase-sync/`, `docs/notificaciones-y-recordatorios.md` — las ~30 rutas están borradas del árbol
  de trabajo actual (`git status` las marca `D`, sin commitear).
- **[UX/Proceso] Media** — Varios comentarios XML-doc en código de producción referencian activamente
  ficheros que no existen en disco: `docs/ajustes-docs/01-04-*.md` (citados desde `SettingsViewModel.cs`,
  `AppSettings.cs`), `docs/seccion-archivos.md` (`FilesViewModel.cs:230`),
  `docs/funcionalidad-zoom-interfaz.md`, `docs/analisis-mejoras-funcionalidades.md`,
  `docs/ia-skills/especificacion-desarrollo.md` (citados desde `SkillService.cs`, `AppSettings.cs`). Un
  contribuidor o un agente que siga cualquiera de estos enlaces llega a un 404 dentro del propio repo.

### Sistema de diseño
- **[Deuda técnica] Baja** — `views/settings/SettingsView.axaml:288,294`: la barra de zoom de interfaz usa
  `CornerRadius="3"` en dos `Border` rectangulares, contradiciendo la regla explícita de "cero excepciones,
  `CornerRadius=0` salvo indicadores circulares" de `docs/sistema-diseno-web.md`/`AGENTS.md`. Es la única
  violación encontrada en todo el sistema de diseño.

### Onboarding, ajustes y funcionalidades nuevas — verificado sólido
- `LanguageSetupViewModel`/`VaultSetupViewModel` manejan bien cancelación de selector de carpeta, ruta
  inválida, permisos denegados, ruta demasiado larga y vault ya existente con notas (no sobrescribe la
  nota de bienvenida). No se encontró ningún `TODO`/`FIXME`/`NotImplementedException` real en todo
  `enki/src/enki/` tras grep exhaustivo.
- Todas las propiedades de `AppSettings` revisadas tienen efecto y persistencia real; no hay ajustes "a
  medias". `Skills` tiene CRUD completo (import/export/duplicar/eliminar, resolución de conflictos de
  nombre), no es un placeholder.

### Primera impresión de un usuario nuevo
- **[UX] Media** — Sin `LICENSE` en la raíz del repo.
- **[UX] Media** — Sin instalador ni empaquetado (`.iss`/`.desktop`/`.deb`/build de release); solo
  `dotnet build`/`dotnet run` documentados — un usuario no técnico no puede "instalar" Enki, solo un
  desarrollador con el SDK de .NET 8.
- **[UX] Baja** — README 100% en español pese a que la app ya soporta 6 idiomas de UI.

---

## 8. Resumen ejecutivo global

El proyecto está en un punto notablemente más maduro de lo que su propia documentación de planificación
sugiere (la i18n, por ejemplo, ya está completa pese a que el doc que la describe la marca como pendiente),
y buena parte de la infraestructura de persistencia y sincronización (`JsonFileStore`, el protocolo LWW de
Polar, el gating de "no molestar") está resuelta con cuidado real, con manejo de excepciones específico y
bien razonado en comentarios. Sin embargo, la auditoría encuentra un patrón que se repite en varios
módulos independientes: **el camino que persiste el dato más valioso de cada función usa consistentemente
el método menos seguro disponible**, mientras que ya existe en el propio repo un patrón atómico y
bien probado (`JsonFileStore`) que simplemente no se aplicó ahí — así ocurre con el contenido Markdown de
las notas (`VaultService.WriteContent`, sin atomicidad, sin manejo de errores, y con el autoguardado capaz
de tumbar la app entera) y, de forma análoga, con el flag `IsTriggered` de los recordatorios, que puede
dejar de notificar para siempre sin que nada en la UI lo delate.

El segundo bloque de riesgo real es la ausencia casi total de redes de seguridad ante el error humano:
borrar una columna Kanban, borrar una sesión completa, o exportar los ajustes de la app son todas acciones
irreversibles o expone-credenciales que hoy se ejecutan con un solo clic y sin confirmación ni redacción de
datos sensibles. A esto se suma un hallazgo de seguridad concreto y explotable en el mundo real del módulo
Polar (filas no sincronizadas de una cuenta se empujan a la siguiente cuenta vinculada en el mismo
dispositivo), y una deficiencia de accesibilidad total (cero soporte de lector de pantalla) que excluye por
completo a una categoría de usuarios. Finalmente, la total ausencia de logging/crash reporting y de
infraestructura de CI/release convierte cualquiera de estos problemas, una vez lleguen a producción, en
prácticamente indiagnosticable sin reproducción en vivo por parte del desarrollador.

**Orden de prioridad recomendado**: (1) atomizar y proteger con try/catch `VaultService.WriteContent` y el
autoguardado del editor; (2) corregir el reseteo de `Reminder.IsTriggered` al reprogramar; (3) mover el
borrado de columnas Kanban y de sesiones al flujo de papelera + confirmación ya existente para el resto de
entidades; (4) resolver el aislamiento de cuenta en el store local de Polar antes de que el escenario de
fuga entre cuentas se materialice con un usuario real; (5) introducir un logging mínimo a fichero y un
handler de crash que al menos persista el error antes de terminar el proceso — sin esto, cualquier
corrección de los puntos anteriores seguirá siendo difícil de verificar en producción.
