# 13 — Pruebas y checklist de calidad

## Contexto rápido

Enki tiene un proyecto xUnit (`enki/tests/enki.Tests/`) ya establecido, con tests de lógica no trivial en
`ViewModels/` y `Services/`, y un patrón de `storageDirectory` opcional para aislar los tests del disco
real (ver `docs/notificaciones-y-recordatorios.md`, sección "Pruebas"). Este documento define qué probar
automáticamente y qué recorrer a mano antes de dar la funcionalidad por terminada.

## 1. Qué merece un test automático (xUnit)

Siguiendo el mismo criterio que ya aplicó la funcionalidad de recordatorios ("lo que el propio diseño
señalaba como más valioso para testear" — lógica no trivial, no getters/setters):

- **Expansión de ocurrencias fantasma** (`CalendarAggregationService`, ver
  `11-arquitectura-tecnica.md` §3, `03-glosario-y-modelo-de-datos.md` §4):
  - Una tarea `WEEKLY` genera exactamente las ocurrencias esperadas dentro de un rango de 4 semanas.
  - Una tarea `MONTHLY` respeta el ajuste de overflow de días que ya prueba (o debería probar) el motor
    de recurrencia de Polar — no reinventar el caso de prueba, adaptarlo.
  - El tope de seguridad de iteraciones corta correctamente sin colgar el cálculo (test con una
    recurrencia y un rango deliberadamente amplios).
  - Ninguna ocurrencia fantasma se genera para una `PolarTask` con `Recurrence == None`.
  - Ninguna ocurrencia fantasma se genera para `KanbanCard` (no tiene recurrencia, ver
    `03-glosario-y-modelo-de-datos.md` §2.1).
- **Agregación de las tres fuentes**: dado un `KanbanBoard`, un `PolarTaskStore` y una lista de
  `Reminder` de prueba, `CalendarAggregationService.BuildItems(rango)` devuelve el conjunto correcto de
  `CalendarItem`s, respetando:
  - Items con `DueDateUtc`/`NotifyAtUtc` fuera del rango solicitado quedan excluidos.
  - Items con `IsDeleted == true` nunca aparecen.
  - `PolarTask`/`PolarSubtask` quedan excluidas por completo cuando se simula `PolarLinked == false`.
  - Un `PolarSubtask` con fecha propia genera su propio `CalendarItem`, distinto del de su tarea padre.
- **Cálculo de "sin fecha"**: tarjetas/tareas con `DueDateUtc == null` aparecen en el conjunto de "no
  programadas" y nunca en el conjunto posicionado en el calendario.
- **`IsPastDue`/`IsAllDay` sobre `CalendarItem`**: mismos casos límite que ya prueba
  `ReminderItemViewModelTests` para `KanbanCard.IsPastDue` (fecha con hora vs. fecha de todo el día),
  aplicados ahora también a `PolarTask`/`Reminder` a través del `CalendarItem` unificado — para
  confirmar que los tres orígenes usan literalmente el mismo criterio (ver
  `08-notificaciones-e-integracion.md` §3).
- **`ReminderService.RemindersChanged`** (si se añade en la Fase 3, ver `11-arquitectura-tecnica.md`
  §5.1): se dispara tras `Create`/`Delete`/`ToggleCompleted`, siguiendo el mismo test que ya existe para
  `KanbanService.BoardChanged` o `PolarTaskService.DataChanged` como referencia de estilo.

## 2. Qué NO merece un test automático (verificar a mano, §3)

Todo lo puramente visual/interactivo de una app de escritorio con Avalonia (posición exacta de un chip
arrastrado, animaciones, popovers) — consistente con cómo el resto de Enki ya se prueba: lógica en xUnit,
UI a mano.

## 3. Checklist manual de QA (recorrer contra la app corriendo de verdad)

### Datos y consistencia
- [ ] Crear una tarjeta Kanban con fecha desde el calendario aparece de inmediato en el tablero Kanban al
      navegar ahí, en la columna correcta.
- [ ] Editar la fecha de una tarea Polar desde su editor completo (no desde el calendario) actualiza el
      calendario si está abierto, sin recargar la página.
- [ ] Completar una tarea Polar recurrente desde el calendario dispara el mismo rollover que completar
      la misma tarea desde la vista "Gestión de tareas" (nueva `DueDateUtc`, subtareas reseteadas).
- [ ] Eliminar un recordatorio desde el calendario lo hace desaparecer también de la bandeja de
      recordatorios (si aplicase) y de la campana, según corresponda.
- [ ] Con `PolarLinked == false`, el calendario no muestra ningún botón/opción relativo a Polar en
      ningún sitio (creación rápida, capas, filtros).
- [ ] Vincular Polar con el calendario abierto hace aparecer sus tareas y listas sin recargar.

### Vistas y navegación
- [ ] Las cuatro vistas muestran el mismo conjunto de items para el mismo rango de fechas.
- [ ] Cambiar de vista conserva la fecha de referencia.
- [ ] Navegar varios meses hacia adelante y hacia atrás no degrada el rendimiento perceptible ni deja
      fugas visuales (chips de un mes anterior quedando pintados).
- [ ] El mini-calendario y la vista principal permanecen sincronizados en ambas direcciones.
- [ ] Un día con más items de los que caben en la celda de Mes muestra correctamente "+N más" y el
      popover con la lista completa.

### Interacciones
- [ ] Arrastrar un item conserva la hora si solo cambia de día, y la asigna correctamente si se suelta
      sobre una franja horaria concreta.
- [ ] Un item fantasma no se puede arrastrar, completar ni eliminar; abrirlo navega a la tarea real.
- [ ] El popover de creación rápida oculta la opción "Tarea Polar" cuando no hay vínculo.
- [ ] "Más opciones" en la creación rápida guarda lo ya introducido y abre el editor completo sin perder
      datos.

### Zonas horarias y fechas límite
- [ ] Un item con fecha "todo el día" (medianoche local) se muestra como "vencido" solo a partir del
      cambio de día local, no a medianoche UTC — mismo criterio que ya usa `KanbanCard.IsPastDue`.
- [ ] Un item creado con hora específica cerca de medianoche aparece en el día local correcto, no en el
      día UTC si difieren por la zona horaria del sistema.
- [ ] Cambiar la zona horaria del sistema operativo (si es practicable probarlo) no descoloca items ya
      guardados — se recalculan a partir del `DateTimeUtc` persistido, nunca de un valor local cacheado.

### Diseño visual
- [ ] Recorrer el checklist de `10-diseno-visual-y-componentes.md` §9 completo.
- [ ] Comparar una captura del calendario junto a una captura de Kanban/Gestión de tareas — deben leerse
      como la misma app, sin discontinuidad de estilo.

### Regresión sobre lo existente
- [ ] La bandeja de recordatorios (📥) sigue funcionando exactamente igual que antes de esta
      funcionalidad (mismo criterio de bucketing, mismo comportamiento de descarte).
- [ ] La campana de notificaciones (🔔) no recibe entradas nuevas provenientes de acciones del usuario
      dentro del calendario.
- [ ] El selector de fecha del editor de tarjetas Kanban (`Border.calendar-popover` original) sigue
      funcionando idéntico si se reutilizó/movió código compartido con la vista Mes del calendario (ver
      `11-arquitectura-tecnica.md` §4).
- [ ] `dotnet build` y `dotnet test enki/tests/enki.Tests/enki.Tests.csproj` pasan limpios.
