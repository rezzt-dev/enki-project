# Gestión de carpetas en Notas y Archivos

Las dos secciones trabajan sobre las mismas carpetas reales del vault. La gestión está disponible en
las vistas de rejilla y árbol, desde el menú contextual **Gestionar carpeta**. El botón de acciones de
las filas laterales abre el mismo panel.

## Operaciones

- **Crear carpeta o subcarpeta**: pide el nombre antes de escribir en disco; cancelar no crea nada.
- **Renombrar**: desde el panel o mediante el renombrado en línea existente.
- **Mover y copiar**: selector de destino dentro del espacio de trabajo actual, excluyendo la carpeta
  origen y sus descendientes. Copiar/cortar/pegar también acepta carpetas y permite trasladarlas entre
  sesiones. Se conserva la jerarquía completa, incluidos archivos, notas, carpetas vacías y adjuntos.
- **Eliminar**: envía la carpeta completa a la Papelera. Notas confirma siempre esta operación;
  Archivos mantiene el ajuste existente de confirmación antes de borrar.
- **Restaurar y eliminar permanentemente**: categoría Carpetas de la Papelera. Restaurar conserva la
  ubicación original; si ya existe un elemento con ese nombre se añade un sufijo numérico. El padre
  original debe existir y, si pertenece a una sesión cifrada, estar desbloqueado.
- **Abrir ubicación**: abre la carpeta en el explorador del sistema.

Las colisiones nunca fusionan árboles ni sobrescriben archivos. Los nombres rechazan rutas, caracteres
reservados, nombres internos y nombres de dispositivos de Windows. Las raíces del vault y las sesiones
se siguen gestionando desde sus módulos respectivos.

## Integración y protección de datos

`VaultService.Folders.cs` contiene las operaciones. `FolderManagerViewModel` y `FolderManagerView`
comparten el formulario entre las dos secciones, respetando estilos, idiomas y notificaciones existentes.
Los eventos `FolderChanging` y `FolderChanged` se emiten en el hilo del llamante, como el resto de los
eventos del vault; los comandos de interfaz los ejecutan en el hilo de la UI.

Antes de copiar, mover, renombrar o eliminar, las notas abiertas guardan sus cambios por el camino
comprobado del editor (`PrepareFileTransfer`). Un conflicto externo sin resolver cancela la operación.
Tras mover o renombrar se actualizan las pestañas, las previsualizaciones, las rutas de las tarjetas
Kanban y los historiales de las notas. No se reescriben enlaces Markdown externos ni enlaces relativos
que crucen los límites de la carpeta trasladada.

La copia se prepara en un directorio temporal oculto y se publica al completarse. Dentro del mismo
ámbito de cifrado el movimiento usa el traslado de directorio. Entre ámbitos de cifrado se vuelve a
cifrar cada archivo para el destino y se comprueba un inventario con hashes antes de retirar el origen.
Si falla la publicación se conserva o restituye el origen. Las transferencias rechazan enlaces
simbólicos, y el explorador no recorre carpetas enlazadas.

La Papelera reutiliza `.trash-origins.json` para conservar la ubicación de las carpetas eliminadas y su
asociación a sesiones cifradas. No muestra carpetas de sesiones bloqueadas. El contenedor reservado
`.trash/sessions` no se trata como una carpeta de usuario. Restaurar una carpeta con un nombre ocupado
no cambia las pestañas abiertas sobre la carpeta que ya ocupa ese nombre.

## Verificación

- `VaultServiceFoldersTests`: nombres, colisiones, copia recursiva, destinos inválidos, cifrado,
  sesiones bloqueadas, enlaces simbólicos, cancelación y recuperación.
- `FolderManagementTests`: sincronización entre módulos, guardado tras renombrar, conflictos externos,
  confirmación de borrado, portapapeles, destinos y restauración con colisión.
- `FolderManagerUiTests`: las cuatro combinaciones de sección y disposición, con formulario XAML real,
  enlace del campo de nombre y botón de confirmación.
- Se mantienen las pruebas de navegación y previsualización de Archivos y de Papelera.

Verificación manual antes de distribuir: recorrer los flujos con teclado y lector de pantalla, comprobar
ventanas pequeñas y escalado alto, y trasladar carpetas grandes con un sincronizador activo. Las
operaciones de disco son síncronas; una transferencia grande puede ocupar el hilo de interfaz.
