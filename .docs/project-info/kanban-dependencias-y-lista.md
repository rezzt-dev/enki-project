# KANBAN: DEPENDENCIAS Y VISTA DE LISTA

> Seguimiento de **MED-07**. Relación «bloquea a» entre tarjetas y una vista de lista sobre los mismos datos.

## 1. DEPENDENCIAS

**Datos.** `KanbanCard.Blocks` es la lista de identificadores de las tarjetas del mismo tablero a las que esa
tarjeta bloquea. Es la única mitad que se guarda; «bloqueada por» se deriva
(`KanbanDependencies.BlockersOf`), así que no hay dos copias que puedan discrepar. Un identificador que ya no
existe (tarjeta borrada o en la papelera) no cuenta, pero se conserva al guardar: si la tarjeta se restaura,
la relación vuelve.

**Bloqueada** = algún bloqueo del mismo tablero que no está terminado. Terminada = en la columna «Hecho», con
fecha de completado o archivada. Terminar el bloqueo libera la tarjeta sin tocar nada guardado.

**Editar.** Formulario de tarjeta › «Bloquea a»: lista de tarjetas elegidas y selector del resto. Solo se
ofrecen tarjetas que no cierran un ciclo (se excluyen las que, directa o indirectamente, ya bloquean a esta;
`KanbanDependencies.AddBlock` también lo rechaza). En edición, una línea de solo lectura dice qué la bloquea.

**Ver.** Candado cerrado en la tarjeta bloqueada y abierto en la que bloquea a otras, con el detalle en el
tooltip; sección «Dependencias» en el detalle de la tarjeta; icono en la vista de lista.

**Avisos al mover** (`KanbanViewModel.MoveCard`, el único punto que usan el arrastre y el selector de columna
del formulario):
- a **Hecho**: diálogo «Tarjeta bloqueada… ¿Completarla de todas formas?»; no se mueve nada hasta confirmar;
- a **En curso**: se mueve y se avisa por notificación;
- cualquier otra columna y los cambios de orden dentro de una columna: sin aviso.

## 2. VISTA DE LISTA

Botón «Lista» / «Tablero» en la barra del tablero (`IsListLayout`; no se guarda entre sesiones). Cada fila es
la **misma** tarjeta del tablero (`KanbanCardViewModel`), con su columna, prioridad, fecha e indicador de
bloqueo. Respeta buscador, etiquetas y filtros rápidos (usa `KanbanCardViewModel.IsVisible`). Ordena pulsando la
cabecera (título, columna, prioridad, fecha, bloqueadas); pulsar otra vez invierte el sentido, y las tarjetas sin
fecha van siempre al final. Pulsar una fila abre el detalle de siempre.

## 3. LÍMITES CONOCIDOS

- Las relaciones son de un solo tablero; al mover una tarjeta a otro tablero dejan de aplicarse (no se borran).
- Completar una tarjeta fuera de `KanbanViewModel.MoveCard` (el asistente, el calendario, `MoveCardToBoard`)
  no pasa por la confirmación; el indicador sí se actualiza al recargar.
- Con la vista de lista activa, exportar el tablero a PNG o PDF no encuentra la región visible y no exporta.
- Las tarjetas archivadas no se ofrecen como destino de «bloquea a».

Pruebas: `KanbanDependenciesTests`, `KanbanViewModelTests` (dependencias, confirmación, editor, lista) y
`KanbanViewUiTests`.
