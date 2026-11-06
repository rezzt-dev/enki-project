# Gestor global de etiquetas

La sección **Etiquetas** de la barra lateral reúne las etiquetas que están en uso en Notas, Kanban y Polar. La lista se reconstruye al abrir la sección desde `NoteFile.Tags`, `KanbanCard.Tags` y `PolarTask.Tags`; no existe un catálogo ni un archivo adicional de etiquetas.

## Uso

- **Renombrar** cambia una etiqueta en los tres módulos. Si el nombre de destino ya existe en una entidad, se conserva una sola aparición.
- **Fusionar** requiere seleccionar al menos dos etiquetas. El nombre de destino se propone a partir de la más usada, pero se puede editar antes de confirmar.
- **Eliminar** retira la etiqueta de las entidades activas después de mostrar una confirmación.
- La búsqueda filtra la lista sin perder la selección para fusionar.

Los nombres se comparan sin distinguir mayúsculas. No se admiten comas, corchetes ni saltos de línea como nombre de destino, porque el frontmatter de notas usa una lista inline separada por comas. Las notas normalizan sus etiquetas a minúsculas, igual que el editor existente.

La operación afecta a notas del vault y de sesiones accesibles, tarjetas activas o archivadas del tablero, y tareas Polar que no están borradas. Las sesiones bloqueadas y la papelera quedan fuera. En las notas se conserva el resto del frontmatter y se captura una versión antes de la operación; en Polar se marca cada tarea modificada para sincronizarla. Las pestañas de notas abiertas se guardan antes del cambio y se actualizan después.

Pruebas: `dotnet test enki/tests/enki.Tests/enki.Tests.csproj`. Los casos del gestor están en `Services/TagManagerServiceTests.cs`.
