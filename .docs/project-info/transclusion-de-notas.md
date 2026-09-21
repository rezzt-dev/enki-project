# Transclusión de notas (`![[Nota]]`)

> **Alcance**: qué referencias se reconocen como contenido embebido, cómo se resuelven sobre el vault real,
> qué se dibuja en el live preview del editor y qué sale en el HTML exportado.
>
> **Fuera de alcance**: los wikilinks normales (`[[Nota]]`) y su navegación, que son previos y siguen
> funcionando igual; las imágenes incrustadas con la misma sintaxis (`![[foto.png]]`), documentadas en
> `imagenes-en-notas.md`; las fórmulas y diagramas, en `formulas-y-diagramas.md`.
>
> **Fuente de verdad**: `services/NoteTransclusionService.cs`, `services/WikiLinkParser.cs`,
> `views/editor/Markdown/MarkdownLineParser.cs`,
> `views/editor/Markdown/MarkdownTransclusionPresenter.cs`,
> `views/editor/Markdown/MarkdownTransclusionContext.cs`,
> `views/editor/Markdown/MarkdownElementGenerator.cs`, `viewmodels/editor/EditorViewModel.cs`,
> `views/editor/EditorView.axaml.cs` y `services/NoteExportRenderer.cs`.

## 1. Qué se reconoce

La sintaxis es la de Obsidian, que es con la que la nota va a viajar si el usuario cambia de herramienta:

```markdown
![[Otra nota]]
![[Otra nota#Una sección]]
![[Otra nota#^bloque-1]]
![[Otra nota|Alias que aquí no se muestra]]
```

Reglas del reconocimiento (`MarkdownLineParser.ScanTransclusion`):

- **`![[…]]` con extensión de imagen sigue siendo una imagen.** `![[diagrama.svg]]` se dibuja como imagen,
  no como nota embebida; lo decide la misma lista de extensiones que gobierna el resto del editor
  (`MarkdownLineParser.LooksLikeImage`). Solo lo que no parece un archivo de imagen llega a ser
  transclusión.
- El **destino** es lo que hay antes del `|`; el alias se ignora al resolver (una nota embebida ya trae su
  propio título en la cabecera del bloque).
- `#Sección` y `#^bloque` acotan qué parte se embebe. Sin ninguno de los dos se embebe la nota entera.
- Dentro de una cerca de código no se reconoce nada, como en todo el live preview.

## 2. Cómo se resuelve

`NoteTransclusionService.Resolve` trabaja **directamente sobre las notas reales del vault**: usa el mismo
`WikiLinkParser.ResolveTargetWithStatus` que los wikilinks y lee con `VaultService.ReadContent`, cuya caché
ya se invalida por fecha y tamaño de archivo. No hay un segundo índice ni una copia del contenido embebido
— es el patrón de "unificar sin duplicar" de `AGENTS.md`.

El resultado es un `NoteTransclusionResult` inmutable con un `Status`, y **solo el estado `Resolved` lleva
texto**: ningún error filtra contenido parcial. Los estados y lo que muestran:

| Estado | Cuándo | Qué se ve |
| --- | --- | --- |
| `Resolved` | La nota (y la sección/bloque, si se pidió) existe | El contenido embebido |
| `Broken` | No hay ninguna nota con ese nombre | «La nota embebida no existe» |
| `Ambiguous` | Más de una nota coincide | Aviso de referencia ambigua |
| `SectionMissing` / `BlockMissing` | La nota existe, la sección o el bloque no | Aviso, sin volcar la nota entera |
| `Locked` | La nota vive en una sesión protegida y bloqueada | Aviso; **nunca** su contenido |
| `Cycle` | A embebe B que vuelve a embeber A | Aviso, y la recursión se corta ahí |
| `DepthLimit` | Se alcanzó `MaxDepth` (5 niveles) | Aviso, y la recursión se corta ahí |
| `ReadFailed` | El archivo existe pero no se pudo leer | Aviso |

**Ciclos y profundidad.** Cada resolución recibe la *ascendencia*: la lista de rutas de notas por las que
ya se ha pasado, empezando por la nota anfitriona. El ciclo se detecta comparando rutas **antes de leer el
archivo**, no después, así que A → B → A nunca llega a abrir A por segunda vez. El tope de cinco niveles es
la red de seguridad para cadenas largas sin ciclo.

**Sesiones bloqueadas.** Se comprueba `VaultService.IsPathInLockedSession` antes de leer, y además se
captura `SessionLockedException` por si la sesión se bloquea entre la comprobación y la lectura. En los dos
casos el estado es `Locked` y el contenido queda vacío.

**Secciones y bloques.** Una sección va desde su encabezado hasta el siguiente del mismo nivel o superior,
y el encabezado pedido se compara normalizado (minúsculas, sin signos, espacios como guiones), de forma que
`#Una Sección Útil` y `#una-seccion-util` encuentran lo mismo. Un `^id` al final de una línea identifica
esa línea; si el identificador está en una línea propia, pertenece al párrafo no vacío inmediatamente
anterior.

## 3. Cómo se dibuja

El generador de elementos sustituye el texto `![[…]]` entero por un bloque de solo lectura
(`MarkdownTransclusionPresenter`), con el cursor fuera; al entrar en él, el live preview devuelve la
referencia a su forma de texto como con cualquier otro marcador.

El bloque lleva una cabecera con el icono y el título de la nota y, debajo, su contenido **reparseado con
el mismo `MarkdownLineParser`** que alimenta el editor: encabezados, listas, tareas, citas, código y
formato en línea se ven igual que en la nota original. No se crea un segundo parser ni un segundo
documento editable.

- La edición sigue siendo siempre sobre la nota origen. El embebido no recibe entrada de texto.
- Un clic en el bloque abre la nota embebida en una pestaña (`EditorViewModel.OpenWikiLink`).
- Las transclusiones **anidadas** se resuelven recursivamente, pasando la ascendencia hacia abajo, y se
  dibujan como otro bloque dentro del bloque.
- El contenido se corta a 300 líneas con un pie de «vista previa truncada»: un embebido no debería
  convertir una nota corta en un documento gigante.
- El bloque expone su nombre accesible (`Editor_Transclusion_AccessibleName`) para el lector de pantalla.

**Refresco.** Una nota embebida no es un búfer abierto y por eso no pasa por el vigilante de conflictos
(`cambios-externos-en-el-vault.md`). Mientras el documento tenga al menos una transclusión,
`EditorView` repinta cada dos segundos; la caché del vault evita I/O si nada ha cambiado, así que editar la
nota origen en otra pestaña —o fuera de Enki— actualiza el embebido sin reabrir la nota anfitriona.

## 4. En la exportación a HTML

`NoteExportRenderer` no resuelve transclusiones: el documento exportado tiene que ser autónomo y fuera del
vault no hay nada que resolver. La referencia se conserva como enlace interno destacado
(`<span class="wikilink">`), igual que un wikilink normal — lo que no se hace es tragársela en silencio.

## 5. Cómo extender

- **Un estado nuevo** se añade a `NoteTransclusionStatus`, a `MarkdownTransclusionPresenter.StatusKey` y a
  los siete `.resx` de `localization/`. Si el estado no es `Resolved`, `Markdown` debe quedar vacío.
- **Otro tipo de destino** (por ejemplo embeber un adjunto) se decide en
  `MarkdownLineParser.ScanTransclusion`, antes de crear el span, no en el presentador.
- Toda lectura sigue pasando por `VaultService`. No añadas caminos que lean el disco por su cuenta: se
  saltarían la caché, el descifrado de sesiones y la comprobación de bloqueo.
