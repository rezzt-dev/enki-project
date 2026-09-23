# Propuesta de nuevas funcionalidades — Enki

> **Alcance**: a diferencia de [`inventario-funcionalidades-y-mejora-continua.md`](inventario-funcionalidades-y-mejora-continua.md) y de [`roadmap.md`](analisis-proyecto/roadmap.md) — que documentan explícitamente **solo** lo que ya existe y cómo pulirlo, sin proponer nada nuevo — este documento hace lo contrario: propone **funcionalidad genuinamente nueva** que Enki no tiene hoy, pensada para que el producto se sienta más completo sin traicionar su filosofía (local-first, privado, monocromo, sin gamificación, sin distracciones).
>
> **Cómo se organiza cada propuesta**: toda funcionalidad principal se descompone en sub-funcionalidades y detalles concretos, y se analiza desde cuatro ángulos:
> - **Usuario nuevo** — alguien que acaba de instalar Enki y ve la app por primera vez.
> - **Usuario habitual (uso al 50 %)** — alguien que ya usa Notas y Kanban a diario, pero no ha tocado Sesiones, Polar o Ajustes avanzados.
> - **Usuario avanzado (uso pleno)** — alguien que exprime todos los módulos y cruza datos entre ellos constantemente.
> - **Desarrollador** — qué tan bien encaja con la arquitectura actual (MVVM manual, sin DI, persistencia JSON/Markdown, Avalonia UI) y qué complejidad implica.
>
> **Filtro aplicado**: se descartó deliberadamente todo lo que choque con la filosofía del proyecto (gamificación agresiva, telemetría, dependencia obligatoria de la nube, IA forzada). Cada propuesta que toca esas líneas se marca como **opcional y desactivada por defecto**.

---

## 1. Resumen por puntos

### A. Núcleo de conocimiento (Notas)
1. Plantillas de notas con variables (`{{date}}`, `{{title}}`, `{{time}}`).
2. Transclusión / embeber contenido de otra nota (`![[Nota#Sección]]`).
3. Historial de versiones locales por nota (snapshots automáticos, diff, revertir).
4. Gestor global de etiquetas (ver, renombrar, fusionar, eliminar tags en todo el vault).
5. Grafo global del vault (no solo por sesión).
6. Exportar notas a PDF/HTML y vista de impresión.
7. Modo enfoque/zen en el editor.
8. Notas diarias mejoradas: navegación anterior/siguiente, plantilla propia, calendario de constancia tipo "heatmap" discreto.
9. Web clipper: pegar una URL y guardarla como nota Markdown limpia.

### B. Asistencia inteligente (dar vida real a "Skills")
10. Motor de IA opcional, "trae tu propia clave" (BYO-key) o modelo local, para resumir, preguntar sobre el vault y sugerir enlaces.
11. Búsqueda semántica local (embeddings) como capa adicional a la búsqueda por texto.
12. Panel de "notas relacionadas" por similitud de contenido/tags.

### C. Productividad y seguimiento personal
13. Seguimiento de hábitos minimalista (sin rachas agresivas ni gamificación).
14. Registro de tiempo (Pomodoro/cronómetro) vinculado a tarjetas Kanban y tareas Polar.
15. Página de "Estadísticas" / insights personales (notas escritas, tareas completadas, tendencias).
16. Diario breve integrado en la nota diaria (estado de ánimo opcional, texto libre).

### D. Organización estructurada
17. Notas "tabla" con propiedades tipadas (mini base de datos por nota, al estilo Obsidian Bases).

### E. Continuidad y multi-dispositivo
18. Sincronización opcional de notas (no solo tareas) reutilizando el backend de Polar, con cifrado extremo a extremo opcional.
19. Multi-vault: cambiar de vault sin reiniciar, lista de "vaults recientes".
20. Captura rápida desde la app Android de Polar (una nota corta que aparece en el vault al sincronizar).

### F. Archivos y adjuntos
21. Notas de voz adjuntas a una nota o recordatorio, con transcripción local opcional.
22. Pegar capturas de pantalla/imágenes directamente en el editor como adjunto versionado.

### G. Interacción y flujo de trabajo
24. Paleta de comandos unificada (`Ctrl+K`) para ejecutar acciones, no solo navegar.
25. Automatizaciones simples tipo "cuando ocurra X en el módulo A, haz Y en el módulo B".
26. Dashboard configurable por el usuario (elegir y ordenar qué tarjetas se muestran).
27. Exportar/imprimir el Calendario o el tablero Kanban como imagen o PDF.

### H. Plataforma y personalización
28. Atajos de teclado remapeables por el usuario.
29. Sistema de plugins/extensiones a futuro (sandbox simple sobre las Skills existentes).

---

## 2. Detalle de cada propuesta

---

### A. Núcleo de conocimiento (Notas)

#### 1. Plantillas de notas

Enki ya siembra una nota de bienvenida y una nota diaria de nombre fijo (`yyyy-MM-dd.md`), pero no existe ningún concepto de plantilla reutilizable. Un "segundo cerebro" vive y muere de la fricción para capturar: si cada reunión, cada nota de proyecto o cada entrada diaria exige escribir la misma estructura a mano, el usuario deja de capturar.

**Sub-funcionalidades:**
- Carpeta `.enki/templates/` (paralela a `.enki/skills/`) con archivos `.md` que son la plantilla.
- Variables sustituidas al crear: `{{date}}`, `{{time}}`, `{{title}}`, `{{weekday}}`.
- Comando "Nueva nota desde plantilla" en el grid de Notas, junto al botón "+" ya existente.
- Plantilla asignable por defecto a la nota diaria, configurable en Ajustes › Editor.
- Plantillas locales al vault (no globales), coherente con que todo el contenido vive en el vault.

**Usuario nuevo**: probablemente ni la note al principio; seguirá usando "Nueva nota" normal. Pero si la semilla de bienvenida incluye un ejemplo de plantilla, la descubre antes.

**Usuario habitual (50 %)**: es quien más la agradece — ya escribe notas de reunión o notas de proyecto con una estructura repetida a mano; una plantilla le ahorra ese copia-pega mental cada vez.

**Usuario avanzado**: tendrá varias plantillas (reunión, proyecto, revisión semanal, nota diaria) y las combinará con tags y wikilinks para que cada nota nueva ya nazca conectada al resto del vault.

**Desarrollador**: encaja de forma natural con el patrón ya usado para Skills (`SkillService` lee `.md` con contenido; aquí no hace falta ni frontmatter YAML, solo sustitución de variables por `string.Replace`/regex). Complejidad **baja**; no toca ningún servicio existente, solo añade `TemplateService` y un diálogo de selección.

---

#### 2. Transclusión / embeber contenido de otra nota

Los wikilinks (`[[Nota]]`) ya conectan notas, pero solo como referencia navegable. La transclusión (`![[Nota#Sección]]`) permite que el **contenido** de una nota (o de una sección concreta) se renderice dentro de otra, sin duplicarlo.

**Sub-funcionalidades:**
- Sintaxis `![[Nota]]` embebe la nota completa; `![[Nota#Encabezado]]` embebe solo esa sección.
- Renderizado en el live preview ya existente (`MarkdownLivePreviewColorizer`), como un bloque con borde sutil y la nota origen indicada.
- Click en el bloque embebido abre la nota origen (mismo comportamiento que un wikilink normal).
- Ciclos (nota A embebe a B, B embebe a A) detectados y mostrados como error visual, no como bucle infinito.

**Usuario nuevo**: no lo necesita de entrada; es una funcionalidad de usuario que ya organiza mucho contenido.

**Usuario habitual (50 %)**: probablemente no la usa aún porque no ha topado con el problema de "quiero repetir este fragmento en dos notas sin mantenerlo dos veces".

**Usuario avanzado**: la usa constantemente para notas "índice" o "dashboard personal" que agregan fragmentos de varias notas (ej. una nota de "Proyecto X" que embebe la sección de tareas pendientes de tres notas de reunión distintas).

**Desarrollador**: el parser de wikilinks ya existe (`WikiLinkParser`); esta funcionalidad extiende ese mismo parser para reconocer el prefijo `!` y añade un paso de resolución de sección (buscar el encabezado Markdown correspondiente). El renderizado embebido en el editor de texto plano es la parte más delicada — probablemente se implemente primero solo para el hipotético panel de preview renderizado completo (ver punto de "vista previa" ya cerrado como "no se va a construir" en el roadmap) o, más realista, como un bloque colapsable en el propio editor vía el `MarkdownBackgroundRenderer` existente. Complejidad **media-alta**.

---

#### 3. Historial de versiones locales por nota

Las notas son archivos Markdown en disco: si el usuario borra un párrafo por error y autoguarda, no hay vuelta atrás salvo deshacer en la misma sesión del editor. Un historial de versiones locales (no un control de versiones completo tipo git) da red de seguridad sin salir del espíritu "tus datos son tuyos, son archivos planos".

**Sub-funcionalidades:**
- Snapshot automático cada N minutos de edición activa o al cerrar la pestaña, guardado en `<vault>/.enki/history/<hash-de-ruta>/<timestamp>.md`.
- Límite configurable de snapshots por nota (ej. últimos 20) con purga automática de los más antiguos.
- Vista de "Historial" en el panel lateral del editor (junto a Outline/Backlinks/Stats ya existentes) con diff línea a línea entre dos versiones.
- Botón "Restaurar esta versión" que reemplaza el contenido actual (creando a su vez un snapshot del estado previo a restaurar, para que restaurar nunca sea destructivo).

**Usuario nuevo**: no la ve hasta que la necesita — es puramente una red de seguridad silenciosa.

**Usuario habitual (50 %)**: es el perfil que más se beneficia sin saberlo: quien reescribe notas largas y en algún momento "se carga" un párrafo bueno sin querer.

**Usuario avanzado**: la usa activamente para ver la evolución de una nota de proyecto a lo largo de semanas, no solo como red de seguridad ante errores.

**Desarrollador**: sigue el mismo patrón de "carpeta oculta por vault" que ya usan Sesiones (`.sessions/`) y Skills (`.enki/skills/`). El reto real es no duplicar el trabajo que ya hace el autoguardado con debounce de 600 ms — el snapshot debe ser mucho menos frecuente (minutos, no segundos) para no llenar el disco. Complejidad **media**; el diff visual es la parte más costosa de construir bien.

---

#### 4. Gestor global de etiquetas

Hoy los tags existen en notas (frontmatter YAML), tarjetas Kanban y tareas Polar, cada uno con su propio filtro local (`TagFilterViewModel`), pero no hay ningún sitio para ver **todas** las etiquetas del vault de un vistazo, renombrarlas o fusionar dos que quedaron duplicadas por error de tipeo (`proyecto` vs `proyectos`).

**Sub-funcionalidades:**
- Página o panel "Etiquetas" con listado de todos los tags usados, conteo de notas/tarjetas por tag, y en qué módulos aparece cada uno.
- Renombrar un tag en todos los sitios donde aparece (notas, Kanban, Polar) en una sola operación.
- Fusionar dos tags en uno.
- Eliminar un tag de todos los ítems que lo tengan, con confirmación.
- Click en un tag navega a una vista filtrada (reutilizando el `TagFilterViewModel` ya existente).

**Usuario nuevo**: no lo necesita todavía — sus tags son pocos y coherentes.

**Usuario habitual (50 %)**: empieza a notar tags duplicados por error de escritura pero no tiene forma de arreglarlo salvo editar nota por nota.

**Usuario avanzado**: con decenas o cientos de notas, esta pantalla es la diferencia entre un sistema de tags que sigue siendo útil y uno que se vuelve ruido.

**Desarrollador**: no requiere nuevo almacenamiento — es una vista de agregación pura sobre datos que ya existen (similar en espíritu a `CalendarAggregationService`, que lee de varios módulos sin duplicar el store). Complejidad **media**: lo delicado es la operación de renombrado atómica cruzando `VaultService` (reescribir frontmatter de notas), `KanbanService` y `PolarTaskService` a la vez, con manejo de fallos parciales.

---

#### 5. Grafo global del vault

El grafo visual actual (`SessionGraphRenderer`) solo muestra relaciones **dentro de una sesión**. Un "segundo cerebro" también se beneficia de ver cómo se conecta todo el vault, sesiones aparte — ya existe `LoadCombinedGraphAsync` para el dashboard, que cuenta nodos sin renderizar, así que la base de agregación ya está.

**Sub-funcionalidades:**
- Vista "Grafo" accesible desde Notas o como página propia, mostrando wikilinks entre todas las notas del vault, coloreadas por sesión o por tag.
- Mismo motor de renderizado ya evaluado y mantenido (sigma.js + d3-force sobre WebView), reutilizado en vez de reconstruido.
- Filtro para aislar solo notas sin sesión, o cruzar dos sesiones a la vez.

**Usuario nuevo**: probablemente confuso con un grafo vacío o casi vacío — debe presentarse solo cuando hay contenido suficiente (ej. >5 notas enlazadas), igual que ya se cuida el "empty state" en otras pantallas.

**Usuario habitual (50 %)**: es quien organiza todo en sesiones y nunca ha visto el grafo combinado; puede sorprenderle descubrir conexiones que no sabía que existían entre notas de sesiones distintas.

**Usuario avanzado**: lo usa como herramienta de descubrimiento — encontrar notas huérfanas (sin ningún enlace) es tan valioso como ver las muy conectadas.

**Desarrollador**: la infraestructura de renderizado y el conteo agregado ya existen; "solo" falta exponerlo como vista interactiva reutilizando `SessionGraphRenderer` con un dataset más grande. El roadmap ya documentó que reemplazar WebView por un renderizador nativo no compensa, así que esta propuesta hereda esa decisión. Complejidad **media**; el mayor riesgo es de rendimiento con vaults grandes (cientos de notas), a validar con el mismo cuidado que ya se aplicó al loader de sesión.

---

#### 6. Exportar notas a PDF/HTML y vista de impresión

Las notas viven como Markdown plano por diseño, pero en algún momento el usuario querrá compartir o imprimir una nota fuera de Enki (una receta, un resumen, apuntes de estudio) sin que el receptor tenga que instalar un editor Markdown.

**Sub-funcionalidades:**
- "Exportar como PDF" y "Exportar como HTML" desde el menú contextual de una nota o desde el editor.
- Render usando el mismo parser Markdown que ya existe para el live preview, aplicado a un documento estático en vez de al editor interactivo.
- Plantilla de exportación simple y monocroma, coherente con el sistema de diseño de la app.
- Opción de incluir o no el frontmatter/tags en la exportación.

**Usuario nuevo**: no la usa de entrada, pero es la típica funcionalidad que un usuario nuevo busca en los primeros días ("¿puedo sacar esto de aquí?") y cuya ausencia genera desconfianza incluso si nunca la llega a usar.

**Usuario habitual (50 %)**: la usa puntualmente para compartir una nota concreta con alguien que no usa Enki.

**Usuario avanzado**: exporta lotes de notas (ej. todas las de una sesión) para archivarlas fuera del vault o entregarlas como documento.

**Desarrollador**: ya existe infraestructura de renderizado PDF en el módulo de Archivos (`PdfDocumentViewModel`, `PdfRenderHelper`) aunque orientada a *visualizar* PDFs existentes, no a *generarlos*; para generar PDF desde Markdown hace falta una librería de render HTML→PDF (ej. a través de un motor headless) o construir el documento con primitivas de impresión de Avalonia. Complejidad **media** para HTML (es casi gratis, reutilizando el parser existente), **media-alta** para PDF si se evita una dependencia pesada.

---

#### 7. Modo enfoque / zen en el editor

El editor ya tiene un panel lateral colapsable y una toolbar con grupos colapsables (Fase 4 del roadmap), pero no existe un modo que oculte **todo** lo demás (sidebar, pestañas, toolbar) para escribir sin distracción — coherente con la filosofía "sin distracciones" que la propia app declara pero que hoy solo aplica al no-tener-notificaciones-constantes, no al layout de la ventana.

**Sub-funcionalidades:**
- Atajo dedicado (ej. `Ctrl+Shift+Enter`) que colapsa sidebar, pestañas y toolbar, dejando solo el texto centrado con márgenes cómodos.
- Salir con `Esc` o el mismo atajo.
- Opción de atenuar (dim) todo el texto salvo el párrafo activo (typewriter/paragraph focus), como mejora opcional posterior.

**Usuario nuevo**: lo descubre tarde, pero cuando lo hace refuerza justo la promesa de "app sin distracciones" que leyó en el README.

**Usuario habitual (50 %)**: lo usa para sesiones de escritura largas (una nota de proyecto extensa, redactar un documento).

**Usuario avanzado**: probablemente lo combina con el modo enfoque + plantillas + historial de versiones para sesiones de escritura serias.

**Desarrollador**: complejidad **baja**; es esencialmente ocultar/mostrar controles ya existentes vía bindings, sin nuevo estado persistente más allá de un booleano transitorio en `EditorViewModel`.

---

#### 8. Notas diarias mejoradas

Hoy la nota diaria es una sola pieza: nombre fijo `yyyy-MM-dd.md`, sin más estructura alrededor. El concepto de "diario" se beneficia de navegación temporal y de una vista de constancia, sin caer en gamificación (nada de rachas con fuego ni puntos).

**Sub-funcionalidades:**
- Botones "día anterior" / "día siguiente" dentro del editor cuando la nota abierta es una nota diaria, creando la del día siguiente si no existe.
- Plantilla propia para la nota diaria (usa la funcionalidad de plantillas del punto 1).
- Vista de calendario de constancia discreta (un cuadrito por día, con/sin nota, sin colores llamativos — coherente con la paleta monocroma) accesible desde el módulo Notas.

**Usuario nuevo**: puede no journalear todavía, pero ver el cuadrito de constancia desde el primer día le da una señal clara de "esto es para usar cada día", sin presión (no hay racha que "romper").

**Usuario habitual (50 %)**: es exactamente el perfil que escribe notas diarias de vez en cuando y agradece poder navegar entre ellas sin ir al buscador cada vez.

**Usuario avanzado**: usa la nota diaria como columna vertebral de su segundo cerebro, enlazando desde ahí a Kanban y notas de proyecto; la navegación día a día es central en su flujo.

**Desarrollador**: complejidad **baja-media**; reutiliza `VaultService` para resolver/crear el archivo del día adyacente, y el "heatmap" es una agregación de fechas de archivo ya disponibles (`NoteFile` incluye fecha), no requiere nuevo modelo de datos.

---

#### 9. Web clipper

Enki ya importa archivos Markdown; falta el flujo inverso más común de un segundo cerebro: guardar contenido de la web como nota. No hace falta una extensión de navegador completa para empezar — un simple "pegar URL → Enki descarga y limpia el artículo" cubre el 80 % del caso de uso.

**Sub-funcionalidades:**
- Comando "Nueva nota desde URL" que descarga el HTML, extrae el contenido principal (heurística tipo "readability") y lo convierte a Markdown.
- Metadatos automáticos: título, URL de origen (guardada en frontmatter), fecha de captura.
- Fallback claro si el sitio bloquea el scraping o requiere login: guardar solo el enlace y el título.
- (Extensión de navegador real como iteración futura, fuera de alcance del núcleo de escritorio.)

**Usuario nuevo**: no la usa en el primer minuto, pero es una de las razones típicas por las que alguien busca una app de "segundo cerebro" — su ausencia es una carencia notable frente a herramientas como Obsidian+clippers o Notion.

**Usuario habitual (50 %)**: la usaría regularmente para guardar artículos que hoy probablemente acumula en marcadores del navegador sin conectar con el resto de su conocimiento.

**Usuario avanzado**: la combina con tags y wikilinks para que cada artículo guardado quede conectado a sus notas de proyecto.

**Desarrollador**: es la propuesta con más riesgo de "servicio externo poco fiable" (parseo de HTML arbitrario, sitios que cambian de estructura, posible necesidad de un `HttpClient` con user-agent adecuado). Complejidad **media-alta**; conviene empezar con una heurística simple (extraer `<article>`/`<main>` y texto denso) y aceptar que no será perfecto en todos los sitios, documentándolo como limitación conocida en vez de perseguir un parser universal.

---

### B. Asistencia inteligente

#### 10. Motor de IA opcional (BYO-key o modelo local)

El módulo "IA Skills" ya reconoce en su propio texto que **no hay ningún motor de IA conectado** — son solo plantillas de prompt estáticas para pegar en un asistente externo. Esta es la carencia más visible del nombre del módulo frente a su contenido real, y la oportunidad más grande de añadir valor genuino: conectar esas skills (y el vault en general) a un modelo real, sin comprometer la filosofía de privacidad.

**Sub-funcionalidades:**
- Configuración en Ajustes › IA (nueva sección) para introducir una API key propia del usuario (Claude, OpenAI, u otra) **o** apuntar a un servidor local compatible (ej. Ollama) — nunca una clave gestionada por Enki ni una llamada obligatoria.
- Desactivado por completo por defecto, igual que Polar hoy está oculto sin credenciales.
- Botón "Ejecutar con IA" sobre una skill ya existente, que envía el contenido de la skill + el contexto que el usuario elija (la nota activa, una selección de texto) al modelo configurado.
- Casos de uso concretos: resumir la nota activa, generar tags sugeridos, redactar una respuesta a partir de una selección, generar un plan de subtareas para una tarjeta Kanban a partir de su descripción.
- Todo el intercambio queda visible al usuario antes de enviarse (no hay envío silencioso de datos en segundo plano).

**Usuario nuevo**: la ve desactivada por defecto y no la echa en falta el primer día; cuando lee que existe, valora que sea opcional y con su propia clave en vez de una suscripción forzada.

**Usuario habitual (50 %)**: es probablemente quien hoy copia manualmente el contenido de una skill a ChatGPT/Claude en el navegador — esta funcionalidad le ahorra exactamente ese salto de aplicación.

**Usuario avanzado**: la integra en su flujo diario para resumir notas de reunión largas o generar subtareas de Kanban a partir de una descripción de proyecto.

**Desarrollador**: es la propuesta de mayor impacto pero también la que exige más disciplina de diseño para no romper la promesa "funciona sin internet" del README — debe quedar clarísimo que es 100 % opcional y que el núcleo (Notas, Kanban, Calendario, Recordatorios) sigue funcionando sin ella, igual que Polar hoy. Técnicamente es "solo" un cliente HTTP hacia la API elegida (patrón ya usado para Supabase) con streaming de respuesta; el reto real es de UX (mostrar claramente qué se envía y cuándo) y de alcance (empezar por 2-3 acciones concretas, no un "chat general" desde el día uno). Complejidad **alta**.

---

#### 11. Búsqueda semántica local

La búsqueda global de hoy es por coincidencia de texto (título, preview, tags) con debounce de 200 ms. Es rápida y predecible, pero no encuentra una nota sobre "cómo organizar mis finanzas" si el usuario busca "presupuesto mensual" y esas palabras exactas no aparecen. La búsqueda semántica resuelve eso sin depender de un servicio en la nube, usando un modelo de embeddings pequeño que corre localmente.

**Sub-funcionalidades:**
- Índice de embeddings generado en segundo plano al guardar una nota (modelo ligero, ej. vía ONNX Runtime, sin llamada de red).
- Nueva pestaña "Similares" en los resultados de búsqueda global, junto a los resultados por texto exacto.
- Reconstrucción del índice bajo demanda desde Ajustes (útil tras importar muchas notas de golpe).
- Completamente opcional y desactivable — el buscador por texto sigue siendo el modo por defecto.

**Usuario nuevo**: no lo necesita con pocas notas; el buscador por texto ya es suficiente al principio.

**Usuario habitual (50 %)**: empieza a notar que "recuerda que escribió algo sobre esto" pero no encuentra la nota con las palabras exactas.

**Usuario avanzado**: con cientos de notas, la búsqueda semántica es la diferencia entre un vault que sigue siendo navegable y uno donde el conocimiento se pierde por no recordar el vocabulario exacto usado meses atrás.

**Desarrollador**: complejidad **alta** — es la única propuesta que introduce una dependencia de inferencia de modelo local (tamaño de paquete, tiempo de indexado, gestión de un índice vectorial simple tipo HNSW o incluso fuerza bruta para vaults pequeños). Conviene tratarla como fase 2 de la propuesta 10 (IA), no como primera pieza a construir.

---

#### 12. Panel de "notas relacionadas"

Con o sin búsqueda semántica completa, un panel simple de "notas que comparten tags o wikilinks salientes/entrantes con esta" ya aporta valor de descubrimiento con el dato que Enki ya tiene hoy, sin necesitar ningún modelo de IA.

**Sub-funcionalidades:**
- Nueva pestaña en el panel lateral del editor (junto a Outline/Backlinks/Stats), "Relacionadas".
- Cálculo por solapamiento de tags y por notas que comparten un wikilink en común con la actual (no por contenido semántico, salvo que la propuesta 11 esté activa, en cuyo caso se prioriza esa señal).
- Click abre la nota relacionada igual que un backlink.

**Usuario nuevo**: con pocas notas el panel estará casi siempre vacío, lo cual es correcto y no debe mostrarse como error, sino como un estado vacío explicativo.

**Usuario habitual (50 %)**: empieza a ver conexiones que no había hecho explícitas con wikilinks.

**Usuario avanzado**: lo usa activamente para tejer la red de notas, confirmando que ciertos apuntes dispersos en realidad hablan del mismo tema.

**Desarrollador**: complejidad **baja-media** en su versión sin IA (es una consulta sobre datos ya indexados por `VaultService`/`NoteTagHelper`/`WikiLinkParser`); mismo panel se reutiliza más adelante si se añade la señal semántica.

---

### C. Productividad y seguimiento personal

#### 13. Seguimiento de hábitos minimalista

Kanban y Polar cubren tareas con fin ("hacer X una vez"), pero no hábitos recurrentes de seguimiento simple ("meditar", "leer 20 min", "sin cafeína después de las 6") donde lo que importa es el registro diario, no completar una tarea con fecha límite. Enki ya tiene el motor de recurrencia de Polar (`RollRecurringTasks`), que es un punto de partida técnico distinto pero cercano.

**Sub-funcionalidades:**
- Lista corta de hábitos definidos por el usuario (nombre, frecuencia: diario/semanal/días concretos).
- Marcar como hecho/no hecho por día, sin rachas visuales agresivas — un indicador discreto de constancia (ej. un cuadrito relleno u hueco por día, mismo lenguaje visual que el heatmap de notas diarias del punto 8).
- Aparece en el Dashboard como una sección más, junto a "HOY" y las tareas del tablero.
- Sin puntos, sin niveles, sin notificaciones de "no rompas tu racha" — coherente con "no hay gamificación" del README.

**Usuario nuevo**: puede ignorarlo sin problema; es una sección más del Dashboard, no un onboarding obligatorio.

**Usuario habitual (50 %)**: es quien probablemente ya usa Kanban para "hábitos" de forma forzada (una tarjeta que recrea cada semana) — esta funcionalidad le da la herramienta correcta para eso.

**Usuario avanzado**: lo integra en su rutina diaria junto al diario breve (punto 16) y la nota del día.

**Desarrollador**: complejidad **baja-media**; nuevo modelo simple (`Habit`, `HabitEntry` por fecha) y un `HabitService` que sigue el mismo patrón de persistencia JSON ya estandarizado en todo el proyecto. No depende de Polar ni de sincronización para la primera versión.

---

#### 14. Registro de tiempo (Pomodoro/cronómetro) vinculado a tareas

Las tarjetas Kanban y las tareas Polar ya tienen una estimación en minutos (`DurationMinutes`), pero no hay forma de registrar el tiempo **real** invertido, que es la comparación que de verdad interesa a quien gestiona su tiempo.

**Sub-funcionalidades:**
- Botón "Iniciar cronómetro" en el detalle de una tarjeta Kanban o tarea Polar.
- Temporizador Pomodoro opcional (25/5 por defecto, configurable) con notificación discreta al terminar el bloque, reutilizando el `NotificationService` ya existente.
- Registro acumulado de tiempo real por tarjeta, visible junto a la estimación original.
- Resumen de tiempo invertido por día/semana, alimentando la página de Estadísticas (punto 15).

**Usuario nuevo**: no lo necesita el primer día; es una funcionalidad para quien ya gestiona activamente su tiempo.

**Usuario habitual (50 %)**: es quien estima duración en sus tarjetas Kanban pero nunca compara con lo real — esta funcionalidad cierra ese círculo.

**Usuario avanzado**: usa el cronómetro en cada sesión de trabajo enfocado, y la comparación estimado-vs-real le sirve para mejorar sus propias estimaciones futuras.

**Desarrollador**: complejidad **media**; el temporizador en sí es sencillo (similar al scheduler de recordatorios cada 30 s ya existente), pero hay que decidir cómo conviven varias tarjetas con cronómetro simultáneo (probablemente limitarlo a una activa a la vez, con aviso al intentar iniciar una segunda).

---

#### 15. Página de Estadísticas / Insights personales

El Dashboard resume "hoy", pero no hay ninguna vista de tendencia a lo largo del tiempo (¿escribo más o menos que el mes pasado? ¿completo más tareas los lunes?). Esta propuesta agrega datos que ya existen en distintos módulos sin crear un nuevo almacén de datos.

**Sub-funcionalidades:**
- Nueva página "Estadísticas" (o sección dentro de Ajustes/Dashboard) con: notas creadas/editadas por semana, tareas Kanban/Polar completadas por semana, tiempo invertido (si el punto 14 está activo), constancia de hábitos (si el punto 13 está activo).
- Gráficas simples y monocromas, coherentes con el sistema de diseño (barras/líneas finas, sin colores llamativos salvo verde/rojo ya establecidos para éxito/error).
- Rango configurable (última semana, último mes, todo el histórico).

**Usuario nuevo**: la ve vacía o casi vacía los primeros días — debe comunicar explícitamente que "vuelve cuando tengas más actividad", igual que otros estados vacíos ya cuidados en la app.

**Usuario habitual (50 %)**: la consulta de vez en cuando por curiosidad, sin que sea parte de su rutina diaria.

**Usuario avanzado**: la revisa semanalmente como parte de su propia revisión personal (weekly review), cruzando datos de varios módulos que hoy solo puede ver por separado.

**Desarrollador**: complejidad **media**; es agregación pura sobre datos existentes (fechas de archivo de `VaultService`, `CompletedAt` de Kanban/Polar), similar en espíritu a `CalendarAggregationService`. Requiere elegir una librería de gráficos ligera compatible con Avalonia o dibujar las gráficas simples a mano con `Path`/`Canvas` para no añadir una dependencia pesada.

---

#### 16. Diario breve integrado en la nota diaria

Distinto de journaling completo: un campo opcional y rápido (estado de ánimo de una lista corta, o un campo de texto libre de una línea) que se guarda junto a la nota diaria sin obligar a nada.

**Sub-funcionalidades:**
- Campo opcional en la cabecera de la nota diaria (no un módulo nuevo separado): "¿Cómo ha ido el día?" con 3-5 opciones neutras (texto, no emojis, coherente con el tono monocromo) o campo libre.
- Se guarda como parte del frontmatter de la nota diaria, no en un almacén separado.
- Visible en la vista de constancia del punto 8 como una señal adicional (opcional, desactivable).

**Usuario nuevo**: puede ignorarlo sin fricción — es un campo opcional, no un formulario obligatorio.

**Usuario habitual (50 %)**: lo prueba unos días y decide si le aporta o lo abandona sin culpa (no hay racha que romper).

**Usuario avanzado**: lo combina con hábitos y estadísticas para tener una revisión personal más completa.

**Desarrollador**: complejidad **baja**; es un campo más en el frontmatter YAML de la nota diaria, reutilizando el mecanismo de tags-como-frontmatter ya existente.

---

### D. Organización estructurada

#### 17. Notas "tabla" con propiedades tipadas

Para casos donde una lista simple de Markdown no basta (una lista de libros con autor/estado/valoración, un registro de gastos, un directorio de contactos), una nota de tipo "tabla" con columnas tipadas (texto, número, fecha, selección, casilla) cubre ese hueco sin convertir Enki en un gestor de bases de datos completo.

**Sub-funcionalidades:**
- Nuevo tipo de nota (junto a la nota Markdown normal): una tabla con columnas definidas por el usuario y filas como registros.
- Persistencia como un archivo propio junto a la nota (ej. `.table.json` junto al `.md`, siguiendo el mismo patrón que `.session.json`), o como una tabla Markdown estándar editable con una vista enriquecida encima — a decidir en diseño técnico, priorizando que el archivo siga siendo legible/portable fuera de Enki.
- Filtros y ordenamiento por columna, reutilizando el patrón ya visto en Kanban (ordenar por prioridad, fecha, creación).
- Vinculable con wikilinks igual que cualquier nota.

**Usuario nuevo**: no lo necesita el primer día; es una funcionalidad de usuario ya asentado con datos estructurados que gestionar.

**Usuario habitual (50 %)**: probablemente hoy fuerza estas listas dentro de una nota Markdown normal con tablas manuales, perdiendo ordenamiento y filtros.

**Usuario avanzado**: la usa para llevar registros personales completos (lecturas, gastos, seguimiento de proyectos con métricas) sin salir de Enki.

**Desarrollador**: es la propuesta más compleja de esta lista en términos de UI nueva (un editor de tabla con tipos de columna, similar en esfuerzo a construir un mini Kanban desde cero) y la que más se aleja del principio "todo es Markdown plano" si se opta por un formato propio — conviene evaluar seriamente una representación como tabla Markdown estándar (`| col | col |`) con una vista enriquecida opcional encima, para no romper la portabilidad. Complejidad **alta**.

---

#### 18. Sincronización opcional de notas

Polar ya demuestra que Enki puede sincronizar con Supabase de forma offline-first, opcional y sin comprometer el uso local. Extender ese mismo mecanismo a las notas (hoy exclusivamente locales) permitiría tener el vault disponible en más de un equipo, siempre como opción explícita.

**Sub-funcionalidades:**
- Reutiliza la cuenta Polar ya vinculada (mismo login, misma infraestructura Supabase) — no una cuenta ni servicio nuevo.
- Sincroniza el contenido Markdown y la estructura de carpetas, con la misma estrategia de dirty flags + LWW por `updated_at` que ya usa `PolarSyncService` para tareas.
- Cifrado extremo a extremo opcional para el contenido de las notas (distinto del cifrado de sesiones ya implementado con envelope encryption AES-GCM + PBKDF2, pero reutilizando el mismo `SessionEncryptionService` como base técnica) — de forma que ni siquiera el backend de Supabase pueda leer el contenido.
- Resolución de conflictos visible al usuario cuando dos equipos editaron la misma nota offline (no solo "gana el más reciente" silenciosamente, dado que aquí sí importa el contenido, a diferencia de una tarea con pocos campos).

**Usuario nuevo**: no lo activa el primer día — sigue siendo 100 % local y ese debe seguir siendo el comportamiento por defecto, exactamente como Polar hoy.

**Usuario habitual (50 %)**: quizás nunca lo necesite si solo usa un equipo.

**Usuario avanzado**: es quien trabaja desde el portátil y el sobremesa (o quiere leer notas desde el móvil vía la app Android de Polar) y hoy no tiene forma de mantenerlas sincronizadas sin una herramienta externa (Syncthing, Git, Dropbox).

**Desarrollador**: es la propuesta de mayor riesgo de esta lista en términos de **pérdida de datos** si se hace mal — a diferencia de una tarea Kanban con 6 campos, un conflicto de edición en un documento de texto libre no se resuelve bien con "el más reciente gana". Requiere, como mínimo, guardar ambas versiones en conflicto y dejar que el usuario decida (similar a lo que ya hace el historial de versiones del punto 3, que de hecho es un prerrequisito natural de esta propuesta). Complejidad **alta**; se recomienda explícitamente como la última propuesta de esta lista en implementarse, y solo tras tener el historial de versiones local (punto 3) como red de seguridad.

---

#### 19. Multi-vault sin reiniciar

Hoy cambiar de vault implica ir a Ajustes y apuntar a otra carpeta, perdiendo el contexto de la sesión actual. Un selector rápido de "vaults recientes" (similar en espíritu al selector de workspace que ya existe entre vault completo y sesión) reduce la fricción para quien separa, por ejemplo, un vault personal y uno de trabajo.

**Sub-funcionalidades:**
- Selector de vault accesible desde la sidebar o desde Ajustes, con lista de rutas usadas anteriormente.
- Cambiar de vault recarga los servicios ya existentes (mismo mecanismo que ya usa `InitializeShell` al configurar el vault por primera vez), sin reiniciar el proceso.
- Cada vault mantiene sus propios `VaultSettings`, exactamente como ya ocurre hoy.

**Usuario nuevo**: no lo necesita — normalmente empieza con un único vault.

**Usuario habitual (50 %)**: puede que ya tenga la tentación de separar "notas personales" de "notas de trabajo" pero hoy eso implica reconfigurar Enki cada vez.

**Usuario avanzado**: cambia de vault varias veces al día según el contexto (personal/trabajo/proyecto específico).

**Desarrollador**: complejidad **media**; el reto no es técnico sino de disciplina de limpieza de estado — asegurar que ningún servicio quede con referencias al vault anterior tras el cambio (mismo tipo de cuidado que ya se aplicó al separar `.devdata` de la carpeta de datos real en builds de debug).

---

#### 20. Captura rápida desde la app Android de Polar

Polar ya sincroniza tareas con una app Android homónima. Extender esa misma app (o una función ligera dentro de ella) para capturar una nota corta de texto que llegue al vault de escritorio al sincronizar, cierra el caso de uso de "se me ocurre algo en la calle y no tengo el portátil".

**Sub-funcionalidades:**
- Campo de "captura rápida" en la app Android que crea una nota de texto plano corta.
- Al sincronizar, esa captura llega a una carpeta específica del vault (ej. `Inbox/`) para que el usuario la procese y archive después, sin forzar su ubicación final.
- No sincroniza el vault completo de notas a Android (eso sería la propuesta 19) — es solo un flujo de entrada de un sentido.

**Usuario nuevo**: no lo necesita hasta que vincula Polar por primera vez.

**Usuario habitual (50 %)**: probablemente ya usa Polar en Android para tareas; esta ampliación es un paso natural más que un cambio de hábito.

**Usuario avanzado**: la usa para no perder ideas fuera del escritorio, revisando el `Inbox/` como parte de su rutina de repaso.

**Desarrollador**: complejidad **media**; reutiliza la infraestructura de sincronización de Polar ya existente (nueva tabla simple en Supabase, ej. `quick_captures`, con el mismo patrón push/pull), pero requiere tocar la app Android además del proyecto Avalonia — el mayor coste no es de arquitectura sino de superficie de código a mantener en dos plataformas.

---

### F. Archivos y adjuntos

#### 21. Notas de voz

Una idea capturada hablando (en el coche, caminando) es tan válida como una nota escrita. Adjuntar audio a una nota o recordatorio, con transcripción local opcional, cierra ese modo de captura.

**Sub-funcionalidades:**
- Botón de grabación en el editor de notas y en el editor de recordatorios.
- El archivo de audio se guarda como adjunto del vault (mismo tratamiento que cualquier archivo no-Markdown en el módulo Archivos).
- Transcripción opcional y local (modelo pequeño tipo Whisper ejecutado on-device, sin enviar audio a ningún servicio) que inserta el texto transcrito en la nota, dejando el audio original como respaldo.
- Reproductor simple embebido en la vista de la nota.

**Usuario nuevo**: no lo necesita de entrada.

**Usuario habitual (50 %)**: lo usaría en escenarios puntuales (capturar una idea rápida sin teclear).

**Usuario avanzado**: lo integra en su flujo de captura junto al resto de modos de entrada (teclado, web clipper, captura rápida móvil).

**Desarrollador**: la grabación y el adjunto son de complejidad **baja-media** (reutiliza el módulo de Archivos ya existente); la transcripción local añade una dependencia de inferencia de modelo, en la misma familia de coste que la búsqueda semántica (punto 11) — se recomienda lanzar la grabación/adjunto primero y tratar la transcripción como mejora posterior, opcional y desactivada por defecto.

---

#### 22. Pegar capturas de pantalla directamente en el editor

Hoy insertar una imagen en una nota requiere el flujo de "insertar imagen" del toolbar de Markdown, que probablemente exige tener el archivo ya guardado en disco. Pegar una captura de pantalla directamente desde el portapapeles (`Ctrl+V`) y que Enki la guarde automáticamente como adjunto es una fricción menos, muy usada en herramientas comparables.

**Sub-funcionalidades:**
- Al pegar una imagen desde el portapapeles en el editor, Enki la guarda en una carpeta de adjuntos junto a la nota (ej. `<nota>.attachments/`) y auto-inserta la sintaxis Markdown `![](ruta-relativa)`.
- Nombre de archivo generado automáticamente (timestamp) para evitar colisiones.
- Redimensionado automático opcional para no acumular capturas a resolución completa sin necesidad.

**Usuario nuevo**: la nota apenas la primera vez que intenta pegar una imagen y descubre que "simplemente funciona" — es justo el tipo de detalle pequeño que genera confianza inmediata.

**Usuario habitual (50 %)**: la usa con frecuencia para capturas de pantalla de referencia dentro de notas técnicas o de proyecto.

**Usuario avanzado**: la da por hecha como parte de su flujo normal de captura de notas.

**Desarrollador**: complejidad **baja**; es manejo del portapapeles de Avalonia (`Clipboard.GetDataAsync` para imágenes) más guardar el archivo en disco, reutilizando el mecanismo de importación de archivos ya existente en el módulo de Archivos.

---

### G. Interacción y flujo de trabajo

#### 24. Paleta de comandos unificada

Enki ya tiene Quick Switcher (`Ctrl+P`, solo navega a notas) y Búsqueda global (`Ctrl+Shift+F`, solo busca contenido). Falta la tercera pieza clásica: una paleta de **comandos** (`Ctrl+K`) que ejecuta acciones — crear una nota nueva, cambiar de tema, abrir Ajustes, crear una tarjeta Kanban — sin necesidad de navegar con el ratón.

**Sub-funcionalidades:**
- Lista de comandos registrados por cada módulo (similar en espíritu al patrón `[RelayCommand]` ya usado en cada viewmodel, expuesto además a un registro central).
- Búsqueda difusa sobre el nombre del comando, con atajo de teclado mostrado junto a cada entrada si lo tiene.
- Extensible: cada módulo nuevo (incluidas futuras propuestas de este documento) añade sus propios comandos sin tocar un archivo central gigante.

**Usuario nuevo**: es quien más se beneficia sin saberlo — reduce la curva de "¿dónde está el botón para X?" a "escribo lo que quiero hacer".

**Usuario habitual (50 %)**: probablemente ya usa el Quick Switcher para notas; la paleta de comandos le enseña que puede hacer lo mismo con acciones, no solo navegación.

**Usuario avanzado**: la usa como su forma principal de interactuar con la app, casi sin tocar el ratón.

**Desarrollador**: complejidad **media**; el patrón de UI (popup con input + lista filtrada) ya existe tres veces en la app (Quick Switcher, búsqueda global, inbox de vencimientos) — se trata de generalizar ese patrón a un registro de comandos en vez de construir uno nuevo desde cero. El reto real es de gobierno: decidir qué comandos merece la pena registrar sin que la lista se vuelva inmanejable.

---

#### 25. Automatizaciones simples

Un motor de reglas ligero ("cuando ocurra X en el módulo A, haz Y en el módulo B") formaliza conexiones que hoy el usuario hace a mano — ej. "cuando complete todas las subtareas de una tarjeta, márcala como hecha" (que ya existe como automatismo fijo) generalizado a reglas configurables por el usuario.

**Sub-funcionalidades:**
- Reglas simples de la forma "cuando [evento] entonces [acción]", con una lista acotada de eventos (tarjeta movida a columna X, tarea completada, recordatorio vencido, nota creada con tag Y) y acciones (crear recordatorio, mover tarjeta, añadir tag, crear notificación).
- Editor visual simple (no un lenguaje de scripting) para no romper la promesa de simplicidad de la app.
- Cada regla se puede desactivar sin borrarla.

**Usuario nuevo**: no la necesita ni la debe ver de entrada — es una funcionalidad avanzada que debe estar claramente en una sección separada de Ajustes.

**Usuario habitual (50 %)**: probablemente ni la descubre hasta que alguien le sugiere un caso de uso concreto.

**Usuario avanzado**: es quien construye 3-4 reglas que le ahorran pasos repetitivos (ej. "tarea Polar con tag `urgente` completada → notificación con sonido", "tarjeta movida a Hecho con tag `facturable` → recordatorio de facturar en 7 días").

**Desarrollador**: es la propuesta con más riesgo de "explosión de alcance" de todo el documento — un motor de reglas mal acotado tiende a convertirse en un lenguaje de programación por la puerta de atrás. Se recomienda empezar con una lista **fija y pequeña** de combinaciones evento→acción soportadas (no una gramática libre), reutilizando los eventos de servicio ya existentes (`BoardChanged`, `RemindersChanged`, etc.) como ganchos naturales. Complejidad **alta** si se generaliza demasiado, **media** si se mantiene deliberadamente acotada.

---

#### 26. Dashboard configurable

El Dashboard actual tiene una composición fija (resumen de hoy, notas recientes, tareas del tablero, identidad del vault, actividad, accesos rápidos). Dejar que el usuario elija qué secciones ver y en qué orden respeta que cada persona usa Enki de forma distinta — quien no usa Polar no necesita ver su resumen; quien no usa Kanban no necesita esa sección.

**Sub-funcionalidades:**
- Cada sección del Dashboard se puede mostrar/ocultar de forma independiente (mismo patrón ya usado para ocultar secciones enteras de la sidebar, extendido aquí al Dashboard).
- Reordenar secciones por arrastre (mismo mecanismo de drag & drop ya usado en Kanban y en las pestañas del editor).
- Configuración persistida por vault, coherente con que `VaultSettings` ya guarda otras preferencias de interfaz.

**Usuario nuevo**: ve el Dashboard por defecto con todas las secciones — la personalización no debe ser un paso obligatorio de onboarding.

**Usuario habitual (50 %)**: probablemente oculta 1-2 secciones que no usa (ej. Polar si no lo tiene vinculado, aunque hoy ese caso ya se gestiona mostrando "no conectado").

**Usuario avanzado**: reordena el Dashboard para que lo primero que ve al abrir Enki sea justo lo que más usa.

**Desarrollador**: complejidad **baja-media**; extiende el patrón ya existente de "ajustes granulares de sidebar" (recién añadido) a las secciones del Dashboard, sin requerir un sistema de widgets genérico y complejo.

---

#### 27. Exportar/imprimir Calendario o tablero Kanban

Complementa la propuesta 6 (exportar notas): a veces lo que se necesita compartir o archivar no es una nota, sino una vista completa — la semana planeada, o el estado actual de un tablero.

**Sub-funcionalidades:**
- "Exportar como imagen" desde el Calendario (vista Semana/Mes) y desde Kanban, capturando el layout visual tal cual se ve en pantalla.
- "Exportar como PDF" para el Calendario en vista Mes/Semana (más útil para imprimir que una imagen suelta).

**Usuario nuevo**: no la necesita el primer día.

**Usuario habitual (50 %)**: la usaría puntualmente (compartir el plan de la semana, archivar el estado de un tablero antes de limpiarlo).

**Usuario avanzado**: la integra en revisiones periódicas (archivar una foto del tablero al cierre de cada sprint personal).

**Desarrollador**: complejidad **baja-media**; Avalonia permite renderizar un control visual a un bitmap sin necesidad de reconstruir el layout con otra tecnología — es la propuesta de exportación más barata de construir de todo el documento, precisamente porque reutiliza el layout visual ya existente en vez de generar un documento desde cero.

---

### H. Plataforma y personalización

#### 28. Atajos de teclado remapeables

Enki ya tiene una buena cantidad de atajos fijos (`Ctrl+Shift+F`, `Ctrl+P`, `Ctrl++`/`Ctrl+-`/`Ctrl+0`, etc.). Permitir remapearlos es un detalle pequeño pero muy valorado por usuarios de teclado intensivo, y barato de construir dado que ya existe una capa de comandos (`[RelayCommand]`) sobre la que apoyarse.

**Sub-funcionalidades:**
- Página en Ajustes con la lista de atajos actuales y un campo para reasignarlos.
- Detección de conflictos (dos acciones con el mismo atajo) con aviso antes de guardar.
- Botón "Restaurar atajos por defecto".

**Usuario nuevo**: no lo toca el primer día; usa los atajos por defecto sin fricción.

**Usuario habitual (50 %)**: quizás quiera cambiar uno o dos que chocan con atajos de su sistema operativo o de otras apps que usa a la vez.

**Usuario avanzado**: remapea varios atajos para ajustar Enki a su memoria muscular de otras herramientas.

**Desarrollador**: complejidad **media**; requiere centralizar la definición de atajos (hoy probablemente repartida en `KeyBinding`s por vista) en una tabla configurable, similar en espíritu a como ya se centralizaron los labels localizados. No es difícil técnicamente, pero sí requiere tocar bastantes vistas para que todas lean de la misma fuente de verdad.

---

#### 29. Sistema de plugins/extensiones (a futuro)

La sección de Skills ya tiene la forma de una plantilla de extensibilidad (archivos `.md` con frontmatter, importables, exportables, con un catálogo externo). Evolucionar eso a un sistema de plugins reales — módulos que añaden una vista, un comando o una integración sin tocar el core — es la vía natural para que la comunidad (si el proyecto se abre más allá de uso personal) aporte funcionalidad sin que todo pase por el mismo desarrollador.

**Sub-funcionalidades:**
- Manifiesto declarativo simple (nombre, versión, permisos que solicita, punto de entrada) en la línea del frontmatter YAML ya usado por Skills.
- Sandbox de permisos explícito: un plugin declara si necesita leer notas, crear notificaciones, acceder a Kanban, etc., y el usuario aprueba esos permisos al instalarlo (mismo espíritu que el campo `permissions` que el frontmatter de Skills ya contempla, pero aplicado a código ejecutable en vez de solo texto).
- Catálogo de plugins de la comunidad, en la línea del enlace externo que Skills ya tiene hoy.

**Usuario nuevo**: no lo necesita ni lo ve — es infraestructura para power users y para la comunidad, no una funcionalidad de cara al usuario medio.

**Usuario habitual (50 %)**: probablemente nunca instale un plugin, pero se beneficia indirectamente si la comunidad construye integraciones que el equipo core no tendría tiempo de mantener.

**Usuario avanzado**: es quien instalaría plugins de terceros o incluso escribiría los suyos propios.

**Desarrollador**: es, junto con la sincronización de notas (punto 18), de las propuestas de **mayor esfuerzo** de todo el documento — no tanto por la ejecución de plugins en sí (Avalonia y .NET tienen mecanismos de carga dinámica de ensamblados), sino por el coste de mantener una superficie de API estable a largo plazo y un modelo de seguridad serio (un plugin mal hecho o malicioso podría leer todo el vault). Se recomienda **no abordarlo** hasta que el proyecto tenga usuarios más allá del propio autor, y mientras tanto seguir creciendo Skills como plantillas de texto, tal como está documentado hoy.

---

## 3. Cómo priorizar

Ordenado de menor a mayor esfuerzo/riesgo relativo, para decidir por dónde empezar si se aborda este documento de forma incremental:

**Bajo esfuerzo, alto valor inmediato** (candidatos para empezar):
- Plantillas de notas (1)
- Modo enfoque/zen (7)
- Pegar capturas de pantalla (23)
- Notas diarias mejoradas (8)
- Panel de notas relacionadas sin IA (12)
- Exportar Calendario/Kanban como imagen (27)

**Esfuerzo medio, valor alto** (segunda ola):
- Gestor global de etiquetas (4)
- Historial de versiones locales (3)
- Grafo global del vault (5)
- Registro de tiempo vinculado a tareas (14)
- Página de Estadísticas (15)
- Seguimiento de hábitos (13)
- Paleta de comandos (24)
- Dashboard configurable (26)
- Multi-vault (19)

**Esfuerzo alto o riesgo alto** (evaluar con calma, posiblemente por fases):
- Motor de IA opcional (10) → luego búsqueda semántica (11)
- Sincronización de notas (18) — requiere el historial de versiones (3) como prerrequisito
- Notas tabla (17)
- Automatizaciones (25) — mantener acotado desde el diseño
- Web clipper (9) — riesgo de mantenimiento por dependencia de sitios externos
- Notas de voz con transcripción (21) — grabación primero, transcripción después
- Sistema de plugins (29) — no recomendado mientras el uso siga siendo personal

---

## 4. Resumen ejecutivo

Enki ya resuelve bien el núcleo de un segundo cerebro personal: notas conectadas, tareas locales y en la nube, calendario unificado, recordatorios y papelera transversal, todo con una identidad visual coherente. Lo que falta no es corregir lo que hay (eso ya lo cubre el roadmap existente), sino **cerrar los modos de captura y de reflexión** que un segundo cerebro maduro necesita: capturar más fácil (plantillas, web clipper, voz, capturas de pantalla), pensar sobre lo capturado (historial de versiones, grafo global, notas relacionadas, estadísticas) y, si el usuario lo decide explícitamente, dejar que un modelo de IA ayude sin comprometer la privacidad que es la razón de ser de la app.

La propuesta de mayor impacto potencial es también la más delicada: conectar un motor de IA real de forma opcional y con datos del propio usuario (punto 10), porque es lo único que separa hoy a "IA Skills" de ser solo una carpeta de plantillas de texto. Todo lo demás son mejoras de fricción — pequeñas por separado, pero que en conjunto son la diferencia entre una app que "hace lo que promete" y una que "se siente completa".
