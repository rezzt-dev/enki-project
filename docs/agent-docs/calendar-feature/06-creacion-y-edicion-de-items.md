# 06 — Creación y edición de items

## Contexto rápido

El calendario no tiene su propio formulario de edición completo: reutiliza los tres editores que ya
existen en Enki (`KanbanCardEditorViewModel`, `PolarTaskEditorViewModel`, `ReminderEditorViewModel`).
Lo único nuevo es (a) el popover de **creación rápida**, que decide a cuál de los tres tipos pertenece lo
que se está creando, y (b) la navegación desde el calendario hacia el editor completo correspondiente.
Da por leídos `03-glosario-y-modelo-de-datos.md` y `05-interacciones-y-microinteracciones.md`.

## 1. Popover de creación rápida

Se abre desde: clic en celda/franja vacía, atajo `N` (con la fecha de "hoy" o de la fecha actualmente
enfocada), o botón "+" de la barra superior.

Contenido mínimo:

1. Campo de texto para el título (foco automático al abrir).
2. Selector de tipo, en forma de tres botones tipo pestaña (mismo lenguaje visual que el selector de
   vista Mes/Semana/Día/Agenda):
   - **Tarjeta Kanban** — siempre disponible.
   - **Tarea Polar** — solo visible si `AppSettings.PolarLinked == true`; si no está vinculado, ese botón
     directamente no se renderiza (no se muestra deshabilitado con explicación — no añade valor y ensucia
     el popover para el 100% de los usos sin Polar).
   - **Recordatorio** — siempre disponible.
3. Fecha/hora, pre-rellenada a partir de dónde se hizo clic (§"Clic en celda/franja vacía" del documento
   anterior), editable con el mismo selector de calendario que ya existe (`Border.calendar-popover` +
   `KanbanCalendarDayViewModel`).
3b. **Duración (franja horaria), addendum post-Fase 5 — solo si el tipo elegido es "Tarjeta Kanban" y hay
    hora**: toggle "Duración (franja horaria)" + selector de minutos en pasos de 5, con una etiqueta
    ("21:30 – 22:15") que muestra el rango resultante sin que el usuario tenga que calcularlo. Apagado por
    defecto — la mayoría de items no necesitan franja, solo un punto en el tiempo, así que el campo extra
    no aparece hasta que el usuario activa "Agregar hora". No aparece para Tarea Polar/Recordatorio
    (ver `03-glosario-y-modelo-de-datos.md` §2.1/§3 para por qué el alcance se acotó a Kanban) ni sin hora
    (una franja no tiene sentido sin un punto de partida). Una vez creado, el bloque se puede seguir
    ajustando arrastrando su borde inferior en Semana/Día (`05-interacciones-y-microinteracciones.md` §2b).
4. Si el tipo elegido es "Tarjeta Kanban": selector de columna destino (por defecto, la primera columna
   no-final del tablero).
5. Si el tipo elegido es "Tarea Polar": selector de lista destino (`PolarTaskList`), por defecto la
   última usada o la primera disponible.
6. Botón "Crear" (llama al método de creación del servicio correspondiente:
   `KanbanService`/`PolarTaskService.CreateTask`/`ReminderService.Create`) y botón "Más opciones", que
   guarda con los datos mínimos ya introducidos y abre inmediatamente el editor completo para completar
   el resto (descripción, prioridad, subtareas, recurrencia, etiquetas…) — así el popover rápido nunca es
   un techo, siempre hay un camino a las opciones completas sin perder lo ya escrito.

## 2. Qué campos NO expone el popover rápido (y por qué)

Descripción, prioridad, etiquetas, subtareas, recurrencia y geolocalización del recordatorio se dejan
fuera del popover rápido a propósito — son datos que, en la práctica, el usuario rara vez define en el
mismo instante en que agenda algo. Esto sigue el mismo principio que ya aplican los "quick add" de
TickTick/Fantastical (ver `01-contexto-y-vision-de-producto.md` §3): capturar rápido, refinar después
solo si hace falta.

## 3. Edición: navegar al editor completo existente

Al pulsar "Editar" desde el popover de detalle de un `CalendarItem` (`05-interacciones-y-microinteracciones.md`
§4), el calendario **no reimplementa un formulario de edición** — navega a la pantalla de origen y abre
ahí el editor real, exactamente con el mismo mecanismo que ya usa
`MainWindowViewModel.OpenCardFromReminder → Navigate(AppPage.Kanban) + KanbanViewModel.OpenCardById` para
la bandeja de recordatorios:

| `SourceType` | Navega a | Abre |
|---|---|---|
| `Kanban` | `AppPage.Kanban` | `KanbanCardEditorViewModel` de esa tarjeta (reutilizar el mismo `OpenCardById` ya existente, no duplicarlo). Incluye el campo "Duración (franja horaria)" del addendum post-Fase 5 (§1, punto 3b) — no hay un campo separado en el calendario para esto, es literalmente el mismo editor. |
| `PolarTask` / `PolarSubtask` | La pantalla "Gestión de tareas" (Polar) | `PolarTaskEditorViewModel` de la tarea (para una subtarea, abre la tarea padre con esa subtarea resaltada/expandida). |
| `Reminder` | El panel de Recordatorios (📥) o una vista de edición dedicada si existe | `ReminderEditorViewModel` de ese recordatorio. |

Esto garantiza que **toda** la superficie de edición avanzada (recurrencia, prioridad, etiquetas,
subtareas, geolocalización) siga viviendo en un solo sitio por tipo de dato, sin divergencia entre "cómo
edito una tarea Polar desde Polar" y "cómo la edito desde el calendario".

## 4. Edición de recurrencia desde el calendario

El calendario nunca ofrece un control propio para cambiar la recurrencia de una `PolarTask` — ese campo
solo se edita desde el editor completo de Polar (§3). Justificación: la recurrencia interactúa con el
estado `Completed` y el rollover (`docs/supabase-sync/02-logica-de-negocio.md`), y mantener esa lógica en
un único punto de entrada evita que el calendario tenga que replicar reglas de negocio que no le
corresponden.

## 5. Edición de subtareas desde el calendario

El popover de detalle de una `PolarTask`/`KanbanCard` con subtareas las lista en solo lectura, con
checkbox para marcarlas hechas/pendientes (llamando a
`PolarTaskService.SetSubtaskCompleted`/equivalente Kanban) — **no** se permite añadir/eliminar/renombrar
subtareas desde ese popover; esa edición estructural se hace desde el editor completo (§3), consistente
con el principio de "el popover rápido no es un techo, pero tampoco duplica el editor entero".

## 6. Validaciones y casos límite

- Título vacío: el botón "Crear" queda deshabilitado, mismo patrón de validación que ya usan los editores
  existentes (no se introduce un sistema de validación nuevo).
- Crear sin fecha (posible desde el atajo `N` sin haber hecho clic en ninguna celda): el item creado va
  directo al panel de "no programadas" (`04-vistas-y-navegacion.md` §8) en vez de fallar o forzar una
  fecha por defecto.
- Cambiar el tipo seleccionado en el popover (de "Tarjeta Kanban" a "Recordatorio", por ejemplo) antes de
  guardar: limpia los campos específicos del tipo anterior (columna/lista destino) sin pedir
  confirmación — no hay pérdida de datos real porque nada se ha guardado todavía.
