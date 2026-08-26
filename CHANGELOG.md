# Changelog

Todos los cambios notables de Enki se documentan en este fichero. El formato sigue a grandes rasgos
[Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/), y el versionado sigue
[SemVer](https://semver.org/lang/es/) (`MAJOR.MINOR.PATCH`) mientras la app se mantenga en `0.x` — es
decir, cualquier cambio puede romper compatibilidad entre versiones menores hasta llegar a `1.0.0`.

## [Sin publicar]

**Dos vistas para la página de Notas**: nuevo ajuste **Ajustes › Editor › Vista del editor** (Completa /
Simplificada, persistido en `NotesViewMode` y aplicado en caliente, sin cerrar ni reabrir nada). La vista
completa es la de siempre; la simplificada es una disposición mínima al estilo Obsidian, pensada para
escribir sin distracciones:

- Panel lateral con un **árbol de carpetas y notas** de la sesión activa (solo markdown), con renombrado en
  línea, menú contextual (nueva nota aquí, nueva subcarpeta, renombrar, eliminar) y arrastrar una nota
  sobre una carpeta —o sobre el fondo, que es la raíz de la sesión— para moverla. A la derecha, las mismas
  pestañas y el mismo editor de siempre; abrir una nota ya abierta salta a su pestaña en vez de duplicarla,
  y el árbol resalta y despliega la rama de la nota activa se abra desde donde se abra.
- Encima del árbol, los botones de **nueva nota**, **nueva carpeta** (ambos en la raíz de la sesión),
  **nota diaria** y **contraer todo**; encima del panel, los modos **árbol** y **buscar** —búsqueda
  recursiva acotada a la sesión activa— y el botón de mostrar/ocultar el panel; abajo, la **sesión
  seleccionada** con su desplegable. Con el panel oculto queda un raíl con solo esas dos salidas: volver a
  mostrarlo y cambiar de sesión.
- El panel se ensancha arrastrando su separador, y tanto su ancho como si está desplegado y qué carpetas
  quedan abiertas sobreviven a reiniciar la app.
- Esta vista deja fuera a propósito la **barra de formato** y el **panel lateral de esquema/enlaces/
  estadísticas**, junto con sus interruptores de la cabecera, el botón de volver (lo hace la "x" de la
  pestaña), la etiqueta de texto del indicador de guardado (la pestaña ya lleva su punto de "sin guardar")
  y la caja de etiquetas. Los interruptores del usuario no se pisan: se conservan y vuelven a aplicarse al
  volver a la vista completa.
- La cabecera del editor se queda ahí en **el nombre de la nota, en pequeño, y las acciones** —el nombre
  baja de 24px a 13px y sigue siendo donde se renombra—, para que todo el peso visual se lo lleve el
  espacio de edición.
- De paso, la cabecera aguanta anchos pequeños sin recortar nada que haga falta pulsar: lo que cede
  espacio es el nombre de la nota, que además está entero en la pestaña.

**HTML simple en el editor de notas**: el editor vuelve a interpretar un subconjunto fijo de HTML en vez
de imprimirlo como texto plano, para poder centrar, alinear a la derecha o justificar un bloque de la nota:

- Contenedores de bloque `<div>`, `<p>` y `<center>` con `align="left|center|right|justify"` (o el
  equivalente `style="text-align: …"`). La alineación se resuelve para todo el documento, así que se aplica
  a cada línea que el contenedor envuelva, no solo a la que lleva la etiqueta, y los bloques anidados
  restauran la alineación del que los contiene al cerrarse.
- Formato en línea: `<b>`/`<strong>`, `<i>`/`<em>`, `<u>`/`<ins>`, `<s>`/`<strike>`/`<del>`, `<mark>`,
  `<code>`, `<span>` y `<br>`, con el mismo aspecto que su equivalente Markdown (una negrita anidada dentro
  de una cursiva se pinta como ambas, no como la última que cierre).
- Los cierres cuentan igual que las aperturas, incluso a varias líneas de distancia: un `<i>` que abre en
  una línea sigue en cursiva hasta el `</i>`, esté donde esté, y un `</b>` cierra también lo que quedara
  abierto dentro de él (`<b><i>x</b>` acaba ahí, no arrastra la cursiva al resto de la nota). Un cierre sin
  apertura se ignora, y la exportación reabre y cierra cada etiqueta bloque a bloque para que el HTML
  resultante quede balanceado.
- Las etiquetas se ocultan como cualquier otro marcador Markdown y reaparecen —atenuadas— cuando el cursor
  entra en ellas. Lo que no está en esa lista (`<cualquiercosa>`), lo que va dentro de comillas invertidas
  o de un bloque ``` y lo escapado con `\<div>` se sigue viendo literal.
- La exportación a HTML/PDF emite los mismos contenedores con su `text-align` (HTML5 ya no reconoce el
  atributo `align`), saneados: de la etiqueta original solo sobrevive la alineación, nunca atributos
  arbitrarios de la nota.
- La alineación funciona fila visual a fila visual, no solo en la primera: un párrafo largo que se parta en
  cuatro filas se centra (o se pega a la derecha) fila por fila, y `justify` estira los espacios de cada
  fila hasta el borde de la columna dejando la última en bandera, igual que un navegador. AvaloniaEdit da
  la misma alineación (izquierda) a todas las filas visuales y no expone ningún ajuste, así que el editor
  lee del propio motor de texto dónde partió cada fila y cuánto mide, y reserva el hueco que falta; el
  cálculo converge en una pasada porque el hueco que añade nunca puede mover el corte de fila.

**Dashboard 100% personalizable**: la organización "Personalizada" pasa de repartir las secciones en un
`UniformGrid` de N columnas (auto-relleno por orden de lista, sin control real) a un editor de rejilla en
Ajustes › Dashboard donde cada sección tiene su celda y su tamaño:

- Rejilla configurable de 1 a 12 filas y columnas (antes el tope eran 6, y solo columnas). Cada ficha se
  arrastra a la celda que se quiera y se estira desde su esquina inferior derecha; mientras se arrastra,
  una silueta muestra el destino y se marca en rojo si ahí no cabe. Un movimiento inválido se rechaza en
  vez de empujar a las vecinas.
- Todo se puede hacer sin ratón: con una ficha seleccionada, las flechas la mueven, `Mayús`+flechas la
  redimensionan y `Supr` la saca de la rejilla; la barra bajo el lienzo repite esas acciones como botones
  con nombre accesible. Las secciones fuera de la rejilla quedan en una bandeja y vuelven de un clic, a su
  posición anterior si sigue libre.
- Pasar de una plantilla (Clásica, Pirámide invertida, Patrón lateral, Rejilla modular, Mosaico) a
  Personalizado parte de la geometría de esa plantilla en vez de un lienzo en blanco — botón
  "personalizar esta organización" junto a la vista previa. "Auto-organizar" compacta la rejilla
  conservando el tamaño de cada ficha.
- La geometría guardada se sanea siempre antes de pintarse: una sección nueva que no existía cuando se
  guardó el layout (p. ej. Hábitos), una rejilla que ha encogido o un `settings.json` editado a mano ya no
  pueden dejar dos secciones una encima de otra ni hacer desaparecer ninguna.
- El Dashboard y la vista previa de Ajustes dejan de verter las secciones en un `Grid` de 6 columnas
  escrito a mano y usan un panel propio (`Controls.DashboardGridPanel`) que mide las filas por su
  contenido y reparte columnas según el ajuste del vault.

Correcciones relacionadas en Ajustes › Dashboard:

- Al abrir Ajustes con una organización ya guardada, la vista previa dibujaba siempre la disposición
  Clásica aunque la tarjeta marcada fuera otra: la plantilla activa no se leía del vault hasta que se
  pulsaba una tarjeta.
- "Restablecer" devolvía el vault a la organización Clásica pero dejaba la página mostrando la anterior.
- En modo Personalizado la lista de secciones ocupa el ancho completo, en vez de dejar media página vacía
  donde iba la vista previa de plantilla.

Sistema de **temas personalizados** rediseñado para comportarse como un producto publicable:

- El editor de temas incluye selector de color (`ColorPicker`) junto al campo HEX, botón de duplicar en
  la lista y dentro del editor, y exportación con título y errores localizados.
- El normalizador HEX acepta `#RGB`, `#RRGGBB` y variantes sin `#`/en minúsculas, en lugar de forzar a
  negro cualquier formato que no fuera exactamente `#RRGGBB`.
- El resaltado de búsqueda en Markdown y el fondo de la línea actual del editor ahora leen del tema
  activo (`HighlightColor`/`InkBrush`) en vez de blanco/amarillo hardcodeados.
- El grafo de sesiones deja de usar blancos literales para sus texturas y trazados; sigue el color de
  tinta (`InkBrush`) del tema.
- La ventana ya no arranca forzosamente en oscuro: `App.axaml` aplica la variante guardada antes de que
  se muestre la ventana principal.
- Las etiquetas del editor de temas se recargan al cambiar el idioma de la UI.

Nueva funcionalidad: **paleta de comandos unificada** (propuesta 25 de
`docs/propuesta-nuevas-funcionalidades.md`):

- Atajo `Ctrl+Shift+K` abre un popup (mismo patrón de UI que el Quick Switcher y la búsqueda global) con
  búsqueda por substring sobre el nombre de cada acción — no solo notas: navegar a cualquier sección de la
  barra lateral, abrir Ajustes/Recordatorios/Notificaciones, alternar la barra lateral o el tema
  claro/oscuro, zoom de interfaz, nota nueva/diaria, importar notas, alternar modo enfoque, nueva columna
  Kanban, abrir la carpeta del vault, Skills de la comunidad y sincronizar Polar.
- La lista de comandos se reconstruye cada vez que se abre el popup en vez de fijarse una sola vez al
  arrancar, así que entradas que dependen de estado en vivo (qué secciones del sidebar están activas, si
  hay una pestaña de nota abierta, si la cuenta Polar está vinculada) nunca quedan obsoletas ni se ofrecen
  cuando no tendrían ningún efecto.
- Ctrl+K a secas se dejó libre para el editor de notas (ya lo usa para insertar un enlace); el atajo de la
  paleta sigue el mismo criterio que ya distingue Ctrl+F de Ctrl+Shift+F.

Nueva funcionalidad: **modo enfoque/zen en el editor** (propuesta 7 de
`docs/propuesta-nuevas-funcionalidades.md`):

- Botón dedicado en la cabecera del editor (junto al de mostrar/ocultar panel lateral) y atajo
  `Ctrl+Shift+Enter` que ocultan cabecera, barra de formato, tarjetas vinculadas y panel lateral de la nota
  activa, además de la barra de pestañas y el sidebar de la app — dejando solo el cuerpo de la nota
  centrado. `Escape` (con el foco en el editor) o el mismo atajo lo desactivan.
- Es un modo compartido entre pestañas (activarlo desde una nota lo aplica a cualquiera que se abra
  después) pero deliberadamente transitorio: no se persiste en disco y siempre arranca apagado al iniciar
  la app o cambiar de vault.

Nueva funcionalidad: **exportar notas a PDF/HTML y vista de impresión** (propuesta 6 de
`docs/propuesta-nuevas-funcionalidades.md`):

- "Exportar como HTML" (documento autónomo monocromo, con etiquetas incluidas) e "Imprimir / PDF" desde el
  menú contextual de cada nota y desde la cabecera del editor.
- La vista de impresión es un overlay a pantalla completa dentro de la ventana principal (no una ventana
  secundaria, para respetar el zoom de interfaz) con previsualización real del documento, opción de
  incluir o no las etiquetas, impresión vía diálogo del sistema y guardado como PDF — ambos a través del
  motor WebView ya integrado, sin dependencias nuevas.
- El render Markdown→HTML reutiliza el mismo parser que el live preview del editor e incrusta las imágenes
  locales referenciadas como data URI para que el HTML exportado sea autocontenido.

Cierra la Fase 10 (opcional) del roadmap de corrección de deficiencias (`docs/analisis-proyecto/roadmap.md`)
— deuda técnica menor y mejoras de producto derivadas de la auditoría, cajón de sastre deliberado:

- Recurrencia local nativa para Recordatorios (independiente de Polar): un recordatorio recurrente se
  reprograma solo a su siguiente ocurrencia en vez de tener que recrearse a mano cada vez.
- "Deshacer" al descartar un recordatorio de la bandeja del shell, con una ventana de unos segundos antes de
  que el descarte se vuelva permanente.
- Los descartes de recordatorios (`dismissed-reminders.json`) se purgan cuando la tarjeta asociada se borra
  para siempre, en vez de quedar huérfanos.
- El WebView del grafo de sesiones se dispone explícitamente al salir de la pestaña — evitaba un motor de
  navegador nativo huérfano por cada ciclo de entra/sale.
- Cancelación de una búsqueda global en vuelo cuando el usuario sigue tecleando, en vez de arriesgarse a que
  una búsqueda lenta pise con resultados obsoletos a una más reciente.
- Intervalo y retención del backup automático (Ajustes › General) ganan control propio en la UI — antes
  solo el interruptor de activación era visible.
- `AnalysisLevel` del analizador de .NET subido a `latest-recommended` en el proyecto principal, con los
  ~116 diagnósticos resultantes corregidos caso a caso (mayormente `CultureInfo` explícito en formateo de
  texto de UI vs. datos internos).
- Icono de aplicación real (`.ico`/`.icns`/PNG), sustituyendo el icono genérico del SO.
- `README.en.md` — versión en inglés, dado que la app ya soporta 6 idiomas de UI.
- Decisión documentada (sin implementar): mantener la composición manual de servicios del shell en vez de
  introducir un contenedor de DI — ver `docs/analisis-proyecto/decision-contenedor-di.md`.

Cierra también la Fase 9 (opcional) — robustez de sincronización Polar y notificaciones, ninguno de sus
hallazgos compromete la integridad de los datos:

- Reconexión automática de `PolarRealtimeService` tras una caída del socket (reutiliza el backoff del
  temporizador de sincronización en vez de un temporizador paralelo propio).
- Los tres botones de "Sync now" (Recordatorios, Gestión de tareas, Ajustes › Cuenta) distinguen un sync
  completado de uno descartado porque otro ya estaba en curso.
- Reintento periódico (cada hora) del aviso de fallo de sincronización mientras el problema persista, no
  solo en la transición inicial.
- Aviso al usuario cuando una edición local pierde un conflicto de sincronización (Last-Write-Wins) frente a
  una versión remota más reciente.

## [0.1.0] — 2026-08-21

Primera versión con número real (`enki/Directory.Build.props`, antes generaba `1.0.0.0` por defecto sin
que nadie lo hubiera decidido) y con este propio changelog. No es un "1.0" porque la app todavía no ha
tenido una release pública ni un empaquetado real para usuarios finales — ver
`docs/analisis-proyecto/roadmap.md`, Fase 8, para el resto de higiene de release pendiente
(instalador/paquete distribuible).

Cierra las Fases 0–8 del roadmap de corrección de deficiencias
(`docs/analisis-proyecto/roadmap.md`), sobre una auditoría completa del código existente:

- **Fase 0** — Red de seguridad mínima: logging a fichero, captura de excepciones no observadas.
- **Fase 1** — Persistencia atómica del contenido de usuario (notas, skills, sesiones, ajustes) y backup
  automático opcional.
- **Fase 2** — Recordatorios: corrección de notificación perdida tras reprogramar y de deriva horaria en
  tareas recurrentes de Polar.
- **Fase 3** — Borrado seguro: papelera y confirmaciones consistentes en Kanban/Notas/Sesiones.
- **Fase 4** — Seguridad de credenciales y aislamiento de cuentas Polar (tokens redactados en
  exportaciones/backups, filas `Dirty` aisladas por cuenta).
- **Fase 5** — Integridad de la red de enlaces (wikilinks): reescritura al renombrar, resolución
  unificada, distinción visual de enlaces rotos/ambiguos.
- **Fase 6** — Rendimiento del editor y del vault: debounce de backlinks/outline/stats, caché de
  metadatos de notas, tamaño de vault asíncrono.
- **Fase 7** — Accesibilidad: `AutomationProperties.Name` en las 17 vistas, vía alternativa a
  drag&drop para mover tarjetas Kanban, atajos de teclado.
- **Fase 8** — Higiene de documentación y operaciones de release: enlaces de documentación reparados,
  LICENSE, CI (`dotnet build`/`dotnet test` en cada push/PR), analizadores de warnings-como-errores,
  metadatos de build (`Company`/`Copyright`/`Version`), este changelog, y guía de empaquetado para
  usuarios no técnicos.

Detalle completo de cada punto (fichero:línea, tests añadidos, criterio de aceptación) en
`docs/analisis-proyecto/roadmap.md`.
