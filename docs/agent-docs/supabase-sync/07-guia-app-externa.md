# 07 — Guía para integrar la otra app (agnóstica de plataforma)

Este documento es para quien implemente el lado de la **segunda aplicación** (la que ya existe, de otro
codebase/lenguaje/plataforma). No asume nada sobre Android/Kotlin/Room — solo asume que esa app puede
hablar HTTP/WebSocket contra Supabase. El objetivo es que, siguiendo esto, esa app quede sincronizada
con Polar sin que nadie tenga que tocar el código de Polar para "hacerla compatible" a posteriori.

## Lo mínimo que hace falta saber

1. **Proyecto Supabase compartido**: la otra app debe apuntar exactamente al mismo proyecto Supabase
   (misma `SUPABASE_URL`, misma `anon key`) que Polar. No son dos backends distintos sincronizados entre
   sí — es **un único backend** al que ambas apps leen/escriben directamente.
2. **Autenticación compartida**: el usuario inicia sesión con Supabase Auth (email/password como
   mínimo) en ambas apps, con la misma cuenta. `auth.uid()` es lo que aísla sus datos vía RLS (doc 03)
   — sin sesión válida, no hay acceso a ninguna fila.
3. **Esquema**: usar tal cual las tablas `task_lists`, `tasks`, `subtasks`, `reminders` definidas en
   [03-esquema-supabase.md](03-esquema-supabase.md). No crear tablas paralelas ni duplicar el modelo —
   esa es la trampa más común que rompe la compatibilidad ("cada app con su tabla, sincronizadas por un
   proceso aparte"): aquí no hace falta, ambas leen/escriben la misma tabla directamente.
4. **Contrato de campos**: seguir exactamente [05-contrato-interoperabilidad.md](05-contrato-interoperabilidad.md)
   — nombres, tipos, enums, unidades de tiempo, semántica de `null`. Es el documento a validar contra
   cualquier PR/cambio de esa app.

## Operaciones típicas vía API REST autogenerada de Supabase (Postgrest)

Ejemplo de creación de tarea (`POST` a `/rest/v1/tasks` con header `Prefer: return=representation`):

```http
POST /rest/v1/tasks
Authorization: Bearer <jwt del usuario autenticado>
apikey: <anon key>
Content-Type: application/json
Prefer: return=representation

{
  "id": "generado-como-uuid-v4-en-el-cliente",
  "user_id": "<auth.uid() del usuario>",
  "list_id": "<uuid de una lista existente>",
  "title": "Comprar leche",
  "description": "",
  "completed": false,
  "tags": ["casa"],
  "due_date": 1770739200000,
  "order_index": 0,
  "recurrence": "NONE",
  "priority": 0,
  "image_path": null,
  "time_estimate": 0,
  "is_deleted": false,
  "deleted_at": null,
  "created_at": 1770700000000,
  "updated_at": 1770700000000
}
```

Actualización parcial (**solo** los campos que cambiaron, ver regla 5 del doc 05):

```http
PATCH /rest/v1/tasks?id=eq.0f2b6c2e-df6a-4e2a-9a3a-2f0a6a3e9b41
Authorization: Bearer <jwt>
apikey: <anon key>
Content-Type: application/json

{ "completed": true, "updated_at": 1770701000000 }
```

Descargar cambios desde el último sync (`pull`, doc 04):

```http
GET /rest/v1/tasks?updated_at=gt.1770700000000&order=updated_at.asc
Authorization: Bearer <jwt>
apikey: <anon key>
```

Borrado: **nunca** `DELETE` directo desde la app (eso es solo del job de purga del servidor, doc 03).
En su lugar, `PATCH` con `is_deleted: true, deleted_at: <now>, updated_at: <now>`.

## Realtime (opcional pero recomendado)

Suscribirse al canal de Postgres Changes de Supabase Realtime, filtrando por `user_id=eq.<uid>`, para
las 4 tablas. Cualquier SDK oficial de Supabase (JS, Dart/Flutter, Python, Swift, Kotlin, etc.) expone
esto de forma prácticamente idéntica. Esto es lo que permite que un cambio hecho en Polar aparezca en la
otra app sin que el usuario tenga que refrescar manualmente, y viceversa.

## Reglas de negocio a respetar (no solo esquema)

Estas están detalladas en [02-logica-de-negocio.md](02-logica-de-negocio.md); resumen de lo que importa
para no "sorprender" al usuario al alternar entre apps:

- **Recurrencia**: si la otra app no calcula recurrencia por sí misma, no hace falta que haga nada — el
  motor centralizado en Supabase (doc 03, `roll_recurring_tasks`) lo resuelve para ambas. Si sí la
  calcula localmente (por ejemplo para funcionar offline), debe usar exactamente la tabla de reglas del
  doc 02 para `DAILY`/`WEEKLY`/`MONTHLY`/`MON_WED`/`FIRST_DAY_MONTH`.
- **Papelera / soft-delete**: "eliminar" una tarea/recordatorio en la UI de esa app debe ser un
  soft-delete (`is_deleted=true`), no un borrado físico. Igual que Polar, debe ofrecer su propia
  papelera (o al menos no perder el dato) para que "eliminar" no sea irreversible desde el punto de
  vista del usuario que también usa Polar.
- **Listas encadenadas (`is_dependency_chain`)**: si esa app no quiere implementar esta UX, está bien —
  simplemente debe **no** modificar este flag al editar una lista que lo tenga activo (preservarlo, no
  pisarlo a `false` por accidente al hacer un `update` de otros campos de la lista).
- **Subtareas**: al completar/descompletar una tarea con subtareas, replicar el mismo estado en cascada
  a todas sus subtareas (igual que Polar).
- **Imágenes**: si esa app permite adjuntar imágenes a tareas, debe subirlas al mismo bucket
  `task-images` de Supabase Storage con la convención de ruta `{user_id}/{task_uuid}.jpg` y escribir esa
  ruta en `image_path` — nunca una URL/URI propia del dispositivo o de otro storage, porque Polar no
  podría resolverla.
- **Recordatorios con ubicación**: `latitude`/`longitude`/`radius`/`location_name` son datos válidos de
  guardar y mostrar, pero recordar que Polar **no** dispara notificaciones por geofencing real hoy — si
  esa app sí lo hace, es una mejora exclusiva suya, no debe asumirse simétrica.

## Qué NO hace falta replicar de Polar

- Colores de lista (`color`) e iconos (`icon`): son cosméticos, de baja prioridad explícita del usuario.
  Basta con preservarlos al editar (no descartarlos), sin necesidad de tener una UI equivalente para
  elegirlos si esa app no lo necesita.
- Cualquier lógica específica de notificaciones/alarmas de Android (`AlarmManager`, `PendingIntent`,
  canales de notificación) — es 100% interna de Polar y no forma parte del contrato de datos.
- El "Smart Parser" de lenguaje natural en español de Polar (doc 02) — es una conveniencia de entrada
  de datos, no un requisito de compatibilidad; el resultado que produce son campos normales del
  contrato.

## Validación final

Antes de dar por compatible la integración, verificar con datos reales:

1. Crear una tarea en Polar → aparece igual (mismo título, fecha, prioridad, tags, lista) en la otra
   app.
2. Crear una tarea en la otra app → aparece igual en Polar, incluyendo que suene/notifique en el
   dispositivo Android si tiene fecha futura.
3. Completar una tarea recurrente en cualquiera de las dos → en ambas se ve completada, y al llegar la
   siguiente ocurrencia, en ambas se ve descompletada con la nueva fecha.
4. Editar subtareas desde ambas apps de forma alternada → no se duplican ni se pierden.
5. Borrar una tarea desde una app → deja de aparecer en la otra (papelera), y "vaciar papelera" en una
   no revive fantasmas en la otra tras la purga.
6. Editar solo el color/icono de una lista desde una app que no soporta esos campos → el resto de campos
   de esa lista no debe empezar aleatoriamente a diverger entre apps por culpa de PATCHes que
   sobrescriben de más.
