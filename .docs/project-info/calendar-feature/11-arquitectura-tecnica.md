# 11 — Arquitectura técnica

## Contexto rápido

Este documento traduce los documentos 03–10 a estructura de código concreta: qué carpetas, qué clases
nuevas, qué cambios (mínimos) a servicios existentes. Sigue el patrón MVVM ya establecido en
`enki/src/enki/` — no introduce ningún patrón arquitectónico nuevo en el proyecto.

## 1. Ubicación en el árbol de carpetas

Siguiendo el patrón por-feature ya usado (`viewmodels/kanban/`, `viewmodels/polar/`, `viewmodels/reminders/`,
`views/kanban/`, etc.):

```
enki/src/enki/
  viewmodels/calendar/
    CalendarViewModel.cs              # estado de la página: vista activa, fecha de referencia, capas
    CalendarItemViewModel.cs          # wrapper de presentación de un CalendarItem (ver doc 03)
    CalendarMonthDayViewModel.cs      # una celda de la vista Mes (o reutilizar/generalizar KanbanCalendarDayViewModel, ver §4)
    CalendarWeekColumnViewModel.cs    # una columna de la vista Semana
    CalendarQuickCreateViewModel.cs   # popover de creación rápida (doc 06)
    CalendarLayerViewModel.cs         # un interruptor de capa (doc 07)
  views/calendar/
    CalendarView.axaml(.cs)           # contenedor de página, barra superior + sidebar + vista activa
    CalendarMonthView.axaml(.cs)
    CalendarWeekView.axaml(.cs)
    CalendarDayView.axaml(.cs)
    CalendarAgendaView.axaml(.cs)
    CalendarQuickCreatePopover.axaml(.cs)
  services/
    CalendarAggregationService.cs     # ver §3 — único servicio nuevo
```

## 2. `AppPage` y navegación

Añadir `Calendar` al enum existente en `viewmodels/shell/NavItemViewModel.cs`:

```csharp
public enum AppPage
{
    Dashboard, Notes, Sessions, Kanban, Trash, Settings, Reminders, Notifications,
    Calendar
}
```

Cablear en `MainWindowViewModel` siguiendo exactamente el mismo patrón que ya usan las demás entradas
(instanciación del viewmodel de página, entrada en el nav lateral, `NavigationsPanelEnabled`-equivalente
si aplica `CalendarPanelEnabled` de `09-configuracion-y-ajustes.md`).

## 3. `CalendarAggregationService` — el único componente nuevo de verdad

Es el servicio responsable de construir la lista de `CalendarItem`s (concepto de
`03-glosario-y-modelo-de-datos.md` §3) a partir de las tres fuentes, en cada refresco. **No persiste
nada** — es una función de agregación pura sobre el estado ya cargado en memoria de los otros tres
servicios.

Responsabilidades:

1. Recibir/inyectar `KanbanService`, `PolarTaskService`, `ReminderService` (mismo estilo de inyección por
   constructor que ya usan los viewmodels existentes).
2. Exponer un método (p. ej. `BuildItems(DateTime rangeStartUtc, DateTime rangeEndUtc)`) que:
   - Recorre `KanbanService.Board.Columns.SelectMany(c => c.Cards)` con `DueDateUtc` no nulo dentro del
     rango.
   - Recorre `PolarTaskService.Store` (tareas y subtareas) con `DueDateUtc` no nulo dentro del rango,
     **solo si** `AppSettings.PolarLinked == true` — igual comprobación que ya hace la vista "Gestión de
     tareas" antes de mostrarse.
   - Recorre `ReminderService.ActiveReminders` con `NotifyAtUtc` dentro del rango.
   - Para cada `PolarTask` con `Recurrence != None` y `DueDateUtc` dentro o antes del rango: encadena
     `PolarTaskService.CalculateNextDueDate` (método estático ya existente — **reutilizar, no
     duplicar**) generando ocurrencias fantasma hasta cubrir el rango visible, con el tope de seguridad
     descrito en `03-glosario-y-modelo-de-datos.md` §4.
   - Aplica las capas activas y filtros secundarios (`07-filtros-busqueda-y-capas.md`) antes de devolver
     la lista — o expone la lista sin filtrar y deja el filtrado al `CalendarViewModel`, lo que resulte
     más simple una vez escrito el código real; ambas opciones son válidas, decisión de implementación.
3. Exponer también un método para los items **sin fecha** (`DueDateUtc == null`, nunca aplica a
   `Reminder`) que alimenta el panel de "no programadas" (`04-vistas-y-navegacion.md` §8).

## 4. Reutilización de `KanbanCalendarDayViewModel`

`viewmodels/kanban/KanbanCalendarDayViewModel.cs` ya resuelve exactamente el problema de "una celda de
grilla de mes de 42 días fijos, con `IsCurrentMonth`/`IsToday`/`IsSelected`". El calendario necesita lo
mismo más la lista de `CalendarItem`s de ese día. Dos caminos válidos, a decidir en el momento de
implementar según cuál quede más limpio:

- **(a)** Mover `KanbanCalendarDayViewModel` a un namespace/carpeta compartida (p. ej.
  `viewmodels/common/`) y que tanto el selector de fecha de Kanban como la vista Mes del calendario lo
  usen — el calendario compone alrededor (`CalendarMonthDayViewModel` con una referencia o herencia hacia
  ese tipo) en vez de reimplementar el cálculo de "6 filas × 7 columnas con relleno de mes anterior/
  siguiente".
- **(b)** Extraer solo el cálculo puro (qué 42 fechas corresponden a un mes dado) a un método estático
  compartido, y que cada viewmodel construya su propio tipo de celda (mínimo cambio en el código
  existente de Kanban, cero riesgo de romper el selector de fecha ya en producción).

Documentar en este mismo apartado, al implementar, cuál de las dos se eligió y por qué, para que quede
como referencia si se vuelve a tocar esta zona.

**Decisión tomada en Fase 1: opción (b).** Se extrajo el cálculo puro de las 42 fechas a
`viewmodels/common/MonthGridCalculator.cs` (método estático `BuildDates(DateTime month)`), y tanto
`KanbanCardEditorViewModel.RebuildCalendarDays` como `CalendarViewModel.Rebuild` lo consumen para construir
su propio tipo de celda (`KanbanCalendarDayViewModel` y `CalendarMonthDayViewModel` respectivamente).
Motivo: cero riesgo de tocar el selector de fecha de Kanban ya en producción, y `CalendarMonthDayViewModel`
necesitaba de todos modos una forma distinta (lista de `CalendarItemViewModel`s por día, `VisibleItems`/
`OverflowCount` para el "+N más"), así que heredar de `KanbanCalendarDayViewModel` no habría ahorrado nada
frente a construir su propio tipo alrededor del mismo cálculo de fechas.

**Decisión tomada en Fase 2: mismo patrón "cálculo puro compartido" aplicado a Semana** —
`viewmodels/common/WeekGridCalculator.cs` (`StartOfWeek(DateTime date)`, mismo criterio de lunes=inicio de
semana que `MonthGridCalculator`). El mini-calendario del sidebar (`04-vistas-y-navegacion.md` §7)
reutiliza directamente `MonthGridCalculator.BuildDates` con su propio mes de navegación
(`CalendarViewModel.MiniCalendarMonth`, independiente de `ReferenceDate` hasta que se hace clic en un día),
en vez de introducir un tercer cálculo de grilla.

**Desviación documentada respecto al listado de archivos de §1**: las vistas Semana/Día/Agenda **no** se
implementaron como `CalendarWeekView.axaml`/`CalendarDayView.axaml`/`CalendarAgendaView.axaml` separados —
se añadieron como secciones adicionales dentro del mismo `views/calendar/CalendarView.axaml` que ya
contenía la vista Mes desde la Fase 1 (esa vista tampoco se separó en su propio `CalendarMonthView.axaml`
en su momento). Motivo: seguir el patrón que realmente se implementó en Fase 1 en vez de forzar la
separación en archivos que el propio documento marcaba como "a decidir en el momento de implementar", y
porque las cuatro vistas comparten una única barra de navegación temporal y un único sidebar que viven en
el contenedor de página — separarlas en `UserControl`s distintos habría obligado a pasar ese estado
compartido hacia abajo sin beneficio real. Semana y Día además comparten literalmente el mismo tipo de
celda (`CalendarWeekColumnViewModel`, ver `03-glosario-y-modelo-de-datos.md`/`04-vistas-y-navegacion.md`
§4: "Día es simplemente una Semana con una columna"), así que ambas se dibujan con la misma sección XAML
condicionada por `CalendarViewModel.IsHourGridView`.

**Nuevos tipos de la Fase 2** (`viewmodels/calendar/`): `CalendarViewMode` (enum Mes/Semana/Día/Agenda),
`CalendarWeekColumnViewModel` (una columna de Semana, o la única columna de Día — `ObservableObject` solo
por `NowOffsetY`, que se refresca en vivo cada 60s sin reconstruir el resto de la columna),
`CalendarTimedItemViewModel` (posiciona un `CalendarItemViewModel` con hora dentro de la grilla horaria:
`TopOffset`/`Height` en píxeles, cálculo puro y testeado sin servicios), `CalendarHourRowViewModel` (una de
las 24 filas fijas de la grilla horaria), `CalendarAgendaGroupViewModel` (grupo por día de la vista Agenda,
`Build()` estático y puro), `CalendarMiniDayViewModel` (celda del mini-calendario, igual forma que
`CalendarMonthDayViewModel` pero sin chips). Un converter nuevo, `TopOffsetToMarginConverter`
(`converters/`), traduce esos offsets en píxeles a un `Thickness` de solo margen superior para poder
posicionar los items de forma absoluta dentro de un `Panel` normal (que estira a sus hijos) en vez de un
`Canvas` — evita depender de bindings de ancho vía referencia a elementos nombrados.

## 5. Cambios necesarios en servicios existentes

### 5.1 `ReminderService` necesita un evento de cambio

A diferencia de `KanbanService.BoardChanged` y `PolarTaskService.DataChanged`, `ReminderService` (según
su API pública actual: `Create`, `MarkAsRead`, `MarkAllAsRead`, `Delete`, `ClearAll`, `ToggleCompleted`,
`PopDueReminders`) **no expone ningún evento de cambio**. El calendario necesita refrescarse en vivo
cuando un recordatorio se crea/edita/completa/elimina desde cualquier pantalla (incluido el propio
calendario). Añadir:

```csharp
public event Action? RemindersChanged;
```

Disparado desde el mismo punto donde hoy se persiste (`Save()` interno de `ReminderService`), **siguiendo
literalmente el mismo patrón** que `KanbanService.Save()` ya usa para `BoardChanged`. Este es un cambio
pequeño y aislado, sin riesgo para el resto de consumidores actuales de `ReminderService` (nadie pierde
funcionalidad, solo se añade un evento nuevo que nadie estaba escuchando).

### 5.2 Escritura de fecha desde el calendario (drag & drop, quick-create)

No se necesita ningún método nuevo en `KanbanService`/`PolarTaskService`/`ReminderService` más allá de
los que ya usan sus editores respectivos para persistir un cambio de `DueDateUtc`/`NotifyAtUtc` — el
calendario debe **reutilizar esos mismos caminos** (mutar el objeto en memoria ya referenciado desde la
colección del servicio, luego llamar a `Save()`), no añadir una vía de escritura paralela. Confirmar el
método exacto revisando el editor correspondiente antes de implementar (`KanbanCardEditorViewModel`,
`PolarTaskEditorViewModel`, `ReminderEditorViewModel`) — este documento intencionadamente no fija nombres
de métodos que no se han verificado letra por letra en el código al momento de escribir esto, para no
dar por sentado una firma que puede haber cambiado.

## 6. Suscripciones a eventos de cambio (mantener la vista en vivo)

`CalendarViewModel` se suscribe, en su constructor, a los tres eventos de cambio
(`KanbanService.BoardChanged`, `PolarTaskService.DataChanged`, `ReminderService.RemindersChanged` — §5.1)
y reconstruye su lista de `CalendarItem`s llamando a `CalendarAggregationService` cada vez que cualquiera
de los tres se dispara — mismo patrón exacto que ya usa `ReminderInboxViewModel` suscrito a
`KanbanService.BoardChanged` (`docs/notificaciones-y-recordatorios.md`). También debe reaccionar a un
cambio de `AppSettings.PolarLinked` (ver `08-notificaciones-e-integracion.md` §5) — verificar si existe ya
algún evento de cambio de ajustes reutilizable en `SettingsService` antes de añadir uno nuevo.

## 7. Rendimiento — cuándo recalcular, cuándo no

- La expansión de ocurrencias fantasma (§3, punto 2) se recalcula solo para el **rango visible actual**,
  nunca para "todo el futuro" — al cambiar de mes/semana se vuelve a pedir con el nuevo rango. Esto evita
  guardar en memoria una lista potencialmente infinita de ocurrencias calculadas.
- Con los volúmenes de datos esperables en una app personal (decenas o pocos cientos de tareas/
  recordatorios activos, no miles), no se prevé necesidad de índices ni caché adicional en esta fase —
  no optimizar prematuramente; si el rendimiento real resulta un problema al probar con datos reales,
  es un problema a resolver con datos delante, no por anticipado en este documento.

## 8. Persistencia — recordatorio explícito de lo que NO cambia

Ningún archivo JSON nuevo se crea en `%AppData%/Enki/` para el calendario en sí (a diferencia de, por
ejemplo, `dismissed-reminders.json` para la bandeja). Las únicas escrituras nuevas en disco que provienen
del calendario son las que ya hacían `kanban.json`/`polar-*.json`/`reminders.json` al editar esos mismos
datos desde cualquier otra pantalla — el calendario es un cliente más de esos archivos, no dueño de
ninguno nuevo. La única persistencia genuinamente nueva son los campos añadidos a `AppSettings`
(`09-configuracion-y-ajustes.md`), que ya viven en `settings.json` existente.

## 9. Decisiones tomadas en Fase 3 (interacciones)

- **Dos métodos nuevos, mínimos, en servicios existentes** (además de `ReminderService.RemindersChanged`
  de §5.1, ya documentado): `ReminderService.Reschedule(id, notifyAtUtc)` — necesario porque
  `ReminderEditorViewModel` solo tiene modo "crear", nunca "editar", así que no existía ningún camino de
  escritura para cambiar `NotifyAtUtc` de un recordatorio ya creado; y
  `PolarTaskService.UpdateSubtaskDueDate(subtask, dueDateUtc)` — necesario porque ninguna pantalla edita
  hoy la fecha de una `PolarSubtask` por separado de su tarea padre. Ambos siguen el mismo patrón mutar +
  `Save()` que ya usa el resto del servicio, no una vía de escritura paralela.
- **"Editar" (popover de detalle) e "Ir a Kanban/Gestión de tareas" (menú contextual) navegan al mismo
  sitio**: los dos abren el editor completo directamente (`KanbanCardEditorViewModel`/
  `PolarTaskEditorViewModel`) en vez de abrir primero un detalle de solo lectura y exigir un segundo clic
  para editar. Para `Reminder`, que no tiene un editor de edición propio (`ReminderEditorViewModel` solo
  crea), ambas acciones abren su diálogo de detalle existente (checkbox de completado + eliminar), que es
  la superficie de "edición" más cercana que ya existe para un recordatorio ya creado.
- **El menú contextual de clic derecho sobre una celda/franja vacía** (doc 05 §7: "Crear tarjeta Kanban
  aquí"/"Crear tarea Polar aquí"/"Crear recordatorio aquí") se implementó en las celdas de Mes y en la
  franja "todo el día" de Semana/Día, pero **no** en cada una de las 24 celdas de hora de la grilla horaria
  — ahí solo queda el clic simple (que ya cubre el caso principal, doc 05 §3). Motivo: la franja "todo el
  día" y las celdas de Mes exponen su fecha directamente en el `DataContext` del propio control, así que el
  menú se resuelve con bindings XAML puros; una celda de hora solo expone su propia hora
  (`CalendarHourRowViewModel`), y resolver la columna/fecha a la que pertenece exige el mismo recorrido de
  árbol lógico que ya hace `CalendarView.axaml.cs` para el arrastre — hacerlo además por cada ítem de un
  menú contextual en 24 celdas × 7 columnas no compensaba frente al clic simple ya disponible.
- **El arrastre a una franja horaria redondea a 15 minutos** según la posición vertical del cursor dentro
  de la celda de 48px (`CalendarView.axaml.cs`, `RoundToQuarterHour`) — cumple literalmente el ejemplo del
  doc 05 §2 ("redondeado al intervalo de franja, p. ej. 15 o 30 minutos") sin añadir un ajuste de
  granularidad configurable.
- **La creación rápida siempre recibe una fecha concreta**, nunca `null` — el panel de "no programadas"
  (destino natural de un item sin fecha) es alcance de Fase 4, así que permitir "crear sin fecha" desde el
  popover rápido no tendría ningún sitio donde mostrarse todavía. `CalendarQuickCreateViewModel` no expone
  ningún control de "quitar fecha" en esta fase.

## 10. Decisiones tomadas en Fase 4 (capas, filtros, búsqueda, recurrencia)

- **`CalendarAggregationService.BuildItems` devuelve la lista sin filtrar por capas/filtros secundarios**
  (opción explícitamente dejada abierta en §3) — solo aplica el nuevo parámetro
  `includeGhostOccurrences` (por defecto `true`) para no calcular en absoluto ocurrencias fantasma que de
  todas formas se van a ocultar. El filtrado real (capas, prioridad, etiquetas, ocultar completadas,
  resaltado de búsqueda) vive en `CalendarViewModel.BuildFilteredItems`, que envuelve la llamada al
  servicio. Motivo: así el mini-calendario (el punto "hay contenido"), las cuatro vistas y el panel de "no
  programadas" pasan por el mismo único punto de filtrado (`CalendarItemFilter`), en vez de arriesgarse a
  que dos copias de la lógica de filtrado diverjan con el tiempo.
- **`CalendarItemFilter`** (`viewmodels/calendar/CalendarItemFilter.cs`) — funciones estáticas puras
  (`MatchesLayer`/`MatchesSecondaryFilters`/`MatchesSearch`), mismo patrón de "calculadora pura en su
  propio archivo" que `MonthGridCalculator`/`WeekGridCalculator`. Es lo único de esta fase directamente
  testeable sin instanciar `CalendarViewModel` (que sigue sin ser construible en tests por la falta de
  `storageDirectory` en `KanbanService`, ver `CalendarViewModelTests`).
- **La exención de "sin este atributo, el filtro no te excluye" se extiende de prioridad (ya prevista en
  el doc 07 §3 para `Reminder`) a etiquetas también**, para `Kanban`/`Reminder`: activar un filtro de
  etiqueta no oculta las tarjetas Kanban ni los recordatorios (que nunca han tenido etiquetas), solo
  restringe entre los `PolarTask`/`PolarSubtask` visibles. Ver el razonamiento completo en el XML-doc de
  `CalendarItemFilter.MatchesSecondaryFilters`.
- **Persistencia de capas por conjunto de claves *ocultas*, no activas** —
  `AppSettings.CalendarHiddenLayerKeys` (nueva lista en `settings.json`) guarda solo lo que el usuario
  desactivó explícitamente. Con esto, una `PolarTaskList` recién creada (o el vínculo con Polar recién
  activado) aparece activa por defecto sin ninguna migración, cumpliendo literalmente "estado por defecto:
  todas las capas activas" del doc 07 §1 sin tener que enumerar cada clave posible de antemano.
- **Solo capas + "ocultar completadas" + "ocultar ocurrencias fantasma" se persisten** —
  `AppSettings.CalendarHideCompletedItems`/`CalendarHideGhostOccurrences`. La búsqueda y los filtros de
  prioridad/etiqueta son de sesión (se resetean al reabrir Enki): el doc 07 solo exige persistir "la
  combinación de capas elegida" (§1) y las dos preferencias booleanas de §3 que también se mencionan desde
  `09-configuracion-y-ajustes.md`; no hay ningún requisito equivalente para qué prioridades/etiquetas
  estaban marcadas, así que no se inventó uno.
- **`CalendarUnscheduledItemViewModel` es un tipo nuevo**, no `CalendarItemViewModel` con `MomentUtc`
  anulable — casi todo lo que ya expone `CalendarItemViewModel` (`MomentLocal`, `IsPastDue`, `TimeLabel`,
  `EffectiveBorderColorHex`...) asume un instante real que un item sin fecha no tiene. Comparte con
  `CalendarItemViewModel` los mismos `SourceId`/`SourceType`/`LayerKey` para poder reutilizar
  `CalendarViewModel.MoveItem` (ya existente desde Fase 3) sin cambios cuando se suelta sobre la grilla, y
  los mismos `Priority`/`Tags`/`IsCompleted` para poder reutilizar `CalendarItemFilter.MatchesSecondaryFilters`
  vía una sobrecarga dedicada en vez de duplicar la regla.
- **El arrastre desde el panel de "no programadas" reutiliza literalmente el mecanismo de arrastre de un
  chip de la grilla** (`CalendarView.axaml.cs`) — se generalizó el campo privado que antes guardaba el
  `CalendarItemViewModel` completo (`_dragItem`) a una tupla `(SourceType, SourceId)` (`_dragSource`), ya
  que eso es todo lo que el formato de arrastre (`"tipo|id"`) transporta de todas formas. Esto también es
  el punto donde se bloquea el arrastre de una ocurrencia fantasma: el `switch` de
  `Chip_PointerPressed` devuelve `null` para un `CalendarItemViewModel`/`CalendarTimedItemViewModel` con
  `IsGhostOccurrence == true`, así que `Chip_PointerMoved` nunca inicia un `DragDrop.DoDragDropAsync` para
  esos chips.
- **El clic sobre una ocurrencia fantasma sigue abriendo el mismo popover de detalle que cualquier otro
  chip**, en vez de saltar directo al editor de la tarea real como sugiere literalmente
  `03-glosario-y-modelo-de-datos.md` §4 — por consistencia con la decisión ya tomada en Fase 3 de que
  "editar" siempre pasa primero por el popover de detalle (§9 de este documento). El popover de un item
  fantasma sustituye la fecha/VENCIDO por `GhostOccurrenceNoteLabel` ("PRÓXIMA OCURRENCIA ESTIMADA: …") y
  oculta los botones de completar/eliminar (`CanToggleComplete`/`CanDelete` ya en `false` para
  `IsGhostOccurrence == true`), dejando solo "EDITAR" — que, como `SourceId` de un fantasma es el id de la
  tarea real, abre exactamente el editor real sin crear ninguna fila nueva. El resultado final es el mismo
  que pide el doc (clic → termina en el editor real, sin poder completar/eliminar/arrastrar), solo que con
  un paso intermedio idéntico al de cualquier otro item del calendario.
- **`CalendarAggregationService.BuildGhostOccurrences` es un método estático puro** (recibe la `PolarTask`,
  su `PolarTaskList?` y el rango, no el servicio) para poder testearlo sin construir
  `CalendarAggregationService` (que sí necesita `KanbanService`, no testeable — mismo motivo documentado en
  la clase). Reutiliza `PolarTaskService.CalculateNextDueDate` tal cual, encadenado con el tope de 366
  iteraciones de `03-glosario-y-modelo-de-datos.md` §4.
- **Panel de capas/filtros/no programadas viven en el sidebar existente** (envuelto ahora en un
  `ScrollViewer`), no en la barra superior ni en un popover `dropdown-popover` nuevo — el doc 07 dejaba
  ambas ubicaciones como válidas ("en la barra superior o el sidebar") y el propio doc 10 §7 ya describía
  el panel de capas con el lenguaje visual de un panel fijo de sidebar, no de un menú desplegable.

## 11. Decisiones tomadas en Fase 5 (ajustes, notificaciones, pulido)

- **Cuatro de los diez campos del doc 09 §1 ya existían desde la Fase 4** con nombre/forma distinta a la
  literal del documento: `CalendarHiddenLayerKeys`/`CalendarHideCompletedItems`/`CalendarHideGhostOccurrences`
  (en vez de `CalendarActiveLayersJson`/`CalendarShowCompletedItems`/`CalendarShowGhostOccurrences`, ver
  §10) y `CalendarShowSidebarScrollbar` (Ajustes › General, no documentado en el doc 09 original). Fase 5
  no los duplica bajo el nombre literal del doc — serían una segunda fuente de verdad para el mismo
  estado — solo añade los seis campos genuinamente nuevos a `AppSettings.cs`: `CalendarDefaultView`
  (`string`, igual que `Theme`/`EditorColumnPosition` — se evita que `Models` dependa de
  `ViewModels.CalendarViewMode`), `CalendarWeekStartsOnMonday`, `CalendarShowWeekends`,
  `CalendarWorkingHoursStart`/`CalendarWorkingHoursEnd`, `CalendarTimeFormat24h` y `CalendarPanelEnabled`.
- **`CalendarShowSidebarScrollbar` se mueve de Ajustes › General a la nueva sección "Calendario"** — vivía
  ahí solo porque la sección no existía todavía cuando se implementó en Fase 4; el doc 09 §2 es explícito
  en que la sección Calendario es su sitio natural.
- **Mecanismo de reactividad en vivo: un callback explícito por grupo, no un evento genérico de
  `SettingsService`** — `SettingsService` no expone (ni expuso nunca) un evento de "algo cambió"; cada
  ajuste que un viewmodel ya abierto necesita conocer en vivo se propaga vía un parámetro `Action`/`Action<bool>`
  inyectado en el constructor de `SettingsViewModel`, exactamente como ya hacía `CalendarShowSidebarScrollbar`
  desde la Fase 4. Los seis ajustes nuevos usan dos callbacks: `Action<bool> onCalendarPanelChanged`
  (visibilidad del nav, ver más abajo) y un único `Action onCalendarSettingsChanged` que agrupa
  `CalendarWeekStartsOnMonday`/`CalendarShowWeekends`/`CalendarWorkingHoursStart`/`CalendarWorkingHoursEnd`/
  `CalendarTimeFormat24h` — un callback por grupo en vez de uno por propiedad (a diferencia del patrón
  estricto "uno por ajuste" de Fases anteriores) porque las cinco viven conceptualmente juntas ("cómo se ve
  la grilla") y `MainWindowViewModel.OnCalendarSettingsChanged` simplemente reasigna las cinco propiedades
  correspondientes de `CalendarViewModel` de un tirón. `CalendarDefaultView` no dispara ningún callback —
  solo importa la próxima vez que se abre la página, nunca a una ya abierta.
- **`CalendarPanelEnabled` se implementa filtrando `MainWindowViewModel.NavItems`, no añadiendo un
  `IsVisible` a `NavItemViewModel`** — a diferencia de `RemindersPanelEnabled`/`NotificationsPanelEnabled`
  (que solo gatean los dos botones de la barra de título, un punto de entrada aparte, nunca nada dentro de
  `NavItems`), "Calendario" sí es un miembro real de esa colección. `OnCalendarPanelModeChanged` inserta o
  quita el `NavItemViewModel` de la posición 1 según el valor, y navega a `Dashboard` si el usuario estaba
  precisamente en la página de Calendario cuando se desactiva.
- **`MonthGridCalculator.BuildDates`/`WeekGridCalculator.StartOfWeek` ganan un parámetro opcional
  `startOnMonday = true`** en vez de una segunda función paralela — con el valor por defecto, el selector
  de fecha de Kanban (`KanbanCardEditorViewModel`) y el de la creación rápida del calendario
  (`CalendarQuickCreateViewModel`) siguen exactamente igual que antes sin tocar sus llamadas; solo
  `CalendarViewModel` (Mes, Semana y el mini-calendario del sidebar) pasa el valor real de
  `AppSettings.CalendarWeekStartsOnMonday`. El popover de fecha de la propia creación rápida del calendario
  se deja fuera a propósito — el doc 09 §1 dice literalmente "afecta a las vistas Mes/Semana y al
  mini-calendario", no a cada selector de fecha embebido de la app.
- **`CalendarShowWeekends` se implementa filtrando fechas de fin de semana de la grilla ya construida de 42
  celdas, nunca generando una grilla de 30 con un cálculo propio** — `RebuildMonth` sigue pidiendo las 42
  fechas de siempre y luego les aplica `.Where(IsWeekday)` cuando el ajuste está desactivado; como cada
  semana completa aporta exactamente 5 días laborables, el resultado son 30 fechas que siguen siendo "6
  filas" exactas, así que `CalendarMonthDayViewModel.IsLastRow` solo necesitó parametrizar su cálculo por
  el número de columnas (`5 * columns`), no reescribirse. `MonthGridColumns` (5 o 7) alimenta el
  `UniformGrid.Columns` tanto de la cabecera de días (`WeekdayHeaders`, ahora una `ItemsControl` en vez de
  7 `TextBlock` fijos) como de la propia grilla — ambos ya estaban duplicados en XAML antes de esta fase
  (cabecera de Mes y cabecera del mini-calendario), así que solo la de Mes gana el filtrado; la del
  mini-calendario mantiene sus 7 columnas siempre (doc 09 §1: el ajuste "afecta a las vistas Mes/Semana",
  no al mini-calendario). Semana/Día no necesitaron el mismo tratamiento — su `UniformGrid.Columns` ya
  estaba enlazado a `WeekColumns.Count` desde la Fase 2, así que filtrar esa colección en `RebuildWeek`
  reflowa las columnas automáticamente.
- **`CalendarTimeFormat24h` solo afecta las etiquetas de las 24 filas fijas de la grilla horaria
  (`CalendarHourRowViewModel.Label`), no la hora de cada chip individual** — el doc 09 §1 dice literalmente
  "en las franjas horarias", y limitarlo a eso evita tener que añadir el flag de formato a
  `CalendarItemViewModel` (un `record` inmutable reconstruido desde cero en cada fuente por
  `CalendarAggregationService`) y propagarlo por cada punto donde se construye uno — un coste real para un
  matiz cosmético que el documento no pedía explícitamente para los chips.
- **Atajos de teclado: un único `KeyDown` en el `UserControl` raíz de `CalendarView.axaml`, no un manejador
  por control** — no existía ningún patrón de atajos a nivel de página en el resto de la app para copiar
  (los únicos `KeyDown` existentes son de un `TextBox` individual, p. ej. "Enter para confirmar" en
  `KanbanView.axaml.cs`); al ser un evento `Bubble` por defecto, un solo handler en la raíz recibe los de
  cualquier control interno con foco. Los controles de texto (`TextBox`/`NumericUpDown`, p. ej. el título
  de creación rápida o la búsqueda) quedan exentos salvo para `Escape`, para que escribir "n" en un título
  no dispare "nueva creación rápida". `Escape` solo cierra explícitamente `QuickCreate`/`ItemPendingDelete`
  (overlays propios controlados por `IsVisible`, no `Popup`) — el popover de detalle y el "+N más" ya se
  cierran solos con Escape por el light-dismiss nativo de `Popup` de Avalonia, así que no necesitan código
  adicional.
- **La animación de "reveal" del doc 10 §8 no existía en ningún sitio de la app pese a que el documento
  daba a entender que sí** — una búsqueda exhaustiva de `TranslateTransform`/`Animation`/`KeyFrame` en todo
  el árbol de `views/`/`styles/` no encontró ningún precedente real, solo transiciones de *hover* (p. ej.
  `Border.note-card:pointerover`) que usan el mismo lenguaje de sintaxis (`RenderTransform="translateY(…)"`,
  `CubicEaseOut`, ~300ms) pero disparado por el puntero, no por un cambio de estado del viewmodel. Se
  implementó desde cero: `CalendarViewModel.RevealOpacity`/`RevealOffsetY` (compartidas por las tres
  secciones de vista — Mes/Semana-Día/Agenda — ya que solo una está visible a la vez), puestas a
  `0`/`8px` justo antes de reconstruir por `AnimateReveal()` y devueltas a `1`/`0` un `Dispatcher.UIThread.Post`
  después (con prioridad `Loaded`, para que Avalonia efectivamente renderice el fotograma "recién
  aparecido" antes de animar hacia el estado estable — asignar ambos valores en la misma pila de llamadas
  no le da a `Transitions` ningún fotograma "de partida" del que animar). Un nuevo
  `DoubleToTranslateYConverter` (mismo patrón que `TopOffsetToMarginConverter`) traduce el `double` a
  `TransformOperations` para el `RenderTransform`. Deliberadamente **no** se dispara desde `Rebuild()` en
  general — solo desde `SetView`/`PreviousPeriod`/`NextPeriod`/`GoToToday`/`SelectMiniDay` — para que editar
  una tarjeta en Kanban con el calendario abierto no vuelva a animar la grilla en cada refresco de datos.
- **Indicadores de vencido/hoy y la reacción en vivo a vincular/desvincular Polar ya estaban completos
  desde fases anteriores** — `CalendarItemViewModel.IsPastDue` ya reutilizaba `KanbanCard.IsPastDue`
  verbatim (doc 08 §3) y `CalendarViewModel` ya se suscribía a `PolarAuthService.LinkStateChanged` desde la
  Fase 1 (doc 08 §5). Fase 5 no tocó ninguno de los dos, solo los verificó contra el checklist de
  `13-pruebas-y-checklist-de-calidad.md`. Se dejó documentado (comentario XML en
  `CalendarItemViewModel.DangerColorHex`) que ese hex duplica intencionadamente `DangerColor` de
  `styles/Colors.axaml` en vez de resolverlo vía recurso de Avalonia — el `record` no tiene ninguna otra
  dependencia de `Application.Current` y se construye en tests xUnit sin una `Application` en marcha.

## 12. Decisiones tomadas en el addendum post-Fase 5 (franja horaria de Kanban)

- **`DurationMinutes` (`int?`) se añade solo a `KanbanCard`**, no a `PolarTask`/`PolarSubtask`/`Reminder` —
  ver `00-README.md` "Addendum post-Fase 5" para el porqué (Kanban es el único de los tres orígenes que no
  sincroniza con Supabase/la app Android). `CalendarItemViewModel` gana el mismo campo, poblado únicamente
  en la rama Kanban de `CalendarAggregationService.BuildItems` (y forzado a `null` si el item resulta
  "todo el día", para que una franja guardada en un card que luego pierde su hora no se dibuje por error).
- **Se guarda como minutos relativos (`DurationMinutes`), no como un `EndDateUtc` absoluto** — mismo patrón
  que ya usa `PolarTask.TimeEstimate`. Ventaja concreta: `MoveItem` (Fase 3) ya reescribe `DueDateUtc` sin
  tocar `DurationMinutes`, así que arrastrar una tarjeta con franja a otro día/hora conserva su duración
  automáticamente sin ningún cambio en `MoveItem`.
- **`CalendarTimedItemViewModel` sigue siendo un `record`/clase pura sin `INotifyPropertyChanged`** — se
  le añadió `EffectiveDurationMinutes` (duración real con el suelo de 15 min aplicado, o la convención fija
  de siempre si no hay duración real) calculado una vez en el constructor, igual que `TopOffset`/`Height`.
  Se decidió **no** convertirla en `ObservableObject` para soportar una vista previa en vivo durante el
  arrastre de redimensionado — hubiera contradicho su propio comentario de clase ("pure pixel math... never
  written back") y roto su testabilidad directa sin `Application`. En su lugar, la vista previa en vivo
  durante el arrastre se resuelve manipulando directamente `Panel.Height` en código-behind
  (`CalendarView.axaml.cs`, `ResizeHandle_PointerMoved`) — un valor local que sobreescribe temporalmente el
  binding hasta que `CalendarViewModel.ResizeItem` persiste el cambio real y el siguiente refresco (disparado
  por `KanbanService.BoardChanged`) reconstruye el árbol visual entero con el binding real.
- **El gesto de redimensionar es un segundo "state machine" de puntero, separado del de arrastrar-para-mover
  ya existente (`Chip_PointerPressed`/`Chip_PointerMoved`/`DragDrop`)** — el asa vive dentro del mismo chip
  que ya escucha ese primer gesto (`TimedChip_Initialized`), así que en vez de intentar que ambos
  coexistan sin pisarse (el ancestro se suscribe con `handledEventsToo: true`, así que marcar el evento
  como manejado en el asa no evita que el handler del chip también se dispare), se añadió un campo
  `_resizing` que `Chip_PointerMoved` comprueba primero y usa para no arrancar un `DragDrop.DoDragDropAsync`
  mientras un redimensionado está en curso. Alternativa descartada: separar el asa en su propio control con
  `RoutingStrategies` distintas — más frágil ante refactors futuros del template que este único flag.
- **`CalendarViewModel.RoundToFiveMinutes`/`MaxDurationMinutesSameDay`/`MinimumDurationMinutes` son
  `public static`** a propósito — permite que `CalendarView.axaml.cs` los use durante el arrastre sin
  duplicar la lógica de redondeo/tope, y que se cubran con tests xUnit sin tener que construir un
  `CalendarViewModel` completo (que, como ya documenta la nota de `CalendarViewModelTests`, no se puede
  instanciar en tests porque `KanbanService` no tiene una carpeta de almacenamiento configurable).
- **Iteración de visibilidad (mismo addendum, tras feedback de uso real)**: el primer corte mostraba el
  rango combinado ("21:30–22:15") solo en la esquina superior del chip, junto al título — en un bloque
  alto (o en una columna estrecha de Semana) esa única línea no comunicaba bien "esto ocupa desde aquí
  hasta aquí". Se cambió a `CalendarItemViewModel.HasDuration`/`EndTimeLabel`: el chip de Semana/Día
  ahora muestra la hora de inicio arriba (sin cambios) y la hora de fin anclada aparte al borde inferior,
  junto al asa de redimensionado — las dos horas quedan físicamente donde ocurren dentro del bloque. El
  rango combinado (`TimeLabel`) se mantiene tal cual para Mes/Agenda/el popover de detalle, que no tienen
  un alto de bloque con el que asociar cada hora por separado. De paso, `MinimumRealDurationFloorMinutes`
  subió de 15 a 30 minutos — el layout de dos etiquetas (título + inicio arriba, fin abajo, más el asa)
  necesita más aire vertical que solo el título para seguir siendo legible en una franja muy corta.
- **Bug real encontrado tras el punto anterior (no solo "poco visible"): el chip no llenaba el alto real
  de la franja.** `ToggleButton.calendar-chip` nunca fijó `VerticalAlignment` — con el `Panel` posicionador
  ya con el alto real vía binding (`Height="{Binding Height}"`, correcto), el `ToggleButton` de dentro se
  quedaba con su alto automático (ajustado al texto), dejando un hueco vacío entre el chip visible y el
  asa de redimensionado (que sí estaba anclada correctamente al fondo del `Panel`, por eso se veía "suelta"
  más abajo). Pasaba desapercibido con la convención fija de 40 min de siempre porque el alto automático
  del botón resultaba parecido; con una duración real más larga el hueco se hizo evidente. Arreglado
  añadiendo `VerticalAlignment="Stretch"` + `VerticalContentAlignment="Stretch"` a
  `ToggleButton.calendar-chip.calendar-chip-timed` (`styles/Styles.axaml`) — acotado a esa variante, no a
  `.calendar-chip` base, porque el chip de Mes/Agenda vive en un contenedor de alto automático donde
  Stretch no cambia nada pero tampoco hace falta tocarlo.
- **El editor de tarjeta (`KanbanCardEditorViewModel`) y la creación rápida (`CalendarQuickCreateViewModel`)
  guardan duración en minutos con un stepper de ±5, no un segundo selector de hora de fin** — mostrar un
  picker de "hora de fin" independiente hubiera necesitado validar "fin después de inicio" y el cruce de
  medianoche por duplicado; en su lugar cada viewmodel expone una `DurationRangeLabel` de solo lectura
  (`"21:30 – 22:15"`) calculada a partir de inicio + minutos, así el usuario ve el resultado real sin que
  el campo de entrada tenga que ser una hora absoluta. Ambos steppers reusan el grano de 5 minutos ya
  establecido por el selector de minutos de "Agregar hora"/`PolarTask.TimeEstimate`.
