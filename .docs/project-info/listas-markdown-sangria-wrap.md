# Sangría de continuación de listas Markdown

## Causa y punto de integración

El editor usa AvaloniaEdit 12.0.0 sobre Avalonia 12.1.0. `MarkdownElementGenerator`
reemplaza los bullets y las casillas por elementos visuales; `MarkdownLivePreviewColorizer`
aplica los estilos antes de crear las filas de texto. El documento no cambia.

`TextView.BuildVisualLine` calcula la sangría mediante `IsWhitespace`, pero el formateador
de Avalonia no consume `TextParagraphProperties.Indent`. Además, los números y las casillas
no forman parte de la secuencia inicial de espacios que reconoce AvaloniaEdit.

Fuentes de las versiones inspeccionadas:

- [TextView de AvaloniaEdit 12.0.0](https://github.com/AvaloniaUI/AvaloniaEdit/blob/12.0.0/src/AvaloniaEdit/Rendering/TextView.cs)
- [TextFormatterImpl de Avalonia 12.1.0](https://github.com/AvaloniaUI/Avalonia/blob/12.1.0/src/Avalonia.Base/Media/TextFormatting/TextFormatterImpl.cs)
- [TextLineImpl de Avalonia 12.1.0](https://github.com/AvaloniaUI/Avalonia/blob/12.1.0/src/Avalonia.Base/Media/TextFormatting/TextLineImpl.cs)

## Implementación

`MarkdownListTextFormatter` decora exclusivamente el formateador del `TextView` que tiene
el generador Markdown. Registra las listas con el parser existente, incluyendo su contexto
de cercas de código. Una tabla débil por `VisualLine` guarda el inicio del contenido y su X;
no existe una caché persistente por número de línea ni se retienen documentos cerrados.

1. La primera fila se formatea igual que antes. Se convierte `ContentStart` a columna visual
   y se mide su X con `GetDistanceFromCharacterHit`, después de reemplazos y estilos.
2. Cada continuación se parte con el ancho disponible menos esa X.
3. Se mide la fila ya partida sin wrap y se alinea a la derecha en una caja cuyo ancho es
   exactamente el ancho medido más la sangría. La caché de runs evita repetir el shaping
   entre esta medida y su colocación. Esta alineación interna deja el inicio en la X deseada;
   no es una alineación a la derecha del párrafo del usuario.
4. Se devuelve la geometría nativa resultante, conservando el `TextLineBreak` de la primera
   operación. Dibujo, caret, límites de selección y hit-testing usan esa misma línea nativa.
   Los anchos expuestos a AvaloniaEdit incluyen `Start`, porque los usa como borde derecho
   para calcular scroll y espacio virtual.

Si el prefijo no cabe en la primera fila, se mide completo sin wrap. Se mantiene su sangría,
aunque un ancho menor que el propio prefijo necesariamente deja contenido fuera del viewport.
No se aplica el límite de media columna que tiene el algoritmo original de AvaloniaEdit.

No se añaden caracteres, saltos, márgenes de párrafo ni transformaciones de dibujo.
Cambiar el documento, el ancho, la fuente, el zoom o el estado de previsualización reconstruye
las líneas y sus medidas. Las líneas que no son listas pasan al formateador original.

## Dependencia de versión

AvaloniaEdit no publica un setter para su formateador. El acceso por reflexión a `_formatter`
queda aislado en esta clase y se comprueba al generar líneas, porque cambiar de documento
restablece ese campo. No se cambia el formateador global ni se copia la biblioteca.
Al actualizar AvaloniaEdit hay que revisar este punto y ejecutar las pruebas de layout;
si cambia el campo, la integración falla explícitamente en lugar de ignorar la sangría.

El coste adicional se limita a las continuaciones de listas: se vuelve a medir cada fila
con sus runs y estilos y se conserva el salto original. No hay bucles de redibujado para
estabilizar el wrap ni modificaciones del modelo persistido.

## Verificación

`MarkdownListWrapUiTests` usa el pipeline de generación/colorización en una ventana headless:

- `-`, `*`, `+`, listas numeradas de varias cifras y con `)`, tareas y listas anidadas.
- Espacios y tabuladores, fuentes y tamaños distintos, cambios de ancho y palabras sin espacios.
- Sintaxis inline oculta y visible, selección, X de las continuaciones, límites de selección,
  conversión de coordenadas de ratón a offsets y espacio virtual al final de la línea.
- Texto y número de líneas intactos, sin entradas en undo; selección del documento completo.
- Cambio de documento y desactivación de WordWrap.
- Párrafos normales, indentados y listas aparentes dentro de código con el layout original.

Estas son pruebas automatizadas de geometría e interacción programática, no una sesión manual
con ratón y teclado en un escritorio gráfico.
