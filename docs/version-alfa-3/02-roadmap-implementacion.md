# Roadmap de implementación — Enki alfa-3

> Deriva de [01-analisis-detallado.md](01-analisis-detallado.md); cada tarea cita el módulo/hallazgo del
> que procede para volver al detalle completo (evidencia `archivo:línea`, escenario de fallo) sin
> repetirlo aquí. Mismo criterio que el roadmap anterior
> (`docs/agent-docs/analisis-proyecto/roadmap.md`): cada fase es entregable de forma independiente, deja
> la app usable y no hace falta completar el roadmap entero para obtener valor. Fases numeradas de forma
> independiente a las de aquel roadmap (todas cerradas) para no confundir ambos historiales.

## Cómo usar este roadmap

Marcar aquí qué fase está en curso o completada según se implemente:

- [x] Fase 0 — Bugs críticos nuevos (bloqueantes)
- [x] Fase 1 — Seguridad y pérdida de datos en sesiones protegidas
- [x] Fase 2 — Consistencia de UX transversal (`ModalOverlayHost` y confirmaciones de borrado)
- [x] Fase 3 — Corrección de estados "vencido"/"completado" en Calendario, Dashboard y Recordatorios
- [x] Fase 4 — Deuda técnica y duplicación
- [x] Fase 5 — Cobertura de tests en funcionalidad nueva sin probar
- [x] Fase 6 (opcional) — Paridad de producto Kanban↔Polar
- [x] Fase 7 (opcional) — Accesibilidad puntual restante
- [ ] Fase 8 (opcional) — Rendimiento puntual y mejoras menores

---

## Fase 0 — Bugs críticos nuevos (bloqueante, esfuerzo bajo-medio)

Los tres hallazgos que revientan la app o rompen una funcionalidad anunciada de forma silenciosa. Sin
dependencias entre sí; pueden repartirse en paralelo.

- [x] **Capturar `SessionLockedException` en todo el camino de Notas/Editor**, no solo en Archivos.
      Mínimo: `EditorViewModel.Save()`, `NotesViewModel.RestoreOpenTabs()` y
      `NotesViewModel.TryReadContentForExport`. Mismo criterio que ya usa `FilesViewModel
      .IsSelectedSessionLocked`: comprobar el estado de bloqueo *antes* de leer, en vez de leer y capturar
      el fallo. (análisis §4.1, hallazgo crítico) — **prioridad máxima**, es un crash reproducible tanto al
      arrancar (pestaña restaurada de una sesión que arranca bloqueada) como en caliente (bloquear una
      sesión con una pestaña de sus notas todavía abierta).
      *Depende de*: la Fase 0 también debe resolver que bloquear una sesión notifique/cierre las pestañas
      abiertas sobre sus notas (ver Fase 1) para que la corrección sea completa, no solo defensiva.
- [x] **Corregir el comando de barra oblicua `/tabla`**: añadir la clave de recurso
      `Editor_SlashCommand_Table_InsertText` (con la plantilla de tabla Markdown real) a los 7 `.resx`, y
      revisar `CursorOffset = 53` contra la longitud real del texto insertado. Añadir un test que verifique
      que `SlashCommand.InsertText` resuelve a texto no-clave para cada entrada de
      `SlashCommandsViewModel.AllCommands`, para que una clave de recurso faltante en cualquier comando
      futuro falle en CI en vez de en producción. (análisis §4.2, hallazgo crítico)
- [x] **Corregir `VaultService.LooksLikeExistingVaultRoot`** para reconocer un vault cuyo contenido vive
      solo en subcarpetas — extender la comprobación a `SearchOption.AllDirectories`, o (más robusto)
      escribir un archivo marcador (`.enki/`) una sola vez en la creación del vault y comprobar su
      presencia en vez de inferir la estructura por convención. Añadir un test con un vault sintético cuyas
      notas están exclusivamente en subcarpetas. (análisis §4.5, hallazgo crítico) — de las tres tareas de
      esta fase, la única con riesgo real de que un usuario existente pierda de vista (que no pierda) sus
      datos al abrir Enki en una máquina nueva.

**Criterio de aceptación**: los tres escenarios de repro descritos en el análisis (§4.1, §4.2, §4.5) dejan
de reproducirse; suite completa en verde.

---

## Fase 1 — Seguridad y pérdida de datos en sesiones protegidas (esfuerzo medio, impacto alto)

Cierra el resto de huecos de la funcionalidad de cifrado de sesiones más allá del crash de la Fase 0.
Tiene sentido secuenciarla justo después porque toca el mismo código.

- [x] **Bloquear una sesión debe invalidar/cerrar sus pestañas de editor abiertas** — al menos notificar
      claramente que el contenido visible pertenece a una sesión ahora bloqueada y bloquear el
      autoguardado sobre esas pestañas hasta que se desbloquee de nuevo; idealmente, cerrarlas. (análisis
      §4.1, Alta) — resuelve la causa raíz de uno de los dos escenarios de crash de la Fase 0, no solo el
      síntoma.
- [x] **Cablear `SessionEncryptionService.LockAll()` al cierre de la app** (`MainWindow.axaml.cs`, junto al
      resto de volcados en `Closing`) — hoy no se llama desde ningún sitio pese a que su propio comentario
      dice que sí. (análisis §4.1, Alta)
- [x] **Limpiar archivos temporales descifrados al cerrar la app** (barrido de `enki-dec-*` en el directorio
      temporal del SO) y, si es viable a bajo coste, también al arrancar (por si el cierre anterior fue un
      crash). Endurecer los permisos del archivo temporal en su creación (`UnixFileMode` restrictivo en
      Linux/macOS). (análisis §4.1, Alta/Media)
- [x] **Migrar los 4 diálogos de Sesiones (crear, códigos de recuperación, desbloquear/olvidé mi
      contraseña, borrar) a `ModalOverlayHost`** — puede hacerse aquí de forma aislada o como parte del
      bloque mecánico de la Fase 2; dado que son los diálogos de mayor sensibilidad de seguridad del
      release, se recomienda no esperar a la Fase 2 completa para estos cuatro en concreto. (análisis §4.1,
      Alta)
- [x] Mover las 6 derivaciones PBKDF2 de `ConfirmCreateSession` (y la derivación única de
      `ConfirmUnlock`/`ConfirmPasswordReset`) fuera del hilo de UI (`Task.Run` + indicador de carga),
      siguiendo el mismo patrón que el resto de la app ya usa para trabajo de disco. (análisis §4.1, Media)
- [x] Auto-limpiar el portapapeles unos segundos después de copiar los códigos de recuperación; confirmar
      (y si hace falta, reforzar) que el diálogo de códigos de recuperación deja explícito que perder la
      contraseña y los 5 códigos a la vez es irrecuperable. (análisis §4.1, Media)
- [x] Añadir `AutomationProperties.Name` a los 6 campos de contraseña/código nuevos en
      `SessionsView.axaml`. (análisis §4.1, Baja — puede agruparse con la Fase 7)
- [x] Añadir tests para `GraphGroupFilterItemViewModel`/`RebuildGraphGroupFilters`/`PublishGraph` (sin
      ningún test hoy) y para la resolución de wikilinks ambiguos entre sesiones distintas
      (`ComputeGlobalWikiLinkEdges`). (análisis §4.1, Media — puede moverse a la Fase 5 si el esfuerzo no
      encaja aquí)
- [x] Eliminar `VaultService.WriteTextFile` (código muerto, duplicado exacto de `WriteFileAtomic`).
      (análisis §4.1/§4.2, Media)

**Dependencias**: ninguna respecto a otras fases, salvo que el punto de "bloquear cierra pestañas" es
prerrequisito conceptual para dar la Fase 0 por completamente cerrada.

---

## Fase 2 — Consistencia de UX transversal: `ModalOverlayHost` y confirmaciones de borrado (esfuerzo medio, impacto alto)

El hallazgo más repetido de toda la auditoría (cuatro frentes independientes lo señalaron) — ver
[01-analisis-detallado.md §6](01-analisis-detallado.md#6-hallazgo-transversal-modaloverlayhost-con-rollout-incompleto).

- [x] **Migrar los diálogos modales restantes a `ModalOverlayHost`**: `KanbanView.axaml`,
      `TrashView.axaml`, `HabitsView.axaml`, `SkillsView.axaml`, `RemindersView.axaml`,
      `CalendarView.axaml`, `SettingsView.axaml` (los 4 de Sesiones, si no se hicieron ya en la Fase 1).
      Es un cambio mecánico por vista (sustituir `Border Classes="modal-overlay"` por
      `<controls:ModalOverlayHost>` siguiendo el ejemplo ya funcionando en `NotificationsView.axaml`) —
      candidato ideal para hacerse en un único PR de barrido, o para confirmar primero con quien introdujo
      el control si ya hay un plan de migración por fases antes de duplicar el esfuerzo. (análisis §6)
      — 22 diálogos migrados en los 7 `.axaml` listados (más los de Sesiones, ya migrados en la Fase 1).
- [x] **Aplicar `ConfirmBeforeDelete` a `FilesViewModel.DeleteFile` y `NotesViewModel.DeleteNote`**, igual
      que ya hacen Hábitos/Polar/Skills/Kanban/Calendario. (análisis §4.5, Alta) — de paso, se aplicó el
      mismo patrón request/cancel/confirm al botón "eliminar" propio de la barra de herramientas del editor
      (`EditorViewModel.Delete`), un segundo punto de entrada al mismo borrado que la auditoría no había
      enumerado por separado pero comparte el mismo hallazgo.
- [x] Añadir una categoría de papelera real para tareas/listas Polar borradas (mismo patrón de retención
      que Kanban), o al menos evaluar y documentar por qué Polar queda deliberadamente fuera de la papelera
      si la decisión de producto es esa — hoy no hay ninguna nota que lo explique. (análisis §4.3, Alta) —
      se implementó la categoría "Tareas Polar" en la Papelera (`PolarTaskService.RestoreList`/
      `RestoreTask`, `TrashViewModel.PolarLists`/`PolarTasks`), pero deliberadamente solo con restaurar, sin
      borrado permanente individual ni "vaciar categoría": una fila Polar recién borrada suele seguir
      `Dirty` (su tombstone aún no llegó a Supabase), así que purgarla en local antes de ese push la haría
      "resucitar" en el siguiente pull — mismo riesgo ya documentado para Recordatorios. El purgado real por
      antigüedad, una vez sincronizado, queda para la Fase 4 (ver ese punto de purga de tombstones).
- [x] Añadir un indicador visual explícito en la categoría "Recordatorios" de la Papelera de por qué no
      tiene botón de "borrar para siempre" (o replantear si debería tenerlo una vez sincronizado).
      (análisis §4.3, Baja)

**Dependencias**: independiente de las Fases 0-1 salvo por los 4 diálogos de Sesiones ya cubiertos ahí.

---

## Fase 3 — Corrección de estados "vencido"/"completado" (esfuerzo bajo-medio, impacto alto)

Todos estos hallazgos comparten causa raíz (el estado "completado" de Kanban no se propaga a las vistas
agregadas) o son del mismo tipo de bug de "estado visual no sincronizado con el estado real". Buen bloque
para atacar junto porque toca los mismos archivos (`CalendarAggregationService`, `DashboardViewModel`).

- [x] **`CalendarAggregationService.BuildItems`/`BuildUnscheduledItems`**: derivar `IsCompleted` de
      `KanbanCard.CompletedAtUtc` en vez de fijarlo a `false`. (análisis §4.4, Alta — el hallazgo con más
      radio de impacto de esta fase, afecta a Calendario, Dashboard y la bandeja de vencimientos del shell
      a la vez)
- [x] **`ReminderInboxViewModel.Reload`**: excluir explícitamente la columna "Hecho" (o depender del mismo
      `IsCompleted` corregido arriba, si se unifica el criterio). (análisis §4.4, Alta) — se unificó el
      criterio: filtra por `KanbanCard.CompletedAtUtc is null` en vez de por nombre de columna, así no
      depende de que la columna "Hecho" se siga llamando exactamente así.
- [x] **`DashboardViewModel.LoadTodayItems`**: acotar el backlog considerado (p. ej. limitar a los últimos
      N días de antigüedad, o separar visualmente "vencido antiguo" de "hoy" en vez de competir por el
      mismo cupo de 5 elementos). (análisis §4.4, Alta) — se acotó la ventana de backlog a 30 días
      (`TodayItemsBacklogWindow`) y, dentro de esa ventana, los ítems que vencen hoy siempre ocupan el cupo
      antes que el backlog vencido, que solo rellena las plazas sobrantes empezando por el más reciente.
- [x] **`ReminderListItemViewModel.IsOverdue`**: excluir recordatorios completados, replicando el criterio
      ya correcto de `CalendarItemViewModel.IsPastDue`. (análisis §4.4, Media)
- [x] Añadir tests de regresión para los cuatro puntos anteriores (ninguno tiene cobertura hoy).

**Dependencias**: ninguna respecto a otras fases.

---

## Fase 4 — Deuda técnica y duplicación (esfuerzo bajo-medio, impacto en mantenibilidad)

Ninguno de estos hallazgos es visible para el usuario final; se agrupan porque son baratos de resolver y
reducen el riesgo de que un futuro cambio se aplique a medias (una sola de las dos copias) como ya ha
pasado con el selector de rejilla de mes.

- [x] `PolarTaskEditorViewModel` debe reutilizar `MonthGridCalculator` en vez de reimplementar la misma
      matemática de rejilla. (análisis §4.3, Media)
- [x] Purgar tombstones de Polar (`IsDeleted=true`) con el mismo criterio de antigüedad que ya usan Kanban
      (30 días) y Recordatorios. (análisis §4.3, Media) — `PolarTaskService.PurgeOldTombstones`, llamado
      desde el constructor igual que `KanbanService`/`ReminderService`; test de regresión en
      `PolarTaskServiceTests`.
- [x] Unificar el selector de hora límite Kanban/Polar (mismo control, mismo tipo `int` para
      `DueHour`/`DueMinute`). (análisis §4.3, Media) — el popover de Polar en `KanbanView.axaml` pasó del
      `NumericUpDown` (decimal) al mismo control "time-stepper" que ya usaba Kanban, con los mismos
      comandos Increment/DecrementDueHour/Minute en `PolarTaskEditorViewModel`.
- [x] Extraer la lógica de puntuación difusa compartida entre `CommandPaletteViewModel` y
      `QuickSwitcherViewModel` a un helper común. (análisis §4.5, Baja) — `TitleFuzzyScorer.ScoreTitle` en
      `viewmodels/common/`.
- [x] Eliminar uno de los dos `PdfRenderHelperTests.cs` duplicados (conservar el de `ViewModels/`, más
      completo, o fusionar el caso distinto del de `Services/` antes de borrar el segundo). (análisis §4.5,
      Baja) — se fusionó el caso del PDF de dos páginas generado con SkiaSharp como test adicional en
      `ViewModels/PdfRenderHelperTests.cs` y se borró `Services/PdfRenderHelperTests.cs`.
- [x] Dividir `SettingsViewModel.cs` en archivos parciales por sección, siguiendo el mismo patrón ya
      aplicado a `MainWindowViewModel` (`.Callbacks.cs`/`.Composition.cs`/`.CommandPalette.cs`). (análisis
      §4.5, Baja-Media — el de mayor esfuerzo de esta fase, tratar como tarea de fondo sin urgencia) —
      dividido en 9 archivos parciales por sección de Ajustes (`.SidebarSections.cs`, `.Appearance.cs`,
      `.Editor.cs`, `.Notifications.cs`, `.Calendar.cs`, `.Account.cs`, `.Shortcuts.cs`, `.Dashboard.cs`,
      `.DangerZone.cs`); el archivo principal conserva el constructor, los campos compartidos y las
      secciones General/Acerca de. Sin cambios de comportamiento — build y los 915 tests en verde.
- [x] Corregir el comentario de clase de `HexColorUtility` para que no afirme soporte de `#ARGB`/
      `#AARRGGBB` que el código no implementa. (análisis §4.5, Media — trivial)
- [x] Corregir/eliminar el comentario desfasado sobre exportación a PDF/impresión de notas en
      `NoteExportRenderer.cs`/`Program.cs`, o implementar la funcionalidad que describen (ver Fase 6).
      (análisis §4.2, Media) — corregidos los tres comentarios (`NoteExportRenderer`, `NoteExportService`,
      `Program.cs`) para dejar claro que hoy solo existe exportación a HTML, sin vista de impresión ni
      WebView/PDF propios; implementarlo de verdad queda en la Fase 6.

**Dependencias**: ninguna. Cajón de sastre deliberado, cada punto es independiente.

---

## Fase 5 — Cobertura de tests en funcionalidad nueva sin probar (esfuerzo medio, impacto en riesgo de regresión)

Todas las funcionalidades de producto añadidas desde la última auditoría que se enviaron sin (o casi sin)
tests propios. Priorizar por lo que ya tiene un bug conocido de la Fase 0 (para bloquear que regrese) y por
tamaño de superficie sin probar.

- [x] Tests para `PolarTaskManagementViewModel` (528 líneas, ~30 comandos, hoy solo 2 tests) — como mínimo
      crear/editar/borrar lista y tarea, completar tarea, cambio de lista seleccionada, filtro de tags.
      (análisis §4.3, Media) — 16 tests nuevos cubriendo crear/renombrar/borrar lista (con y sin
      `ConfirmBeforeDelete`), crear/editar/borrar/completar tarea, cambio de lista seleccionada y filtro de
      etiquetas (con y sin selección).
- [x] Tests para `SlashCommandsViewModel`/`SlashCommandItemViewModel` — el que habría atrapado el bug de
      `/tabla` de la Fase 0 antes de que llegara a producción. (análisis §4.2, Baja — pero alto valor de
      prevención) — la cobertura de la Fase 0 (resolución de `InsertText`/`CursorOffset` contra
      `Strings.resx`) ya existía; se añadió cobertura del propio ViewModel (filtro por clave/etiqueta,
      apertura/reset, navegación circular con `MoveUp`/`MoveDown`, `ConfirmSelected`/`Select` disparando
      `CommandSelected` y cerrando el popover).
- [x] Tests para `GraphGroupFilterItemViewModel` y el resto de la lógica de filtro de grupos del grafo de
      sesiones. (análisis §4.1, Media) — la cobertura principal ya se había añadido en la Fase 1
      (`SessionsViewModelGraphFilterTests`); se añadieron 2 casos más (reactivar un filtro restaura sus
      nodos y limpia `IsGraphFiltered`; un vault sin sesiones, solo notas sueltas, genera una única fila de
      filtro para el grupo raíz).
- [x] Tests para `CombinedDueDate`/`MaxDurationMinutes`/`OnHasDueTimeChanged` en
      `KanbanCardEditorViewModel`. (análisis §4.3, Baja) — ambos son privados; se ejercitan vía `BuildCard`
      (combinación fecha+hora) y vía el clamp de `DurationMinutes` al acercar `DueHour`/`DueMinute` a
      medianoche.
- [x] Tests de énfasis/negrita/cursiva en `MarkdownLineParserTests.cs` (hoy solo cubre wikilinks/tablas/
      encabezados/listas) — de paso, decidir y fijar el comportamiento correcto para `snake_case` (hallazgo
      §4.2, Media) antes o después de escribir el test, según se decida corregir el parser en esta fase o
      solo documentar el comportamiento actual. — se corrigió `MarkdownLineParser` para que `_` (a
      diferencia de `*`) no abra ni cierre énfasis entre dos caracteres alfanuméricos (regla de CommonMark),
      así un identificador `snake_case` ya no se parsea parcialmente en cursiva; 13 tests nuevos cubren
      negrita/cursiva/negrita+cursiva con `*` y `_`, marcador sin cerrar, y el propio caso `snake_case`.
- [x] Test para `StatisticsViewModel.HasEnoughData` que cubra el caso "solo hábitos, sin notas/tareas".
      (análisis §4.4, Media) — el test expuso el bug ya documentado en el análisis (`HasEnoughData` ignoraba
      `HabitCompletionPercent`); se corrigió para que un usuario que solo usa Hábitos también salga del
      estado vacío de Estadísticas.
- [x] Test de regresión para el límite de 366 iteraciones de ocurrencias fantasma con una tarea recurrente
      diaria muy antigua. (análisis §4.4, Media) — documenta el comportamiento actual (conocido, no
      corregido en esta fase): una tarea diaria sin marcar desde hace más de 366 días desaparece en
      silencio del Calendario; un segundo test fija el límite justo por debajo del tope para que un cambio
      futuro al algoritmo de avance lo note explícitamente.
- [x] Ampliar `enki.UITests` (hoy 10 casos headless para 20 vistas) — al menos una pasada de humo por vista
      que abra cada diálogo modal nuevo (los de Sesiones en particular) para detectar regresiones visuales
      básicas sin depender de una pasada manual. — se añadieron 10 tests nuevos (Archivos: renderizado +
      diálogo de borrado; Recordatorios: formulario de creación + "vaciar todo"; Papelera: renderizado +
      diálogo de vaciar categoría), subiendo la suite headless de 10 a 20 casos. **Sesiones queda fuera**:
      se intentó (los 4 diálogos, con los mismos comandos que `SessionsViewModelEncryptionTests` ya cubre a
      nivel de ViewModel) pero construir un `SessionsViewModel` real bajo `Avalonia.Headless` cuelga el
      proceso de test de forma reproducible — el método de test en sí termina casi al instante (confirmado
      con logging a disco), pero el proceso `dotnet test` nunca vuelve a imprimir salida ni sale, incluso
      esperando más de 150s, y el cuelgue persiste igual construyendo el ViewModel fuera del hilo con
      `HeadlessApp.EnsureInitialized()` ya llamado (se descartó por tanto que sea el `SynchronizationContext`
      de Avalonia). No reproducible con ningún otro ViewModel de la suite (todos síncronos en su
      constructor); `SessionsViewModel` es el único cuyo constructor dispara `Reload()` con E/S asíncrona
      real (`Task.Run` encadenado). Merece una investigación aparte con herramientas de diagnóstico reales
      (dump de hilos) en vez de más intentos a ciegas — la cobertura de los 4 diálogos de Sesiones a nivel
      de ViewModel (`SessionsViewModelEncryptionTests`) ya existía y sigue siendo válida mientras tanto.
      De paso, se corrigió `enki.UITests.csproj` (le faltaba `<RollForward>LatestMajor</RollForward>`,
      presente en `enki.Tests.csproj`/`enki.csproj`) — sin él, la suite no podía ni arrancar en un entorno
      sin el runtime exacto de .NET 8.0.0 instalado.

**Dependencias**: ninguna estricta; tiene sentido secuenciar los tests de `/tabla` y del filtro de grupos
justo después de que la Fase 0/1 los toque, para no escribir el test contra un comportamiento que va a
cambiar.

---

## Fase 6 (opcional) — Paridad de producto Kanban↔Polar (esfuerzo alto, impacto en coherencia de producto)

Ninguno de estos hallazgos es un bug de datos; son decisiones de producto sobre si Polar debe seguir
sintiéndose como "el mismo tablero en la nube" o si su superficie más reducida es una limitación aceptada.
Requiere una decisión explícita antes de implementar, no solo una corrección mecánica. Decisiones tomadas
por el propietario del proyecto al abordar esta fase — ver el detalle de cada una junto a su punto.

- [x] Evaluar y, si se decide que sí, implementar drag&drop para reordenar listas/tareas Polar, igual que
      Kanban. (análisis §4.3, Alta — el de mayor esfuerzo) — **decisión: no implementar**, se documenta como
      limitación aceptada. Polar mantiene el orden de creación (`OrderIndex` fijado una sola vez); reordenar
      libremente un tablero requiere Kanban.
- [x] Evaluar y, si se decide que sí, dar a la prioridad de una tarea Polar el mismo ciclo de un clic que
      ya tiene Kanban. (análisis §4.3, Media) — **decisión: no implementar**, se documenta como limitación
      aceptada junto con el punto anterior. Cambiar la prioridad de una tarea Polar sigue exigiendo abrir su
      editor completo.
- [x] Añadir atajos de teclado a la vista de "Gestión de tareas" Polar, espejando los de Kanban. (análisis
      §4.3, Baja) — **decisión: no implementar**, misma limitación aceptada que los dos puntos anteriores.
- [x] Añadir "Mostrar en el sistema" a Archivos (`FileCardViewModel`), espejando lo ya hecho para Notas —
      trivial dado que `FolderOpener.RevealFile` ya existe. (análisis §4.5, Media) — añadido
      `FileCardViewModel.RevealInSystemCommand` (mismo patrón que `NoteCardViewModel`) y su entrada de menú
      contextual en `FilesView.axaml`, con `Files_RevealInSystemMenuItem` en los 7 `.resx`.
- [x] Decidir y documentar (o implementar) si "Ocultar sección X de la barra lateral" también debería
      esconder los resultados de esa sección en Búsqueda global/Paleta de comandos, o si el comportamiento
      actual ("ocultar es solo declutter, no sandbox") es la decisión de producto correcta. (análisis §4.5,
      Media) — **decisión: mantener el comportamiento actual** (ocultar es solo declutter de la barra
      lateral, nunca sandbox de contenido) y documentarlo explícitamente: nota nueva bajo la descripción de
      "Secciones de la barra lateral" en Ajustes (`Settings_General_Sidebar_SearchNote`, los 7 `.resx`).
- [x] Corregir el guardarraíl de "al menos una sección visible" para que cuente sobre las mismas 9
      secciones que realmente pueblan `NavItems`, no sobre las 12 flags totales. (análisis §4.5, Media) —
      `SettingsViewModel.CountEnabledSidebarSections` ya no cuenta Recordatorios/Notificaciones/Estadísticas
      (los 3 flags que no pueblan `NavItems`, sino sus propios botones fijos en `MainWindow.axaml`); sus tres
      `OnXSidebarEnabledChanged` dejaron de invocar el guardarraíl en consecuencia, ya que ocultar los tres a
      la vez no vacía `NavItems`.
- [x] Añadir una advertencia de contraste (o un botón siempre alcanzable de "restablecer apariencia") al
      editor de temas personalizados, para que un tema ilegible nunca deje al usuario sin salida. (análisis
      §4.5, Alta) — banner de bajo contraste (ratio WCAG < 3:1 sobre los pares texto/fondo reales, nuevo
      `HexColorUtility.TryGetContrastRatio`) en `ThemeEditorViewModel.HasLowContrastWarning`; de paso se
      corrigió que "Alternar tema" (`SettingsViewModel.ToggleTheme`) fuera un no-op silencioso con un tema
      personalizado activo (ahora lo desactiva primero) y se añadió "Desactivar tema personalizado" a la
      Paleta de comandos cuando hay uno activo — dos vías de escape alcanzables solo con teclado que antes no
      existían, sin depender de poder leer un editor ya ilegible. El botón "Restablecer" del propio editor ya
      existía (`ResetToDefaultsCommand`).
- [x] Añadir previsualización de ocurrencias fantasma para Recordatorios recurrentes en el Calendario,
      igual que ya existe para tareas Polar recurrentes. (análisis §4.4, Media) — nueva sobrecarga
      `CalendarAggregationService.BuildGhostOccurrences(Reminder, ...)`, misma cadena de
      `PolarTaskService.CalculateNextDueDate` que ya usa `Reminder.Recurrence`; `BuildItems` la invoca igual
      que para tareas Polar, y toda la UI de ocurrencia fantasma (`IsGhostOccurrence`) ya era genérica por
      tipo de origen, así que no necesitó cambios propios.
- [x] Implementar de verdad la exportación a PDF / vista de impresión de notas que la documentación y los
      comentarios de código ya describen, o retirar esas menciones si se decide no construirla a corto
      plazo. (análisis §4.2, Media) — **decisión: no implementar por ahora**. Los comentarios de
      `NoteExportRenderer.cs`/`NoteExportService.cs`/`Program.cs` ya se habían corregido en la Fase 4 para
      no prometer PDF/impresión propias; revisados de nuevo en esta fase, ya reflejan con precisión que solo
      existe exportación a HTML (con `@page`/`@media print` para que el navegador del usuario imprima o
      "guarde como PDF" ese HTML) y no necesitaron más cambios.

**Dependencias**: ninguna técnica; dependía de decisiones de producto del propietario del proyecto antes de
empezar cualquiera de los puntos — ya tomadas (ver arriba).

---

## Fase 7 (opcional) — Accesibilidad puntual restante (esfuerzo bajo-medio, impacto en inclusión)

La pasada de accesibilidad general ya se hizo (Fase 7 del roadmap anterior, cerrada) — esto es
específicamente lo que las funcionalidades nuevas dejaron sin cubrir.

- [x] `AutomationProperties.Name` en las 15 herramientas de la barra de formato del editor y los 5 botones
      de Buscar/Reemplazar. (análisis §4.2, Media-Alta) — añadido en `EditorView.axaml` reutilizando la
      misma clave de recurso que ya tenía cada botón en `ToolTip.Tip` (mismo patrón que ya usaban los
      botones de la cabecera del editor: Atrás, Alternar barra, Alternar panel lateral, Modo zen, Exportar
      HTML, Borrar). No se tocaron los 5 botones "chevron" de plegado de grupo de la barra de formato ni el
      toggle "Aa" de mayúsculas/minúsculas de Buscar/Reemplazar — quedan fuera del alcance explícito de esta
      tarea (los 15+5 elementos enumerados) y, en el caso de los chevron, un nombre accesible útil requeriría
      una clave de recurso nueva por grupo en vez de reutilizar la genérica compartida "Editor_Toolbar_ToggleGroup_Tooltip".
- [x] `AutomationProperties.Name` en los 6 campos de contraseña/recuperación de Sesiones (si no se hizo ya
      en la Fase 1). (análisis §4.1, Baja) — confirmado: ya se había hecho en la Fase 1
      (`SessionsView.axaml`, los 6 campos de crear sesión/desbloquear/olvidé mi contraseña). Sin cambios.
- [x] Cierre con `Escape` en los cuatro diálogos nuevos de Sesiones, siguiendo el mismo criterio que el
      resto de diálogos modales de la app. (análisis §4.1, Baja) — mismo patrón ya usado en
      `KanbanView.axaml`/`.axaml.cs` (`Focusable="True"` + `Loaded`/`KeyDown` en el `UserControl` raíz +
      `switch` sobre el estado del ViewModel que cierra el diálogo "más externo" primero): `SessionsView`
      gana foco al cargar y un nuevo `SessionsView_KeyDown` cierra, por prioridad, el diálogo de borrar
      sesión (`CancelDeleteSessionCommand`), el de desbloquear/olvidé mi contraseña
      (`CancelUnlockCommand`), el de códigos de recuperación (`CloseRecoveryCodesDialogCommand`) o el de
      crear sesión (`CancelCreateSessionCommand`), el que esté abierto. A diferencia del handler de Kanban,
      este solo actúa sobre `Key.Escape`, así que no necesita el guardarraíl de "no interceptar teclas
      normales dentro de un `TextBox` enfocado". El `KeyDown` puntual ya existente en `NewSessionNameBox`
      (Enter para confirmar, Escape para cancelar) queda intacto y sigue funcionando igual cuando el foco
      está en ese campo concreto; el nuevo handler global cubre además los otros tres diálogos y los campos
      de contraseña del propio diálogo de creación, donde antes Escape no hacía nada.

**Dependencias**: ninguna.

---

## Fase 8 (opcional) — Rendimiento puntual y mejoras menores (esfuerzo bajo, impacto bajo salvo vaults/notas grandes)

- [ ] Enrutar `RecomputeFences()` (resaltado de bloques de código en vivo) por el mismo debounce de 600ms
      que ya usa el resto del editor (`services/editor/EditorDebounce.cs`), en vez de recalcular en cada
      pulsación. (análisis §4.2, Media — el más urgente de esta fase para notas largas)
- [ ] Debounce en `FindReplaceViewModel.RefreshStatus()`/`ReplaceAll`. (análisis §4.2, Baja)
- [ ] Debounce en la búsqueda del Calendario (`CalendarViewModel.OnSearchTextChanged`). (análisis §4.4,
      Baja)
- [ ] Contador de palabras/tiempo de lectura consciente de scripts sin espacios (CJK). (análisis §4.2, Baja)
- [ ] Formato de fecha localizado en `ReminderEditorViewModel.NotifyDateDisplayLabel` (usa
      `CultureInfo.CurrentCulture` en vez de `dd/MM/yyyy` fijo, replicando la corrección ya hecha en
      `CalendarQuickCreateViewModel`). (análisis §4.4, Baja)
- [ ] "Resaltar todas las coincidencias" en Buscar/Reemplazar del editor, no solo la actual. (análisis
      §4.2, Baja)
- [ ] Manejo de colisión visual para ítems con hora solapada en Calendario Semana/Día (posicionamiento
      horizontal en vez de superposición total). (análisis §4.4, Media — el de mayor esfuerzo de esta fase)
- [ ] `NoteExportRenderer`: soportar imágenes con URI `data:` preexistente y rutas absolutas de Windows
      con letra de unidad, en vez de descartarlas en silencio. (análisis §4.2, Media)

**Dependencias**: ninguna. Cajón de sastre deliberado, igual que la Fase 10 del roadmap anterior.

---

## Nota sobre priorización

El orden entre fases 0-3 no es negociable (bugs críticos → seguridad/pérdida de datos → consistencia de UX
transversal → estados incorrectos visibles a diario). A partir de la Fase 4, el orden relativo entre fases
es una recomendación, no una dependencia técnica real — puede reordenarse libremente según la capacidad
disponible y el feedback de uso real, igual que el roadmap anterior ya trató sus fases 9 y 10 como
opcionales e independientes entre sí.
