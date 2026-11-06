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

- **tipo:** nuevo
- **usuario afectado:** quien construye un segundo cerebro y olvida enlazar.
- **evidencia:** el editor ya muestra backlinks (`EditorBacklink`) pero no aparecen menciones sin
  enlazar ni notas relacionadas. es la tarea `T7` del analisis previo.
- **propuesta:** bloques "relacionadas" (etiquetas y enlaces compartidos) y "menciones sin enlazar" en
  el panel lateral, con enlazado en un clic mediante `VaultService.WriteContentChecked`.
- **criterio de cierre:** convertir una mencion en `[[enlace]]` actualiza la nota sin perder cambios.

## MED-03 · MIGRACION DESDE MAS HERRAMIENTAS CON INFORME

- **tipo:** mejora
- **usuario afectado:** quien llega desde otra aplicacion y no quiere empezar de cero.
- **evidencia:** `ExternalImportService` lee Obsidian (`ReadObsidian`) y Notion (`ReadNotion`). no
  aparecen lectores de Logseq, Evernote (`.enex`), Google Keep, ni de tareas de Todoist o TickTick.
- **propuesta:** nuevos lectores sobre el mismo `Plan` y un informe final con lo importado, lo omitido y
  por que, reutilizando `ImportInterruptedException` para no dejar importaciones a medias sin avisar.
- **criterio de cierre:** una importacion de prueba por formato termina con informe y sin escribir fuera
  del vault ni de las entidades reales.

## MED-04 · EXPORTAR EL CALENDARIO Y LOS RECORDATORIOS A ICS

- **tipo:** nuevo
- **usuario afectado:** quien quiere ver sus fechas en el movil o en otro calendario sin cuenta.
- **evidencia:** `ExternalCalendarService` se suscribe a calendarios `.ics` y `webcal://` de solo lectura;
  no aparece una exportacion hacia `.ics`.
- **propuesta:** exportar a fichero `.ics` los elementos de `CalendarAggregationService` con su
  recurrencia, como accion manual y sin servidor.
- **criterio de cierre:** el fichero se importa en un calendario externo con las mismas horas y
  repeticiones.

## MED-05 · CORRECTOR ORTOGRAFICO DEL EDITOR

- **tipo:** nuevo
- **usuario afectado:** estudiante y escritor; casi todo el que redacta texto largo.
- **evidencia:** no aparece ningun servicio de correccion ortografica; `Spell` solo coincide con una
  declaracion de interoperabilidad en `X11GlobalHotkey`.
- **propuesta:** subrayado de palabras desconocidas con diccionarios locales por idioma de la
  aplicacion, desactivable, que ignore codigo, enlaces y propiedades.
- **criterio de cierre:** funciona sin red y no ralentiza notas largas.

## MED-06 · CAPTURA DESDE LA LINEA DE COMANDOS Y EN WAYLAND

- **tipo:** mejora
- **usuario afectado:** usuarios de linux con wayland y quien automatiza con scripts.
- **evidencia:** `GlobalHotkey` documenta que bajo Wayland un cliente no puede capturar teclas
  globales y el estado queda degradado; no aparece lectura de argumentos de linea de comandos.
- **propuesta:** aceptar un argumento de captura (por ejemplo `enki --capture "texto"`) que entregue la
  idea a la instancia abierta mediante `SingleInstanceGuard`, de modo que el atajo del entorno de
  escritorio lo invoque.
- **criterio de cierre:** la captura funciona en wayland sin atajo global propio.

## MED-07 · KANBAN CON DEPENDENCIAS Y VISTAS ALTERNATIVAS

- **tipo:** mejora
- **usuario afectado:** autonomo y estudiante con proyectos de varios pasos.
- **evidencia:** la cadena de dependencias aparece solo en las listas polar (`isDependencyChain` en
  `PolarTaskService`); `KanbanCard` tiene subtareas (`KanbanSubtask`) pero no relaciones entre tarjetas.
- **propuesta:** relacion "bloquea a" entre tarjetas con aviso al mover y una vista de lista ordenable
  sobre los mismos datos, sin segundo almacen.
- **criterio de cierre:** una tarjeta bloqueada lo indica y no se completa sin confirmar.

## MED-08 · PAQUETE DE DIAGNOSTICO EXPORTABLE

- **tipo:** nuevo
- **usuario afectado:** quien sufre un fallo y quiere reportarlo sin mostrar sus notas.
- **evidencia:** `LogService` y `StartupTrace` registran localmente; no hay telemetria (correcto para el
  avatar) pero tampoco una forma de reunir version, sistema y registros recientes.
- **propuesta:** accion en ajustes que genera un `.zip` con version, sistema, ajustes sin credenciales y
  registros recortados, mostrando su contenido antes de guardarlo.
- **criterio de cierre:** el paquete nunca incluye notas, rutas del vault ni claves.
