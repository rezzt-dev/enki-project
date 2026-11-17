# Auditoría de interfaz: ajustes de IA y cabecera del asistente

## Resumen

Última revisión: 17 de noviembre de 2026. Alcance: ajustes de inteligencia artificial (activación, personalidad, proveedor, conexión, conversación, permisos, internet, búsqueda semántica e historial), sus confirmaciones y los controles de la cabecera mostrados por el usuario. Referencias: `tema-diseno-estilo-enki.md`, el sistema de diseño y los estilos Avalonia existentes. `.docs/agent-skills/` no existe en esta copia.

Inventario previo a la implementación:

| Superficie | Estado inicial (inventario previo) | Problema observado en el código |
| --- | --- | --- |
| Ajustes de IA | Pendiente, media | Densidad y jerarquía desiguales; ayudas esenciales con tinta demasiado tenue; acciones compiten con mensajes de estado. |
| Proveedor y conexión | Pendiente, media | Opciones en filas rígidas, órdenes no seleccionables y comprobación de conexión duplicada. |
| Conversación y permisos | Pendiente, media | Selectores compiten con las descripciones en columnas estrechas; falta separación entre opciones. |
| Personalidad | Pendiente, alta | Todo el panel se desplaza, incluidas las acciones; ancho y altura fijos. |
| Ámbito y permisos del chat | Pendiente, media | Detalles truncados y aviso final en una fila sin ancho restringido. |
| Modelo y esfuerzo | Pendiente, alta | Modelo predeterminado truncado; alturas desiguales y esfuerzo sin indicador de desplegable. |
| Evaluación | Pendiente, alta | Contenido sin desplazamiento general ni cabecera con cierre directo; estado vacío poco diferenciado. |
| Consentimiento y borrado de historial | Pendiente, media | Revisar ajuste a ventanas pequeñas y jerarquía de acciones. |
| Exportar, nuevo chat y panel lateral | Pendiente, media | Revisar accesibilidad y alineación con los selectores. Exportar usa el selector nativo de archivos; nuevo chat y panel actúan directamente. |

## Pendiente

- Verificación manual en un escritorio real del selector de exportación del sistema y del lector de pantalla. Las capturas Skia y las pruebas headless no validan la apariencia de las ventanas nativas del sistema operativo.

## En duda

Ninguna decisión de producto pendiente. Se conservan paleta, tipografías, geometría rectangular y comportamiento de permisos.

## Hecho

- 17 de noviembre de 2026: reconocimiento del código, estilos, pruebas y documentación; inventario anterior registrado antes de modificar las vistas.

- 17 de noviembre de 2026: ajustes de IA con títulos que ajustan líneas, ayudas esenciales en tinta secundaria, campos de conversación bajo sus descripciones, opciones de autenticación y buscador que pasan a otra línea y estado de guardado separado de las acciones. Las órdenes de conexión se pueden seleccionar. Se elimina la comprobación duplicada del proveedor; sigue disponible en su tarjeta de conexión.
- 17 de noviembre de 2026: cabecera del chat en dos filas, con título y acciones arriba, selectores debajo y salto de línea según el ancho. Modelo de 208 px, controles de 36 px y flecha en el selector de esfuerzo. Borde de foco y apertura mediante los recursos del tema.
- 17 de noviembre de 2026: personalidad con pie de acciones fijo y cuerpo desplazable; ámbito seleccionado con fondo de tinta y detalles en varias líneas; avisos legibles sin truncar. `PopoverViewport` limita estos tres popups al tamaño de la ventana y restaura sus límites al cerrar.
- 17 de noviembre de 2026: evaluación con cabecera, cierre directo, métricas agrupadas, estado vacío diferenciado y resultados separados; consentimiento y borrado de historial con cuerpo desplazable y acciones que pasan a otra línea. `ModalOverlayHost.ConstrainContent` activa el ajuste a la ventana solo en estos diálogos.
- 17 de noviembre de 2026: primera validación del asistente: 26 pruebas correctas, incluidas dos variantes de tema de los popups, redimensionado vertical, Escape, retorno del foco y restauración de límites. Inspección de capturas Skia de personalidad, ámbito, esfuerzo, evaluación y tarjetas de ajustes; datos y ejecutables de prueba en carpetas temporales.

## Evidencias y límites

Las pruebas `AssistantViewUiTests` generan capturas con `ENKI_GRAPH_PREVIEW=<carpeta>`: cabecera a 760 × 540, los tres popups en claro y oscuro, tarjetas de ajustes a 960 × 720 y evaluación a 640 × 380. No se guardan capturas ni datos personales en el repositorio. La restauración de dependencias resolvió el fallo inicial de compilación del paquete de diagnóstico, sin cambiar versiones ni referencias.

Validación final: `enki.Tests` ha pasado sus 3190 pruebas. La ejecución específica de `AssistantViewUiTests` con Skia ha pasado sus 26 pruebas tras la última modificación; incluye reducción a 320 × 380 para los límites del popup y evaluación a 640 × 380 con acciones visibles. Se han inspeccionado también las capturas finales del consentimiento y de la evaluación en ventana baja.

La ejecución adicional de **toda** la suite de interfaz con `ENKI_GRAPH_PREVIEW` terminó con 491 pruebas correctas y un fallo en `EditorViewImagesUiTests.PastingABitmapThatYieldsNoBytes_LeavesNeitherAttachmentNorBrokenLink`: esa prueba exige explícitamente el renderizador simulado, que no codifica bytes, mientras Skia sí codifica la imagen. No se ha modificado el editor para adaptar una prueba ajena al modo de capturas. La suite general ejecutada sin esa variable ha pasado sus 492 pruebas (0 fallos, 0 omitidas).

Cierre de la revisión: ajustes, cabecera y popups del alcance implementados y revisados mediante capturas. Comprobaciones obligatorias: 3190/3190 pruebas de lógica y 492/492 de interfaz correctas; 26/26 pruebas del asistente correctas también con Skia. `git diff --check`, XML de las vistas modificadas y existencia de rutas correctos. Lista de cambios revisada; no hay archivos preparados en el índice ni se ha creado ningún commit. Quedan únicamente las verificaciones manuales indicadas en «Pendiente».

## Revisión transversal de botones secundarios (petición posterior)

17 de noviembre de 2026. Petición adicional: corregir todos los botones que parecen texto subrayado. Hallazgo previo: `Button.ghost` tiene `Padding=0`, carece de contorno y no neutraliza el fondo desactivado de Fluent; este fondo queda pegado a la etiqueta, como se aprecia en «Eliminar resultados locales». Afecta a los usos de la clase compartida en ajustes, diálogos y otras vistas. Objetivo de esta revisión, completado: dar geometría de botón secundario, mantener su jerarquía frente al primario y comprobar reposo, hover, foco y desactivado en ambos temas, con etiquetas directas y contenido compuesto.

Implementado en esta revisión: contorno secundario en `Button.ghost`, relleno interior, altura mínima y refuerzo de foco/hover; estado desactivado con fondo transparente y atenuación de toda la caja. Se conserva el nombre de clase para aplicar la corrección transversal a todas las vistas. Los usos compactos de calendario, Kanban, ajustes y avisos del shell que fijaban margen interior horizontal cero pasan a ocho unidades para que el texto no toque el contorno. Se actualiza `tema-diseno-estilo-enki.md`. Verificación de estilos y asistente completada: 80/80 pruebas correctas con Skia, incluida la matriz de estados en claro y oscuro. Las pruebas de estilo fijan un tema explícito al inicializarse para que su resultado no dependa del orden de otras pruebas. Inspeccionadas las capturas `secondary-buttons-dark.png`, `secondary-buttons-light.png` y la evaluación con sus nuevas acciones secundarias. Las dos suites generales se han comprobado de nuevo.

Alcance comprobado del estilo secundario: 158 botones con la clase exacta `ghost` repartidos en 27 vistas. Las acciones principales conservan su jerarquía. La suite de lógica vuelve a pasar con 3190/3190 pruebas; las pruebas de estilo y asistente pasan con 80/80. `git diff --check` y el análisis XML de las vistas también son correctos. Resultado general confirmado en el cierre de esta revisión.

La suite general de esta revisión detectó dos fallos de clic en `KanbanTagsUiTests`, uno por orientación. Diagnóstico: el botón secundario del filtro común de etiquetas aparece tras calcular cuántos chips quedan ocultos; con la nueva altura general hacía crecer la fila 14 unidades y desplazaba el tablero durante el clic. Se fija el tamaño compacto (altura mínima 22, relleno 8 × 2) en `TagFilterView` para conservar la altura de la fila. La prioridad de las tarjetas conserva su altura compacta de 24 y el desplegable de etiquetas mantiene su tamaño con margen interior horizontal. No se alteran las aserciones existentes de Kanban; la instrumentación temporal se ha retirado. Comprobaciones de regresión completadas tras este ajuste.

Comprobación tras corregir la altura del filtro: 86/86 pruebas específicas correctas con Skia (`ButtonChromeUiTests`, `AssistantViewUiTests` y `KanbanTagsUiTests`). Esto incluye el clic real de despliegue y la expansión dentro de los límites de la tarjeta en ambas orientaciones. La suite general se repite sin el modo de capturas, como exige su prueba del portapapeles.

Cierre de la petición posterior: 3190/3190 pruebas de lógica, 494/494 de interfaz y 86/86 comprobaciones específicas con Skia correctas, sin omisiones. El fallo de clic de Kanban y el aislamiento del tema de las pruebas quedan resueltos. XML y rutas de los archivos modificados revisados, `git diff --check` correcto e índice vacío. No se ha creado ningún commit.
