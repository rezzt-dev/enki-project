# MIGRACIÓN DESDE OTRAS HERRAMIENTAS

> Seguimiento de **MED-03**. Ajustes › Mantenimiento › Importar datos. Cada lector construye el mismo `Plan`
> que ya consumía `ExternalImportService.Apply`; ninguno escribe nada por su cuenta.

## 1. FORMATOS

| origen | entrada | qué se importa | qué se omite (y se anota) |
| --- | --- | --- | --- |
| Obsidian (ya existía) | carpeta | notas `.md`, adjuntos | otros tipos de archivo |
| Notion (ya existía) | ZIP | notas `.md`, tablas `.csv` como Markdown, adjuntos | otros tipos de archivo, rutas inseguras |
| Todoist (ya existía) | CSV | tareas | secciones y notas de proyecto, filas sin título |
| Google Calendar (ya existía) | ICS | eventos | cancelados, sin fecha de inicio |
| **Logseq** | carpeta del grafo | `pages/`, `journals/`, `assets/` | `logseq/`, pizarras `.edn`, archivos `.org`, páginas vacías |
| **Evernote** | `.enex` | notas con etiquetas y fecha de creación, adjuntos reconocidos | texto cifrado, adjuntos de tipos no importables o ausentes, notas vacías |
| **Google Keep** | ZIP de Takeout o carpeta | notas y listas, etiquetas, imágenes adjuntas | notas en la papelera, JSON ilegibles, adjuntos ausentes |
| **TickTick** | CSV de copia de seguridad | tareas (fecha, prioridad, estado) y elementos de tipo nota | repetición (la tarea se importa sin ella), filas sin título |

**Todoist estaba ya.** El análisis daba por ausente «tareas de Todoist»; `ReadTodoist` existe desde antes.

### Conversiones

- **Logseq:** `clave:: valor` de la primera página pasa a frontmatter (`tags::` a etiquetas); `TODO`, `LATER`,
  `NOW`, `DOING` → `- [ ]`; `DONE`, `CANCELED` → `- [x]`; se quitan `id::` y `collapsed::`; los diarios
  `2026_10_15.md` pasan a `2026-10-15.md`; los espacios de nombres (`a___b.md`, `a%2Fb.md`) se convierten en
  la carpeta `a/`, y `[[a/b]]` en `[[b|a/b]]` para que Enki lo resuelva por nombre de archivo.
- **Evernote:** ENML → Markdown con `HtmlReadability.ToMarkdown` (sin selección de contenido); `en-todo` →
  casillas; `en-media` → `![nombre](attachments/nombre)` o `[nombre](attachments/nombre)`, asociando por el
  MD5 del adjunto, como hace Evernote. El `.enex` se lee con `DtdProcessing.Ignore` y sin resolutor, de modo
  que una entidad externa no se expande (hay prueba).
- **Keep:** listas → casillas; etiquetas → etiquetas de Enki; fecha de creación → propiedad `created`.
- **TickTick:** prioridad 5/3/1/0 → alta/media/baja/media; estado 1 y 2 → completada; fecha con desfase
  `+0000` → UTC; las listas de comprobación con `▫`/`▪` pasan a casillas. **No se conservan** las etiquetas
  ni la lista de origen de las tareas (la tarjeta Kanban no tiene dónde ponerlas).

## 2. INFORME

`ExternalImportService.BuildReport` genera Markdown con lo importado (recuentos reales frente a los del plan) y
lo omitido, agrupado por motivo (`ImportIssueKind`). `WriteReport` lo guarda como nota «Informe de
importación» en la carpeta «Importado de …» (se crea si la importación solo escribió tareas), es decir, dentro
del vault. Un aviso indica cuántos elementos se omitieron y dónde está. Si la importación se interrumpe
(`ImportInterruptedException`) el informe también se escribe, con una nota que lo advierte, y el aviso de error
conserva el recuento de lo ya creado.

Límites: 500 incidencias detalladas por informe (el resto se cuenta); nombres recortados a 120 caracteres y sin
saltos de línea ni comillas invertidas.

## 3. SEGURIDAD

- Los lectores no escriben: devuelven un `Plan`. `Apply` crea notas con `VaultService.CreateNote`, tarjetas
  con `KanbanService` y eventos con `ReminderService`; nada fuera del vault ni de esas entidades. Una prueba
  comprueba que la carpeta de origen queda intacta y que no se crea ningún archivo fuera del vault y del
  almacén temporal.
- Límites heredados: 100 MB de entrada y 10 000 entradas; rutas con `..` o absolutas se rechazan.
- Importar de nuevo crea copias (el vault no deduplica).

## 4. NO PROBADO

Las pruebas usan exportaciones sintéticas escritas a mano. Faltan exportaciones reales de cada aplicación:
variantes de ENML poco comunes, grafos de Logseq con bloques anidados complejos y exportaciones de Keep con
audio o dibujos.
