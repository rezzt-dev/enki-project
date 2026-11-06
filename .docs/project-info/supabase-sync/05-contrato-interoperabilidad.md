# 05 — Contrato de interoperabilidad (documento maestro de compatibilidad al 100%)

Este es **el documento que decide si Polar y la otra app son realmente compatibles**. Cualquier
implementación (en cualquier lenguaje/plataforma) que lea y escriba contra Supabase respetando
exactamente esto es, por definición, compatible con Polar. Si en algún momento hay conflicto entre este
documento y lo que haga cualquiera de las dos apps, **este documento manda** — hay que corregir la app,
no el contrato.

## Reglas generales (aplican a las 4 entidades)

1. **Nombres de campo:** `snake_case` en la red (JSON / Postgrest), tal cual las columnas de Supabase
   (doc 03). Cada app traduce internamente a su propia convención (`camelCase` en Kotlin, lo que sea en
   la otra app) en la capa de mapeo, nunca en el propio dato guardado.
2. **Timestamps:** siempre **entero de 64 bits, epoch millis UTC** (`bigint`). Nunca strings ISO-8601,
   nunca timestamps con timezone. Ejemplo: `1770739200000`. Esto es no negociable — es la única forma
   de que `2026-08-11 09:00 en Madrid` y `2026-08-11 09:00 en Ciudad de México` no se confundan al
   comparar; ambas apps hacen su propio formateo/timezone **solo en la capa de presentación**, nunca en
   el dato transportado.
3. **IDs:** siempre `uuid` (formato string estándar `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`), generado
   por el cliente que crea la fila. Nunca reutilizar IDs autoincrementales internos de ninguna app en el
   campo `id` de red.
4. **Booleans:** `true`/`false` reales (Postgres `boolean`), no `0`/`1` ni strings `"true"`.
5. **Campos desconocidos deben preservarse.** Si una app no entiende o no usa un campo (por ejemplo, la
   otra app no tiene concepto de "icono de lista"), **debe seguir enviando de vuelta el mismo valor que
   recibió** al actualizar esa fila, en vez de omitirlo o mandarlo a `null`/vacío. Omitir un campo al
   hacer `update` sobrescribiría con el default y **destruiría información** que la otra app sí usa.
   Recomendación técnica: hacer siempre `PATCH`/`update` con **solo los campos que la app realmente
   cambió**, nunca un `PUT` que reemplace el registro completo con los campos que esa app conoce.
6. **Nulls tienen significado**, no son "campo vacío": por ejemplo `due_date: null` significa
   explícitamente "esta tarea no tiene fecha límite", no "no sé la fecha". No confundir con omitir el
   campo.
7. **Strings vacíos vs. null:** `description` usa `""` como "sin descripción" (nunca `null`,
   coherente con el default de Polar). `title` nunca es `null` ni vacío (obligatorio, validar en
   cliente antes de enviar).

---

## Entidad `task_lists`

| Campo wire | Tipo | Nullable | Notas de compatibilidad |
|---|---|---|---|
| `id` | uuid | no | PK |
| `user_id` | uuid | no | = `auth.uid()` del usuario autenticado |
| `title` | string | no | |
| `icon` | string | no (default `"ic_list"`) | Enum recomendado, ver abajo. Tratar como **string opaco**: si la otra app recibe un valor que no reconoce, debe conservarlo igual al reenviarlo (no sustituirlo por su propio default) y mostrar un icono de fallback propio solo en su UI. |
| `color` | string | no (default `"#7F52FF"`) | Hex `#RRGGBB`. Cosmético — no bloqueante si una app lo ignora visualmente, pero debe preservarlo igual (regla 5). |
| `order_index` | integer | no (default 0) | Orden manual en el listado de listas |
| `home_order_index` | integer | no (default 0) | Orden manual en vista agrupada tipo "Home" |
| `is_dependency_chain` | boolean | no (default false) | Si `true`, las tareas de esta lista son una cadena secuencial (doc 02). Una app que no soporte este modo debe simplemente **no aplicar el bloqueo visual**, pero no debe borrar ni alterar este flag al reenviar la lista. |
| `is_deleted` | boolean | no (default false) | Tombstone (papelera) |
| `deleted_at` | bigint | sí | epoch millis del soft-delete, `null` si `is_deleted=false` |
| `created_at` | bigint | no | |
| `updated_at` | bigint | no | Ver reglas de LWW (doc 04) |

**Enum recomendado de `icon`** (heredado de Polar, ver doc 01): `ic_list`, `ic_folder`, `ic_work`,
`ic_home`, `ic_favorite`, `ic_schedule`, `ic_star`, `ic_circle`, `ic_edit`, `ic_location`, `ic_image`,
`ic_share`, `ic_sort`, `ic_chat`, `ic_check_box`, `ic_heart`. No es una restricción dura en base de
datos (no hay `CHECK`); es una convención para que ambas apps puedan, si quieren, mapear a un icono
propio reconocible. Un valor fuera de esta lista es válido y debe tratarse como "icono desconocido, usar
default", sin perder el string original.

---

## Entidad `tasks`

| Campo wire | Tipo | Nullable | Notas de compatibilidad |
|---|---|---|---|
| `id` | uuid | no | PK |
| `user_id` | uuid | no | |
| `list_id` | uuid | no | FK a `task_lists.id` |
| `title` | string | no | |
| `description` | string | no (default `""`) | |
| `completed` | boolean | no (default false) | |
| `tags` | array de strings | no (default `[]`) | En Postgres es `text[]`. **Importante:** Polar internamente guarda esto como CSV (`"trabajo,urgente"`) en su columna Room; la capa de sync de Polar debe convertir CSV ↔ array en el borde (nunca exponer el CSV crudo a la red). Cualquier app debe enviar/recibir siempre array, no CSV. |
| `due_date` | bigint | sí | `null` = sin fecha límite |
| `order_index` | integer | no (default 0) | |
| `recurrence` | string enum | no (default `"NONE"`) | Valores válidos exactos: `NONE`, `DAILY`, `WEEKLY`, `MONTHLY`, `MON_WED`, `FIRST_DAY_MONTH`. Ver doc 02 para el algoritmo exacto de próxima fecha — si la otra app calcula recurrencia por su cuenta (en vez de depender del motor centralizado del doc 03), **debe** implementar la tabla de reglas exactamente igual o las fechas divergirán entre apps. |
| `priority` | integer 0-3 | no (default 0) | `0`=Ninguna, `1`=Baja, `2`=Media, `3`=Alta. Si la otra app tiene más niveles de prioridad, debe mapear a esta escala de 4 valores al escribir aquí (no inventar valores fuera de 0-3). |
| `image_path` | string | sí | Ruta dentro del bucket `task-images` de Supabase Storage (`{user_id}/{task_uuid}.jpg`), **no** una URI local de ningún dispositivo. `null` = sin imagen. Ver doc 03/04. |
| `time_estimate` | integer | no (default 0) | Minutos estimados |
| `is_deleted` | boolean | no (default false) | |
| `deleted_at` | bigint | sí | |
| `created_at` | bigint | no | |
| `updated_at` | bigint | no | |

---

## Entidad `subtasks`

| Campo wire | Tipo | Nullable | Notas |
|---|---|---|---|
| `id` | uuid | no | |
| `user_id` | uuid | no | |
| `task_id` | uuid | no | FK a `tasks.id` |
| `title` | string | no | |
| `completed` | boolean | no (default false) | |
| `due_date` | bigint | sí | Las subtareas pueden tener su propia fecha/alarma independiente de la tarea padre |
| `order_index` | integer | no (default 0) | Campo nuevo respecto al Polar actual (que ordena por orden de inserción); ver doc 06 |
| `is_deleted` | boolean | no (default false) | |
| `deleted_at` | bigint | sí | |
| `created_at` | bigint | no | |
| `updated_at` | bigint | no | |

**Regla de negocio a preservar:** al completar/descompletar la tarea padre, Polar completa/descompleta
todas sus subtareas en bloque (doc 02). Cualquier app compatible debe replicar este comportamiento en
su propia UI (no es algo que el backend fuerce automáticamente, salvo que se decida añadir un trigger
para ello — no incluido por defecto para no sorprender a una app que quiera permitir subtareas
independientes de su tarea padre).

---

## Entidad `reminders`

| Campo wire | Tipo | Nullable | Notas |
|---|---|---|---|
| `id` | uuid | no | |
| `user_id` | uuid | no | |
| `title` | string | no | |
| `description` | string | no (default `""`) | |
| `date_time` | bigint | no | Momento de disparo del recordatorio |
| `is_completed` | boolean | no (default false) | |
| `latitude` | double | sí | |
| `longitude` | double | sí | |
| `radius` | float | sí | Metros. **Recordatorio importante:** en Polar hoy esto es solo informativo (abre un mapa), no dispara geofencing real. Si la otra app sí implementa geofencing real con este campo, debe documentarlo pero no debe asumir que Polar hará lo mismo. |
| `location_name` | string | sí | Nombre legible del lugar |
| `is_deleted` | boolean | no (default false) | |
| `deleted_at` | bigint | sí | |
| `created_at` | bigint | no | |
| `updated_at` | bigint | no | |

`reminders` no tiene relación con `tasks`/`task_lists` — es intencionadamente independiente, igual que
en Polar hoy (doc 01). No añadir una FK opcional "por si acaso" sin necesidad real de producto: mantiene
el contrato simple y evita ambigüedad de qué significa "una tarea con recordatorio" vs "un recordatorio
suelto".

---

## Ejemplo de payload completo (una tarea)

```json
{
  "id": "0f2b6c2e-df6a-4e2a-9a3a-2f0a6a3e9b41",
  "user_id": "7a1e6b0d-2222-4b7b-9a6c-111122223333",
  "list_id": "b3c9e4a0-1111-4c1a-8a2b-444455556666",
  "title": "Pagar la luz",
  "description": "",
  "completed": false,
  "tags": ["casa", "urgente"],
  "due_date": 1770739200000,
  "order_index": 3,
  "recurrence": "MONTHLY",
  "priority": 2,
  "image_path": null,
  "time_estimate": 5,
  "is_deleted": false,
  "deleted_at": null,
  "created_at": 1770000000000,
  "updated_at": 1770100000000
}
```

## Checklist de compatibilidad al 100% (para validar cualquier implementación nueva)

- [ ] Usa exactamente estos nombres de campo en `snake_case` en la red.
- [ ] Nunca escribe timestamps que no sean `bigint` epoch millis UTC.
- [ ] Nunca genera IDs que no sean `uuid` v4.
- [ ] Al actualizar una fila, solo envía los campos que realmente cambió (nunca sobrescribe con
      defaults campos que no tocó).
- [ ] Preserva campos que no usa/entiende (icon, color, is_dependency_chain, radius, etc.) tal cual los
      recibió.
- [ ] `tags` siempre como array, nunca CSV, en la red.
- [ ] `recurrence` solo usa los 6 valores exactos listados arriba.
- [ ] `priority` solo usa `0`-`3`.
- [ ] Convierte `image_path` (ruta de Storage) en vez de depender de URIs locales del dispositivo.
- [ ] Respeta `is_deleted`/`deleted_at` como tombstone — nunca hace `DELETE` físico directo salvo el
      job de purga documentado en el doc 03.
- [ ] Deja que el trigger de servidor resuelva conflictos (`updated_at`) en vez de implementar su
      propia lógica de "quién gana" en el cliente.
