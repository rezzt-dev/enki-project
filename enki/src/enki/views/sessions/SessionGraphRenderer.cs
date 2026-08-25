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
        // Embed the app's own display font as a base64 data URI so the WebView's canvas uses the same
        // typeface the Avalonia UI does — without this @font-face declaration, the WebView falls back to
        // whatever system font WPE/GTK provides, which has poor legibility for certain glyphs (e.g. the
        // uppercase "I" and other thin-stemmed letters like A/L/T/H/N that the user reported as broken).
        // Only Space Grotesk is embedded: it's the title font (69 KB — well under any data-URI limit an
        // embedded WebKit build might impose), and the JetBrains Mono "Nerd Font" bundle the app carries
        // is 2.5 MB, whose base64 form (~3.4 MB) would bloat every render and risks silently dropping the
        // whole @font-face rule on constrained WebViews. The mono eyebrow/counter glyphs are basic Latin
        // (SESIÓN / 34 NOTAS), which the system monospace fallback renders perfectly legibly. The URI is
        // quoted and declared as font/ttf — the MIME WebKitGTK accepts most reliably.
        var spaceGroteskUri = EmbeddedFonts.DataUri("SpaceGrotesk-Bold.ttf");

        return $$"""
                <!doctype html>
                <html>
                <head>
                <meta charset="utf-8" />
                <style>
                  @font-face { font-family: 'Space Grotesk'; src: url("{{spaceGroteskUri}}") format('truetype'); font-weight: 700; font-style: normal; }
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
                  // Kick the embedded Space Grotesk @font-face into loading before the first frame is
                  // drawn. WebKit canvas text only switches to a webfont once it has actually been fetched
                  // and registered (via document.fonts), so without this the first however-many frames —
                  // and on slow embedded WebViews potentially the whole session — would paint the card
                  // titles with the system fallback that renders thin-stemmed glyphs (I, A, L, T, H, N)
                  // poorly. The render loop redraws every frame anyway, so even a late load self-heals;
                  // this just makes it immediate. document.fonts is missing only on ancient engines, in
                  // which case the @font-face still applies once the stylesheet is processed.
                  if (document.fonts && document.fonts.load) {
                    document.fonts.load("700 16px 'Space Grotesk'");
                  }
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

                  // Session-name cards: every session graph (LABEL_MIN_NOTES+ notes, linked or not) gets a
                  // proper card above its cluster, styled after the app's own UI language
                  // (docs/agent-docs/sistema-diseno-web.md): elevated panel surface, a 3px white accent bar
                  // (the app's active/selected signal — sidebar, stat cards, admin rows), a 1px white/20
                  // border, and the design system's signature typographic hierarchy — a tiny mono tracked
                  // "SESIÓN" eyebrow above the big bold uppercase name, a 1px white/10 hairline rule, and a
                  // mono "N NOTAS" counter below (see drawSessionLabels). Font size (and every other
                  // overlay measurement below) is expressed in world units and multiplied by SCREEN_SCALE()
                  // at draw time — deliberately, so it grows and shrinks with the canvas zoom exactly like
                  // the nodes do — but floored at LABEL_MIN_PX so the name stays legible when the camera
                  // zooms out to frame the whole session ring.
                  const LABEL_MIN_NOTES = 2;
                  const LABEL_BASE_SIZE = 18;
                  const LABEL_MAX_SIZE = 30;
                  // Floor on the name's *screen* size (px) — the ring layout parks sessions far apart, so
                  // fitting the whole scene zooms out well past where world-scaled text would stay readable.
                  const LABEL_MIN_PX = 16;
                  // Card colors come from the app's own theme (THEME.labelBackground/Muted/Rule, plus
                  // THEME.tooltipForeground for the title and THEME.ink for the accent bar), so the cards
                  // adapt to the active theme instead of hard-coding dark-theme values.
                  const LABEL_PAD_X = 0.62; // card horizontal padding, × title size
                  const LABEL_PAD_Y = 0.34; // card vertical padding, × title size
                  const LABEL_GAP = 1.1;    // empty space between the cluster's topmost note and the card, × title size
                  const LABEL_ACCENT_W = 3; // left accent bar width, screen px (like the app's selection bars)

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
                  // Whether the next simulation 'end' should re-fit the camera to the whole scene (see the
                  // simulation's 'end' handler) — true at load and after every graph-data update, cleared
                  // after the first settle so a drag-release 'end' doesn't yank the view mid-interaction.
                  let refitOnSettle = true;

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
                  // draws *underneath* those (the session-affinity mesh, item 9 below) or *on top* of them
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

                  // ===================== Custom node/edge renderers (glow + sphere shading) =====================
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
                  // Nodes are drawn as *spheres*, not flat discs: on top of the bloom each sprite bakes a
                  // radial-gradient shading pass whose focal point sits offset toward the upper-left (the
                  // scene's "light source"), so every note reads as a lit, glossy sphere — bright specular
                  // spot, its own body color/alpha, then a falloff into a darker rim on the far side. It's
                  // purely Canvas 2D (a radial gradient), deliberately not WebGL/three.js: this app's Linux
                  // WebView backend (WPE) only implements software (Shm) compositing, and GPU rendering
                  // modes throw NotImplementedException — the same reason the WebGL-based 3d-force-graph
                  // was replaced in the first place (see the file-scoped doc comment).
                  //
                  // Glow is node-only, deliberately: edges stay crisp, unblurred lines (see the edge
                  // renderer below) so the graph doesn't read as a hazy blob once dozens of links overlap —
                  // the bloom is reserved for drawing the eye to notes (spheres), not the connections
                  // between them.
                  const GLOW_COLOR = 'rgba(255,255,255,0.4)';
                  const GLOW_BLUR = 12;
                  // Sphere shading constants. Highlight offset/radius are fractions of the node radius so
                  // the shading scales exactly with zoom, same as the node itself.
                  const SPHERE_HIGHLIGHT_OFFSET = 0.38; // focal point shift toward upper-left, × radius
                  const SPHERE_HIGHLIGHT_RADIUS = 0.15; // size of the specular spot, × radius
                  const SPHERE_HIGHLIGHT_BOOST = 0.3;   // alpha added to the specular spot over the body
                  const SPHERE_BODY_STOP = 0.35;        // gradient position where the node's own color takes over
                  const SPHERE_RIM_RGB = '110,110,125'; // darker neutral-gray rim (the unlit side)
                  const SPHERE_RIM_ALPHA = 0.85;        // rim alpha, × the node's own alpha

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
                  // Every node's sprite (glow *and* sphere shading) is *identical* except for its screen
                  // radius (which only changes on zoom/pending-state) and fill alpha (one of a small, fixed
                  // set of per-session values from NodeBrightness) — GLOW_COLOR/GLOW_BLUR and the sphere
                  // shading constants themselves never vary. That means the actual *blurred* pixels only
                  // ever take a handful of distinct shapes at once, so the expensive part — running
                  // shadowBlur once to produce that shape — can be done a single time per shape and reused:
                  // rendered once into a small offscreen canvas (a "sprite"), cached by (rounded radius,
                  // fill color), and every subsequent frame just blits the already-blurred bitmap via
                  // drawImage — one of the cheapest calls a Canvas2D context has, since there's no
                  // convolution left to run, just a pixel copy. Visually identical output, far less work.
                  const sphereSprites = new Map();
                  // Screen radius changes continuously while the camera is zooming (mouse wheel, pinch, the
                  // zoomToFit tween), so new (radius, color) keys keep appearing and old ones stop being
                  // reused — capped here so a long session of zooming in and out doesn't grow this into an
                  // unbounded pile of small canvases. Clearing the whole cache is fine, not just cheap: the
                  // handful of sizes/colors actually on screen at any moment get re-cached again within a
                  // frame or two, same as a cold start.
                  const SPHERE_SPRITE_CACHE_LIMIT = 300;

                  // Pulls the alpha out of the rgba() strings this page builds (NodeBrightness's
                  // per-session whites, THEME.nodeActive's pure white) so the sphere shading can derive its
                  // highlight/rim alphas from the node's own.
                  function rgbaAlpha(cssColor) {
                    const match = /rgba?\(([^)]+)\)/.exec(cssColor);
                    if (!match) return 1;
                    const parts = match[1].split(',');
                    const alpha = parseFloat(parts[parts.length - 1].trim());
                    return isNaN(alpha) ? 1 : alpha;
                  }

                  function getSphereSprite(radius, fillStyle) {
                    const r = Math.max(1, Math.round(radius));
                    const key = r + '|' + fillStyle;
                    const cached = sphereSprites.get(key);
                    if (cached) return cached;

                    if (sphereSprites.size >= SPHERE_SPRITE_CACHE_LIMIT) sphereSprites.clear();

                    // Padding gives the blur room to fall off before it hits the sprite canvas's own edge —
                    // too little and the glow reads as a hard-clipped square halo instead of a soft falloff.
                    const padding = GLOW_BLUR * 3;
                    const size = r * 2 + padding * 2;
                    const spriteCanvas = document.createElement('canvas');
                    spriteCanvas.width = size;
                    spriteCanvas.height = size;
                    const spriteCtx = spriteCanvas.getContext('2d');
                    const cx = size / 2, cy = size / 2;

                    // First the bloom: one blurred fill in the node's own color, exactly as before.
                    spriteCtx.shadowColor = GLOW_COLOR;
                    spriteCtx.shadowBlur = GLOW_BLUR;
                    spriteCtx.fillStyle = fillStyle;
                    spriteCtx.beginPath();
                    spriteCtx.arc(cx, cy, r, 0, Math.PI * 2, true);
                    spriteCtx.closePath();
                    spriteCtx.fill();
                    spriteCtx.shadowBlur = 0;

                    // Then the sphere itself on top: a radial gradient from the offset specular spot
                    // (upper-left "light"), through the node's own color, out to the darkened rim. The
                    // sprite's outer glow is untouched because the sphere only covers the circle itself.
                    const alpha = rgbaAlpha(fillStyle);
                    const grad = spriteCtx.createRadialGradient(
                      cx - r * SPHERE_HIGHLIGHT_OFFSET, cy - r * SPHERE_HIGHLIGHT_OFFSET, r * SPHERE_HIGHLIGHT_RADIUS,
                      cx, cy, r);
                    grad.addColorStop(0, 'rgba(255,255,255,' + Math.min(1, alpha + SPHERE_HIGHLIGHT_BOOST).toFixed(3) + ')');
                    grad.addColorStop(SPHERE_BODY_STOP, fillStyle);
                    grad.addColorStop(1, 'rgba(' + SPHERE_RIM_RGB + ',' + (alpha * SPHERE_RIM_ALPHA).toFixed(3) + ')');
                    spriteCtx.fillStyle = grad;
                    spriteCtx.beginPath();
                    spriteCtx.arc(cx, cy, r, 0, Math.PI * 2, true);
                    spriteCtx.closePath();
                    spriteCtx.fill();

                    const sprite = { canvas: spriteCanvas, half: size / 2 };
                    sphereSprites.set(key, sprite);
                    return sprite;
                  }

                  sigma.canvas.nodes.enkiSphere = function (node, context, settings) {
                    const prefix = settings('prefix') || '';
                    const sprite = getSphereSprite(node[prefix + 'size'], node.color || settings('defaultNodeColor'));
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
                      defaultNodeType: 'enkiSphere',
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
                    // Once the layout settles (alpha decayed to rest), re-wire the session mesh and anchor
                    // ring from the final positions — the first build ran on whatever positions the very
                    // first tick saw, which for never-laid-out notes can be a degenerate pile at the origin.
                    // invalidateGroupings is cheap (a few nulls); the next frame's ensureGroupings rebuilds
                    // from settled positions. The camera is re-fit to the settled scene too, but only the
                    // first time (refitOnSettle, set again by __enkiSetGraphData) — a drag release also
                    // fires 'end', and yanking the view to re-fit the whole scene every time would fight the
                    // user mid-interaction.
                    .on('end', function () {
                      invalidateGroupings();
                      if (refitOnSettle) {
                        refitOnSettle = false;
                        setTimeout(function () { zoomToFit(600, 90); }, 250);
                      }
                      reportPositions();
                    });

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
                  // Slightly generous vs. the platform default so a human double click still lands as one
                  // even under this app's software-rendered WebView backend, where event delivery can lag.
                  const DOUBLE_CLICK_MS = 450;

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
                  // mesh lines, labels) in sync with wherever sigma is currently drawing the real nodes/edges.
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
                  // The groupings of "notes in the same session" are built together in one pass since they
                  // share the same source loop, and cached — recomputing any of them from scratch every
                  // frame (a fresh Set + Map churn, 60 times a second forever thanks to the animation loop
                  // never stopping) was pure waste. Rebuilt on demand only when the graph's node/link *set*
                  // changes (see invalidateGroupings, called from __enkiSetGraphData below). Member
                  // *positions* still move every frame (force layout, rotation), so those are re-aggregated
                  // on every draw call — only membership itself is cached:
                  //  - allSessionGroups: every note in a session with LABEL_MIN_NOTES+ notes, linked or not
                  //    — this is the weaker "these just happen to live in the same session" affinity, used
                  //    for the faint mesh scaffold in drawSessionAffinity, the session-name chips in
                  //    drawSessionLabels and the gentle clustering force below. Keeping it separate from
                  //    real links means it never touches the actual link data the "X ENLACES" chip and
                  //    manual-link toggling depend on — it's purely a rendering/layout aid.
                  //  - allSessionMembers: like allSessionGroups but including single-note sessions, so the
                  //    ring snap below also parks isolated notes on their own slot instead of letting them
                  //    drift into a neighboring cluster.
                  //  - sessionAnchors: a fixed ring of slots (one per session, evenly spaced around the
                  //    origin — see SESSION_CENTER_SPACING) that snapSessionsToRing eases each session's
                  //    center of mass toward in the render loop (see snapSessionsToRing for why it lives
                  //    there rather than in the d3 simulation). That pull is the mechanism that guarantees
                  //    the different session graphs stay well apart instead of stacking on top of one
                  //    another. The slots are derived from the session *count* alone, never from live
                  //    positions, so they stay put while the layout settles instead of chasing the clusters
                  //    around.
                  //  - sessionMeshPairs: the edges of the mesh scaffold, wired as a planar mesh — each
                  //    session's notes are Delaunay-triangulated (see triangulateSession), so the "these
                  //    live together" cue reads as a clean wireframe web of triangles instead of a star of
                  //    spokes to one central point. Delaunay is what keeps it clean: the result is planar,
                  //    so no two scaffold lines ever cross, which a naive nearest-neighbor web does
                  //    constantly and turns a dense session into a tangled hairball. Topology is cached,
                  //    not recomputed every frame, so the lines don't flicker while the force layout is
                  //    settling; the ambient rotation is rigid (it preserves every distance), so the cached
                  //    wiring stays visually valid long after it was computed — and the simulation's 'end'
                  //    event invalidates it once the layout settles, so the mesh is re-wired from the final
                  //    positions instead of being frozen at whatever the first tick saw.
                  let allSessionGroups = null;
                  let allSessionMembers = null;
                  let sessionAnchors = null;
                  let sessionMeshPairs = null;
                  function invalidateGroupings() {
                    allSessionGroups = null; allSessionMembers = null; sessionAnchors = null; sessionMeshPairs = null;
                  }
                  function ensureGroupings() {
                    if (allSessionGroups && allSessionMembers && sessionAnchors && sessionMeshPairs) return;

                    const bySession = new Map();
                    nodes.forEach(function (n) {
                      if (!bySession.has(n.session)) bySession.set(n.session, []);
                      bySession.get(n.session).push(n);
                    });

                    allSessionGroups = new Map();
                    allSessionMembers = new Map();
                    bySession.forEach(function (members, session) {
                      allSessionMembers.set(session, members);
                      if (members.length >= LABEL_MIN_NOTES) allSessionGroups.set(session, members);
                    });

                    // Fixed ring of session slots — see the sessionAnchors comment above. The ring's center
                    // is the *weighted* center of mass of the parked clusters, not the raw origin: d3's
                    // forceCenter pulls the scene's live weighted COM toward (0,0), and since each session's
                    // cluster ends up parked at its slot, that COM is Σ(wᵢ·slotᵢ)/Σwᵢ — which is not zero
                    // when sessions have different note counts (64 notes on one side of the ring outweigh
                    // 3 on the other). Centering the ring there makes the forceCenter correction vanish at
                    // equilibrium, so the ring snap and the physics pull in the same direction instead of
                    // fighting forever (verified empirically: without it, forceCenter shifted the whole
                    // ring off its slots by exactly the weighted-COM offset).
                    sessionAnchors = new Map();
                    const sessions = Array.from(allSessionMembers.keys());
                    const count = sessions.length;
                    const ringRadius = count <= 1 ? 0 : (count * SESSION_CENTER_SPACING) / (2 * Math.PI);
                    const ringCenter = { x: 0, y: 0 };
                    {
                      let wx = 0, wy = 0, w = 0;
                      sessions.forEach(function (session, i) {
                        const members = allSessionMembers.get(session);
                        const angle = (i / count) * Math.PI * 2 - Math.PI / 2;
                        const sx = Math.cos(angle) * ringRadius, sy = Math.sin(angle) * ringRadius;
                        wx += members.length * sx; wy += members.length * sy; w += members.length;
                      });
                      ringCenter.x = -wx / w; ringCenter.y = -wy / w;
                    }
                    sessions.forEach(function (session, i) {
                      const angle = (i / count) * Math.PI * 2 - Math.PI / 2; // first slot at the top, then clockwise
                      // The session's color — all nodes in a session share the same one (derived from
                      // the session id by NodeBrightness, or an explicit hex override), so reading it from
                      // the first node is cheap and deterministic.
                      const members = allSessionMembers.get(session);
                      const sessionColor = members && members.length > 0 ? members[0].color : THEME.ink;
                      sessionAnchors.set(session, {
                        x: Math.cos(angle) * ringRadius + ringCenter.x,
                        y: Math.sin(angle) * ringRadius + ringCenter.y,
                        color: sessionColor
                      });
                    });

                    // Mesh wiring, computed from positions as they are right now (see the sessionMeshPairs
                    // comment above for why it's cached rather than re-derived per frame).
                    sessionMeshPairs = [];
                    allSessionGroups.forEach(function (members) {
                      triangulateSession(members).forEach(function (pair) { sessionMeshPairs.push(pair); });
                    });
                  }

                  // Bowyer–Watson incremental Delaunay triangulation over a session's notes, returning the
                  // undirected edge list (pairs of node objects). Delaunay is the canonical "mesh" wiring:
                  // it connects every note to its natural spatial neighbors so the whole group forms one
                  // planar web of triangles — and because the result is planar, no two edges ever cross,
                  // which is the property that keeps a 64-note session looking like a wireframe mesh
                  // instead of a hairball. Degenerate cases are handled defensively: fewer than 3 notes get
                  // a trivial result (a single edge for 2, nothing for 1), and coincident points (e.g.
                  // brand-new notes that were never laid out, all sitting at the origin) collapse to one
                  // participant each so the circumcircle math never divides by ~zero.
                  function triangulateSession(members) {
                    if (members.length < 2) return [];
                    if (members.length === 2) return [[members[0], members[1]]];

                    // Collapse coincident points (within 0.01 world units); excluded nodes ride along with
                    // their twin — they occupy the same spot, so the twin's edges already emanate from it.
                    const pts = [];
                    const used = new Set();
                    members.forEach(function (n) {
                      const key = n.x.toFixed(2) + ',' + n.y.toFixed(2);
                      if (used.has(key)) return;
                      used.add(key);
                      pts.push(n);
                    });
                    if (pts.length < 2) return [];
                    if (pts.length === 2) return [[pts[0], pts[1]]];

                    // Super-triangle enclosing every point, so the incremental insertion always has a
                    // starting triangulation to chew on; its vertices are removed from the result at the end.
                    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
                    pts.forEach(function (p) {
                      if (p.x < minX) minX = p.x;
                      if (p.x > maxX) maxX = p.x;
                      if (p.y < minY) minY = p.y;
                      if (p.y > maxY) maxY = p.y;
                    });
                    const dmax = Math.max(maxX - minX, maxY - minY);
                    const midx = (minX + maxX) / 2, midy = (minY + maxY) / 2;
                    const sup = [
                      { x: midx - 20 * dmax, y: midy - dmax },
                      { x: midx, y: midy + 20 * dmax },
                      { x: midx + 20 * dmax, y: midy - dmax }
                    ];
                    let tris = [[sup[0], sup[1], sup[2]]];

                    function circumcircle(t) {
                      const a = t[0], b = t[1], c = t[2];
                      const d = 2 * (a.x * (b.y - c.y) + b.x * (c.y - a.y) + c.x * (a.y - b.y));
                      if (Math.abs(d) < 1e-12) return null; // degenerate/collinear — leave the triangle alone
                      const a2 = a.x * a.x + a.y * a.y, b2 = b.x * b.x + b.y * b.y, c2 = c.x * c.x + c.y * c.y;
                      const ux = (a2 * (b.y - c.y) + b2 * (c.y - a.y) + c2 * (a.y - b.y)) / d;
                      const uy = (a2 * (c.x - b.x) + b2 * (a.x - c.x) + c2 * (b.x - a.x)) / d;
                      return { x: ux, y: uy, r: Math.hypot(ux - a.x, uy - a.y) };
                    }

                    pts.forEach(function (p) {
                      const bad = [];
                      for (let i = tris.length - 1; i >= 0; i--) {
                        const c = circumcircle(tris[i]);
                        if (c && Math.hypot(p.x - c.x, p.y - c.y) <= c.r) bad.push(tris.splice(i, 1)[0]);
                      }
                      // The boundary of the removed region — edges shared by exactly one removed triangle —
                      // becomes the new triangles' outer edges.
                      const boundary = new Map();
                      function addBoundary(u, v) {
                        const key = u.x < v.x || (u.x === v.x && u.y < v.y)
                          ? u.x + ',' + u.y + '|' + v.x + ',' + v.y
                          : v.x + ',' + v.y + '|' + u.x + ',' + u.y;
                        if (boundary.has(key)) boundary.delete(key); else boundary.set(key, [u, v]);
                      }
                      bad.forEach(function (t) { addBoundary(t[0], t[1]); addBoundary(t[1], t[2]); addBoundary(t[2], t[0]); });
                      boundary.forEach(function (e) { tris.push([e[0], e[1], p]); });
                    });

                    const isSuper = function (p) { return p === sup[0] || p === sup[1] || p === sup[2]; };
                    const seen = new Set();
                    const edges = [];
                    tris.forEach(function (t) {
                      if (t.some(isSuper)) return;
                      [[t[0], t[1]], [t[1], t[2]], [t[2], t[0]]].forEach(function (pair) {
                        const u = pair[0], v = pair[1];
                        const key = u.x < v.x || (u.x === v.x && u.y < v.y)
                          ? u.x + ',' + u.y + '|' + v.x + ',' + v.y
                          : v.x + ',' + v.y + '|' + u.x + ',' + u.y;
                        if (!seen.has(key)) { seen.add(key); edges.push(pair); }
                      });
                    });
                    return edges;
                  }

                  // The session-name card: every session graph (LABEL_MIN_NOTES+ notes, linked or not) gets
                  // a card floating above the cluster's topmost note, built from the app's own design tokens
                  // (see the LABEL_* constants and docs/agent-docs/sistema-diseno-web.md §2.2/§3.3/§7.6): an
                  // elevated panel surface, a 3px white left accent bar, a 1px white/20 border, sharp corners,
                  // and the design system's signature typographic hierarchy: a tiny mono "SESIÓN" eyebrow,
                  // the big bold uppercase name, a 1px white/10 hairline, then the mono "N NOTAS" counter.
                  // Centered on the cluster's center of mass, so it drifts along with the force simulation.
                  // Drawn on the overlay canvas, on top of sigma's own nodes/edges.
                  //
                  // Title text is drawn word-by-word with an explicit gap between words. The WPE
                  // software renderer mis-handles the Space Grotesk font's kerning tables at large sizes,
                  // causing a single fillText("SIN SESIÓN") to produce overlapping/stacked glyphs (the
                  // user sees "S N SES ÓN" with letters piled on top of each other). Splitting into
                  // words and placing each one with a measured advance avoids the renderer bug entirely
                  // — each word is short enough that its internal kerning fits within the renderer's
                  // precision, and the explicit gap between words prevents the space character from
                  // collapsing to zero width.
                  function drawSessionLabels() {
                    const groups = allSessionGroups;
                    if (!groups || groups.size === 0) return;

                    overlayCtx.save();
                    overlayCtx.textAlign = 'center';
                    overlayCtx.textBaseline = 'top';
                    groups.forEach(function (members, session) {
                      let sx = 0, minY = Infinity;
                      members.forEach(function (n) { sx += n.x; if (n.y < minY) minY = n.y; });
                      const cx = sx / members.length;
                      const worldSize = Math.min(LABEL_MAX_SIZE, LABEL_BASE_SIZE + Math.sqrt(members.length - LABEL_MIN_NOTES) * 2.5);
                      const scale = screenScale();
                      const size = Math.max(worldSize * scale, LABEL_MIN_PX);

                      const top = worldToScreen(cx, minY);
                      const name = session.toUpperCase();
                      const count = members.length + (members.length === 1 ? ' NOTA' : ' NOTAS');

                      // Font sizes and vertical metrics — all in screen px.
                      const eyebrowSize = Math.max(7, size * 0.38);
                      const countSize = Math.max(8, size * 0.42);
                      const padX = size * LABEL_PAD_X, padY = size * LABEL_PAD_Y;
                      const gap = size * 0.22;

                      // Measure full strings in one shot.
                      const eyebrowFont = "700 " + eyebrowSize + "px 'JetBrains Mono', ui-monospace, monospace";
                      overlayCtx.font = eyebrowFont;
                      const ew = overlayCtx.measureText('SESIÓN').width;

                      const titleFont = "700 " + size + "px 'Space Grotesk', 'DejaVu Sans', system-ui, -apple-system, sans-serif";
                      overlayCtx.font = titleFont;
                      // Split name into words for word-by-word drawing — the WPE renderer
                      // mangles a single fillText of the full multi-word string at large sizes.
                      const titleWords = name.split(' ');
                      const titleWordGap = size * 0.18; // explicit space ≈ 18% of font size
                      const titleWordWidths = titleWords.map(function (w) { return overlayCtx.measureText(w).width; });
                      const tw = titleWordWidths.reduce(function (a, b) { return a + b; }, 0) + titleWordGap * (titleWords.length - 1);

                      const countFont = "700 " + countSize + "px 'JetBrains Mono', ui-monospace, monospace";
                      overlayCtx.font = countFont;
                      const cw = overlayCtx.measureText(count).width;

                      const maxW = Math.max(ew, tw, cw);
                      const accentW = LABEL_ACCENT_W;
                      const bw = maxW + padX * 2 + accentW;
                      const bh = padY * 2 + eyebrowSize + gap + size + gap + 1 + gap + countSize;
                      const bx = top.x - bw / 2;
                      const by = top.y - size * LABEL_GAP - bh;

                      // Card body: elevated surface, 3px accent bar in the session's color (or ink white
                      // for sessions that have no nodes loaded yet fallback), 1px white/20 border.
                      const accentColor = (sessionAnchors && sessionAnchors.get(session) && sessionAnchors.get(session).color) || THEME.ink;
                      overlayCtx.fillStyle = THEME.labelBackground;
                      overlayCtx.fillRect(bx, by, bw, bh);
                      overlayCtx.fillStyle = accentColor;
                      overlayCtx.fillRect(bx, by, accentW, bh);
                      overlayCtx.strokeStyle = THEME.tooltipBorder;
                      overlayCtx.lineWidth = 1;
                      overlayCtx.strokeRect(bx + 0.5, by + 0.5, bw - 1, bh - 1);

                      // Text positioned right of the accent bar.
                      const textCx = bx + accentW + (bw - accentW) / 2;
                      const ey = by + padY;
                      const ty = by + padY + eyebrowSize + gap;
                      const ruleY = by + padY + eyebrowSize + gap + size + gap + 0.5;
                      const cy = by + padY + eyebrowSize + gap + size + gap + 1 + gap;

                      overlayCtx.font = eyebrowFont;
                      overlayCtx.fillStyle = THEME.labelMuted;
                      overlayCtx.fillText('SESIÓN', textCx, ey);

                      // Draw each word separately with an explicit gap to avoid the WPE
                      // glyph-overlap bug. Left-align within the card; each word is drawn
                      // on its own to keep internal kerning manageable for the renderer.
                      overlayCtx.font = titleFont;
                      overlayCtx.fillStyle = THEME.tooltipForeground;
                      overlayCtx.textAlign = 'left';
                      let wordCx = textCx - tw / 2;
                      for (var wi = 0; wi < titleWords.length; wi++) {
                        var ww = titleWordWidths[wi];
                        overlayCtx.fillText(titleWords[wi], wordCx, ty);
                        wordCx += ww + titleWordGap;
                      }
                      overlayCtx.textAlign = 'center'; // restore for eyebrow + count

                      // Hairline rule.
                      overlayCtx.fillStyle = THEME.labelRule;
                      overlayCtx.fillRect(textCx - maxW / 2 + padX * 0.5, ruleY, maxW - padX, 1);

                      overlayCtx.font = countFont;
                      overlayCtx.fillStyle = THEME.labelMuted;
                      overlayCtx.fillText(count, textCx, cy);
                    });
                    overlayCtx.restore();
                  }

                  // A "these live in the same session" scaffold — every note in a session with 2+ notes is
                  // wired into the session's Delaunay mesh (see triangulateSession), whether or not it has
                  // any real (wikilink/manual) edge to anything. It used to draw a spoke from every note to
                  // the session's own center of mass — a "star" where the whole session met at one hub —
                  // but an interconnected web reads better than a hub-and-spoke diagram, and answers the
                  // same grouping question ("which session is this note in," not "what does it reference").
                  // Deliberately faint (AFFINITY_ALPHA sits well below the real auto-link alpha of ~0.3, so
                  // the scaffold never outshines actual relationships): pure white, uncolored by session,
                  // vs. THEME.link/THEME.linkManual's tinted rgba. Drawn on the affinity canvas, a separate
                  // layer stacked *behind* sigma's own renderer (see the DOM layers section above) so the
                  // real nodes/links always paint over these mesh lines without needing any pre-render hook
                  // into sigma itself.
                  const AFFINITY_ALPHA = 0.2;

                  function drawSessionAffinity() {
                    if (sessionMeshPairs.length === 0) return;

                    const scale = screenScale();
                    affinityCtx.save();
                    affinityCtx.strokeStyle = THEME.ink80;
                    affinityCtx.globalAlpha = AFFINITY_ALPHA;
                    affinityCtx.lineWidth = 0.45 * scale;
                    affinityCtx.beginPath();
                    sessionMeshPairs.forEach(function (pair) {
                      const a = worldToScreen(pair[0].x, pair[0].y);
                      const b = worldToScreen(pair[1].x, pair[1].y);
                      affinityCtx.moveTo(a.x, a.y);
                      affinityCtx.lineTo(b.x, b.y);
                    });
                    affinityCtx.stroke();
                    affinityCtx.restore();
                  }

                  // Gently pulls notes toward their own session's center of mass every simulation tick, on
                  // top of whatever the real link force and node-repulsion (charge) forces already do —
                  // otherwise two same-session notes with no real link between them have nothing pulling
                  // them toward each other at all, and the affinity mesh above would be drawn crossing the
                  // whole canvas at random rather than reading as a loose, local cluster. Deliberately weak
                  // (a nudge, not a clamp) so real links — and the charge force keeping everything from
                  // collapsing into a single point — still dominate the final layout. Registered directly as
                  // a d3-force force function (not via d3's .initialize() protocol) since it closes over
                  // `nodes` directly, same pattern the force-graph version of this file already used.
                  const SESSION_CLUSTER_STRENGTH = 0.08;
                  // Center-to-center arc spacing between adjacent session slots on the ring — deliberately
                  // very generous (several× the radius a dense session reaches under the charge force, see
                  // sessionClusterForce) so neighboring session graphs keep a wide band of empty space
                  // between them instead of drifting together — the whole point of the ring (see the
                  // sessionAnchors comment above). The ring radius grows with the session count
                  // (radius = count × spacing / 2π), so this stays the gap between neighbors no matter how
                  // many sessions exist.
                  const SESSION_CENTER_SPACING = 800;
                  // How much of the remaining gap between a session's center of mass and its ring slot is
                  // closed per millisecond (see snapSessionsToRing) — ≈0.027/frame at 60fps, so a cluster
                  // far from its slot glides home over a couple of seconds.
                  const SESSION_SNAP_RATE = 0.0016; // per ms

                  function sessionClusterForce(alpha) {
                    ensureGroupings();
                    // Cohesion only, exactly as before: compact each session's notes around their own
                    // center of mass (a nudge, not a clamp, so real links and the charge force still shape
                    // the layout). Where each cluster sits on screen is decided by snapSessionsToRing in
                    // the render loop, deliberately not here — see that function's comment for why.
                    allSessionMembers.forEach(function (members) {
                      let cx = 0, cy = 0;
                      members.forEach(function (n) { cx += n.x; cy += n.y; });
                      cx /= members.length; cy /= members.length;

                      members.forEach(function (n) {
                        n.vx = (n.vx || 0) + (cx - n.x) * SESSION_CLUSTER_STRENGTH * alpha;
                        n.vy = (n.vy || 0) + (cy - n.y) * SESSION_CLUSTER_STRENGTH * alpha;
                      });
                    });
                  }

                  // Parks every session graph on its own ring slot — see the sessionAnchors comment above.
                  // Applied here in the render loop (like the ambient rotation), deliberately NOT as a d3
                  // force: a physics pull has to fight d3's forceCenter and its alpha-decay timeline, and
                  // the simulation freezes with clusters stranded mid-flight (verified empirically with the
                  // exact app configuration — velocity-based pulls overshoot the ring, alpha-scaled ones
                  // fade before arrival, and forceCenter shifts the whole graph so the offset never
                  // corrects). A per-frame position snap has no momentum, so it can't overshoot, and it
                  // runs for as long as frames render, so it always converges. Each frame closes
                  // SESSION_SNAP_RATE × dt of the gap between the session's center of mass and its slot —
                  // the same delta for every member, so the cluster moves as a unit without distorting its
                  // internal layout. Paused while a node is being dragged (same as the rotation) so it
                  // never fights the user's hand.
                  function snapSessionsToRing(dt) {
                    if (dt <= 0) return;
                    ensureGroupings();
                    const k = SESSION_SNAP_RATE * dt;
                    allSessionMembers.forEach(function (members, session) {
                      let cx = 0, cy = 0;
                      members.forEach(function (n) { cx += n.x; cy += n.y; });
                      cx /= members.length; cy /= members.length;
                      const anchor = sessionAnchors.get(session);
                      if (!anchor) return;
                      const tx = (anchor.x - cx) * k;
                      const ty = (anchor.y - cy) * k;
                      members.forEach(function (n) { n.x += tx; n.y += ty; });
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
                    // The new node set may change the session ring and the settled extent — re-fit the camera
                    // at the next simulation 'end' (see the 'end' handler and refitOnSettle).
                    refitOnSettle = true;

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
                    if (!isDraggingNode) snapSessionsToRing(dt);
                    s.refresh();

                    ensureGroupings();
                    // Clear each overlay canvas in its own physical-pixel coordinate space
                    // (reset transform → clear → restore) so the clearRect always covers the
                    // full backing store regardless of any stale DPR or size mismatch. Using
                    // the affinity canvas's dimensions for the overlay canvas (as this code
                    // previously did) silently fails if the canvases ever diverge.
                    overlayCtx.save();
                    overlayCtx.setTransform(1, 0, 0, 1, 0, 0);
                    overlayCtx.clearRect(0, 0, overlayCanvas.width, overlayCanvas.height);
                    overlayCtx.restore();
                    affinityCtx.save();
                    affinityCtx.setTransform(1, 0, 0, 1, 0, 0);
                    affinityCtx.clearRect(0, 0, affinityCanvas.width, affinityCanvas.height);
                    affinityCtx.restore();
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
            color = NodeBrightness.ColorFor(n.SessionId, n.SessionColor), x = n.X, y = n.Y
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
        public required string LabelBackground;
        public required string LabelMuted;
        public required string LabelRule;

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
            // The session-name cards (drawSessionLabels) use the app's own surface/ink tokens so they
            // read as part of the UI rather than browser defaults: CardBackgroundHoverBrush is the
            // elevated panel surface (lighter than PanelBackgroundBrush, which is nearly invisible
            // against the graph's own background), Ink40 for the dim mono micro-labels and Ink10 for
            // the hairline rule separating title from count.
            LabelBackground = FindColor("CardBackgroundHoverBrush", "#1F1F1F"),
            LabelMuted = FindColor("Ink40", "rgba(255,255,255,0.4)"),
            LabelRule = FindColor("Ink10", "rgba(255,255,255,0.1)"),
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

    /// <summary>Reads a font file from app resources and returns a base64 data URI, cached across
    /// renders, so the same font the Avalonia UI uses is available to the WebView's canvas renderer via
    /// an <c>@font-face</c> rule. Without this, the WebView falls back to whatever system font WPE/GTK
    /// provides, which has poor legibility for certain glyphs (e.g. uppercase "I") — the whole point of
    /// bundling Space Grotesk and JetBrains Mono in the first place.</summary>
    private static class EmbeddedFonts
    {
        private static readonly Dictionary<string, string> Cache = new();

        public static string DataUri(string fileName)
        {
            if (Cache.TryGetValue(fileName, out var cached)) return cached;

            using var source = AssetLoader.Open(new Uri($"avares://enki/assets/fonts/{fileName}"));
            using var ms = new MemoryStream();
            source.CopyTo(ms);
            var uri = "data:font/truetype;base64," + Convert.ToBase64String(ms.ToArray());
            Cache[fileName] = uri;
            return uri;
        }
    }

    /// <summary>Deterministic per-group node color. Every session gets a stable, distinguishable hue
    /// derived from a hash of its id — visually distinct enough to tell apart at a glance in the
    /// global graph, while keeping the same "one material" feel (saturation and lightness are fixed,
    /// only hue varies). When a session has an explicit <see cref="SessionManifest.Color"/> value, that
    /// overrides the auto-derived hue. Color is baked into the rgba() string since sigma's default node
    /// renderer has no separate node-color setter.</summary>
    public static class NodeBrightness
    {
        private const double Saturation = 0.55;
        private const double Lightness = 0.70;

        /// <summary>Returns an rgba() string (opaque) for the given session id. If
        /// <paramref name="explicitColor"/> is non-null and a valid hex string, it's used directly;
        /// otherwise a stable hue is derived from <paramref name="sessionId"/>.</summary>
        public static string ColorFor(string sessionId, string? explicitColor = null)
        {
            if (explicitColor is { Length: > 0 } hex && TryParseHex(hex, out var r, out var g, out var b))
                return $"rgba({r},{g},{b},1.00)";

            var hue = (StableHash(sessionId) & 0x7FFFFFFF) % 360;
            return HslToRgba(hue, Saturation, Lightness);
        }

        private static bool TryParseHex(string hex, out int r, out int g, out int b)
        {
            r = g = b = 0;
            var h = hex.TrimStart('#');
            if (h.Length != 6) return false;
            try
            {
                r = Convert.ToInt32(h[..2], 16);
                g = Convert.ToInt32(h[2..4], 16);
                b = Convert.ToInt32(h[4..6], 16);
                return true;
            }
            catch { return false; }
        }

        private static string HslToRgba(double h, double s, double l)
        {
            h /= 360.0;
            var q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
            var p = 2.0 * l - q;
            var r = HueToRgb(p, q, h + 1.0 / 3.0);
            var g = HueToRgb(p, q, h);
            var b = HueToRgb(p, q, h - 1.0 / 3.0);
            return $"rgba({r},{g},{b},1.00)";
        }

        private static int HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            double val;
            if (t < 1.0 / 6.0) val = p + (q - p) * 6.0 * t;
            else if (t < 0.5) val = q;
            else if (t < 2.0 / 3.0) val = p + (q - p) * (2.0 / 3.0 - t) * 6.0;
            else val = p;
            return (int)(val * 255);
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
