# 09 — Configuración y ajustes

## Contexto rápido

Enki tiene un patrón consolidado para añadir preferencias: un campo nuevo en
`models/AppSettings.cs`, una sección nueva en `SettingsViewModel`/`SettingsView.axaml`
(`SettingsSectionViewModel` + `partial void On<Prop>Changed` que escribe a `AppSettings` y guarda), como
ya existe para "Notificaciones" (ver `docs/notificaciones-y-recordatorios.md`, sección "Preferencias en
Ajustes"). Este documento define la nueva sección "Calendario" siguiendo exactamente ese mismo patrón.

## 1. Nueva sección en Ajustes: "Calendario"

| Ajuste | Tipo | Por defecto | Descripción |
|---|---|---|---|
| `CalendarDefaultView` | `Mes \| Semana \| Día \| Agenda` | `Mes` | Vista con la que se abre la página de Calendario. |
| `CalendarWeekStartsOnMonday` | `bool` | `true` (o el criterio regional que ya use el resto de la app, si existe alguno) | Si la semana empieza en lunes o domingo, afecta a las vistas Mes/Semana y al mini-calendario. |
| `CalendarShowWeekends` | `bool` | `true` | Ocultar sábado/domingo en Semana/Mes para quien solo planifica días laborables. |
| `CalendarWorkingHoursStart` / `CalendarWorkingHoursEnd` | `int` (hora 0–23) | `9` / `18` | Rango resaltado en la grilla de horas de Semana/Día (fondo ligeramente distinto dentro del horario "activo") y punto de scroll inicial al abrir esas vistas. |
| `CalendarTimeFormat24h` | `bool` | según configuración regional del sistema si es detectable, si no `true` | Formato 24h vs. 12h AM/PM en las franjas horarias. |
| `CalendarShowCompletedItems` | `bool` | `true` | Espejo del filtro "Ocultar completadas" de `07-filtros-busqueda-y-capas.md` §3 — mismo dato, accesible desde ambos sitios. |
| `CalendarShowGhostOccurrences` | `bool` | `true` | Espejo del filtro "Ocultar ocurrencias fantasma" de `07-filtros-busqueda-y-capas.md` §3 y de la previsualización de recurrencia de `03-glosario-y-modelo-de-datos.md` §4. |
| `CalendarActiveLayersJson` | `string` (serialización simple, p. ej. lista de ids/claves de capa) | todas activas | Persistencia de qué capas (`07-filtros-busqueda-y-capas.md` §1) quedaron activas la última vez. |
| `CalendarPanelEnabled` | `bool` | `true` | Muestra/oculta la entrada "Calendario" del nav principal por completo — mismo espíritu que `RemindersPanelEnabled`/`NotificationsPanelEnabled` ya existentes, para quien prefiera no verla. |

## 2. Dónde vive en la UI de Ajustes

Nueva `SettingsSectionViewModel` "Calendario", ubicada junto a "Notificaciones" en el árbol de secciones
de `SettingsView.axaml` (mismo patrón de navegación lateral de Ajustes ya existente) — no se mezcla
dentro de la sección "Notificaciones" existente aunque comparta algún concepto (ventana de vencimiento),
porque conceptualmente son configuraciones de pantallas distintas.

## 3. Qué NO es un ajuste (y por qué)

- **Colores de capa** (`03-glosario-y-modelo-de-datos.md` §5) no son configurables por el usuario en esta
  fase — ya derivan de datos existentes (`PolarTaskList.Color`) o de una constante fija del sistema de
  diseño. Añadir un selector de color propio del calendario duplicaría un control que ya existe en el
  editor de listas Polar para el caso que sí es configurable.
- **Duración por defecto de un bloque sin duración explícita** (`04-vistas-y-navegacion.md` §5) se deja
  como constante de implementación, no como ajuste — es un detalle de dibujo, no una decisión que el
  usuario necesite tomar.

## 4. Preguntas abiertas

_(Ninguna al momento de escribir este documento.)_
