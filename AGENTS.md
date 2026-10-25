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

## Escrituras sobre el vault

El vault es una carpeta de Markdown que el usuario puede tener sincronizada (Syncthing, Dropbox) o abierta
en otro editor. Cualquier escritura sobre el contenido de una nota abierta pasa por
`VaultService.WriteContentChecked`, que compara el disco con la huella de la última lectura/escritura
propia (`NoteDiskStamp`) y aborta si no coinciden, en vez de por `WriteContent` a secas. No añadas caminos
de escritura que se salten esa comprobación. Ver `.docs/project-info/cambios-externos-en-el-vault.md`.

## Notificaciones

Cualquier evento que valga la pena que el usuario vea (fallo de sync, acción destructiva, cambio de
estado importante) se anuncia con `_notificationService.Push(...)` desde el viewmodel/servicio que ya
tenga la referencia — no inventes un mecanismo de aviso paralelo. Ver
`.docs/project-info/notificaciones-y-recordatorios.md`, sección "Cómo extender".

## Diseño visual

Cero excepciones al sistema de diseño de `.docs/project-info/sistema-diseno-web.md`: `CornerRadius="0"` salvo
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
  `.docs/project-info/exportar-vistas-imagen-pdf.md`, `.docs/project-info/aviso-de-version-nueva.md`,
  `.docs/project-info/cambios-externos-en-el-vault.md`, `.docs/project-info/asistente-ia.md`,
  `.docs/project-info/sistema-diseno-web.md` —
  documentación específica por módulo/tema; complementa (no reemplaza) el análisis anterior.
- `.docs/project-info/qa-manual-release-candidate.md` — checklist de QA manual (lector de pantalla,
  render de wikilinks/enlaces rotos, reconexión de Polar Realtime, selector de recurrencia, cambios
  externos en el vault con un sincronizador real); ejecutar antes de cualquier release candidate.
- `.docs/project-info/` — notas técnicas puntuales: internacionalización
  (`internacionalizacion-i18n.md`), inventario de funcionalidades y empaquetado/distribución.

La documentación del proyecto que se comparte en el repositorio vive en `.docs/project-info/`;
`docs/` está ignorada por Git y no es una ruta de referencia para nuevas contribuciones.

Si algo en estos documentos ya no coincide con el código real, el código manda.
