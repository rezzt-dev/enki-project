# Enki

*[Leer en español](README.md)*

Enki is a desktop app (Windows/Linux/macOS via [Avalonia UI](https://avaloniaui.net/), .NET 8, MVVM with
`CommunityToolkit.Mvvm`) built as a personal "second brain": local markdown notes with wikilinks, work
sessions with a visual graph, local tasks (Kanban) and cloud tasks (Polar, synced via Supabase and shared
with a same-name Android app), a unified calendar, reminders, a notification center and a cross-cutting
trash.

## Modules

| Module | What it does |
|---|---|
| Dashboard | Command center: summarizes the rest of the modules. |
| Notes | Local markdown vault with wikilinks (`[[Note]]`). |
| Sessions | Note sub-spaces with a visual relationship graph. |
| Kanban | Local task board. |
| Polar | Cloud-synced tasks (Supabase), shared with the Android app. |
| Calendar | Unified view of Kanban + Polar + Reminders. |
| Reminders | Standalone date-based alerts, independent of Kanban/Polar. |
| Trash | Recovery of deleted items in Notes and Kanban. |
| Settings | Cross-cutting configuration (vault, theme, typography, Polar link). |

## Building and running

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) or newer (`enki/global.json` pins the
exact version with `rollForward: latestMajor`). The main project (`enki.csproj`) targets `net8.0` — if
`dotnet test` fails to launch the test host with a "You must install or update .NET" message even though
the build itself succeeds, the machine is missing the 8.0.x *runtime* (not just a newer SDK); install it,
or use `DOTNET_ROLL_FORWARD=LatestMajor dotnet test ...` as a one-off workaround.

```bash
# Build
dotnet build enki/src/enki/enki.csproj

# Run
dotnet run --project enki/src/enki/enki.csproj

# Tests
dotnet test enki/tests/enki.Tests/enki.Tests.csproj
```

### Polar sync (optional)

Cloud sync (the Polar module) requires Supabase credentials in `enki/local.properties` (git-ignored, never
committed):

```
SUPABASE_URL=<supabase-project-url>
SUPABASE_ANON_KEY=<public-anon-key>
```

Without that file the app still works, and every feature that doesn't depend on Polar stays available —
`SupabaseConfig.IsConfigured` stays `false` and the Polar screens disable themselves gracefully. See
[docs/supabase-sync/08-configuracion-y-credenciales.md](docs/supabase-sync/08-configuracion-y-credenciales.md)
for details (Spanish only, like the rest of `docs/`).

## Documentation

Every document under `docs/` is in Spanish, the project's primary working language — see
[AGENTS.md](AGENTS.md) for that convention. Pointers, for reference:

- [docs/analisis-proyecto/](docs/analisis-proyecto/) — full product-level analysis: what exists, what's
  missing, and the ongoing improvement roadmap (recommended starting point for understanding current
  state).
- [docs/calendar-feature/](docs/calendar-feature/) — design and implementation of the Calendar module.
- [docs/supabase-sync/](docs/supabase-sync/) — Polar↔Supabase sync and the contract with the Android app.
- [docs/ajustes-docs/](docs/ajustes-docs/) — reference for every Settings section (continuity/backup,
  productivity, account and Polar sync, editor and notifications).
- [docs/ia-skills/especificacion-desarrollo.md](docs/ia-skills/especificacion-desarrollo.md) — what a
  "skill" is in Enki and how the Skills section works.
- [docs/seccion-archivos.md](docs/seccion-archivos.md) — Files module (vault navigation, PDF viewer).
- [docs/funcionalidad-zoom-interfaz.md](docs/funcionalidad-zoom-interfaz.md) — global UI zoom.
- [docs/notificaciones-y-recordatorios.md](docs/notificaciones-y-recordatorios.md) — notification bell,
  Kanban due-date inbox and the Reminders module.
- [docs/sistema-diseno-web.md](docs/sistema-diseno-web.md) — visual design system (typography, color,
  reusable components).
- [docs/agent-docs/](docs/agent-docs/) — targeted technical notes: internationalization, feature inventory,
  packaging/distribution, migrating the session graph off WebGL.

See also `AGENTS.md` for the project's code conventions, [CHANGELOG.md](CHANGELOG.md) for the version
history, and [LICENSE](LICENSE) for the code's terms of use.
