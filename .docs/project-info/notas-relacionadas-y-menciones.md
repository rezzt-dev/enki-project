# NOTAS RELACIONADAS Y MENCIONES SIN ENLAZAR

> Seguimiento de **MED-02** (`.docs/agent-docs/analysis/new-features/prioridad-media.md`). Dos bloques nuevos
> en la pestaña «Enlaces» del panel lateral del editor, debajo de los enlaces entrantes.

## 1. QUÉ HAY

| bloque | qué muestra | acción |
| --- | --- | --- |
| notas relacionadas | notas con etiquetas en común o que enlazan a las mismas notas que la activa, con el motivo (`#etiqueta`, nota compartida) | abrir la nota; botón para añadir `[[enlace]]` a la nota abierta |
| menciones sin enlazar | notas que escriben el título o el nombre de archivo de la nota activa en texto plano | abrir la nota; botón para convertir la mención en `[[enlace]]` |

Todo sale del mismo barrido del vault que ya hacía el panel de enlaces entrantes
(`VaultService.GetAllNotesAcrossVaultWithContent`), sin leer el disco otra vez, y se recalcula con el mismo
retardo de 600 ms que el autoguardado.

## 2. REGLAS

**Relacionadas** (`NoteRelationsService.FindRelated`). Puntúa una etiqueta compartida con 1 y una nota
enlazada en común con 2; ordena por puntuación, después por reciente; máximo 8. Excluye las notas que ya
enlazan a la activa y las que la activa ya enlaza, porque ahí no hay nada que descubrir. Los enlaces se
resuelven con el mismo criterio que el Ctrl+clic (`WikiLinkParser`: nombre de archivo único, título único).

**Menciones** (`NoteRelationsService.FindUnlinkedMentions`).
- Busca el título y el nombre de archivo (mínimo 3 letras), con límites de palabra Unicode: «Tarea» no es
  una mención dentro de «Tareas».
- Ignora frontmatter, bloques y fragmentos de código, wikilinks e incrustaciones, enlaces Markdown, etiquetas
  HTML, direcciones y fórmulas (`MaskNonProse`, que conserva longitud y saltos de línea).
- Una nota que ya enlaza a la activa no se ofrece. Una mención por nota.
- Si el nombre no apunta de forma inequívoca a la nota (dos notas con el mismo título y archivo), no se
  ofrece ninguna mención: el enlace nuevo iría a otra.
- Sin coincidencia con el nombre cuando la nota se mira en bruto, se descarta antes de enmascarar; en notas de
  2 MB la búsqueda tarda menos de 2 s (prueba incluida).

## 3. ENLAZAR EN UN CLIC

- **Mención** → `VaultService.LinkMention`: relee la nota de disco, vuelve a buscar la mención en el texto
  actual, toma un snapshot (`NoteVersionTrigger.PreBulkOperation`) y escribe con `WriteContentChecked`. Si el
  texto ya no tiene la mención, no escribe nada; si el archivo cambió fuera de Enki entre la lectura y la
  escritura, `ExternalModificationException` aborta la escritura y se avisa. El texto sale como `[[Nombre]]`
  si coincide con el nombre, o `[[Nombre|texto]]` si solo difiere en mayúsculas o es el otro nombre, de modo
  que la frase se lee igual.
- **Relacionada** → añade `[[nombre]]` en una línea propia al final del documento del editor, no al disco:
  el autoguardado, el historial y los conflictos externos siguen el camino de cualquier otra edición.

## 4. LÍMITES CONOCIDOS

- Si la nota con la mención está abierta en otra pestaña, ese cambio llega como cualquier cambio externo
  (la vigilancia del vault lo recarga o abre el diálogo de conflicto si tenía cambios sin guardar).
- Las menciones usan el texto guardado en disco de las demás notas, no lo que haya sin guardar en pestañas.
- No hay sugerencias por similitud de texto: «relacionadas» se basa solo en etiquetas y enlaces.

Pruebas: `NoteRelationsServiceTests` (30), incluida la escritura comprobada sobre un vault temporal.
