# 12 — Plan de implementación por fases

## Contexto rápido

Este es el documento operativo que convierte todo lo anterior en trabajo ejecutable en bloques
independientes. Cada fase es completable, compilable y usable por sí misma — nunca se deja la app en un
estado roto entre fases. El estado de avance se marca en `00-README.md`, no aquí (para que haya un único
lugar donde mirar "por dónde vamos").

Cada fase indica qué documentos de esta carpeta hay que tener leídos antes de empezarla.

## Fase 0 — Esqueleto de navegación

**Documentos necesarios:** `11-arquitectura-tecnica.md` §2.

**Trabajo:**
- Añadir `Calendar` a `AppPage` (`viewmodels/shell/NavItemViewModel.cs`).
- Crear `CalendarViewModel`/`CalendarView` mínimos: una página vacía navegable desde el sidebar, con
  título "Calendario".
- Cablear en `MainWindowViewModel` igual que las demás páginas.

**Criterio de "hecho":** el usuario puede hacer clic en "Calendario" en el nav y ver una página vacía sin
errores; el resto de la app sigue funcionando exactamente igual que antes.

## Fase 1 — Vista Mes de solo lectura

**Documentos necesarios:** `03-glosario-y-modelo-de-datos.md`, `04-vistas-y-navegacion.md` §1–2,
`10-diseno-visual-y-componentes.md` §2/§4, `11-arquitectura-tecnica.md` §3–4, §6.

**Trabajo:**
- `CalendarAggregationService` construyendo `CalendarItem`s a partir de las tres fuentes (sin ocurrencias
  fantasma todavía — eso es Fase 4b si se prefiere separarlo, o se puede incluir aquí si resulta simple
  una vez escrito el resto).
- Vista Mes con la grilla de 42 celdas (reutilizando o extrayendo el patrón de
  `KanbanCalendarDayViewModel`, ver `11-arquitectura-tecnica.md` §4), chips de solo lectura (clic abre un
  popover de detalle de solo lectura, sin editar todavía).
- Navegación temporal básica: ◀/▶/Hoy, etiqueta de mes.
- Suscripción en vivo a `BoardChanged`/`DataChanged` (aún no a `ReminderService`, si su evento de cambio
  todavía no existe — puede añadirse en esta misma fase o diferirse a la Fase 3, según convenga).

**Criterio de "hecho":** abrir Calendario muestra el mes actual con las tarjetas Kanban, tareas Polar (si
`PolarLinked`) y recordatorios que caen ese mes, correctamente posicionados, sin poder todavía crear ni
mover nada. Un cambio hecho en Kanban se refleja al volver al calendario.

## Fase 2 — Vistas Semana/Día/Agenda + mini-calendario

**Documentos necesarios:** `04-vistas-y-navegacion.md` §3–7, `10-diseno-visual-y-componentes.md` §3/§6.

**Trabajo:**
- Selector de vista funcional (Mes ya existe de la Fase 1).
- Vista Semana y Día: grilla de horas, franja de "todo el día", línea de "ahora".
- Vista Agenda: lista agrupada por día.
- Mini-calendario en el sidebar, navegación cruzada con la vista principal.

**Criterio de "hecho":** las cuatro vistas muestran el mismo conjunto de datos de forma consistente
entre sí (un item vencido se ve vencido en las cuatro; un item de hoy aparece en las cuatro si el rango
las cubre); cambiar de vista conserva la fecha de referencia.

## Fase 3 — Interacciones: crear, editar, arrastrar

**Documentos necesarios:** `05-interacciones-y-microinteracciones.md`, `06-creacion-y-edicion-de-items.md`,
`11-arquitectura-tecnica.md` §5.

**Trabajo:**
- Evento de cambio en `ReminderService` (si no se hizo en Fase 1) y suscripción del calendario.
- Popover de creación rápida (§1–2 y §6 del doc 06), con navegación a "más opciones" → editor completo.
- Navegación desde el popover de detalle hacia los tres editores existentes (doc 06 §3).
- Drag & drop para reprogramar (doc 05 §2), reutilizando el mecanismo ya usado en Kanban/Notes.
- Completar/eliminar desde el popover de detalle (doc 05 §5–6).
- Menú contextual (doc 05 §7).

**Criterio de "hecho":** se puede crear un item de cualquiera de los tres tipos desde el calendario,
arrastrarlo a otra fecha/hora, completarlo y eliminarlo, y esos cambios son visibles de inmediato en
Kanban/Gestión de tareas/bandeja de recordatorios sin recargar nada.

## Fase 4 — Capas, filtros, búsqueda, recurrencia

**Documentos necesarios:** `07-filtros-busqueda-y-capas.md`, `03-glosario-y-modelo-de-datos.md` §4.

**Trabajo:**
- Panel de capas por origen (Kanban/Recordatorios/cada lista Polar), persistido en `AppSettings`.
- Búsqueda y filtros secundarios (prioridad, etiquetas, ocultar completadas).
- Ocurrencias fantasma de recurrencia (si no se incluyó en Fase 1), con su interruptor de
  mostrar/ocultar.
- Panel de "no programadas" con arrastre hacia la grilla (doc 04 §8, doc 05 §2).

**Criterio de "hecho":** desactivar una capa oculta sus items en las cuatro vistas y en el panel de "no
programadas" a la vez; una tarea Polar recurrente muestra sus próximas ocurrencias previsualizadas y
correctamente distinguidas de la real; arrastrar un item del panel de "no programadas" a un día le asigna
esa fecha.

## Fase 5 — Ajustes, integración con notificaciones, pulido

**Documentos necesarios:** `08-notificaciones-e-integracion.md`, `09-configuracion-y-ajustes.md`,
`10-diseno-visual-y-componentes.md` (checklist completo), `04-vistas-y-navegacion.md` §10 (atajos).

**Trabajo:**
- Nueva sección "Calendario" en Ajustes, todos los campos de `09-configuracion-y-ajustes.md` §1.
- Indicadores de vencido/hoy consistentes con el resto de la app (doc 08 §3).
- Reacción en vivo a vincular/desvincular Polar (doc 08 §5).
- Atajos de teclado (doc 04 §10).
- Animaciones de transición entre vistas/periodos (doc 10 §8).
- Recorrer el checklist visual de `10-diseno-visual-y-componentes.md` §9 y corregir cualquier
  desviación del sistema de diseño.

**Criterio de "hecho":** checklist de `13-pruebas-y-checklist-de-calidad.md` recorrido a mano de
principio a fin sin hallazgos bloqueantes.

## Addendum post-Fase 5 — Franja horaria (inicio-fin) para tarjetas Kanban

**Documentos necesarios:** `00-README.md` ("Addendum post-Fase 5", el porqué del alcance acotado a
Kanban), `03-glosario-y-modelo-de-datos.md` §2.1/§3, `04-vistas-y-navegacion.md` §5,
`05-interacciones-y-microinteracciones.md` §2b, `06-creacion-y-edicion-de-items.md` §1/§3,
`10-diseno-visual-y-componentes.md` §4, `11-arquitectura-tecnica.md` §12 (decisiones de implementación
detalladas).

**Trabajo:**
- `KanbanCard.DurationMinutes` (`int?`, minutos) + propagación a `CalendarItemViewModel.DurationMinutes`
  en `CalendarAggregationService.BuildItems` (`null` si el item es "todo el día").
- `CalendarTimedItemViewModel`: alto real cuando hay duración (con suelo de 15 min), convención fija de
  siempre en caso contrario. `CalendarItemViewModel.TimeLabel` muestra el rango completo cuando aplica.
- Asa de redimensionado en el chip de Semana/Día (`Border.calendar-chip-resize-handle`), gesto de arrastre
  en `CalendarView.axaml.cs` (`ResizeHandle_*`) con snap de 5 minutos, tope al final del día —
  `CalendarViewModel.ResizeItem` persiste el resultado.
- Campo "Duración (franja horaria)" en `KanbanCardEditorViewModel`/`KanbanView.axaml` y en
  `CalendarQuickCreateViewModel`/`CalendarView.axaml` (creación rápida), ambos con stepper de ±5 minutos y
  una etiqueta de solo lectura con el rango resultante.
- Tests xUnit: `CalendarTimedItemViewModelTests` (altura/suelo/`TimeLabel`/`CanResize`) y las adiciones a
  `CalendarViewModelTests` (`RoundToFiveMinutes`, `MaxDurationMinutesSameDay`).

**Criterio de "hecho":** `dotnet build`/`dotnet test` en verde (91/91 en el momento de escribir esto);
crear una tarjeta Kanban con hora + duración desde la creación rápida y desde el editor completo, verla
como bloque con alto real en Semana/Día, redimensionarla arrastrando su borde inferior, y confirmar que
arrastrar-para-mover (`MoveItem`) conserva la duración al cambiar de día/hora.

## Fase 6 (futura, opcional — no implementar sin instrucción explícita)

Ideas que quedaron fuera de alcance en `01-contexto-y-vision-de-producto.md` §4 y que **no** se
implementan como parte de esta funcionalidad salvo que el usuario lo pida explícitamente más adelante:

- Entidad `CalendarEvent` nativa (bloques de tiempo/citas sin ser tarea).
- Import/export ICS, integración con Google Calendar/Outlook/iCloud.
- ~~Duración real (campo de fecha de fin) en `KanbanCard`/`PolarTask`/`Reminder`, habilitando
  redimensionar la duración de un bloque arrastrando su borde.~~ **Hecho para `KanbanCard`** en el
  "Addendum post-Fase 5" de arriba. Sigue pendiente (fuera de alcance salvo instrucción explícita) para
  `PolarTask`/`PolarSubtask`/`Reminder` — ambos sincronizan con Supabase/la app Android, así que añadirles
  duración implica coordinar un cambio de contrato fuera de este repo (ver `00-README.md`).
- Selección múltiple avanzada y operaciones batch (doc 05 §8).
- Badge de vencidos en el nav principal (doc 08 §4), si no se hizo ya como parte de la Fase 5.

Si se decide abordar alguna de estas, empieza por un documento nuevo `14-...md` en esta misma carpeta que
la desarrolle con el mismo nivel de detalle que los documentos 03–11, no por código directo.
