# Búsqueda de ajustes, etiquetas Kanban y tareas vinculadas

## Comportamiento

- **Apariencia → Etiquetas de las tarjetas Kanban** permite mostrar u ocultar las etiquetas.
  La preferencia está activada por defecto y se guarda en `settings.json` como `KanbanShowCardTags`.
  Se aplica inmediatamente al tablero vertical, al horizontal y a las tarjetas archivadas.
  Ocultar etiquetas no modifica la tarjeta, la edición, el detalle ni los filtros por etiquetas.
- Cuando las etiquetas no caben en una línea, la tarjeta ofrece **Mostrar todas**. Con más de cinco
  etiquetas se presenta inicialmente una lista de cinco; con cinco o menos, el texto largo se limita
  a una línea. **Mostrar menos** restaura la presentación compacta. El ancho disponible y la fuente
  real determinan el desbordamiento. Las tarjetas horizontales pueden crecer al desplegar la lista.
- El botón de tareas vinculadas del editor centra verticalmente el contador y los iconos. El popup
  conserva su altura máxima y desplazamiento, presenta filas de texto sin contornos individuales y
  utiliza una separación horizontal de un píxel. Pulsar una tarea mantiene la navegación existente.
- La búsqueda de ajustes filtra mientras se escribe, en todas las categorías, utilizando el idioma
  seleccionado. Todas las palabras deben coincidir con una misma opción o su contexto. Se ignoran
  mayúsculas, tildes, diacríticos y signos de puntuación. El orden de las palabras no importa.
- Se buscan etiquetas, descripciones, acciones y nombres de las listas configurables, incluidos los
  atajos, las secciones laterales y las organizaciones del Dashboard. Las opciones coincidentes se
  editan directamente. Las condiciones originales de disponibilidad siguen vigentes.
- **Escape** o el botón de limpiar restauran la sección seleccionada. Elegir una categoría también
  termina la búsqueda. Si no hay coincidencias aparece un mensaje localizado. El buscador y las
  categorías pueden desplazarse en ventanas pequeñas.

## Implementación

`SettingsViewModel.Search.cs` conserva la consulta y el comando de limpieza. Los textos dinámicos
proceden de los catálogos y viewmodels existentes, sin duplicar los datos ni los controles de edición.

`SettingsSearchOption` es un decorador de presentación. Hereda consulta y sección desde la vista,
resuelve `ResourceKeys` con `LocalizationService` y acepta `AdditionalText` para listas dinámicas.
Cada línea de este último representa una opción independiente. Los grupos usan `MatchDescendants`
para evitar que palabras de opciones distintas produzcan un resultado vacío. El decorador no
sobrescribe los bindings de disponibilidad de sus controles hijos. Las suscripciones al cambio de
idioma se liberan al salir del árbol visual.

Al añadir un ajuste:

1. Envolver su fila completa —etiqueta, descripción y control— en `SettingsSearchOption`.
2. Declarar sus claves de traducción y las de contexto en `ResourceKeys`.
3. Para un grupo, utilizar `MatchDescendants="True"`; para listas, enlazar los textos de sus entidades
   reales mediante `AdditionalText`. Si las filas se crean de forma diferida, aportar también sus
   documentos al grupo para que la búsqueda pueda hacerlo visible antes de materializar la lista.
4. Mantener dentro del decorador cualquier condición previa de disponibilidad y probar el filtrado.

`SettingsSearchMatcher` centraliza la normalización y la coincidencia de todas las palabras.
`SettingsService.SettingsChanged` anuncia únicamente guardados completados; `KanbanViewModel` lo
utiliza para actualizar la visibilidad. `KanbanTags` encapsula la medida de texto y la expansión visual
por tarjeta. `KanbanCardViewModel.Tags` renueva su instantánea de presentación al editar la entidad.

## Validación

Las pruebas unitarias cubren normalización, persistencia y limpieza de búsqueda. Las pruebas Avalonia
headless comprueban el filtrado de opciones individuales y dinámicas, los cambios de idioma, el estado
vacío, el desbordamiento y la expansión de etiquetas, el cambio inmediato de visibilidad en ambas
orientaciones y la alineación/navegación del popup del editor.
