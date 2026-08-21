# Migrar el grafo de sesiones de force-graph a sigma.js v1

Handoff para otro agente. Escrito porque se agotaron los tokens de la sesión que hizo la
investigación — este documento es la fuente de verdad completa, no hace falta releer el
historial de chat.

## Qué hay que hacer

Reemplazar la librería que dibuja el grafo de notas-como-nodos en la pestaña "Sesiones"
(`enki/src/enki/views/sessions/SessionGraphRenderer.cs`) — hoy usa **force-graph** (Canvas2D,
vendored en `enki/src/enki/assets/js/force-graph.min.js`) — por **sigma.js v1**, con estilos
personalizados para calzar con el sistema de diseño de la app (monocromo, dark-first — ver
`docs/sistema-diseno-web.md`). Pedido explícito del usuario, no una sugerencia mía.

Dos requisitos explícitos del usuario que hay que preservar en la nueva implementación:
1. **La línea entre dos nodos conectados tiene que ser recta** (sin curvatura). El código actual
   pone `linkCurvature(0.18)` — no llevar eso a la versión sigma.
2. Nada de three.js / WebGL — ver "Por qué no WebGL" abajo, es un callejón sin salida confirmado,
   no solo sospechado.

## Contexto: el bug que originó esta investigación

El usuario reportó (con captura de pantalla) que la línea que conecta dos nodos se ve
**desconectada/con hueco** respecto al nodo, específicamente mientras corre la **rotación
ambiental automática** del grafo (`rotateGraph()` en `SessionGraphRenderer.cs`, gira todos los
nodos alrededor de su centro de masa, una vuelta completa cada 10 minutos, para siempre, aplicado
en `onRenderFramePre` cada frame).

Investigación ya hecha (no repetir):
- Revisé el orden de renderizado dentro del bundle minificado de force-graph (`tickFrame` en
  `force-graph.min.js`): el círculo del nodo y los extremos de la curva del link leen **las
  mismas coordenadas `n.x`/`n.y` en vivo**, en el mismo frame síncrono. Esto descarta un bug de
  "control points" (`__controlPoints`) desactualizados por orden de ejecución — no es eso.
- Hipótesis líder (**sin confirmar todavía**): el `filter: drop-shadow(...)` en CSS aplicado a
  todo el `<canvas>` (usado para el glow/bloom) obliga al compositor por software (ver siguiente
  sección) a recomponer el **canvas completo** en cada frame repintado, para siempre (porque la
  rotación nunca para y `autoPauseRedraw` está en `false`) — sospecho que eso causa un artefacto
  de pintura parcial/obsoleta bajo ese camino de software.
- Para probar esa hipótesis sin arriesgar el código real, monté una página de comparación A/B
  temporal (sigue en el árbol, ver "Archivos temporales de diagnóstico" abajo) que muestra dos
  WebViews lado a lado: una con el filtro CSS actual, otra con el glow puesto directamente en el
  contexto de canvas (`ctx.shadowColor`/`ctx.shadowBlur` seteados una vez por frame en
  `onRenderFramePre`, en vez de un filtro CSS). **Si esa prueba ya se corrió y dio un veredicto,
  úsalo para decidir qué técnica de glow portar a la versión sigma. Si no, corre esa página
  primero (pestaña "Test: Glow" en el sidebar) o repite la comparación directamente dentro de la
  implementación sigma nueva** — el objetivo final es que el grafo con sigma tampoco reproduzca
  el hueco línea/nodo durante la rotación.

## Por qué no WebGL / three.js (confirmado, no repetir esta investigación)

La app ya había migrado de `3d-force-graph` (WebGL/three.js) a `force-graph` (Canvas2D) porque el
grafo 3D salía en blanco. Confirmé la causa raíz con evidencia directa, dos fuentes independientes:

1. **Strings embebidos en el DLL real** `Avalonia.Controls.WebView` 12.0.1 (el paquete que usa
   este proyecto, ver `enki/src/enki/enki.csproj`):
   ```
   EGL rendering mode is not implemented. Use WpeRenderingMode.Auto or WpeRenderingMode.Shm.
   DMABuf rendering mode is not implemented. Use WpeRenderingMode.Auto or WpeRenderingMode.Shm.
   ```
2. **Documentación oficial de Avalonia** (docs.avaloniaui.net/controls/web/webview-environment):
   en Linux, el backend WPE de `NativeWebView` solo tiene implementado el modo `Shm` (software,
   sin GPU); `Egl` y `DmaBuf` están reservados para el futuro y **siempre** lanzan
   `NotImplementedException` si se seleccionan. Esta elección es **global al proceso**, no por
   documento ni por instancia de WebView. 12.0.1 es la versión más reciente publicada en NuGet —
   no hay una más nueva con EGL implementado.

Conclusión: **WebGL no puede renderizar en absoluto** bajo `NativeWebView` en Linux con este
paquete, sin importar cómo se cargue el documento. No lo vuelvas a intentar sin que antes cambie
la versión del paquete `Avalonia.Controls.WebView` y se confirme que implementa EGL. sigma.js v1
tiene que usarse en su **modo de renderer Canvas2D** (o SVG), nunca el plugin/renderer WebGL que
sigma v1 también ofrece opcionalmente — verifica el nombre exacto de la opción de config contra
el código fuente real vendored (ver siguiente sección), no lo asumas de memoria.

## Diferencia arquitectónica importante: sigma v1 NO hace layout de fuerzas

Esto es lo más importante que hay que decidir antes de escribir código, y no está resuelto en
esta investigación — queda para el próximo agente.

`force-graph` trae **d3-force** empaquetado adentro: el layout físico (repulsión entre nodos,
resortes en los links, la fuerza custom `sessionClusterForce` que agrupa notas de la misma
sesión) viene gratis. **sigma.js v1 es solo un renderer** — no calcula posiciones de layout por
sí solo. Para conseguir un comportamiento equivalente hay dos caminos:

- **Opción A** — vendorizar el plugin separado `sigma.layout.forceAtlas2` (o `sigma.layout.noverlap`)
  como script(s) adicionales inlineados, y reimplementar `sessionClusterForce` contra el sistema
  de hooks de ese plugin (que es distinto al de d3-force).
- **Opción B** — usar **d3-force directamente** (standalone, hay que vendorizarlo aparte — hoy
  solo existe empaquetado adentro de `force-graph.min.js`, el JS de `SessionGraphRenderer.cs` no
  puede llegar a él) solo para calcular posiciones cada tick, y usar sigma v1 solo para dibujar
  esas posiciones ya calculadas. Arquitectónicamente es lo más parecido a lo que hay hoy y
  probablemente el port de menor riesgo, pero implica dos librerías cooperando en vez de una.

Documenta cuál elegiste y por qué al hacer el cambio — no lo dejes implícito.

## Restricciones duras que hay que respetar

- **Todo tiene que quedar inline en el HTML, cero red, cero `file://` para el `<script src>`.**
  `NativeWebView.NavigateToString` se llama con base URL `null` (ver
  `SessionsView.axaml.cs::OnGraphReady`), y todo motor de WebView nativo (WebView2, WKWebView,
  WPE) rechaza cargar recursos `file://` en un documento sin base URL/origen local — por eso
  `force-graph.min.js` se lee como `AvaloniaResource` y se inyecta como texto dentro de un
  `<script>...</script>` (ver `SessionGraphRenderer.BundleCache.GetScript()`), en vez de un
  `<script src="...">`. El bundle de sigma v1 (y cualquier plugin que necesite, p. ej. tooltips o
  forceAtlas2) tiene que vendorizarse igual: archivo `.js` en
  `enki/src/enki/assets/js/` (el glob `<AvaloniaResource Include="assets/**" />` en `enki.csproj`
  ya lo recoge automáticamente, no hace falta tocar el `.csproj`), más su `LICENSE.txt` junto
  (formato: ver `force-graph.LICENSE.txt` como ejemplo — texto MIT plano + URL del repo), leído e
  inlineado con el mismo patrón que `BundleCache`.
- **Nada de paquetes NuGet nuevos** — esto es JS corriendo dentro del documento HTML del
  WebView, no tiene relación con el grafo de dependencias .NET.
- **El contrato público de C# no cambia.** `SessionsView.axaml.cs` (que no hay que tocar) llama a:
  - `SessionGraphRenderer.RenderDocument(SessionGraph graph) : string`
  - `SessionGraphRenderer.BuildSetGraphDataScript(SessionGraph graph) : string`
  - `SessionGraphRenderer.BuildSetPendingLinkScript(string? globalId) : string`
  - `SessionGraphRenderer.BuildLinkRejectedScript(string fromGlobalId, string toGlobalId) : string`

  Estas cuatro firmas tienen que seguir existiendo igual (devuelven strings — HTML completo la
  primera, snippets `window.__enkiXxx(...)` las otras tres) — lo único que cambia es qué pasa
  *adentro* del JS generado (hablar con sigma en vez de con el objeto `Graph` de force-graph).

## Todo lo que la implementación actual hace y hay que preservar

Leer `enki/src/enki/views/sessions/SessionGraphRenderer.cs` completo antes de empezar — es corto
(~670 líneas) y cada comentario ahí explica el *por qué*, no solo el qué. Resumen de
comportamiento a portar:

1. **Datos**: `SessionGraph.Nodes[]` (`GlobalId`, `Title`, `SessionName`, `SessionId`, `X`, `Y`) y
   `.Edges[]` (`FromGlobalId`, `ToGlobalId`, `IsManual`) — ver `BuildPayload`.
2. **Tema en vivo desde recursos de Avalonia** (`GraphTheme.FromApp`): background, colores de
   nodo activo/link/link-manual/partícula/peligro, colores de tooltip — todos resueltos vía
   `Application.Current.TryFindResource` contra las claves de `Colors.axaml`
   (`AppBackgroundAltBrush`, `InkBrush`, `DangerBrush`, `PanelBackgroundBrush`,
   `TextPrimaryBrush`, `Ink20`, etc.), con fallback a hex fijo si el recurso no está (diseño en
   tiempo de diseño). Sistema monocromo dark-first: blanco/"ink" puro a distinta opacidad para
   casi todo, único color real es danger `#F87171`/success `#4ADE80`. Tipografía JetBrains Mono
   para todo texto del grafo/tooltip. La versión sigma tiene que resolver las mismas claves de
   tema de la misma forma, no hardcodear colores.
3. **Nodos**: radio base `NODE_REL_SIZE = 4.6`; el nodo con un link pendiente se dibuja más
   grande y con `THEME.nodeActive`; brillo determinístico por sesión vía hash FNV-1a
   (`NodeBrightness.ColorFor`, blanco a 0.60–1.00 alpha) en vez de un hue de color por sesión.
4. **Links**: manuales gruesos/opacos (`THEME.linkManual`, ancho 2.4) vs. auto-derivados de
   wikilinks tenues (`THEME.link` alpha 0.3, ancho 1.1); partículas direccionales de "flujo" (2
   vs 1, ancho 2.2 vs 1.1, velocidad 0.003). **Curvatura 0 (recta) — no la de 0.18 actual.**
5. **Área de click generosa e invisible por nodo** (`HIT_AREA_MULTIPLIER = 3.5×` el radio
   visual). Existe porque en el backend WPE, la detección de click integrada de force-graph
   (depende de eventos `pointermove` en reposo para mantener un `hoverObj` fresco) nunca llegaba
   de forma confiable dentro de este WebView embebido — los clicks se re-derivaron manualmente de
   `pointerdown`/`pointerup` (posición + tiempo) en vez de confiar en el binding de click nativo
   de la librería (ver el bloque `pointerDownAt`/`canvasPoint`/`nearestNodeWithin`).
   **Sigma v1 tiene su propio evento `clickNode` — verificar empíricamente, corriendo la app de
   verdad dentro del WebView WPE real, si sufre el mismo problema ("hover-move nunca llega a un
   WebView embebido"). Si sí, portar el mismo fallback manual de pointerdown/pointerup en vez de
   confiar en el binding de click de sigma.**
6. **Rotación ambiental**: una vuelta cada 10 min, pausada mientras se arrastra un nodo, aplicada
   mutando `x`/`y` de cada nodo directamente cada frame — el mecanismo bajo sospecha del bug
   reportado. Independiente del motor de layout/física que sea (ver la decisión de arquitectura
   Opción A/B arriba).
7. **Glow/bloom**: hoy vía `filter: drop-shadow(...)` en CSS sobre el `<canvas>` — sospechoso del
   bug. La corrección bajo prueba es `ctx.shadowColor`/`ctx.shadowBlur` puestos directamente en
   el contexto 2D una vez por frame. **Portar la que el A/B test haya confirmado que no reproduce
   el hueco línea/nodo durante rotación** (o repetir la comparación ya sobre sigma si no hay
   veredicto todavía).
8. **Etiquetas flotantes de sesión**: sobre cualquier sesión con ≥2 notas *enlazadas entre sí*
   (`LABEL_MIN_LINKED_NODES`), tamaño escalado con raíz cuadrada del tamaño del cluster
   (13–24px), centradas arriba del cluster y siguiéndolo mientras se mueve.
9. **Radios de afinidad de sesión**: de cada nota de una sesión con 2+ notas (enlazadas o no)
   hacia el centro de masa de esa sesión, blanco puro, dibujados **debajo** de los nodos/links
   reales (para que el color/ancho de un link real siga leyéndose encima) — es una relación más
   débil que un link real, puramente visual, más una fuerza `sessionClusterForce` (fuerza 0.025)
   que empuja notas de la misma sesión a acercarse sin dominar sobre los links reales ni la
   repulsión entre nodos.
10. **Anillo de pulso** para el nodo con link pendiente (`__enkiSetPendingLink`) y flash de
    rechazo (`__enkiLinkRejected`) — overlays puramente cosméticos, disparados desde .NET vía
    `NativeWebView.InvokeScript`, no atados a los datos reales del grafo.
11. **Actualización incremental sin recargar la página**: `__enkiSetGraphData(json)` reemplaza
    los datos del grafo ya cargado (llamado desde `SessionsView.OnGraphUpdated` vía
    `BuildSetGraphDataScript`), y reencuadra la cámara 260ms después.
12. **Reporte de posiciones al host** una vez que el layout se asienta (`onEngineStop` →
    `reportPositions()` → `post({type:'positions', nodes})`), consumido por
    `SessionsView.HandleGraphMessage`/`SessionsViewModel.HandlePositionsReported` — **mantener
    exactamente esta forma de mensaje** (`{type:'positions', nodes:[{path, x, y, z}]}`, `z`
    siempre 0 por ser un grafo 2D) porque el lado C# depende de ese JSON exacto.
13. **Cada click, en cualquier modo, se reporta como** `{type:'nodeClick', path: node.id}` — el
    WebView nunca decide abrir-vs-enlazar por sí mismo, eso lo hace
    `SessionsViewModel.HandleNodeClicked` del lado host — mantener esa separación de
    responsabilidades, no mover lógica de decisión al JS.
14. **Función puente `post(message)`**: intenta `window.invokeCSharpAction` primero (confirmado
    que es lo que el control WebView de Avalonia inyecta), y cae en
    `chrome.webview.postMessage` / `webkit.messageHandlers.postAvWebViewMessage` /
    `external.notify` como fallback defensivo.
15. **Tooltip** mostrando `"{título} — {sesión}"` al pasar el mouse, hoy estilizado vía la clase
    CSS `.float-tooltip-kap` (nombre propio del plugin de tooltip de force-graph). sigma v1 no
    tiene ese plugin exacto — portar un tooltip equivalente al pasar el mouse (sigma v1 tiene su
    propio mecanismo de hover, o existe el plugin conocido `sigma.plugins.tooltips` —
    **verificar contra el código fuente real vendorizado, no asumir la API de memoria**),
    estilizado para calzar (JetBrains Mono, mayúsculas, letter-spacing, fondo/borde/blur
    igual a `THEME.tooltip*`).

## Archivos a tocar

- **Reescribir**: `enki/src/enki/views/sessions/SessionGraphRenderer.cs` — `RenderDocument`,
  `BuildSetGraphDataScript`, `BuildSetPendingLinkScript`, `BuildLinkRejectedScript`,
  `GraphTheme`, `NodeBrightness`, `BundleCache`, todos apuntando a la API de sigma v1 en vez de
  la de force-graph.
- **Vendorizar**: `enki/src/enki/assets/js/sigma.min.js` (nombre real a confirmar contra el
  paquete/repo de sigma v1 — históricamente `sigma.min.js`) + su LICENSE, y cualquier plugin que
  termines necesitando (tooltips, forceAtlas2 si eliges la Opción A de layout) como archivos
  adicionales inlineados en el mismo documento HTML.
- **No tocar**: `SessionsView.axaml.cs`, `SessionsView.axaml`, `SessionsViewModel.cs`, el modelo
  `SessionGraph` — mientras el contrato público de `SessionGraphRenderer` (4 métodos + forma de
  los mensajes JSON) se preserve, nada de esto necesita cambios.

## Archivos temporales de diagnóstico (ya eliminados)

Se habían creado durante la investigación del bug de rotación, usando **force-graph** (no sigma),
para comparar filtro CSS vs. glow nativo en canvas mediante una pestaña "Test: Glow" de
comparación lado a lado en la propia app. La migración a sigma quedó verificada usando glow nativo
en canvas (ver `sigma.canvas.nodes.enkiGlow` en `SessionGraphRenderer.cs`), así que la pestaña de
diagnóstico y todo su código (`GraphGlowTestRenderer.cs`, `GraphGlowTestView.axaml(.cs)`,
`GraphGlowTestViewModel.cs`, el valor de enum `AppPage.GraphGlowTest` y su `NavItem`/caso en
`Navigate`/`DataTemplate`) fueron eliminados.

## Notas de entorno / build

- **No hay SDK de .NET disponible** en el sandbox donde se investigó esto (`dotnet` no está
  instalado en absoluto) — no se pudo compilar ni correr la app para validar nada de este
  documento a nivel visual, solo revisión estática de código y del bundle JS. El próximo agente
  tiene que compilar con `dotnet build enki/src/enki/enki.csproj` (o abrir con Rider — hay
  proyecto `.idea` versionado) y **correr la app de verdad en Linux** (requiere WPE/webkit2gtk —
  `WebViewHost.IsEngineAvailable()` bloquea todo esto si no está) para verificar visualmente. Esto
  es un bug de renderizado/interacción — revisión de tipos no alcanza, hay que verlo corriendo.
- El directorio raíz **no es un repositorio git** (`git status` falla ahí) — no hay
  branch/diff que revisar para "qué cambió desde"; este documento es la fuente de verdad completa
  del handoff.
- Convención de idioma del repo: strings visibles al usuario (labels de nav, texto de UI) en
  español; comentarios de código en inglés, densos, explicando el *por qué* de una decisión no
  solo el qué — igualar el registro de los archivos existentes al escribir los nuevos.

## Próximo paso recomendado

1. Si la comparación CSS-filter-vs-glow-nativo (pestaña "Test: Glow") no se corrió todavía,
   córrela primero — es rápida y ya está lista, no depende de la migración a sigma.
2. Decide y documenta la estrategia de layout (Opción A: forceAtlas2, Opción B: d3-force
   standalone + sigma solo como renderer) antes de escribir el resto.
3. Reescribe `SessionGraphRenderer.cs` contra sigma v1, portando cada comportamiento de la lista
   de arriba uno por uno (no es necesario hacerlo en el mismo orden, pero no te saltes ninguno).
4. Verifica corriendo la app de verdad: el grafo se ve, los nodos son arrastrables, el click
   abre/enlaza notas, la rotación ambiental **no** produce el hueco línea/nodo reportado
   originalmente, y el tema visual calza con el resto de la app.
5. Limpia los archivos temporales de diagnóstico listados arriba.
