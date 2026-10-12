# Pantalla de arranque

Al iniciar Enki, la ventana principal se centra en la pantalla donde está el cursor justo antes de
mostrarla. No se registra el último clic. La ventana de error de arranque sigue el mismo criterio.

`StartupWindowPlacement` consulta la posición global mediante las APIs de Windows, macOS o X11 y la
asigna a `Window.Position` antes del primer `Show`. `WindowStartupLocation.CenterScreen` utiliza esa
posición para elegir el monitor y calcular el centrado sobre su área útil con la escala correspondiente.
No se modifica el tamaño de la ventana ni se reposiciona después de aparecer.

Si no se puede consultar el cursor o no se encuentra una pantalla para esa posición, se conserva el
comportamiento de arranque de Avalonia. En Wayland nativo no se consulta X11: la colocación depende del
compositor. Restaurar una ventana existente desde la bandeja conserva su ubicación.

## Comprobación manual

- Con dos monitores, cerrar Enki por completo, dejar el cursor en el secundario y arrancar: la ventana
  debe aparecer centrada en ese monitor. Repetir con el principal.
- Repetir con el monitor secundario a la izquierda o encima del principal (coordenadas negativas).
- Repetir con escalas distintas por monitor y comprobar el centrado y la escala de la interfaz.
- Con un solo monitor, comprobar que sigue centrada en su área útil.
- Ocultar en la bandeja y restaurar: debe conservar su ubicación anterior.
