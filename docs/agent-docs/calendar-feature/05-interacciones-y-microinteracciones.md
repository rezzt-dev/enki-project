# 05 — Interacciones y microinteracciones

## Contexto rápido

Este documento detalla **cada gesto** que el usuario puede hacer sobre un `CalendarItem` o una celda
vacía, y qué operación real dispara sobre el modelo de origen (`KanbanCard`/`PolarTask`/`PolarSubtask`/
`Reminder`). Da por leídos `03-glosario-y-modelo-de-datos.md` y `04-vistas-y-navegacion.md`.

## 1. Principio general: el calendario nunca inventa un dato nuevo

Toda interacción de esta lista termina en una llamada al servicio real correspondiente
(`KanbanService`/`PolarTaskService`/`ReminderService`), mutando el objeto de origen y llamando a su
`Save()` — exactamente el mismo patrón que ya usan los editores existentes
(`KanbanCardEditorViewModel`, `PolarTaskEditorViewModel`, `ReminderEditorViewModel`). El calendario no
mantiene su propio estado persistente de "dónde está cada cosa": siempre pregunta a la fuente.

## 2. Arrastrar y soltar (drag & drop) — reprogramar fecha

- **Gesto**: arrastrar un chip de `CalendarItem` desde su celda/franja actual y soltarlo sobre otra celda
  (vista Mes) u otra franja horaria (vista Semana/Día).
- **Reutilización técnica obligatoria**: Enki ya implementa drag & drop en dos sitios
  (`views/kanban/KanbanView.axaml(.cs)` para mover tarjetas entre columnas, `views/notes/NotesView.axaml(.cs)`
  para mover notas entre carpetas). El calendario debe seguir el mismo mecanismo de Avalonia
  (`DragDrop`/adjuntos de comportamiento), no introducir una librería de terceros ni un patrón distinto.
- **Efecto**:
  - Soltar sobre una celda de día (vista Mes, o franja "todo el día" de Semana): actualiza
    `DueDateUtc`/`NotifyAtUtc` al nuevo día, **conservando la hora original** si la tenía (mismo criterio
    que ya aplica `KanbanCardEditorViewModel` al combinar fecha+hora — no combinar por casualidad al
    revés y perder la hora).
  - Soltar sobre una franja horaria concreta (vista Semana/Día): actualiza fecha **y** hora al punto
    exacto soltado (redondeado al intervalo de franja, p. ej. 15 o 30 minutos).
- **Restricciones**:
  - Un item con `IsGhostOccurrence == true` (ver `03-glosario-y-modelo-de-datos.md` §4) **no es
    arrastrable** — no tiene una fila real donde escribir el cambio.
  - Arrastrar una `PolarTask`/`PolarSubtask` cuando `AppSettings.PolarLinked == false` no puede ocurrir
    (esos items ni siquiera se generan, ver `07-filtros-busqueda-y-capas.md`).
- **Feedback visual durante el arrastre**: la celda/franja de destino se resalta (borde blanco sólido,
  patrón de "inversión" del sistema de diseño) mientras el cursor está encima — mismo lenguaje que ya usa
  el hover de tarjetas Kanban.

## 2b. Redimensionar — ajustar la franja horaria (addendum post-Fase 5, solo Kanban)

- **Gesto**: en Semana/Día, arrastrar el asa del borde inferior de un chip posicionado
  (`CalendarTimedChipTemplate`) hacia arriba/abajo. El asa solo es visible cuando
  `CalendarItemViewModel.CanResize` es `true` — una tarjeta Kanban real, con hora, no fantasma (ver
  `03-glosario-y-modelo-de-datos.md` §3) — para el resto de orígenes (Polar/Recordatorio) o para un item
  "todo el día" no hay campo donde persistir el resultado, así que el asa directamente no se dibuja.
- **Asa siempre visible, no solo en hover**: a diferencia de otras micro-interacciones de este documento,
  la marca del asa (una línea horizontal corta bajo el chip) se ve siempre que `CanResize` es verdadero,
  no solo al pasar el cursor — Enki no tiene ningún tour de onboarding aparte, así que el propio chip tiene
  que enseñar el gesto la primera vez que el usuario lo ve, en vez de depender de que lo descubra pasando
  el ratón por casualidad. El cursor cambia a redimensionado vertical al pasar por encima, y el `ToolTip`
  del asa lo confirma en texto ("Arrastra para ajustar la duración").
- **Snap de 5 minutos**: igual que el resto de la grilla ya redondea a 15 minutos al soltar un arrastre de
  movimiento (§2), redimensionar redondea a pasos de **5 minutos** — el mismo grano que ya usaba el
  selector de minutos de "Agregar hora" en el editor y `PolarTask.TimeEstimate`.
- **Tope**: la duración no puede cruzar medianoche — el máximo es "minutos hasta las 24:00 del mismo día"
  desde la hora de inicio, con un mínimo de 5 minutos. Alargar/acortar nunca cambia `DueDateUtc`, solo
  `DurationMinutes`.
- **Efecto**: al soltar, escribe `KanbanCard.DurationMinutes` y llama a `KanbanService.Save()`
  (`CalendarViewModel.ResizeItem`) — el bloque se redibuja con su alto real en el siguiente refresco
  disparado por `KanbanService.BoardChanged`. Durante el arrastre no hay ida y vuelta con el servicio: el
  alto se mueve en pantalla directamente (código-behind, `CalendarView.axaml.cs`), y solo el resultado
  final al soltar el puntero se persiste — mismo espíritu que el resto de gestos de este documento (§1:
  "termina en una llamada al servicio real").

## 3. Clic en celda/franja vacía — crear en esa fecha

- **Gesto**: clic simple sobre un hueco vacío de la grilla (Mes: una celda de día; Semana/Día: una franja
  horaria).
- **Efecto**: abre el popover de creación rápida (`06-creacion-y-edicion-de-items.md`) con la fecha (y
  hora, si aplica) ya pre-rellenada a partir de dónde se hizo clic.
- **Variante "clic y arrastra" (Semana/Día) — sin implementar**: arrastrar verticalmente desde una franja
  antes de soltar, para definir de una vez hora de inicio y duración al crear, sigue sin implementarse —
  el popover de creación rápida ya cubre "poner duración" con su propio campo (ver
  `06-creacion-y-edicion-de-items.md` §1), y el bloque recién creado se puede redimensionar arrastrando su
  borde inferior justo después (§2b). Sigue siendo opcional (fase de pulido) si se quisiera añadir más
  adelante como atajo — el clic simple + campo de duración del popover ya cubre el caso principal.

## 4. Clic en un `CalendarItem` existente — ver detalle

- **Gesto**: clic simple sobre un chip.
- **Efecto**: abre un popover de detalle compacto (reutiliza `Border.calendar-popover`) con título,
  descripción corta, subtareas si las tiene, y accesos directos a las acciones de §5–§7. Desde ahí, un
  botón "Editar" abre el editor completo correspondiente al `SourceType` (ver
  `06-creacion-y-edicion-de-items.md`).
- Para un item fantasma (`IsGhostOccurrence == true`): el popover muestra la nota "Próxima ocurrencia
  estimada" y solo ofrece "Ver tarea" (abre la tarea real), sin botón de completar/eliminar/mover.

## 5. Completar / marcar como hecho

- Disponible directamente desde el popover de detalle (checkbox), sin necesidad de abrir el editor
  completo — mismo espíritu que ya tiene el checkbox de una tarjeta Kanban o de un item de Polar en su
  vista propia.
- `PolarTask`: llama al mismo camino que ya usa `PolarTaskService.SetTaskCompleted` — **importante**: si
  la tarea tiene `Recurrence != None`, completar dispara el mismo comportamiento de rollover que ya existe
  (no es responsabilidad del calendario reimplementarlo, solo invocar el servicio existente y refrescar
  la vista al recibir `DataChanged`).
- `Reminder`: `ReminderService.ToggleCompleted`.
- `KanbanCard`: no tiene un campo de "completado" propio (ver `03-glosario-y-modelo-de-datos.md` §3) —
  si se quiere ese comportamiento desde el calendario, la acción equivalente es mover la tarjeta a la
  columna final del tablero; **no** añadir un campo `Completed` nuevo a `KanbanCard` solo para esto sin
  antes confirmarlo como cambio de alcance (ver `01-contexto-y-vision-de-producto.md` §4). En esta fase,
  el popover de un `CalendarItem` de origen Kanban simplemente no muestra checkbox de completar.

## 6. Eliminar

- Disponible desde el popover de detalle y desde el menú contextual (§8).
- Sigue el mismo criterio de soft-delete/papelera que ya usa cada servicio (`IsDeleted` +
  `DeletedAtUtc`), nunca un borrado físico directo desde el calendario — mismo patrón que
  `docs/supabase-sync/` documenta para Polar y que `Reminder`/`KanbanCard` ya siguen.

## 7. Menú contextual (clic derecho)

Sobre un `CalendarItem`: "Ver detalle", "Editar", "Completar/Marcar pendiente" (si aplica, ver §5),
"Eliminar", "Ir a Kanban/Gestión de tareas" (navega a la pantalla de origen y abre esa tarjeta/tarea
seleccionada — mismo patrón que ya usa `OpenCardFromReminder` en `MainWindowViewModel` para la bandeja de
recordatorios, ver `docs/notificaciones-y-recordatorios.md`).

Sobre una celda/franja vacía: "Crear tarjeta Kanban aquí", "Crear tarea Polar aquí" (solo si
`PolarLinked`), "Crear recordatorio aquí" — atajo directo a cada tipo sin pasar por el selector del
popover de creación rápida.

## 8. Selección múltiple (alcance opcional, fase de pulido)

- `Ctrl`/`Shift` + clic para seleccionar varios `CalendarItem`s a la vez, permitiendo arrastrar el grupo
  entero a una nueva fecha manteniendo los desplazamientos relativos entre ellos.
- No bloqueante para el resto de la funcionalidad — documentado aquí para que, si se implementa, siga el
  mismo patrón de mutación directa + `Save()` por cada item afectado, no una operación batch nueva en los
  servicios.

## 9. Estados de hover y foco

Consistente con `docs/sistema-diseno-web.md` §6.2 (micro-interacciones constantes del sistema de
diseño): un chip en hover se eleva ligeramente (`translateY` sutil) y su borde pasa a blanco sólido;
transición de 300ms `CubicEaseOut`, igual que el resto de tarjetas de Enki. Foco por teclado (navegación
con flechas entre chips, alcance opcional) debe ser visualmente distinguible del hover sin depender del
`AccentColor` azul por defecto de Fluent (ya sobrescrito a blanco/negro en toda la app).
