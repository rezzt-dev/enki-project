# Justificación inteligente del editor Markdown

Implementación: 16 de septiembre de 2026. Avalonia 12.1.0 y AvaloniaEdit 12.0.0.

## Objetivo y alcance

Los párrafos normales se justifican por defecto cuando sus filas visuales admiten una expansión suave. La última fila de cada línea real del documento conserva su ancho natural, incluso cuando no hay un salto final. Cada salto escrito termina el párrafo a estos efectos: no se concatenan líneas Markdown ni se modifica el parser.

La mejora es exclusivamente visual. No añade espacios al `TextDocument`, no modifica el guardado, el portapapeles ni el historial de deshacer. Las listas y citas conservan su sangría francesa; los encabezados, bloques de código, tablas e imágenes conservan su layout anterior. Las citas mantienen además el cambio previo del proyecto que elimina su cursiva, tanto en el colorizador como en la medición.

## Arquitectura analizada

- `EditorView` crea el `TextEditor`, aplica fuente, tamaño, ancho de columna, scrollbars y preferencias, y conecta los eventos de edición.
- `MarkdownLivePreviewColorizer` transforma los estilos de los elementos visuales sin sustituir el documento.
- `MarkdownElementGenerator` oculta marcadores y produce los elementos de listas, imágenes, tablas y alineaciones HTML. Su `StartGeneration` registra cada `VisualLine` en el formateador.
- `MarkdownBlockTextFormatter` ya sustituía el formateador de **ese TextView**, mediante el campo privado `_formatter`, para resolver las sangrías de listas y citas. Este sigue siendo el único acceso por reflexión; no se añade otro ni se cambia el formateador global.
- `MarkdownQuoteLayout`, `MarkdownBackgroundRenderer` y los componentes de tablas conservan sus responsabilidades.
- `MarkdownAlignmentLayout` sigue atendiendo las alineaciones HTML explícitas mediante su mecanismo existente. La justificación automática no interviene en esos bloques.

`TextView.BuildVisualLine` entrega a `FormatLine` el ancho disponible después de distribuir los márgenes. Ese `paragraphWidth` es la autoridad para esta función: no se calcula desde la ventana ni se resta manualmente el ancho de la numeración o del scrollbar.

## Integración y ciclo de vida

`MarkdownBlockTextFormatter.Register` utiliza el modelo de línea que ya analizaba y registra los párrafos elegibles en una `ConditionalWeakTable<VisualLine, MdLine>`. No retiene documentos cerrados ni mantiene un segundo índice global de párrafos. El reemplazo de documento sigue reinstalando el formateador mediante el mecanismo anterior.

Para un párrafo registrado:

1. El formateador nativo decide el wrap y devuelve una fila con su shaping y su salto originales.
2. `MarkdownParagraphJustification.Format` evalúa exclusivamente esa fila.
3. Si no cumple los criterios, se devuelve el mismo `TextLine` nativo sin reconstruirlo.
4. Si cumple, se copian sus `ShapedBuffer`, respetando texto, tipografía, nivel bidi, índices y clusters de glifos y offsets de posicionamiento.
5. Se amplían únicamente los avances de los glifos que representan espacios admisibles.
6. Se entregan los runs formados al formateador nativo con `NoWrap`. No se repite el shaping ni se permite que el espaciado visual cambie los saltos.
7. El `WrappedRow` existente delega dibujo, caret, hit testing y selección en la fila resultante, y conserva el `TextLineBreak`, longitud y semántica del salto original. Libera ambas filas al desecharse.

Copiar los buffers es esencial: Avalonia puede compartir su almacenamiento entre la caché, la fila actual y los runs pendientes de la siguiente. Cambiar el buffer original corrompería el espaciado de otras filas y de posteriores layouts.

Se utilizan APIs públicas de `TextFormatter`, `ShapedBuffer`, `ShapedTextRun` y `TextLine`; no se accede al `TextLineImpl` interno ni se implementa un dibujo con coordenadas distintas a las del editor.

## Criterio tipográfico

Los umbrales están centralizados en `MarkdownParagraphJustification`:

| Criterio | Valor |
| --- | --- |
| Ocupación natural mínima | 85 % del ancho disponible |
| Espacios internos expandibles mínimos | 3 |
| Expansión máxima por espacio | 0,20 em de la fuente de ese run |
| Expansión máxima relativa | 65 % del avance natural del espacio |

Se aplican los dos límites de expansión simultáneamente. Si un solo espacio superaría su límite, toda la fila mantiene su alineación natural; no se rellenan parcialmente filas ni se deforman palabras.

Solo se expande el espacio ordinario U+0020 entre caracteres no blancos. Esto es una decisión tipográfica, no una suposición sobre la codificación del texto: se preservan los clusters Unicode ya formados, combinantes, pares sustitutos y niveles bidi. Los espacios no separables, espacios repetidos y espacios finales no se expanden. Los idiomas sin espacios entre palabras conservan su espaciado natural; no se introduce justificación entre ideogramas ni elongación árabe.

El código inline mantiene sus espacios literales. También se protegen los delimitadores y destinos de enlaces cuando se revelan para editarlos. Negrita, cursiva, etiquetas visibles de enlaces y otros estilos mantienen sus runs, decoraciones y propiedades; los espacios de prosa a su alrededor pueden expandirse.

## Exclusiones deliberadas

- Última fila de una línea real, párrafos de una sola fila y saltos obligatorios del formateador.
- `NoWrap`, ancho no finito, filas desbordadas y geometría con sangría explícita.
- Líneas con sangría inicial o tabuladores; `WordWrapIndentation` personalizado distinto de cero.
- Encabezados, listas, tareas, citas, reglas horizontales y bloques de código reconocidos por el parser existente.
- Filas de tablas detectadas, incluidas sus representaciones activas y plegadas.
- Párrafos que contienen imágenes y bloques con alineación HTML explícita, incluida `left`.

Estas exclusiones priorizan la geometría existente. Ampliar el alcance a citas o listas requeriría justificar dentro de su ancho útil antes de posicionar sus filas y extender las pruebas de prefijos; no basta con activar `TextAlignment.Justify`.

## Preferencia y persistencia

`AppSettings.EditorJustifyParagraphs` vale `true`, también al deserializar un archivo antiguo que no contiene esa clave. El arranque de `SettingsViewModel` lo aplica a `EditorPreferences.JustifyParagraphs`. Su evento `Changed` utiliza la actualización habitual de los editores abiertos: `ApplyWordWrap` copia el valor a `LivePreviewState` y el refresco existente reconstruye las líneas.

No se añade una página ni un interruptor visible. Para desactivarlo persistentemente, se puede cambiar `EditorJustifyParagraphs` a `false` en `settings.json` con la aplicación cerrada. Una futura opción de interfaz debe guardar el valor en `AppSettings` y actualizar `EditorPreferences`, como hace `EditorWordWrap`.

## Redimensionamiento y rendimiento

Fuente, tamaño, zoom, ancho de columna, paneles, márgenes, numeración y scrollbars siguen invalidando el layout por los mecanismos existentes. Los nuevos cálculos usan el ancho recibido en cada formateo; no hay caché adicional de anchos, timers ni redibujados diferidos específicos de la justificación.

El coste añadido es proporcional a los glifos de cada fila construida por AvaloniaEdit. Las filas descartadas por las condiciones iniciales no crean listas de espacios ni buffers. Las candidatas recorren sus runs; solo las aceptadas copian los buffers y forman una fila adicional sin shaping. No hay escaneo de todo el documento por esta función ni trabajo añadido por frame. Los escaneos de fences, HTML y tablas siguen siendo los que ya existían.

## Pruebas y reproducción

El [documento de prueba](fixtures/justificacion-markdown.md) conserva el ejemplo solicitado y añade casos de estrés para repetir la revisión manual.

`enki/tests/enki.UITests/MarkdownParagraphJustificationUiTests.cs` comprueba el pipeline real de AvaloniaEdit en la plataforma headless del proyecto:

- Alineación de filas intermedias, mismos puntos de wrap y última fila intacta.
- Párrafos cortos, pocas palabras, palabra larga, URL, CJK, NBSP y espacios repetidos.
- Encabezados, listas, tareas, citas anidadas, código, sangrías, HTML y tablas.
- Código inline con espacios, negrita, cursiva y enlaces.
- Conversión caret → coordenada → hit, límites de selección y navegación entre caracteres.
- Cambios de ancho, fuente/tamaño, numeración, wrap, sustitución de documento y undo/redo, sin acumular expansión.
- Escritura, Enter, Backspace, Delete, Home/End, Ctrl+flechas, selección con Shift, copia/pegado, clic, doble/triple clic y selección arrastrada.

`SettingsServiceTests.ParagraphJustification_OldSettingsEnableIt_AndExplicitChoicePersists` verifica el valor predeterminado de archivos anteriores y la persistencia de la desactivación usando un directorio temporal.

```bash
dotnet test enki/tests/enki.Tests/enki.Tests.csproj
dotnet test enki/tests/enki.UITests/enki.UITests.csproj
```

Se contrastó también el documento de ejemplo de la petición en una ventana de escritorio con Space Grotesk y JetBrainsMono Nerd Font Mono, mediante el mismo generador, colorizador, fondo y formateador del proyecto. Se inspeccionaron capturas a distintos anchos y se ejercitó la escritura y navegación con eventos de teclado y ratón. La plataforma headless complementa esta inspección con comprobaciones numéricas; no sustituye una revisión visual de todas las fuentes del sistema.

## Publicación para Arch Linux

La salida es `Release`, `linux-x64`, autocontenida, sin trimming:

```bash
dotnet publish enki/src/enki/enki.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false \
  -o /tmp/enki-release-justificacion
```

La ruta del contenido `default-skills` en `enki.csproj` se actualiza a `.docs/project-info/ia-skills/default-skills/*.md`, siguiendo la reorganización de documentación realizada durante esta tarea. No actualizarla habría producido una publicación sin las skills de serie.

El arranque de Release se comprobó con `XDG_CONFIG_HOME` temporal. Tanto la versión anterior instalada como la nueva emitieron los mismos siete avisos iniciales de bindings y el timeout de accesibilidad AT-SPI del entorno, sin excepciones no controladas; no son una regresión de esta modificación.

La instalación conserva `/home/rezzt/.local/share/enki/enki`, el enlace `/home/rezzt/.local/bin/enki` y la entrada existente del menú KDE. Deben instalarse también las bibliotecas nativas y `default-skills`. Se conserva la configuración local instalada y se respalda la versión anterior antes del reemplazo. El almacenamiento personal de notas y ajustes no forma parte del despliegue.

## Resultado de validación e instalación

- 1.457 pruebas unitarias superadas y 224 pruebas de interfaz superadas, sin fallos ni omisiones; 28 casos nuevos en total.
- Publicación Release completada sin errores ni avisos de compilación.
- Instalados y verificados por SHA-256 los siete archivos de la publicación, incluidos ejecutable, símbolos, cuatro bibliotecas nativas y skill de escritura.
- Respaldo: `/home/rezzt/.local/share/enki/backups/justificacion-20260916-122213/`.
- Ejecutable instalado: `/home/rezzt/.local/share/enki/enki`.
- SHA-256 del ejecutable: `537addb52420388492d346e7baf759de00977a2fd3430ed17eccb93eb83c7f1d`.
- Conservados `local.properties`, los datos personales, el enlace del comando `enki` y el lanzador KDE.

La reorganización concurrente del repositorio mueve la documentación anterior a `.docs/project-info/`, los recursos a `.docs/resources/` y las skills de desarrollo a `.docs/agents-skills/`; también incorpora guías de commits y releases. Estos movimientos se respetan y la referencia de compilación a la skill de escritura se adapta al destino real.

## Referencias del motor y mantenimiento

La investigación se contrastó con el código de las versiones instaladas:

- [AvaloniaEdit 12.0.0: TextView y construcción de filas](https://github.com/AvaloniaUI/AvaloniaEdit/blob/12.0.0/src/AvaloniaEdit/Rendering/TextView.cs).
- [Avalonia 12.1.0: TextFormatterImpl, consumo de runs formados y wrapping](https://github.com/AvaloniaUI/Avalonia/blob/12.1.0/src/Avalonia.Base/Media/TextFormatting/TextFormatterImpl.cs).
- [Avalonia 12.1.0: ShapedBuffer y almacenamiento compartido](https://github.com/AvaloniaUI/Avalonia/blob/12.1.0/src/Avalonia.Base/Media/TextFormatting/ShapedBuffer.cs).
- [Avalonia 12.1.0: justificación nativa](https://github.com/AvaloniaUI/Avalonia/blob/12.1.0/src/Avalonia.Base/Media/TextFormatting/InterWordJustification.cs).

El motor nativo distribuye espacios usando oportunidades de salto Unicode, incluidas algunas dentro de palabras con puntuación y entre ideogramas. Esta función limita deliberadamente la expansión a espacios de prosa y protege el código inline. Al actualizar Avalonia/AvaloniaEdit, ejecutar las pruebas geométricas, revisar la propiedad de los buffers y verificar el único acceso privado existente a `_formatter`.
