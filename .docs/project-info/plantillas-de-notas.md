# Plantillas de notas

> **Alcance**: la carpeta `.enki/templates/` del vault, el formato de una plantilla, las variables que
> admite y los dos flujos que las aplican (nueva nota desde plantilla y nota diaria).
>
> **Fuera de alcance**: el editor Markdown en sí (`.docs/project-info/tablas-en-notas.md`,
> `imagenes-en-notas.md`), las skills de IA —que también viven en `.enki/` pero resuelven otro problema—
> y la papelera del vault, que no interviene aquí.
>
> **Fuente de verdad**: `services/NoteTemplateRenderer.cs`, `services/NoteTemplateService.cs`,
> `models/NoteTemplate.cs`, `viewmodels/notes/NotesViewModel.Templates.cs`,
> `viewmodels/settings/SettingsViewModel.Templates.cs`, `views/notes/NotesView.axaml` y
> `models/VaultSettings.cs`.

## 1. Dónde viven

Cada plantilla es un archivo `.md` dentro de `<vault>/.enki/templates/`. Ni base de datos, ni JSON, ni
formato propio: lo mismo que ya hacen las skills en `<vault>/.enki/skills/`, y por las mismas razones —el
usuario puede crearlas, editarlas, versionarlas con Git o traerlas de otro vault con cualquier herramienta,
y Enki las relee del disco cada vez que las necesita.

La carpeta empieza por punto, y eso no es cosmético: `VaultService` excluye de todo recorrido de notas
cualquier carpeta oculta (`IsUnderHiddenDirectory`), así que **una plantilla nunca aparece como nota** en
la rejilla, la búsqueda global, el quick switcher, el grafo de sesiones ni los backlinks. No hizo falta
ninguna exclusión especial para conseguirlo.

Se admiten subcarpetas (`trabajo/reunion.md`) para organizar una biblioteca grande. La identidad de una
plantilla es su **ruta relativa** a `templates/`, no su nombre: dos subcarpetas pueden tener una
`diario.md` cada una y el ajuste de la nota diaria sigue sabiendo a cuál apunta.

La primera vez que se abre un vault, Enki siembra la carpeta con dos ejemplos traducidos (`daily.md` y
`meeting.md`). La marca de "ya sembrado" es **que la carpeta exista**, no que tenga archivos: quien borra
todas sus plantillas está diciendo que no las quiere, y volver a ponérselas en el siguiente arranque sería
devolverle lo que acaba de tirar.

## 2. Formato de una plantilla

Frontmatter YAML opcional y cuerpo Markdown:

```markdown
---
name: Nota diaria
description: Estructura del día: foco, notas y cierre.
title: "{{date}}"
---

# {{date}} · {{weekday}}

## Foco de hoy
- {{cursor}}
```

| Campo | Para qué sirve |
| --- | --- |
| `name` | Lo que se lee en el selector. Sin él, el nombre del archivo. |
| `description` | Una línea explicando para qué es. Sin ella, se muestra la ruta relativa. |
| `title` | Patrón del **nombre de la nota** creada, con las mismas variables que el cuerpo. Sin él, el título lo decide quien aplica la plantilla. |

El frontmatter describe la plantilla, no la nota: **nunca se copia** a la nota creada. Un frontmatter
abierto y sin cerrar no es frontmatter — se trata como contenido, que es lo que el usuario ve en su
archivo.

## 3. Variables

Sintaxis: `{{nombre[±N unidad][:formato]}}`, con espacios opcionales en cualquier punto.

| Variable | Valor | Alias en castellano |
| --- | --- | --- |
| `{{title}}` | Título de la nota que se está creando | `{{titulo}}`, `{{título}}`, `{{nombre}}` |
| `{{date}}` | Fecha, `yyyy-MM-dd` | `{{fecha}}` |
| `{{time}}` | Hora, `HH:mm` | `{{hora}}` |
| `{{datetime}}` | `yyyy-MM-dd HH:mm` | `{{fechahora}}` |
| `{{weekday}}` | Día de la semana, en el idioma activo | `{{diasemana}}` |
| `{{month}}` | Nombre del mes, en el idioma activo | `{{mes}}` |
| `{{year}}` | Año | `{{año}}`, `{{ano}}` |
| `{{uuid}}` | Identificador único | `{{guid}}` |
| `{{cursor}}` | No escribe nada: marca dónde queda el cursor al abrir la nota | — |

- **Formato**: cualquier patrón de fecha de .NET tras los dos puntos — `{{date:dd/MM/yyyy}}`,
  `{{time:HH:mm:ss}}`. Los dos puntos del propio formato no se confunden con el separador: solo el primero
  separa. Un formato de un solo carácter se interpreta como patrón personalizado (`{{date:d}}` es el día,
  no la fecha corta de la cultura), que es la lectura obvia dentro de una plantilla.
- **Desplazamiento**: `{{date+1d}}` (mañana), `{{date-1w}}`, `{{date+1m}}`, `{{date-1y}}`, `{{time+2h}}`.
  Unidades `d`/`w`/`m`/`y`/`h`/`min` y sus nombres en castellano e inglés. Se puede combinar con formato:
  `{{date+1d:dddd}}`.
- **Cultura**: los valores por defecto de `date`/`time`/`datetime`/`year` son invariantes a propósito —son
  nombres de archivo y ordenaciones, no texto de interfaz, y deben aguantar un cambio de idioma. En cuanto
  se pide un formato explícito, o se usa `weekday`/`month`, manda el idioma activo, porque ahí lo que se
  quiere es texto legible.
- **Lo que no se reconoce se deja tal cual.** Un `{{pendiente}}` que aún no existe, una errata o un formato
  inválido se ven en la nota y se pueden corregir, en vez de desaparecer sin rastro. Una plantilla mal
  escrita nunca tumba la creación de la nota.
- **Un solo instante** alimenta título y cuerpo, así que una nota creada a las 23:59:59.9 no acaba con el
  nombre de un día y el contenido del siguiente.
- Con varios `{{cursor}}`, manda el primero y todos desaparecen del texto: el cursor solo puede estar en un
  sitio, y dejar los demás impresos sería peor que ignorarlos.

## 4. Flujo: nueva nota desde plantilla

Documentos › **Desde plantilla** (también en el menú contextual de la rejilla, en la fila de acciones de la
vista simplificada y en la paleta de comandos, `Ctrl+Shift+K`). Abre un selector con las plantillas del
vault, donde cada fila ofrece:

- **Crear la nota**: la plantilla se relee del disco —no se usa la copia cargada al abrir el selector, por
  si se acaba de editar en una pestaña—, se expanden sus variables y la nota nace ya escrita, en la carpeta
  que se esté mirando (vista completa) o en la raíz del espacio de trabajo activo (vista simplificada): el
  mismo destino que elegiría el botón "Nueva nota" de esa misma vista.
- **Editar la plantilla**: la abre como una pestaña más del editor. Una plantilla es un `.md` del vault, así
  que se edita con el mismo editor que cualquier nota en vez de con un formulario aparte que habría que
  mantener en paralelo.
- **Eliminar la plantilla**: respeta "Preguntar antes de borrar" (Ajustes › General). No pasa por la
  papelera del vault, que es la de las notas del usuario.

El pie del selector ofrece además **Nueva plantilla** —crea una con frontmatter y un ejemplo de la sintaxis
ya rellenos, y la abre para editarla— y **Abrir carpeta**, para organizarlas en el gestor de archivos del
sistema.

Si la plantilla declara `{{cursor}}`, la nota se abre con el cursor ya puesto ahí.

## 5. Flujo: nota diaria

Ajustes › Editor › Plantillas › **Plantilla de la nota diaria** elige cuál se aplica. El ajuste se guarda
en `VaultSettings.DailyNoteTemplate` (`<vault>/.enki/settings.json`) como ruta relativa, **no** en la
configuración global: apunta a un archivo de *este* vault, y el mismo ajuste en global señalaría a una
plantilla inexistente en cuanto se cambiara de vault.

- Sin plantilla configurada, la nota diaria se comporta como siempre: nombre `yyyy-MM-dd` y un encabezado
  con la fecha.
- Con plantilla, es ella quien decide el contenido y —si declara `title`— también el nombre de la nota.
- **La plantilla solo interviene al crear.** Si la nota del día ya existe, se abre tal cual: volver a
  aplicarla pisaría lo que se lleve escrito hoy.
- Si el ajuste apunta a una plantilla que ya no existe, la nota diaria vuelve a su comportamiento de
  siempre y el ajuste **no se borra**, para que recuperar el archivo deje la configuración donde estaba.

## 6. Cómo extender

- **Una variable nueva** se añade en `NoteTemplateRenderer.Resolve` (y su alias en `Canonicalize`), con su
  caso en `NoteTemplateRendererTests`. La regla que no se negocia: lo desconocido se deja intacto.
- **Un campo nuevo del frontmatter** se añade a `TemplateFrontMatter` y a `NoteTemplate`. Los campos que
  no conoce el deserializador se ignoran, así que una plantilla con metadatos propios del usuario se sigue
  leyendo sin quejas.
- **Otro flujo que aplique plantillas** (por ejemplo, una plantilla por carpeta) debe pasar por
  `NoteTemplateService.Prepare` y por `VaultService.CreateNote(titulo, carpeta, contenido)`, en vez de
  componer el Markdown por su cuenta: es lo que mantiene una sola definición de "qué significa aplicar una
  plantilla".
