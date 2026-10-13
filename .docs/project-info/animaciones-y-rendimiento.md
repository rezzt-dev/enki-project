# Animaciones y rendimiento de la interfaz

En Ajustes → Apariencia → Animaciones se puede elegir **Normales** (por defecto) o
**Sutiles**. La elección se guarda en `AppSettings.UseSubtleAnimations`, admite archivos de
ajustes anteriores y se aplica en vivo. No cambia colores, fuentes, medidas, bordes ni componentes.

| Comportamiento | Normales | Sutiles |
|---|---|---|
| Color, opacidad y desplazamiento del interruptor | 150 ms, CubicEaseOut | 90 ms, CubicEaseOut |
| Entrada de sección | 180 ms, 6 px | 100 ms, solo opacidad |
| Navegación del calendario | 180 ms, solo opacidad | 100 ms, solo opacidad |
| Elevación de tarjetas al pasar el cursor | 4 px (1 px en controles compactos) | Sin desplazamiento |
| Grafo de sesiones | Rotación y partículas; presupuesto de 60 repintados/s | Sin rotación ni partículas; presupuesto de 30 repintados/s |
| Indicador de carga de sesiones | Constelación animada y progreso | Constelación estable y progreso |

Los indicadores funcionales de carga siguen activos en ambos modos. Sutiles reduce el movimiento,
pero no es una opción para desactivar absolutamente todas las animaciones.

## Implementación

- `MotionService` publica recursos dinámicos compartidos: no hay duraciones de interacción dispersas
  en las vistas. Arranque, cambio de preferencia y recarga de ajustes aplican el mismo perfil.
- `MotionReveal` ejecuta una entrada explícita tras construir el destino, agrupa solicitudes del mismo
  ciclo y cancela la anterior al navegar de nuevo, ocultar o desmontar el control. No anima primero
  hacia opacidad cero como hacía el antiguo par de transiciones y callbacks del dispatcher.
- El calendario mantiene fija su rejilla durante el fundido para no alterar coordenadas de arrastre
  o redimensionado mientras entra la vista.
- La navegación prepara las colecciones antes de asignar `CurrentPage`, evitando construir la vista
  para después vaciar y reconstruir sus listas. Pulsar la sección ya activa no vuelve a cargarla.
- El interruptor mueve su representación mediante `RenderTransform`, manteniendo fijo el margen.
- El panel del editor aplica su ancho una sola vez. Interpolar el ancho obligaba a medir y
  redistribuir el Markdown en cada fotograma. Se elimina también la transición de ancho del borde
  interno del sidebar, que no era el elemento cuyo ancho cambiaba.
- Sesiones retira su capa de carga en cuanto llega la señal de disponibilidad, con el fundido del perfil.
  Se elimina la espera mínima anterior de 520 ms y el fundido fijo de 360 ms. La capa deja de capturar
  interacción al comenzar su retirada; se conserva el temporizador de seguridad si el WebView falla.
- El grafo limita el trabajo de canvas, omite efectos decorativos en Sutiles y acota el salto temporal
  tras pausas. El indicador usa memoria de pila para las posiciones de sus diez nodos.
- La pantalla de arranque no gira nada: mueve una barra por `Opacity`, por el ancho de su relleno y por una
  `TranslateTransform` en su fase sin pasos que contar, y ninguno de esos gestos obliga a recalcular la
  geometría de un giro por fotograma. El rótulo y el estado de la carga se actualizan en pasos discretos, así
  que un fotograma perdido no se lee como un tirón —al revés que un spinner, que es lo que peor tolera que el
  hilo de interfaz esté ocupado—. Ver `.docs/project-info/pantalla-de-arranque.md`.

## Validación y medición en Arch Linux

Validación automática completada el 3 de octubre de 2026:

- `dotnet test enki/tests/enki.Tests/enki.Tests.csproj --no-restore`: 2241 pruebas superadas.
- `dotnet test enki/tests/enki.UITests/enki.UITests.csproj --no-restore`: 334 pruebas superadas.
- `git diff --check`: sin errores de formato.

Las pruebas de servicios verifican compatibilidad y persistencia. Las pruebas de interfaz headless
comprueban búsqueda y selección de ambos modos, actualización de transiciones existentes y cancelación
al navegar, ocultar y desmontar el contenido.

La plataforma headless no mide fluidez del compositor ni del WebView nativo. No hay una mejora global
porcentual acreditada: reducir una duración de animación no equivale a acelerar todo el programa.
Para comparar rendimiento real, usar builds Release anterior y nueva, una copia del mismo vault, igual
resolución/zoom y el mismo compositor (Wayland o X11). Registrar apertura en frío y veinte recorridos por
las secciones, tiempo hasta poder interactuar, CPU en reposo/con el grafo y fotogramas lentos. Comparar
mediana y percentil 95; repetir para Normales y Sutiles.

Comprobación visual pendiente en la sesión gráfica del usuario:

- Navegar rápidamente por todas las secciones y los períodos del calendario; no debe quedar contenido
  transparente, desplazado ni aparecer un fundido de salida sobre la nueva sección.
- Cambiar modos, tema y zoom: comprobar tarjetas, interruptores, menús, diálogos y paneles.
- Abrir/cerrar el panel del editor con una nota larga, tablas e imágenes.
- Abrir Sesiones, navegar fuera durante la carga y regresar; probar arrastre, zoom y enlaces del grafo.
- Minimizar y restaurar, y reiniciar para comprobar la preferencia guardada.
