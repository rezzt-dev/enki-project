# Grafo de sesiones en 3D: esferas volumétricas

El grafo de **Sesiones** representa cada sesión como una esfera tridimensional formada por sus notas
y sus enlaces reales, suspendida en el plano del visor y girando despacio sobre su propio eje. Las
esferas se colocan en el mismo anillo que usaba el visor 2D, así que la organización general no cambia.

## Por qué no WebGL / Three.js

Se evaluaron Three.js, `3d-force-graph`, `d3-force-3d`, `BufferGeometry`, `InstancedMesh` y raycasting
de Three. Ninguno es viable como motor único:

- `Avalonia.Controls.WebView` 12.1.0 (el paquete que usa la app) solo implementa composición por
  software en Linux. El propio ensamblado contiene «EGL rendering mode is not implemented. Use
  WpeRenderingMode.Auto or WpeRenderingMode.Shm.», comprobado de nuevo para este cambio. Un contexto
  WebGL no puede presentar nada bajo WPE: es el motivo por el que la app ya abandonó `3d-force-graph`
  (ver `sigma-js-graph-migration.md`).
- Un motor WebGL para Windows/macOS más otro de respaldo para Linux duplicaría todo el visor.

La solución es un motor 3D propio y pequeño sobre Canvas 2D, que funciona igual en WebView2, WKWebView
y WPE, y el mismo modelo en el visor nativo de Avalonia (`NativeSessionGraph`, ver
`grafo-compatibilidad-nativa.md`). No es una simulación CSS ni un 2D maquillado: hay coordenadas X/Y/Z
reales, una matriz de rotación por esfera, proyección en perspectiva y pintado ordenado por profundidad.

## Arquitectura

| Capa | Dónde | Responsabilidad |
|---|---|---|
| Parámetros | `SessionSphereParameters` (`SessionSphereProjection.cs`) | Todos los valores ajustables: velocidad, inclinación, perspectiva, radios, separaciones. El documento web los recibe en su carga útil; no se repiten en JavaScript. |
| Distribución | `SessionSphereDistribution` | Posición local de cada nota dentro de su esfera. |
| Malla | `SessionSphereMesh` | Trama decorativa de vecinos espaciales en 3D. |
| Escena | `SessionSphereLayout` → `SessionSphereScene` | Una esfera por sesión (centro, radio, fase), caché de geometría y colocación en anillo. |
| Proyección | `SessionSphereProjection` | `Rx(pitch)·Ry(yaw)`, perspectiva e inversa para el arrastre. |
| Render web | `SessionGraphRenderer` | Documento autocontenido: escena, cámara, proyección, pintor, bucle e interacción. |
| Render nativo | `NativeSessionGraph` | Lo mismo con el `DrawingContext` de Avalonia, sin navegador. |
| Integración | `SessionsView.axaml.cs`, `SessionsViewModel` | Mensajes C#↔JS, elección de renderizador, botón de rotación y persistencia. |

El layout se calcula una vez en C# y ambos renderizadores consumen la misma `SessionSphereScene`, así
que la web y el visor nativo muestran exactamente la misma geometría. `SessionsView` posee una única
instancia de `SessionSphereLayout`: filtrar, recargar el documento o cambiar al visor nativo conserva la
forma y la posición de cada esfera.

## Distribución volumétrica

Cada sesión se distribuye en su propio espacio local, centrado en el origen y sin rotar:

1. **Comunidades**: propagación de etiquetas sobre los enlaces reales de la sesión (determinista, los
   empates eligen la etiqueta menor).
2. **Reparto radial híbrido**: alrededor del 65 % de las notas en una corteza `[0,8 R, R]` con variación
   radial aleatoria estable, y el resto en el interior `[0,08 R, 0,72 R]` con distribución uniforme en
   volumen. La fracción se adapta: las sesiones de hasta 4 notas son todo silueta, las de menos de 12
   pasan gradualmente del 85 % al 65 %, y las redes densas ceden hasta un 10 % más al interior. Las notas
   más conectadas van al interior, de modo que sus enlaces atraviesan el volumen.
3. **Siembra**: espiral de Fibonacci con ruido. Cada comunidad reclama las direcciones libres más cercanas
   a su ancla y ocupa un casquete contiguo. Repartir por índices consecutivos de la espiral formaba bandas
   de latitud y alargaba la esfera en Y.
4. **Refinado por fuerzas** con enfriamiento: repulsión de corto alcance sobre una rejilla espacial
   (O(n) por iteración), resortes en los enlaces, cohesión débil de comunidad y restricción radial hacia
   la profundidad asignada. Termina con una pasada de separación mínima. Las fuerzas solo corren aquí, una
   vez por cambio de notas; la rotación no las vuelve a ejecutar.

No se inventan notas ni enlaces: una sesión de 2 notas es un par en 3D, una de 1 nota es un punto. El radio
crece de forma no lineal, `R = 24·n^0,4`, con un ligero aumento por densidad, acotado entre 36 y 320
unidades de mundo. Una sesión de una sola nota usa 22.

Las métricas medidas con el algoritmo final (Release) son una corteza del 62 al 65 %, una desviación típica
por eje de 0,46 R ± 0,01 (silueta isótropa) y estos tiempos de cálculo, que se cachean después:

| Notas en la sesión | Tiempo |
|---|---|
| 160 | ~45 ms |
| 500 | ~120 ms |
| 2000 | ~0,3 s |

### Estabilidad y posiciones guardadas

- Filtrar, cambiar enlaces o recolorear no recalcula nada: la escena sale de la caché.
- Una nota nueva recalcula solo su sesión, con las demás notas fijadas en su sitio (escaladas si el
  radio crece).
- La colocación en anillo cambia solo si cambian las sesiones o sus radios. Las sesiones ocultas por
  filtros siguen reservando su hueco; una recarga completa sin filtros libera el de las borradas.
- Arrastrar una nota guarda, por el flujo existente (`HandlePositionsReported` → `.session.json`), todas
  las notas visibles de su sesión como coordenadas locales **normalizadas al radio** (módulo ≤ 1). Al
  leerlas, `SessionSphereLayout.TryReadStoredPosition` solo acepta valores con esa forma. Las
  posiciones de los layouts anteriores (2D con Z = 0, o coordenadas de mundo) se ignoran sin migrar el
  archivo, y la sesión se distribuye de nuevo.

## Render

- **Escena compartida**: un único canvas, una única cámara (centro + zoom) y un único bucle
  `requestAnimationFrame` para todas las esferas. Cada esfera tiene su propia transformación
  (`yaw`, `pitch`) y su perspectiva con la cámara a 3,2 radios de su centro.
- **Notas**: impostores esféricos. Cada color tiene un mapa de bits pre-sombreado: luz suave arriba a la
  izquierda, reflejo discreto y borde oscurecido. Como la luz vive en el espacio de la vista, el
  sombreado es correcto con cualquier rotación, y dibujar un mapa de bits cacheado es mucho más barato
  que un degradado por nota en un compositor por software.
- **Profundidad**: tamaño aparente por perspectiva, orden del pintor de atrás hacia delante y
  atenuación en cuatro capas de profundidad. Las aristas se agrupan por capa y estilo, de modo que la
  escena entera cuesta unas pocas llamadas a `stroke()`.
- **Enlaces**: líneas rectas finas que atraviesan el volumen. Los manuales son más gruesos, y los
  enlaces entre sesiones unen esferas distintas.
- **Malla de sesión** (opcional, activa por defecto): k = 3 vecinos espaciales en 3D más los puentes
  imprescindibles. No son enlaces, no cuentan en los resúmenes y su opacidad queda siempre por debajo de
  la de los enlaces reales.
- **Etiquetas**: los nombres de sesión se colocan desde el centro y el radio de su esfera, nunca desde
  sus notas, así que no giran. Los títulos de nota aparecen al acercar o al pasar sobre una nota.

## Rotación

- 0,10 rad/s (una vuelta cada ~63 s) alrededor del eje vertical de cada esfera, con una inclinación
  estática de 0,32 rad y una fase inicial distinta por sesión.
- Usa delta time, acotado a 0,1 s tras una pausa, así que la velocidad no depende de los FPS. Solo
  cambia la orientación del grupo: ni el layout, ni los datos, ni la posición de la esfera.
- La esfera que se está arrastrando, o cuya nota se arrastra, no avanza mientras dura el gesto.

### Botón global

En la barra inferior del visor, junto al zoom, hay un `icon-btn` con `Rotate3dVariant`. Mientras las
esferas giran el icono está en tinta plena (estado `active`); en pausa queda en tinta secundaria. El
tooltip y el nombre accesible describen la acción siguiente («Desactivar rotación» / «Activar
rotación»), y el botón es accesible con el teclado.

- Pausar conserva la orientación de cada esfera y reanudar continúa desde ella, sin reconstruir la
  escena.
- La elección se guarda en `AppSettings.SessionsGraphRotationEnabled`, por vault como el resto de
  preferencias.
- Sin elección guardada (`null`), la rotación arranca activa salvo con el perfil de animaciones
  **Sutiles**. En el visor web, `prefers-reduced-motion: reduce` la detiene durante esa ejecución, sin
  guardarse, y se avisa al viewmodel para que el botón refleje el estado real.

## Interacción

| Gesto | Resultado |
|---|---|
| Clic en una nota | `nodeClick` (abre o, en modo enlace, elige extremo; lo decide el viewmodel) |
| Doble clic en una nota | `nodeDoubleClick`, que abre la nota |
| Arrastrar una nota | La mueve en el plano de la pantalla a su profundidad, dentro de su esfera, y guarda las posiciones |
| Arrastrar el volumen de una esfera | La gira a mano (horizontal y vertical, con la inclinación acotada) |
| Clic en el volumen de una esfera | `sessionClick`, que selecciona la sesión en la lista (marcada con un contorno circular) |
| Arrastrar el fondo / rueda | Desplaza / zoom alrededor del puntero |
| `+` `−` `Inicio` y flechas | Zoom, encuadre y desplazamiento |

El picking es un rayo en pantalla contra los impostores proyectados en el último fotograma: de las
notas cuyo disco contiene el puntero gana la más cercana al observador. Los clics se resuelven con las
posiciones de `pointerdown`/`pointerup`, nunca con una caché de hover, que no era fiable en WPE. El
hover se reevalúa en cada fotograma de rotación para seguir a la nota que queda bajo un cursor quieto.

## Rendimiento y ciclo de vida

- Renderizado bajo demanda: con la rotación detenida y sin animaciones ni interacción, no se pide
  ningún fotograma. La rotación ambiental va a 30 Hz, la interacción directa a 60 Hz (30 Hz en Sutiles)
  y la pestaña oculta suspende el bucle.
- Proyección en arrays tipados reutilizados, sin asignaciones por fotograma, y relación de píxeles
  acotada a 2.
- El tamaño se sigue con `ResizeObserver`. Si el contexto 2D se pierde y se restaura, se regeneran los
  mapas de bits.
- Un error de pintado dentro del documento envía `{ type: 'error' }`, y el host cambia al visor nativo
  en vez de dejar el panel vacío.
- El documento ya no incluye `sigma.min.js` ni `d3-force-standalone.min.js` (unos 130 KB menos por
  render). Solo queda inline la fuente Space Grotesk.

## Mensajes C# ↔ JavaScript

Del host al documento: `__enkiSetGraphData`, `__enkiSetPendingLink`, `__enkiSetSessionColor`,
`__enkiLinkRejected`, `__enkiSetDisplay`, `__enkiCamera`, `setSubtleMotion`, `__enkiSetRotation` y
`__enkiSetSelectedSession`.

Del documento al host: `ready`, `nodeClick`, `nodeDoubleClick`, `positions` (normalizadas),
`sessionClick`, `reducedMotion` y `error`.

## Verificación

- `SessionSphereLayoutTests` cubre:
  - Distribución volumétrica: corteza/interior, profundidades, silueta isótropa y centrado.
  - Separación mínima entre notas.
  - Topología: los nudos van al interior y las notas enlazadas quedan más cerca.
  - Preservación de datos: no se añaden notas ni enlaces.
  - Sesiones de 0 a 4 notas.
  - Determinismo y estabilidad al filtrar, editar enlaces y añadir notas.
  - Posiciones guardadas frente a heredadas, y esferas sin solapes.
  - Grafos grandes, malla y proyección (rotación rígida e inversa).
- `NativeSessionGraphUiTests` cubre:
  - Clic, doble clic, arrastre en profundidad y zoom.
  - Rotación a velocidad constante con delta time.
  - Pausa sin cambios y reanudación sin salto.
  - Etiquetas inmóviles, giro manual, clic en esfera y paneo.
- `GraphPresentationUiTests` cubre el botón global ligado al viewmodel y su persistencia.
- `SessionsViewModelGraphRotationTests` cubre el estado inicial, el perfil Sutiles, el movimiento
  reducido del sistema y la selección por esfera.
- El documento web se validó en Firefox headless con eventos de puntero sintéticos:
  - Clic, doble clic, arrastre con posiciones normalizadas y giro manual.
  - Bucle vivo mientras rota y detenido en pausa.
  - Datos vacíos sin errores.

Pendiente de comprobar a mano en cada sistema (ver `qa-manual-release-candidate.md`):

- Fluidez de la rotación con un vault grande en el WebView real de Linux (WPE por software).
- El lector de pantalla sobre el botón de rotación.
- `prefers-reduced-motion` en Windows y macOS.
