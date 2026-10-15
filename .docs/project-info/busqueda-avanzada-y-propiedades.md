# Búsqueda avanzada y propiedades de notas

El panel **Filtros avanzados** de Notas, acotado a la carpeta y el espacio seleccionados, combina texto completo, etiqueta exacta, propiedad (clave y
valor parcial, sin distinguir mayúsculas), intervalo inclusivo de modificación y subcarpetas. Los
criterios activos se evalúan sobre `NoteFile` y el contenido leído por `VaultService`; el selector de
sesión sigue delimitando el espacio visible y las sesiones bloqueadas no se leen.

Una **vista guardada** conserva también las etiquetas seleccionadas en los chips y los criterios en `<vault>/.enki/settings.json`. No guarda resultados ni
copia contenido: abrirla vuelve a consultar los Markdown del vault. Los cambios hechos en otro editor
aparecen al refrescar la vista. El nombre de una vista existente sirve para actualizarla.

Las **propiedades** son claves de primer nivel del frontmatter YAML de cada nota. El botón de propiedades
junto a Etiquetas en el editor permite elegir una clave existente, editar su valor o eliminarla. `tags`
tiene su editor propio y está reservada. Al guardar se modifica solo la clave elegida, se conserva el
resto del YAML y se usa el guardado comprobado del editor (`WriteContentChecked`), con el mismo control
de conflictos externos y de sesión bloqueada que el texto de la nota. Las propiedades anidadas o listas
importadas se pueden consultar; el editor de propiedades escribe valores escalares.

El motor vive en `NoteQueryService`, el lector/escritor de YAML en `NotePropertyHelper` y la caché de notas
sigue siendo `VaultNoteCache`. La búsqueda global y el Quick Switcher mantienen su búsqueda de texto
completa; el panel avanzado actúa sobre la vista de Notas.
