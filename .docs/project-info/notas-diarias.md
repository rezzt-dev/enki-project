# Notas diarias

> **Alcance**: qué es la nota diaria de un día (cómo se llama, dónde está, cómo nace de su plantilla), la
> navegación día anterior / día siguiente desde el editor, combinar una nota diaria ya existente con su
> plantilla sin pisar lo escrito, y el calendario de constancia de Documentos —incluida la fila de hábitos
> que lo acompaña—. Corresponde a la propuesta 8 de `propuesta-nuevas-funcionalidades.md`.
>
> **Fuera de alcance**: las plantillas en sí —su sintaxis, su catálogo y el selector "Desde plantilla"—,
> documentadas en `plantillas-de-notas.md`; la pantalla de Hábitos (propuesta 13) y el módulo de
> Estadísticas, a los que esto solo enlaza.
>
> **Fuente de verdad**: `services/DailyNoteNaming.cs`, `services/DailyNoteService.cs`,
> `services/DailyNoteTemplateMerge.cs`, `viewmodels/notes/NotesViewModel.Daily.cs`,
> `viewmodels/notes/DailyConsistencyDayViewModel.cs`, `viewmodels/editor/EditorViewModel.DailyNote.cs`,
> `views/notes/DailyConsistencyView.axaml` y la cabecera de `views/editor/EditorView.axaml`.

## 1. Qué es la nota diaria de un día

La identidad de una nota diaria es **el nombre de su archivo**, no su primer encabezado ni ningún
metadato: es lo único que sobrevive a que el usuario reescriba el contenido entero, y lo que permite
encontrarla sin mantener un índice aparte que pudiera quedar desincronizado con el disco.

- **Sin plantilla configurada**: `yyyy-MM-dd.md`, invariante (no depende del idioma activo), el mismo
  nombre que Documentos usaba desde antes de existir las plantillas.
- **Con plantilla configurada** (Ajustes › Editor › Plantillas › *Plantilla de la nota diaria*): manda su
  `title`, expandido para ese día — `title: "Diario {{date}}"` produce `Diario 2026-09-18.md`.

`DailyNoteNaming` hace la traducción en los dos sentidos:

| Dirección | Para qué | Cómo |
| --- | --- | --- |
| fecha → nombre | abrir/crear la nota de ayer, de mañana o de cualquier día del calendario | expande el patrón de título con esa fecha |
| nombre → fecha | saber que la nota abierta **es** la nota diaria de un día, para ofrecer la navegación | convierte el patrón en un formato de fecha de .NET y hace `TryParseExact` |

Tres decisiones que sostienen todo lo demás:

- **La lectura inversa se valida con una ida y vuelta.** Una fecha leída de un nombre solo se acepta si
  volver a generar el nombre con ella devuelve exactamente el original. Cubre de golpe todo lo que un
  patrón puede hacer y el análisis no (un carácter que `VaultService` sanea al crear el archivo, una
  cultura distinta a la que escribió el nombre…). Ante la duda, la nota simplemente no es una nota diaria:
  la cabecera del editor queda como estaba en vez de navegar a un día equivocado.
- **El `yyyy-MM-dd` de siempre se sigue reconociendo aunque haya plantilla.** Configurar una plantilla hoy
  no deja huérfanas las notas diarias que el vault ya tenía, ni crea una segunda nota para un día que ya
  tiene la suya con el nombre anterior.
- **Un desplazamiento en el `title` se ignora.** `{{date+1d}}` tiene sentido en el cuerpo (un enlace a la
  nota de mañana) pero no en el nombre, donde llamaría a la nota de hoy con la fecha de otro día y rompería
  lo único que permite encontrarla.

Un patrón que no identifique un día —`{{title}}`, `{{uuid}}`, o solo `{{month}} {{year}}`, que dos días
distintos compartirían— hace que la navegación no se ofrezca; la nota diaria se sigue creando y abriendo
con normalidad.

## 2. `DailyNoteService`: una sola definición para cuatro superficies

El botón "Nota diaria" de Documentos, la paleta de comandos, la navegación del editor y el calendario de
constancia pasan todos por `DailyNoteService`, en vez de repetir cada uno su propio "buscar por título y
crear si no está":

- `Find(date)` / `FindInRange(from, to)` — busca en **todo el vault**, a cualquier profundidad: la nota
  diaria es una sola por día, esté en la carpeta que esté.
- `OpenOrCreate(date, folder)` — devuelve la existente o crea la del día aplicando la plantilla. La carpeta
  de destino es la misma que usarían "Nueva nota" y "Desde plantilla" (`NotesViewModel.TemplateTargetFolder`),
  para que las tres vías no dispersen las notas.
- `DateOf(note)` — de qué día es una nota, o null si no es una nota diaria.
- `PendingSections(date, content)` / `BuildMergedContent(date, content)` — la combinación con la plantilla
  (§4).

Sin estado ni caché propios: lee el vault, los ajustes del vault y el catálogo de plantillas, que son la
única fuente de verdad de lo suyo.

## 3. Navegación día anterior / día siguiente

Cuando la nota abierta es una nota diaria, la cabecera del editor añade —y solo entonces— un grupo con el
día anterior, la fecha escrita para leerse ("jue 18 sep"), el día siguiente y, si la nota no es la de hoy,
un botón para volver a hoy. En cualquier otra nota el grupo no existe y la cabecera queda exactamente como
estaba.

- Navegar **crea la nota del día destino si no existe**, con su plantilla, igual que el botón de la nota
  diaria: es lo que pide la propuesta 8 y lo que evita la vuelta larga por el buscador.
- Antes de cambiar de nota se fuerza el guardado pendiente (`FlushPendingSave`): la pestaña de origen puede
  quedarse abierta en segundo plano y su autoguardado tardaría hasta 600 ms más.
- Atajos remapeables (Ajustes › Atajos): `Ctrl+Alt+←` y `Ctrl+Alt+→`. Se montan sobre `NotesView` —que es
  quien tiene el foco de la página— y reenvían a la pestaña activa; son un no-op silencioso si esa pestaña
  no es una nota diaria.
- El estado se recalcula al abrir la pestaña y **en cada renombrado**: renombrar `2026-09-18` a `Ideas`
  deja de ser una nota diaria en ese mismo momento, y al revés.

## 4. Combinar la nota diaria con su plantilla

La plantilla solo se aplica al **crear** (volver a aplicarla entera pisaría lo escrito), lo que deja fuera
los dos casos más normales: la nota de hoy ya se había creado a mano o desde un wikilink antes de abrirla
como nota diaria, o la plantilla ganó una sección después de que la nota naciera.

Para eso, la cabecera del editor ofrece **combinar con la plantilla** cuando —y solo cuando— hay algo que
añadir:

- `DailyNoteTemplateMerge` compara los **encabezados Markdown** de la nota y de la plantilla ya expandida, y
  añade al final únicamente los bloques que faltan. La comparación ignora mayúsculas, espacios de más y el
  nivel del encabezado (`##` y `###` son la misma sección), y salta los encabezados que estén dentro de un
  bloque de código.
- **Nunca se toca lo que ya hay**: una sección que la nota tiene se respeta entera, con lo que el usuario
  haya escrito debajo y aunque la haya movido o reordenado. Los bloques nuevos van siempre al final, nunca
  intercalados: adivinar dónde "tocaría" cada sección dentro de un texto ya organizado es justo el tipo de
  automatismo que haría desconfiar de la acción.
- Una nota vacía recibe la plantilla entera, preámbulo incluido.
- El tooltip del botón enumera qué secciones se van a añadir **antes** de pulsarlo, y el resultado se
  anuncia con una notificación. La escritura pasa por `VaultService.WriteContent`, así que el historial
  local de versiones guarda el punto anterior a la combinación y deshacerla es siempre posible desde la
  pestaña "Historial".

## 5. Calendario de constancia

Botón junto a "Nota diaria" en la vista completa de Documentos (y en la fila de acciones de la vista
simplificada), también accesible desde la paleta de comandos. Es un popover, no una pantalla: la constancia
es algo que se consulta de reojo.

- **Diez semanas, una celda por día**, alineadas a semanas naturales: cada columna es una semana y cada
  fila, siempre el mismo día de la semana (el primer día lo decide el idioma activo). La ventana termina al
  final de la semana en curso, así que hoy nunca queda fuera; los días que aún no han llegado se pintan
  apagados y no se pueden pulsar.
- **Celda rellena = ese día tiene nota diaria**; hueca = no la tiene. Monocromo, sin escala de intensidad,
  mismo lenguaje visual que la cuadrícula de Hábitos.
- **Pulsar una celda abre la nota de ese día**, creándola si no existe — el camino más corto entre "ese día
  está vacío" y escribirlo.
- Debajo, **una sola cifra descriptiva**: "24 de 70 días con nota".

### Conexión con Hábitos y Estadísticas

Si el vault tiene hábitos activos, el panel añade una segunda rejilla con la **misma geometría y la misma
ventana de días**, leída de `HabitService` directamente (nunca una copia): celda rellena = ese día se
marcaron todos los hábitos que tocaban, hueca = tocaba alguno y quedó sin marcar, apagada = no tocaba
ninguno. Un hábito no cuenta antes de su fecha de creación, para que crear uno hoy no deje diez semanas de
casillas "sin cumplir" a su espalda.

El pie del panel lleva a Hábitos y a Estadísticas, que es donde cada constancia se gestiona y se analiza a
fondo. El panel no duplica ninguna de las dos.

### Lo que deliberadamente no hay

Ni rachas, ni puntos, ni niveles, ni objetivos, ni un color que "premie" un mes completo, ni ninguna señal
de haber "roto" nada — la propuesta 8 lo pide explícitamente ("sin caer en gamificación") y la propuesta 13
ya había tomado la misma decisión para Hábitos. La única lectura que ofrece el panel es descriptiva:
cuántos días tienen nota y cuáles.

## 6. Cómo extender

- **Otro sitio desde el que abrir la nota de un día**: llama a `NotesViewModel.OpenDailyNoteFor(date)`.
  Nada más — la carpeta de destino, la plantilla, el nombre, la recarga de la rejilla y el refresco del
  calendario ya están dentro.
- **Otra forma de nombrar la nota diaria**: `DailyNoteNaming`, y nada más que ahí. Cualquier nombre nuevo
  tiene que pasar la ida y vuelta de `DailyNoteNamingTests.TitleFor_AndTryParseDate_RoundTripEveryDayOfAYear`;
  si no se puede leer de vuelta, la navegación y el calendario dejan de reconocer esas notas.
- **Otro criterio de combinación** (por ejemplo, insertar la sección en su sitio en vez de al final):
  `DailyNoteTemplateMerge`, que es estático y puro y tiene sus casos en `DailyNoteTemplateMergeTests`. La
  regla que no se negocia: no tocar lo que el usuario ya escribió.
- **Dónde están los tests**: `DailyNoteNamingTests`, `DailyNoteTemplateMergeTests`, `DailyNoteServiceTests`
  y `NotesViewModelDailyNoteTests` en `enki/tests/enki.Tests/`, más `DailyNoteUiTests` en
  `enki/tests/enki.UITests/`, que monta el XAML real del calendario y de la cabecera del editor.
- **Otra ventana del calendario**: `NotesViewModel.ConsistencyWeeks`. Conviene mantenerla igual que
  `HabitsViewModel.ConsistencyWeeks`, porque las dos rejillas del panel se leen en paralelo.
