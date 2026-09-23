# Kanban: tareas archivadas

Documento de referencia de la funcionalidad "archivar" del tablero Kanban local. Describe la decisión
central (dónde vive una tarjeta archivada), qué ve cada módulo de la app, y por dónde se toca cada pieza.

## 1. Qué problema resuelve

La columna "Hecho" solo crece. Un tablero con seis meses de trabajo terminado obliga a desplazarse por
decenas de tarjetas completadas para ver las tres que importan, y la única alternativa que había era
borrarlas — que las manda a la papelera, las quita del Calendario y las purga a los 30 días.

Archivar es la vía intermedia que faltaba: **la tarea sale del tablero pero no desaparece de la app**.
Se sigue viendo en el Calendario, sigue contando en las Estadísticas, se sigue encontrando en la
búsqueda global y sigue apareciendo como "tarea vinculada" de la nota a la que estuviera enlazada.

## 2. La decisión central: la tarjeta no se mueve

`Models.KanbanCard` tiene un campo nuevo, `ArchivedAtUtc` (y su derivada `IsArchived`). **Eso es todo lo
que cambia en los datos.** No hay una lista `ArchivedCards` en `KanbanBoard`, ni un segundo fichero, ni
una copia del modelo: la tarjeta archivada se queda exactamente donde estaba, dentro de
`KanbanColumn.Cards` de su columna, con su posición intacta.

La única frontera entre "existe" y "se dibuja" es `ViewModels.KanbanColumnViewModel`, que al construirse
salta las tarjetas archivadas y no les crea viewmodel:

```csharp
foreach (var card in column.Cards.Where(c => !c.IsArchived)) Cards.Add(new KanbanCardViewModel(card));
```

De ahí salen gratis todas las propiedades que el usuario pidió:

| Módulo | Lee | ¿Ve las archivadas? |
|---|---|---|
| Tablero Kanban | `KanbanColumnViewModel.Cards` (vista) | **No** — es quien las esconde |
| Calendario | `KanbanService.Board.Columns` (modelo) | Sí, sin un solo cambio en su código |
| Estadísticas | `Board.Columns` + `CompletedAtUtc` | Sí — son completadas reales del histórico |
| Búsqueda global (Ctrl+K) | `Board.Columns` | Sí |
| "Tareas vinculadas" de una nota | `Board.Columns` | Sí |
| Panel de inicio (Dashboard) | `Board.Columns`, **filtrando archivadas** | No — es un espejo del tablero |
| Buzón de recordatorios | `Board.Columns` con `CompletedAtUtc is null` | No — ya filtraba lo completado |

El Dashboard es la única excepción explícita (`DashboardViewModel.VisibleCards`): sus contadores y su
barra de progreso son un espejo de lo que se ve en el tablero, así que decir "12 hechas" cuando en el
tablero se ven 2 sería mentir. Las Estadísticas hacen lo contrario a propósito: miran el histórico, no
el estado actual.

Es el mismo criterio de "unificar sin duplicar" que documenta `AGENTS.md`, y por el que el Calendario
lee las entidades reales en vez de mantener su propia copia.

## 3. Las dos trampas de índices

Como el modelo tiene tarjetas que la vista no tiene, **los índices de `Column.Cards` (modelo) y de
`KanbanColumnViewModel.Cards` (vista) dejan de coincidir** en cuanto hay una archivada por delante. Dos
sitios dependían de que coincidieran y se corrigieron:

- **`KanbanViewModel.MoveCard`** — el sitio en el modelo ya no se deduce del índice de la vista, sino de
  la tarjeta ante la que se suelta (`targetColumn.Column.Cards.IndexOf(beforeModel)`).
- **`KanbanColumnViewModel.Sort`** — reconstruye `Column.Cards` desde la lista ordenada de la vista, así
  que rescata las archivadas antes y las vuelve a añadir después. Sin eso, ordenar una columna borraba
  del disco todo lo que tuviera archivado.

Desarchivar es la operación inversa y usa el mismo razonamiento:
`KanbanColumnViewModel.RestoreArchivedCard` cuenta cuántas no archivadas hay por delante en el modelo
para insertar el viewmodel en el sitio que le toca — la tarjeta vuelve a su posición original, no al
final de la columna.

## 4. Reglas de la funcionalidad

- **Solo se archiva desde la columna "Hecho"** (`CoreColumnRole.Done`, por rol y no por nombre, que el
  usuario puede cambiar). Archivar afirma que la tarea está terminada. La tarjeta lo sabe a través de
  `KanbanCardViewModel.CanArchive`, que `KanbanColumnViewModel` sella sobre todas sus tarjetas desde el
  `CollectionChanged` de su colección — el único punto por el que una tarjeta entra o sale de una
  columna, venga del arrastre, del editor, de crearla o de desarchivarla.
- **Archivar sella `CompletedAtUtc`** si estaba vacío (tablero semilla, o una tarjeta creada
  directamente en "Hecho" sin pasar nunca por `MoveCard`). Si no, quedaría archivada pero sin contar
  como completada en el Calendario ni en las estadísticas.
- **Desarchivar la devuelve a la columna donde estaba**, en su posición, que en la práctica es siempre
  "Hecho". El respaldo a `DoneColumn` cubre un `kanban.json` editado a mano.
- **Mover una archivada de columna desde el editor la desarchiva**: pedirla en otra columna solo puede
  querer decir que vuelve al tablero.
- **Borrar una archivada** la manda a la papelera como cualquier otra. La marca de archivado se quita al
  **restaurarla** (`TrashViewModel.RestoreTask`), no al borrarla: así el borrado desde el Calendario
  —que no pasa por `KanbanViewModel`— acaba igual, y "restaurar" siempre significa "vuelve al tablero".

## 5. La interfaz

Todo vive en `views/kanban/KanbanView.axaml`, sin sección nueva en el nav:

- **Menú contextual de la tarjeta** (las dos orientaciones del tablero): EDITAR · ARCHIVAR · ELIMINAR.
  "Archivar" solo aparece con `CanArchive`.
- **Cabecera de la columna "Hecho"**: botón de archivar todo lo completado de golpe, con confirmación y
  un aviso por `NotificationService` al terminar, como el resto de acciones masivas.
- **Barra de herramientas del tablero**: botón "ARCHIVADAS" con el número dentro, visible solo si hay
  algo archivado.
- **Ventana de archivadas**: un `ModalOverlayHost` sobre el propio tablero, no una página aparte — el
  archivo es el reverso del tablero, se entra a mirarlo y se vuelve. Hereda el lenguaje de la barra del
  tablero: buscador con lupa y aspa, chips de prioridad, filtro de etiquetas (cargado solo con las
  etiquetas que existen entre lo archivado) y desplegable de orden (fecha de archivado, fecha límite,
  prioridad, título). Un clic en una fila abre el detalle de siempre; el clic derecho ofrece
  DESARCHIVAR · EDITAR · ELIMINAR.
- **Diálogo de detalle**: marca "ARCHIVADA" con su fecha, y botón archivar/desarchivar — la vía que no
  depende del clic derecho, igual que editar y eliminar ya vivían ahí por no depender del hover.

Los diálogos del tablero son excluyentes entre sí (cada uno es un popup sobre la ventana entera), así
que abrir el detalle, el editor o la confirmación de borrado desde la ventana de archivadas la cierra y
la vuelve a abrir al cerrarlos — es lo que hace la bandera `KanbanViewModel._returnToArchive`.

Atajos: `Ctrl+F` enfoca el buscador de la ventana de archivadas cuando está abierta (y el del tablero
cuando no), `Supr` sobre una fila la manda a la papelera y `Esc` cierra la ventana, después de cualquier
diálogo que se haya abierto encima de ella.

## 6. Dónde tocar

| Pieza | Archivo |
|---|---|
| Campo del modelo | `models/KanbanCard.cs` |
| Qué dibuja la columna, orden e inserción | `viewmodels/kanban/KanbanColumnViewModel.cs` |
| Estado, filtros y comandos del archivo | `viewmodels/kanban/KanbanViewModel.cs` (región "Tareas archivadas") |
| Marca y acciones en el detalle | `viewmodels/kanban/KanbanCardDetailViewModel.cs` |
| Exclusión del panel de inicio | `viewmodels/dashboard/DashboardViewModel.cs` (`VisibleCards`) |
| Limpieza al restaurar de la papelera | `viewmodels/trash/TrashViewModel.cs` (`RestoreTask`) |
| Interfaz | `views/kanban/KanbanView.axaml` (+ atajos en su code-behind) |
| Textos | `localization/Strings*.resx`, claves `Kanban_Archive_*` / `Kanban_ArchiveAll_*` |
| Pruebas | `enki.Tests/ViewModels/KanbanViewModelTests.cs`, `enki.UITests/KanbanViewUiTests.cs` |
