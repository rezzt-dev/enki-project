# Posición de los paneles laterales

Ajustes → Apariencia → Paneles laterales permite elegir **Izquierda** o **Derecha** de forma
independiente para Notas, Archivos, Habilidades y Asistente. Todos empiezan a la izquierda.
El cambio es inmediato; no cierra notas, previsualizaciones ni conversaciones.

La elección afecta a las dos disposiciones de Notas, Archivos y Habilidades, y al historial
del Asistente. Se mantienen el plegado, el ancho, los filtros y la selección existentes.
Los navegadores temporales de Notas y Archivos se abren junto al raíl en el lado elegido;
los botones de plegado y el arrastre del ancho siguen la posición del panel.

## Persistencia e integración

Las cuatro propiedades booleanas `*PanelOnRight` pertenecen a `AppSettings`, con `false` como
valor inicial para archivos antiguos. Siguen la persistencia habitual de preferencias por vault
de `SettingsService`, incluidos la importación/exportación y el cambio de vault.

`SidePanelLayoutViewModel` expone el estado real de esos ajustes a la shell y al formulario.
Un error de guardado restaura el valor anterior y usa las notificaciones existentes.
`SidePanelGrid` cambia únicamente la dirección de distribución de sus hijos; cada contenido
conserva la lectura de izquierda a derecha. El splitter nativo hereda la dirección de distribución;
los tiradores manuales convierten su desplazamiento con `GetResizeDelta`.
Las costuras y los iconos direccionales se adaptan mediante estilos compartidos.

## Comprobación manual

- Elegir cada lado y alternar entre las dos disposiciones de cada sección.
- Abrir una nota, un archivo o una habilidad, cambiar el lado y comprobar que mantiene su estado.
- Plegar, abrir la búsqueda desde el raíl y fijar el navegador en Notas y Archivos.
- Redimensionar desde ambos lados, arrastrar elementos y comprobar su etiqueta flotante.
- Reiniciar, cambiar de vault e importar/exportar los ajustes.
- Buscar «paneles laterales» en Ajustes y recorrer las opciones con teclado.
