# Manual de Enki

Manual de usuario que consulta el asistente integrado. Cada apartado `##` es un tema independiente. Los atajos de
teclado no figuran aquí: se añaden en cada consulta con el gesto que el usuario tiene configurado.

## Primeros pasos, vault e idioma

Enki guarda todo en un vault: una carpeta del equipo que eliges en el primer arranque, junto con el idioma. Las notas
son archivos Markdown normales dentro de esa carpeta, así que se pueden abrir, copiar o sincronizar con otras
herramientas.

- Cambiar de vault: usa el selector de vaults recientes de la ventana principal, o Ajustes › Vault y archivos. Cada
  vault recuerda su propio espacio de trabajo.
- Cambiar de idioma: Ajustes › General. El cambio se aplica al momento, sin reiniciar.
- Abrir la carpeta del vault en el explorador del sistema: paleta de comandos › «Abrir carpeta del almacén».
- Enki funciona sin conexión. La red solo se usa en funciones que tú activas: Polar, guardar una página web, cargar una
  imagen remota, comprobar versiones o el asistente de IA.

## Navegación: barra lateral, búsqueda global, quick switcher y paleta de comandos

- La barra lateral lleva a cada sección: Dashboard, Calendario, Kanban, Notas, Archivos, Sesiones, Papelera, Hábitos,
  Habilidades, Asistente, Etiquetas, Recordatorios, Notificaciones y Estadísticas. Ajustes está al final.
- Mostrar, ocultar o reordenar secciones: Ajustes › Navegación. Una sección desactivada desaparece de la barra, de la
  paleta y de los atajos, pero sus datos se conservan. Se pueden fijar hasta tres accesos junto a Ajustes.
- Búsqueda global («Buscar en todo»): encuentra notas, tarjetas de Kanban, tareas de Polar y recordatorios desde una
  sola caja.
- Quick switcher («Ir a la nota…»): salta a una nota por título, contenido o etiquetas.
- Paleta de comandos: ejecuta acciones sin recorrer menús, como «Nota nueva», «Nueva nota desde plantilla», «Abrir la
  nota diaria de hoy», «Importar notas», «Alternar tema claro/oscuro», «Alternar modo enfoque» o «Ir a» cualquier
  sección.
- Zoom de la interfaz: entre el 50 % y el 200 %, con sus atajos, con Ctrl y la rueda del ratón o desde la paleta
  («Acercar», «Alejar», «Restablecer zoom»).

## Notas: crear, organizar y pestañas

- Crear una nota: botón de nota nueva en la sección Notas, el comando «Nota nueva» de la paleta o su atajo.
- Las notas se organizan en carpetas y subcarpetas; se mueven arrastrando y soltando. También se pueden importar
  documentos existentes con «Importar notas».
- Varias notas pueden estar abiertas en pestañas; al reiniciar Enki se recuperan.
- Hay una vista completa para explorar el vault y una vista simplificada centrada en el árbol de documentos y el editor.
- Renombrar una nota actualiza los `[[wikilinks]]` que apuntan a ella y las tarjetas de Kanban vinculadas.
- Borrar una nota la envía a la Papelera, donde se puede restaurar durante 30 días.

## Editor Markdown

- El editor guarda solo (autoguardado) y permite deshacer y rehacer, buscar y reemplazar, números de línea, ajuste de
  línea, ancho de lectura y alineación configurables en Ajustes › Notas y editor.
- Formato desde la barra de formato o con atajos: encabezados, negrita, cursiva, tachado, resaltado, citas, listas,
  tareas, enlaces, imágenes, código y separadores.
- Escribe `/` en el editor para insertar un comando sin recordar la sintaxis Markdown.
- Las listas continúan y se renumeran solas; se pueden anidar elementos y mover o duplicar líneas.
- Pegar una dirección web sobre un texto seleccionado lo convierte en enlace.
- Junto al editor se consultan el esquema de encabezados, los backlinks, las estadísticas de lectura, las etiquetas, el
  historial y las tareas vinculadas.
- Modo enfoque (zen): oculta la navegación y deja solo el contenido.

## Wikilinks, imágenes, tablas, fórmulas y contenido embebido

- `[[Título de la nota]]` enlaza a otra nota. Los enlaces rotos se señalan y se conservan al renombrar.
- `![[nota]]` embebe una nota completa, una sección o un bloque, con enlace al original.
- Imágenes: desde un archivo, el portapapeles, arrastrando y soltando o con la sintaxis de Markdown y de Obsidian.
- Tablas: se crean y editan con las herramientas del editor, sin escribir cada separador a mano.
- Avisos (nota, consejo, advertencia, peligro) con la sintaxis compatible con Obsidian.
- Los bloques de código se resaltan según su lenguaje y se copian con una acción.
- Las fórmulas LaTeX y los diagramas Mermaid se dibujan en local, sin enviar el contenido fuera del equipo.

## Plantillas de notas

- Una plantilla es un archivo Markdown de `.enki/templates`, dentro del vault. Puede declarar en su cabecera YAML un
  nombre (`name`), una descripción (`description`) y un patrón de título (`title`).
- Crear una nota desde una plantilla: en Notas, «Desde plantilla», o el comando «Nueva nota desde plantilla».
- Crear o editar plantillas: desde el propio selector de plantillas («Nueva plantilla», «Editar plantilla»).
- Variables disponibles: título, fecha, hora, día, mes, año e identificador, con formato y desplazamientos opcionales
  (por ejemplo `{{date}}` o `{{title}}`).

## Nota diaria

- Abrir la nota de hoy: botón «Nota diaria» de la sección Notas o el comando «Abrir la nota diaria de hoy». Si no existe,
  se crea.
- Se puede elegir una plantilla propia para la nota diaria en Ajustes › Notas y editor; esa plantilla también decide el
  nombre de la nota.
- Desde la cabecera de una nota diaria se va al día anterior, al siguiente o de vuelta a hoy.
- Si la nota diaria ya existe, el botón de combinar con la plantilla de su cabecera añade solo las secciones que le
  faltan, sin tocar lo escrito.
- «Constancia de la nota diaria» muestra diez semanas de notas diarias y hábitos, sin convertirlo en una puntuación.

## Historial de versiones y cambios externos

- Cada nota tiene un historial local: se pueden comparar versiones línea a línea y restaurar una anterior. Restaurar
  guarda antes la versión actual, así que siempre hay vuelta atrás.
- Enki crea puntos de restauración mientras trabajas y antes de operaciones delicadas.
- Si otra aplicación (Syncthing, Dropbox, otro editor) cambia una nota abierta, Enki lo detecta: la recarga si no tenías
  cambios, o te deja elegir entre conservar tu versión, cargar la del disco o compararlas.

## Sesiones y grafo

- Una sesión es un espacio de notas separado para un proyecto, un estudio o un viaje, con su propio árbol de notas.
- Una sesión se puede proteger con contraseña y códigos de recuperación: el contenido de sus notas y adjuntos queda
  cifrado mientras está bloqueada.
- El grafo muestra las relaciones entre notas a partir de los wikilinks y enlaces manuales; se filtra por sesión,
  etiqueta o grupo conectado y ayuda a encontrar notas huérfanas.

## Archivos

- La sección Archivos guarda imágenes, PDF, audio, vídeo, código y otros adjuntos dentro del vault o de una sesión.
- Se navega como un explorador con carpetas y tarjetas o como un árbol junto a la vista previa.
- Se puede buscar en subcarpetas, filtrar por tipo y ordenar por nombre, fecha, tamaño o formato.
- Imágenes, SVG, PDF y archivos de código o texto se previsualizan dentro de Enki; el resto se abre con la aplicación
  del sistema.

## Kanban

- Un tablero tiene columnas (pendiente, en curso, hecho y las que añadas) y tarjetas con descripción, prioridad, fecha
  límite, duración, etiquetas, subtareas, enlaces web y una nota vinculada.
- Las tarjetas se mueven y reordenan arrastrando; llevar una a la columna «Hecho» la completa.
- El tablero se busca, filtra y ordena por prioridad, fecha o creación.
- Las tarjetas completadas se pueden archivar: salen del tablero pero siguen en el calendario, las estadísticas y la
  búsqueda. Hay una vista propia para las archivadas.
- Con varios tableros activados, el selector de tableros permite crear, renombrar y cambiar de tablero.
- El tablero se exporta a PNG o PDF desde la paleta («Exportar tablero como PDF»).

## Polar: tareas sincronizadas

- Polar es opcional. Vincula la cuenta en Ajustes › Cuenta Polar para compartir listas, tareas y subtareas con la
  aplicación móvil.
- Sin conexión se sigue trabajando; los cambios se sincronizan al volver la red. «Sincronizar Polar ahora» fuerza una
  sincronización desde la paleta.
- Las tareas de Polar se ven dentro de Kanban y del Calendario.

## Calendario

- Reúne tarjetas de Kanban, tareas de Polar, recordatorios y calendarios externos suscritos en vistas de mes, semana,
  día y agenda.
- Cada origen se puede mostrar u ocultar, y se filtra por prioridad o etiquetas.
- Se crean recordatorios o tarjetas desde un hueco del calendario, y arrastrar un elemento cambia su fecha.
- Primer día de la semana, fines de semana, horario laboral y formato de hora: Ajustes › Calendario.
- La vista actual se exporta a PNG o PDF.

## Recordatorios y notificaciones

- Un recordatorio tiene título, descripción, fecha, hora e icono, y puede repetirse.
- La bandeja de recordatorios reúne lo vencido, lo de hoy y lo que se acerca.
- Un recordatorio se puede aplazar unos minutos, hasta más tarde, hasta mañana o hasta un momento concreto sin cambiar su
  fecha original.
- Avisos de escritorio, sonido y horario de no molestar: Ajustes › Notificaciones.
- El centro de notificaciones conserva los avisos, permite filtrarlos y marcarlos como leídos.

## Hábitos y estadísticas

- Un hábito puede ser diario, semanal o de días concretos de la semana. Se marca como hecho con una acción en la sección
  Hábitos.
- La cuadrícula de constancia muestra diez semanas, sin puntos ni recompensas.
- Estadísticas resume por semana, mes o todo el historial: notas creadas y editadas, palabras, días activos, tareas
  completadas, hábitos y uso del vault.

## Etiquetas

- Las etiquetas de una nota viven en su cabecera YAML; las tarjetas y las tareas tienen las suyas.
- La sección Etiquetas reúne las de notas, tarjetas de Kanban y tareas de Polar: se pueden buscar, renombrar, fusionar o
  eliminar sin editar cada elemento a mano.

## Papelera

- Notas, archivos, carpetas, sesiones, tarjetas, tareas, listas y recordatorios borrados van a una papelera común.
- Desde la Papelera se restaura cualquier elemento o se elimina para siempre. Lo eliminado para siempre no se puede
  recuperar.
- Los elementos se conservan 30 días.

## Captura rápida y guardar páginas web

- La captura rápida guarda una idea como nota en `Inbox/` o como tarjeta de Kanban desde una ventana pequeña. Se abre
  con su atajo, desde la paleta o desde la bandeja del sistema.
- Si la cierras sin enviar, el borrador se conserva.
- Pegar una dirección web crea una nota limpia con el título, el enlace original y el contenido legible de la página.

## Exportar, importar y copias de seguridad

- Una nota se exporta a HTML, PNG o PDF («Exportar nota como PDF» en la paleta).
- El tablero de Kanban y el calendario se exportan a PNG o PDF.
- Copia de seguridad del vault en un archivo ZIP, y exportar o importar los ajustes: Ajustes › Copias de seguridad.
- Para sincronizar el vault entre equipos con Syncthing, iCloud Drive o Google Drive, consulta la guía de
  Ajustes › Vault y archivos.

## Dashboard

- El Dashboard resume el día: notas recientes, estado de tareas, hábitos, actividad, almacenamiento y accesos rápidos.
- Qué bloques aparecen, en qué orden y con qué anchura se configura en Ajustes › Dashboard.

## Apariencia y personalización

- Tema claro, oscuro o temas de color propios: Ajustes › Apariencia. «Alternar tema claro/oscuro» lo cambia desde la
  paleta.
- Fuentes independientes para la interfaz, las notas y el código, y logo propio para la barra lateral: Ajustes ›
  Apariencia.
- Página que se abre al iniciar y otras opciones generales: Ajustes › General.

## Atajos de teclado: cómo cambiarlos

- Todos los atajos con modificador se pueden cambiar en Ajustes › Atajos de teclado, y restablecerse uno a uno o todos a
  la vez.
- Algunas teclas sueltas de contexto (por ejemplo dentro de Kanban) son fijas y se muestran ahí solo como referencia.
- Para saber qué atajo tiene una acción ahora mismo, consulta los apartados «Atajos de teclado» de este manual: reflejan
  la configuración actual del usuario.

## Asistente de IA

- Se activa en Ajustes › Inteligencia artificial, donde se elige el proveedor: Claude, OpenAI (cuenta de ChatGPT mediante
  su CLI), Gemini (clave de API) o un modelo local con Ollama.
- Se usa como sección propia (Asistente) o como panel lateral junto a cualquier otra sección; el botón de la barra de
  título y su atajo lo abren y lo cierran.
- Cada chat tiene un ámbito: el vault o una sesión. El selector de ámbito de la cabecera permite ampliarlo, y el permiso
  de consulta decide si el asistente puede buscar y leer por su cuenta.
- El botón del globo del cuadro de mensaje activa el acceso a internet de ese chat.
- Se pueden adjuntar notas y archivos a un mensaje. Antes de enviarlos se muestra qué se va a enviar.
- Lo que el asistente hace en el vault aparece como una lista de acciones en su respuesta. «Revertir esta respuesta»
  deshace lo que cambió, sin tocar lo que hayas editado después.
- Una respuesta se puede copiar, añadir a la nota activa o guardar como nota nueva.
- Si el proveedor los informa, se muestran los tokens de cada respuesta y el total del chat; se oculta en Ajustes ›
  Inteligencia artificial.
- «Permitir cambios en el vault» apagado (Ajustes › Inteligencia artificial) deja al asistente en modo solo lectura.
- «Revisar los cambios antes de aplicarlos» (Ajustes › Inteligencia artificial) deja lo que el asistente cambia en tus
  notas bajo su respuesta, con las diferencias, para aplicar lo que marques o descartarlo todo.
- El botón de exportar de la cabecera del chat guarda la conversación entera como una nota nueva.
- Al adjuntar una imagen o un PDF, el cuadro de mensaje dice a qué proveedor se enviará y si el modelo podrá verla.
- Pídele el resumen de tu día o de tu semana, cómo vas esta semana, que renombre o fusione etiquetas o que sugiera
  enlaces para una nota; también desde la paleta de comandos («Asistente: …»).
- «Búsqueda semántica local» (Ajustes › Inteligencia artificial) crea con Ollama, en este equipo, un índice para que el
  asistente encuentre notas por su significado. Se reconstruye o se borra desde el mismo sitio.

## Skills

- Una skill es un archivo Markdown con instrucciones reutilizables. La sección Habilidades las organiza en carpetas y
  permite crearlas, importarlas, editarlas, duplicarlas y exportarlas.
- «Usar en el asistente» empieza un chat con esa skill como instrucciones de partida.
- La sección está oculta por defecto; se activa en Ajustes › Navegación.

## Privacidad y datos

- El contenido vive en el vault que eliges; los ajustes, en el perfil local de la aplicación. Enki no incluye telemetría
  ni envía tus notas a un servicio propio.
- Las sesiones protegidas cifran el contenido de sus notas y adjuntos; los nombres de archivo y la estructura siguen
  visibles en disco.
- Con el asistente, lo que escribes y lo que adjuntas o le dejas consultar se envía al proveedor de IA que hayas
  elegido. Con Ollama todo se queda en el equipo.
