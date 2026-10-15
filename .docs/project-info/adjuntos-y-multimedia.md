# Adjuntos y multimedia en notas

La nota Markdown es la fuente de verdad. Imágenes, PDF, audio, vídeo y otros documentos se referencian
por rutas relativas; los archivos externos se importan en `<nota>.attachments/`, compartiendo la
convención y el cifrado de las imágenes existentes. Un archivo que ya pertenece al vault se reutiliza
sin copiarlo. No existe una base de datos adicional de adjuntos ni anotaciones.

## Uso

- El clip de la barra de formato y `/adjunto` permiten seleccionar varios archivos.
- Arrastrar archivos al editor los coloca en el punto de caída. Pegar archivos desde el explorador
  sustituye la selección, igual que pegar imágenes. El pegado de capturas conserva su comportamiento.
- El botón de gestión de adjuntos abre las referencias del Markdown actual, incluidas imágenes y
  enlaces a archivos. Se omiten enlaces web, notas y ejemplos dentro de código.
- Las imágenes conservan el renderizado en línea. Los demás documentos incrustados aparecen como
  tarjetas que abren el gestor con un clic; también funciona Ctrl+clic sobre la referencia.
- El gestor reutiliza `FileCardViewModel`, `PdfDocumentViewModel` y `MediaPreviewView`: imagen,
  PDF paginado con zoom, audio y vídeo con controles de reproducción. Los formatos sin visor se pueden
  abrir con la aplicación del sistema.
- Una anotación se inserta junto a la primera referencia independiente al adjunto como un callout
  Markdown con enlace al archivo y, en PDF, el número de página consultada. Si la referencia está dentro
  de una tabla o de un párrafo, se añade al final de la nota para preservar su estructura. Se puede editar, copiar, exportar, sincronizar
  y deshacer como el resto de la nota. No modifica el PDF binario ni pinta encima de él.
- Los borradores de anotaciones se mantienen durante la vida de la pestaña al cambiar de adjunto o
  cerrar el gestor; no son contenido persistido hasta pulsar «Añadir anotación a la nota».
- Quitar una referencia es reversible mediante Ctrl+Z y **conserva el archivo**: otras notas podrían
  estar utilizándolo. Los archivos originales del módulo Archivos se gestionan allí; los importados
  en `<nota>.attachments/` se pueden eliminar desde el explorador del sistema dentro del vault.

Ejemplo interoperable:

```markdown
![Informe.pdf](Proyecto.attachments/Informe.pdf)

> [!note] [Informe.pdf](Proyecto.attachments/Informe.pdf) · Página 3
> Revisar las conclusiones con el equipo.

![Grabación.mp3](Proyecto.attachments/Grabación.mp3)
```

Los archivos sin extensión se insertan como enlaces normales para conservar su nombre y evitar
confundirlos con imágenes o transclusiones de notas.

## Persistencia y límites

`NoteAttachmentService` valida existencia, tamaño, destino y política de apertura. El límite de importación
es de 128 MiB por archivo (64 MiB para imágenes). Se rechazan archivos vacíos y formatos ejecutables.
Los nombres duplicados reciben un sufijo numérico. Las rutas escapan espacios, porcentajes, paréntesis
y caracteres que tienen significado en Markdown.

Las escrituras binarias usan `VaultService.CopyBinaryFile`: copia atómica, acotada y cifrada cuando el
destino pertenece a una sesión protegida. El visor lee mediante los servicios del vault; las aperturas
externas emplean la copia temporal descifrada existente. No se pueden previsualizar archivos de sesiones
bloqueadas. Cerrar el gestor detiene la reproducción y cierra el documento PDF.

Las anotaciones y las retiradas de referencias modifican `EditorViewModel.Document`; el autoguardado
existente pasa por `WriteContentChecked`. Un cambio externo en la nota provoca el flujo habitual de
conflicto y nunca se sobrescribe para guardar una anotación.

La exportación HTML representa los documentos como enlaces, evitando generar imágenes rotas para PDF,
audio o vídeo. Los binarios no se empaquetan dentro del HTML exportado.

## Validación

Pruebas en `NoteAttachmentServiceTests`, `EditorAttachmentsTests` y `EditorAttachmentsUiTests`, junto
con las regresiones existentes de imágenes y exportación. Comprobaciones manuales recomendadas:

1. Importar varios documentos con espacios, acentos y nombres repetidos; mover el vault a otro equipo.
2. Abrir PDF de varias páginas, cambiar zoom y anotar una página.
3. Reproducir audio/vídeo y cerrar el gestor; comprobar que la reproducción termina.
4. Anotar con una nota editada externamente y resolver el conflicto sin perder ninguna versión.
5. Abrir adjuntos cifrados, cerrar el visor durante una carga y bloquear la sesión.
6. Navegar el gestor con teclado, cerrarlo con Escape y comprobar temas claro/oscuro y escalas HiDPI.
