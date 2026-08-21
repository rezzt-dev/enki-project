# 04 — Vistas y navegación

## Contexto rápido

El calendario se registra como una página nueva en el shell de Enki (`AppPage.Calendar`, junto a
`Dashboard/Notes/Sessions/Kanban/Trash/Settings/Reminders/Notifications` en
`viewmodels/shell/NavItemViewModel.cs`). Este documento define las superficies de esa página: qué vistas
temporales existen, cómo se navega entre fechas, y los dos paneles auxiliares (mini-calendario y
"sin programadas"). Da por leído `03-glosario-y-modelo-de-datos.md` — aquí ya se habla de `CalendarItem`
sin volver a definirlo.

## 1. Layout general de la página

Tres zonas, de izquierda a derecha (patrón de "panel lateral + contenido" ya usado en Kanban/Notes):

```
┌──────────────┬──────────────────────────────────────────────┐
│  Sidebar      │  Barra superior: ◀ Hoy ▶   "Marzo 2026"   [Mes|Semana|Día|Agenda]
│  - Mini-mes   ├──────────────────────────────────────────────┤
│  - Capas      │                                              │
│  - Sin fecha  │              Grilla de la vista activa       │
│               │                                              │
└──────────────┴──────────────────────────────────────────────┘
```

- El sidebar es colapsable (mismo patrón visual que otros paneles laterales de Enki), nunca obligatorio
  para operar el calendario.
- La barra superior es común a las cuatro vistas: navegación temporal a la izquierda, selector de vista
  (segmented control, estilo botón outline del sistema de diseño) a la derecha.

## 2. Vista Mes (vista por defecto)

- Grilla de 6 filas × 7 columnas (42 celdas), **reutilizando directamente el patrón ya implementado en**
  `viewmodels/kanban/KanbanCalendarDayViewModel.cs` (mismo motivo: 6 filas fijas para que cambiar de mes
  nunca redimensione la ventana). No reimplementar ese cálculo de días — extraerlo a un lugar compartido
  si hace falta usarlo en dos sitios (ver `11-arquitectura-tecnica.md`).
- Cada celda muestra: número de día, indicador de "hoy" (mismo tratamiento visual que ya tiene
  `KanbanCalendarDayViewModel.IsToday`), y hasta **N chips** de `CalendarItem` (N configurable/fijo según
  espacio, recomendado 3–4 antes de colapsar).
- Cuando una celda tiene más items de los que caben: chip final `+N más`, clic abre un popover con la
  lista completa de esa celda (reutiliza `Border.dropdown-popover`) — mismo patrón que Apple Calendar/
  Google Calendar, evita que la celda crezca y rompa la grilla.
- Los días fuera del mes actual (relleno de la grilla 42) se muestran atenuados, igual que
  `IsCurrentMonth == false` ya hace en el selector de fecha de Kanban.
- Orden de los chips dentro de una celda: por hora (si tiene) y luego todo-el-día primero o al final —
  decidir en implementación siguiendo el mismo criterio visual que Google Calendar (todo-el-día arriba).

## 3. Vista Semana

- 7 columnas (un día cada una) × grilla de horas verticales (00:00–23:00, con scroll inicial centrado en
  el horario laboral configurado, ver `09-configuracion-y-ajustes.md`).
- Fila superior fija con los items "todo el día" de cada columna (igual patrón que Google Calendar: los
  eventos sin hora no ocupan una franja horaria, se quedan arriba).
- Línea horizontal roja/acento (color de peligro del sistema de diseño) marcando la hora actual,
  visible solo en la columna de "hoy" si "hoy" está dentro de la semana mostrada — inspirado en
  Fantastical/Google Calendar, útil de verdad en una app de escritorio con la ventana abierta todo el
  día.
- Altura de una franja horaria: suficiente para leer un chip de una línea; los items con duración (ver
  §5 más abajo, "duración") ocupan varias franjas verticalmente.

## 4. Vista Día

- Misma grilla de horas que la vista Semana pero con una sola columna — útil para días con muchos items
  donde la vista semana los amontona demasiado horizontalmente.
- Mismo indicador de hora actual.

## 5. ¿Qué significa "duración" para un `CalendarItem`?

**Actualización (addendum post-Fase 5, ver `00-README.md` "Estado de avance"):** esta sección documentaba
originalmente que ninguna de las tres fuentes tenía duración propia y que el tema se dejaba fuera a
propósito. Eso cambió, con alcance **deliberadamente acotado a Kanban** — ver la decisión completa y su
razonamiento en `00-README.md`:

- `KanbanCard` tiene ahora `DurationMinutes` (`int?`, minutos, opcional) — modelo local, sin sincronizar,
  así que añadirlo no toca ni el esquema de Supabase ni la app Android (ver `docs/supabase-sync/`).
- `PolarTask`/`PolarSubtask` y `Reminder` **siguen sin duración**: ambos sincronizan con Supabase/Android,
  y esa expansión de alcance se dejó fuera a propósito para no tener que coordinar un cambio de contrato
  con una app fuera de este repo. `Reminder` además es conceptualmente un instante ("avisarme a las X"),
  no una tarea con inicio y fin.
- Por lo tanto `CalendarItem.DurationMinutes` (ver `03-glosario-y-modelo-de-datos.md` §3) solo se rellena
  para `SourceType == Kanban`; para los demás orígenes sigue siendo siempre `null`.

Reglas de dibujo en Semana/Día (`CalendarWeekColumnViewModel`/`CalendarTimedItemViewModel`):

- Un item **sin** duración real (`DurationMinutes == null`) se sigue dibujando con la **duración fija
  mínima visual** de siempre (40 minutos de alto en la grilla) — convención de dibujo pura, sin cambios
  respecto a antes de este addendum.
- Un `KanbanCard` **con** duración real se dibuja con su alto real (`DurationMinutes` minutos), con un
  suelo de legibilidad de 30 minutos (`CalendarTimedItemViewModel.MinimumRealDurationFloorMinutes`) para
  que una franja muy corta (p. ej. 5 minutos) siga teniendo sitio para el título y las dos horas (ver
  siguiente punto) — ese suelo es solo visual, nunca se reescribe el valor real guardado.
- El chip de Semana/Día muestra la hora de **inicio arriba** y la hora de **fin abajo, junto al asa de
  redimensionado** (`CalendarItemViewModel.HasDuration`/`EndTimeLabel`) en vez de las dos apretadas en una
  sola línea — así ambas quedan ancladas donde realmente ocurren dentro del bloque, y siguen siendo
  legibles aunque la columna sea estrecha (vista Semana con 7 columnas). El rango combinado
  ("21:30–22:15", `CalendarItemViewModel.TimeLabel`) se sigue usando tal cual en Mes/Agenda/el popover de
  detalle, donde no hay un alto propio del bloque con el que asociar visualmente cada hora por separado.
- Una duración solo tiene sentido junto a una hora real: un `KanbanCard` "todo el día"
  (`IsAllDay == true`) nunca dibuja franja aunque `DurationMinutes` tenga un valor guardado (se ignora al
  proyectar el `CalendarItem`, ver `CalendarAggregationService.BuildItems`) — un item "todo el día" se
  sigue dibujando en la franja superior de Semana/Día, nunca dentro de la grilla horaria.
- La duración se puede ajustar de dos formas — arrastrando el borde inferior del bloque en Semana/Día
  (`05-interacciones-y-microinteracciones.md` §2b) o desde el campo "Duración (franja horaria)" del editor/
  creación rápida (`06-creacion-y-edicion-de-items.md`) — ambas snapean a pasos de **5 minutos**, el mismo
  grano que ya usaba el selector de minutos de "Agregar hora" y el campo `TimeEstimate` de Polar.

## 6. Vista Agenda

- Lista vertical de días con items, agrupados por fecha con un separador de encabezado por día (formato
  "Lunes 3 de marzo"), desplazándose hacia adelante desde "hoy" (o desde la fecha seleccionada en el
  mini-calendario).
- Días sin ningún item **no se muestran** (a diferencia de Mes/Semana, que sí muestran celdas vacías) —
  es la vista pensada para "qué tengo pendiente", no para "cómo se ve el mes".
- Es la vista más parecida a la bandeja de recordatorios existente, pero a diferencia de esa bandeja
  (que solo mira Kanban y solo mira "próximo a vencer"), la Agenda muestra las tres fuentes y no tiene
  ventana de tiempo limitada — se puede seguir haciendo scroll hacia el futuro. Ver
  `08-notificaciones-e-integracion.md` para cómo conviven ambas superficies sin pisarse.

## 7. Mini-calendario (sidebar)

- Versión compacta de la grilla de Mes (también reutilizando el patrón `KanbanCalendarDayViewModel`),
  sin chips de items — solo un punto/indicador pequeño en los días que tienen al menos un `CalendarItem`
  (mismo espíritu que el indicador de "hay contenido" de muchos calendarios de escritorio).
- Clic en un día: navega la vista principal a ese día (si está en Semana/Día/Agenda) o simplemente
  resalta el día (si está en Mes, ya visible).
- Sirve como navegación rápida entre meses sin tener que pasar por la vista Mes completa.

## 8. Panel de "no programadas"

- Lista de `CalendarItem`s sin fecha (`KanbanCard`/`PolarTask`/`PolarSubtask` con `DueDateUtc == null` —
  ver `03-glosario-y-modelo-de-datos.md` §6), agrupadas por origen (Kanban / cada lista Polar).
- Cada fila es arrastrable hacia cualquier celda/franja de la grilla principal — soltar asigna
  `DueDateUtc` (ver `05-interacciones-y-microinteracciones.md` para el detalle del gesto). Este panel es
  la razón principal por la que el calendario es más útil que Google Calendar para el caso de uso de
  Enki: convierte "tengo una tarea sin fecha" en "arrástrala al día que quieras" sin abrir ningún editor.
- Vacío cuando no hay ningún item sin fecha — se colapsa/oculta automáticamente en vez de mostrar un
  estado vacío permanente que reste espacio a la grilla.

## 9. Navegación temporal

- Botones `◀`/`▶`: retroceden/avanzan una unidad de la vista activa (mes en Mes, semana en Semana, día
  en Día, "una página de agenda" en Agenda).
- Botón "Hoy": vuelve la vista a la fecha actual sin cambiar de vista.
- Etiqueta central: muestra el rango visible ("Marzo 2026" en Mes, "3–9 mar 2026" en Semana, "Lunes 3 de
  marzo" en Día), clic abre el mini-calendario si está colapsado, para saltar directo a una fecha lejana.
- Cambiar de vista (Mes↔Semana↔Día↔Agenda) **conserva la fecha de referencia** — si estoy viendo el 15
  de marzo en Mes y cambio a Día, aterrizo en el día 15, no en "hoy".

## 10. Atajos de teclado (alcance recomendado, no bloqueante)

Consistente con que Enki es una app de escritorio donde los atajos de teclado sí se esperan:

| Atajo | Acción |
|---|---|
| `←` / `→` | Periodo anterior/siguiente (según vista activa) |
| `T` | Ir a "Hoy" |
| `M` / `S` / `D` / `A` | Cambiar a vista Mes/Semana/Día/Agenda |
| `N` | Abrir creación rápida (ver `06-creacion-y-edicion-de-items.md`) |
| `Esc` | Cerrar cualquier popover abierto (detalle, creación rápida, "+N más") |

Implementar en la fase de pulido (ver `12-plan-de-implementacion-por-fases.md`), no bloqueante para el
resto de vistas.
