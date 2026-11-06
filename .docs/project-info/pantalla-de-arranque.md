# Pantalla de arranque

## Qué se ve mientras arranca

Al abrir un vault, `views/shell/StartupScreen.axaml` presenta una única pantalla con el logo
personalizado del usuario (o el de Enki si no hay uno), la marca y una barra fina. El logo se lee desde
`BrandLogoService` antes de crear la shell, con los mismos ajustes y archivo que utiliza el sidebar.
No se sustituye un logo provisional por otro al construir el ViewModel.

La barra avanza de cero a completo durante **2 segundos**. Después se funde la pantalla de carga
en 130 ms para mostrar la aplicación lista. El relleno se anima mediante una escala horizontal
en el hilo del compositor, sin depender del trabajo de la UI, recalcular el layout ni alternar entre una
barra indeterminada y otra de progreso real. Es una animación temporal de bienvenida, no un porcentaje
de lectura del vault. El trabajo de carga transcurre detrás de ella. Si una carga excepcional supera
ese intervalo, se conserva la misma pantalla hasta que la sección inicial esté lista; nunca se
presenta una segunda ventana ni se descubre una sección incompleta para cumplir el cronómetro.

La bienvenida y el selector de idioma permanecen ocultos cuando el vault guardado está disponible.
La espera no bloquea la UI. Cerrar la ventana cancela las transiciones pendientes para que no
construyan la shell después del cierre.

El primer fotograma ya contiene el fondo opaco y la marca: `PrepareForOpening` los prepara antes de
`Show`, sin disparar temporizadores ni construir servicios. `BeginOpening`, desde `OnOpened`, inicia
la barra y su línea temporal de 2 segundos. El logo permanece quieto. Solo la salida desvanece el fondo; una entrada
transparente dejaría ver la ventana vacía y sus botones. El marco nativo o personalizado se configura
antes de mostrar la ventana. El launcher queda fuera del zoom del vault para que restaurar la escala
no lo redimensione durante la carga.

El arranque va en tres fases (ver `MainWindowViewModel.RunSectionWarmUpAsync`):

1. **Índice del vault**, en un hilo de trabajo. Es la fase más larga de un vault grande y es E/S pura, así
   que el hilo de interfaz se queda libre y la pantalla mantiene la misma animación, sin cambiar de modo.
2. **La sección de inicio**, que es la primera que se construye y carga. Al terminar se solicita retirar
   la cortina, respetando la línea temporal de 2 segundos: la aplicación no espera a las demás secciones.
3. **El resto de secciones**, ya sin cortina, en los huecos libres del hilo de interfaz. Lo que falte se
   construye al abrir su sección, como funcionaba antes de que existiera la precarga.

Cada tramo de interfaz que dura lo bastante como para notarse devuelve un fotograma entero al compositor
antes de encadenar el siguiente. La precarga de fondo deja un margen inicial para que la cortina termine
su permanencia mínima y su salida sin competir con la construcción de otras vistas. Su presupuesto es
de 60 segundos desde que termina la indexación; pasado ese punto se
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
  de avance continuo, y la aplicación debe quedar usable cuando la sección de inicio esté lista
  y termine la animación de 2 segundos —
  mucho antes de que termine la precarga de fondo. No debe verse ninguna sección construyéndose a medias.
- Navegar por las secciones nada más arrancar y comprobar que ninguna aparece en blanco ni a medio cargar
  mientras la precarga de fondo sigue en marcha.
- Arrancar sin vault configurado: no debe verse la pantalla de arranque, solo la de configuración inicial.
- Arrancar con un vault pequeño ya configurado: no debe aparecer la bienvenida ni un destello del selector
  de idioma; la animación debe verse durante al menos 2 segundos antes de dar paso a la sección inicial.
- Comprobar el primer fotograma con barra nativa y personalizada: debe mostrar directamente el logo y
  la barra de carga, sin un fondo vacío con botones ni un cambio de marco. Repetir con zoom del vault distinto
  del 100 %; la animación no debe cambiar de escala mientras se restauran los ajustes.
- Cerrar durante la entrada de la animación: la shell no debe construirse después del cierre.
- Con dos monitores, cerrar Enki por completo, dejar el cursor en el secundario y arrancar: la ventana
  debe aparecer centrada en ese monitor. Repetir con el principal.
- Repetir con el monitor secundario a la izquierda o encima del principal (coordenadas negativas).
- Repetir con escalas distintas por monitor y comprobar el centrado y la escala de la interfaz.
- Con un solo monitor, comprobar que sigue centrada en su área útil.
- Ocultar en la bandeja y restaurar: debe conservar su ubicación anterior.
