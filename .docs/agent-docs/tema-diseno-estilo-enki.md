# Enki: tema, diseño y estilo visual

## 1. Objeto y alcance

Este documento describe el lenguaje visual de Enki: su identidad, composición, colores, tipografía, geometría, componentes, estados y movimiento. Sirve de referencia para diseñar pantallas, ampliar las existentes o reproducir su apariencia manteniendo la coherencia de la aplicación.

La descripción toma como base los recursos y las vistas de la aplicación Avalonia. **La paleta de referencia de este documento es la de [better-dark-dev.css](../resources/better-dark-dev.css)**: todos los colores base de las tablas siguientes proceden de ese fichero. La geometría, las familias tipográficas y los patrones de interacción proceden del código de Enki.

La aplicación incluye temas claros, oscuros y personalizados. El fichero CSS proporcionado es una paleta personalizada y difiere del tema oscuro predeterminado de `Colors.axaml`. Describirla aquí no implica que esté seleccionada en una instalación ni modifica los recursos de la aplicación. Este documento tampoco es una auditoría visual ejecutada en todos los sistemas operativos: describe los estilos y las vistas consultados.

Las dimensiones se expresan en unidades lógicas de Avalonia. Su tamaño físico depende del escalado de pantalla y del zoom de la interfaz. Los valores locales de una vista pueden ajustar los estilos compartidos.

## 2. Identidad y carácter de la aplicación

Enki es un espacio de trabajo de escritorio para notas Markdown, archivos, conocimiento conectado y organización personal. Su diseño debe facilitar leer, escribir, encontrar información y trabajar con tareas durante sesiones prolongadas.

Su identidad se define como **brutalismo editorial suizo, monocromo y concebido primero para un entorno oscuro**. La composición se apoya en una retícula clara, alineaciones firmes, titulares de peso alto, etiquetas pequeñas y superficies delimitadas por líneas. El resultado combina el carácter de una publicación editorial con la precisión de una herramienta técnica.

El negro y los grises estructuran la pantalla. La información ocupa el primer plano mediante contraste, tamaño y posición. El color cromático aparece principalmente cuando expresa peligro, éxito, resaltado o una clasificación propia del contenido.

La sensación general es sobria, densa y ordenada. Las zonas de lectura tienen espacio suficiente para respirar; las barras, árboles, filtros y metadatos concentran información sin competir con el documento o la tarea activa.

Los rasgos que hacen reconocible a Enki son:

- Esquinas rectas en botones, campos, tarjetas, pestañas y paneles.
- Bordes finos y diferencias discretas de luminosidad para separar superficies.
- Titulares grandes frente a microetiquetas monoespaciadas y espaciadas.
- Mayúsculas en numerosos títulos, etiquetas y acciones de interfaz.
- Inversión entre tinta y fondo en acciones destacadas y selección de texto.
- Iconos funcionales, contadores pequeños y estados integrados en el propio contexto.
- Movimiento breve, con un perfil alternativo de animaciones sutiles.

## 3. Paleta Better Dark Dev

### 3.1. Fondos y superficies

| Variable CSS | Valor exacto | Recurso de Enki | Función visual |
| --- | --- | --- | --- |
| `--app-background` | `#080808` | `AppBackgroundColor` | Fondo general de la ventana y base más oscura de la composición. |
| `--app-background-alt` | `#0A0A0A` | `AppBackgroundAltColor` | Fondo alternativo; el pincel del editor también deriva de este recurso. |
| `--sidebar-background` | `#0A0A0A` | `SidebarBackgroundColor` | Fondo de la navegación lateral. |
| `--surface` | `#0D0D0D` | `SurfaceColor` | Superficie diferenciada del fondo general. |
| `--panel-background` | `#101010` | `PanelBackgroundColor` | Paneles, tarjetas y ventanas emergentes compartidas. |
| `--panel-background-hover` | `#181818` | `PanelBackgroundHoverColor` | Variante de panel para los componentes que utilizan este estado al pasar el puntero. |
| `--input-background` | `#0C0C0C` | `InputBackgroundColor` | Fondo de campos de entrada. |
| `--row-background` | `#0C0C0C` | `RowBackgroundColor` | Superficie base de filas que emplean este recurso. |
| `--row-background-hover` | `#151515` | `RowBackgroundHoverColor` | Respuesta visual de filas al pasar el puntero. |
| `--scroll-track` | `#080808` | `ScrollTrackColor` | Carril de desplazamiento integrado con el fondo. |

La diferencia entre superficies es intencionadamente pequeña. Un panel se reconoce por su límite, su contenido y su posición, además de por el fondo. La elevación visual se construye mediante estos cambios tonales y los bordes.

La jerarquía cromática parte de `#080808`, pasa por `#0A0A0A`, `#0C0C0C` y `#0D0D0D`, y llega a `#101010` en los paneles. Los estados de interacción pueden subir a `#151515` o `#181818` según el recurso del componente. Esta secuencia describe la paleta; algunos controles utilizan superposiciones de tinta en lugar de esos fondos.

### 3.2. Texto, tinta y acento

| Variable CSS | Valor exacto | Recurso de Enki | Función visual |
| --- | --- | --- | --- |
| `--text-primary` | `#EEEEEE` | `TextPrimaryColor` | Texto principal, títulos y contenido de mayor relevancia. |
| `--text-secondary` | `#929292` | `TextSecondaryColor` | Subtítulos, explicaciones y contenido de jerarquía secundaria. |
| `--text-muted` | `#585858` | `TextMutedColor` | Metadatos y elementos de menor énfasis que usan el recurso atenuado. |
| `--ink` | `#EEEEEE` | `InkColor` | Tinta estructural: inversión, selección, bordes y rampa de opacidades. |
| `--ink-on` | `#181818` | `InkOnColor` | Texto e iconos sobre superficies rellenas de tinta. |
| `--accent` | `#CCCCCC` | `AccentColor` | Acento neutro y origen de las variantes de acento de Fluent. |

El acento es gris claro. La identidad de esta paleta no depende de introducir un color de marca saturado. `InkColor` y `AccentColor` tienen funciones diferentes: los botones de inversión utilizan tinta, mientras que el sistema de acento de los controles deriva de `AccentColor`.

Al seleccionar texto, el sistema utiliza el fondo de tinta `#EEEEEE` y el primer plano `#181818`. La misma relación permite que un botón principal tenga un relleno claro y un texto oscuro legible.

El texto atenuado `#585858` tiene poco contraste sobre los fondos de esta paleta. Conviene reservarlo para información de menor énfasis y revisar su legibilidad según el tamaño y el contexto. La presencia del token no garantiza el cumplimiento de criterios de accesibilidad en todas sus combinaciones; una instrucción esencial, un error o el contenido principal requieren mayor contraste.

### 3.3. Estados semánticos

| Variable CSS | Valor exacto | Recurso de Enki | Significado |
| --- | --- | --- | --- |
| `--danger` | `#FF6B6B` | `DangerColor` | Error, riesgo o acción destructiva. |
| `--success` | `#54AD7D` | `SuccessColor` | Confirmación o estado satisfactorio. |
| `--highlight` | `#D9A952` | `HighlightColor` | Resaltado y elementos que necesitan atención según su contexto. |

Estos colores se usan en señales concretas: iconos, texto, puntos, bordes o acciones. Su extensión debe mantener visible la jerarquía monocroma del conjunto. El estado se expresa también mediante texto, iconografía o posición para que su interpretación no dependa únicamente del color.

Las etiquetas, prioridades, capas del calendario, gráficos o contenidos de una nota pueden incorporar colores propios. Son información del dominio o del contenido; los tres colores semánticos del tema no constituyen un inventario exhaustivo de todos los colores posibles de la aplicación.

### 3.4. Bordes y opacidades derivadas

Enki incluye una rampa de pinceles `Ink05`, `Ink10`, `Ink15`, `Ink20`, `Ink30`, `Ink40`, `Ink50`, `Ink60`, `Ink70`, `Ink80` e `Ink90`. Los números indican el porcentaje de opacidad aplicado a `InkColor`. Con esta paleta, todos parten de `#EEEEEE`.

| Recurso o uso | Tratamiento |
| --- | --- |
| `Ink05` y `BorderSubtleBrush` | Tinta al 5 %, para límites muy suaves o fondos de interacción discretos. |
| `Ink10`, `BorderBrush1` y `GridSeamBrush` | Tinta al 10 %, habitual en separadores y contornos de tarjetas. |
| `Ink20` | Tinta al 20 %, empleada en el borde de los popovers compartidos. |
| `Ink40` | Tinta al 40 %, presente en microetiquetas e iconos de menor énfasis. |
| `ModalOverlayBrush` | Negro `#000000` al 15 %, oscurece el contenido bajo un diálogo. |
| `GraphChipBackgroundBrush` | Color de panel al 80 %, para controles flotantes del grafo. |

El resultado de una transparencia depende del fondo sobre el que se compone. `Ink10` no equivale a un gris hexadecimal fijo y `Ink40` no es el token `TextMutedColor`. La superposición negra de los diálogos es un recurso de la aplicación, independiente de las 19 variables del CSS.

## 4. Tipografía y tratamiento del texto

### 4.1. Familias

**Space Grotesk** es la familia base de ventanas y controles de usuario. Da forma a los títulos, la navegación y gran parte del texto de interfaz. Sus formas geométricas sostienen el carácter editorial de Enki.

**JetBrainsMono Nerd Font Mono** es la familia monoespaciada empaquetada. Se utiliza como base de las microetiquetas, las cifras y otros datos técnicos. La fuente de código dispone de un recurso independiente para poder cambiarla desde los ajustes del editor.

La aplicación distingue `DisplayFontFamily`, `InterfaceLabelFontFamily`, `ContentControlThemeFontFamily`, `MonoFontFamily` y `CodeFontFamily`. Por defecto, las tres primeras permiten coordinar el texto visible de la interfaz y sus controles; las etiquetas parten de JetBrains Mono. La elección de una fuente de interfaz puede sustituir también las etiquetas, mientras `MonoFontFamily` conserva el monoespaciado reservado. La tipografía del código se configura por separado.

### 4.2. Escala compartida

| Estilo | Tamaño | Peso | Espaciado entre letras | Tratamiento |
| --- | --- | --- | --- | --- |
| `page-title` | 34 | Bold | −1 | Título principal compacto y dominante. |
| `page-subtitle` | 13,5 | Heredado | 0,1 | Explicación secundaria con `TextSecondaryBrush`. |
| `eyebrow` | 10,5 | Bold | 4 | Microetiqueta de contexto con `InterfaceLabelFontFamily` e `Ink40`. |
| `section-title` | 12 | Bold | 1,5 | Encabezado de bloque. |
| Texto de `nav-item` | 11,5 | Bold | 1,5 | Nombre de sección en la navegación principal. |
| `faint` | 10,5 | Heredado | Heredado | Metadatos con `InterfaceLabelFontFamily` y `TextMutedBrush`. |
| `muted` | Heredado | Heredado | Heredado | Texto secundario mediante `TextSecondaryBrush`. |

Son valores de los estilos globales; las vistas pueden redefinir tamaños para su contexto. El peso de `page-title` en la aplicación es `Bold`, aunque la referencia web describa titulares con peso 900. Los tamaños de la web no deben trasladarse a la aplicación como si ya fueran valores de Avalonia.

### 4.3. Mayúsculas y legibilidad

Numerosos títulos de página, etiquetas, botones y opciones se presentan en mayúsculas mediante conversores de texto. El espaciado amplio ayuda a distinguir las microetiquetas, mientras que el titular utiliza letras más próximas.

El cuerpo de las notas, las conversaciones, los nombres de usuario y los textos explicativos mantienen su escritura natural. La transformación de una etiqueta de interfaz no debe alterar el contenido almacenado de una nota ni un identificador técnico.

En castellano se conservan las tildes y la letra ñ, también en mayúsculas. El contenido largo se distribuye en líneas o se recorta con puntos suspensivos según el contexto. Los títulos completos y las acciones deben seguir siendo identificables al cambiar de fuente o aumentar el zoom.

## 5. Geometría, separación y alineación

### 5.1. Esquinas y contornos

La geometría base es rectangular: `CornerRadius="0"`. Esto incluye las clases llamadas `pill`; su nombre técnico no significa que deban dibujarse como cápsulas redondeadas.

Los contornos de tarjetas y popovers son habitualmente de una unidad lógica. Los botones `pill` y `pill-primary` usan un borde de 1,5. Una selección de navegación puede reforzarse mediante una línea lateral de tres unidades.

Los círculos se reservan para elementos cuya función o representación es circular: puntos de estado, indicadores, selectores de color y geometría del grafo. El logotipo personalizado también contempla un recorte circular. Estas excepciones no cambian la geometría de los contenedores.

### 5.2. Espacio y retícula

La aplicación combina calles, márgenes y rellenos de distintos tamaños según la densidad necesaria. Las vistas contienen separaciones pequeñas para iconos y metadatos, rellenos intermedios para filas y tarjetas, y márgenes más amplios para las cabeceras de página. No hay evidencia de una única escala rígida que obligue a todos los módulos a usar el mismo múltiplo.

Entre las referencias concretas se encuentran el relleno 16 del popover general, el relleno 18 del popover de calendario, las tarjetas de estadísticas con relleno 22 y los márgenes laterales de 36 en la cabecera de Sesiones. Son decisiones locales dentro del mismo lenguaje.

Las tarjetas de navegación de notas y archivos y las de papelera comparten como referencia una caja de 224 × 150 con una separación de 16. Esa medida no se extiende a las tarjetas de Kanban, a los mensajes del asistente ni a cualquier otro bloque por el simple hecho de llamarse tarjeta.

### 5.3. Iconos y texto en una misma fila

Iconos, indicadores y etiquetas comparten el centro vertical calculado por el diseño de la fila. La altura real del texto depende de la fuente, el tamaño y el número de líneas; los iconos se alinean con esa geometría.

Los estilos globales aplican centrado a hijos de filas horizontales y de `Grid.icon-text-row`; `IconTextAlignment` participa en la alineación de los iconos. Se evita compensar una fuente concreta mediante desplazamientos arbitrarios que fallen cuando cambie la tipografía o el zoom.

## 6. Estructura de la ventana

### 6.1. Marco y navegación principal

La ventana principal tiene un mínimo declarado de 980 × 620. Los valores 1320 × 820 presentes como dimensiones de diseño sirven al diseñador visual y no representan un tamaño obligatorio de ejecución.

La composición sitúa una navegación principal vertical junto al área de trabajo. La barra lateral principal tiene 248 unidades cuando está desplegada y 76 cuando está plegada. En su cabecera aparece la marca Enki, con un icono de 24 × 24, el nombre y una pequeña línea de contexto; el logotipo puede personalizarse.

La barra lateral reúne el selector de vault, las secciones disponibles y el acceso a ajustes. En modo desplegado combina iconos con etiquetas; en modo plegado concentra la navegación en los iconos. Los elementos visibles y su orden pueden depender de la configuración.

La franja superior reserva 44 unidades en la composición del shell. Los accesos globales, los indicadores y los controles de ventana se presentan como una secuencia compacta de acciones con iconos. Búsqueda, navegación rápida, paleta de comandos, avisos y captura utilizan superficies emergentes coherentes con el resto de la aplicación.

### 6.2. Área de trabajo y paneles secundarios

El área principal cambia con la sección activa. Las páginas de resumen y gestión suelen comenzar con una microetiqueta, un título grande, una explicación y las acciones principales. El contenido aparece debajo en listas, retículas o paneles.

Las superficies de edición y conversación priorizan el espacio útil: pestañas, barras de herramientas, árbol o historial y documento activo sustituyen a una portada de tarjetas cuando corresponde.

Los paneles de Notas, Archivos, Habilidades y Asistente pueden situarse a la izquierda o a la derecha según la configuración. El borde y la dirección de los indicadores se adaptan para conservar una separación coherente con el contenido. Los paneles que admiten redimensionado muestran un límite que se puede arrastrar.

El asistente también puede abrirse en un panel junto a la sección actual. Este panel tiene su propia cabecera compacta de 48 unidades y un separador lateral. En la sección completa del asistente, el historial puede plegarse a un raíl de 52 unidades; este raíl es distinto de la navegación principal de 76.

## 7. Componentes e interacción

### 7.1. Botones

El botón secundario `pill` tiene fondo transparente, contorno de tinta, texto destacado y esquinas rectas. Su referencia global es una altura de 40, relleno horizontal de 20 y vertical de 11, y texto de tamaño 11 en negrita. Al pasar el puntero puede invertir su relación de fondo y contenido según el estilo.

El botón principal `pill-primary` parte de un relleno de tinta `#EEEEEE`, primer plano `#181818`, borde de 1,5 y relleno 18 × 10. La superficie clara le da prioridad en un entorno oscuro. Las acciones destructivas tienen variantes específicas que incorporan el color de peligro.

Las acciones secundarias conservan la clase `ghost` en las vistas, pero se presentan como botones rectangulares: borde discreto `Ink30`, relleno interior de 14 × 8 y altura mínima de 36. Hover y foco refuerzan el contorno con tinta plena. Al desactivarse conservan la forma y atenúan todo el control; el fondo de Fluent no debe quedar ceñido a la etiqueta. La variante `modal-cancel` mantiene las dimensiones de la acción principal. Las acciones compactas integradas en tarjetas y filtros declaran su altura y relleno locales, conservando el contorno sin provocar saltos de altura al aparecer.

Los botones `icon-btn` son acciones compactas para barras, cabeceras y controles auxiliares. Su estado se hace visible mediante el cambio de tinta o de fondo que corresponda a la clase. Deben contar con una identificación comprensible mediante etiqueta accesible o ayuda contextual cuando carezcan de texto visible.

### 7.2. Navegación, selección y pestañas

Una entrada de navegación normal utiliza texto secundario sobre fondo transparente. En la navegación desplegada, pasar el puntero introduce `Ink05`; la pulsación utiliza `Ink10`. La entrada activa combina `Ink10`, una línea lateral de tinta de tres unidades y texto e icono principales; el texto pasa a negrita.

La navegación plegada tiene variantes propias, por lo que no debe suponerse que todos sus detalles coinciden con los de la fila desplegada. En ambas disposiciones, la sección activa debe poder localizarse con rapidez.

Las pestañas y los selectores segmentados reutilizan la lógica de límites rectos, contraste e indicación de estado. El paso del puntero, el foco de teclado, la pulsación y la selección persistente son estados distintos; una vista nueva debe conservar esa distinción.

### 7.3. Tarjetas y filas

La tarjeta compartida `Border.card` utiliza fondo de panel `#101010`, borde `Ink10` de una unidad y radio cero. El relleno y el contenido se deciden en la vista. Las tarjetas que admiten interacción pueden añadir cambios de borde, fondo o desplazamiento mediante sus estilos específicos.

Las filas compactas suelen organizar icono o indicador a la izquierda, título y contexto en la zona central y metadatos o acciones al final. Los separadores mantienen la lectura de la lista sin convertir cada fila en un bloque de gran contraste.

Una tarjeta muestra primero su título o dato principal; etiquetas, fecha, procedencia y acciones forman niveles secundarios. El número de elementos visibles debe ajustarse al espacio disponible y al objetivo del módulo.

### 7.4. Campos, menús y ayudas

Los campos mantienen esquinas rectas, texto principal y fondos oscuros. La etiqueta, el contenido, el marcador de posición y los errores cumplen funciones diferentes. La selección de texto utiliza la inversión de tinta compartida.

Los controles de Fluent se integran mediante estilos y plantillas propios. El acento del tema alimenta sus estados para que los controles no introduzcan un acento ajeno a la paleta. Los estados de los botones también se ajustan en la plantilla que dibuja su contenido.

Los menús contextuales comparten radio cero; sus entradas tienen una altura mínima de 36 y tipografía de interfaz de tamaño 12 y peso seminegrita. Los menús, selectores y ayudas deben conservar su relación de escala con el contenido al aplicar zoom.

### 7.5. Popovers y diálogos

El popover general `Border.dropdown-popover` utiliza fondo de panel, borde `Ink20` de una unidad, radio cero y relleno 16. Su altura máxima compartida es 420, aunque algunas instancias declaran otro límite.

El popover de calendario `Border.calendar-popover` comparte fondo, borde y geometría, con relleno 18. Son dos patrones existentes que deben reutilizarse según el contexto, conservando la familia visual de las ventanas emergentes.

Los diálogos que bloquean la interacción se distinguen del contenido mediante la superposición negra al 15 %. El título, la explicación y las acciones deben exponer qué se está decidiendo. Las acciones de cancelación y confirmación han de reconocerse también por su texto.

## 8. Expresión del diseño por sección

### 8.1. Panel de inicio

El panel de inicio combina resúmenes, notas recientes y elementos de organización en una composición de bloques. Los indicadores pequeños, las cifras, los encabezados y las listas compactas facilitan reconocer la actividad relevante. Su distribución es configurable, por lo que el diseño debe funcionar con diferentes combinaciones de bloques.

Las notas recientes combinan título y fecha o contexto. Las tareas utilizan indicadores de prioridad y vencimiento. Los elementos de agenda pueden incluir una franja del color de su capa, un icono de origen y la hora. El marco general sigue siendo monocromo aunque esos datos tengan color propio.

### 8.2. Notas y editor

La vista completa de Notas ofrece un navegador de tarjetas con carpetas y filtros cuando no hay una nota abierta. Al abrir una nota, la barra de pestañas y el editor pasan a ocupar el área de trabajo. La vista simplificada organiza la navegación y la edición mediante un panel lateral más permanente.

El editor da prioridad al documento sobre el marco de la aplicación. El fondo deriva de `AppBackgroundAltColor`, aquí `#0A0A0A`. Las herramientas, pestañas y paneles auxiliares mantienen bordes discretos y controles compactos.

La jerarquía del Markdown pertenece al contenido: títulos, párrafos, listas, tareas, citas, enlaces, tablas, código, imágenes, fórmulas y diagramas necesitan tratamientos reconocibles. Los títulos de una nota no heredan automáticamente la escala del titular de una página de gestión. El código dispone de su fuente configurable y puede incorporar resaltado sintáctico.

Las etiquetas, propiedades, adjuntos, esquema y enlaces relacionados se presentan como contexto del documento. Deben ayudar a orientarse sin desplazar el foco visual de la lectura y la escritura.

### 8.3. Archivos y habilidades

Archivos combina navegación por carpetas, tarjetas o filas y previsualización. Comparte el lenguaje del navegador de Notas: límites rectos, iconos de tipo, títulos claros y metadatos en segundo plano. La previsualización adapta el espacio a documentos, imágenes y medios.

Habilidades reutiliza la navegación de biblioteca, con árbol, navegador, detalle y editor según la tarea. La información técnica y los archivos asociados se organizan con etiquetas y bloques discretos. El estado de instalación puede expresarse mediante indicadores y acciones específicas sin crear una identidad visual independiente.

### 8.4. Kanban y tareas

Kanban dispone las tarjetas por columnas de estado. Las cabeceras, contadores, límites de columna y espacios entre tarjetas construyen la retícula. Las tarjetas priorizan el título y reservan un nivel secundario para etiquetas, prioridad, vencimiento, enlaces y relaciones.

Los indicadores de bloqueo o dependencia necesitan una señal comprensible junto al contenido. La vista de lista organiza las mismas tarjetas en filas y mantiene sus filtros. El cambio de representación conserva el significado de los estados y de las acciones.

Las tareas y subtareas procedentes de Polar deben integrarse con el mismo tratamiento de filas, fechas, casillas y metadatos cuando aparecen en los resúmenes o en la agenda. La procedencia se identifica sin imponer otro lenguaje visual al conjunto.

### 8.5. Calendario y recordatorios

El calendario se estructura con una barra de navegación temporal, selectores de vista, filtros o capas y una retícula o listado de eventos. Las líneas finas delimitan las unidades temporales; las fechas, las horas y los indicadores sitúan cada elemento.

Las capas pueden tener colores configurables y los eventos conservar señales de su origen. Un color de capa no debe confundirse por sí solo con peligro, éxito o selección. Los popovers de detalle y edición mantienen el fondo de panel y el borde del sistema.

Los recordatorios y las notificaciones se leen como listas de eventos: título, contexto, fecha y acciones cercanas. Los estados próximo, vencido, completado o aplazado necesitan texto o iconos además del tono correspondiente. Los estados vacíos y los filtros sin coincidencias deben distinguirse de una pantalla todavía en carga.

### 8.6. Sesiones y grafo

Sesiones combina una lista de contextos con una superficie de grafo. El grafo representa notas y relaciones mediante volúmenes, nodos, líneas y variaciones de profundidad. La geometría circular de estas representaciones corresponde a la visualización de datos.

El fondo compartido del grafo incluye una trama de puntos cada 26 unidades y un halo radial de tinta de opacidad baja. Los controles flotantes emplean superficies parcialmente opacas para seguir siendo legibles sobre el grafo. Esta textura está localizada en la visualización y no obliga a cubrir todas las pantallas con ruido o degradados.

Los controles de zoom y rotación, la selección de sesión y los estados de carga deben integrarse con la misma tipografía, los iconos y las superficies del resto de Enki. Existen representaciones web y nativa, que deben mantener la coherencia del contenido y del marco.

### 8.7. Asistente

El asistente se organiza como un espacio de conversación: historial, cabecera con contexto y controles, transcripción y compositor. La superficie completa ofrece más espacio para el historial; el panel lateral permite mantener visible la sección en la que se trabaja.

Los mensajes conservan una lectura natural. Las herramientas utilizadas, los permisos, las decisiones pendientes, los errores y los cambios propuestos aparecen en bloques vinculados a la respuesta. El código y las diferencias de contenido requieren tipografía y señales propias sin abandonar las superficies y los bordes compartidos.

El compositor reúne entrada de texto, adjuntos y controles de envío o cancelación. Los indicadores de actividad y respuesta lista permiten entender el estado incluso cuando la conversación está fuera de la vista. El historial plegado conserva las acciones esenciales en su raíl de 52 unidades.

### 8.8. Hábitos y estadísticas

Hábitos utiliza tarjetas o filas con nombre, frecuencia, progreso y controles de registro. Las estadísticas combinan cifras destacadas, microetiquetas, tablas o gráficos y separadores. Los datos deben poder compararse sin que la ornamentación compita con ellos.

Los gráficos, mapas de actividad y progresos pueden añadir intensidad o color para representar valores. Sus ejes, etiquetas, leyendas y contenedores mantienen la tipografía técnica y la paleta estructural de la aplicación.

### 8.9. Ajustes, gestores y papelera

Ajustes organiza las opciones en secciones identificables, con etiquetas, explicaciones, campos y acciones próximas. El editor de temas presenta los colores como valores configurables dentro de una estructura común; cambiar la paleta no cambia por sí mismo la geometría del sistema.

Los gestores de carpetas y etiquetas conservan la estructura de listas y formularios. La papelera reutiliza una geometría de tarjeta compartida para evitar tamaños inconexos entre tipos de elementos. Restaurar y eliminar definitivamente deben diferenciarse por su texto y por el tratamiento semántico de sus acciones.

### 8.10. Inicio y captura rápida

Las pantallas de arranque y configuración inicial mantienen la marca, los contrastes y las esquinas del sistema mientras orientan al usuario hacia la elección de idioma o vault. La captura rápida concentra la entrada y el destino del contenido en una superficie breve, con la misma lógica de campos y confirmación.

## 9. Iconografía, marca y texturas

La interfaz utiliza `Material.Icons.Avalonia`. Son habituales los iconos de contorno, con tamaños de alrededor de 14–20 unidades en las acciones compactas y variaciones según su función. Los iconos participan de la jerarquía de tinta del texto: secundario en reposo, principal o semántico cuando el estado lo requiere.

La marca aparece mediante el nombre ENKI y sus recursos de icono. La posibilidad de elegir un logotipo personalizado no autoriza a cambiar los radios, colores o tipografía de los demás controles.

La aplicación dispone de un recurso de grano vectorial `GrainBrush`, con opacidad 0,05. Su disponibilidad no implica que se dibuje sobre todas las vistas. Las texturas se describen y aplican según el componente que las consume.

La referencia web contiene recursos y efectos propios de una página, como cabeceras con desenfoque, imágenes en escala de grises y entradas al hacer scroll. Esos efectos no deben darse por presentes en Enki si la vista de escritorio no los implementa. La identidad compartida se conserva mediante retícula, tipografía, bordes, contraste y geometría.

## 10. Movimiento y respuesta visual

El movimiento se define mediante `MotionService` y recursos consumidos por los estilos. La curva `CubicEaseOut` aparece en las transiciones y en las entradas de contenido.

| Propiedad | Perfil habitual | Animaciones sutiles |
| --- | --- | --- |
| Duración de interacción | 150 ms | 90 ms |
| Duración de entrada | 180 ms | 100 ms |
| Desplazamiento de entrada | 6 unidades | 0 |
| Elevación de tarjeta que usa `MotionCardLift` | −4 unidades | 0 |
| Elevación pequeña que usa `MotionSmallLift` | −1 unidad | 0 |

Los valores describen los recursos actuales; solo afectan a los componentes que los utilizan. El perfil sutil elimina esos desplazamientos y acorta las transiciones, manteniendo la estructura de la pantalla.

Algunos comentarios históricos de los estilos mencionan 300 ms. La referencia vigente para estos recursos es `MotionService`, cuyos valores son los de la tabla. Las animaciones particulares del grafo o de un indicador de carga tienen su propio comportamiento y deben revisarse en su módulo.

El objetivo del movimiento es confirmar una interacción, revelar contenido o expresar actividad. El estado final debe seguir siendo comprensible cuando los desplazamientos se desactivan.

## 11. Adaptación, accesibilidad y personalización

La aplicación adapta el espacio mediante paneles plegables, controles redimensionables, ajustes de disposición y desplazamiento. Su ventana de escritorio tiene un mínimo declarado; no debe describirse como una interfaz móvil sin comprobar una implementación específica.

El zoom modifica la escala de la interfaz. Los popups compartidos utilizan la capa superpuesta para conservar su relación con el árbol visual escalado. Menús, ayudas, filas y botones deben seguir siendo utilizables con fuentes distintas, tamaños mayores y etiquetas de varias líneas.

El foco de teclado debe distinguirse de la selección y del paso del puntero. Los iconos sin texto necesitan nombres accesibles; las acciones de riesgo requieren un texto claro; los errores deben permanecer localizables. Una reducción de opacidad puede indicar deshabilitación, pero no debe ser la única explicación de por qué una acción no está disponible.

Los temas claro y oscuro y las paletas personalizadas comparten recursos semánticos. Este documento fija los valores de Better Dark Dev para la descripción solicitada; no define una paleta clara equivalente ni presupone que todos los usuarios utilicen los mismos colores.

El CSS de temas es un formato de intercambio de colores: el catálogo reconoce exactamente las 19 variables enumeradas en las tablas anteriores. El analizador requiere el bloque `:root`, valida los nombres y los valores hexadecimales y rechaza una importación incompleta o con variables desconocidas. No funciona como una hoja de estilos capaz de cambiar tipografías, medidas o esquinas de Avalonia.

## 12. Criterios para ampliar el diseño

Una pantalla nueva debe poder reconocerse como parte de Enki por su composición incluso antes de leer el título. Para ello:

1. Asignar los colores mediante los recursos semánticos y mantener los valores de Better Dark Dev cuando se reproduzca esta paleta.
2. Conservar contenedores rectos, bordes finos y separación tonal entre fondo, panel y contenido.
3. Usar los estilos tipográficos compartidos y una jerarquía clara entre título, explicación y metadatos.
4. Elegir el patrón existente de botón, tarjeta, fila, panel o popover que corresponda a la tarea.
5. Definir reposo, paso del puntero, pulsación, foco, selección, deshabilitación y error cuando sean aplicables.
6. Mantener centrados iconos y texto sin compensaciones ligadas a una fuente concreta.
7. Prever contenido largo, estados vacíos, carga, errores, aumento de zoom y paneles plegados.
8. Reservar los colores semánticos para su significado y acompañarlos con señales comprensibles.
9. Utilizar el perfil de movimiento compartido en los componentes que deban animarse.
10. Verificar la legibilidad de los grises atenuados sobre el fondo real antes de utilizarlos para información necesaria.

## 13. Fuentes dentro del repositorio

Las siguientes referencias permiten contrastar y actualizar esta descripción:

| Fuente | Información que aporta |
| --- | --- |
| [better-dark-dev.css](../resources/better-dark-dev.css) | Las 19 variables y los valores exactos de la paleta descrita. |
| [Colors.axaml](../../enki/src/enki/styles/Colors.axaml) | Recursos, pinceles, opacidades, fuentes, selección, grano y fondo del grafo. |
| [Styles.axaml](../../enki/src/enki/styles/Styles.axaml) | Tipografía, geometría y estados de los componentes compartidos. |
| [ControlThemes.axaml](../../enki/src/enki/styles/ControlThemes.axaml) | Integración visual de controles y plantillas. |
| [SidePanels.axaml](../../enki/src/enki/styles/SidePanels.axaml) | Adaptación del borde y los indicadores al lado del panel. |
| [App.axaml](../../enki/src/enki/App.axaml) | Carga de estilos, recursos e iconografía. |
| [ThemeCatalog.cs](../../enki/src/enki/services/ThemeCatalog.cs) | Correspondencia entre variables CSS y recursos de color. |
| [ThemeCssParser.cs](../../enki/src/enki/services/ThemeCssParser.cs) | Validación del formato de importación de colores. |
| [ThemeRegistry.cs](../../enki/src/enki/services/ThemeRegistry.cs) | Aplicación de colores y derivación del acento de Fluent. |
| [MotionService.cs](../../enki/src/enki/services/MotionService.cs) | Duraciones, desplazamientos y perfil sutil. |
| [MotionReveal.cs](../../enki/src/enki/views/behaviors/MotionReveal.cs) | Entrada animada de contenido. |
| [IconTextAlignment.cs](../../enki/src/enki/views/behaviors/IconTextAlignment.cs) | Alineación de iconos respecto al texto. |
| [MainWindow.axaml](../../enki/src/enki/views/shell/MainWindow.axaml) y [ShellView.axaml](../../enki/src/enki/views/shell/ShellView.axaml) | Ventana, navegación, acciones globales y panel auxiliar. |
| [MainWindowViewModel.cs](../../enki/src/enki/viewmodels/shell/MainWindowViewModel.cs) | Anchuras de la navegación principal. |
| [Vistas de la aplicación](../../enki/src/enki/views/) | Composición de cada sección y ajustes locales de los componentes. |
| [Fuentes empaquetadas](../../enki/src/enki/assets/fonts/) | Familias tipográficas incluidas en la distribución. |
| [Sistema de diseño de referencia](../project-info/sistema-diseno-web.md) | Origen y criterios del lenguaje editorial compartido con la web. |

Cuando una referencia histórica difiera de la implementación, los recursos y las vistas actuales determinan el comportamiento de Enki. Para los colores de este documento, la referencia específica es `better-dark-dev.css`.
