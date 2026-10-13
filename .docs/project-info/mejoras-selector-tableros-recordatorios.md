# Selector de tableros y listado de recordatorios

El selector de Tableros utiliza el popover compartido y separa cabecera, búsqueda,
lista desplazable y acción de creación. El contador refleja los resultados de la
búsqueda. El tablero activo se identifica con una línea lateral y un check; la
etiqueta de predeterminado es independiente de la selección. Los nombres largos
se recortan con elipsis y conservan su tooltip. Las acciones de gestión tienen una
zona propia de 36 px. El buscador recibe el foco al abrir; se conservan flecha abajo,
Intro para elegir y Escape para cerrar y devolver el foco al título.
El bloque de texto queda centrado verticalmente respecto a la acción de gestión.

Cada recordatorio se presenta en una fila enmarcada que incluye su acción de
completar o devolver a pendiente. El título y la descripción admiten dos líneas;
fecha, estado y recurrencia se distribuyen debajo y saltan de línea cuando falta
ancho. Los completados mantienen el título legible y se identifican por su estado
y el icono de acción. Los vencidos usan el token de peligro, además del texto de
estado; el punto de no leído se conserva. Abrir el detalle y completar siguen
siendo acciones independientes.
El hover de completar y de gestión de tableros resalta solo el icono, sin fondo
cuadrado; ambos mantienen su área de clic y el foco visible por teclado.

Ambas vistas reutilizan recursos de color y tipografía del tema, esquinas rectas,
comandos y traducciones existentes. No cambian modelos ni persistencia.

## Validación

Las pruebas de interfaz de `KanbanMultipleBoardsUiTests` y `RemindersViewUiTests`
cubren teclado, búsqueda, nombres largos, muchos tableros, textos de recordatorios,
recurrencia y límites de las acciones a 480 y 1100 px. Se ejecutan con Avalonia
Headless; no sustituyen una revisión visual en una sesión gráfica real.

## Listado de sesiones

El panel de sesiones reserva 260 px y organiza cada fila en dos líneas: color y
nombre arriba, contador de notas y acciones debajo. La rejilla limita el ancho del
nombre, que conserva su tooltip y se recorta con elipsis. Renombrar dispone del
ancho completo de la primera línea. El nombre también es un botón accesible por
teclado para seleccionar la sesión.

Las acciones de bloqueo, renombrado y eliminación tienen áreas de 28 px y resaltan
solo el icono al pasar el puntero. Se conserva el indicador de sesión predeterminada
y la selección con borde lateral. `SessionsListUiTests` comprueba que los nombres
largos y la edición no invadan las acciones ni salgan de la fila.
