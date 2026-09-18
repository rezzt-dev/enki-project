# Roadmap de corrección de deficiencias — Enki

Plan de trabajo en fases para resolver **todos** los hallazgos de
[analisis-deficiencias-proyecto.md](analisis-deficiencias-proyecto.md). Mismo criterio que el resto de
roadmaps del proyecto (`docs/calendar-feature/12-plan-de-implementacion-por-fases.md`): cada fase es
entregable de forma independiente, deja la app usable y sin romper nada, y no hace falta completar el
roadmap entero para obtener valor. Ordenado por relación riesgo/esfuerzo — primero lo que puede causar
pérdida de datos o fuga de seguridad real, después rendimiento y accesibilidad, al final higiene de
proceso y mejoras opcionales.

> Relación con el histórico de commits: este roadmap es independiente del que ya completó
> `Fase 0 — Higiene de repositorio` y `Fase 1 — Cerrar fallos silenciosos` (commits `1d5a83d`, `2dcd127`,
> `134ef7f`, roadmap ya borrado del árbol de trabajo). Aquel cubría una primera pasada de deuda técnica;
> este parte de una auditoría nueva y mucho más profunda sobre el estado actual del código, con hallazgos
> distintos (algunos nuevos, otros que aquel roadmap no llegó a cubrir). Para evitar confundir ambos
> historiales, las fases de este documento se numeran de forma independiente y no reutilizan los fallos ya
> cerrados por el roadmap anterior.

Cada tarea cita la sección del análisis de la que procede (`análisis §N`) para poder volver al detalle
completo (archivo:línea, escenario de fallo, severidad) sin repetirlo aquí.

---

## Cómo usar este roadmap

Márquese aquí qué fase está en curso o completada según se implemente, para que cualquier sesión nueva
(persona o agente) sepa por dónde retomar sin releer todo el análisis:

- [x] Fase 0 — Red de seguridad mínima (logging y resiliencia básica)
- [x] Fase 1 — Persistencia atómica del contenido de usuario
- [x] Fase 2 — Recordatorios: notificación perdida y deriva horaria
- [x] Fase 3 — Borrado seguro: papelera y confirmaciones consistentes
- [x] Fase 4 — Seguridad de credenciales y aislamiento de cuentas Polar
- [x] Fase 5 — Integridad de la red de enlaces (wikilinks)
- [x] Fase 6 — Rendimiento del editor y del vault
- [x] Fase 7 — Accesibilidad
- [x] Fase 8 — Higiene de documentación y operaciones de release
- [x] Fase 9 (opcional) — Robustez de sincronización Polar y notificaciones
- [x] Fase 10 (opcional) — Deuda técnica menor y mejoras de producto derivadas de la auditoría

---

## Fase 0 — Red de seguridad mínima (bloqueante, esfuerzo bajo) ✅ completada

Sin esto, verificar en producción que las fases siguientes realmente funcionan es imposible — es la razón
por la que va primero, igual que la higiene de repositorio del roadmap anterior.

- [x] Introducir un logging mínimo a fichero (rotación simple, sin dependencias pesadas — por ejemplo
      `Microsoft.Extensions.Logging` + un `FileLoggerProvider` ligero, o `Serilog` con un solo sink de
      fichero) en `%AppData%/Enki/logs/`. Nivel `Warning`+ por defecto, `Debug` activable desde Ajustes.
      (análisis §2, "Manejo de errores y logging") — `Services/LogService.cs`: fichero de texto plano
      rotado por día (`enki-yyyyMMdd.log`), sin dependencias, con purga automática de los ficheros de más
      de 14 días al arrancar. El interruptor "Registro de depuración" vive en Ajustes › General ›
      Diagnóstico (`AppSettings.DebugLoggingEnabled`, `SettingsViewModel.DebugLoggingEnabled`), con un
      botón "Abrir carpeta de logs" al lado.
- [x] Sustituir el handler actual de `AppDomain.CurrentDomain.UnhandledException` (`Program.cs:16-18`) por
      uno que además escriba la excepción completa (stack trace, tipo, mensaje, versión de la app) al log
      antes de que el proceso termine. — `Program.cs`: el handler ahora llama a `LogService.Error(...)`
      con la excepción completa, además de seguir escribiendo a consola.
- [x] Añadir un handler de `TaskScheduler.UnobservedTaskException` que registre la excepción en vez de
      dejarla desaparecer silenciosamente. — `Program.cs`, cableado junto al resto de handlers de arranque.
- [x] Envolver `SingleInstanceGuard.Acquire()` en `Program.Main` en un try/catch que al menos loguee y
      muestre un mensaje mínimo antes de salir si el mutex falla. (análisis §2, "Arquitectura y
      composición") — `Program.cs`: loguea con `LogService.Error`, avisa por consola y sale con código 1
      en vez de dejar que la excepción escape sin control.
- [x] Proteger `SessionService.List()`/`LoadCombinedGraph()` (usadas desde los `async void` de
      `SessionsViewModel.Reload`/`LoadGraph`) con try/catch alrededor de
      `Directory.EnumerateDirectories(SessionsRoot)`, para que un permiso denegado o una unidad
      desconectada no propague una excepción no capturable en un `async void`. (análisis §2,
      "Concurrencia/async") — `SessionService.ListSessionDirectories()`: captura
      `IOException`/`UnauthorizedAccessException`, registra en `LogService` y avisa por el `ManifestError`
      ya existente (que `MainWindowViewModel.Composition` reenvía a `NotificationService`) en vez de
      propagar.

**Criterio de aceptación**: forzar un fallo de E/S real (p. ej. quitar permisos de escritura al vault
mientras la app corre) produce una entrada en el log con stack trace legible, y la app no termina sin
rastro. Verificado con `LogServiceTests` (escritura real a fichero, filtrado por nivel, degradación sin
excepción cuando la carpeta de logs es inutilizable) y con
`SessionServiceTests.List_DoesNotThrow_WhenSessionsRootIsUnreadable` (permiso denegado real vía
`UnixFileMode` sobre `.sessions`).

**Dependencias**: ninguna. Desbloquea la verificación de todas las fases siguientes.

---

## Fase 1 — Persistencia atómica del contenido de usuario (esfuerzo medio, impacto crítico) ✅ completada

El bloque de mayor severidad del análisis: el contenido que el usuario más valora (notas, skills) se
guarda hoy con el método menos seguro del repo, mientras que ya existe uno atómico y probado
(`JsonFileStore`) sin usar ahí.

- [x] Reescribir `VaultService.WriteContent` (`services/VaultService.cs:287`) para escribir a un fichero
      temporal en el mismo directorio y hacer `File.Move(tmp, note.FullPath, overwrite: true)`, replicando
      el patrón de `JsonFileStore.Save`. Envolver en try/catch específico
      (`IOException`/`UnauthorizedAccessException`/`DirectoryNotFoundException`) y propagar el fallo a
      quien llama en vez de tragarlo. (top 10 #1, análisis §3) — implementado vía `VaultService.WriteFileAtomic`
      (fichero temporal + `File.Move(overwrite: true)`, log en `LogService.Error` y rethrow), reutilizado
      también por `CreateNote`.
- [x] `EditorViewModel.Save` (`viewmodels/editor/EditorViewModel.cs:332-340`): capturar el fallo de
      `WriteContent`, marcar visualmente la pestaña como "no guardado" (icono/color de estado ya existente
      para "pendiente"), y notificar al usuario vía `NotificationService.Push(...)` con el motivo si es
      identificable (disco lleno, permisos, ruta no encontrada). El timer de autoguardado debe seguir
      funcionando tras un fallo puntual (reintentar en el siguiente `Tick`), no quedar en estado roto. (top
      10 #2, análisis §3) — `Save` captura `IOException`/`UnauthorizedAccessException`, deja `IsDirty`/
      `_pendingText` intactos, reinicia `_saveTimer` para reintentar en el siguiente tick y notifica vía
      `NotificationService` (motivo resuelto por `VaultFailureReasons.Describe`). `OnTitleChanged` (renombrar
      desde el propio editor) recibe el mismo tratamiento: revierte el título en vez de divergir del disco.
- [x] Mismo patrón atómico + manejo de excepciones en `SkillService.SaveSkill`
      (`services/SkillService.cs:121`). (análisis §3, "Pérdida de datos y estabilidad") — y
      `SkillEditorViewModel.Save` con el mismo tratamiento de fallo/reintento que `EditorViewModel.Save`.
- [x] Mismo patrón atómico en `SessionService.WriteManifest` (`services/SessionService.cs:463-480`), que
      hoy ya captura las excepciones pero no escribe de forma atómica. (análisis §2, "Persistencia")
- [x] `MainWindow.axaml.cs:42-46` (`Closing`): añadir el flush pendiente del editor de Skills
      (`SkillsViewModel.ActiveEditor?.FlushPendingSave()`, ya existe el método, solo falta la llamada) junto
      al de Notas y Ajustes. (análisis §3, "Pérdida de datos y estabilidad")
- [x] Detectar vault inaccesible de forma proactiva: antes de cada operación de escritura relevante
      (`WriteContent`, `RenameNote`, `CreateNote`, `MoveNote`), comprobar `Directory.Exists(VaultPath)`; si
      no existe, notificar claramente ("tu vault no está disponible, revisa la conexión de la unidad") en
      vez de dejar que el primer síntoma sea una excepción de autoguardado. (análisis §3, "Pérdida de datos
      y estabilidad") — `VaultService.EnsureVaultAvailable` lanza `VaultUnavailableException` (deriva de
      `IOException`); `NotesViewModel` capta el fallo en crear/renombrar/mover y notifica.
- [x] Extender el patrón `LoadedFromCorruptData` de `KanbanService` (hoy único caso) a
      `SettingsService`/`NotificationService`/`ReminderService`, para que un JSON corrupto notifique al
      usuario en vez de resetear silenciosamente los ajustes (incluido el vínculo Polar). (análisis §2,
      "Persistencia")
- [x] `VaultBackupService.CreateBackup` (`services/VaultBackupService.cs:33-53`): devolver/loguear la lista
      de ficheros que fallaron al incluirse en el backup, y mostrarla al usuario si no está vacía, en vez de
      un backup silenciosamente incompleto. (análisis §3, "Pérdida de datos y estabilidad") — `CreateBackup`
      devuelve ahora `VaultBackupResult` (`FailedEntries`/`IsComplete`); `SettingsViewModel.CreateBackup`
      muestra un aviso distinto cuando el backup queda incompleto.
- [x] Añadir backup automático periódico opcional (p. ej. diario, configurable en Ajustes, reutilizando
      `VaultBackupService.CreateBackup` con rotación de los últimos N backups) como complemento al backup
      manual todo-o-nada que existe hoy — hoy una edición destructiva sin que el usuario haya pulsado
      "backup" no tiene ninguna versión anterior a la que volver. (análisis §3, "Pérdida de datos y
      estabilidad") — `AppSettings.AutoBackupEnabled`/`AutoBackupIntervalDays`/`AutoBackupRetentionCount`/
      `LastAutoBackupUtc` + `VaultBackupService.RunScheduledBackupIfDue` (rotación incluida), comprobado por
      un timer en `MainWindowViewModel` cada 6h y al arrancar; toggle en Ajustes › General › zona de peligro.

**Tests añadidos**: `VaultServiceTests` con un `IOException`/`UnauthorizedAccessException` simulada en la
escritura (directorio inexistente vía `VaultUnavailableException`, y directorio de solo lectura) verificando
que se propaga en vez de tragarse, más un test de que `WriteContent` no deja ficheros `.tmp-*` tras un
guardado correcto; `EditorViewModelSaveFailureTests` confirma que un fallo de `WriteContent`/`RenameNote` no
descarta el contenido no guardado (ni el título) y que un reintento posterior (vault restaurado) tiene
éxito; `SkillsViewModelTests.CloseEditor_FlushesAPendingEdit_InsteadOfLosingIt` confirma el flush de skills
al cerrar el editor (mismo camino que `MainWindow.axaml.cs`); `SkillServiceTests`,
`SettingsServiceTests`/`NotificationServiceTests`/`ReminderServiceTests` (`LoadedFromCorruptData`) y
`VaultBackupServiceTests` (ficheros fallidos + `RunScheduledBackupIfDue`, incluida la rotación) cubren el
resto de la fase. Suite completa verificada en verde (519 tests).

**Dependencias**: usa el logging de la Fase 0 para que los fallos capturados dejen rastro.

---

## Fase 2 — Recordatorios: notificación perdida y deriva horaria (esfuerzo bajo-medio, impacto crítico) ✅ completada

- [x] `ReminderService.Reschedule` (`services/ReminderService.cs:152-160`): añadir
      `reminder.IsTriggered = false;` antes de `Touch(reminder)`. (top 10 #3, análisis §6) — implementado
      tal cual: reprogramar (arrastre en el calendario) rearma el recordatorio para que vuelva a notificar
      en la nueva fecha.
- [x] `PolarSyncService.ApplyReminderDto` (`services/polar/PolarSyncService.cs:548-560`): igual, resetear
      `IsTriggered` cuando `NotifyAtUtc` cambia por un valor recibido del servidor (reprogramación desde
      Android/Polar). (análisis §6) — el rearme es condicional (`reminder.NotifyAtUtc != notifyAtUtc` antes
      de sobrescribir): una edición remota que no toca la fecha (título, nota, ubicación) no provoca una
      segunda notificación del mismo aviso.
- [x] `PolarTaskService.CalculateNextDueDate` (`services/polar/PolarTaskService.cs:279-296`): convertir el
      `DateTime` UTC almacenado a hora local del sistema, aplicar `AddDays`/`AddMonths` sobre la hora de
      pared local, y reconvertir a UTC — en vez de operar directamente sobre UTC. Aplica tanto al avance
      real de tareas completadas (`RollRecurringTasks`) como a la expansión de ocurrencias fantasma del
      calendario (`CalendarAggregationService.cs:241-273`, que reutiliza la misma lógica). (análisis §6) —
      implementado con `TimeZoneInfo.ConvertTimeFromUtc` → aritmética local → `ConvertTimeToUtc`; ambos
      consumidores llaman a la misma función, así que quedan cubiertos a la vez. La firma gana un
      `TimeZoneInfo?` opcional (por defecto la zona local del sistema) para que los tests de DST sean
      deterministas en cualquier máquina; una hora local inexistente (hueco del salto de primavera) se
      interpreta como hora estándar, con un desvío acotado de ±1 h documentado en el XML-doc.
- [x] `ReminderService.PopDueReminders` (`services/ReminderService.cs:212-221`): añadir una guarda simple
      contra saltos de reloj hacia adelante no razonables (p. ej. si `now` está más de X minutos por delante
      del último `PopDueReminders` registrado, no disparar en bloque todo lo pendiente hasta esa fecha —
      esto es de menor prioridad que los dos puntos anteriores, puede diferirse si el esfuerzo no compensa
      frente al riesgo real). (análisis §6) — implementada: checkpoint en memoria (`_lastPopCheckUtc`,
      deliberadamente no persistido para que la pesca al vuelo tras días con la app cerrada siga
      funcionando) y umbral `MaxReasonableClockGap = 15 min` (el poller corre cada 30 s). Al detectar un
      salto se registra en `LogService.Warning`, se dispara solo lo que ya estaba vencido en la última
      comprobación y el resto queda armado — ni tormenta de avisos ni marcas de "disparado" con una hora de
      sistema no fiable; el ciclo siguiente (30 s después) retoma el comportamiento normal. El método gana
      un overload `internal PopDueReminders(DateTime nowUtc)` como seam de testeo (mismo patrón que el
      `storageDirectory` del constructor).

**Tests añadidos**: `ReminderServiceTests.Reschedule_AfterTrigger_ReArmsAndFiresAgainAtTheNewDate`
(dispara, reprograma a fecha futura y confirma que un segundo `PopDueReminders` en esa fecha vuelve a
dispararlo, verificando además que el rearme persiste en disco) más tres tests de la guarda de saltos
(salto hacia adelante sin disparo en bloque, disparo de lo genuinamente vencido antes del hueco, salto
hacia atrás sin guarda) y uno de pesca al vuelo sin checkpoint previo; `PolarSyncServiceTests`
(`ApplyReminderDto_WhenServerMovesTheDate_ReArmsIsTriggered` /
`ApplyReminderDto_WhenServerKeepsTheSameDate_PreservesIsTriggered`); y la suite DST de
`PolarTaskService.CalculateNextDueDate` sobre `Europe/Madrid` cubriendo los saltos de primavera y otoño de
2024 para `Daily`/`Weekly`/`Monthly`/`FirstDayMonth`, con dos casos explícitos que fijan que la hora UTC se
desplaza ±1 h mientras la hora de pared se mantiene. La teoría preexistente
`CalculateNextDueDate_AdvancesRelativeToThePreviousDueDate` (cuyas fechas de marzo→abril cruzan una
transición DST) pasa a fijar `TimeZoneInfo.Utc` para seguir verificando el avance relativo de forma
determinista. Suite completa verificada en verde (536 tests).

**Dependencias**: ninguna respecto a otras fases; puede ejecutarse en paralelo a la Fase 1.

---

## Fase 3 — Borrado seguro: papelera y confirmaciones consistentes (esfuerzo bajo-medio, impacto crítico/alto) ✅ completada

- [x] `KanbanViewModel.DeleteColumn` (`viewmodels/kanban/KanbanViewModel.cs:215-224`): mover las tarjetas
      de la columna a `TrashedCards` (mismo camino que `DeleteCard`) antes de eliminar la columna, en vez de
      perderlas. (top 10 #4, análisis §4) — el privado `DeleteColumn` mueve ahora cada tarjeta a
      `Board.TrashedCards` con su `ColumnId`/`ColumnTitle`/`CardIndex` antes de quitar la columna, y avisa
      vía `NotificationService` (`Kanban_Notify_ColumnDeleted_*`, severidad Warning) de que las tarjetas
      quedan recuperables en la Papelera.
- [x] Añadir un diálogo de confirmación al comando `DeleteColumn`, con el mismo componente de confirmación
      ya usado para "vaciar papelera" (`TrashViewModel.cs:221-282`). (análisis §4) — flujo
      `RequestDeleteColumn`/`CancelDeleteColumn`/`ConfirmDeleteColumn` con `ColumnPendingDelete`, calcado al
      de tarjetas (incluido respetar el ajuste "preguntar antes de borrar", que salta directo al borrado si
      está desactivado); modal propio en `KanbanView.axaml` con el recuento de tarjetas pluralizado
      (`PendingDeleteColumnMessage`, claves `Kanban_DeleteColumn_*`).
- [x] Separar visualmente/espacialmente el icono de "eliminar columna" del de "renombrar columna" en
      `views/kanban/KanbanView.axaml:135-140` (o añadir confirmación es suficiente mitigación; hacer ambas
      cosas si el esfuerzo lo permite). (análisis §4) — hechas ambas: el margen del botón de borrar pasa de
      2px a 12px (`Margin="12,0,0,0"`), además del diálogo de confirmación del punto anterior.
- [x] `TrashViewModel.RestoreTask` (`viewmodels/trash/TrashViewModel.cs:168-193`): reordenar la lógica para
      resolver la columna destino **antes** de quitar la tarjeta de `TrashedCards`; si no hay columna
      destino válida, dejar la tarjeta en la papelera y notificar el motivo en vez de perderla. (análisis
      §4) — implementado tal cual: sin columna destino la tarjeta permanece en `TrashedCards` y se notifica
      con `Trash_Notify_CardRestoreFailed_*` (Warning).
- [x] Unificar el criterio de confirmación: el borrado permanente individual (`DeleteTaskForeverCommand`,
      `DeleteForeverCommand`, `DeleteFileForeverCommand`) debe pedir confirmación igual que "vaciar
      papelera", o alternativamente ofrecer un `undo` con toast de unos segundos tras el borrado (cualquiera
      de las dos soluciones es válida; elegir la más consistente con el resto de la app). (análisis §4) —
      elegido el modal, por ser el patrón ya establecido para "vaciar papelera" y el borrado de tarjetas:
      los tres comandos pasan a ser `RequestDelete{,File,Task}Forever` (más `RequestDeleteSessionForever`
      para la nueva categoría), que abren un diálogo compartido (`PendingPermanentDelete`,
      `Trash_DeleteForeverModal_*`) resuelto por `ConfirmPermanentDelete`. Los recordatorios siguen sin
      borrado individual a propósito (sus tombstones `Dirty` deben sincronizar antes), como ya documentaba
      el código.
- [x] Añadir una categoría "Sesiones" a `TrashViewModel`/`TrashView.axaml`, listando el contenido de
      `<vault>/.trash/sessions/` (que `SessionService.Delete` ya genera) con opción de restaurar/purgar,
      igual que Notas/Tareas/Archivos/Recordatorios. (top 10 #10, análisis §4) — `SessionService` gana
      `ListTrashedSessions`/`RestoreTrashedSession`/`PermanentlyDeleteTrashedSession` (misma guarda de E/S
      y desduplicación por sufijo que el resto del servicio); la Papelera lista las sesiones con su
      recuento de notas (`TrashedSessionCardViewModel`, que reutiliza las claves `Sessions_NoteCount_*`),
      las restaura (avisando a `_onSessionsChanged` para que los selectores de Notas/Archivos la vuelvan a
      ofrecer al momento) y las purga individualmente o con `TrashEmptyTarget.Sessions`/"vaciar todo".
- [x] Añadir confirmación a `DeleteSessionCommand`
      (`viewmodels/sessions/SessionsViewModel.cs:290-302`). (análisis §4) — flujo
      `RequestDeleteSession`/`CancelDeleteSession`/`ConfirmDeleteSession` con `SessionPendingDelete` y modal
      en `SessionsView.axaml` (`Sessions_DeleteModal_*`) que muestra nombre y recuento de notas pluralizado;
      la sesión por defecto sigue excluida antes incluso de abrir el diálogo.
- [x] Definir y aplicar una política de purga automática para papeleras que hoy crecen sin límite: papelera
      de Kanban (`KanbanTrashedCard`) y `DismissedReminderService` — mismo criterio de expiración por
      antigüedad que ya usa `ReminderService.PurgeOldTombstones` (30 días) o el tope de 50 elementos de
      `NotificationService`, el que encaje mejor con cada caso. (análisis §4, §6) — papelera de Kanban:
      expiración por antigüedad (30 días sobre `KanbanTrashedCard.DeletedUtc`, purgada al arrancar en
      `KanbanService.PurgeOldTrashedCards`); descartes de recordatorios: tope de 50 entradas
      (`DismissedReminderService.MaxItems`, que además gana el `storageDirectory` opcional de rigor para
      tests) — encaja mejor que la antigüedad porque una entrada vieja puede seguir siendo la que suprime
      un recordatorio vigente, y el peor caso del recorte es que un descarte antiguo reaparezca en la
      bandeja, nunca que se pierda uno reciente.

**Tests añadidos**: `KanbanViewModelTests` — columna **con** tarjetas antes de borrarla, verificando que
aparecen en `TrashedCards` con columna e índice correctos, más el flujo de confirmación completo (request
abre diálogo sin tocar nada, cancel, confirm, `ConfirmBeforeDelete` desactivado borra directo, notificación
emitida); `TrashViewModelTests.RestoreTaskCommand_WithNoColumnsOnBoard_LeavesCardInTrash_AndNotifies` —
la tarjeta permanece recuperable con `Board.Columns` vacío — más la cobertura de la nueva categoría de
Sesiones (listar con recuento, restaurar conservando notas y disparando `onSessionsChanged`, purgar
individual y por categoría, `IsFullyEmpty`/`TotalCount` actualizados) y del nuevo modal de borrado
permanente individual; `SessionServiceTests` — `ListTrashedSessions`/`RestoreTrashedSession` (incluida la
desduplicación de carpeta y el retorno `false` con carpeta desaparecida)/`PermanentlyDeleteTrashedSession`;
`KanbanServiceTests.Constructor_PurgesTrashedCardsOlderThan30Days_ButKeepsRecentOnes` (con persistencia de
la purga); `DismissedReminderServiceTests` (roundtrip de `IsDismissed`, reemplazo por tarjeta y el tope de
50 con su persistencia); y `SessionsViewModelTests` con el flujo de confirmación de borrado de sesión
(request/cancel/confirm + guarda de la sesión por defecto). La clave `Trash_Notify_CardRestored_FallbackBoard`
quedó sin uso tras reordenar `RestoreTask` y se eliminó de los 7 `.resx`. Suite completa verificada en verde
(562 tests en `enki.Tests`, 6 en `enki.UITests`; paridad de claves i18n incluida).

**Dependencias**: ninguna respecto a otras fases.

---

## Fase 4 — Seguridad de credenciales y aislamiento de cuentas Polar (esfuerzo medio, impacto alto — seguridad) ✅ completada

- [x] Añadir un campo `UserId` (o equivalente) a las filas del store local de Polar
      (`PolarTask`/`PolarTaskList`/`PolarSubtask`, y `Reminder` para las sincronizadas) que se rellene al
      recibir/crear cada fila con la cuenta actualmente vinculada. (top 10 #5, análisis §5) — campo
      `UserId` local-only (nunca viaja por el wire) en los cuatro modelos; se estampa en el momento en que
      una fila pasa a `Dirty` por primera vez (creación, o el primer `Touch()` de una fila heredada sin
      propietario), no en el momento del push — de lo contrario el propio escenario de fuga (crear offline
      bajo A, desvincular antes de sincronizar, vincular B) seguiría reproduciéndose. `PolarTaskService`/
      `ReminderService` reciben un `Func<string?> currentUserIdProvider` opcional (por defecto "siempre
      null", para no romper los tests existentes que no les importa el aislamiento de cuentas); el
      compositor (`MainWindowViewModel.Composition.cs`) lo conecta a `PolarAuthService.UserId`. Editar una
      fila ya propiedad de otra cuenta nunca reasigna el propietario — queda `Dirty` indefinidamente hasta
      que la cuenta correcta se re-vincule, en vez de heredarla la sesión activa.
- [x] `PolarSyncService` (`PushTaskListsAsync`/`PushTasksAsync`/`PushSubtasksAsync`/`PushRemindersAsync`,
      `services/polar/PolarSyncService.cs:176-358`): filtrar las filas `Dirty` a enviar por
      `row.UserId == _auth.UserId` (o equivalente); las filas `Dirty` que pertenezcan a una cuenta distinta
      de la vinculada actualmente no deben empujarse jamás bajo la sesión activa. (análisis §5) — helper
      común `SelectOwnedDirty` usado por los cuatro `Push*Async`: selecciona `Dirty && (UserId is null ||
      UserId == userId)` y reclama (`UserId = userId`) las filas sin propietario justo antes de enviarlas.
      Las filas pulladas (`Pull*Async`, inserción o actualización) también se estampan con
      `_auth.UserId` — pertenecen por definición a la cuenta vinculada (RLS de Supabase solo deja ver las
      propias filas), lo que además sana en el momento cualquier fila local heredada de antes de este campo.
- [x] `PolarAuthService.Unlink()` (`services/supabase/PolarAuthService.cs:106-118`): decidir y documentar
      explícitamente el comportamiento al desvincular — opción recomendada: limpiar por completo el store
      local de Polar (`PolarTaskService.Store`) al desvincular, ya que sin cuenta vinculada esos datos no
      tienen ningún propietario válido; alternativa si se quiere conservar caché offline: marcar todas las
      filas como pertenecientes a la cuenta que se desvincula y bloquear su push si se vincula una cuenta
      distinta (cubierto por el punto anterior). (análisis §5) — opción elegida: limpieza completa
      (`PolarTaskService.ClearAll()`), deliberadamente **no** aplicada a Recordatorios (a diferencia de
      "Gestión de tareas" Polar, los recordatorios tienen un uso local legítimo sin cuenta vinculada — ver
      `Reminder.UserId`). Cableado desde el compositor, en la rama "se acaba de desvincular" del handler ya
      existente de `LinkStateChanged` (no dentro de `PolarAuthService` en sí, para no acoplar el servicio de
      identidad a un dominio de datos concreto); avisa al usuario vía `NotificationService` solo si había
      algo que limpiar.
- [x] `SettingsViewModel.ExportSettings` (`viewmodels/settings/SettingsViewModel.cs:1082-1104`): redactar
      `PolarAccessToken`/`PolarRefreshToken` (sustituir por `null`/cadena vacía) en el fichero exportado, o
      excluir esas claves del JSON exportado por completo. Añadir un aviso explícito en la UI de que el
      fichero exportado no incluye credenciales de sesión. (análisis §5) — `SettingsService.
      BuildRedactedExportJson()` construye una copia redactada (round-trip JSON, sin mutar la sesión en
      memoria) en vez de copiar el fichero tal cual; el mensaje de éxito ("Ajustes exportados a…") ahora
      aclara que la sesión no se incluyó, y un texto explícito (`Settings_General_DangerZone_ExportHint`)
      avisa de esto mismo antes de pulsar el botón, en Ajustes › General.
- [x] `VaultBackupService.CreateBackup` (`services/VaultBackupService.cs:48-52`): igual, redactar los
      tokens de `settings.json` antes de incluirlo en el `.zip` de backup. (análisis §5) —
      `TryAddRedactedSettingsFile` sustituye a `TryAddFile` específicamente para `settings.json`: deserializa,
      redacta los dos tokens y escribe el JSON redactado como entry del zip; un `settings.json` corrupto se
      cuenta como entrada fallida (mismo criterio que un fichero bloqueado) en vez de incluirse sin redactar.
- [x] `SupabaseConfig.Load` (`services/supabase/SupabaseConfig.cs:20-27`): validar que `SUPABASE_URL`
      empiece por `https://`; si no, tratar la configuración como no válida (`IsConfigured = false`) en vez
      de permitir una degradación silenciosa a texto plano en REST/Realtime. (análisis §5) — lógica extraída
      a `SupabaseConfig.NormalizeAndValidateHttpsUrl` (`internal`, pura, testeable sin depender del
      constructor estático); una URL sin esquema `https://` hace que `Url` quede `null` y por tanto
      `IsConfigured` en `false`.
- [x] Evaluar (no necesariamente implementar en esta fase si el esfuerzo es alto) mover
      `PolarAccessToken`/`PolarRefreshToken` fuera de `settings.json` hacia un almacén seguro del SO
      (DPAPI en Windows, Secret Service/libsecret en Linux vía `Avalonia`/interoperabilidad nativa, Keychain
      en macOS); si no es viable a corto plazo, documentar la decisión y su riesgo aceptado explícitamente
      en `docs/supabase-sync/`. (análisis §2, §5) — evaluado y **no implementado** esta fase: riesgo
      aceptado documentado en
      [decision-almacenamiento-credenciales-polar.md](decision-almacenamiento-credenciales-polar.md). Vive
      junto a este roadmap (en `docs/analisis-proyecto/`) en vez de en `docs/supabase-sync/`: esa carpeta no
      existe hoy en el árbol de trabajo (borrada sin commitear, ver Fase 8, "Higiene de documentación") y
      restaurarla no era parte del alcance de esta fase.

**Tests añadidos**: `PolarSyncServiceContractTests` — el escenario completo del hallazgo (fila `Dirty`
estampada con la cuenta A, se re-vincula como cuenta B, `SyncNowAsync()` no la empuja y no aparece en ningún
cuerpo de petición), más el caso contrario (misma cuenta sí empuja) y el de fila sin propietario (se reclama
y empuja) y el de pull (fila remota nueva se estampa con la cuenta vinculada); `PolarTaskServiceTests`/
`ReminderServiceTests` — estampado en creación, no reasignación al editar bajo una cuenta distinta, reclamo
de filas heredadas sin propietario, cascada de propietario en el borrado de subtareas, y `ClearAll`;
`SettingsServiceTests.BuildRedactedExportJson_*` — tokens ausentes del JSON exportado, resto de ajustes
intacto, sesión en memoria no mutada; `VaultBackupServiceTests.CreateBackup_RedactsPolarTokensInsideSettingsJson`;
`SupabaseConfigTests` — URL `https://` aceptada (con `/` final recortado), `http://` y sin esquema
rechazadas. Suite completa verificada en verde (585 tests en `enki.Tests`).

**Dependencias**: ninguna respecto a otras fases. Es la fase de mayor prioridad de seguridad; considerar
adelantarla si hay usuarios reales usando el vínculo Polar activamente antes de que las demás fases estén
listas.

---

## Fase 5 — Integridad de la red de enlaces (wikilinks) (esfuerzo medio, impacto alto) ✅ completada

- [x] Al renombrar una nota (`VaultService.RenameNote`, invocado desde
      `NotesViewModel.CommitRenameNote`/`EditorViewModel.OnTitleChanged`), recorrer las notas del vault y
      reescribir los `[[Título Viejo]]` que apunten a ella por el nuevo título/ruta — reutilizando el mismo
      mecanismo de detección de `EditorBacklinksService.FindBacklinks` para encontrar qué notas hay que
      tocar. (análisis §3, "Integridad de la red de enlaces") — `RenameNote` toma una foto de
      `GetAllNotesRecursiveWithContent` (contenido + metadatos) del alcance de la nota *antes* de mover el
      fichero, mueve el fichero, y luego reescribe (`RewriteWikilinksToRenamedNote`/`RewriteLinksTo`) cada
      ocurrencia `[[...]]` que resolvía al fichero antiguo, en cualquier nota de esa foto — incluida la
      propia nota renombrada si se autoenlazaba. La reescritura preserva `|alias` y `#ancla` tal cual
      (`ExtractLinkSuffix`) y usa el mismo `WriteFileAtomic` que el resto de la clase; un fallo al reescribir
      una nota concreta (bloqueada, permisos) se registra y se salta en vez de abortar el renombrado que ya
      tuvo éxito — un enlace obsoleto en una nota es recuperable, un renombrado a medias no.
- [x] Unificar el criterio de resolución de wikilinks entre `EditorBacklinksService.FindBacklinks` (hoy
      busca por título) y `WikiLinkParser.ResolveTarget` (hoy prioriza nombre de fichero): que ambos usen
      exactamente la misma estrategia de resolución (recomendado: nombre de fichero primero, título como
      fallback si es inequívoco, igual que `WikiLinkParser` ya hace) para que el panel de Backlinks y el
      Ctrl+click nunca diverjan. (análisis §3) — `WikiLinkParser.ResolveTargetWithStatus` es ahora la única
      implementación de la estrategia (nombre de fichero primero, título como respaldo si es inequívoco);
      `ResolveTarget` pasa a ser un envoltorio sobre ella. `FindBacklinks` cambia de firma (recibe la
      `NoteFile` objetivo, no un `string targetTitle` suelto) y usa `WikiLinkParser.FindOccurrences` +
      `ResolveTarget` para decidir si un enlace apunta a la nota objetivo, en vez de una búsqueda de texto
      independiente — mismo camino que sigue el Ctrl+click del editor (`EditorViewModel.OpenWikiLink`) y la
      nueva coloración en vivo (siguiente punto). `VaultService.ResolveWikiLinkScopeRoot` (antes duplicado
      como método privado en `EditorViewModel`) pasa a ser el único sitio que decide el alcance (sesión vs.
      vault completo) que usan el renombrado, el Ctrl+click y el aviso de borrado.
- [x] Distinguir visualmente en el editor un wikilink "roto" (target no existe) de uno "ambiguo" (dos notas
      comparten título) — por ejemplo con un tooltip o estilo distinto al pasar el ratón — en vez de que
      ambos casos se vean igual de "no hacen nada" al hacer Ctrl+click. (análisis §3) — estilo distinto:
      `MarkdownLivePreviewColorizer` resuelve cada wikilink contra `EditorViewModel.WikiLinkScopeNotes` (el
      mismo conjunto de notas que usaría un Ctrl+click) y pinta uno resuelto en el acento habitual, uno roto
      en `DangerBrush` (ya existía en `MarkdownStyle`, sin usar hasta ahora) y uno ambiguo en `DangerBrush` +
      cursiva — mismo color de "esto necesita tu atención" para ambos casos, pero sin verse idénticos. El
      colorizador se comparte entre pestañas, así que `EditorView` le reapunta el proveedor de notas
      (`WikiLinkNotesProvider`) a la pestaña activa en cada cambio de pestaña en vez de crear una instancia
      por editor.
- [x] Antes de mover una nota a la papelera (`NotesViewModel.DeleteNote`/`EditorViewModel.Delete`),
      consultar `EditorBacklinksService.FindBacklinks` y, si hay resultados, avisar al usuario de cuántas
      notas quedarán con un enlace roto (sin bloquear el borrado, solo informar). (análisis §3) —
      `EditorBacklinksService.WarnIfDeletingLeavesBrokenLinks` (nuevo helper compartido por ambos
      ViewModels, evitando duplicar la lógica) empuja una notificación `Warning` con el recuento pluralizado
      antes del borrado si hay backlinks, y no hace nada si no las hay o si no hay
      `NotificationService` (tests).
- [x] `VaultService.RenameNote`/`RenameFile`/`RenameFolder`: cuando el nombre destino ya existe, no hacer
      un no-op silencioso — o bien añadir un sufijo numérico automático (como ya hace `CreateNote` en
      colisión) y notificar el nombre final usado, o bien rechazar la operación con un mensaje explícito en
      la UI. Cualquiera de las dos opciones es válida siempre que dejen de existir renombrados "fantasma"
      donde el `Title` del ViewModel cambia pero el fichero no. (análisis §3) — elegido el sufijo numérico
      (`ResolveCollision`, mismo criterio que `MoveWithUniqueSuffix`), en las tres operaciones. Los
      llamadores (`NotesViewModel.CommitRenameNote`, `EditorViewModel.OnTitleChanged`) comparan el nombre
      final devuelto contra lo que el usuario pidió y, si difieren, notifican (`Info`) el nombre real usado;
      `EditorViewModel` además corrige `Title` al valor real en vez de dejarlo divergir del fichero.
- [x] `NoteTagHelper.SetTags` (`services/NoteTagHelper.cs:69-70`): cuando `cleanTags.Count == 0`, no borrar
      el frontmatter completo — eliminar solo la clave `tags` y conservar el resto de claves existentes
      (`aliases`, `created`, etc.), reescribiendo el bloque `---...---` sin esa clave en vez de con
      `StripFrontMatter`. (análisis §3) — `RemoveTagsKey`/`RemoveTagsKeyLines` quitan solo la línea `tags:`
      (y, si la etiqueta estaba en forma de lista YAML, las líneas `- item` que la seguían, para no dejar
      líneas huérfanas) y solo recurren a `StripFrontMatter` completo cuando no queda ninguna otra clave. La
      rama de reemplazo (`cleanTags.Count > 0` sobre una clave ya existente) reutiliza el mismo mecanismo, lo
      que de paso corrige que actualizar unas etiquetas en forma de lista a forma inline dejaba las viejas
      líneas `- item` colgando.
- [x] Revisar las comparaciones de ruta `StringComparison.OrdinalIgnoreCase` en `NotesViewModel`/
      `VaultService` (apertura de pestañas, cierre, identidad de nota) y usarlas solo quando el SO es
      case-insensitive (Windows/macOS por defecto); en Linux, comparar con `StringComparison.Ordinal` para
      no fusionar `Todo.md`/`todo.md` como si fueran la misma nota. (análisis §3) — `VaultService.
      PathComparison`/`PathsEqual` centralizan el criterio (`OrdinalIgnoreCase` en Windows/macOS,
      `Ordinal` en Linux) y sustituyen las comparaciones de identidad de ruta en ambos ficheros (más
      `EditorBacklinksService`, tocado por el mismo cambio de firma del punto 2). Las comparaciones que no
      son de identidad de ruta (orden alfabético de carpetas, filtro de búsqueda por texto, extensión
      `.md`) se dejan con `OrdinalIgnoreCase` a propósito, sin cambios.
- [x] `KanbanCardViewModel.LinkedNoteLabel`: distinguir visualmente (icono/color) un enlace nota↔tarjeta
      roto (nota ya no existe) del resto, en vez de que solo se descubra al hacer clic. (análisis §4) —
      `IsLinkedNoteMissing` (un `File.Exists` directo, sin inyectar `VaultService` en la tarjeta, a
      propósito según el comentario ya existente de la clase), `LinkedNoteIcon` (`NoteAlertOutline` en vez
      de `NoteTextOutline`) y `LinkedNoteTooltip` (reutiliza el título "Linked note not found" que ya existe
      para el aviso de clic fallido, en vez de añadir una cadena nueva para el mismo hecho) — mirrorizados
      en `KanbanCardDetailViewModel` y consumidos en el popover de detalle de tarjeta de
      `KanbanView.axaml`. Sin cambio de color (el tablero reserva el único color real a "vencida", según
      `docs/sistema-diseno-web.md` §1.3).

**Tests añadidos**: `VaultServiceTests` — colisión de nombre con sufijo numérico en `RenameNote`/
`RenameFolder`/`RenameFile`; reescritura de wikilinks al renombrar (por nombre de fichero, preservando
alias/ancla, sin tocar enlaces a otras notas, autoenlace incluido); `ResolveWikiLinkScopeRoot` dentro/fuera
de una sesión. `WikiLinkParserTests` — `ResolveTargetWithStatus` distingue `Broken`/`Ambiguous`/`Resolved`;
`FindOccurrences` devuelve posición y texto crudo de cada enlace. `EditorBacklinksServiceTests` reescrito
sobre la nueva firma basada en `NoteFile` — incluye el caso "título ≠ nombre de fichero" que confirma que
resuelve igual que `WikiLinkParser.ResolveTarget`, el caso de título ambiguo (no debe emparejar) y
`WarnIfDeletingLeavesBrokenLinks` sin `NotificationService`. `NoteTagHelperTests` — clave ajena a `tags`
sobrevive a vaciar las etiquetas, tanto en forma inline como en forma de lista YAML, sin líneas huérfanas.
`EditorViewModelWikiLinkTests`/`NotesViewModelWikiLinkTests` (nuevos) — aviso al borrar una nota con
backlinks (y ausencia de aviso sin ellos), notificación del nombre final tras una colisión de renombrado
(con `Title` corregido en el editor), y `OpenWikiLink` resolviendo contra el alcance correcto.
`KanbanCardViewModelTests` — `IsLinkedNoteMissing`/`LinkedNoteIcon`/`LinkedNoteTooltip` con el fichero
presente, ausente, y sin nota vinculada. Suite completa verificada en verde (614 tests en `enki.Tests`, 6 en
`enki.UITests`).

**Verificación manual pendiente**: la distinción visual de wikilinks (punto 3) y el icono de enlace roto en
Kanban (punto 8) se han verificado por tests headless (resolución de estado, propiedades del ViewModel) y
por inspección de código de la plantilla `.axaml`, pero no se han visto renderizados en una ejecución gráfica
real de la app durante esta sesión — pendiente de una pasada visual manual antes de dar la fase por cerrada
en producción.

**Dependencias**: ninguna respecto a otras fases, aunque conviene hacerla después de la Fase 1 (atomicidad
de escritura) porque la reescritura masiva de wikilinks al renombrar toca muchos ficheros a la vez y se
beneficia de que cada escritura individual ya sea segura.

---

## Fase 6 — Rendimiento del editor y del vault (esfuerzo medio, impacto alto en vaults grandes) ✅ completada

- [x] Añadir debounce (mismo mecanismo de 600ms que ya usa el autoguardado) a
      `EditorViewModel.OnTextChanged` antes de invocar `Backlinks.Refresh()`, en vez de recalcular en cada
      pulsación de tecla. (top 10 #6, análisis §3) — implementado reutilizando el mismo `_saveTimer` del
      autoguardado en vez de añadir un segundo temporizador paralelo: su `Tick` ahora guarda (si procede) y
      refresca `Backlinks` en el mismo disparo, y `OnTextChanged` ya no llama a `Backlinks.Refresh()`
      directamente. `FlushPendingSave()` (cerrar pestaña, cerrar la app) también refresca los backlinks
      pendientes, para que cambiar de pestaña justo después de teclear no deje el panel con datos obsoletos
      hasta la siguiente tecla.
- [x] Evitar el doble barrido completo del vault en `Backlinks.Refresh()`: introducir una caché en memoria
      de metadatos de nota (título, tags, preview) que `VaultService.GetAllNotesRecursive` y
      `EditorBacklinksService.FindBacklinks` puedan compartir en vez de leer cada `.md` dos veces por
      refresco; invalidar la caché por nota al guardarla (`WriteContent`) en vez de releer todo el vault.
      (análisis §3) — `VaultNoteCache` (nuevo, `services/VaultNoteCache.cs`): caché en memoria por ruta,
      con la frescura validada contra `FileInfo.LastWriteTimeUtc` (obtenido gratis de la propia
      enumeración de directorio vía `DirectoryInfo.EnumerateFiles`, sin `stat` extra por fichero) para que
      una edición externa al proceso (otro editor, otra ventana de Enki) siga viéndose en el siguiente
      escaneo. Cada escritura de la propia app (`WriteContent`/`CreateNote`/`DeleteNote`/`RestoreNote`/
      `PermanentlyDelete`/`RenameNote`/`MoveNote`) actualiza o invalida la entrada directamente en vez de
      depender de esa granularidad de mtime. `EditorBacklinksService.FindBacklinks` cambia de firma para
      recibir notas-con-contenido ya leídas (`VaultService.GetAllNotesRecursiveWithContent`, ahora servida
      por la misma caché) en vez de releer cada fichero por su cuenta — el "doble barrido" que el análisis
      señalaba. **Bug real encontrado y corregido durante la implementación**: la caché, al devolver la
      misma instancia de `NoteFile` en cada lectura, permitía que una mutación en un sitio (`RenameNote`
      reasigna `note.FullPath` directamente) corrompiera silenciosamente una foto tomada momentos antes por
      otro llamador que compartía la misma entrada de caché — reproducido por
      `RenameNote_RewritesWikilinksInOtherNotes_ByFileName` fallando tras el primer borrador de este cambio.
      Corregido con `NoteFile.Clone()`: la caché siempre devuelve una copia independiente (con su propia
      lista de `Tags`), nunca la instancia que tiene guardada.
- [x] Aplicar el mismo debounce a `EditorOutlineViewModel`/`EditorStatsViewModel` en su suscripción a
      `document.TextChanged`. (análisis §3) — cada uno con su propio `DispatcherTimer` (no comparten
      instancia con `EditorViewModel`, que no les pasa ninguna referencia), pero con el mismo retraso vía la
      constante compartida `EditorDebounce.Delay` (nueva, `services/editor/EditorDebounce.cs`) en vez de
      repetir el literal de 600ms en cuatro sitios. `Outline.Refresh()`/`Stats.Refresh()` siguen siendo
      públicos y se llaman de forma inmediata (sin pasar por el debounce) donde ya se hacía antes de un
      renombrado (`EditorViewModel.OnTitleChanged`/`SyncRenamedNote`).
- [x] Reutilizar la caché de metadatos del primer punto en `GlobalSearchViewModel.SearchNotes`,
      `QuickSwitcherViewModel.RefreshResults` y `NotesViewModel.Reload`, en vez de que cada uno vuelva a leer
      y parsear el vault por su cuenta. (análisis §3) — sin tocar esos tres ficheros: al vivir la caché
      dentro de `VaultService` mismo (una única instancia, compartida por todo el compositor de la shell),
      los tres ya se benefician automáticamente en cuanto llaman a `GetAllNotesRecursive`/`GetNotes`/
      `GetNote`, que ahora pasan por `VaultNoteCache` internamente.
- [x] Hacer `VaultService.GetVaultSizeBytes` asíncrono (o cachear su resultado con invalidación por cambio,
      dado que el Dashboard no necesita el dato al segundo) para no bloquear el hilo de UI con vaults
      grandes. (análisis §3) — opción de caché elegida: `GetVaultSizeBytes()` devuelve el último valor en
      caché de forma síncrona (0 la primera vez, o justo tras una escritura que invalida la caché) y
      dispara en segundo plano `RefreshVaultSizeBytesAsync()` si hace falta; el recorrido real del vault
      corre siempre en un hilo de la pool. `VaultSizeChanged` avisa cuando el recálculo aterriza;
      `DashboardViewModel`/`SettingsViewModel` se suscriben y reenvían la actualización al hilo de UI vía
      `Dispatcher.UIThread.Post` (mismo patrón que ya usaban `FileCardViewModel`/`PdfDocumentViewModel` para
      miniaturas calculadas en segundo plano). Cada operación de escritura que cambia bytes en disco
      (`WriteContent`, `CreateNote`, `ImportFile`, `RenameNote`, borrado permanente de nota/archivo, ...)
      invalida la caché.

**Tests añadidos**: `VaultServiceTests` — caché de notas (misma escritura sin recorrer el vault dos veces,
instancias independientes por lectura para no repetir el bug de aliasing, `ReadContent`/`GetNote` reflejando
una escritura reciente en vez de una copia obsoleta) y caché de tamaño de vault (`GetVaultSizeBytes` antes/
después de `RefreshVaultSizeBytesAsync`, 0 antes de la primera resolución, invalidación tras una escritura,
evento `VaultSizeChanged`), más un test de regresión de rendimiento con un vault sintético de 1500 notas que
compara un escaneo en frío contra uno servido por caché (umbral generoso para evitar flakiness en CI).
`EditorBacklinksServiceTests` adaptado a la nueva firma basada en notas-con-contenido.
`EditorViewModelBacklinksDebounceTests` (nuevo) — un cambio de texto no refresca los backlinks de inmediato,
`FlushPendingSave` sí vuelca el refresco pendiente. `EditorOutlineAndStatsDebounceTests` (nuevo) — mismo
criterio para `EditorOutlineViewModel`/`EditorStatsViewModel`, confirmando además que `Refresh()` sigue
siendo inmediato cuando se llama directamente. Suite completa verificada en verde (629 tests en
`enki.Tests`, 6 en `enki.UITests`).

**Dependencias**: ninguna estricta, pero tiene más sentido después de la Fase 5 (los cambios de resolución
de wikilinks tocan el mismo código de `EditorBacklinksService` que aquí se optimiza — evita tener que
tocarlo dos veces).

---

## Fase 7 — Accesibilidad (esfuerzo medio-alto, impacto alto — inclusión) ✅ completada

- [x] Auditar las 17 vistas `.axaml` y añadir `AutomationProperties.Name` a todo control interactivo sin
      texto visible propio (botones icon-only, toggles, popovers) — priorizar por uso: navegación del shell
      (`MainWindow.axaml`), acciones de Kanban (crear/borrar/mover columna y tarjeta), Papelera, Calendario,
      Ajustes. (top 10 #9, análisis §7) — las 17 vistas cubiertas (`MainWindow`, `SettingsView`,
      `CalendarView`, `KanbanView`, `EditorView`, `NotesView`, `FilesView`, `DashboardView`, `SkillsView`,
      `SkillEditorView`, `SessionsView`, `TrashView`, `RemindersView`, `NotificationsView`, `TagFilterView`,
      `LanguageSetupView`, `VaultSetupView`); patrón consistente: mismo texto que el `ToolTip.Tip` existente
      cuando lo hay, `{Binding Título}` en tarjetas/celdas sin tooltip (nota, tarjeta Kanban, sesión, celda
      de calendario vía `{Binding Date, StringFormat={}{0:D}}`), y el mismo criterio que ya usaba
      `SettingsView` para asociar un `ToggleSwitch` a la etiqueta de texto de su fila
      (`AutomationProperties.Name` con la misma clave que el `TextBlock` adyacente). 11 claves nuevas
      (`A11y_Files_*`, `A11y_Notes_*`, `A11y_Kanban_ListPanelSplitter`, `A11y_Reminders_Notify{Hour,Minute}Field`)
      más `Kanban_Field_ColumnLabel` (ver punto siguiente), traducidas en los 7 `.resx` con paridad de claves
      verificada (1043 claves, sin diferencias entre idiomas).
- [x] Revisar el orden de foco (`TabIndex`) en los layouts más complejos — tablero Kanban (columnas), grid
      de Calendario, secciones de Ajustes — donde el orden de árbol visual por defecto puede no coincidir
      con el orden lógico de uso. (análisis §7) — revisado sin necesidad de `TabIndex` explícito en ninguno
      de los tres: el tablero Kanban tabula columna a columna de izquierda a derecha y, dentro de cada
      columna, tarjeta a tarjeta en el mismo orden que `KanbanColumnViewModel.Cards` (orden de declaración
      = orden visual); el grid de Mes y el mini-calendario recorren `Days`/`MiniCalendarDays` en orden
      cronológico estricto dentro de un `UniformGrid` de fila principal, así que el orden de documento ya
      coincide con la lectura fila a fila; las secciones de Ajustes son pares `Grid ColumnDefinitions="*,Auto"`
      donde la Columna 0 (etiqueta/control principal) siempre precede a la Columna 1 (el control secundario,
      p. ej. un `ToggleSwitch`), y las secciones se apilan de arriba a abajo en un único `ScrollViewer` en
      el mismo orden de declaración. No se encontró ningún caso de árbol visual divergiendo del orden lógico.
- [x] Añadir una vía de mover una tarjeta Kanban entre columnas sin drag&drop — por ejemplo un selector de
      columna destino en `KanbanCardEditorViewModel`/el formulario de edición de tarjeta, o un menú
      contextual con "Mover a...". Resuelve a la vez el hallazgo de accesibilidad motora y el de "no hay
      forma de mover una tarjeta si el arrastre falla". (análisis §4, §7) — elegido el selector: nuevo
      `KanbanCardEditorViewModel.AvailableColumns`/`SelectedColumn` (arranca en la columna actual, no mueve
      nada hasta Guardar, mismo contrato que el resto del formulario) con un `ComboBox` nuevo
      (`Kanban_Field_ColumnLabel`) justo debajo del título en `KanbanView.axaml`.
      `KanbanViewModel.SaveCardEditor` reutiliza el `MoveCard` ya existente (el mismo que usan los
      manejadores de drag&drop) cuando `SelectedColumn` difiere de la columna original, así que la
      automatización de subtareas (`ApplySubtaskAutomation`) y la persistencia son idénticas por cualquiera
      de las dos vías.
- [x] Extender `KeyBinding` a las vistas que hoy solo dependen de los atajos globales del shell — como
      mínimo, un atajo para "nueva tarjeta"/"nueva columna" en Kanban, "nuevo recordatorio" en Recordatorios,
      "vaciar papelera" en Papelera. (análisis §7) — ya resuelto de una pasada anterior: Kanban tiene
      `N`/`C` para tarjeta/columna nueva (`KanbanView.axaml.cs`, `KanbanView_KeyDown`), Recordatorios tiene
      `Ctrl+N` (`OpenCreateEditorCommand`) y Papelera `Ctrl+Shift+E` (`RequestEmptyTrashCommand`), ambos en
      un bloque `<UserControl.KeyBindings>` — verificado, sin cambios necesarios.
- [x] Pasada manual de verificación con un lector de pantalla real (Orca en Linux, o NVDA si se prueba en
      Windows) sobre los flujos principales: crear/abrir una nota, crear una tarjeta Kanban, crear un
      recordatorio, navegar el menú de Ajustes — para confirmar que los nombres accesibles añadidos son
      realmente útiles y no solo presentes.

**Verificación manual pendiente**: el punto anterior (pasada con lector de pantalla real) no se ha podido
ejecutar en esta sesión — requiere un entorno gráfico interactivo con Orca/NVDA, no disponible aquí. El resto
de la fase está implementado y verificado por tests/inspección de código; esta pasada queda como el único
paso manual real antes de dar la fase por cerrada en producción (mismo criterio que la "Verificación manual
pendiente" ya documentada al cierre de la Fase 5).

**Tests añadidos**: `KanbanViewModelTests.SaveCardEditor_WithDifferentSelectedColumn_MovesCardLikeDragAndDrop`/
`SaveCardEditor_WithUnchangedSelectedColumn_LeavesCardInPlace` cubren el nuevo selector de columna del editor
de tarjeta (mueve igual que el drag&drop; no toca nada si no cambia la selección). Los tests de UI existentes
(`enki.UITests`) pueden extenderse más adelante para afirmar la presencia de `AutomationProperties.Name` en
los controles clave tocados por esta fase, como red de regresión barata frente a que vuelvan a faltar en
vistas nuevas — no añadido en esta pasada. Suite completa verificada en verde (631 tests en `enki.Tests`).

**Dependencias**: ninguna respecto a otras fases; es independiente y puede ejecutarse en paralelo a
cualquiera de las anteriores.

---

## Fase 8 — Higiene de documentación y operaciones de release (esfuerzo bajo, impacto medio) ✅ completada

- [x] Actualizar `docs/agent-docs/internacionalizacion-i18n.md` para reflejar que la i18n ya está
      implementada — reescribirlo como documento de referencia del sistema actual en vez de plan pendiente,
      o marcarlo explícitamente como "plan histórico, ya completado" al principio del fichero. (análisis
      §7) — elegida la segunda opción (banner al principio del fichero en vez de reescritura completa de
      693 líneas): cifras reales verificadas contra los `.resx` (6 idiomas + neutral = 7 ficheros, **1028
      claves por idioma**, no las 962 que citaba el roadmap original — la implementación creció desde que
      se escribió esa cifra). §8 ("Proceso para añadir una clave nueva") se deja como la única sección
      todavía genuinamente prescriptiva; el resto queda como referencia arquitectónica del sistema ya
      construido.
- [x] Revisar y corregir los enlaces rotos detectados. (análisis §7) — `docs/calendar-feature/`,
      `docs/supabase-sync/`, `docs/notificaciones-y-recordatorios.md` y
      `docs/agent-docs/sigma-js-graph-migration.md` **sí estaban recuperables** (`git checkout HEAD --
      <ruta>`): no habían desaparecido del historial, solo del árbol de trabajo sin commitear — restaurados
      tal cual. Para los documentos que nunca llegaron a existir (`docs/ajustes-docs/01-04-*.md`,
      `docs/seccion-archivos.md`, `docs/funcionalidad-zoom-interfaz.md`,
      `docs/ia-skills/especificacion-desarrollo.md`) se escribieron de cero como documentos de referencia
      del estado actual, verificados contra el código citado en cada caso (no contra suposiciones) — ver
      esos ficheros para el detalle de cada uno, incluidas las inconsistencias de comportamiento que salieron
      a la luz al escribirlos (p. ej. `AutoBackupIntervalDays`/`RetentionCount` sin control en la UI de
      Ajustes, pendiente para la Fase 10). `docs/analisis-mejoras-funcionalidades.md` — citado ~25 veces con
      referencias "Fase N §M" a un documento que tampoco existe en el historial de git — se trató distinto:
      reconstruirlo a partir de fragmentos de comentario habría sido inventar contenido sin poder
      verificarlo, así que se limpiaron las 25 citas conservando el texto explicativo que ya llevaba cada
      comentario (nunca se perdió información real, solo el puntero muerto). Un mismo barrido encontró
      **dos referencias adicionales que el propio roadmap no había detectado**
      (`docs/analisis-proyecto/03-problemas-y-deuda-tecnica.md` y `04-oportunidades-de-integracion.md`,
      restos de la serie de documentos numerados que este mismo roadmap sustituyó) — sus ~19 citas se
      redirigieron a la Fase 5 de este roadmap donde el contenido aplica, o se limpiaron igual que las
      anteriores donde no había un destino real. Verificado con un barrido final
      (`grep -rohE 'docs/[A-Za-z0-9_./-]+\.md'` sobre `enki/src`, `README.md`, `AGENTS.md` contra los
      ficheros reales en disco) sin ningún resultado pendiente.
- [x] Corregir `views/settings/SettingsView.axaml`: quitar el `CornerRadius="3"` de la barra de zoom de
      interfaz para cumplir la regla de "cero excepciones, `CornerRadius=0`" de `docs/sistema-diseno-web.md`.
      (análisis §7) — las líneas habían cambiado de número desde el análisis original (accesibilidad de la
      Fase 7 tocó el fichero); localizadas por contenido (`grep 'CornerRadius="3"'`) en vez de por línea fija.
- [x] Añadir un fichero `LICENSE` en la raíz del repo. (análisis §7) — decisión del propietario del
      proyecto: código con todos los derechos reservados (sin licencia pública de uso/redistribución).
- [x] Añadir un pipeline de CI mínimo (GitHub Actions) que ejecute `dotnet build` y `dotnet test` en cada
      push/PR. (análisis §2) — `.github/workflows/ci.yml`: compila y testea los tres proyectos (`enki`,
      `enki.Tests`, `enki.UITests`) por separado (no hay `.sln` en el repo). Instala explícitamente el SDK
      `8.0.x` además del que fije `global.json`, porque `enki.csproj` apunta a `net8.0` y el test host de
      `dotnet test` no siempre respeta el `RollForward` de la app — sin el runtime 8.0.x instalado falla al
      lanzar aunque la compilación tenga éxito con un SDK más nuevo (reproducido en esta misma sesión, ver
      nota en `README.md`, "Cómo compilar y correr").
- [x] Activar `TreatWarningsAsErrors` (o al menos `EnableNETAnalyzers`+`AnalysisLevel` con un umbral
      razonable) en `Directory.Build.props`, una vez el CI anterior esté en verde con el código actual.
      (análisis §2) — verificado en local que las tres compilaciones daban 0 warnings antes de activar
      `TreatWarningsAsErrors`+`EnableNETAnalyzers`; suite completa (631 tests) sigue en verde tras el
      cambio. `AnalysisLevel` se deja deliberadamente en el valor por defecto del SDK: subirlo a
      `latest-recommended` (probado en esta sesión) hace aparecer 115 errores nuevos de golpe, casi todos
      CA1305 de formateo sensible a cultura repartidos por buena parte de los ViewModels — corregirlos bien
      exige decidir caso a caso `CurrentCulture` vs `InvariantCulture` coherente con el sistema de i18n ya
      implementado, no un `IFormatProvider` genérico añadido a ciegas; se deja documentado en
      `Directory.Build.props` y movido a la Fase 10 como su propia pasada dedicada en vez de colarse aquí
      como efecto secundario.
- [x] Rellenar `Directory.Build.props` (`Company`, `Copyright`, `Version`) antes de cualquier build de
      release real, y empezar un `CHANGELOG.md` a partir de esa primera versión etiquetada. (análisis §2) —
      `Company`/`Copyright` acordes a la decisión de licencia de arriba; primera versión real `0.1.0` (antes
      el SDK generaba `1.0.0.0` por defecto sin que nadie lo hubiera decidido — no es un "1.0" porque la app
      todavía no ha tenido release pública). `CHANGELOG.md` en la raíz, formato Keep a Changelog + SemVer,
      con la entrada `0.1.0` resumiendo el cierre de las Fases 0-8.
- [x] Evaluar y documentar (aunque sea en un primer borrador) el camino de empaquetado para usuarios no
      técnicos — como mínimo, instrucciones de `dotnet publish` self-contained por plataforma; a más largo
      plazo, instalador nativo por SO. (análisis §7) —
      `docs/agent-docs/empaquetado-y-distribucion.md`: comando base con RIDs por plataforma, verificado
      ejecutándolo de verdad contra este repo (`linux-x64`, publish self-contained + single-file exitoso,
      ~104 MB, librerías nativas de Avalonia/PDFium que quedan sueltas junto al ejecutable en vez de
      empotradas), justificación de por qué `PublishTrimmed` se deja fuera (serialización JSON sin
      source-gen sobre varios modelos, riesgo real de romper persistencia en runtime sin avisar en
      compilación), y el camino de instaladores nativos (MSIX/Inno Setup, `.deb`/AppImage, `.app`/`.dmg`)
      documentado como siguiente paso explícito, no implementado en esta fase. De paso detectada la
      ausencia de un icono de app real (ningún `.ico`/`.icns` en el repo) — anotada como hueco pendiente
      antes de cualquier instalador con buena pinta.

**Tests**: fase de higiene de documentación/build, sin cobertura de tests propia — verificación por
compilación limpia (0 warnings en los tres proyectos, `TreatWarningsAsErrors` activo) y suite completa en
verde (631 tests en `enki.Tests`) tras cada cambio de `Directory.Build.props`.

**Dependencias**: el CI depende de que las fases 1-7 no dejaran tests rotos (verificado: no los dejaron).
`TreatWarningsAsErrors` depende de la compilación en 0 warnings del CI anterior.

---

## Fase 9 (opcional) — Robustez de sincronización Polar y notificaciones (esfuerzo medio) ✅ completada

Ninguno de estos hallazgos compromete la integridad de los datos (a diferencia de la Fase 4); son mejoras
de robustez y de feedback al usuario que se pueden diferir sin riesgo.

- [x] `PolarRealtimeService`: reconectar automáticamente si el socket se cae tras una conexión exitosa (hoy
      solo reconecta en el siguiente `LinkStateChanged` o reinicio de la app), con el mismo backoff que ya
      usa el timer de sync. (análisis §5) — nueva `PolarRealtimeService.IsConnected` (basada en `_client`)
      pasa a `false` en cuanto el socket se cierra o falla por su cuenta, no solo tras un `Disconnect()`
      explícito: `Client.AddStateChangedHandler` detecta `SocketState.Close`/`Error` (marshallado al hilo de
      UI vía `Dispatcher.UIThread.Post`, porque el callback llega desde el hilo propio del socket) y limpia
      `_client`. En vez de un segundo temporizador de backoff en paralelo, el mismo tick de
      `MainWindowViewModel._polarSyncTimer` que ya reintenta push/pull con backoff exponencial ahora también
      llama a `PolarRealtimeService.ConnectAsync()` cuando `IsLinked && !IsConnected` — reutiliza literalmente
      el mismo intervalo (25 s base, hasta 5 min mientras el dispositivo esté offline) en vez de duplicar la
      lógica de backoff.
- [x] `PolarTaskManagementViewModel.SyncNow` (botón manual): distinguir en el estado expuesto a la UI entre
      "sync completado" y "descartado porque ya había uno en curso" (el `SemaphoreSlim.WaitAsync(0, ct)` de
      `PolarSyncService.SyncNowAsync` hoy no informa cuál de los dos ocurrió). (análisis §5) —
      `PolarSyncService.SyncNowAsync` devuelve ahora `PolarSyncOutcome` (`Completed`/`SkippedAlreadyRunning`/
      `NotLinked`) en vez de `void`. Los tres botones manuales de "Sync now" (Recordatorios, Gestión de
      tareas, Ajustes › Cuenta) ganan un `LastSyncNowWasSkipped` que se activa solo cuando el intento propio
      fue descartado por otro sync ya en curso (temporizador de fondo, debounce de realtime, o el botón de
      otra de las tres vistas), y se limpia en cuanto cualquier ciclo de sync termina de verdad
      (`SyncCompleted`) — nunca queda pegado tras resolverse la causa. Aviso breve junto al botón
      (`Polar_Sync_AlreadyRunningNotice`), mismo patrón visual que `NextRetryLabel`.
- [x] Re-notificar periódicamente (no solo en la transición inicial) mientras `PolarSyncService` siga en
      estado de fallo — por ejemplo, un recordatorio cada hora mientras el problema persista, en vez de un
      único aviso que se puede descartar y olvidar. (análisis §5) — `MainWindowViewModel` guarda
      `_lastPolarSyncFailureNotifiedUtc` y vuelve a empujar la notificación de fallo cada
      `PolarSyncFailureRenotifyInterval` (1 hora) mientras `LastError` siga sin resolverse, además de en la
      transición éxito→fallo original que ya existía; se resetea en cuanto un ciclo vuelve a tener éxito.
- [x] Avisar al usuario (vía `NotificationService`, consistente con el resto del módulo) cuando una edición
      local pierde un conflicto LWW frente a una versión remota más reciente — hoy el cambio local se
      sobrescribe sin ningún rastro visible. (análisis §5) — nuevo evento
      `PolarSyncService.LocalEditsOverwritten`, disparado una sola vez por ciclo de sync (nunca por fila,
      mismo criterio de agregación que `KanbanViewModel.DeleteColumn` con el recuento de tarjetas) con el
      título de cada fila cuyo `Dirty` se descartó porque el servidor tenía una versión más reciente —
      capturado en las 4 rutas de push (listas/tareas/subtareas/recordatorios) justo antes de aplicar el DTO
      remoto que lo sobrescribe, para guardar lo que el usuario había escrito realmente y no el valor ya
      sustituido. `MainWindowViewModel` lo traduce a una notificación (`Notification_PolarConflictOverwritten_*`,
      severidad Warning) usando `Plural.Get` para "tu edición de «X»" en singular o "N de tus ediciones
      locales" en plural — mismo mecanismo que `Kanban_DeleteColumn_Cards_one/_other`.

**Tests añadidos**: `PolarSyncServiceContractTests` — los tres valores de `PolarSyncOutcome`
(`Completed`/`NotLinked`/`SkippedAlreadyRunning`, este último con una respuesta HTTP bloqueada por un
`TaskCompletionSource` para forzar una segunda llamada real mientras la primera sigue en vuelo);
`LocalEditsOverwritten` con un conflicto (título capturado antes de sobrescribirse) y con varios conflictos
en el mismo ciclo (una sola llamada al evento, títulos agrupados). `PolarTaskManagementViewModelTests`
(nuevo) — el mismo escenario de concurrencia a nivel de ViewModel: `LastSyncNowWasSkipped` se activa cuando
el propio clic de "Sync now" se descarta por un sync ya en curso, y se limpia en cuanto el siguiente ciclo
completa de verdad. Suite completa verificada en verde (637 tests en `enki.Tests`, 6 en `enki.UITests`).

**Verificación manual pendiente**: la reconexión de `PolarRealtimeService` tras una caída real del socket no
tiene cobertura de test — `Supabase.Realtime` abre un WebSocket real sin transporte inyectable (mismo motivo
por el que `PolarSyncServiceTests` deja fuera la orquestación HTTP completa de `SupabaseRestClient`, ver su
propio comentario de clase), así que el camino "el socket se cae → `IsConnected` pasa a `false` → el
siguiente tick del temporizador reconecta" solo se ha verificado por inspección de código y contra la API
pública documentada de `Supabase.Realtime` 7.4.0 (`Client.AddStateChangedHandler`/`Constants.SocketState`).
Queda pendiente una comprobación manual con una cuenta Polar real (cortar la red unos segundos con la app
abierta y confirmar en los logs que reconecta sola) antes de dar este punto por cerrado en producción — mismo
criterio que las "Verificación manual pendiente" ya documentadas al cierre de las Fases 5 y 7.

**Dependencias**: ninguna respecto a otras fases.

---

## Fase 10 (opcional) — Deuda técnica menor y mejoras de producto derivadas de la auditoría ✅ completada

Hallazgos de severidad baja o mejoras de producto que la auditoría señaló pero que no son bugs ni riesgos
de seguridad — priorizar según feedback de uso real, no por orden fijo.

- [x] Añadir recurrencia nativa a `Reminder` (hoy solo las tareas Polar la tienen), para que un usuario sin
      cuenta vinculada pueda crear recordatorios repetitivos sin recrearlos manualmente. (análisis §6) —
      `Reminder.Recurrence` (nuevo, local-only como `DurationMinutes`: no forma parte del contrato de
      sincronización Polar/Supabase, reutiliza el mismo enum `PolarRecurrence` en vez de duplicar el
      concepto). `ReminderService.PopDueReminders` gana un helper `Fire` que, para un recordatorio
      recurrente, reprograma a la siguiente ocurrencia en vez de marcarlo disparado para siempre —
      reutilizando `PolarTaskService.CalculateNextDueDate` (misma aritmética consciente de DST que
      `RollRecurringTasks`, sin duplicarla) y avanzando en bucle si hubo varias ocurrencias perdidas con la
      app cerrada. El editor de recordatorios gana el mismo selector segmentado de recurrencia que Kanban,
      reutilizando sus claves de i18n ya traducidas; la lista/detalle muestran un chip "cada día"/"lun y
      mié"/etc. — extraído a `PolarRecurrenceFormatter` (nuevo, compartido con `PolarTaskRowViewModel`, que
      antes duplicaba la misma lógica de formateo).
- [x] Añadir un `undo`/deshacer al "Dismiss" de la bandeja de recordatorios del shell
      (`ReminderInboxViewModel.Dismiss`). (análisis §6) — ventana de 6 s (`DispatcherTimer` de un disparo)
      tras cada descarte, con una barra inline "Recordatorio descartado · Deshacer" en el propio dropdown
      (mismo patrón visual de texto+botón fantasma que `Polar_Sync_AlreadyRunningNotice`, sin componente de
      toast nuevo). Descartar un segundo elemento antes de que expire la ventana reemplaza el objetivo del
      undo sin des-descartar el primero.
- [x] Purgar `dismissed-reminders.json` cuando la tarjeta asociada se elimina definitivamente (hoy la
      entrada queda para siempre). (análisis §6) — `DismissedReminderService.RemoveForCard` (nuevo, también
      reutilizado por el punto anterior para el propio undo) enganchado a un nuevo evento
      `KanbanService.CardPermanentlyDeleted`, disparado desde las tres vías reales de borrado permanente
      (`PermanentlyDeleteTrashedCard`, `PermanentlyDeleteAllTrashedCards` y la purga automática de 30 días)
      — centralizadas en `KanbanService` en vez de que `TrashViewModel` siguiera manipulando
      `Board.TrashedCards` directamente.
- [x] Botón de "Probar sonido" en la sección de notificaciones de Ajustes. (análisis §6) — ya estaba
      implementado en el árbol de trabajo al empezar esta fase (`SettingsViewModel.TestNotificationSoundCommand`
      + `NotificationSoundPlayer`, cableado en `SettingsView.axaml`); verificado y dejado tal cual.
- [x] Evaluar si el `WebView` del grafo de sesiones (`SessionsView.axaml.cs`) necesita disposición
      explícita al salir de la pestaña repetidamente. (análisis §4) — evaluado sin necesitar perfilar en
      vivo: `MainWindow.axaml` hospeda cada página vía `ContentControl.Content`+`DataTemplate`, que
      reconstruye la vista (y por tanto el `NativeWebView`) desde cero en cada entrada a la pestaña en vez
      de mantenerla viva; `Avalonia.Controls.WebView.dll` 12.0.1 expone un `System.IDisposable.Dispose()`
      real (verificado contra el ensamblado del paquete) para liberar el motor de navegador nativo
      subyacente, que Avalonia no llama por sí sola al desconectar el control del árbol visual. Respuesta:
      sí hacía falta — `SessionsView.DisposeGraphWebView` (nuevo) lo llama desde `DetachedFromVisualTree`.
- [x] Propagar `CancellationToken` desde la UI hacia `GlobalSearchViewModel.SearchAsync` para cancelar una
      búsqueda en vuelo si el usuario seguía tecleando. (análisis §2) — `SearchAsync` cancela el
      `CancellationTokenSource` de la llamada anterior antes de lanzar una nueva; los cuatro `Search*`
      comprueban el token en cada iteración. De paso, un `.ToList()` que faltaba dentro de cada `Task.Run`
      (el `IEnumerable` perezoso de `yield return` no se materializaba hasta el `SelectMany` posterior al
      `await`, es decir en el hilo que esperaba, no en el hilo de fondo que `Task.Run` pretendía usar) — se
      corrigió de paso al tocar esas mismas líneas para pasar el token.
- [x] Evaluar introducir un contenedor de DI ligero. (análisis §2) — evaluado y **no implementado**, mismo
      criterio que la decisión de credenciales de la Fase 4: documentado en
      [decision-contenedor-di.md](decision-contenedor-di.md). La motivación real (testeabilidad) ya está
      resuelta por un camino más barato — el `storageDirectory`/`currentUserIdProvider` inyectable que cada
      servicio ya acepta (Fases 0/1/4) — y el caso de uso que un contenedor resolvería mejor (construir el
      `MainWindowViewModel` completo en un test) no ha aparecido todavía en los 656 tests de la suite.
- [x] Traducir `README.md` (o añadir una versión en inglés) dado que la app ya soporta 6 idiomas de UI.
      (análisis §7) — [README.en.md](../../../README.en.md) (nuevo), con enlace cruzado "Read in
      English"/"Leer en español" al principio de ambos ficheros. El resto de `.docs/project-info/` se deja en español a
      propósito (es el idioma de trabajo del proyecto, ver `AGENTS.md`) — el README es la única puerta de
      entrada que un visitante angloparlante de GitHub ve antes de decidir si le interesa seguir leyendo.
- [x] Subir `AnalysisLevel` en `Directory.Build.props` a `latest-recommended` y corregir los diagnósticos
      resultantes. (análisis §2) — subido, pero solo en `src/enki/enki.csproj`, no en
      `Directory.Build.props` (que sigue compartido por los tres proyectos): el hallazgo original
      ("~115 diagnósticos, casi todos CA1305, repartidos por los ViewModels") es específico del código de
      la app, y subirlo también en los proyectos de test arrastra un lote de reglas de higiene de tests
      (CA1816 en el patrón `Dispose` de xUnit sobre ~40 clases, CA1707 contra el naming
      `Method_Scenario_Expected` de toda la suite) que son un hallazgo distinto, no cubierto por este punto.
      Con el ámbito correcto: 116 diagnósticos únicos reales (76 CA1305, 18 CA1051, 13 CA1822, 3 CA1859, 3
      CA1001, 2 CA1869, 1 CA1865) — todos corregidos, ninguno suprimido a ciegas:
      - **CA1305** (formateo sensible a cultura): `CultureInfo.CurrentCulture` en todo lo que es texto de
        UI (la inmensa mayoría — `string.Format(LocalizationService.Instance[...], ...)`, `DateTime`/`int`
        `.ToString(...)` de etiquetas visibles); `CultureInfo.InvariantCulture` en lo que no lo es
        (timestamps de `LogService`, el nombre de nota diaria `yyyy-MM-dd` de `NotesViewModel`/el slash
        command "/fecha" del editor — identificadores técnicos, no texto que deba variar por idioma).
      - **CA1051** (campos públicos visibles): las 18 en `views/editor/Markdown/MarkdownModel.cs` (POCOs
        internos del parser de Markdown) pasadas a auto-propiedades — cambio mecánico, sin ningún `ref`/`out`
        sobre esos campos en el resto del código.
      - **CA1822** (miembro que no toca estado de instancia): marcados `static` los que son seguros
        (métodos privados, comandos `[RelayCommand]` — CommunityToolkit.Mvvm los soporta sin cambiar la
        propiedad de comando generada), con la salvedad de dos propiedades bindeadas desde XAML
        (`SettingsViewModel.AvailableLanguages`, `LanguageSetupViewModel.Languages`) que **no** se tocaron:
        un binding de Avalonia resuelve contra la instancia del `DataContext`, así que hacerlas estáticas
        rompería el binding en tiempo de ejecución sin que ningún test headless lo detectara — suprimidas
        con `#pragma warning disable CA1822` y el porqué documentado in situ en vez de "arreglarse" a
        ciegas. (Nota: `VaultService.CreateFolder`/`RenameFolder`/`MoveFile`, `SessionService.
        PermanentlyDeleteTrashedSession` y `SkillService.Export`/`ParseSkill` pasaron a `static` — sus
        llamadores, que los invocaban vía instancia, se actualizaron a la sintaxis de tipo, tanto en la app
        como en los tests: C# no permite invocar un miembro `static` a través de una referencia de
        instancia, `CS0176`, a diferencia de lo que un vistazo rápido a la regla podría sugerir.)
      - **CA1859**/**CA1869**/**CA1865**: tipos de retorno más concretos (`StringComparer`/`List<string>`),
        `JsonSerializerOptions` cacheado como `static readonly` en `SettingsService`/`VaultBackupService`, y
        `StartsWith(char)` en vez de `StartsWith(string)` — los tres mecánicos, sin implicaciones de diseño.
      - **CA1001** (tipo con campo disponible sin ser `IDisposable`): `PolarSyncService`/`PolarAuthService`/
        `SupabaseRestClient` son singletons de vida igual a la de la app, sin ninguna secuencia de apagado
        en todo el proyecto a la que engancharse — suprimido con `#pragma warning disable CA1001` y el
        porqué documentado, mismo criterio que el resto de supresiones de esta fase.
- [x] `AppSettings.AutoBackupIntervalDays`/`AutoBackupRetentionCount` (Fase 1) no tienen ningún control en
      la UI de Ajustes. (análisis §3) — `SettingsViewModel.AutoBackupIntervalDays`/`AutoBackupRetentionCount`
      (nuevos) con dos `NumericUpDown` (1–30) en Ajustes › General, deshabilitados junto al resto de la
      sección de backup automático mientras el interruptor esté apagado.
- [x] Añadir un icono de aplicación real (`.ico`/`.icns`/PNG). (análisis §7) — `assets/icon/`: monograma "E"
      blanco (`#F5F5F5`) sobre negro casi puro (`#121212`) en un cuadrado redondeado, siguiendo al pie de la
      letra "nada de color de marca" de `docs/sistema-diseno-web.md` §1.3, con la tipografía Inter que la
      propia app ya usa. `enki.ico` (multi-resolución 16–256) embebido como `ApplicationIcon` del `.exe` de
      Windows y como `Window.Icon` de `MainWindow.axaml` (las tres plataformas en tiempo de ejecución);
      `enki.icns` (familia completa 16–1024 con `@2x`) construido a mano contra el formato ICNS documentado,
      sin herramientas de macOS disponibles en este entorno — recomendable regenerarlo con `iconutil` en una
      máquina macOS real como verificación cruzada antes de un instalador `.dmg` de producción, pero el
      fichero actual ya es válido. Detalle completo en
      `docs/agent-docs/empaquetado-y-distribucion.md`.

**Tests añadidos**: `ReminderServiceTests` — un recordatorio recurrente vencido se reprograma en vez de
quedar disparado para siempre (una y varias ocurrencias perdidas de una vez), uno sin recurrencia conserva
el comportamiento preexistente. `DismissedReminderServiceTests.RemoveForCard_*` — descarte existente
limpiado sin importar la fecha, persistencia entre instancias, id desconocido sin efecto.
`KanbanServiceTests` — `PermanentlyDeleteTrashedCard`/`PermanentlyDeleteAllTrashedCards` disparan
`CardPermanentlyDeleted` por cada tarjeta (y ninguna vez si no había nada que borrar). `ReminderInboxViewModelTests`
(nuevo) — descartar muestra la barra de deshacer, deshacer revierte el descarte y hace reaparecer el
elemento, deshacer sin nada pendiente no hace nada, descartar un segundo elemento reemplaza el objetivo del
undo sin des-descartar el primero. `GlobalSearchViewModelTests.SearchAsync_NewerCall_CancelsThePreviousInFlightToken`
— la cancelación en sí (no una carrera end-to-end: este proceso de test no tiene el
`SynchronizationContext` de UI que serializa las fusiones de resultados en la app real, así que una carrera
de verdad no sería determinista aquí). Suite completa verificada en verde (650 tests en `enki.Tests`, 6 en
`enki.UITests`); las tres compilaciones (`enki` con `AnalysisLevel=latest-recommended`, `enki.Tests`,
`enki.UITests`) en 0 warnings con `TreatWarningsAsErrors` activo.

**Verificación manual pendiente**: el renderizado real del selector de recurrencia en el editor de
recordatorios, la barra de "Deshacer" del dropdown de recordatorios, y el icono de aplicación (ventana/
Explorador/Dock) se han verificado por tests headless e inspección de código, pero no se han visto en una
ejecución gráfica real de la app durante esta sesión (entorno sin servidor gráfico) — mismo criterio que las
"Verificación manual pendiente" ya documentadas al cierre de las Fases 5, 7 y 9.

**Dependencias**: ninguna. Esta fase es un cajón de sastre deliberado — cada punto es independiente de los
demás y de las fases 0-9.
