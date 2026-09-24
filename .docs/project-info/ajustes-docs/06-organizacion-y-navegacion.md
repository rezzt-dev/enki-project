# Organización y navegación de Ajustes

La navegación se organiza por intención del usuario. El buscador sigue siendo global y opcional;
cualquier ajuste es accesible desde una sección. El título, la descripción y el aviso de guardado
permanecen visibles mientras se desplaza el formulario. El menú tiene desplazamiento independiente
y conserva el título y el campo de búsqueda.

| Grupo | Sección | Contenido |
| --- | --- | --- |
| Aplicación | General | Idioma, página de inicio y bandeja del sistema |
| Aplicación | Apariencia | Temas, fuentes de interfaz, zoom, barra de título y logo |
| Aplicación | Navegación | Visibilidad, orden y anclaje de las secciones laterales |
| Aplicación | Atajos de teclado | Personalización y referencia de teclas |
| Aplicación | Notificaciones | Paneles de avisos, categorías, recordatorios, sonido y no molestar |
| Notas y datos | Vault y archivos | Carpeta, espacio utilizado y confirmación de borrado |
| Notas y datos | Copias de seguridad | Copias manuales y automáticas, restauración y traslado de ajustes |
| Notas y datos | Cuenta Polar | Cuenta vinculada, sincronización y etiquetas de tareas Polar |
| Herramientas | Notas y editor | Lectura, escritura, fuentes, tamaño y plantillas diarias |
| Herramientas | Dashboard | Distribución, widgets y barra de desplazamiento |
| Herramientas | Kanban | Orientación del tablero y filtro de etiquetas |
| Herramientas | Calendario | Vista inicial, semana, horario y panel lateral |
| Herramientas | Skills de IA | Visibilidad y catálogo comunitario |
| Ayuda y mantenimiento | Actualizaciones y diagnóstico | Versiones, registros y restablecimiento |
| Ayuda y mantenimiento | Acerca de Enki | Producto, tecnología y privacidad |

Crear copias de seguridad ya no aparece dentro de una zona de peligro. El restablecimiento conserva
su acción de confirmación y el borde de advertencia, separado de las operaciones habituales.
Los controles trasladados conservan sus bindings, servicios y callbacks; no hay cambios de formato
de persistencia ni migración de preferencias.

## Comportamiento

- Seleccionar una sección limpia la búsqueda y muestra únicamente su formulario desde el principio.
- El menú indica la selección activa y ofrece una descripción de cada destino al pasar el puntero.
- La búsqueda conserva encabezados de sección para situar cada resultado.
- Los nombres, grupos, descripciones y encabezados se actualizan al cambiar de idioma.
- Se mantienen las fuentes, recursos de color, botones y tarjetas rectangulares del sistema visual.

## Verificación

Las pruebas de interfaz recorren todos los destinos usando sus botones sin depender del buscador,
comprueban la ubicación de las opciones trasladadas y ejercitan las búsquedas existentes. Las pruebas
del viewmodel verifican los grupos y sus traducciones en todos los idiomas disponibles.
