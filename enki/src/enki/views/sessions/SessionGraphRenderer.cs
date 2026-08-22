using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using enki.Models;

namespace enki.Views.Sessions;

/// <summary>
/// Builds the self-contained HTML document that hosts the session graph — a 2D network of notes-as-nodes.
/// Renders with the vendored <c>sigma.js v1</c> Canvas 2D renderer (MIT, see <c>assets/js/sigma.LICENSE.txt</c>)
/// for drawing, plus a standalone <c>d3-force v3</c> build (ISC, see
/// <c>assets/js/d3-force-standalone.LICENSE.txt</c>) for the physics layout — sigma v1 has no force layout of
/// its own, unlike the <c>force-graph</c> bundle this replaced, which packaged d3-force internally. This is a
/// deliberate architecture choice ("Option B" from the migration handoff, see
/// <c>docs/agent-docs/sigma-js-graph-migration.md</c>): vendor d3-force on its own and keep sigma purely as the
/// renderer, rather than porting <c>sessionClusterForce</c> against sigma's separate forceAtlas2 plugin — this
/// is the lower-risk port since it keeps the exact same tuned physics model (charge, link, custom session
/// clustering, alpha cooling) the app already had, just swapping the paint layer underneath it.
///
/// Both bundles are Canvas 2D only, never the WebGL renderer sigma also ships (optionally) — this app's Linux
/// WebView backend (WPE) only implements software (Shm) compositing; GPU rendering modes throw
/// <c>NotImplementedException</c>. That's why the app moved off the WebGL-based <c>3d-force-graph</c> in the
/// first place, and it's exactly as true for sigma's WebGL renderer, so it's never loaded here.
///
/// Both bundles are inlined directly into <c>&lt;script&gt;</c> tags rather than referenced via a
/// <c>file://</c> <c>src</c>: this document is loaded with <c>NavigateToString</c> and no base URL, and every
/// native WebView engine (WebView2, WKWebView, WPE) refuses to load local <c>file://</c> resources from a
/// document with no base URL/local origin.
/// </summary>
public static class SessionGraphRenderer
{
    // IncludeFields is required here: GraphTheme is a plain-field class (no { get; set; } properties), and
    // System.Text.Json only serializes public *properties* by default — without this, Serialize(theme, ...)
    // silently produces "{}", every THEME.xxx read in the JS below is undefined, and Canvas2D quietly
    // ignores an invalid fillStyle/strokeStyle assignment and keeps its default black — so nodes and links
    // rendered pure black on the dark background, indistinguishable from the graph not rendering at all.
    // CamelCase is required too: the fields are PascalCase (Background, NodeActive, ...) but the JS reads
    // camelCase (THEME.background, THEME.nodeActive, ...); BuildPayload's anonymous node/link objects
    // already use lowercase property names, so this policy is a no-op for them.
    private static readonly JsonSerializerOptions DataJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true
    };

    public static string RenderDocument(SessionGraph graph)
    {
        var sigmaScript = VendoredScripts.Get("sigma.min.js");
        var d3ForceScript = VendoredScripts.Get("d3-force-standalone.min.js");
        var theme = GraphTheme.FromApp();
        // A note title, session name or path that happens to contain the literal substring "</script"
        // (e.g. a note about HTML/JS) would otherwise close this document's <script> tag early the same
        // way an un-escaped vendored bundle would (see VendoredScripts.Get's identical guard) — confirmed
        // by rendering a payload with such a title in a real browser: the tag closes mid-JSON, the rest of
        // the document prints as literal page text, and the following <script> block fails to parse at
        // all ("Invalid or unexpected token"), so the graph never loads. Unlike the vendored bundles,
        // nothing upstream already guards these two since they're built fresh from user data on every call.
        var dataJson = JsonSerializer.Serialize(BuildPayload(graph), DataJsonOptions)
            .Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
        var themeJson = JsonSerializer.Serialize(theme, DataJsonOptions)
            .Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);

        return $$"""
                <!doctype html>
                <html>
                <head>
                <meta charset="utf-8" />
                <style>
                  html, body { margin: 0; padding: 0; overflow: hidden; background: {{theme.Background}}; }
                  #graph { position: relative; width: 100vw; height: 100vh; }
                  #graph canvas, #graph > div { position: absolute; inset: 0; width: 100%; height: 100%; }
                  #graph-overlay { pointer-events: none; }
                  .enki-graph-tooltip {
                    position: fixed;
                    z-index: 20;
                    pointer-events: none;
                    font-family: 'JetBrains Mono', ui-monospace, monospace;
                    font-size: 10.5px;
                    text-transform: uppercase;
                    letter-spacing: 0.08em;
                    background: {{theme.TooltipBackground}};
                    color: {{theme.TooltipForeground}};
                    border: 1px solid {{theme.TooltipBorder}};
                    padding: 7px 11px;
                    box-shadow: 0 10px 30px rgba(0,0,0,0.5);
                    display: none;
                    white-space: nowrap;
                  }
                </style>
                <script>{{sigmaScript}}</script>
                <script>{{d3ForceScript}}</script>
                </head>
                <body>
                <div id="graph"></div>
                <script>
                (function () {
                  const DATA = {{dataJson}};
                  const THEME = {{themeJson}};
                  // Which note (if any) is the first pick of a pending link pair — purely a visual cue
                  // (the pulse ring drawn in drawOverlays below). The host decides whether a click opens a
                  // note or picks/completes a link (see SessionsViewModel.HandleNodeClicked); this page just
                  // reports every click as-is and, best-effort, mirrors the host's pending pick back here
                  // for the pulse via __enkiSetPendingLink so the click logic never has to trust that a
                  // fire-and-forget InvokeScript call actually landed.
                  let pendingLinkNode = null;
                  let rejectFlash = null; // { from, to, until, duration } — brief error pulse when a link is refused

                  const NODE_REL_SIZE = 4.6;
                  const PENDING_VAL = 2.6;
                  // How much bigger a node's *clickable* area is than its drawn radius, and (see below) the
                  // radius used for hover/tooltip lookups too. Both click and drag resolution here work by
                  // finding the nearest node to the pointer in graph space (see nearestNodeWithin) rather
                  // than trusting any hover-cache — see the big comment above the pointerdown listener for
                  // why, and HIT_AREA_MULTIPLIER just keeps that generous instead of pixel-exact so a
                  // slightly-off click still lands.
                  const HIT_AREA_MULTIPLIER = 3.5;
                  const CLICK_RADIUS = NODE_REL_SIZE * HIT_AREA_MULTIPLIER;

                  // Session-name labels only appear over a session once it has at least this many notes
                  // linked to each other — an isolated note (or a session with no links yet) isn't a
                  // cluster worth naming. Font size (and every other overlay measurement below) is expressed
                  // in world units and multiplied by SCREEN_SCALE() at draw time — deliberately, so it grows
                  // and shrinks with the canvas zoom exactly like the nodes do, instead of staying a fixed
                  // screen size.
                  const LABEL_MIN_LINKED_NODES = 2;
                  const LABEL_BASE_SIZE = 13;
                  const LABEL_MAX_SIZE = 24;

                  // Slow, constant orbit of the whole graph around its own center of mass — a full turn
                  // every 10 minutes is intentionally well past "notice it moving," more of an ambient sense
                  // that the scene is alive. Applied as a plain rotation of each node's (x, y) once per
                  // animation frame (see rotateGraph/the main loop below), independent of the d3-force
                  // simulation itself, so it keeps going forever, including after the layout has settled and
                  // the simulation has stopped ticking.
                  const ROTATION_PERIOD_MS = 10 * 60 * 1000;
                  const ROTATION_SPEED = (Math.PI * 2) / ROTATION_PERIOD_MS; // radians / ms
                  let isDraggingNode = false;
                  let lastRotationTs = null;

                  // Avalonia.Controls.WebView injects window.invokeCSharpAction on every adapter (WebView2,
                  // WKWebView, GTK/WPE) as the one cross-platform way to reach WebMessageReceived.Body on
                  // the host side — the other paths below are only a defensive fallback in case a future
                  // adapter changes that convention.
                  function post(message) {
                    const json = JSON.stringify(message);
                    try { if (typeof window.invokeCSharpAction === 'function') { window.invokeCSharpAction(json); return; } } catch (e) {}
                    try { if (window.chrome && window.chrome.webview) { window.chrome.webview.postMessage(json); return; } } catch (e) {}
                    try { if (window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.postAvWebViewMessage) { window.webkit.messageHandlers.postAvWebViewMessage.postMessage(json); return; } } catch (e) {}
                    try { if (window.external && window.external.notify) { window.external.notify(json); return; } } catch (e) {}
                  }

                  // ===================== DOM layers =====================
                  // sigma only draws nodes/edges/labels into its own renderer container. Everything this page
                  // draws *underneath* those (the session-affinity spokes, item 9 below) or *on top* of them
                  // (particles, pulse rings, session-name labels) lives on two extra canvases stacked via
                  // plain CSS z-order (see #graph canvas, #graph > div in the style block above) — cheaper
                  // and far simpler than hooking into sigma's own per-node/edge draw calls, and it means
                  // ordering is just DOM order, not something to get right inside a render callback.
                  const el = document.getElementById('graph');

                  const affinityCanvas = document.createElement('canvas');
                  el.appendChild(affinityCanvas);
                  const affinityCtx = affinityCanvas.getContext('2d');

                  const sigmaHost = document.createElement('div');
                  el.appendChild(sigmaHost);

                  const overlayCanvas = document.createElement('canvas');
                  overlayCanvas.id = 'graph-overlay';
                  el.appendChild(overlayCanvas);
                  const overlayCtx = overlayCanvas.getContext('2d');

                  const tooltipEl = document.createElement('div');
                  tooltipEl.className = 'enki-graph-tooltip';
                  document.body.appendChild(tooltipEl);

                  function sizeOverlayCanvases() {
                    const w = el.clientWidth || window.innerWidth;
                    const h = el.clientHeight || window.innerHeight;
                    const dpr = window.devicePixelRatio || 1;
                    [affinityCanvas, overlayCanvas].forEach(function (c) {
                      c.width = w * dpr;
                      c.height = h * dpr;
                      c.getContext('2d').setTransform(dpr, 0, 0, dpr, 0, 0);
                    });
                  }
                  sizeOverlayCanvases();
                  window.addEventListener('resize', sizeOverlayCanvases);

                  // ===================== Custom node/edge renderers (glow) =====================
                  // A soft bloom drawn straight into the canvas 2D context via ctx.shadowColor/shadowBlur,
                  // set once per shape right before it's painted — the fix confirmed by an A/B comparison
                  // against the production bug this migration exists to fix: a CSS `filter: drop-shadow(...)` applied to
                  // the *whole* canvas element forces the software (Shm-only, no GPU under this app's Linux
                  // WebView backend) compositor to recomposite the entire canvas every repainted frame,
                  // forever (the ambient rotation below never stops), and that's what produced the
                  // line/node desync artifact reported in the original bug. Setting the shadow directly on
                  // the drawing context instead reaches every shape drawn through it with no extra
                  // compositor pass at all — sigma v1's per-type custom renderer hooks
                  // (sigma.canvas.nodes.<type> / sigma.canvas.edges.<type>, selected via defaultNodeType /
                  // defaultEdgeType below) are the supported way to add this without forking the bundle.
                  //
                  // Glow is node-only, deliberately: edges stay crisp, unblurred lines (see the edge
                  // renderer below) so the graph doesn't read as a hazy blob once dozens of links overlap —
                  // the bloom is reserved for drawing the eye to notes (spheres), not the connections
                  // between them.
                  const GLOW_COLOR = 'rgba(255,255,255,0.4)';
                  const GLOW_BLUR = 12;

                  // context.shadowBlur is by far the single most expensive thing this page draws: every
                  // browser's Canvas2D backend has to run an actual blur convolution over the shape's
                  // bounding box, and under this app's software-only (Shm, no GPU) WebView backend that cost
                  // is paid on the CPU, per node, every single animation frame, forever (the main loop below
                  // never stops — ambient rotation, particles and the pending-link pulse all need to keep
                  // animating even once the layout itself has settled). With shadowBlur set the naive way
                  // (right on the live context, as the original implementation above did), frame time scales
                  // with node count directly through that convolution — the first thing to fall over as a
                  // vault's note count grows.
                  //
                  // Every node's glow is *identical* except for its screen radius (which only changes on
                  // zoom/pending-state) and fill alpha (one of a small, fixed set of per-session values from
                  // NodeBrightness) — GLOW_COLOR/GLOW_BLUR themselves never vary. That means the actual
                  // *blurred* pixels only ever take a handful of distinct shapes at once, so the expensive
                  // part — running shadowBlur once to produce that shape — can be done a single time per
                  // shape and reused: rendered once into a small offscreen canvas (a "sprite"), cached by
                  // (rounded radius, fill color), and every subsequent frame just blits the already-blurred
                  // bitmap via drawImage — one of the cheapest calls a Canvas2D context has, since there's no
                  // convolution left to run, just a pixel copy. Visually identical output, far less work.
                  const glowSprites = new Map();
                  // Screen radius changes continuously while the camera is zooming (mouse wheel, pinch, the
                  // zoomToFit tween), so new (radius, color) keys keep appearing and old ones stop being
                  // reused — capped here so a long session of zooming in and out doesn't grow this into an
                  // unbounded pile of small canvases. Clearing the whole cache is fine, not just cheap: the
                  // handful of sizes/colors actually on screen at any moment get re-cached again within a
                  // frame or two, same as a cold start.
                  const GLOW_SPRITE_CACHE_LIMIT = 300;

                  function getGlowSprite(radius, fillStyle) {
                    const r = Math.max(1, Math.round(radius));
                    const key = r + '|' + fillStyle;
                    const cached = glowSprites.get(key);
                    if (cached) return cached;

                    if (glowSprites.size >= GLOW_SPRITE_CACHE_LIMIT) glowSprites.clear();

                    // Padding gives the blur room to fall off before it hits the sprite canvas's own edge —
                    // too little and the glow reads as a hard-clipped square halo instead of a soft falloff.
                    const padding = GLOW_BLUR * 3;
                    const size = r * 2 + padding * 2;
                    const spriteCanvas = document.createElement('canvas');
                    spriteCanvas.width = size;
                    spriteCanvas.height = size;
                    const spriteCtx = spriteCanvas.getContext('2d');
                    spriteCtx.shadowColor = GLOW_COLOR;
                    spriteCtx.shadowBlur = GLOW_BLUR;
                    spriteCtx.fillStyle = fillStyle;
                    spriteCtx.beginPath();
                    spriteCtx.arc(size / 2, size / 2, r, 0, Math.PI * 2, true);
                    spriteCtx.closePath();
                    spriteCtx.fill();

                    const sprite = { canvas: spriteCanvas, half: size / 2 };
                    glowSprites.set(key, sprite);
                    return sprite;
                  }

                  sigma.canvas.nodes.enkiGlow = function (node, context, settings) {
                    const prefix = settings('prefix') || '';
                    const sprite = getGlowSprite(node[prefix + 'size'], node.color || settings('defaultNodeColor'));
                    context.drawImage(sprite.canvas, node[prefix + 'x'] - sprite.half, node[prefix + 'y'] - sprite.half);
                  };

                  // Straight line, deliberately — no curvature. The original force-graph implementation set
                  // linkCurvature(0.18); that's an explicit hard requirement *not* to carry over here.
                  // No shadow here — glow is reserved for nodes only (see GLOW_COLOR/GLOW_BLUR above).
                  //
                  // Trimmed to stop exactly at each endpoint's node radius rather than running from center
                  // to center: the node itself is a semi-transparent fill (see NodeBrightness — alpha
                  // 0.60-1.00, never fully opaque) with a soft shadowBlur glow around its edge, so a
                  // full-length line drawn underneath (sigma's canvas renderer paints edges before nodes)
                  // was visible bleeding through the circle's fill and glow all the way to its center.
                  // Shortening the segment itself is robust regardless of paint order or node opacity,
                  // unlike clipping/masking tricks against the node's own draw call.
                  sigma.canvas.edges.enkiGlow = function (edge, source, target, context, settings) {
                    const prefix = settings('prefix') || '';
                    const sx = source[prefix + 'x'], sy = source[prefix + 'y'];
                    const tx = target[prefix + 'x'], ty = target[prefix + 'y'];
                    const sr = source[prefix + 'size'] || 0;
                    const tr = target[prefix + 'size'] || 0;
                    const dx = tx - sx, dy = ty - sy;
                    const dist = Math.sqrt(dx * dx + dy * dy);
                    // Nodes close enough (or overlapping) that trimming both radii would flip the segment's
                    // direction — nothing real to draw between two circles that already touch/overlap.
                    if (dist <= sr + tr) return;
                    const ux = dx / dist, uy = dy / dist;

                    context.save();
                    context.strokeStyle = edge.color || settings('defaultEdgeColor');
                    context.lineWidth = edge[prefix + 'size'] || 1;
                    context.beginPath();
                    context.moveTo(sx + ux * sr, sy + uy * sr);
                    context.lineTo(tx - ux * tr, ty - uy * tr);
                    context.stroke();
                    context.restore();
                  };

                  // ===================== sigma instance =====================
                  const s = new sigma({
                    renderers: [{ container: sigmaHost, type: 'canvas' }],
                    settings: {
                      // sigma defaults to continuously re-fitting the whole graph's bounding box into the
                      // container on every refresh() (its own "rescale" middleware) — fine for a static
                      // graph, but this page calls refresh() every animation frame forever (see the main
                      // loop below), and refitting the bounding box every frame while nodes drift under the
                      // ambient rotation would fight any manual pan/zoom and jitter the effective scale.
                      // Turning it off makes sigma use a plain 1:1 copy of raw (x, y) instead — the same
                      // simple camera model force-graph used, where zoom/pan is one transform over fixed
                      // graph-space coordinates.
                      autoRescale: false,
                      // Graph/node objects are shared, live references between this page's own `nodes` array
                      // and sigma's internal graph — required so the d3-force simulation (which mutates
                      // node.x/node.y in place every tick) is immediately visible to sigma without any sync
                      // step. sigma's default ('clone: true') would copy every node into a separate object on
                      // read(), silently breaking that link.
                      clone: false,
                      immutable: false,
                      // Both powRatio settings at 1 make node/edge screen size scale exactly linearly with
                      // zoom (screenSize = worldSize / camera.ratio) — the same one-transform-for-everything
                      // behavior force-graph had, instead of sigma's default sub-linear size scaling (0.5),
                      // which is tuned for "stay legible at any zoom" graphs rather than a world-space scene.
                      nodesPowRatio: 1,
                      edgesPowRatio: 1,
                      minNodeSize: 0,
                      maxNodeSize: 0,
                      minEdgeSize: 0,
                      maxEdgeSize: 0,
                      defaultNodeType: 'enkiGlow',
                      defaultEdgeType: 'enkiGlow',
                      defaultNodeColor: THEME.link,
                      defaultEdgeColor: THEME.link,
                      // No permanent on-canvas node text — matches the original, which only ever showed a
                      // note's title in the hover tooltip (see tooltipEl below) plus the coarser
                      // session-cluster labels drawn separately in drawSessionLabels.
                      drawLabels: false,
                      drawEdgeLabels: false,
                      // sigma's own built-in double-click zooms the camera toward the click point. Double
                      // click on a node now has a real, different meaning (open the note — see
                      // handleNodeDoubleClick below), so it's turned off here to avoid the camera also
                      // zooming underneath the note that's about to open. Nothing else in this file relied
                      // on it (touch/mouse-wheel zoom are untouched).
                      // Built-in hovering drives sigma's own hover halo/label and is what
                      // sigma.plugins.dragNodes (not used here — see the pointerdown listener below) keys
                      // off to know which node a drag started on. Both depend on overNode/outNode, which
                      // sigma fires from *idle mousemove* events read against a live hover cache — the exact
                      // same shape of dependency that made force-graph's built-in click detection
                      // unreliable under this app's WPE WebView backend (see the pointerdown listener
                      // below). Click, drag and tooltip hover are all reimplemented manually instead, so
                      // sigma's own hover machinery is just switched off rather than left to race it.
                      enableHovering: false,
                      mouseWheelEnabled: true,
                      doubleClickEnabled: false,
                      touchEnabled: true,
                      zoomMin: 0.03,
                      zoomMax: 6
                    }
                  });

                  // ===================== Graph data =====================
                  // `nodes` is the one array of live node objects shared by sigma (drawing) and d3-force
                  // (physics) — see the `clone: false` comment above for why that sharing works. `sigmaEdges`
                  // is what sigma's own graph holds (source/target stay as plain string ids, exactly as
                  // supplied); `forceLinks` is a *separate* set of plain {source, target} objects handed to
                  // d3.forceLink, because forceLink mutates link.source/link.target from a string id into
                  // the resolved node object the first time it runs — sharing edge objects between the two
                  // would silently corrupt sigma's own source/target ids.
                  let nodes = [];
                  let sigmaEdges = [];
                  let forceLinks = [];

                  function buildNodes(payload) {
                    return payload.nodes.map(function (n) {
                      return {
                        id: n.id,
                        label: n.name,
                        session: n.session,
                        baseColor: n.color,
                        color: n.color,
                        x: n.x,
                        y: n.y,
                        size: NODE_REL_SIZE
                      };
                    });
                  }

                  function buildSigmaEdges(links) {
                    return links.map(function (l, i) {
                      return {
                        id: 'e' + i + ':' + l.source + '->' + l.target,
                        source: l.source,
                        target: l.target,
                        manual: l.manual,
                        // Manual links (the ones a person actually drew by clicking two notes) are rendered
                        // clearly bolder than auto-derived wikilink edges — a thin, near-transparent line was
                        // easy to miss entirely, which read as "linking doesn't do anything" even once a link
                        // genuinely existed in the data.
                        color: l.manual ? THEME.linkManual : THEME.link,
                        size: l.manual ? 2.4 : 1.1
                      };
                    });
                  }

                  // The node pending a link is rendered noticeably larger, not just recolored — a subtle
                  // color swap was easy to miss, which is what made "click a note, click another to link
                  // them" read as broken even though the message-passing underneath worked fine. Recomputed
                  // only when pendingLinkNode actually changes (not every frame — see __enkiSetPendingLink).
                  function applyNodeVisualState() {
                    nodes.forEach(function (n) {
                      const isPending = n.id === pendingLinkNode;
                      n.size = NODE_REL_SIZE * Math.sqrt(isPending ? PENDING_VAL : 1);
                      n.color = isPending ? THEME.nodeActive : n.baseColor;
                    });
                  }

                  nodes = buildNodes(DATA);
                  sigmaEdges = buildSigmaEdges(DATA.links);
                  forceLinks = DATA.links.map(function (l) { return { source: l.source, target: l.target }; });
                  applyNodeVisualState();
                  s.graph.read({ nodes: nodes, edges: sigmaEdges });

                  // Tells the host the graph has actual nodes/edges to paint, so it can dismiss the
                  // "loading graph" skeleton it shows from the moment NavigateToString is called (see
                  // SessionsView.OnGraphReady) — WebView engine startup plus parsing/executing this whole
                  // inlined document (sigma, d3-force, this script) is the real source of latency on a
                  // large vault, not anything after this point, so signalling here rather than after the
                  // first animation frame brackets that cost without waiting on the (purely cosmetic,
                  // never-ending) render loop below.
                  post({ type: 'ready' });
                  // Id -> node lookup, rebuilt whenever `nodes` itself is replaced (see __enkiSetGraphData) —
                  // used by drawParticles/drawPulseRing below to resolve an id without an O(n) scan.
                  let nodeById = new Map(nodes.map(function (n) { return [n.id, n]; }));

                  // ===================== Force layout (d3-force, standalone) =====================
                  // sigma v1 does no layout of its own — this simulation is the entire physics engine, same
                  // as the d3-force instance force-graph used to run internally. Charge/link tuning carried
                  // over as-is; forceCenter is new (force-graph's setup implicitly had one) and just keeps
                  // the whole graph from drifting away from the origin over time, which matters more here
                  // since nothing else recenters the camera except the explicit zoomToFit calls below.
                  const simulation = d3.forceSimulation(nodes)
                    .force('charge', d3.forceManyBody().strength(-95))
                    .force('link', d3.forceLink(forceLinks).id(function (n) { return n.id; }))
                    .force('center', d3.forceCenter(0, 0))
                    .force('sessionCluster', sessionClusterForce)
                    .on('end', reportPositions);

                  // ===================== Click & drag (manual pointer resolution) =====================
                  // Both are resolved here from raw pointer events instead of sigma's own click/drag paths
                  // (sigma's built-in `clickNode` event and the sigma.plugins.dragNodes plugin, which this
                  // page deliberately does not vendor). The reason is the same one documented at length in
                  // the force-graph version of this file: on the WPE WebView backend this app runs under on
                  // Linux, node interaction that depends on a *hover cache built from idle mousemove events*
                  // was observed to be unreliable — dragging worked (d3-drag resolves its subject directly
                  // from the mousedown position, no hover cache involved) but a plain click on a node did
                  // not (force-graph's built-in click read a `hoverObj` computed from the last pointermove).
                  // sigma.plugins.dragNodes turns out to have *exactly* that same shape of dependency: its
                  // mousedown handler only gets attached once an `overNode` event has already fired for that
                  // node (see the plugin's nodeMouseOver/_hoverStack), so it's exposed to the identical risk
                  // that broke force-graph's click. Rather than ship it and hope, drag is folded into the
                  // same pointerdown-position + pointerup-position resolution already proven reliable for
                  // clicks — nearestNodeWithin runs directly against current node positions in graph space,
                  // never against a cached hover state.
                  let pointerDownAt = null; // { x, y, t } in canvas-local pixels
                  let dragCandidate = null;
                  let dragConfirmed = false;
                  let hoverNode = null;
                  let lastHoverCheckTs = 0;
                  const HOVER_THROTTLE_MS = 40;

                  // Double-click-to-open detection, resolved from the same manual pointerdown/pointerup
                  // click already used for single clicks (see the big comment above) rather than the
                  // browser's native 'dblclick' event — sigma's built-in doubleClickEnabled zoom is turned
                  // off above specifically so this page owns double-click meaning end to end, and staying
                  // on the same pointer-event-derived click resolution keeps both gestures consistent under
                  // whatever hover/click quirks this embedded WPE WebView has.
                  let lastClickNodeId = null;
                  let lastClickAt = 0;
                  const DOUBLE_CLICK_MS = 380;

                  function screenPoint(e) {
                    const rect = el.getBoundingClientRect();
                    return { x: e.clientX - rect.left, y: e.clientY - rect.top };
                  }

                  // Inverse of sigma's own camera.applyView() transform (graph space -> screen space), used
                  // here to go the other way. sigma never rotates its camera in this app (angle stays 0), so
                  // this deliberately only implements the angle=0 case rather than a full matrix inverse.
                  function screenToWorld(p) {
                    const cam = s.camera;
                    const w = sigmaHost.clientWidth || 1;
                    const h = sigmaHost.clientHeight || 1;
                    return {
                      x: (p.x - w / 2) * cam.ratio + cam.x,
                      y: (p.y - h / 2) * cam.ratio + cam.y
                    };
                  }

                  // Forward direction of the same transform, for drawing overlays (particles, pulse rings,
                  // spokes, labels) in sync with wherever sigma is currently drawing the real nodes/edges.
                  function worldToScreen(x, y) {
                    const cam = s.camera;
                    const w = sigmaHost.clientWidth || 1;
                    const h = sigmaHost.clientHeight || 1;
                    return {
                      x: (x - cam.x) / cam.ratio + w / 2,
                      y: (y - cam.y) / cam.ratio + h / 2
                    };
                  }

                  // World-units-to-screen-pixels multiplier, matching nodesPowRatio/edgesPowRatio = 1 above —
                  // used to scale anything drawn on the overlay/affinity canvases that should grow and shrink
                  // with zoom exactly like sigma's own nodes/edges do.
                  function screenScale() {
                    return 1 / s.camera.ratio;
                  }

                  function nearestNodeWithin(worldPoint, maxDistance) {
                    let closest = null, closestDistSq = maxDistance * maxDistance;
                    for (const n of nodes) {
                      const dx = n.x - worldPoint.x, dy = n.y - worldPoint.y;
                      const distSq = dx * dx + dy * dy;
                      if (distSq <= closestDistSq) { closest = n; closestDistSq = distSq; }
                    }
                    return closest;
                  }

                  el.addEventListener('pointerdown', function (e) {
                    if (e.button !== 0) return;
                    const p = screenPoint(e);
                    pointerDownAt = { x: p.x, y: p.y, t: Date.now() };
                    dragCandidate = nearestNodeWithin(screenToWorld(p), CLICK_RADIUS);
                    dragConfirmed = false;
                  }, { capture: true });

                  el.addEventListener('pointermove', function (e) {
                    const p = screenPoint(e);

                    if (pointerDownAt && dragCandidate) {
                      const movedPx = Math.hypot(p.x - pointerDownAt.x, p.y - pointerDownAt.y);
                      if (!dragConfirmed && movedPx > 6) {
                        dragConfirmed = true;
                        isDraggingNode = true;
                        // Pausing sigma's own camera-drag captor while a node is actively being dragged
                        // keeps the cursor "stuck" to the node the user is holding — without this, the
                        // background pan captor (which listens on the same mousedown/mousemove) would also
                        // fire, dragging the camera underneath the node at the same time.
                        s.settings('mouseEnabled', false);
                      }
                      if (dragConfirmed) {
                        const world = screenToWorld(p);
                        dragCandidate.x = world.x;
                        dragCandidate.y = world.y;
                        dragCandidate.fx = world.x;
                        dragCandidate.fy = world.y;
                        simulation.alphaTarget(0.05).restart();
                        tooltipEl.style.display = 'none';
                        return;
                      }
                    }

                    // Hover/tooltip — best-effort, same idle-mousemove caveat as everything else that
                    // depends on pointer movement reaching this embedded WebView (see the big comment
                    // above). Not load-bearing for any real interaction, unlike click/drag. Throttled: this
                    // is an O(n) scan over every node, and pointermove fires far more often than the tooltip
                    // needs to update — at native mousemove frequency this was a measurable source of input
                    // lag on graphs with a few hundred notes.
                    const now = Date.now();
                    if (now - lastHoverCheckTs < HOVER_THROTTLE_MS) return;
                    lastHoverCheckTs = now;

                    const node = nearestNodeWithin(screenToWorld(p), CLICK_RADIUS);
                    hoverNode = node;
                    if (node) {
                      tooltipEl.textContent = node.label + ' — ' + node.session;
                      tooltipEl.style.left = (e.clientX + 14) + 'px';
                      tooltipEl.style.top = (e.clientY + 14) + 'px';
                      tooltipEl.style.display = 'block';
                    } else {
                      tooltipEl.style.display = 'none';
                    }
                  }, { capture: true });

                  el.addEventListener('pointerleave', function () {
                    hoverNode = null;
                    tooltipEl.style.display = 'none';
                  });

                  el.addEventListener('pointerup', function (e) {
                    if (e.button !== 0 || !pointerDownAt) return;
                    const down = pointerDownAt;
                    const candidate = dragCandidate;
                    pointerDownAt = null;
                    dragCandidate = null;

                    if (dragConfirmed) {
                      dragConfirmed = false;
                      isDraggingNode = false;
                      candidate.fx = null;
                      candidate.fy = null;
                      simulation.alphaTarget(0);
                      s.settings('mouseEnabled', true);
                      return;
                    }

                    const p = screenPoint(e);
                    const movedPx = Math.hypot(p.x - down.x, p.y - down.y);
                    // A real drag (moving a node, or panning the camera) shouldn't also register as a click
                    // on release, and a long press-and-hold isn't a click either.
                    if (movedPx > 6 || Date.now() - down.t > 800) return;

                    const node = nearestNodeWithin(screenToWorld(p), CLICK_RADIUS);
                    if (!node) return;

                    handleNodeClick(node);

                    const now = Date.now();
                    if (lastClickNodeId === node.id && now - lastClickAt <= DOUBLE_CLICK_MS) {
                      handleNodeDoubleClick(node);
                      // Reset rather than let this click seed a new pair — otherwise three quick clicks on
                      // the same node would fire a second double-click off the 2nd+3rd instead of requiring
                      // a fresh pair.
                      lastClickNodeId = null;
                      lastClickAt = 0;
                    } else {
                      lastClickNodeId = node.id;
                      lastClickAt = now;
                    }
                  }, { capture: true });

                  function reportPositions() {
                    const reported = nodes.map(function (n) { return { path: n.id, x: n.x, y: n.y, z: 0 }; });
                    post({ type: 'positions', nodes: reported });
                  }

                  // Every click, in every mode, is just reported — the host (SessionsViewModel.HandleNodeClicked)
                  // is the one place that decides what it means, since it's the one place that always has an
                  // up-to-date view of link mode and the pending pick (see the note on pendingLinkNode above).
                  function handleNodeClick(node) {
                    post({ type: 'nodeClick', path: node.id });
                  }

                  // Reported alongside (never instead of) the plain nodeClick above — the host always opens
                  // the note on a double click, in any mode (see SessionsViewModel.HandleNodeDoubleClicked).
                  function handleNodeDoubleClick(node) {
                    post({ type: 'nodeDoubleClick', path: node.id });
                  }

                  // ===================== Ambient rotation =====================
                  // Rotates every node's (x, y) a hair further around the graph's current center of mass —
                  // called once per animation frame (see the main loop below), before that frame's redraw.
                  // Runs forever, independent of whether the d3-force simulation itself is still ticking
                  // (d3-force stops calling its own listeners once alpha decays below alphaMin — this loop
                  // does not), which is what makes the orbit continue after the layout settles instead of
                  // freezing the moment the physics stops.
                  function rotateGraph(dt) {
                    if (dt <= 0 || isDraggingNode) return;

                    let cx = 0, cy = 0, count = 0;
                    for (const n of nodes) { cx += n.x; cy += n.y; count++; }
                    if (count === 0) return;
                    cx /= count; cy /= count;

                    const angle = ROTATION_SPEED * dt;
                    const cos = Math.cos(angle), sin = Math.sin(angle);
                    for (const n of nodes) {
                      const dx = n.x - cx, dy = n.y - cy;
                      n.x = cx + dx * cos - dy * sin;
                      n.y = cy + dx * sin + dy * cos;
                    }
                  }

                  // ===================== Session groupings (cached) =====================
                  // Two different groupings of "notes in the same session," built together in one pass since
                  // they share the same source loop, and cached — recomputing either from scratch every
                  // frame (a fresh Set + Map churn, 60 times a second forever thanks to the animation loop
                  // never stopping) was pure waste. Rebuilt on demand only when the graph's node/link *set*
                  // changes (see invalidateGroupings, called from __enkiSetGraphData below). Member
                  // *positions* still move every frame (force layout, rotation), so those are re-aggregated
                  // on every draw call — only membership itself is cached:
                  //  - linkedSessionClusters: sessions with LABEL_MIN_LINKED_NODES+ notes already linked to
                  //    one another — real relationships, used for the floating session-name label.
                  //  - allSessionGroups: every note in a session with 2+ notes, linked or not — this is the
                  //    weaker "these just happen to live in the same session" affinity, used for the faint
                  //    scaffold in drawSessionAffinity and the gentle clustering force below. Keeping it
                  //    separate from real links means it never touches the actual link data the "X ENLACES"
                  //    chip and manual-link toggling depend on — it's purely a rendering/layout aid.
                  let linkedSessionClusters = null;
                  let allSessionGroups = null;
                  function invalidateGroupings() { linkedSessionClusters = null; allSessionGroups = null; }
                  function ensureGroupings() {
                    if (linkedSessionClusters && allSessionGroups) return;

                    const linkedIds = new Set();
                    sigmaEdges.forEach(function (l) {
                      linkedIds.add(l.source);
                      linkedIds.add(l.target);
                    });

                    const bySession = new Map();
                    const byLinkedSession = new Map();
                    nodes.forEach(function (n) {
                      if (!bySession.has(n.session)) bySession.set(n.session, []);
                      bySession.get(n.session).push(n);

                      if (!linkedIds.has(n.id)) return;
                      if (!byLinkedSession.has(n.session)) byLinkedSession.set(n.session, []);
                      byLinkedSession.get(n.session).push(n);
                    });

                    linkedSessionClusters = new Map();
                    byLinkedSession.forEach(function (members, session) {
                      if (members.length >= LABEL_MIN_LINKED_NODES) linkedSessionClusters.set(session, members);
                    });

                    allSessionGroups = new Map();
                    bySession.forEach(function (members, session) {
                      if (members.length >= 2) allSessionGroups.set(session, members);
                    });
                  }

                  // Labels the cluster of notes belonging to each session, once that session has at least
                  // LABEL_MIN_LINKED_NODES notes linked to one another — centered above the cluster's
                  // current center of mass, so it drifts along with the force simulation instead of pinning
                  // to a stale spot. Grows with the cluster (sqrt, not linear, and capped) so a session with
                  // a handful of notes doesn't dwarf one with dozens. Drawn on the overlay canvas, on top of
                  // sigma's own nodes/edges.
                  function drawSessionLabels() {
                    const clusters = linkedSessionClusters;
                    if (clusters.size === 0) return;

                    const scale = screenScale();
                    overlayCtx.save();
                    overlayCtx.textAlign = 'center';
                    overlayCtx.textBaseline = 'bottom';
                    overlayCtx.fillStyle = THEME.ink70;
                    clusters.forEach(function (members, session) {
                      let sx = 0, minY = Infinity;
                      members.forEach(function (n) { sx += n.x; if (n.y < minY) minY = n.y; });
                      const cx = sx / members.length;
                      const worldSize = Math.min(LABEL_MAX_SIZE, LABEL_BASE_SIZE + Math.sqrt(members.length - LABEL_MIN_LINKED_NODES) * 3);
                      const size = worldSize * scale;

                      const top = worldToScreen(cx, minY);
                      overlayCtx.font = size + "px 'JetBrains Mono', ui-monospace, monospace";
                      overlayCtx.fillText(session.toUpperCase(), top.x, top.y - size * 0.9);
                    });
                    overlayCtx.restore();
                  }

                  // A "these live in the same session" scaffold — every note in a session with 2+ notes gets
                  // a solid white spoke to the session's own center of mass, whether or not it has any real
                  // (wikilink/manual) edge to anything. It's answering "which session is this note in," not
                  // "what does it reference," so it stays visually distinct from a real link even at high
                  // opacity — pure white, uncolored by session, vs. THEME.link/THEME.linkManual's tinted
                  // rgba. Drawn on the affinity canvas, a separate layer stacked *behind* sigma's own
                  // renderer (see the DOM layers section above) so the real nodes/links always paint over
                  // these spokes without needing any pre-render hook into sigma itself.
                  const AFFINITY_ALPHA = 0.8;

                  function drawSessionAffinity() {
                    if (allSessionGroups.size === 0) return;

                    const scale = screenScale();
                    affinityCtx.save();
                    affinityCtx.strokeStyle = THEME.ink80;
                    affinityCtx.globalAlpha = AFFINITY_ALPHA;
                    affinityCtx.lineWidth = 0.6 * scale;
                    allSessionGroups.forEach(function (members) {
                      let cx = 0, cy = 0;
                      members.forEach(function (n) { cx += n.x; cy += n.y; });
                      cx /= members.length; cy /= members.length;
                      const center = worldToScreen(cx, cy);

                      affinityCtx.beginPath();
                      members.forEach(function (n) {
                        const p = worldToScreen(n.x, n.y);
                        affinityCtx.moveTo(center.x, center.y);
                        affinityCtx.lineTo(p.x, p.y);
                      });
                      affinityCtx.stroke();
                    });
                    affinityCtx.restore();
                  }

                  // Gently pulls notes toward their own session's center of mass every simulation tick, on
                  // top of whatever the real link force and node-repulsion (charge) forces already do —
                  // otherwise two same-session notes with no real link between them have nothing pulling
                  // them toward each other at all, and the affinity spokes above would be drawn crossing the
                  // whole canvas at random rather than reading as a loose, local cluster. Deliberately weak
                  // (a nudge, not a clamp) so real links — and the charge force keeping everything from
                  // collapsing into a single point — still dominate the final layout. Registered directly as
                  // a d3-force force function (not via d3's .initialize() protocol) since it closes over
                  // `nodes` directly, same pattern the force-graph version of this file already used.
                  const SESSION_CLUSTER_STRENGTH = 0.025;

                  function sessionClusterForce(alpha) {
                    ensureGroupings();
                    allSessionGroups.forEach(function (members) {
                      let cx = 0, cy = 0;
                      members.forEach(function (n) { cx += n.x; cy += n.y; });
                      cx /= members.length; cy /= members.length;

                      members.forEach(function (n) {
                        n.vx = (n.vx || 0) + (cx - n.x) * SESSION_CLUSTER_STRENGTH * alpha;
                        n.vy = (n.vy || 0) + (cy - n.y) * SESSION_CLUSTER_STRENGTH * alpha;
                      });
                    });
                  }

                  // ===================== Directional flow particles =====================
                  // sigma v1 has no built-in equivalent of force-graph's linkDirectionalParticles, so this
                  // hand-rolls the same idea: a handful of dots per edge, each a [0, 1) progress value along
                  // the (straight — see the edge renderer above) line from source to target, advanced every
                  // frame and wrapped back to 0. Manual links get more/wider/brighter particles than
                  // auto-derived ones, same ratio as before (2 vs 1 particles, 2.2 vs 1.1 width).
                  const PARTICLE_SPEED = 0.003; // fraction of the link's length crossed per ms
                  let particleState = [];

                  function buildParticleState() {
                    particleState = sigmaEdges.map(function (edge) {
                      const count = edge.manual ? 2 : 1;
                      const progress = [];
                      for (let i = 0; i < count; i++) progress.push(i / count);
                      return { edge: edge, progress: progress, width: edge.manual ? 2.2 : 1.1 };
                    });
                  }

                  function drawParticles(dt) {
                    const scale = screenScale();
                    overlayCtx.save();
                    overlayCtx.fillStyle = THEME.particle;
                    particleState.forEach(function (state) {
                      const source = nodeById.get(state.edge.source);
                      const target = nodeById.get(state.edge.target);
                      if (!source || !target) return;

                      state.progress = state.progress.map(function (t) {
                        const next = t + PARTICLE_SPEED * dt;
                        return next - Math.floor(next);
                      });
                      state.progress.forEach(function (t) {
                        const wx = source.x + (target.x - source.x) * t;
                        const wy = source.y + (target.y - source.y) * t;
                        const p = worldToScreen(wx, wy);
                        overlayCtx.beginPath();
                        overlayCtx.arc(p.x, p.y, (state.width / 2) * scale, 0, Math.PI * 2, false);
                        overlayCtx.fill();
                      });
                    });
                    overlayCtx.restore();
                  }

                  // ===================== Pending-link pulse & reject flash =====================
                  // Drawn on the overlay canvas, on top of everything else — purely cosmetic, so it can't
                  // interfere with click/drag hit-testing (which never reads these pixels, see
                  // nearestNodeWithin above). Ring line width and blur are constant screen-space values,
                  // deliberately *not* multiplied by screenScale() like the rest of this file's overlays —
                  // this is drawn directly in screen pixels already (via worldToScreen), so no compensation
                  // is needed to keep it a crisp, constant size on screen at any zoom level.
                  function drawOverlays() {
                    if (pendingLinkNode) drawPulseRing(pendingLinkNode, THEME.nodeActive);
                    if (rejectFlash) {
                      const remaining = rejectFlash.until - Date.now();
                      if (remaining <= 0) { rejectFlash = null; }
                      else {
                        const fade = remaining / rejectFlash.duration;
                        drawPulseRing(rejectFlash.from, THEME.danger, fade);
                        drawPulseRing(rejectFlash.to, THEME.danger, fade);
                      }
                    }
                  }

                  function drawPulseRing(nodeId, color, fadeOverride) {
                    const node = nodeById.get(nodeId);
                    if (!node) return;
                    const p = worldToScreen(node.x, node.y);
                    const t = (Date.now() % 1200) / 1200;
                    const pulse = 1 + 0.25 * Math.sin(t * Math.PI * 2);
                    const fade = fadeOverride === undefined ? 1 : fadeOverride;
                    const baseR = NODE_REL_SIZE * Math.sqrt(PENDING_VAL) * screenScale();
                    overlayCtx.save();
                    overlayCtx.beginPath();
                    overlayCtx.arc(p.x, p.y, baseR * 1.55 * pulse, 0, 2 * Math.PI, false);
                    overlayCtx.lineWidth = 1.6;
                    overlayCtx.strokeStyle = color;
                    overlayCtx.globalAlpha = 0.75 * fade;
                    overlayCtx.shadowColor = color;
                    overlayCtx.shadowBlur = 16;
                    overlayCtx.stroke();
                    overlayCtx.restore();
                  }

                  window.__enkiSetPendingLink = function (globalId) {
                    pendingLinkNode = globalId || null;
                    applyNodeVisualState();
                  };

                  window.__enkiSetGraphData = function (json) {
                    const data = JSON.parse(json);
                    pendingLinkNode = null;

                    // Preserve the current position of every note that already existed instead of snapping
                    // the whole graph back to the server-supplied coordinates — those are only used as the
                    // starting point the very first time a note is seen, same as before.
                    const existingById = nodeById;
                    nodes = data.nodes.map(function (n) {
                      const prev = existingById.get(n.id);
                      return {
                        id: n.id,
                        label: n.name,
                        session: n.session,
                        baseColor: n.color,
                        color: n.color,
                        x: prev ? prev.x : n.x,
                        y: prev ? prev.y : n.y,
                        size: NODE_REL_SIZE
                      };
                    });
                    applyNodeVisualState();
                    nodeById = new Map(nodes.map(function (n) { return [n.id, n]; }));

                    sigmaEdges = buildSigmaEdges(data.links);
                    forceLinks = data.links.map(function (l) { return { source: l.source, target: l.target }; });
                    buildParticleState();
                    invalidateGroupings();

                    s.graph.clear();
                    s.graph.read({ nodes: nodes, edges: sigmaEdges });

                    simulation.nodes(nodes);
                    simulation.force('link', d3.forceLink(forceLinks).id(function (n) { return n.id; }));
                    // Re-heat the simulation so newly-added or newly-linked notes actually move toward their
                    // settled position instead of sitting wherever they were dropped.
                    simulation.alpha(0.6).restart();

                    // A new link can turn several loose notes into one bigger cluster (a "brain") — pull
                    // the camera back to fit it instead of leaving the view cropped at the old extent. The
                    // short delay gives the simulation a beat to settle the newly-connected nodes toward
                    // each other before measuring the bounding box to fit.
                    setTimeout(function () { zoomToFit(600, 90); }, 260);
                  };

                  // A manual link was refused host-side (e.g. the two notes live in different sessions) —
                  // the click still has to *do* something visible, or it reads as the click not registering.
                  window.__enkiLinkRejected = function (fromId, toId) {
                    rejectFlash = { from: fromId, to: toId, until: Date.now() + 900, duration: 900 };
                  };

                  // ===================== Camera fit =====================
                  function zoomToFit(duration, paddingPx) {
                    if (nodes.length === 0) return;
                    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
                    nodes.forEach(function (n) {
                      if (n.x < minX) minX = n.x;
                      if (n.x > maxX) maxX = n.x;
                      if (n.y < minY) minY = n.y;
                      if (n.y > maxY) maxY = n.y;
                    });

                    const cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
                    const bboxW = Math.max(1, maxX - minX), bboxH = Math.max(1, maxY - minY);
                    const w = sigmaHost.clientWidth || 1, h = sigmaHost.clientHeight || 1;
                    const availW = Math.max(1, w - paddingPx * 2), availH = Math.max(1, h - paddingPx * 2);
                    let ratio = Math.max(bboxW / availW, bboxH / availH);
                    ratio = Math.max(s.settings('zoomMin'), Math.min(s.settings('zoomMax'), ratio));

                    sigma.misc.animation.camera(s.camera, { x: cx, y: cy, ratio: ratio }, { duration: duration });
                  }

                  // ===================== Main animation loop =====================
                  // Runs forever (there is no autoPauseRedraw-style opt-out here, deliberately — same as the
                  // force-graph version): the ambient rotation, the pending-node pulse and the flow particles
                  // all need to keep animating long after the force simulation itself has settled and
                  // stopped calling its own 'tick' listeners. Every frame: rotate, ask sigma to repaint
                  // itself from the current node positions, then redraw both overlay canvases in sync via
                  // the same camera transform sigma just used.
                  //
                  // refresh() is called *without* skipIndexation, even though click/drag/hover resolution
                  // here never uses sigma's own quadtree (see nearestNodeWithin above) — the canvas renderer
                  // itself also reads that same quadtree every render() to decide which nodes/edges count as
                  // "on screen" at all (camera.getRectangle + quadtree.area, see sigma.renderers.canvas.js).
                  // Skipping indexation left that query permanently empty, so nothing ever got painted —
                  // found by comparing a real render against the theory in a throwaway Playwright harness
                  // before wiring this into the actual WebView (see the migration handoff doc for why: no
                  // .NET SDK/display was available when the original investigation happened, so getting this
                  // kind of thing wrong silently was the expected failure mode to guard against).
                  function frame(ts) {
                    // Skip the whole redraw (rotation math, sigma's full-graph repaint, both overlay
                    // canvases) while the WebView isn't actually visible — e.g. the app minimized or this
                    // page backgrounded some other way. requestAnimationFrame itself keeps firing here
                    // regardless (still scheduled below) so the loop resumes the instant it's visible again;
                    // resetting lastRotationTs avoids the ambient rotation jumping by however long it was
                    // hidden, all in one frame, once it does.
                    if (document.hidden) {
                      lastRotationTs = null;
                      requestAnimationFrame(frame);
                      return;
                    }

                    const dt = lastRotationTs === null ? 0 : ts - lastRotationTs;
                    lastRotationTs = ts;

                    rotateGraph(dt);
                    s.refresh();

                    ensureGroupings();
                    const w = affinityCanvas.width, h = affinityCanvas.height;
                    const dpr = window.devicePixelRatio || 1;
                    affinityCtx.clearRect(0, 0, w / dpr, h / dpr);
                    overlayCtx.clearRect(0, 0, w / dpr, h / dpr);
                    drawSessionAffinity();
                    drawSessionLabels();
                    drawParticles(dt);
                    drawOverlays();

                    requestAnimationFrame(frame);
                  }

                  buildParticleState();
                  requestAnimationFrame(frame);
                  setTimeout(function () { zoomToFit(500, 90); }, 400);
                })();
                </script>
                </body>
                </html>
                """;
    }

    /// <summary>Pushes a freshly-computed graph into an already-loaded document without a full page reload,
    /// so the running simulation isn't reset by a server-side edit (e.g. toggling a manual link) — the host
    /// calls this via <c>NativeWebView.InvokeScript</c>. The camera itself is deliberately re-fit to the new
    /// data afterwards (see <c>__enkiSetGraphData</c>): a link can turn loose notes into a bigger cluster,
    /// and the view should pull back to keep it in frame rather than staying cropped at the old extent.</summary>
    public static string BuildSetGraphDataScript(SessionGraph graph)
    {
        var json = JsonSerializer.Serialize(BuildPayload(graph), DataJsonOptions);
        // The payload becomes a JS string literal argument, not raw script — passed through
        // JsonSerializer once more so any quote/backslash in a note title can't break out of it.
        var jsonLiteral = JsonSerializer.Serialize(json);
        return $"window.__enkiSetGraphData && window.__enkiSetGraphData({jsonLiteral});";
    }

    /// <summary>Pulses the given note as the pending first pick of a manual link, or clears the pulse when
    /// <paramref name="globalId"/> is null. Purely cosmetic — see the doc comment on <c>pendingLinkNode</c>
    /// in the rendered document for why the actual link mechanics never depend on this call landing.</summary>
    public static string BuildSetPendingLinkScript(string? globalId)
    {
        var literal = globalId is null ? "null" : JsonSerializer.Serialize(globalId);
        return $"window.__enkiSetPendingLink && window.__enkiSetPendingLink({literal});";
    }

    /// <summary>Tells the already-loaded document a manual link attempt was refused host-side (e.g. the two
    /// notes live in different sessions — <c>SessionsViewModel.HandleLinkToggled</c> never crosses that
    /// boundary) so the click that triggered it still gets a visible reaction instead of silently doing
    /// nothing, which is what made linking read as broken in the first place.</summary>
    public static string BuildLinkRejectedScript(string fromGlobalId, string toGlobalId)
    {
        var fromLiteral = JsonSerializer.Serialize(fromGlobalId);
        var toLiteral = JsonSerializer.Serialize(toGlobalId);
        return $"window.__enkiLinkRejected && window.__enkiLinkRejected({fromLiteral}, {toLiteral});";
    }

    private static object BuildPayload(SessionGraph graph) => new
    {
        nodes = graph.Nodes.Select(n => new
        {
            id = n.GlobalId, name = n.Title, session = n.SessionName,
            color = NodeBrightness.ColorFor(n.SessionId), x = n.X, y = n.Y
        }),
        links = graph.Edges.Select(e => new { source = e.FromGlobalId, target = e.ToGlobalId, manual = e.IsManual })
    };

    private sealed class GraphTheme
    {
        public required string Background;
        public required string NodeActive;
        public required string Link;
        public required string LinkManual;
        public required string Particle;
        public required string Danger;
        public required string Ink;
        public required string Ink70;
        public required string Ink80;
        public required string TooltipBackground;
        public required string TooltipForeground;
        public required string TooltipBorder;

        public static GraphTheme FromApp() => new()
        {
            Background = BuildPremiumBackground(
                FindColor("AppBackgroundAltBrush", "#0D0D0D"),
                FindColor("InkBrush", "#FFFFFF", overrideAlpha: 0.05)),
            NodeActive = FindColor("InkBrush", "#FFFFFF"),
            // Bumped up from a barely-there 0.12 for the same reason LinkManual was bumped below: an
            // auto-derived [[wikilink]] edge is real, deliberate data (the user typed the link), and at
            // 0.12 alpha / 0.6px width it was close enough to invisible against the dark background that a
            // freshly-linked pair of notes looked exactly like two unrelated ones — "linking doesn't do
            // anything" even when the edge existed and was correctly rendered underneath the noise floor.
            Link = FindColor("InkBrush", "#FFFFFF", overrideAlpha: 0.3),
            // Bumped up from a barely-there 0.4 — a manually-drawn link is the one thing in this graph a
            // user deliberately created, and it needs to read as obviously present, not as a rumor of a line.
            LinkManual = FindColor("InkBrush", "#FFFFFF", overrideAlpha: 0.9),
            Particle = FindColor("InkBrush", "#FFFFFF", overrideAlpha: 0.5),
            Danger = FindColor("DangerBrush", "#F87171"),
            Ink = FindColor("InkBrush", "#FFFFFF"),
            Ink70 = FindColor("InkBrush", "#FFFFFF", overrideAlpha: 0.7),
            Ink80 = FindColor("InkBrush", "#FFFFFF", overrideAlpha: 0.8),
            TooltipBackground = FindColor("PanelBackgroundBrush", "#1B1B1B"),
            TooltipForeground = FindColor("TextPrimaryBrush", "#F5F5F5"),
            TooltipBorder = FindColor("Ink20", "rgba(255,255,255,0.2)"),
        };

        /// <summary>Layers a faint dot-grid texture and a soft top-of-frame bloom over the app's own
        /// background color — this is what actually reads as "premium" instead of a flat fill, and because
        /// it's set as the document's <c>html/body</c> background (see the surrounding <c>&lt;style&gt;</c>
        /// block), it covers the graph panel's full rectangle with no seam, whether or not the canvases have
        /// finished sizing themselves yet. Uses the tema activo's <c>InkBrush</c> para que la textura respete
        /// tanto temas claros como oscuros.</summary>
        private static string BuildPremiumBackground(string baseColor, string inkRgba) =>
            $"radial-gradient(circle at 1px 1px, {inkRgba} 1px, transparent 1.6px) 0 0 / 26px 26px, "
            + $"radial-gradient(ellipse 70% 55% at 50% 28%, {inkRgba}, rgba(0,0,0,0) 72%), "
            + baseColor;

        private static string FindColor(string key, string fallback, double? overrideAlpha = null)
        {
            if (Application.Current is { } app && app.TryFindResource(key, out var value) && value is Avalonia.Media.ISolidColorBrush solid)
            {
                var c = solid.Color;
                var alpha = overrideAlpha ?? (c.A / 255.0) * solid.Opacity;
                return $"rgba({c.R},{c.G},{c.B},{alpha.ToString(CultureInfo.InvariantCulture)})";
            }
            return fallback;
        }
    }

    /// <summary>Reads a vendored JS bundle out of app resources once per file and keeps it in memory, so it
    /// can be inlined straight into a <c>&lt;script&gt;</c> tag on every render instead of relying on a
    /// <c>file://</c> reference (see the file-scoped doc comment for why that reference approach silently
    /// fails under this app's WebView engines). Shared by both vendored bundles (sigma, d3-force-standalone)
    /// since they're read and cached the exact same way.</summary>
    private static class VendoredScripts
    {
        private static readonly Dictionary<string, string> Cache = new();

        public static string Get(string fileName)
        {
            if (Cache.TryGetValue(fileName, out var cached)) return cached;

            using var source = AssetLoader.Open(new Uri($"avares://enki/assets/js/{fileName}"));
            using var reader = new StreamReader(source);
            // Defensive against a future bundle update introducing a literal "</script" in a string/comment,
            // which would otherwise close the tag early and truncate the inlined script.
            var script = reader.ReadToEnd().Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
            Cache[fileName] = script;
            return script;
        }
    }

    /// <summary>Deterministic per-session node brightness. Nodes used to carry a rainbow per-session hue
    /// so the combined graph (every session's notes in one scene, see <c>SessionService.LoadCombinedGraph</c>)
    /// stayed visually grouped — but a premium, three.js-style graph reads as one coherent material, not a
    /// pile of hues, so every node is pure white now and sessions are instead told apart by a stable, subtle
    /// opacity variance. Alpha is baked into the rgba() string since sigma's default node renderer has no
    /// separate node-opacity setter.</summary>
    private static class NodeBrightness
    {
        public static string ColorFor(string sessionId)
        {
            var alpha = 0.6 + (StableHash(sessionId) & 0x7FFFFFFF) % 41 / 100.0; // 0.60–1.00
            return $"rgba(255,255,255,{alpha.ToString(CultureInfo.InvariantCulture)})";
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var b in System.Text.Encoding.UTF8.GetBytes(value))
                {
                    hash ^= b;
                    hash *= 16777619;
                }
                return (int)hash;
            }
        }
    }
}
