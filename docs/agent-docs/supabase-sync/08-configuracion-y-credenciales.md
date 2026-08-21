# 08 — Qué hacer en Supabase y qué credenciales hacen falta

Este documento es el paso previo a todo lo demás: hasta que esto no esté hecho, ningún código del plan
del documento 06 puede probarse contra un backend real. Se divide en dos partes: (A) lo que hay que
hacer en el dashboard de Supabase, en orden; (B) qué datos hay que recopilar y **cómo** dármelos —
importante, porque no todos los datos deben pegarse en un chat.

---

## Parte A — Checklist en el dashboard de Supabase (supabase.com)

Hazlo en este orden; algunos pasos dependen del anterior.

### 1. Crear el proyecto
- Crea una cuenta/organización si no la tienes, y un **proyecto nuevo**.
- Elige una **contraseña de base de datos** fuerte cuando te la pida — guárdala en tu gestor de
  contraseñas. Solo se usa para acceso directo a Postgres (psql/CLI), no la necesita el código de la
  app.
- Elige la región más cercana a donde vayas a usar las apps (menor latencia).

### 2. Habilitar extensiones necesarias (antes de ejecutar el SQL)
Ve a **Database → Extensions** y activa:
- `pgcrypto` (normalmente ya viene activada; genera los UUID).
- `pg_cron` (necesaria para los jobs programados del doc 03: purga de papelera y motor de recurrencia).
- `pg_net` (si tu versión de Supabase la pide como dependencia de `pg_cron` para tareas HTTP; en la
  mayoría de proyectos nuevos no hace falta si solo usas `cron.schedule` con SQL puro como en el doc 03).

Estas dos (`pg_cron` sobre todo) **no se pueden activar con `CREATE EXTENSION` desde el SQL Editor** en
proyectos gestionados — hay que activarlas desde el toggle del dashboard, si no, los `cron.schedule(...)`
del final del documento 03 fallarán.

### 3. Ejecutar el esquema (documento 03)
Ve a **SQL Editor** y ejecuta, en este orden, los bloques SQL de
[03-esquema-supabase.md](03-esquema-supabase.md):
1. Extensiones (`pgcrypto`, ya cubierto en el paso 2).
2. `CREATE TABLE` de `task_lists`, `tasks`, `subtasks`, `reminders` + índices.
3. `ALTER TABLE ... ENABLE ROW LEVEL SECURITY` + las políticas (`create policy ...`).
4. La función `touch_and_resolve_lww()` + los 4 `CREATE TRIGGER`.
5. `purge_old_tombstones()` + su `cron.schedule(...)`.
6. `roll_recurring_tasks()` (y la función auxiliar `calculate_next_due_date` que hay que escribir a
   partir de las reglas del doc 02) + su `cron.schedule(...)`.
7. El bloque de Storage (`insert into storage.buckets ...` + policies).

Si algún bloque falla, no sigas con el siguiente hasta resolverlo — se ejecutan en orden porque
dependen unos de otros (triggers referencian tablas ya creadas, etc.).

### 4. Activar Realtime en las 4 tablas
Ve a **Database → Replication**, entra en la publicación `supabase_realtime` y activa las 4 tablas
(`task_lists`, `tasks`, `subtasks`, `reminders`). Este paso es manual y se olvida fácilmente: sin él, la
suscripción Realtime del doc 04 no recibirá ningún evento aunque el resto funcione bien.

### 5. Configurar Auth
Ve a **Authentication → Providers** y confirma que **Email** está habilitado (suele venirlo por
defecto).

Ve a **Authentication → URL Configuration** y define un **Site URL** (aunque sea un placeholder tipo
`https://polar.app` si ninguna de las dos apps tiene todavía un dominio propio) — algunos flujos de
Supabase Auth (reseteo de contraseña, magic link) lo requieren para construir el enlace de vuelta.

Ve a **Authentication → Settings** y decide si quieres **"Confirm email"** activado o no:
- Activado (por defecto): el usuario debe verificar su email antes de poder iniciar sesión — más
  seguro, más fricción.
- Desactivado: se puede iniciar sesión inmediatamente tras registrarse — más cómodo mientras se está
  desarrollando/probando. Se puede cambiar en cualquier momento sin afectar a usuarios ya creados.

### 6. Verificar RLS activo
En **Table Editor**, cada una de las 4 tablas debe mostrar el candado de RLS activado. Si el SQL del
paso 3 se ejecutó completo, ya debería estar así — es solo una verificación final antes de dar por
buena la configuración.

---

## Parte B — Qué datos necesito y cómo dármelos

**Importante — seguridad primero:** de los siguientes datos, dos son secretos de verdad y **no deben
pegarse nunca en este chat, ni en ningún archivo del repositorio, ni en ningún documento**. Te explico
cuáles son y por qué en la tabla:

| Dato | Dónde lo encuentras | ¿Es secreto? | Qué hacer con él |
|---|---|---|---|
| **Project URL** | Settings → API → "Project URL" (ej. `https://abcdefgh.supabase.co`) | No | Va en `local.properties` (ver abajo) |
| **publishable key** (`sb_publishable_...`; en proyectos antiguos se llama `anon` `public`) | Settings → API → "Project API keys" | No es secreta por diseño (la protege RLS), pero tampoco hace falta compartirla aquí | Va en `local.properties` |
| **secret key** (`sb_secret_...`; en proyectos antiguos se llama `service_role`) | Settings → API → "Project API keys" | **SÍ, muy secreta** — salta toda la seguridad RLS | **No la necesito y no debe usarse en ninguna de las dos apps móviles, ni guardarse en ningún archivo del repositorio, ni siquiera en `local.properties`.** Solo tendría sentido si en el futuro se crea un Edge Function o script de administración; en ese caso se guarda como *secret* del propio proyecto Supabase (Settings → Edge Functions → Secrets), nunca en el código cliente. |
| **Contraseña de la base de datos** | La que elegiste al crear el proyecto | **SÍ, secreta** | Solo para conexión directa por `psql`/CLI si algún día hace falta depurar algo a mano. No la necesito para nada de lo planificado aquí. |
| **Project Ref / ID** | Se ve en la URL del dashboard (`supabase.com/dashboard/project/<ref>`) | No especialmente | Opcional, útil solo si en algún momento uso la Supabase CLI; no es imprescindible dármelo. |

> ⚠️ **Corrección de formato de claves + incidente detectado:** Supabase tiene un formato de claves
> nuevo: `sb_publishable_...` (equivalente al `anon key` de siempre — segura para el cliente, protegida
> por RLS) y `sb_secret_...` (equivalente al `service_role key` — **salta toda la seguridad RLS,
> jamás debe ir en una app móvil ni en un repositorio**). En una edición anterior de este documento se
> pegó por error un valor `sb_secret_...` real en el campo `SUPABASE_ANON_KEY`, en un archivo que **no
> está en `.gitignore`**. Ese valor ha sido retirado de aquí, pero como estuvo en texto plano en este
> archivo (y en la conversación), la recomendación es **regenerar esa clave secreta ahora mismo** desde
> el dashboard: *Settings → API → Project API keys → `secret` → Regenerate*, y no reutilizar la que se
> llegó a pegar. La clave `sb_publishable_...` que también se compartió no es sensible por diseño, pero
> igualmente se recomienda no dejar valores reales en documentos versionados — solo en `local.properties`.

**En resumen: lo único que realmente hace falta para que yo pueda escribir el código de conexión son la
Project URL y la clave `publishable` (el `sb_publishable_...`, no el `sb_secret_...`) — y ni siquiera
necesito que me las pegues en el chat.** Guárdalas tú directamente donde el proyecto ya las va a leer:
`local.properties` (que está en `.gitignore`, así que nunca se sube al repositorio). Añade estas líneas
al `local.properties` que ya existe en la raíz del proyecto, sustituyendo por tus valores reales
**solo ahí, nunca en un archivo de `agent-docs/` ni en ningún otro archivo versionado**:

```properties
SUPABASE_URL=https://tu-proyecto.supabase.co
SUPABASE_ANON_KEY=sb_publishable_xxxxxxxxxxxxxxxxxxxx
```

(La `SUPABASE_JWKS_URL` no es secreta —es un endpoint público derivable siempre como
`{SUPABASE_URL}/auth/v1/.well-known/jwks.json`— así que no hace falta guardarla aparte; el propio
`SDK`/backend la calcula si la necesita. El `sb_secret_...` no debe guardarse en ningún sitio de este
proyecto Android.)

Y avísame cuando lo hayas hecho (sin pegarme los valores) — yo añadiré en `app/build.gradle.kts` la
lectura de ese archivo hacia `BuildConfig`, algo así (ya queda listo para cuando implementemos el punto
0/4 del documento 06):

```kotlin
import java.util.Properties
import java.io.FileInputStream

val localProperties = Properties().apply {
    val file = rootProject.file("local.properties")
    if (file.exists()) load(FileInputStream(file))
}

android {
    buildFeatures {
        viewBinding = true
        buildConfig = true
    }
    defaultConfig {
        buildConfigField("String", "SUPABASE_URL", "\"${localProperties.getProperty("SUPABASE_URL", "")}\"")
        buildConfigField("String", "SUPABASE_ANON_KEY", "\"${localProperties.getProperty("SUPABASE_ANON_KEY", "")}\"")
    }
}
```

Con esto, el código nunca contiene la clave en texto plano dentro de un archivo versionado, y yo puedo
seguir escribiendo/revisando código sin necesidad de conocer el valor real de tus credenciales.

### Para la otra app

Si la otra app también va a compilar contra el mismo proyecto, necesita exactamente los mismos dos
valores (`SUPABASE_URL` y `SUPABASE_ANON_KEY`), guardados según el mecanismo de configuración/secrets
que use esa plataforma (variables de entorno, `.env` con su propio gitignore, etc. — el equivalente a
`local.properties` en su ecosistema). El principio es el mismo: URL y anon key sí pueden vivir en config
de cliente; `service_role` y contraseña de DB nunca.

### Lo único que sí necesito que me confirmes por chat (no son secretos)

- Que ya ejecutaste el SQL completo del documento 03 sin errores (o si algún bloque falló, cuál y qué
  error dio).
- Que activaste Realtime para las 4 tablas (paso A.4).
- Si quieres "Confirm email" activado o desactivado para empezar a probar (paso A.5) — dime cuál
  prefieres y seguimos con esa asunción en el código de Auth del documento 06.
