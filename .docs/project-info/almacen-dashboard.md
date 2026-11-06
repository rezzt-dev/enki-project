# Almacén del panel principal

La tarjeta resume el contenido del vault activo leyendo los servicios existentes:

- **Notas**: Markdown de la raíz, sus subcarpetas y todas las sesiones.
- **Archivos**: archivos visibles distintos de Markdown, también dentro de sesiones y subcarpetas.
  No cuenta carpetas, ficheros ocultos, metadatos internos ni contenido de la papelera.
- **Sesiones**: sesiones existentes del almacén.
- **Tamaño**: tamaño total de la carpeta en disco, incluidos los datos internos y la papelera.
  Es una medida de espacio ocupado, por lo que su alcance difiere del inventario visible.

El inventario de notas, archivos y sesiones se recalcula al recargar el panel. El tamaño conserva
el cálculo asíncrono de `VaultService`. Los textos de ayuda explican el alcance de los recuentos.

La vista mantiene el diseño de la tarjeta y su altura se adapta al contenido de la rejilla.
