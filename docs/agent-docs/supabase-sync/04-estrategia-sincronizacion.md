# 04 — Estrategia de sincronización (cliente ↔ Supabase)

Este documento describe cómo un cliente (Polar, o la otra app) debe comportarse frente al esquema del
documento 03 para mantenerse offline-first y sincronizado de forma robusta. Es agnóstico de plataforma
en su diseño; el documento 06 lo traduce a pasos concretos de Kotlin/Room/WorkManager para Polar.

## Columnas locales nuevas necesarias (en cualquier cliente)

Cada entidad sincronizable (lista, tarea, subtarea, recordatorio) necesita, además de sus columnas de
negocio, estas columnas **locales**:

| Columna | Tipo | Propósito |
|---|---|---|
| `uuid` | string, único, no nulo | Identificador global — es el `id` que existe en Supabase. Se genera **en el momento de creación**, en el cliente, con un UUID v4. Nunca cambia. |
| `updatedAt` | epoch millis | Se actualiza en **cada** escritura local (crear, editar, completar, mover, borrar-a-papelera, restaurar). Es el campo que decide el Last-Write-Wins. |
| `deletedAt` | epoch millis, nullable | Se rellena cuando `isDeleted` pasa a `true`. Es el tombstone. |
| `dirty` | boolean, **solo local, nunca se sincroniza** | `true` mientras la fila tiene cambios locales aún no confirmados en servidor. Lo pone a `true` cualquier escritura local; lo pone a `false` el `SyncWorker` tras un push exitoso. |
| `remoteDeletedConfirmed` (opcional) | boolean | Útil si se quiere distinguir "borrado local pendiente de subir" de "borrado ya confirmado en servidor", pero puede resolverse igual solo con `dirty`. |

El `id` local `Long` autoincremental de Room **se mantiene sin cambios** — sigue siendo la PK real de
Room (más barata para índices/joins/foreign keys locales) y sigue siendo la que usa
`AlarmManagerHelper` para los `PendingIntent` request codes. El `uuid` es un campo adicional, indexado
como `UNIQUE`, que es el que viaja a Supabase.

## Resolución de relaciones (FK local `Long` ↔ FK remota `uuid`)

`Task.listId` es local (`Long`). Al empujar una tarea a Supabase hay que enviar el `uuid` de su lista
padre, no su `listId` local. Como cada fila hija ya conoce el `id` local de su padre, y el padre ya
tiene su propio `uuid` en la misma base de datos local, resolver esto es un simple `SELECT uuid FROM
task_lists WHERE id = :listId` en el momento de construir el payload de sync — no hace falta ninguna
tabla de mapeo adicional. Al **recibir** (pull) una tarea desde Supabase con `list_id` en formato
`uuid`, se hace el camino inverso: `SELECT id FROM task_lists WHERE uuid = :remoteListId` para resolver
el `listId` local antes de insertar/actualizar en Room. Si el padre aún no existe localmente (orden de
llegada), se debe sincronizar primero `task_lists` y luego `tasks`/`subtasks` (orden de dependencia:
listas → tareas → subtareas; recordatorios no dependen de nada).

## Flujo de escritura local (cualquier mutación del usuario)

1. Si es una fila nueva: generar `uuid = UUID.randomUUID()`.
2. Fijar `updatedAt = now()`.
3. Si la mutación es un soft-delete: fijar `isDeleted = true`, `deletedAt = now()`.
4. Fijar `dirty = true`.
5. Persistir en Room (como hoy).
6. Notificar al `SyncWorker` para que intente subir pronto (no bloqueante, ver doc 06).

Esto aplica también a mutaciones "de sistema" que hoy no pasan por el ViewModel de forma obvia: el
`RecurrenceWorker` al resetear una tarea recurrente, y `NotificationActionReceiver` al completar/posponer
desde una notificación. **Ambos deben marcar `dirty = true` y actualizar `updatedAt`**, si no, esos
cambios nunca se sincronizarían.

## Push (subir cambios locales)

1. Consultar todas las filas con `dirty = true` en las 4 tablas.
2. Traducir cada una al payload de red (documento 05: nombres `snake_case`, `list_id`/`task_id` como
   `uuid` resuelto, `tags` como array, etc.).
3. `upsert` vía Postgrest con `on_conflict=id` (el `id` uuid es la clave de conflicto).
4. El trigger de Supabase (doc 03) resuelve LWW automáticamente — la respuesta del `upsert` devuelve la
   fila **ganadora** (puede no ser la que acabamos de enviar, si perdimos el conflicto).
5. Si la fila devuelta tiene `updated_at` == la que enviamos → ganamos → poner `dirty = false`
   localmente.
6. Si la fila devuelta tiene `updated_at` mayor a la que enviamos → perdimos el conflicto → **sobrescribir
   la fila local con la versión de servidor** y dejar `dirty = false` (nuestro cambio quedó descartado
   porque alguien escribió después; no reintentar).

## Pull (bajar cambios remotos)

1. Guardar un cursor local `lastSyncAt` (epoch millis, en `SharedPreferences`/`DataStore`, uno por
   tabla o uno global — un global es suficiente si se usa el mínimo `updated_at` de las 4 consultas).
2. Consultar `select * from tasks where updated_at > :lastSyncAt` (y equivalente para las otras 3
   tablas), ordenado por dependencia (listas → tareas → subtareas → recordatorios).
3. Para cada fila remota:
   - Buscar localmente por `uuid`.
   - Si no existe localmente → `INSERT`.
   - Si existe y `remote.updated_at > local.updated_at` → `UPDATE` local con los datos remotos, `dirty
     = false`.
   - Si existe y `remote.updated_at <= local.updated_at` → **no tocar** (el local ya está igual o más
     nuevo; si es más nuevo y aún no se subió, quedará `dirty = true` y el siguiente push lo resolverá).
   - Si `remote.is_deleted = true` → aplicar como soft-delete local (no hard-delete todavía).
4. Detectar tombstones purgados: si una fila que localmente existe y no está en papelera deja de
   aparecer en sucesivas pulls **y** se confirma con un `select` puntual por `uuid` que ya no existe en
   servidor, tratarla como borrado remoto real (hard-delete local). Esto es un caso raro (solo ocurre
   tras la purga de 30 días); no es necesario resolverlo con altísima prioridad, pero debe documentarse
   en el otro extremo para que no queden "fantasmas" eternos.
5. Actualizar `lastSyncAt`.

## Realtime (recomendado, no estrictamente obligatorio)

Suscribirse a `postgres_changes` en las 4 tablas filtradas por `user_id = eq.<uid>`. Cada evento
recibido se aplica con la misma lógica de "pull" de un único registro (comparar `updated_at`). Esto es
lo que da la sensación de sincronización instantánea entre Polar y la otra app cuando ambas están
abiertas — "que se enteren la una de la otra al momento". Sin Realtime, el sync sigue funcionando vía
polling periódico (`SyncWorker`), solo que con más latencia.

## Primera sincronización (login inicial / migración de datos ya existentes)

Cuando un usuario inicia sesión por primera vez en un dispositivo con datos locales preexistentes
(offline, sin `uuid` todavía — instalaciones ya en uso de Polar antes de esta migración):

1. Migración de esquema local (Room v15, doc 06) genera `uuid` nuevo para cada fila existente y marca
   todas `dirty = true`.
2. Primer `push` sube todo como si fuera nuevo (con sus `uuid` recién generados).
3. Primer `pull` trae lo que ya hubiera en Supabase de ese usuario (por ejemplo, si ya sincronizó desde
   la otra app antes). Como son `uuid` distintos de ambos lados (no hay forma automática de saber que
   "Comprar leche" local es la "misma" tarea que una remota preexistente), **no hay deduplicación
   automática de contenido duplicado en el primer sync** — ambas quedan como filas separadas. Es un
   trade-off aceptado: intentar hacer *fuzzy matching* por título es frágil y puede fusionar tareas que
   no son la misma. Si se quiere evitar duplicados, la recomendación de producto es: en el primer login,
   preguntar al usuario si quiere "subir mis datos locales" o "descartar datos locales y usar solo lo de
   la nube" en vez de fusionar a ciegas.

## Qué NO se sincroniza (permanece 100% local en cada dispositivo/app)

- `id` local `Long` de Room.
- `dirty`.
- Cualquier `PendingIntent` / *request code* de alarmas.
- Estado de filtros de UI, `sort_mode` (`SharedPreferences` locales, `task_prefs`).
- Caché local de imagen (`imageUri` con `content://`) — se reemplaza por descarga desde Storage usando
  `image_path` (ver doc 03).
- Estado de "expandido/colapsado" de secciones de UI, tutoriales vistos, etc.

## Manejo de borrado en cascada (listas → tareas → subtareas)

Como en Supabase las FKs son `ON DELETE CASCADE` igual que en Room, un soft-delete de una lista
(`is_deleted = true`) **no** dispara cascada automática (el cascade de Postgres solo actúa en `DELETE`
real, no en `UPDATE`). El cliente que borra una lista debe, en la misma operación lógica, marcar
`is_deleted = true` también en todas sus tareas (y esas tareas propagar a sus subtareas), igual que hoy
hace Polar localmente al confiar en el `ON DELETE CASCADE` de SQLite para el borrado físico — la
diferencia es que en el mundo sincronizado el borrado de una lista pasa a ser **también** un
soft-delete en cascada explícito, no un hard-delete inmediato, para dar tiempo a que la otra app se
entere antes de que la fila desaparezca físicamente (ver "purga de tombstones", doc 03).
