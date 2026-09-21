<div align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset=".docs/resources/project-icons/dark-icon/enki-icon-light.png">
    <source media="(prefers-color-scheme: light)" srcset=".docs/resources/project-icons/light-icon/enki-icon-dark.png">
    <img alt="enki" src=".docs/resources/project-icons/dark-icon/enki-icon-light.png" width="96">
  </picture>

  <h1>ENKI</h1>
  <p><em>tu segundo cerebro, en tu escritorio.</em></p>

  <p>
    <a href="README.en.md">read in english</a>
  </p>
</div>

---

enki es una aplicacion de escritorio pensada para una sola persona: tu. reune notas, archivos, tareas, calendario, recordatorios, habitos y espacios de trabajo en un mismo lugar, sin obligarte a depender de una cuenta ni de una conexion permanente.

nacio de una frustracion sencilla: tener que abrir varias aplicaciones para entender que importa hoy. enki mantiene cada cosa en su sitio, pero permite relacionarlas cuando tiene sentido. una nota puede estar vinculada a una tarea, una fecha puede verse en el calendario y una etiqueta puede organizar contenido de varios modulos sin duplicarlo.

> el objetivo no es que uses enki todo el dia. es que, cuando lo abras, sepas exactamente donde esta lo que necesitas.

---

## QUE HACE DIFERENTE A ENKI

- **tus datos son tuyos.** las notas son archivos markdown normales dentro de una carpeta que eliges. puedes abrirlos con otro editor, copiarlos, sincronizarlos o respaldarlos con la herramienta que prefieras.
- **funciona primero en local.** notas, archivos, kanban, calendario, recordatorios, habitos, estadisticas y sesiones funcionan sin internet. polar es una ampliacion opcional para quien quiera sincronizar tareas.
- **conecta sin amontonar.** cada modulo se puede usar por separado, pero todos leen las mismas notas, tareas, fechas y etiquetas cuando necesitan cruzarse.
- **protege el trabajo silenciosamente.** el autoguardado, el historial de versiones, la papelera y la deteccion de cambios externos reducen el riesgo de perder contenido sin interrumpir la escritura.
- **evita la productividad como competicion.** los habitos y las estadisticas describen lo que haces, sin puntos, premios ni presion artificial.
- **se adapta a ti.** puedes cambiar la disposicion, los atajos, el tema, las fuentes, el zoom, el dashboard y el orden de la barra lateral.

---

## TODO LO QUE PUEDES HACER

### CAPTURAR SIN ROMPER EL RITMO

- guarda una idea como nota en `Inbox/` o como tarjeta kanban desde una ventana de captura rapida.
- abre la captura desde la bandeja del sistema, la paleta de comandos, un atajo dentro de enki o, cuando la plataforma lo permite, un atajo global configurable.
- cierra la captura sin miedo: el borrador se conserva hasta que lo envies correctamente.
- pega una direccion web para crear una nota limpia con el titulo, el enlace original y el contenido legible de la pagina.
- cambia entre vaults recientes sin reiniciar la aplicacion y deja que cada vault recupere su propio espacio de trabajo.

### ESCRIBIR Y CUIDAR TUS NOTAS

- crea notas markdown, carpetas y subcarpetas, importa documentos existentes y mueve el contenido con arrastrar y soltar.
- trabaja con varias notas abiertas en pestanas, recuperadas automaticamente al volver a iniciar enki.
- usa una vista completa para explorar el vault o una vista simplificada centrada en el arbol de documentos y el editor.
- escribe con autoguardado, deshacer y rehacer, busqueda y reemplazo, numeros de linea, ajuste de linea, ancho de lectura y alineacion configurables.
- aplica encabezados, negrita, cursiva, tachado, citas, listas, tareas, enlaces, imagenes, codigo y separadores desde la barra de formato o mediante atajos.
- continua y renumera listas de forma inteligente, anida elementos, mueve o duplica lineas y pega una url sobre texto seleccionado para convertirlo en enlace.
- inserta comandos con `/` para no tener que recordar toda la sintaxis markdown.
- consulta el esquema de encabezados, los backlinks, las estadisticas de lectura, las etiquetas, el historial y las tareas vinculadas sin salir del editor.
- activa el modo zen cuando quieras ocultar la navegacion y dejar solo el contenido.

### VER MARKDOWN COMO CONTENIDO, NO COMO RUIDO

- visualiza el formato directamente mientras escribes, manteniendo la sintaxis accesible cuando colocas el cursor sobre ella.
- navega por `[[wikilinks]]`, detecta enlaces rotos y conserva sus referencias cuando una nota cambia de nombre.
- inserta imagenes desde un archivo, desde el portapapeles, mediante arrastrar y soltar o con la sintaxis de enlaces de markdown y obsidian.
- crea tablas sin escribir cada separador a mano y edita filas y columnas desde las herramientas del propio editor.
- muestra avisos de tipo nota, consejo, advertencia o peligro con la sintaxis compatible con obsidian.
- resalta bloques de codigo segun su lenguaje y permite copiarlos con una sola accion.
- representa formulas latex y diagramas mermaid de forma local, sin depender de una cdn ni enviar el contenido fuera del equipo.
- embebe una nota completa, una seccion o un bloque con `![[nota]]`, manteniendo un enlace directo al contenido original.
- exporta una nota a html, png o pdf; las notas largas se reparten en paginas listas para guardar o compartir.

### REPETIR ESTRUCTURAS SIN REPETIR TRABAJO

- crea notas desde plantillas markdown guardadas dentro del propio vault.
- usa variables de titulo, fecha, hora, dia, mes, ano o identificador, con formato y desplazamientos opcionales.
- elige una plantilla propia para la nota diaria y deja que tambien defina su nombre.
- recorre dias anteriores y siguientes desde la cabecera de una nota diaria, vuelve a hoy o abre cualquier fecha desde el calendario de constancia.
- combina una nota diaria existente con su plantilla para anadir solo las secciones que todavia faltan.
- consulta diez semanas de notas diarias y habitos desde una vista discreta, sin convertir la constancia en una puntuacion.

### RECUPERAR LO QUE CAMBIO

- revisa el historial local de cada nota, compara una version anterior linea a linea y restaurala cuando lo necesites.
- deja que enki cree puntos de restauracion automaticos mientras trabajas y antes de operaciones delicadas.
- sigue editando el mismo vault con syncthing, dropbox u otro editor: enki detecta los cambios hechos fuera de la aplicacion.
- recarga automaticamente una nota que no tiene cambios locales o decide entre conservar tu version, cargar la del disco y comparar ambas cuando existe un conflicto.
- conserva una copia de la version descartada antes de resolver un conflicto, para que ninguna eleccion destruya contenido de forma silenciosa.
- recupera notas, archivos, tareas, listas, sesiones y recordatorios desde una papelera comun antes de que llegue el momento de eliminarlos definitivamente.

### EXPLORAR CONOCIMIENTO Y PROYECTOS

- organiza grupos de notas en sesiones separadas para concentrarte en un proyecto, un estudio, un viaje o cualquier contexto concreto.
- protege una sesion con contrasena y codigos de recuperacion; el contenido de sus notas y adjuntos queda cifrado cuando la sesion esta bloqueada.
- explora las relaciones entre notas en un grafo interactivo construido a partir de wikilinks y enlaces manuales.
- contempla el vault completo, incluso sin sesiones, y filtra el grafo por sesion, etiqueta o grupo conectado.
- descubre notas huerfanas y conexiones que atraviesan distintas sesiones sin cambiar ni duplicar tus archivos.

### GUARDAR Y CONSULTAR OTROS ARCHIVOS

- importa imagenes, pdf, audio, video, codigo, texto y otros adjuntos dentro del vault o de una sesion.
- navega los archivos como un explorador con carpetas y tarjetas o como un arbol compacto junto a la vista previa.
- busca dentro de subcarpetas, filtra por tipo y ordena por nombre, fecha, tamano o formato.
- previsualiza imagenes, svg, pdf y archivos de codigo o texto sin salir de enki.
- recorre las paginas de un pdf, ajusta el zoom y consulta miniaturas sin cargar el documento completo de golpe.
- abre los formatos sin visor con la aplicacion del sistema, muestra su ubicacion, copia su ruta o importalos directamente en una carpeta.
- renombra, mueve y elimina archivos conservando actualizada la vista que tengas abierta.

### PLANIFICAR TRABAJO LOCAL CON KANBAN

- organiza tarjetas en columnas que puedes crear, ordenar y adaptar a tu flujo.
- anade descripcion, prioridad, fecha limite, duracion, etiquetas, subtareas y una nota vinculada.
- mueve y reordena tarjetas con arrastrar y soltar, o deja que el tablero las ordene por prioridad, fecha o creacion.
- busca y filtra el tablero sin perder el contexto de las columnas.
- archiva las tareas completadas para limpiar el tablero sin borrarlas ni sacarlas del calendario, las estadisticas o la busqueda.
- abre las tareas archivadas en una vista propia, filtralas, ordenalas, editalas, restauralas o envialas a la papelera.
- exporta el tablero completo a png o pdf aunque todas sus columnas no quepan a la vez en pantalla.

### SINCRONIZAR TAREAS CON POLAR

- vincula opcionalmente una cuenta polar para compartir tareas con su aplicacion android mediante supabase.
- trabaja con listas, tareas, subtareas, prioridades, fechas, estimaciones, recurrencias, etiquetas y cadenas de dependencias.
- sigue trabajando sin conexion y sincroniza los cambios cuando la red vuelva a estar disponible.
- recibe actualizaciones en tiempo real y consulta el estado de la sincronizacion sin convertir la nube en un requisito para usar enki.
- guarda los tokens de sesion en el almacen seguro del sistema operativo y evita incluirlos en exportaciones o copias de ajustes.

### VER EL TIEMPO EN UN SOLO CALENDARIO

- reune tarjetas kanban, tareas polar y recordatorios en vistas de mes, semana, dia y agenda.
- muestra u oculta cada origen, filtra por prioridad o etiquetas y decide si quieres ver elementos completados u ocurrencias futuras.
- crea recordatorios o tarjetas directamente desde un hueco del calendario.
- arrastra una tarea para cambiar su fecha y ajusta la duracion de una tarjeta kanban desde el propio bloque.
- configura el primer dia de la semana, los fines de semana, el horario laboral y el formato de hora.
- exporta la vista actual a png o pdf con sus dias y elementos, sin los controles de navegacion.

### RECORDAR SIN RECIBIR RUIDO

- crea recordatorios independientes con titulo, descripcion, fecha, hora e icono.
- consulta lo vencido, lo previsto para hoy y lo que se acerca desde una bandeja comun.
- recibe avisos dentro de enki, notificaciones de escritorio y sonido solo cuando tu configuracion lo permita.
- activa un horario de no molestar: los avisos siguen guardados, pero no interrumpen.
- aplaza un recordatorio durante unos minutos, hasta mas tarde, hasta manana o hasta un momento personalizado sin cambiar su fecha original.
- revisa un centro de notificaciones persistente, filtra lo importante y marca todo como leido cuando hayas terminado.

### OBSERVAR HABITOS Y TENDENCIAS

- crea habitos diarios o para dias concretos de la semana y marca lo hecho con una accion.
- consulta una cuadricula de constancia de diez semanas sin puntos, recompensas ni mensajes de culpa.
- revisa estadisticas por semana, mes o todo el historial para entender actividad, ritmo, tareas, conocimiento, habitos y almacenamiento.
- observa notas creadas y editadas, palabras escritas, dias activos, tareas completadas, prioridades, progreso de subtareas y uso del vault.
- usa las cifras como contexto descriptivo: enki no las convierte en objetivos obligatorios.

### ORDENAR ETIQUETAS EN TODO ENKI

- consulta en una sola vista las etiquetas activas de notas, tarjetas kanban y tareas polar.
- busca por nombre y comprueba cuantas veces aparece cada etiqueta en cada modulo.
- renombra, fusiona o elimina etiquetas sin tener que editar cada elemento por separado.
- conserva el historial y el resto del contenido de las notas mientras se actualizan sus etiquetas.

### ENCONTRAR Y EJECUTAR ACCIONES RAPIDAMENTE

- abre la busqueda global para encontrar notas, tarjetas kanban, tareas polar y recordatorios desde una sola caja.
- usa el quick switcher para saltar a una nota por titulo, contenido previo o etiquetas.
- abre la paleta de comandos para navegar, crear, importar, sincronizar, cambiar el tema, controlar el zoom o ejecutar acciones sin recorrer menus.
- personaliza los atajos y restablece cualquiera de ellos si una combinacion deja de encajar contigo.
- reorganiza las secciones de la barra lateral y fija hasta tres accesos junto a ajustes.

### ADAPTAR EL ESPACIO A TU FORMA DE TRABAJAR

- configura un dashboard con el resumen de hoy, notas recientes, estado de tareas, habitos, actividad, almacenamiento y accesos rapidos.
- elige que bloques aparecen, en que orden y con que anchura se distribuyen.
- cambia entre tema claro, oscuro o temas de color propios editados desde la aplicacion.
- usa una imagen personalizada como logo de la barra lateral y decide si se muestra cuadrada o circular.
- elige fuentes independientes para la interfaz, las notas y el codigo.
- ajusta el zoom general entre `50 %` y `200 %`, amplia o contrae la barra lateral y decide que pagina se abre al iniciar.
- cambia el idioma en caliente entre english (us), english (uk), deutsch, espanol (espana), espanol (latinoamerica) y francais.
- recorre los flujos principales con teclado, nombres accesibles y orden de foco preparado para tecnologias de asistencia.

### CONSERVAR SKILLS COMO CONOCIMIENTO REUTILIZABLE

- guarda skills en markdown con instrucciones, contexto, herramientas, etiquetas y permisos descritos en el propio archivo.
- crea carpetas, importa, edita, duplica, mueve y exporta skills desde una biblioteca con vista de explorador o arbol.
- activa esta seccion solo si te resulta util; permanece oculta por defecto.
- entiende su alcance actual: enki organiza las skills como contenido reutilizable, pero todavia no incorpora un motor de ia que las ejecute.

---

## COMO SE CONECTA TODO

enki evita crear copias paralelas de la misma informacion. el calendario muestra las fechas que ya viven en kanban, polar y recordatorios; el dashboard resume esos mismos datos; el gestor de etiquetas modifica las etiquetas originales; y el grafo representa las relaciones que ya existen entre tus notas.

un flujo cotidiano puede empezar con una idea escrita en la captura rapida, continuar como nota dentro de una sesion, convertirse en una tarjeta kanban vinculada y terminar situada en el calendario. si la completas, el tablero, el dashboard, la busqueda y las estadisticas ven el cambio sin que tengas que mantener cuatro versiones de la misma tarea.

---

## DATOS, PRIVACIDAD Y SEGURIDAD

- el contenido principal vive en el vault que eliges y los ajustes auxiliares se guardan en el perfil local de la aplicacion.
- enki no incluye telemetria ni envia tus notas a un servicio propio.
- el nucleo funciona offline; la red solo se usa en funciones explicitas como polar, guardar una pagina web, cargar una imagen remota o comprobar si existe una version nueva.
- la comprobacion automatica de versiones esta desactivada de fabrica y, cuando se activa, solo abre la pagina de descarga elegida por el usuario.
- las imagenes remotas se descargan con limites de tamano, tiempo y redireccion, y se bloquean direcciones privadas o locales.
- los guardados importantes se realizan de forma segura para evitar archivos incompletos si una operacion falla.
- las sesiones protegidas cifran el contenido de las notas y sus adjuntos; los nombres de archivo, la estructura y las fechas permanecen visibles en disco.
- puedes crear una copia zip del vault y exportar o importar los ajustes cuando necesites trasladar tu entorno.

---

## ESTADO DEL PROYECTO

> en desarrollo activo. la version declarada actualmente es `0.1.0` y el siguiente conjunto de cambios permanece en la seccion `SIN PUBLICAR / UNRELEASED` del changelog.

enki ya cubre el flujo diario completo y dispone de empaquetado para windows, linux y macos. aun es un proyecto joven: antes de considerar una version candidata se revisan manualmente los recorridos que dependen del sistema operativo, el lector de pantalla, la sincronizacion real y la convivencia del vault con otras aplicaciones.

el historial detallado esta en [`CHANGELOG.md`](CHANGELOG.md). la lista de cambios sin publicar explica lo que ya existe en el codigo pero todavia no forma parte de una version cerrada.

---

## PRIMEROS PASOS DESDE EL REPOSITORIO

necesitas el sdk de .net indicado por el proyecto. despues puedes compilar, ejecutar y comprobar la aplicacion con estos comandos:

```bash
# compilar
dotnet build enki/src/enki/enki.csproj

# ejecutar
dotnet run --project enki/src/enki/enki.csproj

# ejecutar los tests
dotnet test enki/tests/enki.Tests/enki.Tests.csproj
```

en el primer arranque, enki pide el idioma y la carpeta que funcionara como vault. puedes crear una nueva o abrir una que ya contenga tus notas. a partir de ahi, el contenido se guarda en esa carpeta y el resto de funciones locales queda disponible sin configurar ninguna cuenta.

los builds de desarrollo usan datos separados de una instalacion normal, por lo que una copia recien clonada empieza limpia y no mezcla su configuracion con la version de uso diario.

---

## ACTIVAR POLAR DE FORMA OPCIONAL

polar solo aparece como opcion de cuenta cuando el proyecto conoce la direccion publica de supabase y su clave publicable. para un entorno de desarrollo, crea `enki/local.properties`, que esta ignorado por git:

```properties
SUPABASE_URL=<url-del-proyecto>
SUPABASE_ANON_KEY=<clave-publicable>
```

si tu entorno usa `SUPABASE_PUBLISHABLE_KEY`, copia ese valor en `SUPABASE_ANON_KEY`. no uses `SUPABASE_SECRET_KEY`: enki no necesita ni debe recibir una clave secreta de servidor.

sin este archivo, enki sigue funcionando con normalidad y conserva todas sus funciones locales.

---

## DOCUMENTACION DEL PROYECTO

la documentacion versionada vive en [`.docs/project-info/`](.docs/project-info/). `docs/` esta ignorada por git y no forma parte de este repositorio. estos son los puntos de entrada principales:

- [inventario funcional](.docs/project-info/inventario-funcionalidades-y-mejora-continua.md) — recorrido detallado por las funciones existentes y como se relacionan.
- [analisis del proyecto](.docs/project-info/analisis-proyecto/analisis-deficiencias-proyecto.md) — auditoria del estado del producto y sus riesgos.
- [roadmap](.docs/project-info/analisis-proyecto/roadmap.md) — fases de mejora y estado de cada una.
- [editor markdown](.docs/project-info/editor-markdown.md) — comportamiento de escritura y vista previa.
- [archivos](.docs/project-info/seccion-archivos.md) — navegacion, previsualizacion y organizacion de adjuntos.
- [calendario](.docs/project-info/calendar-feature/00-README.md) — vision y alcance de la vista unificada.
- [sincronizacion polar](.docs/project-info/supabase-sync/00-README.md) — decisiones y limites de la sincronizacion opcional.
- [notificaciones y recordatorios](.docs/project-info/notificaciones-y-recordatorios.md) — avisos, horario de no molestar y aplazamientos.
- [cambios externos](.docs/project-info/cambios-externos-en-el-vault.md) — proteccion frente a ediciones realizadas por otras aplicaciones.
- [sistema de diseno](.docs/project-info/sistema-diseno-web.md) — principios visuales y de interaccion.
- [qa de release candidate](.docs/project-info/qa-manual-release-candidate.md) — verificaciones manuales antes de publicar una version.

las convenciones para contribuir estan en [`AGENTS.md`](AGENTS.md), las reglas de commits en [`.docs/commit-guidelines.md`](.docs/commit-guidelines.md) y el formato de pull requests, merges y releases en [`.docs/pr-merge-release-guidelines.md`](.docs/pr-merge-release-guidelines.md).

---

<div align="center">
  <p><sub>construido con paciencia para uso personal.</sub></p>
  <p><a href="CHANGELOG.md">changelog</a> · <a href="LICENSE">licencia</a></p>
</div>
