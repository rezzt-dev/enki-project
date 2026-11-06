# ENKI-PROJECT: REPOSITORY GUIDE

avalonia desktop workspace for markdown notes, kanban, calendar and local knowledge management. this guide covers the repository; nested agent instructions apply within their own folders.

## PROJECT OVERVIEW

| property | verified value |
| --- | --- |
| repository | `rezzt-dev/enki-project` |
| active source | `enki/` |
| development base for this change | `beta-version-1.0.0` |
| remote default branch | `main` |
| version source | `enki/src/enki/enki.csproj`; `enki/tests/enki.Tests/enki.Tests.csproj`; `enki/tests/enki.UITests/enki.UITests.csproj` |
| continuous integration | `.github/workflows/ci.yml`, `.github/workflows/release.yml`, `.github/workflows/web.yml` |

## TECHNOLOGY STACK

- .net project files declare target frameworks and package references; preserve platform-specific requirements

## PROJECT STRUCTURE

- `changelog.md`
- `enki/`
- `license.md`
- `readme.en.md`
- `readme.md`
- `scripts/`

management documentation lives in `.docs/manager.md`, `.docs/-github-guides/` and `.docs/repo-audit-and-fix.md`. keep this file and `claude.md` consistent.

## BUILD, RUN AND TEST COMMANDS

for application changes, run these checks from `enki/`:

```bash
dotnet test tests/enki.Tests/enki.Tests.csproj
dotnet test tests/enki.UITests/enki.UITests.csproj
```

## CODE STYLE AND ARCHITECTURAL DECISIONS

preserve mvvm and manual service composition; use vaultservice.writecontentchecked for note writes; keep test storage isolated from real vaults. follow the existing formatter and naming of the changed module. verify manifest versions when dependencies change.

## TESTING STRATEGY

documentation-only changes require `git diff --check`, existing-path checks and a review of the staged file list. run module checks for application changes, with disposable data. report unavailable tooling and failed checks; never claim a check passed without running it.

## SECURITY AND DEPLOYMENT

keep credentials, local configuration, signing material and user data out of commits. do not print secret values while auditing. inspect configuration names and code references instead.

existing legal and community files: `license.md`. when changing dependencies, permissions, storage or network services, review those files against the implementation. record missing policies as audit work; do not invent licence terms or ownership decisions.

deployment, signing, store publication, migrations against hosted databases and repository settings require their own explicit request. this documentation task prepares management instructions and pull requests.

## COMMON TASKS

1. read the relevant module and its nearest agent instructions
2. inspect the working tree and choose the smallest change that solves the request
3. update the affected documentation and changelog with evidence from the diff
4. run checks appropriate to the changed module and report their actual results
5. for git operations, follow the manager and the digest below

## EXISTING PROJECT CONVENTIONS

the following project-specific context is retained from the previous guide. paths are relative to the active module when stated. for git operations, the manager and final digest supersede previous workflow examples.

## AGENTS.MD

convenciones de código y documentación que ya sigue este proyecto — leer antes de tocar código, para no
tener que inferirlas releyendo todo `enki/src/enki/`.

## ARQUITECTURA

- **mvvm estricto**: modelos poco en `models/`, lógica de persistencia en `services/`, estado y comandos
  en `viewmodels/` (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` de
  `CommunityToolkit.Mvvm`), vistas declarativas en `views/*.axaml` con code-behind mínimo.
- **sin contenedor de di** — composición manual en `viewmodels/shell/MainWindowViewModel.cs`, que
  instancia todos los servicios y cablea los callbacks entre módulos. no introduzcas interfaces
  genéricas, repositorios ni capas de mapeo que el resto del código no usa.
- **unificar sin duplicar**: cuando una funcionalidad nueva conecta dos módulos, lee/escribe directamente
  sobre la entidad real existente (`KanbanCard`, `PolarTask`, `NoteFile`, `Reminder`…) — nunca crea un
  segundo almacén ni copia el dato a un modelo nuevo. el calendario (`CalendarAggregationService`) es el
  ejemplo de referencia de este patrón.

## PERSISTENCIA

mismo patrón en todos los servicios (`KanbanService`, `PolarTaskService`, `ReminderService`,
`SettingsService`, `NotificationService`):

- json en `%AppData%/Enki/*.json` (`~/.config/Enki/` en linux) — resuelto vía `AppDataDirectory.Resolve()`
  (`services/AppDataDirectory.cs`), no repetido inline en cada servicio.
- en un build de debug compilado desde el código fuente, `AppDataDirectory.Resolve()` redirige a
  `src/enki/.devdata/` (en `.gitignore`) en vez de `%AppData%/Enki`, para que compilar desde una copia
  recién clonada arranque siempre en blanco (sin vault, sin polar vinculado) y no mezcle datos de
  desarrollo con una instalación release real en la misma máquina. para resetear el estado de desarrollo,
  borra esa carpeta.
- constructor que acepta un `storageDirectory` opcional para poder testear sin tocar el disco real del
  desarrollador (ni el real de `%AppData%/Enki` ni el de desarrollo en `.devdata/`).
- un método `Save()` que serializa y dispara un evento `Action?` de cambio (p. ej.
  `KanbanService.BoardChanged`, `PolarTaskService.DataChanged`, `NotificationService.NotificationAdded`) —
  así otras pantallas se mantienen al día sin necesitar una referencia directa al servicio que originó el
  cambio.

## ESCRITURAS SOBRE EL VAULT

el vault es una carpeta de markdown que el usuario puede tener sincronizada (syncthing, dropbox) o abierta
en otro editor. cualquier escritura sobre el contenido de una nota abierta pasa por
`VaultService.WriteContentChecked`, que compara el disco con la huella de la última lectura/escritura
propia (`NoteDiskStamp`) y aborta si no coinciden, en vez de por `WriteContent` a secas. no añadas caminos
de escritura que se salten esa comprobación. ver `.docs/project-info/cambios-externos-en-el-vault.md`.

## NOTIFICACIONES

cualquier evento que valga la pena que el usuario vea (fallo de sync, acción destructiva, cambio de
estado importante) se anuncia con `_notificationService.Push(...)` desde el viewmodel/servicio que ya
tenga la referencia — no inventes un mecanismo de aviso paralelo. ver
`.docs/project-info/notificaciones-y-recordatorios.md`, sección "cómo extender".

## DISEÑO VISUAL

cero excepciones al sistema de diseño de `.docs/project-info/sistema-diseno-web.md`: `CornerRadius="0"` salvo
indicadores circulares, reutiliza los estilos de popover/borde ya existentes (`Border.calendar-popover`,
`Border.dropdown-popover`) en vez de crear estilos nuevos, tipografía space grotesk/jetbrains mono ya
empaquetadas en `assets/fonts/`.

## TESTS

xunit en `enki/tests/enki.Tests/`, en la carpeta `Services/` o `ViewModels/` según corresponda, siguiendo
el estilo de los tests ya existentes.

```bash
dotnet test enki/tests/enki.Tests/enki.Tests.csproj
```

## IDIOMA

comentarios de código tipo xml-doc y documentación en español (mismo estilo que el resto del repo);
identificadores de código (clases, métodos, variables) en inglés. respeta la mezcla tal cual está, no la
normalices a un solo idioma.

## DOCUMENTACIÓN DE REFERENCIA

- `.docs/project-info/analisis-proyecto/` — análisis del proyecto como producto (`analisis-deficiencias-proyecto.md`) y
  el roadmap de mejoras en curso, fase a fase (`roadmap.md`).
- `.docs/project-info/calendar-feature/`, `.docs/project-info/supabase-sync/`,
  `.docs/project-info/ajustes-docs/`, `.docs/project-info/ia-skills/especificacion-desarrollo.md`,
  `.docs/project-info/seccion-archivos.md`, `.docs/project-info/vista-simplificada-notas.md`,
  `.docs/project-info/plantillas-de-notas.md`, `.docs/project-info/notas-diarias.md`,
  `.docs/project-info/imagenes-en-notas.md`, `.docs/project-info/tablas-en-notas.md`,
  `.docs/project-info/transclusion-de-notas.md`, `.docs/project-info/formulas-y-diagramas.md`,
  `.docs/project-info/busqueda-texto-completo.md`, `.docs/project-info/logo-personalizado.md`,
  `.docs/project-info/funcionalidad-zoom-interfaz.md`,
  `.docs/project-info/notificaciones-y-recordatorios.md`,
  `.docs/project-info/exportar-vistas-imagen-pdf.md`, `.docs/project-info/exportar-notas-pdf.md`,
  `.docs/project-info/aviso-de-version-nueva.md`,
  `.docs/project-info/cambios-externos-en-el-vault.md`, `.docs/project-info/asistente-ia.md`,
  `.docs/project-info/grafo-sesiones-3d.md`, `.docs/project-info/grafo-compatibilidad-nativa.md`,
  `.docs/project-info/sistema-diseno-web.md` —
  documentación específica por módulo/tema; complementa (no reemplaza) el análisis anterior.
- `.docs/project-info/qa-manual-release-candidate.md` — checklist de qa manual (lector de pantalla,
  render de wikilinks/enlaces rotos, reconexión de polar realtime, selector de recurrencia, cambios
  externos en el vault con un sincronizador real); ejecutar antes de cualquier release candidate.
- `.docs/project-info/` — notas técnicas puntuales: internacionalización
  (`internacionalizacion-i18n.md`), inventario de funcionalidades y empaquetado/distribución.

la documentación del proyecto que se comparte en el repositorio vive en `.docs/project-info/`;
`docs/` está ignorada por git y no es una ruta de referencia para nuevas contribuciones.

si algo en estos documentos ya no coincide con el código real, el código manda.

## COMMIT, PR, MERGE & RELEASE GUIDELINES

read `.docs/manager.md` first, then the selected guide in `.docs/-github-guides/`. these files are versioned; this digest also works without opening them.

| request | guide |
| --- | --- |
| commit title, body and changelog | `commit-guide.md`, `changelog-guide.md` |
| pull request or merge | `pr-merge-guide.md`, `branching-guide.md` |
| version and release | `release-guide.md`, `changelog-guide.md` |
| permissions and sensitive data | `agent-rules.md` |

- inspect `git status`, both diffs and recent history before describing changes
- use `<prefix> | <imperative summary>` with `update`, `fix`, `hotfix`, `changes` or `changelog`; keep titles within 72 characters
- write commit bodies as 2 to 6 markdown bullets, one logical change per line, with literal paths in backticks
- never add authorship footers, automated attribution, signatures or generation notices; preserve the configured git identity
- update `changelog.md` under `## SIN PUBLICAR / UNRELEASED` in matching `### CASTELLANO` and `### ENGLISH` sections
- use the existing changelog category vocabulary and preserve language parity; describe only changes in the actual diff
- summarize pull requests in 1 to 3 bullets per category and language, at most 110 characters per bullet
- documentation and repository-management work targets `beta-version-1.0.0`; the remote default is `main`; do not silently promote development work to the default branch
- the authorization for a requested action remains valid throughout that task; creating a pull request includes pushing its branch, but does not authorize merging or publishing
- use merge commits when a merge is requested; never force-push, rewrite published history or remove branches without an explicit instruction
- releases use the approved version and an immutable tag on the verified release commit, with english notes from the closed changelog; do not create a release for documentation work
- deliver requested titles, bodies and changelog fragments in separate code blocks; report executed actions with their commit hash or pull request link
