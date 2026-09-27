# Vista simplificada de Notas

> **Alcance**: la segunda disposición de la página de Notas, elegible en **Ajustes › Editor › Vista del
> editor**. Cubre cómo se elige la disposición, qué contiene la vista simplificada y qué deja fuera a
> propósito.
>
> **Fuera de alcance**: el editor de notas en sí (formato, live preview, exportación) y el panel lateral
> de esquema/enlaces/estadísticas, que son los mismos en las dos vistas y no cambian por esto.
>
> **Fuente de verdad**: `viewmodels/notes/NotesViewModel.SimpleView.cs`,
> `viewmodels/notes/NoteTreeItemViewModel.cs`, `views/notes/SimpleNotesView.axaml(.cs)`,
> `views/notes/NotesView.axaml(.cs)`, `views/notes/FullNotesView.axaml(.cs)`,
> `views/notes/EditorTabStripView.axaml(.cs)`, `services/EditorPreferences.cs` y
> `viewmodels/settings/SettingsViewModel.Editor.cs`.

## 1. Las dos disposiciones y cómo se elige entre ellas

La página de Notas se dibuja de una de dos formas:

- **Completa** (`FullNotesView`, por defecto) — la de siempre: portada con la rejilla de tarjetas, panel
  de carpetas y filtro de etiquetas mientras no hay ninguna nota abierta, y la barra de pestañas más el
  editor a pantalla completa en cuanto la hay.
- **Simplificada** (`SimpleNotesView`) — panel lateral con un árbol de carpetas y notas markdown de la
  sesión activa, y a la derecha las pestañas abiertas sobre el mismo editor. Pensada para escribir sin
  distracciones, con lo mínimo a la vista.

La elección vive en `AppSettings.NotesViewMode` (string `"Full"`/`"Simple"`, mismo criterio que
`EditorColumnPosition` de no acoplar `Models` a un enum de UI) y se propaga en caliente a través de
`EditorPreferences.SimplifiedNotesView`, igual que el resto de preferencias del editor: cambiarla en
Ajustes reordena la página al momento, sin cerrar ni reabrir nada.

`NotesView` ya no dibuja nada por sí mismo. Es un contenedor que:

1. monta en su `ContentControl` **solo** la disposición elegida y descarta la otra
   (`NotesView.axaml.cs`, `ApplyLayout`), y
2. aloja lo que es común a las dos: los atajos de gestión de pestañas y el diálogo de confirmación al
   eliminar una nota, que se dispara igual desde la rejilla de una que desde el árbol de la otra.

**Por qué se monta una sola y no las dos con `IsVisible`**: cada disposición contiene su propio
`EditorView`, y dos `EditorView` vivos sobre el mismo `EditorViewModel` se suscriben los dos a sus
eventos (comandos `/`, saltos desde el esquema, buscar y reemplazar), con lo que cada acción se aplicaría
dos veces sobre el documento. Hay un test que lo fija
(`SimpleNotesViewUiTests.SwitchingViewMode_SwapsTheLayoutInsteadOfKeepingBothAlive`).

Ambas comparten el mismo `NotesViewModel` y la misma barra de pestañas (`EditorTabStripView`), así que
cambiar de vista no cierra pestañas ni pierde estado.

## 2. Qué hay en la vista simplificada

### Panel lateral

- **Barra superior** — dos botones de modo (árbol / buscar) a la izquierda y el de ocultar el panel a la
  derecha. Alto fijo de 58px para casar al píxel con la barra de pestañas de la derecha (`Border.editor-tab`
  mide 41 + los 8+8 del margen del contenedor + 1 de borde inferior).
- **Fila de acciones** — nueva nota, nueva carpeta, nota diaria y contraer todo. "Nueva nota" y "nueva
  carpeta" crean **siempre en la raíz de la sesión**, no en una "carpeta actual": en esta vista un destino
  previsible vale más que uno que dependa de dónde se pulsó por última vez. Para crear dentro de una
  carpeta concreta está su menú contextual en el árbol.
- **Árbol** (`SimpleTreeRoots`, nodos `NoteTreeItemViewModel`) — carpetas y notas mezcladas en la misma
  lista de hijos, primero las subcarpetas (recursivamente) y después las notas sueltas de esa carpeta.
  Solo markdown: `VaultService.GetNotes` ya enumera únicamente `.md`, así que no hace falta filtrar nada.
  Renombrado en línea, menú contextual (nueva nota aquí / nueva subcarpeta / renombrar / eliminar) y
  arrastrar una nota sobre una carpeta —o sobre el fondo vacío, que es la raíz de la sesión— para moverla.
- **Pie** — la sesión seleccionada, con un menú para cambiarla.

Qué carpetas quedan expandidas se persiste en `AppSettings.NotesSimpleExpandedFolderPaths`,
deliberadamente separado de `NotesExpandedFolderPaths` (el panel de carpetas de la vista completa): son
dos árboles distintos —este incluye notas y el otro no— y compartir el estado haría que navegar en uno
reorganizara el otro a la espalda del usuario.

El panel se puede ocultar (`NotesSimpleSidebarVisible`) y ensanchar arrastrando su separador
(`NotesSimpleSidebarWidth`, acotado a 200–520px; se escribe al soltar el ratón, no en cada píxel del
arrastre). Con el panel plegado queda un raíl de 52px con accesos a **explorar, buscar, nueva nota,
plantillas y nota diaria**, además de desplegar el panel arriba y cambiar de sesión abajo. Los botones
incluyen tooltip y nombre accesible; el selector muestra también la sesión actual en su tooltip.

Explorar y buscar abren temporalmente el **mismo panel** sobre el editor, sin desplazarlo ni modificar
`NotesSimpleSidebarVisible`. El acceso activo queda resaltado. Abrir una nota, volver a pulsar el acceso
activo, pulsar Escape, cerrar o hacer clic fuera descarta el navegador. La búsqueda recibe el foco al
abrirse, incluso al retomar una consulta. Las filas del árbol y los resultados admiten Enter/Espacio.
La chincheta fija el panel desplegado y guarda esa elección. El raíl permite desplazamiento vertical en
ventanas bajas para mantener accesibles las acciones.

### Editor y pestañas

La barra de pestañas es la misma clase que en la vista completa. El botón "+" lo decide
`NotesViewModel.NewTab`: en la vista completa vuelve a la rejilla dejando las pestañas intactas; en la
simplificada, donde no hay rejilla a la que volver, crea directamente una nota en la raíz de la sesión.

Abrir una nota que ya está abierta **salta a su pestaña** en vez de duplicarla — eso ya lo hacía
`NotesViewModel.OpenNote` (empareja por ruta completa) y el árbol simplemente lo reutiliza. Al revés
también: `SyncSimpleTreeSelection` marca en el árbol la nota de la pestaña activa y abre las carpetas que
la contienen, así que abrir una nota desde cualquier otro sitio (dashboard, grafo de sesiones, restaurar
pestañas al arrancar) la deja visible y resaltada en el panel.

### Búsqueda

El segundo modo del panel busca por título y contenido de forma recursiva pero **acotada a la sesión
activa** (`EffectiveRootPath`), coherente con que el resto de la vista tampoco deja salirse de ella.
Entrar en modo búsqueda pone el cursor en el cuadro directamente.

## 3. Qué se deja fuera, y por qué

| Elemento | Motivo |
|---|---|
| Barra de formato del editor | Es la distracción principal que esta vista existe para quitar; el formato sigue disponible por Markdown y por los comandos `/` |
| Panel lateral de esquema / enlaces / estadísticas | Ídem — y su interruptor de la cabecera desaparece con él, en vez de quedarse como un botón que no hace nada |
| Portada con rejilla de tarjetas y filtro de etiquetas | El árbol ya es el navegador de la vista |
| "Favoritos" del panel lateral | Descartado a propósito: no existe como concepto en la app |
| Botón de volver de la cabecera | Cierra la pestaña actual, que es justo lo que hace la "x" de la propia pestaña |
| Etiqueta de texto del indicador de guardado | La pestaña ya lleva su punto de "sin guardar"; queda solo el icono, con el estado en el tooltip |
| Caja de etiquetas de la cabecera | Todo el peso visual tiene que llevárselo el espacio de edición; las etiquetas siguen editables desde la vista completa y desde el frontmatter de la nota |

Los dos primeros están gobernados por `EditorViewModel.IsSimplifiedView`, que se combina con los
interruptores del usuario (`IsToolbarVisible`, `IsSidePanelVisible`) en vez de sobrescribirlos: se
conservan tal cual y vuelven a aplicarse al volver a la vista completa. Los tres últimos van por
`EditorViewModel.ShowFullViewChrome`.

## 4. Cabecera del editor

En la vista simplificada la cabecera se reduce a **el nombre de la nota, en pequeño, y las acciones**.
Nada más: el editor puede ocupar media ventana, y lo único que debe destacar ahí es el espacio de
edición.

- **Nombre de la nota** — `EditorViewModel.TitleFontSize` lo baja de 24px a 13px. Sigue siendo el sitio
  donde se renombra (misma caja, mismo `LostFocus`/Enter), pero deja de leerse como un titular que compite
  con el texto de la nota; el nombre completo está además en la pestaña.
- **Sin caja de etiquetas** — fuera por `ShowFullViewChrome`, igual que el botón de volver y la etiqueta
  de texto del indicador de guardado.
- **`HeaderMargin`** cierra el hueco entre esa línea y el cuerpo (10px en vez de 20): con el título
  reducido y sin etiquetas, la cabecera es una sola fila de iconos y el aire de la vista completa quedaría
  desproporcionado.
- **`RootMargin`** ajusta el margen exterior del editor: menos aire en la simplificada, donde la nota ya
  está enmarcada por el panel lateral y la barra de pestañas.

La cabecera sigue siendo un `Grid` de tres columnas con la del título elástica (`*`, con `MinWidth`) y
`ClipToBounds`: al estrechar el editor lo que cede espacio es el nombre —que está entero en la pestaña— y
nunca las acciones, que dejarían de poder pulsarse.

## 5. Cómo extender

- **Una acción nueva en el panel lateral**: comando en `NotesViewModel.SimpleView.cs` y botón en la fila
  de acciones de `SimpleNotesView.axaml`. Si toca el vault, envuélvelo en el mismo `try/catch` de
  `IOException`/`UnauthorizedAccessException`/`SessionLockedException` con `NotifyVaultWriteFailed` que
  usan los que ya están.
- **Algo que invalide el árbol**: llama a `Reload()`. El árbol se reconstruye al final de ese método —
  un único punto de recarga en vez de que cada comando tenga que acordarse de refrescar la rejilla y el
  árbol por separado. `ReloadSimpleTree()` directo solo hace falta cuando la operación no pasa por
  `Reload()` (crear una carpeta, por ejemplo).
- **Algo que la vista simplificada deba ocultar**: si es chrome de la cabecera, cuélgalo de
  `ShowFullViewChrome`; si es una parte entera del editor, de `IsSimplifiedView`, combinándolo con el
  interruptor del usuario en vez de pisarlo.
