# 01 — Contexto y visión de producto

## Contexto rápido

Enki es una app de escritorio (Windows/Linux/macOS vía Avalonia UI, .NET, MVVM con
`CommunityToolkit.Mvvm`) tipo "segundo cerebro" personal: notas markdown con wikilinks, un grafo de
sesiones de trabajo, un tablero Kanban local, un sistema de tareas sincronizado en la nube ("Polar",
compartido con una app Android homónima vía Supabase), recordatorios propios y un centro de
notificaciones. Este documento explica **por qué** se construye un Calendario y **con qué visión**, antes
de entrar en el detalle funcional de los documentos siguientes.

## 1. Por qué un calendario

Hoy, para saber "qué tengo que hacer esta semana", el usuario tiene que mirar tres sitios distintos:

1. El tablero **Kanban**, columna por columna, fijándose en qué tarjetas tienen `DueDateUtc`.
2. La vista **Gestión de tareas (Polar)**, lista por lista (solo si vinculó su cuenta).
3. La **bandeja de recordatorios** (📥 del title bar), que ya agrupa por "vencida/hoy/pronto" pero solo
   mira las tarjetas Kanban, no Polar ni los Recordatorios propios.

No existe ninguna vista temporal (mes/semana/día) que muestre las tres fuentes juntas en el eje del
calendario. Esa es la carencia que resuelve esta funcionalidad: **un lugar único para ver y mover en el
tiempo todo lo que ya vive en Enki**, sin duplicar dónde se guarda el dato.

## 2. Visión de producto

> El mejor calendario no es el que tiene más funciones — es el que hace que reprogramar una tarea sea tan
> rápido como arrastrarla, y que nunca haga dudar al usuario de "¿esto lo edité de verdad?" porque el
> calendario y el Kanban/Polar muestran exactamente el mismo dato, en vivo, sin sincronización manual.

Tres pilares:

- **Unificación sin duplicación.** El calendario nunca es la fuente de verdad de un dato — siempre lee y
  escribe directamente sobre `KanbanCard`, `PolarTask`/`PolarSubtask` o `Reminder`. Si el usuario abre el
  Kanban después de mover una tarjeta desde el calendario, ve el cambio ya reflejado, porque es la misma
  fila.
- **Interacción directa por encima de formularios.** Arrastrar para reprogramar, arrastrar el borde para
  cambiar la duración, clic en un hueco vacío para crear — el formulario completo (editor existente)
  siempre queda disponible, pero no debe ser el camino obligatorio para las operaciones más frecuentes.
- **Cero fricción para quien no usa Polar.** Polar es una vinculación opcional (`AppSettings.PolarLinked`).
  El calendario tiene que ser completamente útil solo con Kanban + Recordatorios, y ampliarse sin fricción
  el día que el usuario vincula Polar — nunca al revés.

## 3. Inspiración competitiva: qué robar de cada app y qué no

Este documento **no** pide clonar ninguna app entera. Cada una aporta una o dos ideas concretas que sí
encajan con el modelo de datos de Enki; el resto de cada producto se descarta explícitamente para no
inflar el alcance.

| App | Qué se adopta | Por qué encaja | Qué se descarta explícitamente |
|---|---|---|---|
| **Google Calendar** | El esqueleto de vistas (Mes/Semana/Día/Agenda), la navegación con flechas + "Hoy", los chips de evento con color, clic-y-arrastra para crear un rango de horas. | Es el lenguaje visual que todo el mundo ya entiende; minimiza la curva de aprendizaje. | Invitados/asistentes, videollamadas, calendarios compartidos entre usuarios, disponibilidad ("free/busy"), múltiples calendarios de Google — no hay backend multiusuario de eventos en Enki. |
| **Notion Calendar (ex Cron)** | El panel lateral de "tareas sin fecha" que se arrastra directamente al hueco del día; la superposición ligera de tareas de una herramienta externa (aquí: Polar) sobre el calendario nativo. | Encaja perfecto con que Enki ya tiene tareas *sin* fecha (Kanban/Polar) que hoy no aparecen en ningún calendario. | Cuentas múltiples, integración con Google/Outlook reales. |
| **TickTick / Todoist (vista calendario)** | Mostrar tareas recurrentes con una marca visual distinta a las de fecha fija; contador de tareas pendientes por día en la celda de mes cuando hay demasiadas para listarlas todas. | TickTick resuelve bien el mismo problema que tiene Polar: recurrencia basada en "la próxima fecha", no en un `RRULE` con instancias infinitas. | Pomodoro/temporizador integrado, hábitos, puntuación/gamificación. |
| **Fantastical** | Micro-interacciones pulidas: overlay de creación rápida con parseo de texto natural ("mañana a las 5"), animaciones de apertura sutiles, indicador de "ahora" (línea roja) en vista día/semana. | Son detalles de acabado que elevan mucho la percepción de calidad sin requerir nuevo modelo de datos. | Suscripción a calendarios de terceros, integración con contactos/tiempo. |
| **Apple Calendar** | Densidad de información equilibrada en vista Mes (nunca amontonar más de 3-4 líneas por celda antes de colapsar a "+N más"), mini-calendario de navegación en la barra lateral. | Referencia de legibilidad para escritorio con ventanas redimensionables (Enki también es una app de escritorio, no responsive-web). | Family Sharing, Siri, widgets del sistema operativo. |
| **Linear (vista de ciclos/timeline)** | El patrón de "capas" activables/desactivables por color (aquí: por origen — Kanban/Polar/Recordatorios, y dentro de Polar por lista), en vez de una casilla de "mostrar/ocultar todo". | Ya existe un precedente de color por entidad en Enki (`PolarTaskList.Color`); esto lo aprovecha en vez de inventar un sistema de color nuevo. | Roadmaps de producto, dependencias entre issues. |

**Regla de decisión para cualquier idea nueva que no esté en esta tabla:** si requiere un backend
multiusuario, una cuenta de terceros, o una entidad de datos completamente nueva (ver §4), no entra en el
alcance de esta funcionalidad sin pasar antes por una decisión de producto explícita — no se improvisa
durante la implementación.

## 4. No-objetivos de esta fase

Declarados explícitamente para que ningún agente los añada "porque Google Calendar lo tiene":

- **No hay entidad `CalendarEvent` nativa.** No se modela un "evento" independiente de tarea (reunión sin
  checklist, cita, bloque de tiempo puro). Todo lo que aparece en el calendario es, en el fondo, una
  tarjeta Kanban, una tarea/subtarea Polar o un Recordatorio. Si en el futuro se decide que sí hace falta
  un evento puro, es un nuevo dominio de datos y un nuevo documento — no una extensión silenciosa de
  este.
- **No hay invitados, asistentes ni disponibilidad compartida.** Enki es una app de un solo usuario por
  vault/vínculo Polar.
- **No hay integración con Google Calendar/Outlook/iCloud** (ni import ni export ICS) en esta fase — se
  deja anotado como posible Fase 6 en
  [12-plan-de-implementacion-por-fases.md](12-plan-de-implementacion-por-fases.md), no como trabajo
  actual.
- **No se toca el algoritmo de recurrencia de Polar.** Se reutiliza tal cual (`CalculateNextDueDate`); el
  calendario es cliente de ese cálculo, no lo reimplementa ni lo cambia.
- **No se crea una cuarta pantalla de "gestión de tareas".** El calendario no reemplaza a Kanban ni a
  Polar como lugar para gestionar el detalle de una tarea (columnas, listas, tags) — es un lugar para
  verlas y moverlas en el tiempo. El editor completo sigue siendo el existente.

## 5. A quién sirve este documento

A cualquier persona o agente que vaya a:

- Diseñar el detalle funcional del calendario (documentos 3–10 de esta carpeta).
- Implementarlo (documento 11 en adelante).
- Revisar si una idea nueva "encaja" con la visión antes de añadirla al alcance.

Si en algún momento una decisión de los documentos siguientes parece contradecir algo de este
documento, este documento gana — o se actualiza aquí primero, explícitamente, antes de seguir.
