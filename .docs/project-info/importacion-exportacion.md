# Importación y exportación

## Importación local

Ajustes › Copias y datos ofrece cuatro entradas:

- **Obsidian:** seleccionar la carpeta del vault. Se leen notas Markdown y adjuntos comunes en subcarpetas; se omiten carpetas ocultas y enlaces simbólicos.
- **Notion:** seleccionar la exportación ZIP en formato «Markdown & CSV». Se leen sus páginas Markdown, sus bases de datos CSV como tablas Markdown y sus adjuntos comunes.
- **Todoist:** seleccionar el CSV de tareas. Se crean tarjetas en el tablero Kanban por defecto; las tareas completadas van a «Hecho». Se conservan título, descripción, prioridad y fecha cuando es reconocible. Las secciones, subtareas y recurrencias del CSV no tienen equivalencia automática en esta importación.
- **Google Calendar:** seleccionar un ICS. Cada VEVENT no cancelado se añade como recordatorio visible en el calendario, marcado como ya notificado para no generar alertas no solicitadas. Se conservan título, descripción, hora de inicio y duración; se respetan las zonas horarias `TZID` reconocidas por el sistema. Las reglas `RRULE` no se expanden: solo se importa la primera fecha y se avisa con el número de series afectadas. Las exclusiones y adjuntos del evento tampoco se expanden. Los eventos importados son locales y no se envían a Polar.

Las notas se crean bajo una nueva carpeta `Importado de …` del vault activo. Los adjuntos admitidos son PNG, JPEG, GIF, WebP, SVG, PDF, MP3, MP4, WAV y OGG. Los nombres en conflicto reciben el sufijo habitual de `VaultService.CreateNote` o `VaultService.ImportFile`. Repetir una importación crea copias; no se modifica la exportación original ni se reemplazan datos existentes. El lector limita cada exportación a 100 MB y 10 000 elementos, y descarta rutas inseguras del ZIP. Las escrituras utilizan los servicios existentes para que las vistas se actualicen mediante sus eventos. Si una escritura falla después de importar parte de los datos, el aviso indica cuántos elementos ya quedaron creados.

## Exportación

La exportación de notas a HTML sigue en `NoteExportService`. Notas, Kanban y calendario ya pueden exportarse a PNG o PDF desde sus menús y desde la paleta de comandos: ver `exportar-vistas-imagen-pdf.md` para el alcance visual, paginación y pruebas.
