# 07 — Filtros, búsqueda y capas

## Contexto rápido

Con tres fuentes de datos mostrándose juntas, el calendario necesita una forma de que el usuario controle
qué ve. Este documento define el sistema de "capas" (activar/desactivar orígenes completos), la búsqueda
y los filtros secundarios. Da por leído `03-glosario-y-modelo-de-datos.md` §5 (modelo de color, base de
este documento).

## 1. Capas por origen

Panel en el sidebar (ver `04-vistas-y-navegacion.md` §1), lista de interruptores, uno por capa:

- **Kanban** — un único interruptor, cubre todas las columnas del tablero (el tablero no tiene concepto
  de "sub-lista" filtrable de forma natural).
- **Recordatorios** — un único interruptor.
- **Una fila por cada `PolarTaskList`** (solo si `AppSettings.PolarLinked == true`), con el color de esa
  lista como indicador, igual que ya se ve en la vista "Gestión de tareas".

Cada interruptor oculta/muestra sus `CalendarItem`s en todas las vistas (Mes/Semana/Día/Agenda/
mini-calendario) simultáneamente — es un estado del `CalendarViewModel`, no una preferencia por vista.

Estado por defecto: **todas las capas activas**. Persistir la última combinación elegida por el usuario
entre sesiones (mismo patrón que otras preferencias de `AppSettings`, ver
`09-configuracion-y-ajustes.md`) para que no tenga que reactivar sus capas cada vez que abre Enki.

## 2. Búsqueda

- Campo de búsqueda en la barra superior o el sidebar (patrón visual "underline search" de
  `docs/sistema-diseno-web.md` §7.3, coherente con el resto de la app).
- Busca por título (y opcionalmente descripción) sobre los `CalendarItem`s generados por las capas
  actualmente activas — no busca dentro de capas ocultas, para que el resultado sea consistente con lo
  que el usuario ya está viendo.
- Resultado: en vista Mes/Semana/Día, resalta los chips que coinciden (mismo tratamiento que un filtro,
  no navega automáticamente); en vista Agenda, puede colapsar a solo los días con coincidencia — decisión
  de implementación, no bloqueante.
- No busca dentro de subtareas por separado en esta fase (alcance mínimo); si una subtarea coincide, se
  resalta igual que cualquier otro item porque también es un `CalendarItem` propio (ver
  `03-glosario-y-modelo-de-datos.md` §2.2).

## 3. Filtros secundarios

Menú de filtro (icono de embudo, patrón ya usado en el dashboard/listados de Enki si existe, si no,
nuevo popover con el estilo `Border.dropdown-popover`):

- **Prioridad**: mostrar solo items de prioridad Alta/Media/Baja/Ninguna (aplica a Kanban `Priority` y
  Polar `Priority`; `Reminder` no tiene prioridad, queda fuera de este filtro siempre visible salvo que
  su capa esté desactivada).
- **Etiquetas** (`PolarTask.Tags`): multi-selección, solo relevante/visible si `PolarLinked`.
- **Ocultar completadas**: oculta `CalendarItem`s con `IsCompleted == true` (ver
  `03-glosario-y-modelo-de-datos.md` §3). Por defecto **desactivado** (se muestran completadas, atenuadas
  — es más útil ver "hoy completé esto" en un calendario que ocultarlo de golpe), pero configurable como
  preferencia persistida.
- **Ocultar ocurrencias fantasma**: alterna la previsualización de recurrencia descrita en
  `03-glosario-y-modelo-de-datos.md` §4 — mismo interruptor que aparece también en
  `09-configuracion-y-ajustes.md` (es una sola preferencia, accesible desde ambos sitios por comodidad).

Los filtros de prioridad/etiqueta se combinan con AND entre categorías y OR dentro de la misma categoría
(ej.: "Alta" + "Media" muestra ambas; "Alta" + etiqueta "trabajo" muestra solo items que cumplen las dos).

## 4. Relación entre capas y el panel de "no programadas"

El panel de no programadas (`04-vistas-y-navegacion.md` §8) respeta las mismas capas activas — si
"Kanban" está desactivado, las tarjetas Kanban sin fecha tampoco aparecen ahí. Los filtros secundarios
(§3) también aplican a ese panel, salvo "Ocultar ocurrencias fantasma" (no aplica: un item sin fecha
nunca genera fantasmas, ver `03-glosario-y-modelo-de-datos.md` §4/§6).

## 5. Qué pasa si Polar no está vinculado

Con `AppSettings.PolarLinked == false`:

- No aparece ninguna fila de lista Polar en el panel de capas.
- El filtro de etiquetas no se muestra (no hay `Tags` sin Polar).
- Si el usuario vincula Polar más tarde en la misma sesión, el panel de capas se actualiza en vivo
  (suscripción al evento de cambio de vínculo, ver `11-arquitectura-tecnica.md`) sin necesidad de
  reabrir el calendario.
