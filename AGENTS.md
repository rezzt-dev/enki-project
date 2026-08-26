# AGENTS.md

Convenciones de código y documentación que ya sigue este proyecto — leer antes de tocar código, para no
tener que inferirlas releyendo todo `enki/src/enki/`.

## Arquitectura

- **MVVM estricto**: modelos POCO en `models/`, lógica de persistencia en `services/`, estado y comandos
  en `viewmodels/` (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` de
  `CommunityToolkit.Mvvm`), vistas declarativas en `views/*.axaml` con code-behind mínimo.
- **Sin contenedor de DI** — composición manual en `viewmodels/shell/MainWindowViewModel.cs`, que
  instancia todos los servicios y cablea los callbacks entre módulos. No introduzcas interfaces
  genéricas, repositorios ni capas de mapeo que el resto del código no usa.
- **Unificar sin duplicar**: cuando una funcionalidad nueva conecta dos módulos, lee/escribe directamente
  sobre la entidad real existente (`KanbanCard`, `PolarTask`, `NoteFile`, `Reminder`…) — nunca crea un
  segundo almacén ni copia el dato a un modelo nuevo. El Calendario (`CalendarAggregationService`) es el
  ejemplo de referencia de este patrón.

## Persistencia

Mismo patrón en todos los servicios (`KanbanService`, `PolarTaskService`, `ReminderService`,
`SettingsService`, `NotificationService`):

- JSON en `%AppData%/Enki/*.json` (`~/.config/Enki/` en Linux) — resuelto vía `AppDataDirectory.Resolve()`
  (`services/AppDataDirectory.cs`), no repetido inline en cada servicio.
- En un build de Debug compilado desde el código fuente, `AppDataDirectory.Resolve()` redirige a
  `src/enki/.devdata/` (en `.gitignore`) en vez de `%AppData%/Enki`, para que compilar desde una copia
  recién clonada arranque siempre en blanco (sin vault, sin Polar vinculado) y no mezcle datos de
  desarrollo con una instalación Release real en la misma máquina. Para resetear el estado de desarrollo,
  borra esa carpeta.
- Constructor que acepta un `storageDirectory` opcional para poder testear sin tocar el disco real del
  desarrollador (ni el real de `%AppData%/Enki` ni el de desarrollo en `.devdata/`).
- Un método `Save()` que serializa y dispara un evento `Action?` de cambio (p. ej.
  `KanbanService.BoardChanged`, `PolarTaskService.DataChanged`, `NotificationService.NotificationAdded`) —
  así otras pantallas se mantienen al día sin necesitar una referencia directa al servicio que originó el
  cambio.

## Notificaciones

Cualquier evento que valga la pena que el usuario vea (fallo de sync, acción destructiva, cambio de
estado importante) se anuncia con `_notificationService.Push(...)` desde el viewmodel/servicio que ya
tenga la referencia — no inventes un mecanismo de aviso paralelo. Ver
`docs/notificaciones-y-recordatorios.md`, sección "Cómo extender".

## Diseño visual

Cero excepciones al sistema de diseño de `docs/sistema-diseno-web.md`: `CornerRadius="0"` salvo
indicadores circulares, reutiliza los estilos de popover/borde ya existentes (`Border.calendar-popover`,
`Border.dropdown-popover`) en vez de crear estilos nuevos, tipografía Space Grotesk/JetBrains Mono ya
empaquetadas en `assets/fonts/`.

## Tests

xUnit en `enki/tests/enki.Tests/`, en la carpeta `Services/` o `ViewModels/` según corresponda, siguiendo
el estilo de los tests ya existentes.

```bash
dotnet test enki/tests/enki.Tests/enki.Tests.csproj
```

## Idioma

Comentarios de código tipo XML-doc y documentación en español (mismo estilo que el resto del repo);
identificadores de código (clases, métodos, variables) en inglés. Respeta la mezcla tal cual está, no la
normalices a un solo idioma.

## Documentación de referencia

- `docs/analisis-proyecto/` — análisis del proyecto como producto (`analisis-deficiencias-proyecto.md`) y
  el roadmap de mejoras en curso, fase a fase (`roadmap.md`).
- `docs/calendar-feature/`, `docs/supabase-sync/`, `docs/ajustes-docs/`,
  `docs/ia-skills/especificacion-desarrollo.md`, `docs/seccion-archivos.md`,
  `docs/vista-simplificada-notas.md`,
  `docs/funcionalidad-zoom-interfaz.md`, `docs/notificaciones-y-recordatorios.md`,
  `docs/sistema-diseno-web.md` — documentación específica por módulo/tema; complementa (no reemplaza) el
  análisis anterior.
- `docs/agent-docs/` — notas técnicas puntuales: internacionalización (`internacionalizacion-i18n.md`),
  inventario de funcionalidades, empaquetado/distribución.

Si algo en estos documentos ya no coincide con el código real, el código manda.
