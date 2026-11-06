# Copiar, cortar y pegar en Notas y Archivos

Las dos disposiciones de cada sección permiten copiar y cortar una nota o archivo desde su menú
contextual. «Pegar» está disponible en las carpetas y en el fondo del explorador, incluso vacío.
También funcionan Ctrl+C, Ctrl+X y Ctrl+V (Cmd en macOS) cuando el foco está en el explorador.
Dentro del editor, la búsqueda o un cuadro de renombrado se mantienen los atajos de texto.
Escape cancela la operación pendiente.

El portapapeles es interno, temporal e independiente para cada sección. Contiene una ruta y el tipo de
operación; no guarda contenido ni crea un segundo almacén. Se opera sobre un elemento cada vez.
No intercambia archivos con el portapapeles del sistema ni copia carpetas completas.

- Copiar permite pegar varias veces, incluso en la carpeta de origen.
- Cortar mantiene el original hasta pegar correctamente y después consume la operación pendiente.
- Pegar un corte en su carpeta original no cambia el nombre ni crea una copia.
- Los nombres repetidos reciben los sufijos ` 2`, ` 3`, etc., sin sobrescribir archivos ni carpetas.
- Los fallos se notifican y conservan la operación pendiente para poder reintentar.
- Se comprueba el cifrado del origen y del destino en el momento de pegar. Una sesión bloqueada no
  permite leer ni trasladar su contenido, aunque se hubiese copiado antes de bloquearla.

## Notas abiertas y adjuntos

Antes de transferir una nota abierta se guarda su edición pendiente por el mecanismo existente de
`WriteContentChecked` y se comprueba su huella en disco. Un conflicto externo impide la operación.
Tras un corte, la pestaña, el vigilante, el historial, las rutas guardadas y las tarjetas Kanban
vinculadas siguen la nota a su destino. Arrastrar una nota usa la misma transferencia segura.

La copia incluye la carpeta `<nota>.attachments`, sus subcarpetas y el cifrado correspondiente a la
sesión de destino. Si cambia el nombre, se ajustan las referencias Markdown generadas por Enki a esa
carpeta. Los adjuntos originales se conservan al cortar para no eliminar recursos que otras notas
puedan estar utilizando. Los enlaces relativos a otros recursos externos a esa carpeta no se reescriben.
La copia no duplica el historial de versiones; un corte sí lo traslada.

## Implementación y pruebas

`VaultService.Transfer.cs` realiza las escrituras con publicación sin sobrescritura. Las partes
`*.Clipboard.cs` de los viewmodels coordinan los comandos, las notificaciones y la actualización de las
vistas. `ExplorerClipboardKeys` limita los atajos al contexto del explorador.

Las pruebas de servicios cubren colisiones, copias repetidas, adjuntos, cifrado y fallos. Las de
viewmodels cubren el ciclo del portapapeles y las notas abiertas. Las pruebas de interfaz montan el
XAML real y verifican los bindings de menú y el aislamiento de los atajos de texto.
