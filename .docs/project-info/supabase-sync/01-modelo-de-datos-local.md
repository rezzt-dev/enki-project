# 01 — Modelo de datos local actual (Room / SQLite)

Este documento describe **exactamente** lo que existe hoy en el código de Polar, campo a campo, para
que quien diseñe el backend no tenga que adivinar nada ni volver a leer el código fuente.

Fuente: `app/src/main/java/app/polar/data/entity/*.kt`, `AppDatabase.kt` (versión de esquema: **14**).

## Resumen de entidades

```
TaskList (1) ──< Task (N) ──< Subtask (N)
Reminder                                    (independiente, sin relación con Task/TaskList)
```

- `Task.listId` → FK a `TaskList.id`, `ON DELETE CASCADE` (borrar una lista borra sus tareas).
- `Subtask.taskId` → FK a `Task.id`, `ON DELETE CASCADE` (borrar una tarea borra sus subtareas).
- `Reminder` es una entidad totalmente independiente — no cuelga de ninguna tarea ni lista.

Todos los IDs son `Long` autoincrementales (SQLite `rowid`), **locales al dispositivo**. Esto es
importante: hoy dos instalaciones de Polar en dos móviles distintos pueden tener una tarea con `id = 7`
que no tiene ninguna relación entre sí. Esto se resuelve en el documento 04.

---

## `TaskList` (tabla `task_lists`)

```kotlin
data class TaskList(
  val id: Long = 0,                    // PK autoincremental
  val title: String,                   // Nombre de la lista, obligatorio
  val icon: String = "ic_list",        // Ver enum de iconos abajo
  val createdAt: Long = System.currentTimeMillis(),
  val orderIndex: Int = 0,             // Orden manual dentro del listado de listas (drawer)
  val homeOrderIndex: Int = 0,         // Orden manual en la vista "Home" (agrupado por lista)
  val isDependencyChain: Boolean = false, // Ver "listas encadenadas" en doc 02
  val color: String = "#7F52FF"        // Color hex ARGB/RGB de la lista (cosmético, baja prioridad)
)
```

**Enum de iconos válidos** (`app/.../ui/dialog/TaskListDialog.kt`) — son claves de texto ya semánticas,
no nombres de recursos Android arbitrarios, así que son perfectamente reutilizables como enum canónico:

```
ic_list, ic_folder, ic_work, ic_home, ic_favorite, ic_schedule, ic_star,
ic_circle, ic_edit, ic_location, ic_image, ic_share, ic_sort, ic_chat,
ic_check_box, ic_heart
```

No hay validación en base de datos: es un `TEXT` libre. Cualquier valor fuera de esta lista simplemente
no tendrá icono reconocible en la UI de Polar, pero no rompe nada.

---

## `Task` (tabla `tasks`)

```kotlin
data class Task(
  val id: Long = 0,                    // PK autoincremental
  val listId: Long,                    // FK -> task_lists.id, obligatorio
  val title: String,                   // obligatorio
  val description: String = "",
  val completed: Boolean = false,
  val tags: String = "",               // CSV: "trabajo,urgente,casa" — ver doc 05 para formato wire
  val createdAt: Long = System.currentTimeMillis(),
  val dueDate: Long? = null,           // epoch millis, nullable (tarea sin fecha)
  val orderIndex: Int = 0,             // orden manual dentro de la lista
  val recurrence: String = "NONE",     // ver enum de recurrencia abajo
  val isDeleted: Boolean = false,      // soft-delete (papelera)
  val priority: Int = 0,               // 0=Ninguna, 1=Baja, 2=Media, 3=Alta
  val imageUri: String? = null,        // URI local content:// del dispositivo — NO portable, ver doc 03/04
  val timeEstimate: Int = 0            // minutos estimados para completar la tarea
)
```

**Enum de `recurrence`** (string libre, sin `CHECK` constraint en SQLite hoy):

| Valor | Significado |
|---|---|
| `NONE` | Tarea no recurrente (default) |
| `DAILY` | Se repite todos los días |
| `WEEKLY` | Se repite cada semana (mismo día de la semana) |
| `MONTHLY` | Se repite cada mes (mismo día del mes) |
| `MON_WED` | Se repite lunes y miércoles |
| `FIRST_DAY_MONTH` | Se repite el día 1 de cada mes |

El algoritmo exacto de cálculo de "próxima fecha" para cada valor está en el documento
[02-logica-de-negocio.md](02-logica-de-negocio.md#motor-de-recurrencia) — es crítico reproducirlo
exactamente igual en cualquier sistema que también calcule recurrencia, para no divergir entre apps.

**`priority`**: entero `0..3`. No hay enum de texto en el modelo actual, es puramente numérico. Se
usa también para ordenar tareas (`UNMARK_FIRST` sort mode: no completadas primero, luego por prioridad
descendente).

**`tags`**: string CSV sin espacios extra garantizados, separadas por coma. No hay tabla de tags
normalizada; es un campo de texto plano en la propia fila de `Task`.

---

## `Subtask` (tabla `subtasks`)

```kotlin
data class Subtask(
  val id: Long = 0,                    // PK autoincremental
  val taskId: Long,                    // FK -> tasks.id, obligatorio, ON DELETE CASCADE
  val title: String,
  val completed: Boolean = false,
  val dueDate: Long? = null            // epoch millis, nullable — las subtareas también pueden tener alarma propia
)
```

Nota importante de comportamiento actual (ver doc 02): cuando se edita una tarea desde la UI, Polar
**borra todas las subtareas de esa tarea y las vuelve a insertar** (`deleteAllForTask` +
`insertAll`/`insert` en bucle) en vez de hacer un diff campo a campo. Esto es aceptable en local
(mismos IDs se regeneran, no hay observadores externos) pero **es peligroso para sincronización**:
generaría un aluvión de deletes+inserts en vez de un update, y podría pisar cambios concurrentes de
otro dispositivo/app sobre esa misma subtarea. Está marcado como refactor obligatorio antes de
sincronizar (ver doc 06).

---

## `Reminder` (tabla `reminders`)

```kotlin
data class Reminder(
    val id: Long = 0,                  // PK autoincremental
    val title: String,
    val description: String = "",
    val dateTime: Long,                // epoch millis, obligatorio — momento de disparo
    val isCompleted: Boolean = false,
    val createdAt: Long = System.currentTimeMillis(),
    val isDeleted: Boolean = false,     // soft-delete (papelera)
    val latitude: Double? = null,
    val longitude: Double? = null,
    val radius: Float? = null,          // metros — hoy NO se usa como geofence real, ver doc 02
    val locationName: String? = null    // nombre legible del lugar (ej. "Casa", "Oficina")
)
```

`Reminder` **no** tiene relación con `Task`/`TaskList`: es una entidad de recordatorios "sueltos"
(tipo "recuérdame X a las Y"), independiente de las tareas. Importante no confundir con
`Task.dueDate` (que también dispara una notificación, pero es la fecha límite de una tarea).

---

## Historial de migraciones (`AppDatabase.kt`)

Útil para entender por qué algunos campos son nullable/con default y llegaron "después":

| Migración | Cambio |
|---|---|
| 6→7 | `task_lists.homeOrderIndex` |
| 7→8 | `tasks.recurrence` |
| 8→9 | `tasks.isDeleted`, `reminders.isDeleted` (papelera) |
| 9→10 | `task_lists.isDependencyChain` |
| 10→11 | `tasks.priority`, `tasks.imageUri`, `subtasks.dueDate` |
| 11→12 | `tasks.timeEstimate` |
| 12→13 | `reminders.latitude/longitude/radius/locationName` |
| 13→14 | `task_lists.color` |

La app usa `fallbackToDestructiveMigration()` como red de seguridad, pero todas las migraciones reales
están escritas a mano con `ALTER TABLE ... ADD COLUMN`. **La migración a sincronización (v15) debe
seguir el mismo patrón** — ver doc 06.

## Modelos derivados (no son tablas, son proyecciones de consulta)

Viven en `app/.../data/model/`: `TaskWithList`, `TaskGroup`, `PriorityCount`, `ListTaskCount`. Son
resultados de `JOIN`/`GROUP BY` para la UI (título de lista junto a la tarea, conteos para
estadísticas). **No necesitan sincronizarse**: se recalculan localmente a partir de las tablas base, en
cualquiera de las dos apps.

## Campos que NO son portables tal cual (requieren tratamiento especial)

| Campo | Problema | Solución (ver doc 03/04/05) |
|---|---|---|
| `Task.imageUri` | URI `content://...` válida solo en el dispositivo/app que la creó | Subir a Supabase Storage, sincronizar la ruta del bucket, no la URI local |
| `Task.id`, `TaskList.id`, `Subtask.id`, `Reminder.id` | `Long` autoincremental local, colisiona entre dispositivos/apps | Añadir `uuid` global, ver doc 04 |
| `TaskList.icon` | Claves ya semánticas (ver enum arriba), pero cada app puede tener su propio set de iconos | Tratar como string opaco; cada app mapea a su propio asset o cae a un icono por defecto si no lo reconoce |
| `TaskList.color` | Cosmético, no prioritario | Sincronizar tal cual como texto, sin validación estricta |
