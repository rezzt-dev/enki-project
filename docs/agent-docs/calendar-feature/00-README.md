# Calendario de Enki — documentación de la funcionalidad

> Carpeta destinada a quien (persona o agente de IA) vaya a diseñar en detalle y luego implementar una
> vista de **Calendario** dentro de Enki. El objetivo no es clonar Google Calendar como producto — es
> replicar su **esqueleto de funcionalidades** (vistas, interacciones, creación rápida, capas,
> notificaciones) usando como única fuente de datos **lo que Enki ya tiene**: tarjetas Kanban, tareas
> Polar (sincronizadas con la app Android vía Supabase) y Recordatorios propios. Nada de esto depende de
> la API de Google ni de una cuenta externa.

## Por qué existe esta carpeta

Enki ya tiene tres sistemas de "cosas con fecha" que hoy viven en pantallas separadas (Kanban, Gestión
de tareas/Polar, bandeja de Recordatorios) y no hay ninguna vista que las junte en el tiempo. El
Calendario es esa vista unificadora: no sustituye a Kanban ni a Polar ni a la bandeja de recordatorios
(que siguen existiendo tal cual), sino que los **visualiza y edita todos juntos** en formato mes/semana/
día/agenda, con arrastrar-y-soltar, creación rápida y capas por origen — inspirado en lo mejor de Google
Calendar, Notion Calendar, Fantastical, TickTick y Apple Calendar, adaptado al modelo de datos real de
Enki en vez de al de ninguna de esas apps.

## Cómo usar esta carpeta

**Léase en orden la primera vez.** Cada documento numerado asume que ya se leyó el anterior, pero cada
uno también trae al principio un bloque **"Contexto rápido"** con lo mínimo imprescindible para poder
retomarlo en frío, en una sesión nueva, sin haber leído los anteriores — pensado explícitamente para
que un agente con la ventana de contexto/tokens agotada pueda cerrar sesión después de cualquier
documento y otro agente (u otra sesión del mismo) continúe desde el siguiente sin perder nada esencial.

| # | Documento | Contenido | Tipo |
|---|-----------|-----------|------|
| 0 | [00-README.md](00-README.md) | Este índice. | Mapa |
| 1 | [01-contexto-y-vision-de-producto.md](01-contexto-y-vision-de-producto.md) | Qué es Enki hoy, por qué un calendario, inspiración competitiva (qué robar de cada app y qué no), principios de producto, no-objetivos. | Contexto |
| 2 | [02-prompt-maestro-de-implementacion.md](02-prompt-maestro-de-implementacion.md) | **El prompt profesional listo para pegar** a un agente que vaya a implementar toda la funcionalidad de punta a punta, fase a fase, citando el resto de documentos. | Prompt operativo |
| 3 | [03-glosario-y-modelo-de-datos.md](03-glosario-y-modelo-de-datos.md) | Términos exactos (para no confundir "Recordatorio" con "bandeja de recordatorios"), las tres fuentes de datos reales, el concepto nuevo `CalendarItem`, cómo se expande la recurrencia de Polar. | Fundacional |
| 4 | [04-vistas-y-navegacion.md](04-vistas-y-navegacion.md) | Mes, semana, día, agenda, mini-calendario, panel "sin fecha", navegación temporal, atajos de teclado. | Funcionalidad |
| 5 | [05-interacciones-y-microinteracciones.md](05-interacciones-y-microinteracciones.md) | Arrastrar y soltar, redimensionar duración, clic para crear, menús contextuales, selección múltiple, hover/foco. | Funcionalidad |
| 6 | [06-creacion-y-edicion-de-items.md](06-creacion-y-edicion-de-items.md) | Popover de creación rápida (elegir Kanban/Polar/Recordatorio), reutilización de los editores existentes, edición de recurrencia y subtareas desde el calendario. | Funcionalidad |
| 7 | [07-filtros-busqueda-y-capas.md](07-filtros-busqueda-y-capas.md) | Capas por origen/lista, búsqueda, filtros por prioridad/etiqueta, color por fuente. | Funcionalidad |
| 8 | [08-notificaciones-e-integracion.md](08-notificaciones-e-integracion.md) | Relación con la campana y la bandeja de recordatorios existentes, indicadores de vencido/hoy, badge de navegación. | Integración |
| 9 | [09-configuracion-y-ajustes.md](09-configuracion-y-ajustes.md) | Nueva sección en Ajustes: vista por defecto, primer día de semana, horario laboral, formato de hora, densidad. | Funcionalidad |
| 10 | [10-diseno-visual-y-componentes.md](10-diseno-visual-y-componentes.md) | Traducción del sistema de diseño brutalista de Enki a componentes concretos de calendario (celda de mes, grilla de hora, chip de evento). | Diseño |
| 11 | [11-arquitectura-tecnica.md](11-arquitectura-tecnica.md) | Modelos, servicios, viewmodels, vistas, eventos de cambio, huecos que hay que rellenar en servicios existentes. | Técnico |
| 12 | [12-plan-de-implementacion-por-fases.md](12-plan-de-implementacion-por-fases.md) | Roadmap en bloques entregables de forma independiente (Fase 0 → Fase 6), cada uno con criterio de "hecho". | Roadmap |
| 13 | [13-pruebas-y-checklist-de-calidad.md](13-pruebas-y-checklist-de-calidad.md) | Plan de pruebas xUnit, checklist manual de QA, casos límite (zonas horarias, todo-el-día, Polar desvinculado). | QA |

## Decisiones de diseño ya tomadas (no reabrir sin motivo)

Para que un agente pueda avanzar sin quedarse bloqueado pidiendo permiso, estas decisiones se consideran
**cerradas**. Se desarrollan con su razonamiento completo en los documentos citados; aquí solo el resumen
ejecutivo:

- **El Calendario no es una cuarta fuente de datos.** No se crea ninguna entidad `CalendarEvent` nueva en
  esta fase. Es una capa de visualización/interacción sobre las tres fuentes que ya existen (Kanban,
  Polar, Recordatorios). Ver [01](01-contexto-y-vision-de-producto.md#no-objetivos-de-esta-fase) y
  [03](03-glosario-y-modelo-de-datos.md).
- **La bandeja de recordatorios (🔔/📥 del title bar) no se toca.** Sigue siendo la proyección rápida de
  "qué vence pronto"; el Calendario es la vista completa "qué hay en cada día". Son complementarios, no
  hay que fusionarlos ni deprecar ninguno. Ver [08](08-notificaciones-e-integracion.md).
- **La recurrencia de Polar no genera filas nuevas.** Sigue con su modelo actual de "una fila que rueda
  hacia adelante al completarse". El calendario puede *previsualizar* ocurrencias futuras calculándolas
  con el mismo algoritmo ya existente (`PolarTaskService.CalculateNextDueDate`), pero solo la ocurrencia
  real es interactiva; las futuras son una vista previa de solo lectura. Ver
  [03](03-glosario-y-modelo-de-datos.md#recurrencia-y-ocurrencias-fantasma).
- **Todo el trabajo de fecha se hace en la hora local del usuario y se persiste en UTC**, exactamente
  igual que ya hace `KanbanCard.DueDateUtc` y `Reminder.NotifyAtUtc` hoy. No se introduce ninguna noción
  nueva de zona horaria.
- **El calendario respeta el sistema de diseño existente al 100%**: cero radio de esquina salvo
  indicadores circulares, tipografía Space Grotesk/JetBrains Mono, bordes de 1px en vez de sombras,
  reutilización literal de `Border.calendar-popover`/`Border.dropdown-popover`. Ver
  [10](10-diseno-visual-y-componentes.md) y `docs/sistema-diseno-web.md`.
- **Se implementa en bloques incrementales** (ver [12](12-plan-de-implementacion-por-fases.md)), cada uno
  entregable y usable por sí mismo, para poder pausar entre fases sin dejar la app rota.
- **Addendum post-Fase 5 — franja horaria (inicio-fin) para tarjetas Kanban.** `04-vistas-y-navegacion.md`
  §5 documentaba que ninguna de las tres fuentes tenía duración propia y que añadirla era "cambio de
  alcance mayor que se decide aparte". Se decidió resolverlo, con alcance **acotado a `KanbanCard`
  únicamente** (nuevo campo local `DurationMinutes`, `int?`, minutos): `KanbanCard` no sincroniza con
  Supabase, así que el cambio no toca ningún esquema externo ni la app Android. `PolarTask`/`PolarSubtask`
  y `Reminder` sí sincronizan (ver `docs/supabase-sync/`), así que se dejaron **fuera a propósito** —
  darles duración implicaría coordinar un cambio de contrato con una app fuera de este repo, y en el caso
  de `Reminder` además no encaja conceptualmente (es un instante de aviso, no una tarea con inicio y fin).
  Detalle completo: `03-glosario-y-modelo-de-datos.md` §2.1/§3, `04-vistas-y-navegacion.md` §5,
  `05-interacciones-y-microinteracciones.md` §2b, `06-creacion-y-edicion-de-items.md` §1/§3.

## Estado de avance (actualizar según se implementa)

Marcar aquí qué fase de [12-plan-de-implementacion-por-fases.md](12-plan-de-implementacion-por-fases.md)
está en curso o completada, para que cualquier sesión nueva sepa por dónde retomar sin tener que
inspeccionar el código primero:

- [x] Fase 0 — Esqueleto de navegación (página vacía en el nav)
- [x] Fase 1 — Vista Mes de solo lectura, agregando las 3 fuentes
- [x] Fase 2 — Vistas Semana/Día/Agenda + mini-calendario
- [x] Fase 3 — Interacciones: crear, editar, arrastrar, redimensionar
- [x] Fase 4 — Capas, filtros, búsqueda
- [x] Fase 5 — Ajustes, notificaciones, pulido visual y de animación
- [x] Addendum post-Fase 5 — Franja horaria (inicio-fin) para tarjetas Kanban: campo
      `KanbanCard.DurationMinutes`, bloque con alto real + redimensionado arrastrando el borde inferior
      (snap de 5 min) en Semana/Día, campo "Duración" en el editor y en la creación rápida
- [ ] Fase 6 (futuro opcional) — Entidad de evento nativo, import/export ICS

## Documentación relacionada del proyecto (`docs/`, `agent-docs/`)

- `docs/notificaciones-y-recordatorios.md` — diseño actual de campana + bandeja de recordatorios. Léelo
  antes del documento [08](08-notificaciones-e-integracion.md).
- `docs/sistema-diseno-web.md` — sistema de diseño visual completo de Enki (origen: la web de rezzt.dev,
  portado 1:1 a Avalonia). Léelo antes del documento [10](10-diseno-visual-y-componentes.md).
- `docs/supabase-sync/` — contrato de datos e implementación de Polar (tareas/listas/subtareas/
  recordatorios sincronizados con la app Android). Léelo antes del documento
  [03](03-glosario-y-modelo-de-datos.md), en particular `01-modelo-de-datos-local.md`,
  `02-logica-de-negocio.md` (motor de recurrencia) y `05-contrato-interoperabilidad.md`.
- `docs/agent-docs/sigma-js-graph-migration.md` — no relacionado con el calendario (migración de la
  librería de grafo de Sesiones); se menciona aquí solo para que quede claro que no hace falta leerlo.
