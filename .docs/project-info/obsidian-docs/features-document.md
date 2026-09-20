# enki - documento de funcionalidades

> catalogo de las funcionalidades de enki, segmentado entre **funcionalidades principales** (el nucleo del producto) y **funcionalidades secundarias** (complementos y herramientas de apoyo), con un apartado final de **detalles** del proyecto.

---

## 1. funcionalidades principales

### 1.1 📝 notas (vault markdown)

el corazon de enki: un vault de notas en markdown que vive en tu disco, sin bloqueos ni formatos propietarios.

- **carpetas**: crear, renombrar, eliminar y arrastrar notas entre carpetas.
- **etiquetas (`tags`)**: edicion inline, filtrado y deteccion automatica via frontmatter yaml.
- **wikilinks (`[[nota]]`)**: enlazar notas entre si; `ctrl+click` abre la nota enlazada; los enlaces se resaltan en el editor y se reescriben automaticamente al renombrar notas.
- **editor con pestanas multiples**: pestanas reordenables, restauracion al arrancar, autoguardado con debounce e indicador "guardado · hh:mm".
- **barra de formato markdown**: negrita, cursiva, tachado, encabezados h1-h3, citas, listas, listas de tareas, enlaces, imagenes, codigo y regla horizontal.
- **panel lateral del editor**: esquema de encabezados (outline), backlinks y estadisticas (palabras, caracteres, tiempo de lectura).
- **notas diarias**: crear nota con nombre `yyyy-mm-dd.md`.
- **busqueda interna** en la vista de notas (por titulo, preview y tags) y **notas recientes**.
- **importar** archivos markdown.

### 1.2 🗂️ kanban (tablero de tareas local)

un tablero de tareas local, completo y sin depender de la nube.

- **columnas**: tres columnas core ("por hacer", "en progreso", "hecho") mas columnas adicionales, reordenables.
- **tarjetas**: titulo, descripcion, prioridad (alta/media/baja/ninguna), fecha limite, duracion estimada, etiquetas y subtareas.
- **drag & drop**: mover tarjetas entre columnas y reordenar dentro de una columna; automatismos que marcan/desmarcan subtareas al mover a "hecho" o de vuelta.
- **ordenamiento**: manual, por prioridad, fecha o creacion.
- **subtareas** con progreso visible en la tarjeta.
- **nota vinculada**: cada tarjeta puede apuntar a una nota del vault.
- **busqueda y filtros** por titulo, descripcion y tags.
- **borrado a papelera** con confirmacion configurable.

### 1.3 🗓️ calendario unificado

una vista unica que agrega las tareas del kanban, las tareas de polar y los recordatorios, sin duplicar datos.

- **4 vistas**: mes, semana, dia y agenda, con mini-calendario lateral.
- **drag & drop para reprogramar**: arrastrar un item cambia su fecha real de origen; redimensionar el asa inferior ajusta la duracion (kanban).
- **capas**: mostrar/ocultar por origen (kanban, recordatorios, cada lista polar) - las capas ocultas se recuerdan.
- **filtros**: por prioridad, tags, ocultar completadas, ocultar ocurrencias fantasma (proxima aparicion de tareas recurrentes).
- **creacion rapida**: crear un recordatorio o una tarjeta kanban directamente desde un hueco vacio.
- **menu contextual**: editar, completar, eliminar o "crear tarjeta kanban aqui".
- **linea "ahora"** en las vistas semana/dia y configuracion de inicio de semana, fines de semana, horario laboral y formato 24h/am-pm.

### 1.4 🔔 recordatorios

avisos independientes con fecha y hora.

- crear, editar, completar y eliminar recordatorios (con titulo, descripcion, fecha/hora e icono).
- **recurrencia nativa**: un recordatorio recurrente se reprograma solo a su siguiente ocurrencia.
- aparecen en el calendario y alimentan la bandeja de vencimientos.
- se convierten en **notificaciones del sistema** al vencer.
- se sincronizan con polar (tabla `reminders`) si la cuenta esta vinculada.

### 1.5 ☁️ polar (tareas en la nube, opcional)

tareas sincronizadas con la nube (supabase), compartidas con la app android del mismo nombre. **optativo**: si no se vincula, enki funciona exactamente igual.

- **listas de tareas** con nombre, icono, color y orden.
- **tareas** con prioridad, fecha limite, recurrencia, estimacion, etiquetas y subtareas.
- **cadenas de dependencias**: en listas de dependencias, solo la primera tarea incompleta esta desbloqueada.
- **sincronizacion offline-first**: funciona sin conexion y sincroniza cuando vuelve (push/pull, resolucion de conflictos, reintentos con espera progresiva).
- **motor de recurrencia local** (diaria, dias de semana, mensual, etc.) que no depende de estar conectado.

### 1.6 🕸️ sesiones y grafo de notas

subespacios de notas con un grafo visual de relaciones, ideales para proyectos aislados (una tesis, un viaje, un producto).

- crear, renombrar y eliminar sesiones (la sesion "default" no se puede borrar).
- **grafo visual**: nodos = notas, aristas = wikilinks + enlaces manuales; nodos arrastrables con posiciones guardadas.
- **modo enlace manual**: doble clic abre la nota; clic simple en modo enlace une dos notas de la misma sesion.
- **grafo combinado**: vista unificada de todas las sesiones.
- **proteccion con contrasena** (cifrado de sobre aes-gcm + pbkdf2): las sesiones bloqueadas quedan excluidas del grafo, la lista de archivos y las vistas previas hasta que se desbloquean.
  - **que cubre exactamente el cifrado**: el *contenido* de las notas y de sus adjuntos, incluido su historial de versiones. **no** se cifran los nombres de archivo de las notas, el nombre de la sesion, sus fechas, su numero de notas ni la estructura de enlaces del manifiesto: siguen legibles en disco para quien tenga acceso a la carpeta del vault (una copia de seguridad, una carpeta sincronizada, un equipo compartido). es una decision de diseno —la barra lateral tiene que poder mostrar una sesion bloqueada sin abrirla— y el dialogo de crear sesion protegida lo dice ahora con todas las letras. cerrarlo del todo exigiria cifrar tambien los nombres, que es un cambio de formato con migracion.

### 1.7 🏠 dashboard

el centro de mando que resume el dia al abrir la aplicacion.

- **resumen "hoy"**: items de kanban + polar + recordatorios con chips de prioridad, hora y origen.
- **notas recientes**: tarjeta destacada + lista compacta.
- **tareas del tablero**: mini columnas pendiente / en progreso / hecho con barra de progreso.
- **identidad del vault**: ruta, tamano total, numero de notas y sesiones.
- **actividad**: resumen de sincronizacion polar y ultimas notificaciones.
- **accesos rapidos** a calendario, recordatorios, notificaciones y ajustes.

---

## 2. funcionalidades secundarias

### 2.1 🔎 busqueda y navegacion

- **busqueda global** (`ctrl+shift+f`): busca en paralelo en notas, tarjetas kanban, tareas polar y recordatorios.
- **quick switcher** (`ctrl+p`): salto rapido a cualquier nota.
- **paleta de comandos** (`ctrl+shift+k`): ejecutar acciones por nombre - navegar a secciones, crear nota, alternar tema, zoom, sincronizar polar, etc.
- **atajos de teclado** generales y por modulo.

### 2.2 🔔 centro de notificaciones

- log persistente de eventos del sistema con severidades (info, success, warning, error).
- toasts de escritorio y sonido opcional.
- silenciamiento por severidad y horario **"no molestar"** (se conserva el registro aunque se silencie la interrupcion).
- bandeja de **vencimientos** con badge en la barra lateral.

### 2.3 🗑️ papelera transversal

- recuperacion de **notas, tarjetas kanban, archivos y recordatorios** desde una sola pantalla.
- restaurar individualmente (las tarjetas kanban vuelven a su columna original o la primera disponible).
- vaciar por seccion o todo, con confirmacion obligatoria.
- badge con el total de elementos en la navegacion.

### 2.4 📂 archivos y documentos

- gestion de archivos no-markdown dentro del vault (importar, arrastrar, renombrar, mover, eliminar a papelera).
- **vista previa en la app**: imagenes y pdfs.
- "abrir con aplicacion del sistema".

### 2.5 🛠️ ia skills (plantillas de prompts)

- seccion de skills/plantillas markdown con frontmatter (nombre, descripcion, emoji, color, herramientas, tags, permisos).
- importar, exportar, duplicar y editar skills.
- catalogo externo de la comunidad (enlace).
- *nota*: son plantillas de prompt estaticas para usar con un asistente de ia externo - enki **no** incluye ni se conecta a ningun motor de ia por si misma.

### 2.6 🧪 habitos y estadisticas

- **habitos**: rejilla de hecho/no-hecho/no aplica, sin rachas punitivas (un dia perdido no "resetea" nada).
- **estadisticas**: actividad de notas creadas/editadas, tareas completadas y grafico de consistencia de habitos.

### 2.7 🎨 personalizacion

- **tema claro/oscuro** (oscuro por defecto) y **temas personalizados** con editor propio, selector de color, duplicar y exportar.
- **zoom de interfaz** (50%-200%) por vault con atajos `ctrl++` / `ctrl+-` / `ctrl+0`.
- **fuente de interfaz y del editor** (space grotesk por defecto, fuente del sistema opcional).
- **ajustes del editor**: tamano de fuente, ancho de columna, numeros de linea, word wrap, tamano de tab, etc.
- **barra lateral personalizable**: mostrar/ocultar cada seccion de forma independiente.
- **exportar/importar ajustes** en json y **backup zip** completo del vault.

### 2.8 🌍 internacionalizacion

- **6 idiomas**: ingles (us y gb), espanol (espana y latinoamerica), aleman y frances.
- selector de idioma en el primer arranque y en ajustes; cambio de idioma en caliente.
- plurales, tiempos relativos y fechas segun la cultura del idioma elegido.

### 2.9 ✍️ herramientas del editor (extras)

- **modo enfoque/zen** (`ctrl+shift+enter`): oculta todo menos el cuerpo de la nota, centrado.
- **exportar nota a html** (documento autonomo) e **imprimir / pdf** con vista de impresion integrada.
- **buscar/reemplazar** dentro de la nota.
- **comandos de barra oblicua** (`/`) para insertar elementos (tabla, etc.).
- **mostrar en el sistema**: revelar la nota en el explorador de archivos del so.

### 2.10 ⚙️ ajustes generales

- pagina de inicio configurable, confirmacion antes de borrar, sonido, panel de recordatorios flotante, opciones de calendario (inicio de semana, fines de semana, horario laboral), gestion de la cuenta polar (vincular/desvincular, sincronizacion manual) y backup automatico con intervalo configurable.

---

## 3. detalles del proyecto

### 3.1 tecnologia

| aspecto | detalle |
|---|---|
| plataforma | escritorio multiplataforma: windows, linux, macos |
| ui framework | avalonia ui 11 (xaml `.axaml`) |
| lenguaje / runtime | c# / .net 8 |
| patron | mvvm estricto con communitytoolkit.mvvm |
| inyeccion de dependencias | ninguna; composicion manual de servicios |
| persistencia | json en `%appdata%/enki/` (linux: `~/.config/enki/`) y archivos markdown en el vault |
| sincronizacion en la nube | supabase (solo para polar, opcional) |
| iconos | material symbols outlined |
| fuentes | space grotesk (interfaz) y jetbrains mono (monoespaciada) |

### 3.2 estructura de datos en disco

**global (`%appdata%/enki/`)**:
- `settings.json` - ajustes de la aplicacion
- `kanban.json` - tablero kanban
- `reminders.json` - recordatorios
- `notifications.json` - centro de notificaciones (max. 50 entradas)
- `polar-tasks.json` - cache local de tareas polar
- `dismissed-reminders.json` - recordatorios descartados

**por vault (`<vault>/`)**:
- `**/*.md` - las notas, como archivos markdown normales
- `.enki/settings.json` - ajustes por vault
- `.enki/skills/*.md` - skills
- `.sessions/<nombre>/.session.json` - manifiestos de sesiones (y cifrado si estan protegidas)
- `.trash/` - elementos borrados (papelera)

### 3.3 integraciones entre modulos

- notas ↔ kanban: tarjetas vinculadas a notas; renombrar una nota actualiza las tarjetas.
- kanban/polar/recordatorios → calendario: todo se agrega en una vista unificada.
- recordatorios → notificaciones: los vencidos se convierten en avisos del sistema.
- recordatorios ↔ polar: se sincronizan como una tabla mas de supabase.
- sesiones → notas: workspace limitado a la sesion.
- dashboard ← todos los modulos (resumen de hoy, recientes, actividad).
- papelera ← notas, kanban, archivos, recordatorios.

### 3.4 calidad y estado

- **tests**: suite xunit con cientos de casos (servicios, viewmodels, localizacion, renderizado) + tests de ui headless.
- **ci**: build + tests en cada push/pr a `main` (github actions).
- **analisis estatico**: warnings tratados como errores; analizadores de .net en el nivel mas reciente.
- **estado**: en desarrollo activo, repositorio privado de uso personal; sin instalador publicado aun.
- **licencia**: todos los derechos reservados.

### 3.5 detalles curiosos / decisiones de producto

- **cero border-radius**: todas las esquinas son rectas salvo indicadores circulares (puntos de estado, thumb de toggles). es la senal de identidad mas fuerte del diseno.
- **monocromo deliberado**: no hay color de marca; el unico color real es rojo (error) y verde (exito/estado activo). los temas personalizados pueden sobrescribir cualquier token.
- **habitos sin rachas**: el diseno responde por construccion a "¿un dia perdido resetea injustamente?" - no hay racha que resetear, solo una rejilla plana.
- **tus notas son solo archivos**: la promesa central es que puedes abrir cualquier nota con cualquier editor; por eso la escritura en disco es atomica (una nota nunca queda a medias).
- **polar no es obligatorio**: toda la arquitectura esta pensada para que la nube sea un anadido y nunca un requisito.
- **el grafo de sesiones** se renderiza en un webview embebido, pero el resto de la app es 100% nativa.
