# Editor Markdown: edición y vista previa

El editor usa AvaloniaEdit y mantiene un único documento Markdown: el formato visual no modifica el
texto guardado. Los marcadores se revelan al colocar el cursor o seleccionar su contenido.

## Justificación

La alineación predeterminada es justificada y se puede cambiar desde Ajustes → Editor. Se aplica a las
filas intermedias de párrafos, listas, tareas y citas cuando el ajuste de línea está activo. La última
fila, los encabezados y el código conservan su composición natural. Los saltos de línea escritos en el
documento delimitan párrafos; no se unen automáticamente.

`MarkdownJustification` distribuye primero el espacio entre palabras y después, si hace falta, un
interletraje limitado. Copia los glifos antes de ajustar sus avances para conservar las métricas de
selección y del cursor y no alterar los buffers compartidos del formateador.

## Escritura

- Negrita, cursiva, tachado, resaltado y código en línea alternan el formato. Sin selección se aplican a
  la palabra del cursor; en un espacio vacío dejan los marcadores preparados para escribir.
- Los botones de encabezado cambian el nivel existente. Las listas convierten el marcador existente;
  una selección mixta se homogeneiza y una selección del mismo tipo pierde los marcadores.
- Tab y Mayús+Tab anidan y desanidan listas. Intro continúa sus elementos y sale de un elemento vacío.
  La numeración se actualiza en la misma acción de deshacer, sin operaciones pendientes entre pestañas.
- Inicio alterna entre el contenido y el margen. Retroceso al inicio del contenido elimina el marcador.
  Las ayudas automáticas de listas no reinterpretan ejemplos dentro de cercas de código.
- El autocierre de paréntesis, corchetes, llaves y comillas dobles o angulares es configurable. Con texto
  seleccionado también se pueden envolver marcadores Markdown. Los acentos graves no se autocierran:
  así se pueden escribir tres seguidos para abrir un bloque.
- Pegar una URL sobre texto seleccionado crea un enlace. Insertar un bloque de código conserva el
  contenido seleccionado y su posición, y usa una cerca mayor si el contenido incluye acentos graves.
- Mover, duplicar e insertar líneas conserva los saltos LF o CRLF de la línea de referencia.

Las operaciones de texto están en `MarkdownEditing`; `EditorView.Editing.cs` conecta sus resultados con
la selección y los eventos de AvaloniaEdit. Las ediciones compuestas se agrupan con `RunUpdate()`.

Atajos predeterminados, modificables en Ajustes → Atajos:

| Acción | Atajo |
| --- | --- |
| Resaltado | Ctrl+Mayús+M |
| Cita | Ctrl+Mayús+Q |
| Lista | Ctrl+Mayús+L |
| Crear/completar tarea | Ctrl+Intro |
| Mover líneas | Alt+Arriba / Alt+Abajo |
| Duplicar líneas | Ctrl+Mayús+D |
| Borrar líneas | Ctrl+Mayús+Y |
| Seleccionar línea y ampliar | Ctrl+L |

## Código y avisos

Los bloques con lenguaje declarado usan las gramáticas TextMate y los temas claro/oscuro del visor de
Archivos. El registro se inicializa en segundo plano; los resultados se almacenan por bloque y se
invalidan al reanalizar el documento. La tokenización tiene un presupuesto compartido de 200 ms por
bloque y un máximo de 2.000 líneas. El resto queda sin resaltado; el texto continúa siendo editable.

El comando `/aviso` inserta un aviso con sintaxis `> [!note]`. Los tipos reconocidos (`note`, `info`,
`tip`, `warning`, `danger`, entre otros y sus alias) muestran icono, título y fondo según los colores del
tema. Si falta el título se muestra el nombre del tipo. El marcador original se conserva en el archivo.

Límites actuales: los sufijos `+` y `-` se reconocen, pero aún no hay plegado interactivo de avisos.
Los tipos desconocidos se muestran como citas normales. Los avisos anidados no tienen un árbol de
bloques independiente. El resaltado se limita a la vista del editor; esta mejora no cambia el exportador.

## Validación

```bash
dotnet test enki/tests/enki.Tests/enki.Tests.csproj
dotnet test enki/tests/enki.UITests/enki.UITests.csproj
```

Las pruebas cubren operaciones, selección, deshacer, CRLF, protección de código, análisis de avisos,
resaltado y geometría de justificación. `EditorEditingUiTests` envía eventos de teclado y texto al
control real en Avalonia Headless. No sustituye la comprobación visual y de accesibilidad en escritorio.
