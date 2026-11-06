# Tablas en las notas

> **Alcance**: qué se reconoce como tabla Markdown, cómo la dibuja el live preview del editor, cómo se
> edita y cómo sale en el HTML exportado.
>
> **Fuera de alcance**: el resto del live preview (encabezados, listas, código, imágenes, alineación
> HTML), documentado en `docs/imagenes-en-notas.md` y en el propio código de `views/editor/Markdown/`.
>
> **Fuente de verdad**: `views/editor/Markdown/MarkdownTableParser.cs`,
> `views/editor/Markdown/MarkdownTableLayout.cs`, `views/editor/Markdown/MarkdownTableFolding.cs`,
> `views/editor/Markdown/MarkdownElementGenerator.cs`,
> `views/editor/Markdown/MarkdownBackgroundRenderer.cs`,
> `views/editor/Markdown/MarkdownLivePreviewColorizer.cs`, `views/editor/EditorView.axaml.cs` y
> `services/NoteExportRenderer.cs`.

## 1. Qué se reconoce como tabla

La sintaxis es la de GitHub Flavored Markdown, que es la que usan Obsidian, GitHub y prácticamente
cualquier editor con el que la nota vaya a compartirse:

```markdown
| Plan    | Precio | Usuarios  |
| ---     | ---:   | :---:     |
| Gratis  | 0 €    | 1         |
| Equipo  | 12 €   | Hasta 10  |
```

Reglas del reconocimiento (`MarkdownTableParser.Scan`):

- **Hacen falta dos líneas**: una fila de encabezados y, justo debajo, una **fila separadora** cuyas celdas
  sean solo guiones con dos puntos opcionales (`---`, `:---`, `---:`, `:---:`). Sin separadora válida no
  hay tabla: `a | b` seguido de `c | d` es texto normal, y tiene que seguir siéndolo.
- La separadora debe declarar **exactamente tantas columnas como celdas tiene el encabezado**. Es la regla
  de GFM y es lo que evita que dos líneas cualesquiera con barras se conviertan en tabla por accidente.
- Las **barras exteriores son opcionales**: `a | b` y `| a | b |` son la misma fila de dos celdas.
- Una **barra escapada** (`\|`) es contenido, no un separador.
- La tabla **termina** en la primera línea en blanco, en una línea sin barras, o en una que empiece otro
  bloque (encabezado, cita, lista). Dentro de una cerca de código no se reconoce nada: allí todo es texto
  literal.
- Los **dos puntos de la separadora** fijan la alineación de cada columna (izquierda, centro, derecha), y
  se respetan tanto al dibujar como al exportar.
- Una fila con **más celdas que el encabezado ensancha la tabla** en vez de perder la celda de más. Un
  renderizador GFM estricto la recortaría; aquí el criterio es no esconder texto que el usuario escribió.
  Una fila con menos celdas se rellena con celdas vacías.

El escaneo del documento entero se rehace con el mismo *debounce* que las cercas de código
(`EditorView.RecomputeFences`), porque como ellas es una pregunta que no se puede contestar mirando una
línea suelta. El troceado en celdas de cada fila, en cambio, se rehace **por línea en cada repintado**
(`MarkdownTableParser.SplitCells`), para que lo que se dibuja salga siempre del texto actual y nunca de un
escaneo que va un par de pulsaciones por detrás.

## 2. Cómo se dibuja

AvaloniaEdit es un editor de texto: no tiene un concepto de "tabla" que pintar, ni un control de rejilla
que insertar. Lo que hay es una línea de texto por fila, así que la tabla se construye con las tres capas
que ya usa el resto del live preview, cada una haciendo su parte de la misma geometría
(`MdTableGeometry` — una sola cuenta, compartida, para que la línea vertical no caiga nunca encima de una
letra):

1. **`MarkdownTableLayout`** mide cada celda con la tipografía real (ya sin los marcadores que se ocultan,
   y en seminegrita si es del encabezado) y decide el ancho de cada columna: el de su celda más ancha, más
   un hueco interior a cada lado. **Una tabla ocupa siempre el ancho de la columna de texto**, nunca el de
   su contenido: el espacio sobrante se reparte entre las columnas en proporción a lo que ocupa cada una
   —igual que hace un navegador con un `<table>` al 100 % de ancho, de modo que la columna de las notas
   largas se lleva el hueco y la del "sí/no" se queda estrecha— y el resto de la división va a la última
   columna, para que el borde derecho caiga al píxel donde acaba el texto de cualquier párrafo. Con todas
   las celdas vacías (una tabla recién insertada) no hay proporción que respetar y el reparto es
   equitativo.
2. **`MarkdownElementGenerator`** oculta lo que separa una celda de la siguiente —los espacios y la barra—
   y reserva en su lugar exactamente el blanco que hace que el texto caiga en la X de su columna, incluida
   la parte que lo centra o lo alinea a la derecha dentro de ella. Es el mismo mecanismo con el que se
   centra una línea con `<div align="center">`: mover el texto es la única forma de alinearlo, porque
   AvaloniaEdit da a todas las líneas visuales el mismo `TextAlignment`.
3. **`MarkdownBackgroundRenderer`** dibuja el fondo del encabezado y la rejilla de 1px (misma tinta al
   10 % que los separadores del resto de la app, `docs/agent-docs/sistema-diseno-web.md` §3.2). Las filas
   visibles de una tabla llegan seguidas, así que se acumulan y se dibuja un solo rectángulo exterior; de
   una tabla más alta que la pantalla, los bordes superior e inferior solo se dibujan si esa fila está
   realmente a la vista.

Las filas ganan además unos píxeles de **relleno vertical** (arriba y abajo del texto) para que la rejilla
no quede pegada a las letras. Se consigue haciendo que el primer elemento de la fila sea más alto que el
texto: Avalonia compone cada línea con el mayor ascenso y el mayor descenso de sus elementos, así que un
espaciador más alto arrastra a toda la línea consigo (`SpacerMetrics`).

Cuando las celdas necesitan varias líneas, `MarkdownTableRowVisual` dibuja cada fila con el alto de su
celda más alta y centra verticalmente el contenido de cada celda, también en el encabezado. La línea
base del bloque reserva el descenso del editor dentro de ese alto para que la rejilla no añada espacio
inferior adicional. La alineación horizontal sigue siendo la indicada por la separadora Markdown.

### La fila separadora

`| --- | --- |` no es contenido, es la declaración de las alineaciones, y una tabla dibujada no puede
enseñarla. Ocultarle el texto no basta: **una línea del documento no puede medir menos que
`TextView.DefaultLineHeight`**, así que dejaría una banda vacía tan alta como cualquier fila. La única
forma de que deje de ocupar alto es **plegarla**, el mismo mecanismo con el que un editor de código pliega
una función, y de eso se ocupa `MarkdownTableFolding`.

Plegar tiene dos mitades y las dos son obligatorias: el pliegue quita el alto, y el generador de elementos
oculta el texto de la línea plegada, que al perder su línea visual propia pasa a ser la continuación de la
del encabezado. Por eso el generador pregunta a `MarkdownTableFolding.IsCollapsed` en vez de decidirlo por
su cuenta.

Nada queda plegado si el cursor está dentro de la tabla o si la tabla no cabe a lo ancho, y `Refresh()`
vuelve a decidirlo entero en cada movimiento del cursor, en cada reescaneo del documento y en cada cambio
de tamaño: un pliegue que deje de estar justificado se deshace en el siguiente evento, así que no hay
forma de que una línea se quede escondida.

### Cuándo se ve el Markdown en crudo

- **Con el cursor o la selección dentro de la tabla**, que se muestra entera como texto: barras y fila
  separadora incluidas, apagadas a color de texto secundario para que siga destacando el contenido. Es a
  nivel de bloque y no de línea a propósito — una tabla con una fila en crudo y las demás dibujadas tendría
  esa fila fuera de la rejilla, y editar una celda exige ver las barras que la separan de la siguiente.
- **Si la tabla no cabe a lo ancho de la columna** ni apretando los huecos interiores al mínimo. Dibujarla
  dejaría las filas partidas por el ajuste de línea y la rejilla descuadrada; el Markdown se lee mejor que
  un dibujo roto.

### Qué hay dentro de una celda

El formato en línea funciona dentro de las celdas —negrita, cursiva, tachado, código, resaltado, enlaces,
wikilinks— y los anchos se miden por lo que se ve, no por el texto crudo, así que ocultar los `**` no
descuadra la columna. Una **imagen** dentro de una celda también se dibuja, con un techo de ancho propio
(`MarkdownTableLayout.CellImageMaxWidth`): sin él, una imagen sin tamaño explícito ocuparía la columna de
texto entera y la tabla dejaría de caber.

## 3. Cómo se escribe y se edita

| Gesto | Qué hace |
|---|---|
| **Botón de tabla** de la barra de formato (grupo *Insertar*) | Inserta una tabla de 2×2 con los encabezados traducidos, en su propia línea, y deja el cursor en la primera celda. |
| **`/tabla`** | Lo mismo. |
| **`Tab`** / **`Mayús+Tab`** | Salta a la celda siguiente / anterior, saltándose la fila separadora. En la última celda de la última fila, `Tab` añade otra fila. Fuera de una tabla el tabulador sigue indentando. |
| **`Intro`** | Añade otra fila vacía con las mismas columnas y deja el cursor en su primera celda. Sobre el encabezado o la separadora, la fila nueva va **debajo de la separadora** — nunca entre las dos, que rompería la tabla. |
| **`Intro`** en una fila de datos ya vacía | La borra, que es la forma de salir de la tabla — igual que una viñeta vacía cierra una lista. |

Las columnas no se alinean en el texto del archivo: el `.md` guardado conserva las barras tal y como se
escribieron. La alineación es cosa del dibujo, así que una tabla escrita a mano y sin cuadrar se ve
perfectamente cuadrada sin tocar el archivo.

## 4. Exportación a HTML

`NoteExportRenderer` vuelca cada tabla como un `<table>` de verdad: la fila de encabezados en `<thead>`
con `<th>`, las de datos en `<tbody>` con `<td>`, y la fila separadora en nada — su única información, la
alineación de cada columna, viaja como clase CSS (`ta-left`, `ta-center`, `ta-right`) de cada celda. Toda
fila se rellena hasta el número de columnas de la tabla, para que el documento exportado tenga la misma
rejilla rectangular que dibuja el editor. El formato en línea de cada celda se renderiza con el mismo
código que el resto del documento, limitado al rango de la celda: un `**` abierto en una celda y cerrado
en la siguiente se queda como texto literal en vez de emitir una etiqueta a medias.

## 5. Cómo se prueba

- `enki.Tests/Views/Editor/Markdown/MarkdownTableParserTests.cs` — qué es una tabla y qué no, dónde empieza
  y acaba, y en qué celdas se parte cada fila.
- `enki.Tests/Services/NoteExportRendererTests.cs` — el `<table>` exportado, con sus alineaciones y sus
  filas rellenadas.
- `enki.UITests/EditorViewTablesUiTests.cs` — lo puramente visual y lo interactivo, contra el `EditorView`
  real: que las celdas de una misma columna caen en la misma X aunque su texto mida distinto, que la fila
  separadora desaparece plegada, que las filas ganan aire vertical, que el cursor dentro devuelve el
  Markdown en crudo, y que `Tab`/`Intro` mueven y crean filas donde deben. Son medidas de tipografía real,
  así que solo tienen sentido con una plataforma Avalonia levantada.
