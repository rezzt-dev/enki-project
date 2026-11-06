# 03 — Esquema de Supabase propuesto

Este documento es el DDL concreto a crear en el proyecto de Supabase. Usa Postgres + Auth + Realtime +
Storage. Todo el diseño da servicio simultáneamente a Polar y a la otra app: ninguna tabla ni columna es
específica de Android.

## Principios de diseño

1. **`id uuid` como clave primaria real**, generado en el cliente (`gen_random_uuid()` como default de
   servidor también disponible por si algún cliente no lo genera, pero lo ideal es que el cliente lo
   genere para poder trabajar offline-first antes de la primera sincronización).
2. **`user_id uuid` en cada tabla**, referencia a `auth.users.id`. Aísla los datos de cada usuario con
   Row Level Security. Ambas apps deben autenticar contra el mismo proyecto Supabase para compartir
   `auth.uid()`.
3. **Timestamps como `bigint` (epoch millis)**, no `timestamptz`, para calzar 1:1 con
   `System.currentTimeMillis()` de Android y con `Date.now()` de JS/lo que use la otra app, sin
   conversión de timezone en ningún punto del camino.
4. **`updated_at` se actualiza siempre por trigger de servidor**, nunca confiar en que el cliente lo
   mande bien — así el *Last-Write-Wins* es fiable incluso si el reloj de un dispositivo está mal
   ajustado en el momento de la escritura local (se corrige en el momento de la escritura en servidor).
5. **Tombstones, no `DELETE` inmediato**: `is_deleted boolean` + `deleted_at bigint`. Un job periódico
   purga (`DELETE` real) las filas con `is_deleted = true` y `deleted_at` más antiguo que la retención
   configurada (ej. 30 días) — momento en el que sí desaparecen de verdad.

## DDL

```sql
-- ============================================================
-- EXTENSIONES
-- ============================================================
create extension if not exists "pgcrypto"; -- gen_random_uuid()

-- ============================================================
-- TASK_LISTS
-- ============================================================
create table public.task_lists (
  id                  uuid primary key default gen_random_uuid(),
  user_id             uuid not null references auth.users(id) on delete cascade,
  title               text not null,
  icon                text not null default 'ic_list',
  color               text not null default '#7F52FF',
  order_index         integer not null default 0,
  home_order_index    integer not null default 0,
  is_dependency_chain boolean not null default false,
  is_deleted          boolean not null default false,
  deleted_at          bigint,
  created_at          bigint not null,
  updated_at          bigint not null,
  server_updated_at   timestamptz not null default now() -- solo para auditoría/debug, no es el campo de LWW
);

-- ============================================================
-- TASKS
-- ============================================================
create table public.tasks (
  id             uuid primary key default gen_random_uuid(),
  user_id        uuid not null references auth.users(id) on delete cascade,
  list_id        uuid not null references public.task_lists(id) on delete cascade,
  title          text not null,
  description    text not null default '',
  completed      boolean not null default false,
  tags           text[] not null default '{}',
  due_date       bigint,
  order_index    integer not null default 0,
  recurrence     text not null default 'NONE'
                   check (recurrence in ('NONE','DAILY','WEEKLY','MONTHLY','MON_WED','FIRST_DAY_MONTH')),
  priority       smallint not null default 0 check (priority between 0 and 3),
  image_path     text,          -- ruta en Supabase Storage (bucket task-images), NO una content:// URI
  time_estimate  integer not null default 0,
  is_deleted     boolean not null default false,
  deleted_at     bigint,
  created_at     bigint not null,
  updated_at     bigint not null,
  server_updated_at timestamptz not null default now()
);

-- ============================================================
-- SUBTASKS
-- ============================================================
create table public.subtasks (
  id          uuid primary key default gen_random_uuid(),
  user_id     uuid not null references auth.users(id) on delete cascade,
  task_id     uuid not null references public.tasks(id) on delete cascade,
  title       text not null,
  completed   boolean not null default false,
  due_date    bigint,
  order_index integer not null default 0,   -- NUEVO respecto a Polar hoy: ver nota abajo
  is_deleted  boolean not null default false,
  deleted_at  bigint,
  created_at  bigint not null,
  updated_at  bigint not null,
  server_updated_at timestamptz not null default now()
);

-- ============================================================
-- REMINDERS
-- ============================================================
create table public.reminders (
  id            uuid primary key default gen_random_uuid(),
  user_id       uuid not null references auth.users(id) on delete cascade,
  title         text not null,
  description   text not null default '',
  date_time     bigint not null,
  is_completed  boolean not null default false,
  latitude      double precision,
  longitude     double precision,
  radius        real,
  location_name text,
  is_deleted    boolean not null default false,
  deleted_at    bigint,
  created_at    bigint not null,
  updated_at    bigint not null,
  server_updated_at timestamptz not null default now()
);

create index on public.task_lists (user_id);
create index on public.tasks (user_id, list_id);
create index on public.tasks (user_id, updated_at);
create index on public.subtasks (user_id, task_id);
create index on public.reminders (user_id, updated_at);
```

> **Nota `subtasks.order_index`**: Polar hoy ordena subtareas por `id` de inserción, sin columna de
> orden explícita. Para sincronización se añade `order_index` desde el inicio (evita ambigüedad de
> orden cuando dos apps insertan subtareas offline en paralelo). Ver doc 06 para el cambio equivalente
> en Room.

## Row Level Security (RLS)

Cada usuario solo puede ver/editar sus propias filas. Aplica igual a las cuatro tablas:

```sql
alter table public.task_lists enable row level security;
alter table public.tasks      enable row level security;
alter table public.subtasks   enable row level security;
alter table public.reminders  enable row level security;

-- Patrón idéntico para las 4 tablas (ejemplo con task_lists, repetir para las demás)
create policy "select_own" on public.task_lists
  for select using (user_id = auth.uid());
create policy "insert_own" on public.task_lists
  for insert with check (user_id = auth.uid());
create policy "update_own" on public.task_lists
  for update using (user_id = auth.uid());
create policy "delete_own" on public.task_lists
  for delete using (user_id = auth.uid());
```

Repetir literalmente esas 4 policies para `tasks`, `subtasks` y `reminders`.

## Trigger: `updated_at` autoritativo + Last-Write-Wins

Este es el mecanismo central que hace posible que **ambas apps** escriban sin lógica de merge en el
cliente y aun así el resultado sea determinista. Aplica a las 4 tablas:

```sql
create or replace function public.touch_and_resolve_lww()
returns trigger as $$
begin
  -- Si es un UPDATE y la fila entrante es "más vieja" que la que ya está en servidor,
  -- se descarta la escritura entrante (gana la que ya estaba) — Last-Write-Wins real,
  -- no "el último que llega gana sin comparar".
  if TG_OP = 'UPDATE' and NEW.updated_at < OLD.updated_at then
    return OLD;
  end if;

  -- El timestamp de servidor siempre se refresca aquí, es el que se usará para
  -- comparar en la siguiente escritura. El "updated_at" que manda el cliente se
  -- respeta si gana el LWW arriba, pero server_updated_at es infraestructura interna.
  NEW.server_updated_at := now();
  return NEW;
end;
$$ language plpgsql;

create trigger trg_task_lists_lww before insert or update on public.task_lists
  for each row execute function public.touch_and_resolve_lww();
create trigger trg_tasks_lww before insert or update on public.tasks
  for each row execute function public.touch_and_resolve_lww();
create trigger trg_subtasks_lww before insert or update on public.subtasks
  for each row execute function public.touch_and_resolve_lww();
create trigger trg_reminders_lww before insert or update on public.reminders
  for each row execute function public.touch_and_resolve_lww();
```

Con esto, un `upsert` normal vía Postgrest (`on_conflict=id`) desde **cualquiera de las dos apps** ya
resuelve conflictos correctamente sin que ninguna de las dos tenga que implementar lógica de merge
propia: si Polar sube una versión desactualizada de una tarea que la otra app ya modificó más tarde, el
trigger la descarta silenciosamente y la fila en servidor no cambia. El cliente que perdió el conflicto
debe simplemente **volver a leer** la fila (el siguiente `pull`, ver doc 04) para quedarse con la
versión ganadora — así ambas apps convergen al mismo estado sin coordinación adicional.

## Purga de tombstones (borrado definitivo)

```sql
create or replace function public.purge_old_tombstones()
returns void as $$
begin
  delete from public.reminders where is_deleted and deleted_at < (extract(epoch from now())*1000 - 30*24*3600*1000);
  delete from public.subtasks  where is_deleted and deleted_at < (extract(epoch from now())*1000 - 30*24*3600*1000);
  delete from public.tasks     where is_deleted and deleted_at < (extract(epoch from now())*1000 - 30*24*3600*1000);
  delete from public.task_lists where is_deleted and deleted_at < (extract(epoch from now())*1000 - 30*24*3600*1000);
end;
$$ language plpgsql security definer;

select cron.schedule('purge-tombstones-daily', '0 4 * * *', $$select public.purge_old_tombstones();$$);
```

(Requiere la extensión `pg_cron`, disponible en proyectos Supabase.) 30 días es el valor sugerido; se
puede ajustar. Ambas apps deben interpretar "la fila ya no existe en el servidor" como "bórrala también
localmente si aún la tienes", en el flujo de `pull` (ver doc 04).

## Motor de recurrencia centralizado (recomendado)

Para que el "desmarcar tarea recurrente cuando llega su próxima fecha" ocurra igual sin importar qué
app esté abierta (o si ninguna lo está), se recomienda una función SQL que replique
`RecurrenceWorker.calculateNextDueDate` (documento 02) y un `pg_cron` que la ejecute cada 15–60 min:

```sql
create or replace function public.roll_recurring_tasks()
returns void as $$
declare
  r record;
  next_due bigint;
begin
  for r in
    select * from public.tasks
    where completed = true and recurrence <> 'NONE' and due_date is not null and not is_deleted
  loop
    next_due := public.calculate_next_due_date(r.due_date, r.recurrence);
    if next_due <= (extract(epoch from now())*1000)::bigint then
      update public.tasks
        set completed = false, due_date = next_due, updated_at = (extract(epoch from now())*1000)::bigint
        where id = r.id;
      update public.subtasks set completed = false, updated_at = (extract(epoch from now())*1000)::bigint
        where task_id = r.id;
    end if;
  end loop;
end;
$$ language plpgsql security definer;

select cron.schedule('roll-recurring-tasks', '*/15 * * * *', $$select public.roll_recurring_tasks();$$);
```

`calculate_next_due_date(bigint, text)` implementa en SQL/plpgsql exactamente la tabla de reglas del
documento 02 (`DAILY`/`WEEKLY`/`MONTHLY`/`MON_WED`/`FIRST_DAY_MONTH`). Al escribir esta función,
verificar los casos límite contra el código Kotlin original (`Calendar.add`) para no divergir,
especialmente en `MONTHLY` (overflow de fin de mes) y `MON_WED` (búsqueda día a día).

Con esto activo, tanto Polar como la otra app simplemente **reciben** el cambio vía Realtime/pull; no
necesitan reimplementar el algoritmo de recurrencia en absoluto (aunque Polar debe conservar su
`RecurrenceWorker` local como red de seguridad para el caso sin conexión, ver doc 06).

## Supabase Storage — imágenes de tareas

```sql
insert into storage.buckets (id, name, public) values ('task-images', 'task-images', false);

create policy "read_own_images" on storage.objects for select
  using (bucket_id = 'task-images' and (storage.foldername(name))[1] = auth.uid()::text);
create policy "write_own_images" on storage.objects for insert
  with check (bucket_id = 'task-images' and (storage.foldername(name))[1] = auth.uid()::text);
create policy "delete_own_images" on storage.objects for delete
  using (bucket_id = 'task-images' and (storage.foldername(name))[1] = auth.uid()::text);
```

Convención de ruta: `{user_id}/{task_id}.jpg` (usar el `uuid` de la tarea, no el `id` local de Room).
`tasks.image_path` guarda esa ruta relativa (no una URL firmada, que caduca) — cada app resuelve la URL
de descarga/firma en el momento de mostrarla.

## Realtime

Habilitar réplica lógica para las 4 tablas (Supabase → Database → Replication) para que
`postgres_changes` funcione vía Realtime, protegido igualmente por RLS. Ambas apps se suscriben a
`INSERT`/`UPDATE`/`DELETE` filtrando `user_id = auth.uid()` para recibir cambios de la otra app casi al
instante mientras estén online.
