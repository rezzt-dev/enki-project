# ANALISIS DE PRODUCTO: PRIORIDAD MEDIA

> mejoras que cierran flujos que ya existen a medias o que amplian el publico sin cambiar la
> filosofia del producto. se abordan despues de la prioridad alta y antes de explorar ideas nuevas. el
> indice con el avatar y el resumen esta en [`indice.md`](./indice.md).

**Fecha del analisis:** 2026-11-06 · rama `beta-v2.0`
**Metodo:** igual que [`prioridad-alta.md`](./prioridad-alta.md); "no aparece" significa "sin
coincidencias en el codigo" y se comprueba antes de empezar cada tarea.

---

## MED-01 · TIEMPO REAL TRABAJADO Y TEMPORIZADOR DE ENFOQUE

- **tipo:** nuevo
- **usuario afectado:** estudiante y autonomo que quieren saber cuanto dedican a cada cosa.
- **evidencia:** `KanbanCard.DurationMinutes` guarda solo la duracion estimada. no aparecen `Pomodoro`
  ni ningun campo de tiempo real. es la tarea `T6` pendiente del analisis previo.
- **propuesta:** campo de tiempo real en tarjetas kanban y tareas polar, temporizador que avisa por
  `NotificationService` y resumen en `StatisticsAggregationService`. el tiempo se registra, no se
  puntua: sin rachas ni premios, para respetar el principio del producto.
- **criterio de cierre:** una sesion de enfoque suma minutos a la tarjeta y se ve en estadisticas.

## MED-02 · NOTAS RELACIONADAS Y MENCIONES SIN ENLAZAR

> **ESTADO (2026-11-07): implementado, pendiente de comprobación manual.** Pestaña «Enlaces» del editor con
> dos bloques nuevos bajo los enlaces entrantes: **notas relacionadas** (etiquetas y enlaces compartidos,
> con botón para añadir `[[enlace]]` al final de la nota abierta) y **menciones sin enlazar** (convierte la
> primera mención en `[[enlace]]` con `VaultService.LinkMention`, comprobado, con snapshot en el historial).
> Detalle y decisiones en [`notas-relacionadas-y-menciones.md`](../../../project-info/notas-relacionadas-y-menciones.md).

- **tipo:** nuevo
- **usuario afectado:** quien construye un segundo cerebro y olvida enlazar.
- **evidencia:** el editor ya muestra backlinks (`EditorBacklink`) pero no aparecen menciones sin
  enlazar ni notas relacionadas. es la tarea `T7` del analisis previo.
- **propuesta:** bloques "relacionadas" (etiquetas y enlaces compartidos) y "menciones sin enlazar" en
  el panel lateral, con enlazado en un clic mediante `VaultService.WriteContentChecked`.
- **criterio de cierre:** convertir una mencion en `[[enlace]]` actualiza la nota sin perder cambios.

## MED-03 · MIGRACION DESDE MAS HERRAMIENTAS CON INFORME

> **ESTADO (2026-11-07): implementado, pendiente de comprobación manual con exportaciones reales.** La
> evidencia estaba desfasada en un punto: **Todoist (CSV) ya existía** (`ReadTodoist`). Se han añadido
> Logseq (carpeta), Evernote (`.enex`), Google Keep (ZIP de Takeout o carpeta) y TickTick (copia CSV), y un
> informe final que se guarda como nota en la carpeta «Importado de …». Detalle en
> [`migracion-desde-otras-herramientas.md`](../../../project-info/migracion-desde-otras-herramientas.md).
> Las pruebas usan exportaciones sintéticas escritas a mano; no se han probado con exportaciones reales de
> cada aplicación.

- **tipo:** mejora
- **usuario afectado:** quien llega desde otra aplicacion y no quiere empezar de cero.
- **evidencia:** `ExternalImportService` lee Obsidian (`ReadObsidian`) y Notion (`ReadNotion`). no
  aparecen lectores de Logseq, Evernote (`.enex`), Google Keep, ni de tareas de Todoist o TickTick.
- **propuesta:** nuevos lectores sobre el mismo `Plan` y un informe final con lo importado, lo omitido y
  por que, reutilizando `ImportInterruptedException` para no dejar importaciones a medias sin avisar.
- **criterio de cierre:** una importacion de prueba por formato termina con informe y sin escribir fuera
  del vault ni de las entidades reales.

## MED-04 · EXPORTAR EL CALENDARIO Y LOS RECORDATORIOS A ICS

> **ESTADO (2026-11-07): implementado, pendiente de comprobación con calendarios externos.** Calendario ›
> menú de exportar › «Calendario (ICS)». Verificado con `Ical.Net` y con el propio lector de importación de
> Enki (mismas horas, duraciones y recurrencias); **no** se ha importado en Google Calendar, Thunderbird ni
> en el móvil. Detalle en [`exportar-calendario-ics.md`](../../../project-info/exportar-calendario-ics.md).

- **tipo:** nuevo
- **usuario afectado:** quien quiere ver sus fechas en el movil o en otro calendario sin cuenta.
- **evidencia:** `ExternalCalendarService` se suscribe a calendarios `.ics` y `webcal://` de solo lectura;
  no aparece una exportacion hacia `.ics`.
- **propuesta:** exportar a fichero `.ics` los elementos de `CalendarAggregationService` con su
  recurrencia, como accion manual y sin servidor.
- **criterio de cierre:** el fichero se importa en un calendario externo con las mismas horas y
  repeticiones.

## MED-05 · CORRECTOR ORTOGRAFICO DEL EDITOR

> **ESTADO (2026-11-07): motor e interfaz hechos; diccionarios sin incluir.** Subrayado en segundo plano con
> WeCantSpell.Hunspell, desactivable en Ajustes › Editor. **No se distribuye ningún diccionario** porque la
> mayoría tienen licencias (GPL, LGPL, MPL) que hay que revisar frente a `license.md`: decisión pendiente del
> titular. Sin diccionario para el idioma activo el corrector no subraya nada; cada usuario puede copiar los
> suyos. Detalle en [`corrector-ortografico.md`](../../../project-info/corrector-ortografico.md).

- **tipo:** nuevo
- **usuario afectado:** estudiante y escritor; casi todo el que redacta texto largo.
- **evidencia:** no aparece ningun servicio de correccion ortografica; `Spell` solo coincide con una
  declaracion de interoperabilidad en `X11GlobalHotkey`.
- **propuesta:** subrayado de palabras desconocidas con diccionarios locales por idioma de la
  aplicacion, desactivable, que ignore codigo, enlaces y propiedades.
- **criterio de cierre:** funciona sin red y no ralentiza notas largas.

## MED-06 · CAPTURA DESDE LA LINEA DE COMANDOS Y EN WAYLAND

> **ESTADO (2026-11-07): implementado; no probado con una sesión Wayland real.** `enki --capture "texto"`
> (también `--capture=texto` y `--capture -` para leer la entrada estándar) entrega la idea a la instancia
> abierta por una tubería con nombre restringida al usuario, y la guarda en el destino de captura rápida
> elegido por última vez. La primera instancia atiende la entrega a través de `SingleInstanceGuard`: el canal
> es nuevo, el guardián solo decide quién es la primera. Detalle y ejemplos para compositores en
> [`captura-desde-linea-de-comandos.md`](../../../project-info/captura-desde-linea-de-comandos.md).

- **tipo:** mejora
- **usuario afectado:** usuarios de linux con wayland y quien automatiza con scripts.
- **evidencia:** `GlobalHotkey` documenta que bajo Wayland un cliente no puede capturar teclas
  globales y el estado queda degradado; no aparece lectura de argumentos de linea de comandos.
- **propuesta:** aceptar un argumento de captura (por ejemplo `enki --capture "texto"`) que entregue la
  idea a la instancia abierta mediante `SingleInstanceGuard`, de modo que el atajo del entorno de
  escritorio lo invoque.
- **criterio de cierre:** la captura funciona en wayland sin atajo global propio.

## MED-07 · KANBAN CON DEPENDENCIAS Y VISTAS ALTERNATIVAS

> **ESTADO (2026-11-07): implementado, pendiente de comprobación manual.** Relación «bloquea a»
> (`KanbanCard.Blocks`) editable en el formulario de tarjeta, con indicador en la tarjeta y en el detalle;
> terminar una tarjeta bloqueada pide confirmación y empezarla avisa; botón «Lista» con una vista ordenable
> sobre las mismas tarjetas. Detalle en [`kanban-dependencias-y-lista.md`](../../../project-info/kanban-dependencias-y-lista.md).

- **tipo:** mejora
- **usuario afectado:** autonomo y estudiante con proyectos de varios pasos.
- **evidencia:** la cadena de dependencias aparece solo en las listas polar (`isDependencyChain` en
  `PolarTaskService`); `KanbanCard` tiene subtareas (`KanbanSubtask`) pero no relaciones entre tarjetas.
- **propuesta:** relacion "bloquea a" entre tarjetas con aviso al mover y una vista de lista ordenable
  sobre los mismos datos, sin segundo almacen.
- **criterio de cierre:** una tarjeta bloqueada lo indica y no se completa sin confirmar.

## MED-08 · PAQUETE DE DIAGNOSTICO EXPORTABLE

> **ESTADO (2026-11-07): implementado, pendiente de comprobación manual.** Ajustes › Mantenimiento ›
> Diagnóstico › «Exportar paquete de diagnóstico»: genera el `.zip`, enseña cada archivo y solo lo escribe
> si se confirma. Detalle en [`paquete-de-diagnostico.md`](../../../project-info/paquete-de-diagnostico.md).

- **tipo:** nuevo
- **usuario afectado:** quien sufre un fallo y quiere reportarlo sin mostrar sus notas.
- **evidencia:** `LogService` y `StartupTrace` registran localmente; no hay telemetria (correcto para el
  avatar) pero tampoco una forma de reunir version, sistema y registros recientes.
- **propuesta:** accion en ajustes que genera un `.zip` con version, sistema, ajustes sin credenciales y
  registros recortados, mostrando su contenido antes de guardarlo.
- **criterio de cierre:** el paquete nunca incluye notas, rutas del vault ni claves.
