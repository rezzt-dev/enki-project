# 02 — Lógica de negocio (lo que no se ve en el esquema de la base de datos)

Este documento recoge las **reglas de comportamiento** implementadas en código Kotlin que cualquier
sistema compatible (el backend Supabase, o la otra app) debe replicar o respetar para que el dato
"signifique lo mismo" en ambos lados. Son las partes que un simple `CREATE TABLE` no captura.

## Motor de recurrencia

Fuente: `app/.../worker/RecurrenceWorker.kt`.

**Comportamiento actual:** cuando el usuario marca como completada una tarea recurrente, Polar **no**
crea una tarea nueva — mantiene la misma fila, con `completed = true` y conserva el `dueDate` con el
que se completó. Un `WorkManager` periódico (`RecurrenceWorker`) recorre todas las tareas con
`completed = true` y `recurrence != "NONE"`, calcula la **próxima** fecha de vencimiento a partir de la
`dueDate` actual, y si esa próxima fecha ya llegó (`nextDueDate <= now`):

1. Pone `completed = false`.
2. Actualiza `dueDate = nextDueDate`.
3. Resetea todas las subtareas de esa tarea a `completed = false`.
4. Reprograma la alarma/notificación para la nueva `dueDate`.

Algoritmo `calculateNextDueDate(currentDueDate, recurrence)` — **debe reproducirse exactamente igual**
en cualquier otro motor (incluido el que se proponga centralizar en Supabase, ver doc 04):

```
DAILY            -> currentDueDate + 1 día calendario
WEEKLY           -> currentDueDate + 1 semana calendario
MONTHLY          -> currentDueDate + 1 mes calendario (usa Calendar.add(MONTH, 1): ajusta overflow de días igual que java.util.Calendar)
MON_WED          -> avanza día a día desde currentDueDate hasta caer en lunes o miércoles (el próximo que sea)
FIRST_DAY_MONTH  -> fija día=1 del mes de currentDueDate, luego suma 1 mes (es decir: el día 1 del MES SIGUIENTE)
```

Notas de exactitud:
- La suma es siempre relativa a la `dueDate` **anterior**, no a "hoy". Si el usuario no abre la app
  varios días, al recalcular solo avanza **un** intervalo (no salta múltiples ocurrencias perdidas).
- Se conserva la hora del día original (`Calendar.add` no toca hora/minuto).
- El chequeo `nextDueDate <= now` es lo que decide si "ya toca" reaparecer; si la próxima ocurrencia
  todavía es futura, la tarea sigue mostrándose como completada hasta que llegue esa fecha.

**Recomendación para el sistema multi-app** (desarrollada en doc 04): mover este cálculo a una función
programada en Supabase (Postgres/`pg_cron` o Edge Function) que se ejecute igual de periódica y aplique
la misma regla sobre la tabla `tasks` en la nube, para que el "rollover" ocurra una sola vez de forma
centralizada y ambas apps vean siempre el mismo resultado, estén o no abiertas. El `RecurrenceWorker`
local de Polar pasa a ser un *fallback* para modo offline prolongado, usando el mismo algoritmo.

## Sistema de alarmas y notificaciones

Fuente: `util/AlarmManagerHelper.kt`, `receiver/AlarmReceiver.kt`, `receiver/BootReceiver.kt`,
`receiver/NotificationActionReceiver.kt`, `util/NotificationHelper.kt`.

- Cada tarea con `dueDate` no nulo y no completada programa una alarma exacta
  (`AlarmManager.setExactAndAllowWhileIdle`) que dispara una notificación en el momento de la fecha
  límite.
- Cada recordatorio (`Reminder`) programa igual una alarma exacta en su `dateTime`.
- Las subtareas con `dueDate` propio también programan su propia alarma independiente.
- Los `PendingIntent` usan **request codes basados en el `id` local `Long`** con offsets para no
  colisionar entre tipos: tareas usan `taskId.toInt()`, recordatorios `1_000_000 + reminderId.toInt()`,
  subtareas `2_000_000 + subtaskId.toInt()`. **Esto es un detalle 100% local de Android y no debe
  sincronizarse ni depender del `uuid` global** — cada instalación de Polar reprograma sus propias
  alarmas locales a partir del dato ya sincronizado (ver doc 04, sección "qué NO se sincroniza").
- `BootReceiver` reprograma todas las alarmas activas al reiniciar el dispositivo (porque
  `AlarmManager` no persiste alarmas a través de reinicios).
- La notificación de una tarea permite "completar" o "posponer 1h" directamente desde la notificación
  (`NotificationActionReceiver`), lo cual dispara los mismos flujos de `TaskViewModel`/`ViewModel` que
  completar desde la UI (y por tanto debe disparar sync igual que cualquier otra edición).
- Al completar una tarea con fecha, la alarma se **cancela** (no se dispara notificación de una tarea
  ya hecha). Al descompletarla, se **reprograma**.
- Las notificaciones de recordatorio con `latitude`/`longitude` presentes muestran una acción "ver
  ubicación" que abre Google Maps con esas coordenadas. **Importante:** hoy `latitude/longitude/radius`
  son puramente informativos — **no existe geofencing real** (no hay `GeofencingClient` ni receiver de
  entrada/salida de zona). El campo `radius` está guardado pero no se usa para disparar nada por
  proximidad; solo se ofrece un botón para abrir el mapa. Cualquier compatibilidad con la otra app debe
  preservar estos campos como datos, sin asumir que Polar dispara alarmas por geolocalización.

## Listas encadenadas (`isDependencyChain`)

Cuando `TaskList.isDependencyChain = true`, las tareas de esa lista se tratan como una **cadena
secuencial dependiente**, ordenadas por `orderIndex`:

- Solo la primera tarea no completada de la cadena está "desbloqueada"; todas las que están después en
  el orden se muestran como bloqueadas (`isBlocked = true`) hasta que se completen las anteriores.
- En la vista "Home" (agrupada por lista), si una lista es cadena, **solo se muestra la primera tarea
  no completada** de esa lista (las demás quedan ocultas hasta que les toque).
- Esto es lógica puramente de **presentación/filtrado sobre datos existentes** (`orderIndex` +
  `completed`) — no añade columnas nuevas, así que no requiere tratamiento especial en el esquema de
  sync, pero **sí** implica que `orderIndex` debe sincronizarse de forma consistente y sin huecos
  ambiguos si dos dispositivos reordenan la misma lista offline al mismo tiempo (ver doc 04, resolución
  de conflictos).

## Papelera / soft-delete

- Nunca se borra físicamente una tarea o recordatorio al "eliminarlo" desde la UI normal: se marca
  `isDeleted = true` (`softDelete`).
- La papelera permite `restore` (`isDeleted = false`) o `permanentDelete` (borrado físico real,
  irreversible) o `emptyTrash` (borra físicamente todo lo que esté en papelera).
- Todas las consultas normales de la app filtran siempre `WHERE isDeleted = 0`.
- `TaskList` **no tiene papelera** — al borrar una lista se borra físicamente y en cascada arrastra sus
  tareas (`ON DELETE CASCADE`), pasando por alto el `isDeleted` de esas tareas. Ojo con esto al
  sincronizar: borrar una lista debe propagar como borrado real (o tombstone) de todas sus tareas
  también, no como soft-delete individual de cada una.
- `Subtask` tampoco tiene papelera propia: se completa/descompleta junto a su tarea padre, y se borra en
  cascada si se borra la tarea.

## Parser inteligente ("Smart Add")

Fuente: `domain/util/SmartParser.kt`. Al escribir una tarea en lenguaje natural en español (ej. "Pagar
la luz mañana a las 17:00 cada mes #casa"), Polar extrae automáticamente:

- **Tags** (`#palabra` → tag).
- **Recurrencia** (frases como "cada día", "todos los meses" → `DAILY`/`WEEKLY`/`MONTHLY`).
- **Fecha relativa** ("hoy", "mañana", "pasado mañana").
- **Hora** (`17:00`, `5pm`, `a las 17`).

Esto es una **conveniencia de entrada de datos únicamente en el cliente Android**: el resultado final
son campos normales (`title`, `dueDate`, `recurrence`, `tags`) ya limpios. No es parte del contrato de
sincronización — cada app puede tener (o no) su propio parser de lenguaje natural; lo único que importa
es que el resultado se guarde en los campos estándar del documento 05.

## Matriz de Eisenhower

Fuente: `ui/fragment/EisenhowerFragment.kt`. Es una vista **puramente calculada** sobre `Task.priority`
y `Task.dueDate` existentes (urgente = vencimiento próximo/vencido, importante = prioridad alta) — **no
persiste ningún dato nuevo**, no hay tabla ni columna de "cuadrante". Cualquier app compatible puede
recrear esta vista con la misma fórmula sin necesidad de sincronizar nada adicional.

## Estadísticas

Fuente: consultas agregadas en `TaskDao` (conteos por estado, por prioridad, por lista, por rango de
fechas). También son **cálculos derivados** sobre las tablas base — no hay estado propio que
sincronizar.

## Backup/Restore local (JSON)

Fuente: `data/backup/BackupManager.kt`. Exporta/importa un JSON con `taskLists + tasks + subtasks +
reminders` completo, usado para backup manual a un archivo elegido por el usuario (Storage Access
Framework), no para sync en la nube. El *restore* actual es destructivo (`deleteAll` + `insertAll`):
borra todo lo local y lo reemplaza por el contenido del backup. Se menciona aquí solo como referencia
de que ya existe un formato de serialización completo del dato — pero **no incluye** `uuid`,
`updatedAt` ni ningún campo de sincronización, así que no puede reutilizarse tal cual como formato de
red (ver doc 05 para el formato correcto).
