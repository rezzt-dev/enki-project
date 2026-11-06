# INDICE DEL ANALISIS DE PRODUCTO

> mapa de lo que falta, de lo que hay que mejorar y de lo que hay que cambiar en enki, visto desde el
> usuario previsto y desde quienes usarian solo una parte. cada funcionalidad tiene su ficha completa
> (evidencia, propuesta y criterio de cierre) en el fichero de su prioridad.

**Fecha del analisis:** 2026-11-06 · rama `beta-v2.0`
**Metodo:** lectura de la documentacion y busquedas en `enki/src/enki/`. no se ha ejecutado la aplicacion;
si algo no coincide con el codigo real, manda el codigo.

| prioridad | fichero | fichas |
| --- | --- | --- |
| alta | [`prioridad-alta.md`](./prioridad-alta.md) | 5 |
| media | [`prioridad-media.md`](./prioridad-media.md) | 8 |
| baja | [`prioridad-baja.md`](./prioridad-baja.md) | 6 |

---

## EL AVATAR Y SUS ALREDEDORES

**avatar principal:** una sola persona con un vault de markdown local y la privacidad como requisito, que
quiere sustituir varias aplicaciones por una (estudiante, profesional o trabajador del conocimiento).
funciona primero en local, sin cuenta, y rechaza la gamificacion (`readme.md`).

| perfil cercano | que usa de enki | que le falta |
| --- | --- | --- |
| solo notas (llega de obsidian o notion) | vault, editor, enlaces, grafo | ocultar el resto, migracion mas amplia |
| planificador (kanban, calendario, recordatorios) | tareas, fechas, avisos, habitos | exportar a `.ics`, tiempo real, dependencias |
| estudiante | notas, formulas, diagramas, exportar a pdf | corrector, repaso espaciado, movil |
| autonomo o profesional | kanban, calendario, estadisticas | tiempo real, informes, firma de la app |
| usuario con accesibilidad | teclado, zoom, temas | alto contraste, verificacion con lector de pantalla |
| usuario multidispositivo | polar para tareas | sincronizar y capturar notas desde el movil |
| pareja o equipo pequeño | nada pensado para ellos | decidir si se atiende o se descarta |

---

## PRIORIDAD ALTA

[`prioridad-alta.md`](./prioridad-alta.md): sin esto, parte de los usuarios no llega a usar enki a diario.

- **ALT-01 · sincronizacion de notas y captura desde el movil** (nuevo)
  hoy solo las tareas viajan por polar; empezar por una bandeja de ideas y despues sincronizar notas.
- **ALT-02 · modulos opcionales y perfiles de uso** (rediseño)
  la barra lateral reordena pero no oculta; hacer los modulos activables con perfiles de partida.
- **ALT-03 · accesibilidad verificada y alto contraste** (mejora)
  hay 652 propiedades de accesibilidad, pero falta tema de alto contraste y probar con lector de pantalla.
- **ALT-04 · calidad de la entrega en windows y macos** (mejora)
  la integracion continua solo prueba en linux y la entrega no muestra firma ni notarizacion.
- **ALT-05 · documentacion de analisis alineada con el codigo** (cambio)
  varios analisis previos dan por pendiente lo ya hecho; marcarlo y enlazar las prioridades vigentes.

## PRIORIDAD MEDIA

[`prioridad-media.md`](./prioridad-media.md): cierran flujos a medias o amplian el publico.

- **MED-01 · tiempo real trabajado y temporizador de enfoque** (nuevo)
  registrar minutos reales en tarjetas y tareas con avisos y resumen, sin rachas ni puntos.
- **MED-02 · notas relacionadas y menciones sin enlazar** (nuevo)
  bloques en el panel del editor para descubrir y enlazar notas en un clic.
- **MED-03 · migracion desde mas herramientas con informe** (mejora)
  añadir logseq, evernote, keep y tareas de todoist o ticktick, con informe de lo importado y omitido.
- **MED-04 · exportar el calendario y los recordatorios a ics** (nuevo)
  hoy solo se leen calendarios externos; permitir llevar las fechas a otros calendarios.
- **MED-05 · corrector ortografico del editor** (nuevo)
  subrayado local por idioma, desactivable y que ignore codigo y enlaces.
- **MED-06 · captura desde la linea de comandos y en wayland** (mejora)
  argumento `--capture` entregado a la instancia abierta, para usar atajos del propio escritorio.
- **MED-07 · kanban con dependencias y vistas alternativas** (mejora)
  relacion "bloquea a" entre tarjetas y vista de lista sobre los mismos datos.
- **MED-08 · paquete de diagnostico exportable** (nuevo)
  zip revisable con version, sistema y registros recortados, sin notas ni claves.

## PRIORIDAD BAJA

[`prioridad-baja.md`](./prioridad-baja.md): valor real, pero con coste alto o demanda sin validar.

- **BAJ-01 · busqueda semantica local opcional** (nuevo)
  indice de vectores en el vault, desactivado de fabrica y sin enviar nada fuera.
- **BAJ-02 · exportar e importar la personalizacion** (nuevo)
  un perfil unico con temas, atajos, barra lateral y panel para llevar a otro equipo.
- **BAJ-03 · publicar notas como sitio estatico** (nuevo)
  exportar una carpeta o etiqueta a paginas html enlazadas, de solo lectura.
- **BAJ-04 · repaso espaciado para estudio** (nuevo)
  tarjetas de pregunta y respuesta dentro de las notas, sin tocar el markdown ni gamificar.
- **BAJ-05 · mas idiomas de interfaz** (mejora)
  portugues y catalan sobre la comprobacion de paridad de recursos que ya existe.
- **BAJ-06 · tableros compartidos para pareja o equipo pequeño** (exploracion)
  no implementar hasta validar la demanda, porque obliga a repensar identidad y permisos.

---

## DOCUMENTOS PREVIOS EN ESTA CARPETA

- [`funcionalidades-pendientes-priorizadas.md`](./funcionalidades-pendientes-priorizadas.md): lista `T1`–`T13`; `T6`, `T7`, `T12` y `T13` siguen abiertas y aparecen aqui como `MED-01`, `MED-02`, `ALT-01` y `MED-03`.
- [`auditoria-seguridad-y-fallos-criticos.md`](./auditoria-seguridad-y-fallos-criticos.md): problemas de seguridad y fallos; no se repiten.
- [`roadmap-publicacion-y-mejora-continua.md`](./roadmap-publicacion-y-mejora-continua.md): orientacion de publicacion; complementaria.
