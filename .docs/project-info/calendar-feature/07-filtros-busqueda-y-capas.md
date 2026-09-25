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
- **Una fila por cada `PolarTaskList`** (solo si `AppSettings.PolarLinked == true`), con su color de
  calendario como indicador (ver §1b).

Cada interruptor oculta/muestra sus `CalendarItem`s en todas las vistas (Mes/Semana/Día/Agenda/
mini-calendario) simultáneamente — es un estado del `CalendarViewModel`, no una preferencia por vista.

Estado por defecto: **todas las capas activas**. Persistir la última combinación elegida por el usuario
entre sesiones (mismo patrón que otras preferencias de `AppSettings`, ver
`09-configuracion-y-ajustes.md`) para que no tenga que reactivar sus capas cada vez que abre Enki.

## 1b. Color por origen

Hasta la alfa-3 el calendario pintaba **todas** las tareas del mismo color (`#E5E5E5` para cualquier
tarjeta de Kanban) y todas las listas de Polar con el mismo morado, porque `PolarTaskList.Color` nace con
el mismo valor por defecto (`#7F52FF`) en todas. El panel de capas servía solo para mostrar/ocultar. Esta
sección añade el color como diferenciador: de un vistazo se ve **de dónde viene** cada item.

### Qué color tiene cada cosa

- **Columnas de Kanban** — una por columna, no una para todo el tablero. Las tres columnas core llevan
  color fijo por su `CoreColumnRole`: `Backlog` rojo, `InProgress` ámbar, `Done` verde. Las columnas que
  cree el usuario reciben un acento de `CalendarLayerPalette.AccentPool`.
- **Listas de Polar** — un acento del mismo `AccentPool`, repartido de forma que dos listas nunca
  coincidan (ni una lista con una columna personalizada: comparten un único reparto).
- **Recordatorios** — el índigo de siempre (`CalendarItemViewModel.ReminderLayerColorHex`).

`AccentPool` **excluye rojos, amarillos y verdes**: esos tres significan un estado de Kanban y confundir
una lista de Polar con "pendiente" o "realizada" hace leer mal el calendario. La exclusión deja poco más
de 130° de rueda de color, así que el pool es deliberadamente corto (ocho tonos): con doce, dos listas
contiguas quedaban a ~11° una de otra y en una pastilla del mes —donde el color se pinta al 8% de relleno
y 40% de borde— se volvían indistinguibles, sobre todo en el tema claro. Con más de ocho capas la paleta
se repite.

Todos los tonos, los de rol incluidos, están calculados a la **misma luminancia relativa** (~0.22): ~3.2:1
de contraste sobre el fondo claro y ~4.4:1 sobre el oscuro. Un color elegido a ojo sobre el tema oscuro
suele desaparecer en el claro, y el calendario pinta el color a opacidad plena en el icono del chip, no
solo como relleno tenue. `CalendarLayerColorServiceTests` fija estas dos propiedades (separación respecto
a los colores de estado, y hex normalizado) para que ampliar la paleta no las rompa en silencio.

### Dónde se ve y dónde se cambia

**Solo dentro del Calendario.** Es una personalización de esta pantalla, no de Kanban ni de "Gestión de
tareas":

- El color se guarda en `AppSettings.CalendarLayerColors` (clave de color → hex), nunca en `KanbanColumn`
  ni en `PolarTaskList.Color` — este último se sincroniza con Supabase, así que reescribirlo cambiaría la
  lista en todos los dispositivos del usuario por un ajuste visual de una sola pantalla.
- `CalendarItemViewModel`/`CalendarUnscheduledItemViewModel` lo exponen en `CalendarColorHex` (efectivo:
  `EffectiveCalendarColorHex`), **aparte** de `LayerColorHex`. La lista "HOY" del Dashboard —el otro
  consumidor de esta proyección— sigue leyendo `LayerColorHex` y no cambia de aspecto.
- Solo se guardan las capas cuyo color se cambió a mano. Lo que no está en el diccionario lo reparte
  `CalendarLayerPalette` de forma determinista a partir de un hash FNV-1a del identificador, así que una
  columna o lista nueva ya nace con color propio sin migración, y el color no baila al reiniciar ni al
  borrar otra capa. Un color puesto a mano se aparta del reparto para que ningún automático lo repita.

### El panel

La fila **KANBAN** conserva su interruptor (sigue mostrando/ocultando todo Kanban a la vez) y pasa a ser
la cabecera de un grupo: debajo cuelga una sub-fila indentada por columna, **solo color, sin interruptor
propio**. El filtrado no cambia — las sub-filas existen para poder ver y tocar su color.

El punto de color de una fila editable es un botón: abre un emergente anclado (`Border.calendar-popover`)
de 340 px de ancho, con títulos que admiten varias líneas para evitar cortes. Incluye una rejilla de
colores sugeridos, un selector visual y un campo hexadecimal sincronizados. Al abrirlo se carga el color
actual. Las muestras de la rejilla se aplican con un clic; el color personalizado se confirma con
"Guardar" o Intro. Un hexadecimal inválido no se guarda y recupera el color actual. Si la capa tiene
color puesto a mano, "automático" permite devolverla al reparto. El emergente es anclado y no modal
a propósito: el usuario puede seguir viendo el calendario detrás.

`CalendarViewModel.RebuildLayers` actualiza las filas **en sitio** cuando la estructura no cambia (un
renombrado, un color nuevo) y solo reconstruye la colección cuando cambia el conjunto de filas. Si
reconstruyera siempre, el emergente se cerraría solo en cuanto se pulsa un color.

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
