# Documentación de sincronización Polar ↔ Supabase ↔ App externa

> Carpeta destinada a un agente de IA (o desarrollador) que va a implementar sincronización en la nube
> para **Polar** usando **Supabase**, y a hacer que esos datos sean **100% compatibles** con otra
> aplicación distinta que el usuario ya posee y que también gestiona tareas y recordatorios.

## Objetivo del proyecto

Polar es hoy una app **100% local y offline** (Room/SQLite, sin red). El objetivo de esta iniciativa es:

1. Añadir un backend en **Supabase** (Postgres + Auth + Realtime + Storage) que sirva como fuente de
   verdad compartida.
2. Sincronizar Polar contra ese backend (multi-dispositivo).
3. Definir un **contrato de datos canónico** para que una **segunda aplicación** (de otro
   codebase/plataforma) pueda leer y escribir las mismas tareas y recordatorios sin pérdida ni
   ambigüedad — es decir, ambas apps deben poder crear, editar, completar y borrar el mismo dato y que
   la otra app lo entienda perfectamente.

**Prioridad explícita del usuario:** lo importante es que **la información** (tareas, subtareas,
recordatorios, listas, fechas, recurrencia, prioridad, etc.) viaje íntegra y sea compatible al 100%.
Los aspectos puramente estéticos de Polar (colores personalizados, temas) **no son prioritarios** — se
documentan para no perder información, pero no deben bloquear ni complicar el diseño del contrato.

## Cómo usar esta carpeta

Léase en orden. Cada documento asume que ya se leyó el anterior:

| # | Documento | Contenido |
|---|-----------|-----------|
| 1 | [01-modelo-de-datos-local.md](01-modelo-de-datos-local.md) | Qué existe hoy en Polar: entidades Room, campos, tipos, enums, relaciones, migraciones. La "verdad" actual del dato. |
| 2 | [02-logica-de-negocio.md](02-logica-de-negocio.md) | Reglas de negocio que no son obvias solo mirando la base de datos: recurrencia, alarmas, listas encadenadas, papelera, estadísticas, parser inteligente, matriz de Eisenhower. |
| 3 | [03-esquema-supabase.md](03-esquema-supabase.md) | DDL de Postgres propuesto: tablas, tipos, RLS, triggers, Storage. Lo que hay que crear en el proyecto Supabase. |
| 4 | [04-estrategia-sincronizacion.md](04-estrategia-sincronizacion.md) | Cómo sincroniza Polar (Android) contra ese esquema: IDs, resolución de conflictos, offline-first, Realtime, borrado. |
| 5 | [05-contrato-interoperabilidad.md](05-contrato-interoperabilidad.md) | **El documento más importante para la compatibilidad al 100%.** Contrato de datos campo a campo, agnóstico de plataforma, que ambas apps deben respetar exactamente igual. |
| 6 | [06-plan-implementacion-android.md](06-plan-implementacion-android.md) | Plan paso a paso para implementar todo esto dentro del código de Polar (Kotlin/Room/Hilt/WorkManager). |
| 7 | [07-guia-app-externa.md](07-guia-app-externa.md) | Guía agnóstica de plataforma para integrar la otra app contra el mismo backend Supabase y el mismo contrato. |
| 8 | [08-configuracion-y-credenciales.md](08-configuracion-y-credenciales.md) | **Empezar por aquí en la práctica:** checklist de qué hacer en el dashboard de Supabase y qué credenciales hacen falta (y cuáles nunca deben compartirse). |

## Decisiones de diseño ya tomadas (no reabrir sin motivo)

Estas decisiones se toman de forma definitiva en los documentos siguientes para que un agente pueda
implementar sin quedarse bloqueado pidiendo permiso. Resumen ejecutivo:

- **Identificadores:** cada fila sincronizable tiene un `UUID` global (`id` en Supabase) generado en el
  cliente que la crea. Polar conserva además su `id` local `Long` autoincremental de Room (uso interno:
  claves foráneas locales, `PendingIntent` request codes de alarmas), pero **nunca** lo expone a
  Supabase ni a la otra app.
- **Timestamps:** todos los campos de fecha/hora viajan como **epoch millis (`bigint`)**, igual que ya
  hace Polar internamente (`System.currentTimeMillis()`). Cero ambigüedad de timezone, cero
  parsing de ISO-8601 distinto entre plataformas.
- **Resolución de conflictos:** *Last-Write-Wins* por `updated_at`, aplicado con un **trigger en
  Postgres** (no en el cliente) para que sea simétrico entre Polar y la otra app sin duplicar lógica.
- **Borrado:** *soft delete* con tombstone (`is_deleted`, `deleted_at`) replicado a Supabase; el
  "vaciar papelera" de cada app no borra físicamente la fila en Supabase, solo la marca; un job
  periódico en Supabase purga tombstones antiguos. Esto evita que un borrado físico premature rompa la
  sincronización de la otra app.
- **Recurrencia:** el algoritmo de "siguiente fecha de vencimiento" se centraliza como función SQL en
  Supabase (ejecutada por `pg_cron` / Edge Function) para que el comportamiento sea idéntico
  sin importar qué app esté abierta. Polar mantiene su `RecurrenceWorker` local como *fallback* offline.
- **Autenticación:** Supabase Auth es el proveedor de identidad único; ambas apps autentican contra el
  mismo proyecto Supabase para que `auth.uid()` identifique al mismo usuario en ambas.
- **Imágenes de tareas:** se suben a Supabase Storage; el campo local `imageUri` (URI `content://` del
  dispositivo) deja de ser el dato canónico — el dato canónico es la ruta en el bucket de Storage.
- **Colores/temas:** se sincronizan como texto plano sin validación estricta (best-effort). No es un
  requisito duro de compatibilidad.

## Estado actual del código (referencia rápida)

- `app/src/main/java/app/polar/data/AppDatabase.kt` — Room DB v14, sin red.
- Entidades: `TaskList`, `Task`, `Subtask`, `Reminder` (ver documento 01).
- No existe ningún cliente de red, autenticación, ni SDK de Supabase en el proyecto todavía.
- Ya existe un sistema de backup/restore local a JSON (`BackupManager.kt`) que sirve de referencia de
  serialización, pero **no** es el contrato de sync (le faltan `uuid`, `updatedAt`, `user_id`, etc.).
