# ROADMAP DE PUBLICACION Y MEJORA CONTINUA

> lectura de producto para orientar las siguientes fases de Enki una vez que el
> nucleo principal ya esta consolidado. este documento no sustituye al roadmap
> historico ni al inventario funcional: resume que falta para que el proyecto
> sea publicable, mas comodo de usar a diario y preparado para crecer sin meter
> ruido.
>
> se aplica cuando se prioricen cambios de producto, se prepare una version
> candidata o se decida que bloque de trabajo abordar antes de abrir una nueva
> fase de desarrollo.

---

## PRINCIPIO GENERAL

Enki ya no esta verde: notas, Kanban, Polar, calendario, recordatorios,
archivos, papelera, dashboard, habitos, estadisticas, temas, tests, i18n, sync
y persistencia tienen muchas capas de pulido. el siguiente salto no deberia ser
anadir otro modulo enorme por defecto, sino cerrar tres frentes: producto
publicable, captura y conocimiento mas fluidos, y garantias manuales que los
tests no pueden cubrir solos.

el criterio de priorizacion es simple: menos funcionalidades aisladas y mas
comodidad diaria inevitable. plantillas, historial, captura rapida, tags
globales, grafo global y release pulido tienen mas valor que abrir otra gran
superficie de producto sin cerrar lo existente.

---

## 1. PRIORIDAD UNO: PRODUCTO PUBLICABLE

### 1.1 arreglar la documentacion visible

- alinear `README.md` con la documentacion real del checkout, porque actualmente apunta a `docs/agent-docs/...` mientras la documentacion viva esta en `.docs/project-info/...`.
- evitar que una persona que entre al repositorio encuentre enlaces rotos o rutas prometidas que no existen.
- decidir si la documentacion publica debe moverse a `docs/`, si el README debe apuntar a `.docs/`, o si se mantiene una capa publica reducida y una capa interna en `.docs/`.

### 1.2 empaquetado real por sistema

- pasar de la documentacion de `dotnet publish` a paquetes instalables reales para Windows, Linux y macOS.
- completar iconos por plataforma, integracion `.desktop` en Linux, paquete `.app` o `.dmg` en macOS y MSI, Inno Setup o MSIX en Windows.
- documentar el flujo minimo de release para que compilar, empaquetar y publicar no dependa de memoria manual.

### 1.3 qa manual documentada

- crear una checklist de release candidate con las verificaciones que los tests automaticos no cubren bien.
- comprobar lector de pantalla real, render visual de wikilinks y links rotos, reconexion real de Polar Realtime y selector de recurrencia.
- ejecutar la checklist antes de pensar en una version `0.2.0` o equivalente.

### 1.4 credenciales Polar en almacen seguro

- sustituir el almacenamiento temporal de tokens en `settings.json` por un almacen seguro por plataforma.
- evaluar e implementar Secret Service o `libsecret` en Linux, DPAPI en Windows y Keychain en macOS.
- mantener la redaccion de backups y exportaciones mientras exista cualquier dato sensible persistido fuera del almacen seguro.

---

## 2. PRIORIDAD DOS: SEGUNDO CEREBRO SIN RUIDO

### 2.1 plantillas de notas

- crear una carpeta `.enki/templates/` dentro del vault para plantillas Markdown reutilizables.
- soportar variables sencillas como `{{date}}`, `{{title}}`, `{{time}}` o equivalentes que encajen con el editor actual.
- permitir una plantilla especifica para la nota diaria y un flujo de nueva nota desde plantilla.

### 2.2 historial local de versiones por nota

- anadir snapshots locales por nota para que el autoguardado no convierta un error rapido en una perdida permanente.
- guardar versiones en una zona interna del vault, permitir comparar diferencias y restaurar una version anterior.
- crear un snapshot antes de restaurar o ejecutar operaciones masivas para que la recuperacion nunca sea destructiva.

### 2.3 gestor global de etiquetas

- agregar tags de notas, Kanban y Polar en una vista unica de mantenimiento del vault.
- permitir renombrar, fusionar y limpiar etiquetas duplicadas o mal escritas en todos los modulos que las usan.
- reutilizar las entidades reales existentes sin crear un segundo almacen de tags.

### 2.4 notas relacionadas

- sugerir notas relacionadas sin IA a partir de tags compartidos, backlinks y wikilinks comunes.
- exponer la relacion como ayuda contextual del editor, no como otra bandeja que el usuario tenga que mantener.
- priorizar relaciones explicables antes que una busqueda semantica opaca.

### 2.5 grafo global del vault

- extender el grafo existente hacia una vista global del vault filtrable por sesion y por tag.
- reutilizar el renderizador y la agregacion ya disponibles en vez de construir un sistema paralelo.
- usar el grafo para descubrir notas huerfanas, conexiones cruzadas y clusters de conocimiento.

---

## 3. PRIORIDAD TRES: CAPTURA RAPIDA

### 3.1 pegar capturas e imagenes en el editor

- soportar `Ctrl+V` desde el portapapeles cuando haya una imagen disponible.
- guardar la imagen como adjunto del vault e insertar automaticamente el Markdown correspondiente en la nota activa.
- mantener nombres de archivo estables y compatibles con el sistema actual de adjuntos e imagenes.

### 3.2 notas diarias mejoradas

- anadir navegacion de dia anterior y dia siguiente para notas diarias.
- combinar la nota diaria con su plantilla propia.
- mostrar una vista discreta de constancia que conecte bien con habitos y estadisticas sin convertirlo en gamificacion.

### 3.3 web clipper simple

- empezar por el flujo `pegar URL -> crear nota` con titulo, enlace de origen y texto principal cuando se pueda extraer.
- guardar un fallback util con titulo y enlace cuando el sitio bloquee la extraccion o requiera sesion.
- aplazar una extension de navegador hasta que el flujo de escritorio sea solido.

### 3.4 captura rapida movil via Polar

- evaluar una tabla `quick_captures` en Polar para que la app Android pueda enviar capturas cortas al escritorio.
- convertir esas capturas en notas dentro de `Inbox/` al sincronizar.
- mantener el patron de integracion existente con Polar sin duplicar datos innecesarios.

---

## 4. PRIORIDAD CUATRO: IA ACOTADA

### 4.1 ajustes de IA desactivados por defecto

- crear una seccion de ajustes de IA que nazca apagada y sea explicitamente opt-in.
- evitar que el producto dependa de IA para flujos basicos de notas, tareas o calendario.

### 4.2 proveedor configurable

- permitir clave propia del usuario o endpoint local tipo Ollama.
- documentar claramente que proveedor se usa y que datos saldran del dispositivo.

### 4.3 acciones concretas antes que chat infinito

- empezar por acciones pequenas: resumir nota, sugerir tags, convertir seleccion en subtareas y ejecutar una Skill sobre la nota activa.
- mostrar siempre el contexto que se enviara antes de ejecutar la accion.
- aplazar embeddings y busqueda semantica hasta que la ejecucion de Skills con contexto elegido este bien resuelta.

---

## 5. ROADMAP RECOMENDADO

### fase a: saneamiento de release

- enlazar correctamente la documentacion visible desde `README.md`.
- crear y pasar una checklist de qa manual para release candidate.
- definir y construir el empaquetado minimo por plataforma.
- evaluar e implementar el almacenamiento seguro de credenciales Polar.

### fase b: seguridad de contenido

- implementar historial local de versiones por nota.
- permitir restaurar versiones anteriores desde la interfaz.
- crear snapshots antes de restauraciones y operaciones masivas.

### fase c: captura y escritura

- implementar plantillas de notas y plantilla diaria.
- mejorar la navegacion de notas diarias.
- permitir pegar imagenes desde el portapapeles en el editor.
- cerrar o completar el modo zen si todavia no esta acabado.

### fase d: organizacion del vault

- implementar gestor global de etiquetas.
- anadir notas relacionadas por tags, backlinks y wikilinks.
- exponer el grafo global del vault con filtros utiles.

### fase e: IA opcional

- anadir proveedor configurable y desactivado por defecto.
- ejecutar Skills con contexto elegido por el usuario.
- ofrecer acciones pequenas sobre nota, tarjeta o seleccion antes de introducir chat o embeddings.

---

## RESUMEN

Enki ya tiene cuerpo. lo que falta ahora es que usarlo todos los dias sea
inevitablemente comodo: documentacion visible correcta, empaquetado serio,
checklist de release candidate, credenciales seguras, plantillas, historial,
captura rapida, tags globales, notas relacionadas, grafo global e IA opcional
solo cuando aporte valor claro.
