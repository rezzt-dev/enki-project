# 03 — Glosario y modelo de datos

## Contexto rápido

El Calendario no tiene modelo de datos propio: es una vista sobre tres dominios que ya existen en Enki y
se persisten por separado. Este documento define (a) el vocabulario exacto para no confundir conceptos
que suenan parecido, (b) el detalle campo a campo de las tres fuentes reales, y (c) el concepto nuevo
`CalendarItem`, que es una **proyección en memoria, no persistida**, usada solo para renderizar y
gestionar interacción en las vistas de calendario.

## 1. Glosario (términos que se prestan a confusión)

| Término | Qué es | Qué NO es |
|---|---|---|
| **Tarjeta Kanban** (`KanbanCard`) | Una tarjeta del tablero local de Enki, con `DueDateUtc` opcional. Vive en `kanban.json`, dentro de una `KanbanColumn` de la `KanbanBoard`. | No tiene recurrencia. No tiene lista propia (su "lista" es la columna). |
| **Tarea Polar** (`PolarTask`) | Una tarea del sistema "Gestión de tareas", sincronizado con Supabase y con la app Android Polar. Vive en `PolarTaskStore`, dentro de una `PolarTaskList`. Solo existe/se ve si `AppSettings.PolarLinked == true`. | No es lo mismo que una tarjeta Kanban aunque ambas tengan `DueDateUtc` — son dos tableros independientes que **no** se sincronizan entre sí. |
| **Subtarea Polar** (`PolarSubtask`) | Hija de una `PolarTask`, con su propia `DueDateUtc` opcional. | Una subtarea Kanban (`KanbanSubtask`) — esa **no** tiene fecha propia, solo título y `IsDone`. |
| **Recordatorio** (`Reminder`, modelo) | Entidad independiente y persistida (`reminders.json`), con `NotifyAtUtc` obligatorio. También sincronizada con Polar/Supabase. No cuelga de ninguna tarea ni tarjeta. | **No** es lo mismo que la "bandeja de recordatorios" (ver fila siguiente) — comparten nombre en la UI pero son conceptos distintos en el código. |
| **Bandeja de recordatorios** / *Reminder Inbox* (`ReminderInboxViewModel`, panel 📥 del title bar) | Una **proyección de solo lectura** sobre `KanbanCard.DueDateUtc` — no lee `Reminder` en absoluto. Agrupa en `Overdue/Today/Soon`. No se persiste como entidad propia (solo qué se descartó, en `DismissedReminder`). | No hay ningún dato exclusivo de la bandeja que el calendario necesite replicar: todo lo que muestra ya sale de leer las tarjetas Kanban directamente. |
| **`CalendarItem`** (nuevo, este documento) | Interfaz/registro **en memoria**, calculado al vuelo, que unifica tarjeta Kanban + tarea/subtarea Polar + Recordatorio en una forma común para dibujar en las vistas de calendario. | No es una tabla, no se serializa, no tiene `Id` propio persistido (usa el `Id` de su origen). |
| **Ocurrencia fantasma** (*ghost occurrence*) | Una instancia futura *calculada* de una tarea Polar recurrente, mostrada de forma visual distinta, no interactiva salvo para abrir la tarea real. | No es una fila real, no tiene su propio `DueDateUtc` persistido en ningún sitio. |

## 2. Las tres fuentes reales (resumen campo a campo)

> Fuente de verdad exacta: `enki/src/enki/models/KanbanCard.cs`, `models/KanbanSubtask.cs`,
> `models/polar/PolarTask.cs`, `models/polar/PolarSubtask.cs`, `models/Reminder.cs`. Si algo de aquí
> difiere del código real en el momento de implementar, gana el código.

### 2.1 `KanbanCard` (siempre disponible, no depende de vínculo con Polar)

| Campo | Tipo | Relevancia para el calendario |
|---|---|---|
| `Id` | `string` | Identidad del `CalendarItem`. |
| `Title` | `string` | Título mostrado en el chip. |
| `Description` | `string` | Se muestra en el popover de detalle, no en el chip. |
| `Priority` | `KanbanPriority` | Puede influir en orden dentro del día, no en color (ver §5). |
| `DueDateUtc` | `DateTime?` | **El campo que posiciona la tarjeta en el calendario.** `null` → tarjeta "sin fecha", va al panel lateral de no programadas (ver `04-vistas-y-navegacion.md`), nunca a la grilla. |
| `DurationMinutes` | `int?` | **(Addendum post-Fase 5)** Franja horaria opcional: minutos que ocupa la tarjeta a partir de `DueDateUtc` (p. ej. 21:30 + 45 = "21:30–22:15"). `null`/`0` → sin duración, comportamiento puntual de siempre. Solo tiene efecto si `DueDateUtc` lleva hora (no medianoche); campo local, no sincronizado — ver `04-vistas-y-navegacion.md` §5 para el porqué de acotar esto a Kanban. |
| `CreatedUtc` | `DateTime` | No se usa para posicionar, solo informativo. |
| `Subtasks` | `List<KanbanSubtask>` | Se muestran en el detalle del item; **no** generan `CalendarItem`s propios porque `KanbanSubtask` no tiene fecha. |

`KanbanCard.IsPastDue(DateTime)` y `KanbanCard.FormatDueLabel(DateTime)` ya encapsulan la regla de "todo
el día vs. con hora" — **reutilizar tal cual**, no reimplementar ese criterio en el calendario (ver
`docs/notificaciones-y-recordatorios.md`, la sección que documenta por qué existen).

### 2.2 `PolarTask` / `PolarSubtask` (solo si `AppSettings.PolarLinked == true`)

| Campo | Tipo | Relevancia para el calendario |
|---|---|---|
| `Id` / `ListId` | `string` | Identidad y agrupación por lista (color de capa, ver §5). |
| `Title`, `Description` | `string` | Igual que Kanban. |
| `Completed` | `bool` | Un item completado se muestra atenuado/tachado, sigue visible salvo que el filtro "ocultar completadas" esté activo (`09-configuracion-y-ajustes.md`). |
| `Tags` | `List<string>` | Disponible para filtro (`07-filtros-busqueda-y-capas.md`), no se dibuja en el chip por defecto (demasiado ruido visual en celdas pequeñas). |
| `DueDateUtc` | `DateTime?` | Igual criterio que Kanban: `null` → sin fecha, va al panel lateral. |
| `Recurrence` | `PolarRecurrence` | Ver §4 — dispara el cálculo de ocurrencias fantasma. |
| `Priority` | `int` (0–3) | Igual que Kanban, informativo/orden, no color. |
| `OrderIndex`, `ImagePath`, `TimeEstimate` | — | No relevantes para el calendario en esta fase. |
| `IsDeleted` | `bool` | Un `CalendarItem` **nunca** se genera desde una fila con `IsDeleted == true` (papelera). |

`PolarSubtask.DueDateUtc` (a diferencia de `KanbanSubtask`) **sí genera su propio `CalendarItem`**,
enlazado visualmente a su tarea padre (mismo color de lista, indentado o con un glifo distinto en el
chip) — ver `04-vistas-y-navegacion.md`.

### 2.3 `Reminder` (siempre disponible; también sincronizado con Polar si está vinculado)

| Campo | Tipo | Relevancia para el calendario |
|---|---|---|
| `Id`, `Title`, `Description` | — | Igual patrón que las anteriores. |
| `NotifyAtUtc` | `DateTime` | **Obligatorio** (a diferencia de `DueDateUtc`, nunca es `null`) — un `Reminder` siempre tiene fecha, por lo tanto **nunca** aparece en el panel de "sin fecha". |
| `IsCompleted` | `bool` | Igual tratamiento visual que `PolarTask.Completed`. |
| `IsDeleted` | `bool` | Igual que Polar: nunca genera `CalendarItem` si está en papelera. |
| `Icon` | `string` (`MaterialIconKind`) | Se reutiliza como glifo del chip — es el único de los tres orígenes que ya trae icono propio elegido por el usuario. |
| `Latitude/Longitude/Radius/LocationName` | — | Fuera de alcance visual en esta fase (no hay mapa en Enki); si `LocationName` no es nulo, se puede mostrar como texto secundario en el detalle, nada más. |

## 3. El concepto `CalendarItem`

Forma conceptual (no es obligatorio que sea literalmente una interfaz C# — ver
`11-arquitectura-tecnica.md` para la decisión de implementación; aquí se define el **contrato de
información**, no la sintaxis):

| Propiedad | Descripción |
|---|---|
| `SourceId` | El `Id` real del `KanbanCard`/`PolarTask`/`PolarSubtask`/`Reminder` de origen — **nunca se genera un id nuevo**. |
| `SourceType` | `Kanban \| PolarTask \| PolarSubtask \| Reminder`. Determina a qué editor navegar al abrir el detalle (ver `06-creacion-y-edicion-de-items.md`) y qué color de capa usar (§5). |
| `Title` | Copiado del origen. |
| `MomentUtc` | El instante que posiciona el item: `DueDateUtc` (Kanban/Polar/PolarSubtask) o `NotifyAtUtc` (Reminder). |
| `DurationMinutes` | **(Addendum post-Fase 5)** Copiado de `KanbanCard.DurationMinutes`, `null` si `IsAllDay` (una franja no tiene sentido sin hora). Siempre `null` para `PolarTask`/`PolarSubtask`/`Reminder` — ver `04-vistas-y-navegacion.md` §5. |
| `IsAllDay` | `true` si `MomentUtc`, convertido a hora local, cae exactamente en medianoche (mismo criterio que ya usa `KanbanCard.IsPastDue`/`FormatDueLabel` para distinguir "solo día" de "con hora"). |
| `IsCompleted` | De `PolarTask.Completed` / `Reminder.IsCompleted`. Las tarjetas Kanban no tienen estado "completado" propio (su equivalente es estar en la columna final) — tratar como `false` siempre para Kanban salvo que `11-arquitectura-tecnica.md` documente lo contrario tras revisar el código de columnas. |
| `IsPastDue` | Reutiliza `KanbanCard.IsPastDue` para los tres orígenes con fecha (misma fórmula, aplicada al `MomentUtc` correspondiente). |
| `IsGhostOccurrence` | `true` solo para ocurrencias futuras calculadas de una `PolarTask` recurrente (§4). Un item fantasma nunca es `IsCompleted` ni interactivo para arrastrar/redimensionar. |
| `LayerColor` | Ver §5. |
| `HasSubtasksOrChildren` | Para dibujar el indicador correspondiente en el chip. |

## 4. Recurrencia y ocurrencias fantasma

Recordatorio del modelo real (`docs/supabase-sync/02-logica-de-negocio.md`, "Motor de recurrencia"):
una `PolarTask` recurrente **no genera filas nuevas**. Es una sola fila que, al completarse, avanza su
propia `DueDateUtc` a la siguiente ocurrencia (`PolarTaskService.CalculateNextDueDate`,
`PolarTaskService.RollRecurringTasks`). Esto significa que, mirando solo la base de datos, **nunca hay
más de una ocurrencia visible a la vez** de una tarea recurrente — al contrario que un evento `RRULE` de
Google Calendar, que sí tiene instancias futuras reales.

Para que el calendario sea útil como calendario (ver una tarea "todos los lunes" repetida hacia
adelante, no solo el próximo lunes), se define lo siguiente:

- El calendario **calcula** ocurrencias fantasma hacia adelante, encadenando
  `PolarTaskService.CalculateNextDueDate(fecha, recurrencia)` sucesivamente a partir de la
  `DueDateUtc` real de la tarea, **solo dentro del rango de fechas visible** en la vista actual (por
  ejemplo, las celdas del mes mostrado), con un tope duro de seguridad (p. ej. 366 iteraciones) para
  evitar bucles si algún caso de `MON_WED`/`FIRST_DAY_MONTH` degenera.
- Cada ocurrencia fantasma se marca `IsGhostOccurrence = true` y se dibuja de forma visualmente distinta
  (ver `10-diseno-visual-y-componentes.md`: opacidad reducida, borde discontinuo o icono de repetición).
- Un item fantasma **no se puede arrastrar, redimensionar, completar ni eliminar** — al hacer clic, abre
  el editor de la tarea real con una nota tipo "próxima ocurrencia estimada: <fecha>", nunca crea una
  fila nueva ni modifica `DueDateUtc` de la tarea real.
- Este cálculo es **puramente de presentación** — no persiste nada, no llama a `RollRecurringTasks`, no
  interfiere con el job real de rollover al completar la tarea.
- Ver `09-configuracion-y-ajustes.md` para el ajuste que permite desactivar esta previsualización si el
  usuario la encuentra confusa (mostrar solo la ocurrencia real, comportamiento mínimo/literal).

`KanbanCard` no tiene recurrencia (confirmado en el modelo, §2.1) — nunca genera ocurrencias fantasma.

## 5. Modelo de color (capas)

No se inventa un sistema de color nuevo: se reutiliza el que ya existe por origen, para que el color de
un item en el calendario **coincida siempre** con el color que ese mismo item ya tiene en su pantalla de
origen.

| Origen | Color de capa | De dónde sale |
|---|---|---|
| Tarjeta Kanban | Un color fijo de "capa Kanban" (acento neutro del sistema de diseño, p. ej. blanco/gris — Kanban no tiene color por columna hoy). | Constante de la paleta del calendario, ver `10-diseno-visual-y-componentes.md`. |
| Tarea/Subtarea Polar | El `PolarTaskList.Color` de su lista padre. | `PolarTaskStore` → `PolarTaskList` por `ListId`. |
| Recordatorio | Un color fijo de "capa Recordatorios", distinto del de Kanban. | Constante de la paleta del calendario. |

Esto también define de forma natural el modelo de **capas activables/desactivables** de
`07-filtros-busqueda-y-capas.md`: activar/desactivar "Kanban", "Recordatorios", y cada `PolarTaskList`
individualmente por su propio color.

## 6. Items sin fecha ("no programados")

Una `KanbanCard` con `DueDateUtc == null` o una `PolarTask`/`PolarSubtask` con `DueDateUtc == null`
**no genera ningún `CalendarItem` posicionado en la grilla** — en su lugar, alimenta el panel lateral de
"sin fecha" (ver `04-vistas-y-navegacion.md`, §"Panel de no programadas"), que sí es parte del alcance de
esta funcionalidad porque es precisamente lo que Google Calendar (que no tiene noción de tarea sin fecha
propia) no resuelve bien y Notion Calendar/TickTick sí. `Reminder` nunca aparece aquí porque
`NotifyAtUtc` no es nulo en su modelo.

## 7. Preguntas abiertas

_(Ninguna al momento de escribir este documento. Si aparece una durante la implementación, añádela aquí
con el formato: pregunta, opción conservadora elegida para no bloquear, y por qué — según indica
`02-prompt-maestro-de-implementacion.md`, §4.)_
