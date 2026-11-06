# IA Skills — referencia técnica de la funcionalidad actual

> **Alcance**: este documento describe la sección "Habilidades" (`Skills`) tal como está implementada hoy
> en el código de `enki/src/enki/`. No es un plan ni una propuesta — la funcionalidad está construida y
> disponible en la app (detrás de un ajuste desactivado por defecto). Cada afirmación de este documento
> está verificada contra el código citado; donde el comportamiento era ambiguo se describe lo observado en
> vez de suponer una intención.
>
> **Fuente de verdad**: si algo aquí deja de coincidir con el código, el código manda.

> Actualización de la interfaz y estabilidad: [Editor y biblioteca de habilidades](editor-y-biblioteca.md).
> Esa guía detalla la creación directa, el nuevo lector/editor y los destinos de arrastre.

## Qué es una "skill" en Enki

Una skill es, literalmente, **un archivo Markdown con frontmatter YAML** — una plantilla de prompt
reutilizable. No hay ningún motor de IA embebido en Enki que las ejecute: el propio texto estático de la
sección (`Skills_Page_StaticNotice`, ver `enki/src/enki/localization/Strings.es-419.resx:1025`) lo dice
explícitamente:

> "Son plantillas de prompt estáticas para usar con un asistente de IA externo — Enki no incluye ni se
> conecta a ningún motor de IA por sí misma."

En la práctica, la sección es una **biblioteca/gestor de archivos** de estas plantillas: se importan,
organizan, editan, duplican, exportan y se copia su contenido al portapapeles (`CopySkillContentCommand`,
`SkillsViewModel.cs:207`) para pegarlo donde el usuario tenga su asistente de IA real (Claude, ChatGPT,
Claude Code, etc.). Esto es relevante porque el texto de Ajustes describe la sección de forma más
ambiciosa ("habilidades que extienden Enki con nuevas capacidades e integraciones",
`Settings_Skills_Description`) — la implementación real, hoy, es la de un gestor de plantillas de texto,
sin ninguna integración de ejecución.

**No hay ninguna conexión entre las skills y el editor de notas.** El menú `/` del editor
(`SlashCommandsViewModel.cs`) es una lista estática y completamente independiente de comandos de formato
Markdown (encabezados, listas, tablas, fecha…) cableados a mano en `AllCommands`; no lee, referencia ni
inserta contenido de `SkillFile`/`SkillService` en ningún punto del código. Cualquier lectura previa de
este proyecto que asuma "las skills se invocan desde el slash menu del editor" es incorrecta — no existe
tal integración en el código actual.

## Formato de archivo

Cada skill es un `.md` con frontmatter YAML delimitado por `---` al principio del archivo, igual que el
front matter habitual de Jekyll/Hugo. Ejemplo real, la única skill empaquetada por defecto
(`docs/ia-skills/default-skills/writer.md`):

```markdown
---
name: Writer
description: Helps you write and edit your notes.
emoji: "✍️"
tags: []
permissions: []
---

This is a built-in skill that helps with drafting and editing Markdown notes.
```

El frontmatter se separa del cuerpo con `SkillService.SplitFrontMatter` (busca la primera línea `---` y la
siguiente que también sea `---`; si no encuentra ambos delimitadores, considera que el archivo no tiene
frontmatter). El YAML se parsea con `YamlDotNet`, convención de nombres camelCase e
`IgnoreUnmatchedProperties()` — un campo desconocido en el YAML simplemente se ignora, no rompe la carga.

### Campos del frontmatter (`SkillService.SkillFrontMatter`, todos opcionales)

| Campo (YAML) | Tipo | Uso real en la UI hoy |
|---|---|---|
| `name` | string | Nombre visible de la tarjeta/detalle. Si falta, se usa el nombre de archivo sin extensión. |
| `description` | string | Descripción corta en la tarjeta y en el panel de detalle. |
| `emoji` | string | Emoji mostrado junto al nombre en tarjeta y detalle. |
| `color` | string | **Leído y almacenado en el modelo, pero no se renderiza en ningún control de `SkillsView.axaml`/`SkillEditorView.axaml` hoy.** No confirmar visualmente ningún efecto de este campo. |
| `vibe` | string | Se muestra en el panel de detalle bajo la etiqueta "Vibe" solo si no está vacío. |
| `tools` | lista de strings | Se muestra como chips en el panel de detalle. El propio comentario del modelo (`SkillFile.cs:27`) la describe como "herramientas que la skill puede usar en una futura integración con IA" — es decir, es metadata reservada, sin ninguna lógica que la aplique hoy. |
| `tags` | lista de strings | Chips en tarjeta/detalle, y alimenta el filtro de etiquetas (`TagFilterViewModel`) compartido con Notas/Kanban/Polar. |
| `permissions` | lista de strings | Chips en el panel de detalle ("Permisos"). El comentario del modelo (`SkillFile.cs:33`) la describe como "campo reservado para permisos futuros (leer notas, escribir archivos, ejecutar comandos...)" — igual que `tools`, sin lógica de aplicación hoy. |

### Modelo en memoria (`enki/src/enki/models/SkillFile.cs`)

Además de los campos anteriores, `SkillFile` guarda:

- `Id` — nombre de archivo sin extensión. Es el identificador técnico *dentro de su carpeta*: desde que
  existen subcarpetas ya no es único en la biblioteca (dos temas pueden tener cada uno su `redactor.md`),
  así que se sigue usando para proponer el nombre al duplicar, pero **no** para comparar skills entre
  recargas — eso lo hace `FilePath`.
- `Content` — el Markdown completo del archivo, frontmatter incluido.
- `FilePath` — ruta absoluta en disco. Es la identidad estable de una skill.
- `FolderPath` — carpeta que la contiene, absoluta.
- `RelativeFolderPath` — esa misma carpeta relativa a la raíz de la biblioteca, con `/` como separador y
  cadena vacía en la raíz (p. ej. `"escritura/blog"`). Es lo que se muestra como "ubicación" en el
  inspector y en la tarjeta cuando la rejilla enseña resultados de toda la biblioteca.
- `IsAtRoot` — atajo de "`RelativeFolderPath` está vacío".
- `IsDefault` — `true` si el archivo pertenece al conjunto de skills empaquetadas con la app (ver más
  abajo). Controla si se puede eliminar y si "Editar" pide confirmación primero.

## Dónde se almacenan

`SkillService.SkillsDirectory` (`enki/src/enki/services/SkillService.cs:38`) apunta a
**`<vault>/.enki/skills/`**, calculada en vivo a partir de `VaultService.VaultPath` en vez de fijarse una
sola vez en el constructor — así, si el usuario cambia de vault desde Ajustes, `SkillsViewModel` sigue
apuntando al vault activo sin necesitar reconstruirse. Solo se leen archivos `.md`, pero **de forma
recursiva** (`SearchOption.AllDirectories`): la biblioteca es un árbol de carpetas que el usuario
organiza, ver la sección siguiente.

Esto sigue el mismo patrón de persistencia "directa a archivo" que `VaultService` usa para las notas —
**a diferencia de** `KanbanService`, `PolarTaskService`, `ReminderService`, etc., que serializan a un JSON
único en `%AppData%/Enki/`. No hay ningún almacén JSON intermedio para las skills: el archivo `.md` en
disco *es* el dato.

Un archivo individual que no se puede leer (permisos, corrupción, YAML inválido) se descarta en silencio
durante la carga (`TryReadSkill`, `TryParseFrontMatter`) sin abortar la carga del resto de la biblioteca.

## Organización en carpetas

El usuario puede crear carpetas y subcarpetas dentro de `<vault>/.enki/skills/` para agrupar skills por
tema. No hay ningún índice ni metadato aparte: **las carpetas en disco son la organización**, igual que
el archivo `.md` *es* la skill.

- `SkillService.GetFolderTree()` devuelve el árbol como `Models/SkillFolder` — un POCO propio y no el
  `NoteFolder` que comparten Notas y Archivos, porque este árbol vive en el almacén de skills y además
  lleva dos contadores ya resueltos (`SkillCount`, las de la carpeta; `TotalSkillCount`, incluyendo
  subcarpetas) que la rejilla y el panel pintan sin volver a enumerar el disco por cada fila.
- `LoadSkills()` carga la biblioteca entera (recursiva); `LoadSkillsIn(carpeta)` carga solo un nivel. La
  sección usa la primera como fuente de verdad y decide en memoria qué subconjunto enseña.
- Crear, renombrar, eliminar y mover carpetas vive en `SkillService`, **no** delegado en los métodos
  equivalentes de `VaultService`: aquéllos mueven a la papelera del vault, que no tiene nada que ver con
  esta carpeta interna. Crear y renombrar aplican la misma política de colisión que el resto del proyecto
  (sufijo ` 2`, ` 3`… en vez de fallar o pisar en silencio).
- **Eliminar una carpeta borra todo lo que contiene y no hay papelera**, así que la UI siempre confirma
  salvo que el usuario haya desactivado `ConfirmBeforeDelete`. Y hay una regla propia del servicio: si
  dentro cuelga una skill *default*, `DeleteFolder` lanza `InvalidOperationException` — si no, borrar la
  carpeta sería la puerta trasera para eliminar justo lo que `DeleteSkill` protege.
- `MoveSkill(skill, destino)` mueve un archivo entre carpetas (es lo que hacen los arrastres de las dos
  vistas), con sufijo numérico si el nombre ya está ocupado en el destino. `MoveFolder` rechaza mover una
  carpeta dentro de sí misma o de una descendiente suya.
- Una skill sigue siendo *default* esté donde esté: `IsDefault` se decide por nombre de archivo contra
  `default-skills/`, y `IsWithinSkillsDirectory` comprueba pertenencia a la biblioteca a cualquier
  profundidad. Mover las skills empaquetadas a una carpeta propia es un uso previsto, no un caso raro.
- `EnsureDefaultSkills` considera la biblioteca "no vacía" si hay algún `.md` a cualquier profundidad, de
  modo que mover todas las skills a carpetas no provoca que se vuelvan a copiar las de fábrica.

## Las dos formas de navegar la sección

La sección se dibuja de una de dos maneras, elegidas con el conmutador de su propia barra de herramientas
(y del encabezado del panel derecho en el modo árbol) y persistidas en `AppSettings.SkillsViewMode`
(`"Browser"` / `"Tree"`, string plano por el mismo motivo que `NotesViewMode`: no acoplar `Models` a un
enum de UI):

- **Explorador** (`SkillsBrowserView`, por defecto) — panel de carpetas a la izquierda y rejilla de
  tarjetas a la derecha, la misma disposición que la sección Archivos. Conserva intactos el encabezado de
  la sección, el filtro de etiquetas, el banner de la comunidad y el inspector de detalle. La rejilla
  muestra primero las carpetas navegables (con la entrada `..` si no se está en la raíz) y después las
  skills de la carpeta actual.
- **Árbol** (`SkillsTreeView`) — el panel lateral de la vista simplificada de Notas adaptado a skills:
  carpetas y skills entremezcladas en una misma lista jerárquica, con su propio modo de búsqueda, y a la
  derecha el detalle de la seleccionada o su editor. Deja fuera a propósito el encabezado, el banner y el
  filtro de etiquetas, que es lo que aporta la portada del explorador al principio y estorba después.

El chooser vive en la sección y no en Ajustes —a diferencia de la vista del editor de Notas— porque es una
decisión de *cómo navegar* que se toma mientras se navega y cuyo efecto se ve al instante.

Las dos comparten el mismo `SkillsViewModel` y el mismo estado (carpeta actual, selección, editor
abierto): cambiar de modo reordena la pantalla, nunca reinicia lo que se estaba haciendo.
`SkillsView` es solo un contenedor: monta **una** de las dos en su `ContentControl` y descarta la otra
—por el mismo motivo que `NotesView`: cada una lleva su propio `SkillEditorView`, y dos editores vivos
sobre el mismo `SkillEditorViewModel` escribirían los dos sobre su `Text`— y aloja los cuatro diálogos
modales, que son idénticos en ambas.

El estado de expansión se guarda por separado para cada árbol (`SkillsExpandedFolderPaths` para el panel
del explorador, `SkillsTreeExpandedFolderPaths` para el árbol), igual que Notas separa el suyo entre sus
dos vistas: son dos árboles distintos —uno incluye skills y el otro no— y compartir el estado haría que
navegar en uno reorganizara el otro a la espalda del usuario. El ancho y la visibilidad del panel del modo
árbol viven en `SkillsTreeSidebarWidth`/`SkillsTreeSidebarVisible`, y el ancho se escribe al soltar el
separador, no en cada píxel del arrastre.

`SkillsViewModel.Tree.cs` es el parcial con todo lo propio del modo árbol (construcción del árbol,
búsqueda del panel, estado del panel lateral), igual que `NotesViewModel.SimpleView.cs`.

## Skills por defecto vs. skills de usuario

### Empaquetado

Las skills "de fábrica" viven como código fuente en **`docs/ia-skills/default-skills/*.md`** (dentro del
propio repo, no en `.enki/`) y se copian junto al ejecutable como contenido del `.csproj`:

```xml
<!-- enki.csproj -->
<ItemGroup>
  <Content Include="../../../docs/ia-skills/default-skills/*.md"
           CopyToOutputDirectory="PreserveNewest"
           TargetPath="default-skills/%(Filename)%(Extension)" />
</ItemGroup>
```

En runtime, `SkillService` resuelve esa carpeta empaquetada como
`Path.Combine(AppContext.BaseDirectory, "default-skills")`. Hoy solo existe una skill por defecto:
`writer.md` (ver el ejemplo de arriba).

### Copia al vault (una sola vez)

`SkillService.EnsureDefaultSkills()` copia esos archivos empaquetados a
`<vault>/.enki/skills/` **solo si esa carpeta no existe o está vacía** — es decir, no resucita skills que
el usuario haya borrado deliberadamente. Se invoca en dos sitios:

- `SkillsViewModel.EnsureDefaultSkills()`, llamada desde
  `MainWindowViewModel.OnSkillsPanelModeChanged` la primera vez que se activa el toggle "Mostrar
  Habilidades" en Ajustes.
- Indirectamente al construir `SkillsViewModel`, vía `Reload()` en el constructor (que dispara la carga
  normal; la copia en sí solo ocurre por el toggle, no en cada `Reload`).

### Cómo se marca "default"

Un archivo se considera `IsDefault = true` si (a) está directamente dentro de `SkillsDirectory` (no en una
subcarpeta) **y** (b) su nombre de archivo coincide con uno de los presentes en la carpeta empaquetada
`default-skills/` (`SkillService.IsDefaultFileName`). Es una comprobación por **nombre de archivo**, no por
ningún flag dentro del propio Markdown.

### Consecuencias de ser "default"

- **No se puede eliminar**: `SkillService.DeleteSkill` lanza `InvalidOperationException` si
  `skill.IsDefault`; la UI ni siquiera muestra el botón de borrar para estas tarjetas
  (`IsVisible="{Binding !IsDefault}"` en `SkillsView.axaml`).
- **"Editar" no edita el archivo default in-place**: `SkillsViewModel.EditSkill` detecta `IsDefault` y en
  vez de abrir el editor directamente, pide un nombre nuevo (`SkillNamePromptViewModel`) y llama a
  `SkillService.DuplicateSkill` para crear una copia editable con id nuevo (sufijo `-copia`/`-copy` según
  idioma, vía la clave de localización `Skills_NamePrompt_CopyIdSuffix`); solo esa copia se abre en el
  editor. La skill default original queda intacta en disco.

## Activación de la sección

La sección está **desactivada por defecto**: `AppSettings.SkillsPanelEnabled` (comentario en
`AppSettings.cs:142`: "Disabled by default because it is intended for advanced users") controla si aparece
la entrada "Habilidades" (`Nav_Skills`) en la barra de navegación principal y en Ajustes. El toggle vive en
Ajustes › Habilidades (`Settings_Skills_ShowSkills_Label`/`...Description`).

`MainWindowViewModel.OnSkillsPanelModeChanged` inserta/quita el `NavItemViewModel` correspondiente
(icono `Brain`) justo después de la entrada "Files" en el nav cuando el valor cambia, y navega de vuelta al
Dashboard si la sección estaba activa cuando se desactiva. La primera activación dispara
`Skills.EnsureDefaultSkills()` (ver arriba).

## Flujo CRUD (`SkillsViewModel` + `SkillEditorViewModel`)

`SkillsViewModel` construye su propio `SkillService` a partir de `VaultService` (no lo recibe ya
construido), por el mismo motivo que `SkillsDirectory` se calcula en vivo. Se suscribe a
`SkillService.SkillsChanged` para recargar la lista en cada import/delete/guardado.

### Listar, buscar, filtrar

- `Reload()` carga toda la biblioteca (`LoadSkills()`, recursiva), reconstruye el árbol de carpetas
  conservando qué estaba expandido y cuál era la carpeta actual, reconstruye el árbol del modo árbol y
  recarga el filtro de etiquetas (`TagFilterViewModel`, la misma pieza reutilizable que
  Notas/Kanban/Polar). Un flag `_reloading` evita que las reasignaciones internas de `CurrentFolder` y
  `SelectedSkill` vuelvan a filtrar a media reconstrucción.
- `ApplyFilter()` es el único punto donde se decide qué se ve. Sin búsqueda ni etiquetas activas, la
  rejilla enseña **el contenido de la carpeta actual**; con cualquiera de las dos activas pasa a enseñar
  **resultados de toda la biblioteca** (`IsFlatResults`), sin tarjetas de carpeta y con la ubicación
  visible en cada tarjeta. Es deliberado: buscar dentro de una carpeta y no encontrar lo que está en otra
  sería justo lo contrario de para lo que sirve buscar.
- `SearchText` filtra por coincidencia de subcadena (case-insensitive) en nombre, descripción, carpeta o
  etiquetas. El filtro de etiquetas se combina en AND con él.
- Tres estados vacíos distintos, porque la salida de cada uno es otra: `IsEmpty` (biblioteca sin nada,
  onboarding de "importa tu primera skill"), `IsCurrentFolderEmpty` (la carpeta abierta está vacía) y
  `HasNoSearchResults` (hay skills pero ninguna coincide).
- Contadores de la franja de estadísticas: total, por defecto, personalizadas (`!IsDefault`) y con
  etiquetas — todos recalculados sobre la biblioteca sin filtrar.

### Ver detalle

Seleccionar una tarjeta (`SelectSkillCommand`) abre el panel de detalle a la derecha: nombre, emoji,
badge "por defecto" si aplica, descripción, `vibe` (si no está vacío), etiquetas, `tools`, `permissions`,
el contenido Markdown completo (de solo lectura, con botón de copiar) y la ruta del archivo en disco.

### Crear una skill nueva

**No existe un botón de "crear skill en blanco".** Revisando `SkillsViewModel` completo, los únicos
comandos son: importar (archivo o carpeta), duplicar una existente, editar (que para una default crea una
copia primero), eliminar, exportar. La única forma de incorporar contenido nuevo a la biblioteca es:

1. **Importar** un `.md` ya escrito desde el disco (ver más abajo), o
2. **Duplicar** una skill existente (por defecto o propia) y editar la copia.

### Editar (`SkillEditorViewModel`)

`EditSkill` (sobre una skill que no es default) abre `ActiveEditor`, que alterna `SkillsView` entre "modo
lista" y "modo editor" según si `ActiveEditor` es `null` o no (el propio comentario en el código lo llama
"Opción A": una única vista con dos estados visuales en vez de una vista de editor separada).

El editor reutiliza los colorizadores Markdown de `Views.Editor.Markdown` (los mismos que usa el editor de
notas, `EditorView`) pero enlazados a un `string` plano (`SkillEditorViewModel.Text`) en vez de un
`TextDocument` de AvaloniaEdit por pestaña — no hace falta historial de deshacer persistente entre skills
porque solo hay una skill abierta en el editor a la vez. `SkillEditorView.axaml` no tiene su propia
toolbar; el encabezado (botón "volver", título, indicador de guardado) vive en `SkillsView`, que embebe el
control del editor dentro de su "modo editor".

**Autoguardado con debounce**: cada cambio de `Text` reinicia un `DispatcherTimer` de 600&nbsp;ms (mismo
valor que usa `EditorViewModel` para notas); al disparar, llama a `Save()`. Cerrar el editor
(`CloseEditorCommand`) llama primero a `FlushPendingSave()` para forzar el guardado inmediato sin esperar
al debounce.

Si el guardado falla por `IOException`/`UnauthorizedAccessException`, el error se registra en `LogService`,
se notifica al usuario (`Notification_SkillSaveFailed_*`) y el temporizador se reinicia para reintentar en
el siguiente tick — la skill se queda marcada como "no guardada" en vez de perder el cambio en silencio.

### Duplicar

`DuplicateSkillCommand` pide un id nuevo con `SkillNamePromptViewModel` (prellenado con
`<id-original>-copia`) y llama a `SkillService.DuplicateSkill`, que:

- sanea el id propuesto quitando caracteres inválidos para nombre de archivo (`SanitizeId`; si queda
  vacío, usa un GUID),
- falla con `InvalidOperationException` si ya existe un archivo con ese nombre **en la carpeta del
  original**,
- copia el `Content` completo del original a un archivo nuevo `<id>.md` **junto al original**, no en la
  raíz: duplicar una skill que el usuario colocó dentro de un tema debe dejar las dos juntas.

El mismo método se reutiliza para el flujo "editar una skill default" descrito arriba.

### Eliminar

`RequestDeleteSkill` respeta el ajuste global `ConfirmBeforeDelete` (Ajustes › General): si está activado
(por defecto lo está), abre un modal de confirmación (`PendingDeleteSkill`); si no, borra directamente.
Las skills `IsDefault` no exponen esta acción en absoluto. Si la skill eliminada estaba seleccionada o
abierta en el editor, ambos estados se limpian antes de recargar.

### Importar

Tres entradas: `ImportSkillsCommand` (selector de archivos `.md`, multiselección),
`ImportSkillsFolderCommand` (selector de carpeta) y soltar archivos desde el explorador del sistema sobre
la rejilla del modo explorador — las tres convergen en `SkillsViewModel.BeginImport`.

El destino es siempre **la carpeta que se está navegando** (`CurrentFolderPath`), no la raíz: importar
mientras se recorre un tema deja las skills nuevas en ese tema. Si el origen es una carpeta, sus
**subcarpetas se recrean** bajo el destino (su primer nivel aterriza directamente en él): la organización
que el usuario ya tenía fuera de Enki es exactamente la que esta funcionalidad existe para conservar. Si
detecta una colisión de nombre:

- con resolución **Cancelar** (`ConflictResolution.Cancel`): el archivo se omite y se cuenta en
  `Skipped`.
- con **Renombrar** (`ConflictResolution.Rename`): se le añade un sufijo numérico (`nombre 2.md`,
  `nombre 3.md`, …) hasta encontrar un nombre libre.
- con **Sobrescribir** (`ConflictResolution.Overwrite`): reemplaza el archivo existente.

`SkillsViewModel` detecta las colisiones *antes* de llamar al servicio (`DetectImportConflicts`, que
reutiliza el mismo `SkillService.ExpandMarkdownFiles` que hará la copia, para no razonar dos veces sobre
qué archivos entran) y así muestra el modal "Cancelar / Cambiar nombre / Sobrescribir" solo cuando hace
falta; si no hay conflictos, importa directamente con `Overwrite` (que en ausencia de colisión real es un
simple copy). La resolución elegida se aplica a **todos** los conflictos de esa importación, no archivo
por archivo. Las entradas de `SkillImportResult` son rutas relativas al destino, no nombres sueltos, para
que dos homónimos en subcarpetas distintas no se confundan en el diálogo.

### Exportar

`ExportSkillsCommand` exporta las skills marcadas con la casilla de la tarjeta (`IsCheckedForExport`); si
ninguna está marcada, exporta la seleccionada actualmente en el panel de detalle como fallback. Copia cada
archivo a la carpeta destino elegida por el usuario, conservando el nombre de archivo original y
**recreando bajo ella la carpeta en la que vivía cada skill** (`SkillService.Export`), de modo que
exportar y volver a importar devuelve la biblioteca a la misma organización en vez de aplanarla.

## Persistencia y escritura atómica (`SkillService`)

`SaveSkill(skill, content)` es el único punto de escritura para contenido editado (import/duplicate/export
usan `File.Copy`/`File.WriteAllText` directos porque no hay edición involucrada). Su comportamiento:

1. Si el contenido nuevo ya no tiene frontmatter delimitado por `---` (por ejemplo, el usuario lo borró
   manualmente en el editor), `PrependMinimalFrontMatter` le antepone uno mínimo generado con
   `name` (el de la skill, o el id si está vacío), `description` (la actual) y `permissions: []`.
2. Escribe a un archivo temporal (`<ruta>.tmp-<GUID>`) con `File.WriteAllText` y luego lo mueve sobre el
   destino final con `File.Move(tempPath, skill.FilePath, overwrite: true)` — mismo patrón "escribir aparte
   + mover" que `VaultService.WriteContent` usa para el contenido de notas, para no dejar el archivo
   truncado si el proceso muere a mitad de escritura. El propio comentario del código nota que antes usaba
   `File.WriteAllText` directo sobre el archivo final, el mismo defecto estructural que tenía la escritura
   de notas antes de corregirse.
3. Un fallo de E/S (`IOException`/`UnauthorizedAccessException`) se registra en `LogService.Error` y se
   relanza — no se traga en silencio — para que `SkillEditorViewModel.Save` pueda reaccionar (notificar al
   usuario y reintentar) en vez de perder el cambio sin que nadie se entere.
4. Tras escribir, se re-parsea el archivo final y se actualizan in-place los campos observables de la
   instancia `SkillFile` que ya tenían las vistas (nombre, descripción, emoji, etc.) — así un cambio de
   `name`/`description` en el frontmatter se refleja en la tarjeta de la lista sin recargar toda la
   colección.

`SkillsChanged` (un `Action?` simple, mismo patrón que `KanbanService.BoardChanged` o
`NotificationService.NotificationAdded`) se dispara tras guardar, duplicar, eliminar o importar con éxito;
`SkillsViewModel` está suscrito y recarga la lista completa en cada disparo.

## Enlace a la comunidad de skills

`SkillsViewModel.CommunitySkillsUrl` es una constante fija: `https://skillsclaude.org/skills`. Se abre con
`UrlOpener.Open` desde dos puntos de entrada idénticos en intención — la tarjeta de recomendación dentro de
la propia sección de Habilidades y el enlace equivalente en Ajustes › Habilidades — ambos leyendo la misma
constante para que no puedan desincronizarse entre sí.

## Resumen de lo que NO existe hoy (para no asumir de más)

- Ningún motor de IA integrado ni conexión de red desde esta funcionalidad: es gestión de archivos de
  texto, punto.
- Ninguna integración con el editor de notas ni con su menú `/` (`SlashCommandsViewModel` es un sistema
  totalmente aparte y no referencia skills en ningún punto).
- Ningún botón para crear una skill completamente en blanco; solo importar o duplicar.
- Los campos `tools`, `permissions` y `color` del frontmatter se leen y persisten, pero no tienen ninguna
  lógica de aplicación (`tools`/`permissions`) ni renderizado (`color`) en el código actual — son
  metadata reservada para un desarrollo futuro que todavía no existe.
- No se puede renombrar una skill desde el árbol ni desde la rejilla: su nombre sale del `name` del
  frontmatter, así que se cambia editándola. Solo las carpetas se renombran en línea.
- La biblioteca de skills no tiene papelera propia: eliminar una skill o una carpeta borra del disco. Por
  eso `ConfirmBeforeDelete` es la única red, y por eso una carpeta con skills default no se puede
  eliminar.
