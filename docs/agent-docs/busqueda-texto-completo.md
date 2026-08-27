# Búsqueda de texto completo

> Cómo busca Enki dentro de las notas, qué superficies comparten ese motor y qué invariantes hay que
> respetar al tocarlo. El código manda sobre este documento si alguna vez divergen.

## Qué había antes y por qué se cambió

Las cuatro superficies de búsqueda de la app —búsqueda global (`Ctrl+Shift+F`), Quick Switcher (`Ctrl+P`),
el filtro de la rejilla de Notas y el de la vista simplificada— comparaban el texto escrito contra tres
campos de `NoteFile`: `Title`, `Tags` y `Preview`.

`Preview` no es el contenido de la nota. Lo produce `VaultService.ExtractTitleAndPreview` aplanando el
cuerpo, borrando la sintaxis Markdown y **truncando a 160 caracteres**. En la práctica eso significaba que
todo lo escrito a partir del cuarto o quinto renglón de una nota era imposible de encontrar desde la
aplicación. Para un producto cuyo núcleo es un vault Markdown personal, eso deja al resto de los módulos
sin la pieza que los sostiene.

Además, la búsqueda global y el Quick Switcher partían de `GetAllNotesRecursive(VaultPath)`, que excluye
`.sessions/` por ser un directorio oculto: ninguna nota guardada dentro de una sesión aparecía nunca en
ninguna de las dos.

## Dónde vive

- **`services/NoteSearchService.cs`** — el motor. Sin estado, sin persistencia y sin índice propio.
- **`models/NoteSearchHit.cs`** — el resultado: la nota, qué campo casó (`Title`/`Tag`/`Content`), el
  desplazamiento y la línea del primer acierto en el cuerpo, cuántas veces aparece y el fragmento de texto
  recortado alrededor de la coincidencia.

Se construye una sola vez en `MainWindowViewModel.Composition` y se reparte a las cuatro superficies, igual
que el resto de servicios (composición manual, sin contenedor de DI — ver `AGENTS.md`).

## Los tres alcances

| Método | Recorre | Lo usa |
|---|---|---|
| `Search` | Todo el vault: raíz + todas las sesiones desbloqueadas | Búsqueda global, Quick Switcher |
| `SearchFolder` | Solo las notas directamente dentro de una carpeta | Filtro de la rejilla de Notas |
| `SearchTree` | El árbol completo bajo una raíz | Filtro de la vista simplificada |

Los tres comparten el mismo emparejado y el mismo orden; solo cambia el conjunto de partida.

## Invariantes

- **Nada de índices ni de almacenes nuevos.** Se lee a través de `VaultNoteCache`, que `VaultService` ya
  mantiene invalidado por fecha de modificación y por cada escritura de la app. Buscar dos veces seguidas
  no vuelve a tocar el disco. Es el mismo criterio de "unificar sin duplicar" que sigue el Calendario.
- **Una entrada por nota**, la de mayor peso. `MatchCount` dice cuántas veces aparece el término en el
  cuerpo, para no listar la misma nota tres veces.
- **Orden**: título → etiqueta → contenido y, a igualdad, alfabético por título, para que la lista no baile
  entre pulsaciones.
- **Las sesiones bloqueadas no aportan nada**, sin filtrado explícito aquí: es
  `GetAllNotesAcrossVaultWithContent` quien las excluye, igual que hace `SessionService.LoadGraph`.
- **El fragmento no cruza saltos de línea.** Un fragmento que pegara dos líneas se leería como una frase
  que la nota no contiene.

## Llegar a la coincidencia, no solo a la nota

Un acierto de contenido lleva su desplazamiento hasta el editor:

`NoteSearchHit.Offset` → `GlobalSearchResultViewModel.CaretOffset` →
`MainWindowViewModel.OnGlobalSearchResultSelected` → `NotesViewModel.OpenNote(note, caretOffset)` →
`EditorViewModel.RequestCaretAt`.

`RequestCaretAt` escribe `LastCaretOffset` **y** dispara `CaretNavigationRequested`, porque hacen falta las
dos cosas: una pestaña recién creada todavía no está enlazada a ninguna vista y `EditorView` lee
`LastCaretOffset` al enlazarse, mientras que en una pestaña ya abierta lo que actúa es el evento.
`EditorView` lo atiende con el mismo manejador que la navegación del panel de Esquema.

## Cómo extenderlo

- **Otro tipo de coincidencia** (por ejemplo, buscar en el nombre de fichero además de en el título): añadir
  el valor a `NoteSearchField`, emparejarlo en `BestHitFor` y darle peso en `RelevanceRank`. Las cuatro
  superficies lo heredan sin tocarlas.
- **Otro alcance**: un método nuevo que delegue en `SearchIn` con el conjunto de notas que corresponda. No
  dupliques el emparejado.
- Si alguna vez hiciera falta un índice de verdad (vaults de decenas de miles de notas), el sitio es este
  servicio: las cuatro superficies ya pasan por él y ninguna conoce cómo se resuelve la búsqueda por dentro.

## Tests

- `enki.Tests/Services/NoteSearchServiceTests.cs` — el motor: coincidencias más allá de los 160 caracteres,
  fragmento y número de línea, alcance de sesiones, orden por campo, tope de resultados y cancelación.
- `enki.Tests/ViewModels/GlobalSearchViewModelTests.cs` — de extremo a extremo por el popup, incluido que un
  acierto de cuerpo trae fragmento y `CaretOffset` y uno de título no.
- `enki.Tests/ViewModels/NotesViewModelSearchNavigationTests.cs` — que abrir un resultado deja el cursor en
  la línea encontrada, en pestaña nueva y en pestaña ya abierta.
