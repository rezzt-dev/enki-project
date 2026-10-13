# Pantalla de arranque

## Qué se ve mientras arranca

Al abrir un vault, la ventana se tapa con `views/shell/StartupScreen.axaml` hasta que la aplicación está
en condiciones de usarse. La pantalla lleva el logo (el personalizado si lo hay, el mismo que el sidebar),
el nombre, el lema, una barra de progreso fina y un rótulo con lo que se está haciendo en cada momento
("Indexando notas", "Preparando Documentos"…). Está traducida a los seis idiomas soportados.

El arranque va en tres fases (ver `MainWindowViewModel.RunSectionWarmUpAsync`):

1. **Índice del vault**, en un hilo de trabajo. Es la fase más larga de un vault grande y es E/S pura, así
   que el hilo de interfaz se queda libre y la pantalla se anima sin cortes. Mientras no hay pasos que
   contar, la barra muestra un segmento que la recorre.
2. **La sección de inicio**, que es la primera que se construye y carga. La cortina se retira aquí, no al
   final: la aplicación queda usable sin esperar a las demás.
3. **El resto de secciones**, ya sin cortina, en los huecos libres del hilo de interfaz. Lo que falte se
   construye al abrir su sección, como funcionaba antes de que existiera la precarga.

Cada tramo de interfaz que dura lo bastante como para notarse devuelve un fotograma entero al compositor
antes de encadenar el siguiente. El límite total de la fase de fondo es de 15 segundos; pasado ese punto se
abandona lo que falte en vez de seguir trabajando.

Con `DebugLoggingEnabled` activo, el arranque deja en el log la duración del índice, la de cada tramo lento,
el momento en que se retiró la cortina y el total de la precarga.

## Colocación de la ventana

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

- Arrancar con un vault grande: la pantalla de arranque debe aparecer de inmediato, con la barra en su fase
  de segmento en movimiento, y la aplicación debe quedar usable en cuanto la sección de inicio está lista —
  mucho antes de que termine la precarga de fondo. No debe verse ninguna sección construyéndose a medias.
- Navegar por las secciones nada más arrancar y comprobar que ninguna aparece en blanco ni a medio cargar
  mientras la precarga de fondo sigue en marcha.
- Arrancar sin vault configurado: no debe verse la pantalla de arranque, solo la de configuración inicial.
- Con dos monitores, cerrar Enki por completo, dejar el cursor en el secundario y arrancar: la ventana
  debe aparecer centrada en ese monitor. Repetir con el principal.
- Repetir con el monitor secundario a la izquierda o encima del principal (coordenadas negativas).
- Repetir con escalas distintas por monitor y comprobar el centrado y la escala de la interfaz.
- Con un solo monitor, comprobar que sigue centrada en su área útil.
- Ocultar en la bandeja y restaurar: debe conservar su ubicación anterior.
