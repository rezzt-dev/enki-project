# 06 — Plan de implementación en Polar (Android / Kotlin)

Plan paso a paso para el agente que implemente esto en el código actual de Polar. Sigue el estilo del
proyecto (Hilt, Room, WorkManager, MVVM — ver `AGENTS.md` en la raíz del repo). Todas las decisiones de
diseño ya están tomadas en los documentos 01-05; aquí solo queda ejecutar.

## 0. Dependencias a añadir (`app/build.gradle.kts`)

```kotlin
implementation("io.github.jan-tennert.supabase:postgrest-kt:<version>")
implementation("io.github.jan-tennert.supabase:auth-kt:<version>")
implementation("io.github.jan-tennert.supabase:realtime-kt:<version>")
implementation("io.github.jan-tennert.supabase:storage-kt:<version>")
implementation("io.ktor:ktor-client-android:<version>") // engine HTTP requerido por el SDK
```

(Usar las versiones estables más recientes del SDK oficial `supabase-kt` en el momento de implementar;
comprobar compatibilidad con Kotlin 2.0.21 ya usado en el proyecto.)

`AndroidManifest.xml`: añadir `android.permission.INTERNET` y
`android.permission.ACCESS_NETWORK_STATE` (hoy la app no los tiene porque es 100% offline).

## 1. Migración de Room a v15

En `AppDatabase.kt`, añadir `MIGRATION_14_15` con las columnas nuevas de sync en las 4 tablas, más la
columna nueva `order_index` en `subtasks` (doc 03/05):

```sql
ALTER TABLE task_lists ADD COLUMN uuid TEXT;
ALTER TABLE task_lists ADD COLUMN updatedAt INTEGER NOT NULL DEFAULT 0;
ALTER TABLE task_lists ADD COLUMN deletedAt INTEGER DEFAULT NULL;
ALTER TABLE task_lists ADD COLUMN dirty INTEGER NOT NULL DEFAULT 1;

ALTER TABLE tasks ADD COLUMN uuid TEXT;
ALTER TABLE tasks ADD COLUMN updatedAt INTEGER NOT NULL DEFAULT 0;
ALTER TABLE tasks ADD COLUMN deletedAt INTEGER DEFAULT NULL;
ALTER TABLE tasks ADD COLUMN dirty INTEGER NOT NULL DEFAULT 1;

ALTER TABLE subtasks ADD COLUMN uuid TEXT;
ALTER TABLE subtasks ADD COLUMN updatedAt INTEGER NOT NULL DEFAULT 0;
ALTER TABLE subtasks ADD COLUMN deletedAt INTEGER DEFAULT NULL;
ALTER TABLE subtasks ADD COLUMN dirty INTEGER NOT NULL DEFAULT 1;
ALTER TABLE subtasks ADD COLUMN orderIndex INTEGER NOT NULL DEFAULT 0;

ALTER TABLE reminders ADD COLUMN uuid TEXT;
ALTER TABLE reminders ADD COLUMN updatedAt INTEGER NOT NULL DEFAULT 0;
ALTER TABLE reminders ADD COLUMN deletedAt INTEGER DEFAULT NULL;
ALTER TABLE reminders ADD COLUMN dirty INTEGER NOT NULL DEFAULT 1;
```

`uuid` no puede declararse `NOT NULL UNIQUE` directamente en un `ALTER TABLE` de SQLite sobre filas
existentes sin backfill previo. Pasos dentro de la misma migración (`Migration.migrate`):

1. Ejecutar los `ALTER TABLE` de arriba.
2. Recorrer cada tabla con un `Cursor`/`query` y hacer `UPDATE ... SET uuid = ? WHERE id = ?` con un
   `UUID.randomUUID().toString()` generado por fila, y `updatedAt = createdAt` (o `System.currentTimeMillis()`
   si `createdAt` no existe en esa fila).
3. Crear el índice único después del backfill: `CREATE UNIQUE INDEX idx_tasks_uuid ON tasks(uuid)` (y
   equivalente en las otras 3 tablas).

Actualizar las 4 `@Entity` (`TaskList`, `Task`, `Subtask`, `Reminder`) con los nuevos campos:

```kotlin
val uuid: String = java.util.UUID.randomUUID().toString(),
val updatedAt: Long = System.currentTimeMillis(),
val deletedAt: Long? = null,
val dirty: Boolean = true
```

Y `AppDatabase` sube a `version = 15`, añadiendo `MIGRATION_14_15` a `addMigrations(...)`.

## 2. "Touch" centralizado en los repositorios

Cada método de escritura de `TaskRepository`/`ReminderRepository` (y los DAOs `insert`/`update`) debe
garantizar que toda fila que se persiste lleve `updatedAt = now()` y `dirty = true` ya seteados **antes**
de llegar al DAO — la forma más limpia es añadir una función de extensión/helper:

```kotlin
fun Task.touched(): Task = copy(updatedAt = System.currentTimeMillis(), dirty = true)
fun Task.touchedDeleted(): Task = copy(
    isDeleted = true, deletedAt = System.currentTimeMillis(),
    updatedAt = System.currentTimeMillis(), dirty = true
)
```

y aplicarla en cada punto de escritura de `TaskViewModel`/`RemindersViewModel` (insert, update,
setTaskCompletion, toggleTaskCompletion, moveToTrash, restoreFromTrash, updateTasksOrder,
updateTaskListsOrder). **No olvidar** `RecurrenceWorker` (doc 02/04) y `NotificationActionReceiver`
(completar/posponer desde notificación) — ambos escriben directamente en el DAO sin pasar por el
ViewModel y hoy no tocarían `dirty`/`updatedAt` si no se actualizan explícitamente.

## 3. Refactor obligatorio: subtareas por diff, no por borrar-y-reinsertar

`TaskViewModel.updateTask()` hoy hace `deleteAllSubtasksForTask` + reinsertar todas desde cero
(ver doc 01). Con sync activo esto generaría, para cada edición de tarea, un tombstone + insert nuevo
por cada subtarea aunque no haya cambiado nada — ruido en Realtime, riesgo de pisar una edición
concurrente de otro dispositivo/app sobre una subtarea que ni siquiera cambió. Cambiar a diff real:

```kotlin
suspend fun replaceSubtasks(taskId: Long, newSubtasks: List<Subtask>) {
    val existing = subtaskDao.getSubtasksForTaskDirect(taskId)
    val existingById = existing.associateBy { it.id }
    val incomingIds = newSubtasks.filter { it.id != 0L }.map { it.id }.toSet()

    // Borrar (soft) las que ya no están
    existing.filter { it.id !in incomingIds }.forEach { softDeleteSubtask(it) }

    // Actualizar las que cambiaron, insertar las nuevas (id == 0L)
    newSubtasks.forEach { s ->
        if (s.id == 0L) insertSubtask(s.copy(taskId = taskId).touched())
        else if (existingById[s.id] != s) updateSubtask(s.touched())
    }
}
```

Esto requiere añadir `softDelete`/`restore` a `SubtaskDao` (hoy solo tiene `delete` físico), coherente
con el resto del esquema de tombstones.

## 4. Módulo Hilt para el cliente Supabase

`di/SupabaseModule.kt`:

```kotlin
@Module
@InstallIn(SingletonComponent::class)
object SupabaseModule {
    @Provides @Singleton
    fun provideSupabaseClient(): SupabaseClient = createSupabaseClient(
        supabaseUrl = BuildConfig.SUPABASE_URL,
        supabaseKey = BuildConfig.SUPABASE_ANON_KEY
    ) {
        install(Postgrest)
        install(Auth)
        install(Realtime)
        install(Storage)
    }
}
```

`SUPABASE_URL`/`SUPABASE_ANON_KEY` como `buildConfigField` desde `local.properties` o variables de
entorno de CI — **nunca hardcodeados ni commiteados**. La clave `anon` es pública por diseño de Supabase
(la seguridad real la da RLS), pero igualmente se gestiona por config, no en el código fuente.

## 5. Pantalla/flujo de autenticación

Nueva pantalla mínima (email + password, usando Supabase Auth) accesible desde Ajustes. Sin sesión
iniciada, la app sigue funcionando 100% local como hoy (no forzar login). Al iniciar sesión, disparar la
"primera sincronización" (doc 04).

## 6. `SyncManager` / capa de sincronización

Nuevo paquete `data/sync/`:

- `SyncManager` (o uno por entidad: `TaskListSyncEngine`, `TaskSyncEngine`, `SubtaskSyncEngine`,
  `ReminderSyncEngine`, compartiendo una interfaz genérica `SyncEngine<T>` con `push()` y `pull()`).
- Guarda el cursor `lastSyncAt` en `DataStore<Preferences>` (o `SharedPreferences`, consistente con lo
  que ya usa `TaskViewModel` para `sort_mode`).
- Orden de sincronización respetando dependencias (doc 04): `task_lists` → `tasks` → `subtasks`;
  `reminders` en paralelo (no depende de nada).
- Traduce entre las entidades Room (`camelCase`, `Long id` + `uuid`) y los DTOs de red (`snake_case`,
  solo `uuid`) — DTOs nuevos en `data/sync/dto/` (`TaskListDto`, `TaskDto`, `SubtaskDto`,
  `ReminderDto`) con `kotlinx.serialization` (requerido por el SDK de Supabase-kt).

## 7. `SyncWorker` (WorkManager)

- Periódico (ej. cada 15 min, `PeriodicWorkRequest`, con `Constraints` de red requerida) para el pull
  general + reintentos de push pendiente.
- Disparo inmediato (`OneTimeWorkRequest` encolado con `ExistingWorkPolicy.REPLACE` o similar) cada vez
  que hay cambios locales `dirty`, para minimizar la latencia de propagación sin depender solo del
  periódico — igual que `RecurrenceWorker` ya convive con WorkManager en el proyecto.
- Suscripción Realtime activa mientras la app está en foreground (`Application`/`Activity` lifecycle),
  aplicando cada evento entrante con la misma lógica de merge del pull (doc 04).

## 8. Manejo de imágenes (`imageUri` → Supabase Storage)

- Al asociar una imagen a una tarea, seguir guardando localmente `imageUri` (content URI, para mostrarla
  offline al instante) **y además** subir el archivo a `task-images/{user_id}/{task_uuid}.jpg` en
  background, guardando la ruta resultante en un nuevo campo local `imagePath` que es el que se
  sincroniza (doc 05 — el campo de red es `image_path`, nunca `imageUri`).
- Al recibir una tarea remota con `image_path` no nulo y sin caché local, descargar bajo demanda (por
  ejemplo, al abrir el detalle de la tarea) y cachear localmente.

## 9. Reprogramar alarmas tras sync

Cada vez que el `pull` inserta/actualiza una tarea o recordatorio local con fecha futura y no
completado, debe llamarse a `AlarmManagerHelper.scheduleTaskAlarm`/`scheduleReminderAlarm` igual que ya
hace el flujo local (doc 02) — si no, una tarea creada desde la otra app nunca notificará en el
dispositivo Android aunque el dato ya esté sincronizado.

## 10. Tests

Seguir el patrón existente (`app/src/test/java/app/polar/data/repository/TaskRepositoryTest.kt`, JUnit4
+ MockK + coroutines-test): añadir tests unitarios para el algoritmo de merge LWW cliente
(`remote.updatedAt > local.updatedAt` → gana remoto, etc.), para el diff de subtareas del punto 3, y
para la traducción DTO ↔ entidad (especialmente `tags` CSV ↔ array y la resolución `listId` ↔
`list_id`).

## Orden recomendado de ejecución

1. Migración Room v15 + entidades (punto 1).
2. "Touch" en repositorios/ViewModels + refactor de subtareas (puntos 2-3) — esto ya deja la app lista
   para sincronizar sin tocar todavía Supabase, y es seguro de probar en local primero.
3. Esquema Supabase (doc 03) creado en el proyecto real.
4. Dependencias + Auth + `SupabaseModule` (puntos 0, 4, 5).
5. `SyncManager`/DTOs + `SyncWorker` (puntos 6-7).
6. Imágenes + reprogramación de alarmas (puntos 8-9).
7. Tests (punto 10).
