# Enki

*[Read in English](README.en.md)*

Enki es una app de escritorio (Windows/Linux/macOS vía [Avalonia UI](https://avaloniaui.net/), .NET 8,
MVVM con `CommunityToolkit.Mvvm`) pensada como "segundo cerebro" personal: notas markdown con wikilinks,
sesiones de trabajo con grafo visual, tareas locales (Kanban) y en la nube (Polar, sincronizadas vía
Supabase y compartidas con una app Android homónima), un calendario unificado, recordatorios, un centro de
notificaciones y una papelera transversal.

## Módulos

| Módulo | Qué hace |
|---|---|
| Dashboard | Centro de mando: resume el resto de módulos. |
| Notas | Vault de markdown local con wikilinks (`[[Nota]]`). |
| Sesiones | Subespacios de notas con grafo visual de relaciones. |
| Kanban | Tablero de tareas local. |
| Polar | Tareas sincronizadas en la nube (Supabase), compartidas con la app Android. |
| Calendario | Vista unificada de Kanban + Polar + Recordatorios. |
| Recordatorios | Avisos con fecha propios, independientes de Kanban/Polar. |
| Papelera | Recuperación de elementos borrados en Notas y Kanban. |
| Ajustes | Configuración transversal (vault, tema, tipografía, vínculo con Polar). |

## Cómo compilar y correr

Requiere el [.NET 8 SDK](https://dotnet.microsoft.com/download) o superior (`enki/global.json` fija la
versión exacta con `rollForward: latestMajor`). El proyecto principal (`enki.csproj`) apunta a
`net8.0` — si el `dotnet test` falla al lanzar el test host con un mensaje de "You must install or update
.NET" pese a que la compilación funciona, falta el *runtime* 8.0.x en la máquina (no solo el SDK más
nuevo); instálalo o usa `DOTNET_ROLL_FORWARD=LatestMajor dotnet test ...` como workaround puntual.

```bash
# Compilar
dotnet build enki/src/enki/enki.csproj

# Correr
dotnet run --project enki/src/enki/enki.csproj

# Tests
dotnet test enki/tests/enki.Tests/enki.Tests.csproj
```

### Sincronización con Polar (opcional)

La sincronización en la nube (módulo Polar) requiere credenciales de Supabase en
`enki/local.properties` (ignorado por git, nunca se commitea):

```
SUPABASE_URL=<url-del-proyecto-supabase>
SUPABASE_ANON_KEY=<clave-publica-anon>
```

Sin ese archivo, la app funciona igual y todas las funcionalidades que no dependen de Polar quedan
disponibles — `SupabaseConfig.IsConfigured` queda en `false` y las pantallas de Polar se deshabilitan de
forma controlada. Ver [docs/supabase-sync/08-configuracion-y-credenciales.md](docs/supabase-sync/08-configuracion-y-credenciales.md)
para el detalle.

## Documentación

- [docs/analisis-proyecto/](docs/analisis-proyecto/) — análisis integral del proyecto como producto: qué
  existe, qué falta, y el roadmap de mejoras en curso (punto de partida recomendado para entender el
  estado actual).
- [docs/calendar-feature/](docs/calendar-feature/) — diseño e implementación del módulo Calendario.
- [docs/supabase-sync/](docs/supabase-sync/) — sincronización Polar↔Supabase y contrato con la app
  Android.
- [docs/ajustes-docs/](docs/ajustes-docs/) — referencia de cada sección de Ajustes (continuidad/backup,
  productividad, cuenta y sincronización Polar, editor y notificaciones).
- [docs/ia-skills/especificacion-desarrollo.md](docs/ia-skills/especificacion-desarrollo.md) — qué es una
  "skill" en Enki y cómo funciona la sección Habilidades.
- [docs/seccion-archivos.md](docs/seccion-archivos.md) — módulo Archivos (navegación del vault, visor de
  PDF).
- [docs/funcionalidad-zoom-interfaz.md](docs/funcionalidad-zoom-interfaz.md) — zoom global de la interfaz.
- [docs/notificaciones-y-recordatorios.md](docs/notificaciones-y-recordatorios.md) — campana de
  notificaciones, bandeja de vencimientos de Kanban y módulo Recordatorios.
- [docs/sistema-diseno-web.md](docs/sistema-diseno-web.md) — sistema de diseño visual (tipografía, color,
  componentes reutilizables).
- [docs/agent-docs/](docs/agent-docs/) — notas técnicas puntuales: internacionalización, inventario de
  funcionalidades, empaquetado/distribución, migración del grafo de sesiones fuera de WebGL.

Ver también `AGENTS.md` para las convenciones de código que sigue el proyecto, [CHANGELOG.md](CHANGELOG.md)
para el historial de versiones, y [LICENSE](LICENSE) para los términos de uso del código.
