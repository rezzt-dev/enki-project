# enki - informacion general

> **que es enki?** una aplicacion de escritorio personal que reune notas, tareas, calendario y recordatorios en un solo lugar. su lema: *"tu segundo cerebro, en tu escritorio."*

---

## 1. finalidad

enki nacio de una frustracion concreta: para saber que hacer hoy habia que abrir cinco aplicaciones distintas - las notas en una, las tareas en otra, el calendario en una tercera y los recordatorios en una cuarta. enki junta todo eso en una sola ventana, **pero sin mezclarlo a la fuerza**: cada cosa conserva su lugar y es el usuario quien decide como conectarlas.

> el objetivo no es que uses enki todo el dia. es que, cuando lo abras, sepas exactamente donde esta lo que necesitas.

esta pensado para **una sola persona**: tu. no es una suite colaborativa, ni un saas, ni una herramienta que te obligue a pensar como piensa su equipo de producto. es un espacio privado.

---

## 2. como funciona (en terminos sencillos)

enki funciona alrededor de un **vault**: una carpeta normal de tu disco que tu eliges y que contiene tus notas como archivos markdown ordinarios. a partir de ahi, la aplicacion te da herramientas para organizar y conectar esa informacion:

- **notas** que viven en tu disco como archivos `.md` normales, con carpetas, etiquetas (`#tags`) y enlaces entre ellas (`[[wikilinks]]`).
- **sesiones**: subespacios de notas para proyectos aislados (una tesis, un viaje, un producto), cada uno con su propio grafo visual de relaciones. se pueden proteger con contrasena, que cifra el contenido de las notas y sus adjuntos (los nombres de archivo y la estructura siguen visibles en disco).
- **un tablero kanban** local de tareas con columnas, prioridades, fechas limite y subtareas.
- **polar**: tareas que se sincronizan en la nube y se comparten con la app android del mismo nombre. es **opcional** - si no lo vinculas, enki funciona igual con todo lo demas.
- **un calendario** unificado que reune las tareas del kanban, las tareas de polar y los recordatorios en una sola vista.
- **recordatorios** independientes con fecha y hora que avisan cuando toca.
- un **dashboard** que resume el dia al abrir la aplicacion.

toda la informacion esta **conectada pero no amontonada**: una nota puede estar vinculada a una tarjeta kanban, una tarea se puede arrastrar en el calendario para cambiar su fecha, un recordatorio aparece en la bandeja de "hoy".

### datos y privacidad

- **tus datos son tuyos.** las notas son archivos markdown en una carpeta que tu eliges. puedes abrirlas con cualquier editor, respaldarlas con cualquier herramienta y llevarlas contigo a donde quieras. no hay formatos propietarios ni bloqueos.
- **funciona sin internet.** el nucleo de enki (notas, kanban, recordatorios, calendario) funciona completamente offline. la nube (polar) es un extra opcional.
- **sin cuentas obligatorias.** no necesitas registrarte en nada para usarla.

---

## 3. filosofia de producto

- **tus datos son tuyos** - archivos abiertos, sin dependencia de un servicio.
- **conectado, no amontonado** - cada modulo funciona solo, pero gana sentido al cruzarse con los demas.
- **sin distracciones** - interfaz monocroma, sin gamificacion, sin notificaciones constantes, sin presion por productividad.
- **funciona sin internet** - la nube es opcional, nunca un requisito.
- **adaptable a ti, no al reves** - ajustes granulares para personalizar la interfaz: idioma, tema claro/oscuro, zoom, fuentes, que secciones ver y cuales ocultar.

---

## 4. para quien es?

para cualquiera que quiera un espacio privado donde guardar ideas, tareas, proyectos y recordatorios de forma que todo este conectado, todo este a mano y nada se pierda. especialmente util para:

- gestionar **notas de conocimiento** (un "segundo cerebro" tipo zettelkasten u obsidian, pero en el escritorio).
- organizar **proyectos personales** con sesiones aisladas y sus grafos de relaciones.
- llevar el **dia a dia**: tareas, fechas limite, recordatorios y un calendario unificado.
- trabajar **sin conexion** o con privacidad total, sin depender de servicios en la nube.

---

## 5. plataformas y estado del proyecto

- **plataformas**: escritorio multiplataforma - windows, linux y macos.
- **idiomas de interfaz**: 6 - ingles (us y gb), espanol (espana y latinoamerica), aleman y frances.
- **estado**: 🚧 **en desarrollo activo.** el repositorio es privado y de uso personal por ahora: no hay instalador publicado ni soporte para terceros. la aplicacion ya es funcional para uso diario (notas, kanban, calendario, recordatorios, busqueda y papelera operativos); la sincronizacion con polar requiere credenciales propias de supabase y esta pensada solo para el entorno del autor.
- **licencia**: todos los derechos reservados.

---

## 6. primeros pasos

la primera vez que arranca, enki te pide elegir dos cosas:

1. **idioma** de la interfaz.
2. **carpeta del vault** - crear uno nuevo (la app lo siembra con una nota de bienvenida y un tablero inicial) o abrir uno existente.

a partir de ahi, todo lo que crees se guarda en esa carpeta. para usar la sincronizacion con polar hace falta anadir unas credenciales de supabase en un archivo de configuracion local (`local.properties`); **sin ese archivo, enki sigue funcionando perfectamente en modo local**.

---

## 7. documentacion relacionada

- **funcionalidades**: ver [`features-document.md`](features-document.md)
- **diseno y aspecto visual**: ver [`project-ux-design.md`](project-ux-design.md)
- **historial de cambios**: `CHANGELOG.md`
- **analisis tecnico y roadmap**: `docs/agent-docs/`
