# 08 — Notificaciones e integración con lo existente

## Contexto rápido

Enki ya tiene un centro de notificaciones (🔔, log de eventos de la app) y una bandeja de recordatorios
(📥, proyección de tarjetas Kanban con fecha próxima) — ver `docs/notificaciones-y-recordatorios.md`. El
Calendario **no reemplaza ninguna de las dos**, pero tiene que convivir con ellas de forma coherente. Este
documento define exactamente esa convivencia.

## 1. La bandeja de recordatorios (📥) no cambia de comportamiento

`ReminderInboxViewModel` sigue siendo, tal cual está hoy, una proyección de solo lectura sobre
`KanbanCard.DueDateUtc` con ventana configurable (`AppSettings.ReminderSoonWindowDays`). **No se amplía**
para que también mire `PolarTask`/`Reminder` — sería tentador "ya que estamos", pero cambiaría el
contrato de una funcionalidad ya terminada y documentada sin que el usuario lo haya pedido. Si en el
futuro se decide unificarlas, es una decisión de producto explícita aparte, no un efecto colateral de
construir el calendario.

**Diferencia de propósito, para que quede clara la razón de no fusionarlas:**

| | Bandeja de recordatorios (📥) | Calendario |
|---|---|---|
| Pregunta que responde | "¿Qué vence pronto?" | "¿Qué hay programado, en general?" |
| Fuentes | Solo Kanban | Kanban + Polar + Recordatorios |
| Ventana temporal | Acotada (`ReminderSoonWindowDays`, por defecto 7 días) | Sin límite — se navega libremente hacia el futuro o el pasado |
| Acceso | Popover flotante desde el title bar, en cualquier pantalla | Página propia dedicada |

## 2. La campana de notificaciones (🔔) — ¿el calendario dispara eventos ahí?

`NotificationService.Push(...)` registra eventos de sistema (vault cambiado, sync, papelera). Las
acciones que el usuario hace **dentro** del calendario (crear, mover, completar un item) **no** generan
una notificación en la campana — son acciones directas del usuario sobre su propio dato, no eventos de
sistema. Esto es consistente con que mover una tarjeta dentro de Kanban tampoco genera notificación hoy.

La única excepción a considerar (opcional, fase de pulido): si en el futuro se implementa una alarma
nativa del sistema operativo para recordatorios (mencionado como posible paso futuro en
`docs/notificaciones-y-recordatorios.md`, sección "Posibles próximos pasos"), el calendario sería un
consumidor de esa función, no el que la implementa — no está en el alcance de esta funcionalidad.

## 3. Indicadores de "vencido" y "hoy" — reutilizar el criterio existente, no inventar uno nuevo

`KanbanCard.IsPastDue(DateTime)` ya resuelve la regla exacta de "vencida" distinguiendo fecha-solo de
fecha-con-hora (ver `docs/notificaciones-y-recordatorios.md`, sección homónima). El calendario debe
aplicar **esa misma fórmula** a los tres orígenes (Kanban, Polar, Recordatorio) para que un item se
considere "vencido" con el mismo criterio en la bandeja, en Kanban y en el calendario — nunca tres
criterios ligeramente distintos. Ver `03-glosario-y-modelo-de-datos.md` §3, campo `IsPastDue`.

Tratamiento visual: un item vencido se distingue con un acento de color de peligro (mismo rojo que ya usa
el resto de la app para error/vencido), consistente entre las tres pantallas.

## 4. Badge de navegación (icono "Calendario" en el sidebar principal)

Siguiendo el patrón ya usado por otras entradas de navegación con contador (si existiera alguno hoy —
verificar en `NavItemViewModel`/`MainWindowViewModel` antes de implementar), el ítem de navegación
"Calendario" puede mostrar un badge con el número de items vencidos (mismo cálculo que
`OverdueCount` de la bandeja de recordatorios, extendido a las tres fuentes) — opcional, fase de pulido,
no bloqueante para el resto de la funcionalidad. Si se implementa, reutilizar el mismo
`Border.titlebar-badge` cuadrado (no redondo) que ya usan campana y bandeja.

## 5. Vínculo con Polar — estado enlazado/desenlazado en vivo

El calendario debe reaccionar en vivo si el usuario vincula o desvincula Polar desde Ajustes mientras el
calendario está abierto (`AppSettings.PolarLinked`, sección "Vínculo con Polar" en
`docs/supabase-sync/`): al vincularse, las tareas/subtareas Polar y sus listas aparecen automáticamente
en la grilla y en el panel de capas sin recargar la página; al desvincularse, desaparecen. Ver
`11-arquitectura-tecnica.md` para el mecanismo de suscripción concreto.

## 6. Sincronización en segundo plano (Polar ↔ Supabase)

`PolarSyncService` sincroniza en segundo plano contra Supabase (ver `docs/supabase-sync/`). El calendario
no inicia ni controla esa sincronización — solo se suscribe a `PolarTaskService.DataChanged` (disparado
tanto por ediciones locales como por cambios que llegan de la sincronización) para refrescarse, igual que
ya hace la vista "Gestión de tareas". Un cambio hecho en la app Android Polar debe reflejarse en el
calendario de Enki en el siguiente ciclo de sync, sin acción manual del usuario.
