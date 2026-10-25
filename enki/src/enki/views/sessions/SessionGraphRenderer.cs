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
using enki.Services;

namespace enki.Views.Sessions;

/// <summary>
/// Builds the self-contained HTML document that hosts the session graph: one shared 3D scene in which every
/// session is a volumetric sphere of notes that rotates on its own axis.
///
/// <para>The scene is rendered by a small dedicated 3D engine on Canvas 2D (real X/Y/Z coordinates, per-sphere
/// rotation matrices, perspective projection, depth-sorted painting and shaded sphere impostors), not by
/// WebGL/Three.js. That is a hard platform constraint, re-verified for this rewrite against the
/// <c>Avalonia.Controls.WebView</c> 12.1.0 assembly the app ships: on Linux the WPE backend only implements
/// software (Shm) compositing ("EGL rendering mode is not implemented"), so a WebGL context cannot present
/// anything there — which is exactly why the app had already moved off <c>3d-force-graph</c>. One engine that
/// works on WebView2, WKWebView and WPE alike keeps a single code path instead of a WebGL renderer plus a
/// Canvas fallback (see <c>.docs/project-info/grafo-sesiones-3d.md</c>).</para>
///
/// <para>The layout is not computed here: <see cref="SessionSphereLayout"/> builds it once in C# and both this
/// document and <see cref="NativeSessionGraph"/> consume the same <see cref="SessionSphereScene"/>, so the web
/// and native renderers always show identical geometry. Rotation only changes each sphere's transform; it
/// never re-runs the layout nor touches the data.</para>
///
/// <para>Everything is inlined (no <c>file://</c> or network resources): the document is loaded with
/// <c>NavigateToString</c> and no base URL, and every native WebView engine refuses local resources there.</para>
/// </summary>
public static class SessionGraphRenderer
{
    // IncludeFields: GraphTheme is a plain-field class, and System.Text.Json only serializes public properties
    // by default — without it the theme serializes as "{}" and every THEME.xxx read in the JS is undefined.
    // CamelCase: the fields are PascalCase but the JS reads camelCase.
    private static readonly JsonSerializerOptions DataJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true
    };

    /// <param name="rotationExplicit">Whether <paramref name="rotationEnabled"/> is a saved user choice. When it
    /// is not, the document honours the operating system's reduced-motion preference and reports it back.</param>
    internal static string RenderDocument(SessionGraph graph, SessionSphereScene scene, bool subtleAnimations = false,
        bool rotationEnabled = true, bool rotationExplicit = true)
    {
        var theme = GraphTheme.FromApp();
        // A note title, session name or tag containing the literal "</script" would otherwise close this
        // document's <script> tag early (the rest prints as page text and the graph never loads).
        var dataJson = JsonSerializer.Serialize(BuildPayload(graph, scene), DataJsonOptions)
            .Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
        var themeJson = JsonSerializer.Serialize(theme, DataJsonOptions)
            .Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
        var configJson = JsonSerializer.Serialize(new
        {
            rotationSpeed = SessionSphereParameters.RotationSpeed,
            axisTilt = SessionSphereParameters.AxisTilt,
            maxPitch = SessionSphereParameters.MaxPitch,
            perspective = SessionSphereParameters.PerspectiveDepth,
            manualRotation = SessionSphereParameters.ManualRotationPerPixel
        }, DataJsonOptions);
        // The app's own display font as a data URI, so canvas text matches the Avalonia UI instead of the
        // WPE/GTK system fallback (poor legibility for thin-stemmed capitals). Only Space Grotesk (69 KB):
        // the JetBrains Mono Nerd Font bundle is 2.5 MB and would bloat every render.
        var spaceGroteskUri = EmbeddedFonts.DataUri("SpaceGrotesk-Bold.ttf");
        static string Js(bool value) => value ? "true" : "false";

        return $$"""
                <!doctype html>
                <html>
                <head>
                <meta charset="utf-8" />
                <style>
                  @font-face { font-family: 'Space Grotesk'; src: url("{{spaceGroteskUri}}") format('truetype'); font-weight: 700; font-style: normal; }
                  html, body { margin: 0; padding: 0; overflow: hidden; background: {{theme.Background}}; }
                  #graph { position: relative; width: 100vw; height: 100vh; cursor: grab; outline: none; touch-action: none; }
                  #graph.node-hover { cursor: pointer; }
                  #graph.sphere-hover { cursor: move; }
                  #graph.pointer-active { cursor: grabbing; }
                  #graph canvas { position: absolute; inset: 0; width: 100%; height: 100%; }
                  .enki-graph-tooltip {
                    position: fixed; z-index: 20; pointer-events: none; display: none; white-space: normal;
                    max-width: min(360px, calc(100vw - 32px)); overflow-wrap: anywhere;
                    font-family: 'JetBrains Mono', ui-monospace, monospace; font-size: 10.5px;
                    text-transform: uppercase; letter-spacing: 0.08em;
                    background: {{theme.TooltipBackground}}; color: {{theme.TooltipForeground}};
                    border: 1px solid {{theme.TooltipBorder}}; padding: 7px 11px; box-shadow: 0 10px 30px rgba(0,0,0,0.5);
                  }
                </style>
                </head>
                <body>
                <div id="graph" tabindex="0"></div>
                <script>
                (function () {
                  'use strict';
                  const CONFIG = {{configJson}};
                  const THEME = {{themeJson}};
                  const INITIAL = {{dataJson}};

                  // ===================== State =====================
                  const state = {
                    rotation: {{Js(rotationEnabled)}},
                    rotationExplicit: {{Js(rotationExplicit)}},
                    subtle: {{Js(subtleAnimations)}},
                    display: { mesh: true, labels: true, nodeSize: 1, linkThickness: 1 },
                    pendingLink: null,
                    selectedSession: null,
                    rejectFlash: null,      // { from, to, until, duration }
                    hoverNode: -1,
                    hoverSphere: null,
                    sceneAppearStart: 0
                  };
                  // Without a saved choice the operating system's reduced-motion preference decides: the
                  // spheres start still and the host is told, so its toggle reflects the real state.
                  if (!state.rotationExplicit && window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
                    state.rotation = false;
                    setTimeout(function () { post({ type: 'reducedMotion' }); }, 0);
                  }

                  const NODE_BASE_RADIUS = 4.2;   // world units, before degree/zoom/perspective
                  const NODE_MAX_PX = 14;
                  const NODE_MIN_PX = 2.2;
                  const PENDING_SCALE = 1.5;
                  const DEPTH_BUCKETS = 4;
                  const MESH_ALPHA = 0.3;
                  const APPEAR_MS = 420;
                  const DRAG_THRESHOLD_PX = 6;
                  const DOUBLE_CLICK_MS = 450;   // generous: event delivery can lag on the software WebView
                  const HOVER_THROTTLE_MS = 32;
                  const ZOOM_MIN = 0.03, ZOOM_MAX = 6, FIT_MAX = 2.5;
                  // With the camera PERSPECTIVE radii away, the projected silhouette peaks at ~1.053 R; focus
                  // rings and labels sit just outside it.
                  const SILHOUETTE = 1.12;

                  // Avalonia.Controls.WebView injects window.invokeCSharpAction on every adapter; the other
                  // paths are a defensive fallback in case a future adapter changes that convention.
                  function post(message) {
                    const json = JSON.stringify(message);
                    try { if (typeof window.invokeCSharpAction === 'function') { window.invokeCSharpAction(json); return; } } catch (e) {}
                    try { if (window.chrome && window.chrome.webview) { window.chrome.webview.postMessage(json); return; } } catch (e) {}
                    try { if (window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.postAvWebViewMessage) { window.webkit.messageHandlers.postAvWebViewMessage.postMessage(json); return; } } catch (e) {}
                    try { if (window.external && window.external.notify) { window.external.notify(json); return; } } catch (e) {}
                  }

                  // ===================== Surface =====================
                  const el = document.getElementById('graph');
                  const canvas = document.createElement('canvas');
                  el.appendChild(canvas);
                  const ctx = canvas.getContext('2d');
                  const tooltipEl = document.createElement('div');
                  tooltipEl.className = 'enki-graph-tooltip';
                  document.body.appendChild(tooltipEl);
                  const view = { width: 1, height: 1, dpr: 1 };

                  function resize() {
                    view.width = el.clientWidth || window.innerWidth || 1;
                    view.height = el.clientHeight || window.innerHeight || 1;
                    // Pixel ratio capped at 2: beyond that a software compositor pays 4x+ fill cost for
                    // detail nobody can see on hairlines and small spheres.
                    view.dpr = Math.min(2, window.devicePixelRatio || 1);
                    canvas.width = Math.max(1, Math.round(view.width * view.dpr));
                    canvas.height = Math.max(1, Math.round(view.height * view.dpr));
                    invalidate();
                  }
                  if (typeof ResizeObserver === 'function') new ResizeObserver(resize).observe(el);
                  else window.addEventListener('resize', resize);
                  // A lost 2D context (GPU reset, memory pressure) only invalidates cached bitmaps.
                  canvas.addEventListener('contextrestored', function () { sprites.clear(); invalidate(); });
                  if (document.fonts && document.fonts.load) {
                    document.fonts.load("700 16px 'Space Grotesk'").then(function () { invalidate(); }, function () {});
                  }

                  // ===================== Colour & sphere impostors =====================
                  function parseColor(css) {
                    if (typeof css !== 'string') return [255, 255, 255];
                    const hex = /^#([0-9a-f]{6})$/i.exec(css.trim());
                    if (hex) { const v = parseInt(hex[1], 16); return [(v >> 16) & 255, (v >> 8) & 255, v & 255]; }
                    const m = /rgba?\(([^)]+)\)/i.exec(css);
                    if (!m) return [255, 255, 255];
                    const parts = m[1].split(',').map(function (p) { return parseFloat(p); });
                    return [parts[0] || 0, parts[1] || 0, parts[2] || 0];
                  }
                  function mix(rgb, target, t) {
                    return 'rgb(' + rgb.map(function (c) { return Math.round(c + (target - c) * t); }).join(',') + ')';
                  }
                  // Each note is drawn as a pre-shaded sphere impostor: one small bitmap per colour, lit by a
                  // soft key light from the upper left with a discreet highlight and a darker rim. Lighting
                  // lives in view space, so the bitmap stays correct at every rotation, and drawing a cached
                  // bitmap is far cheaper on a software compositor than per-node gradients.
                  const SPRITE_SIZE = 64;
                  const sprites = new Map();
                  function spriteFor(color) {
                    let sprite = sprites.get(color);
                    if (sprite) return sprite;
                    sprite = document.createElement('canvas');
                    sprite.width = sprite.height = SPRITE_SIZE;
                    const g = sprite.getContext('2d');
                    const c = SPRITE_SIZE / 2, r = c - 1;
                    const rgb = parseColor(color);
                    const gradient = g.createRadialGradient(c - r * 0.32, c - r * 0.36, r * 0.04, c, c, r);
                    gradient.addColorStop(0, mix(rgb, 255, 0.62));
                    gradient.addColorStop(0.18, mix(rgb, 255, 0.22));
                    gradient.addColorStop(0.55, mix(rgb, 0, 0.04));
                    gradient.addColorStop(0.9, mix(rgb, 0, 0.34));
                    gradient.addColorStop(1, mix(rgb, 0, 0.5));
                    g.fillStyle = gradient;
                    g.beginPath();
                    g.arc(c, c, r, 0, Math.PI * 2);
                    g.fill();
                    sprites.set(color, sprite);
                    return sprite;
                  }

                  // ===================== Scene =====================
                  // spheres: { id, name, color, cx, cy, r, yaw, pitch, phase, dragging }
                  // nodes:   { id, label, sessionId, session, tags, color, lx, ly, lz, sphere, degree, appearAt }
                  let spheres = [], sphereById = new Map();
                  let nodes = [], nodeIndex = new Map();
                  let links = [], mesh = [], neighbors = [];
                  // Orientation survives data updates (filters, links, colours): no sphere ever snaps back.
                  const orientations = new Map();
                  let px = new Float32Array(0), py = new Float32Array(0), pr = new Float32Array(0), pd = new Float32Array(0);
                  let drawOrder = new Int32Array(0);
                  let sphereScreen = [];

                  function loadScene(data, animateNew) {
                    const now = performance.now();
                    // New data mid-gesture (e.g. a note changed on disk) invalidates the indices a drag holds.
                    if (drag && drag.sphere) drag.sphere.dragging = false;
                    drag = null;
                    const known = new Set(nodes.map(function (n) { return n.id; }));
                    spheres = data.spheres.map(function (s) {
                      let o = orientations.get(s.id);
                      if (!o) { o = { yaw: s.phase, pitch: CONFIG.axisTilt }; orientations.set(s.id, o); }
                      return { id: s.id, name: s.name, color: s.color, cx: s.x, cy: s.y, r: Math.max(1, s.r), orientation: o, dragging: false };
                    });
                    sphereById = new Map(spheres.map(function (s) { return [s.id, s]; }));
                    nodes = data.nodes.map(function (n) {
                      return {
                        id: n.id, label: n.name, sessionId: n.sessionId, session: n.session, tags: n.tags || [],
                        color: n.color, lx: n.x, ly: n.y, lz: n.z, sphere: sphereById.get(n.sessionId), degree: 0,
                        appearAt: animateNew && !known.has(n.id) ? now : -Infinity
                      };
                    }).filter(function (n) { return !!n.sphere; });
                    nodeIndex = new Map(nodes.map(function (n, i) { return [n.id, i]; }));
                    neighbors = nodes.map(function () { return []; });
                    links = [];
                    data.links.forEach(function (l) {
                      const a = nodeIndex.get(l.source), b = nodeIndex.get(l.target);
                      if (a === undefined || b === undefined || a === b) return;
                      links.push({ a: a, b: b, manual: !!l.manual });
                      neighbors[a].push(b); neighbors[b].push(a);
                    });
                    neighbors.forEach(function (list, i) { nodes[i].degree = new Set(list).size; });
                    mesh = (data.mesh || []).filter(function (p) { return p[0] < nodes.length && p[1] < nodes.length; });
                    px = new Float32Array(nodes.length); py = new Float32Array(nodes.length);
                    pr = new Float32Array(nodes.length); pd = new Float32Array(nodes.length);
                    drawOrder = new Int32Array(nodes.length);
                    state.hoverNode = -1;
                    state.hoverSphere = null;
                    tooltipEl.style.display = 'none';
                    invalidate();
                  }

                  // ===================== Camera =====================
                  // One shared camera for the whole scene: world (x, y) of the viewport centre plus a zoom
                  // scale. Sphere centres live on the z = 0 plane; each sphere adds its own perspective.
                  const camera = { x: 0, y: 0, scale: 1, anim: null };
                  let fitPending = true;

                  function worldToScreenX(x) { return (x - camera.x) * camera.scale + view.width / 2; }
                  function worldToScreenY(y) { return (y - camera.y) * camera.scale + view.height / 2; }

                  function animateCamera(target, duration) {
                    if (state.subtle) duration = Math.min(duration, 100);
                    if (duration <= 0) { camera.x = target.x; camera.y = target.y; camera.scale = target.scale; camera.anim = null; invalidate(); return; }
                    camera.anim = { from: { x: camera.x, y: camera.y, scale: camera.scale }, to: target, start: performance.now(), duration: duration };
                    invalidate();
                  }
                  function stepCamera(now) {
                    const a = camera.anim;
                    if (!a) return;
                    const t = Math.min(1, (now - a.start) / a.duration);
                    const e = 1 - Math.pow(1 - t, 3);
                    camera.x = a.from.x + (a.to.x - a.from.x) * e;
                    camera.y = a.from.y + (a.to.y - a.from.y) * e;
                    // Zoom interpolates geometrically so the motion feels uniform at any scale.
                    camera.scale = a.from.scale * Math.pow(a.to.scale / a.from.scale, e);
                    if (t >= 1) camera.anim = null;
                  }
                  // Waits (fitPending) until the view has a real size and the scene has spheres, so a document
                  // that starts empty or at 0x0 still frames its first real content.
                  function fit(duration) {
                    if (spheres.length === 0 || view.width < 2 || view.height < 2) return;
                    fitPending = false;
                    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
                    spheres.forEach(function (s) {
                      const r = s.r * SILHOUETTE;
                      minX = Math.min(minX, s.cx - r); maxX = Math.max(maxX, s.cx + r);
                      minY = Math.min(minY, s.cy - r - 40); maxY = Math.max(maxY, s.cy + r + 12);
                    });
                    const pad = 48;
                    const scale = Math.max(ZOOM_MIN, Math.min(FIT_MAX,
                      (view.width - pad * 2) / Math.max(1, maxX - minX), (view.height - pad * 2) / Math.max(1, maxY - minY)));
                    animateCamera({ x: (minX + maxX) / 2, y: (minY + maxY) / 2, scale: scale }, duration);
                  }
                  function zoomAt(sx, sy, factor) {
                    const scale = Math.max(ZOOM_MIN, Math.min(ZOOM_MAX, camera.scale * factor));
                    const wx = (sx - view.width / 2) / camera.scale + camera.x;
                    const wy = (sy - view.height / 2) / camera.scale + camera.y;
                    camera.anim = null;
                    camera.scale = scale;
                    camera.x = wx - (sx - view.width / 2) / scale;
                    camera.y = wy - (sy - view.height / 2) / scale;
                    invalidate();
                  }

                  // ===================== Projection =====================
                  // Per sphere: rotate local points by Rx(pitch)·Ry(yaw) about the sphere's own centre, then
                  // apply a perspective divide whose camera sits PERSPECTIVE radii in front of that centre.
                  // Results go into flat typed arrays reused every frame (no per-frame allocation).
                  function nodeWorldRadius(n) {
                    const base = NODE_BASE_RADIUS + Math.min(3, Math.sqrt(n.degree)) * 0.9;
                    return base * state.display.nodeSize * (n.id === state.pendingLink ? PENDING_SCALE : 1);
                  }
                  function project() {
                    sphereScreen = spheres.map(function (s) {
                      const o = s.orientation;
                      return {
                        sphere: s, x: worldToScreenX(s.cx), y: worldToScreenY(s.cy), r: s.r * camera.scale,
                        sinYaw: Math.sin(o.yaw), cosYaw: Math.cos(o.yaw), sinPitch: Math.sin(o.pitch), cosPitch: Math.cos(o.pitch),
                        distance: CONFIG.perspective * s.r
                      };
                    });
                    const screenById = new Map(sphereScreen.map(function (p) { return [p.sphere.id, p]; }));
                    for (let i = 0; i < nodes.length; i++) {
                      const n = nodes[i], p = screenById.get(n.sessionId);
                      const x1 = n.lx * p.cosYaw + n.lz * p.sinYaw;
                      const z1 = -n.lx * p.sinYaw + n.lz * p.cosYaw;
                      const y2 = n.ly * p.cosPitch + z1 * p.sinPitch;
                      const z2 = -n.ly * p.sinPitch + z1 * p.cosPitch;
                      const f = p.distance / Math.max(p.distance * 0.2, p.distance - z2);
                      px[i] = p.x + x1 * f * camera.scale;
                      py[i] = p.y + y2 * f * camera.scale;
                      pd[i] = Math.max(-1, Math.min(1, z2 / n.sphere.r));
                      pr[i] = Math.min(NODE_MAX_PX, Math.max(NODE_MIN_PX * state.display.nodeSize * f, nodeWorldRadius(n) * camera.scale * f));
                      drawOrder[i] = i;
                    }
                    // Painter's algorithm: back-to-front so nearer notes occlude farther ones.
                    drawOrder.sort(function (a, b) { return pd[a] - pd[b]; });
                  }
                  function rotatedDepth(n) {
                    const o = n.sphere.orientation;
                    const z1 = -n.lx * Math.sin(o.yaw) + n.lz * Math.cos(o.yaw);
                    return -n.ly * Math.sin(o.pitch) + z1 * Math.cos(o.pitch);
                  }

                  // ===================== Painting =====================
                  function bucketOf(depth) { return Math.min(DEPTH_BUCKETS - 1, Math.max(0, Math.floor((depth + 1) / 2 * DEPTH_BUCKETS))); }
                  // Notes kept at full strength while one is hovered: itself and its real neighbours.
                  let emphasis = null;
                  function emphasized(i) { return emphasis === null || emphasis.has(i); }
                  function appearOf(n, now) {
                    return n.appearAt === -Infinity || state.subtle ? 1 : Math.min(1, (now - n.appearAt) / APPEAR_MS);
                  }

                  function draw(now) {
                    project();
                    ctx.setTransform(1, 0, 0, 1, 0, 0);
                    ctx.clearRect(0, 0, canvas.width, canvas.height);
                    ctx.setTransform(view.dpr, 0, 0, view.dpr, 0, 0);
                    const sceneAlpha = state.subtle ? 1 : Math.min(1, (now - state.sceneAppearStart) / APPEAR_MS);
                    const hovering = state.hoverNode >= 0;
                    emphasis = hovering ? new Set(neighbors[state.hoverNode].concat([state.hoverNode])) : null;

                    // Edges are bucketed by mean depth and stroked as one path per bucket and style, so the
                    // whole scene costs a handful of stroke() calls instead of one per line.
                    const meshBuckets = [], linkBuckets = [], manualBuckets = [];
                    for (let b = 0; b < DEPTH_BUCKETS; b++) { meshBuckets.push([]); linkBuckets.push([]); manualBuckets.push([]); }
                    if (state.display.mesh) mesh.forEach(function (p) { meshBuckets[bucketOf((pd[p[0]] + pd[p[1]]) / 2)].push(p[0], p[1]); });
                    const highlighted = [];
                    links.forEach(function (l) {
                      if (hovering && (l.a === state.hoverNode || l.b === state.hoverNode)) { highlighted.push(l.a, l.b); return; }
                      (l.manual ? manualBuckets : linkBuckets)[bucketOf((pd[l.a] + pd[l.b]) / 2)].push(l.a, l.b);
                    });
                    const nodeBuckets = [];
                    for (let b = 0; b < DEPTH_BUCKETS; b++) nodeBuckets.push([]);
                    for (let k = 0; k < drawOrder.length; k++) nodeBuckets[bucketOf(pd[drawOrder[k]])].push(drawOrder[k]);

                    ctx.lineCap = 'round';
                    for (let b = 0; b < DEPTH_BUCKETS; b++) {
                      // Depth cue: far layers fade towards the background, near layers stay crisp.
                      const depthAlpha = 0.34 + 0.66 * (b + 0.5) / DEPTH_BUCKETS;
                      strokePairs(meshBuckets[b], THEME.ink80, MESH_ALPHA * depthAlpha * (hovering ? 0.3 : 1) * sceneAlpha, 0.55);
                      strokePairs(linkBuckets[b], THEME.link, depthAlpha * (hovering ? 0.12 : 1) * sceneAlpha, 0.9 * state.display.linkThickness);
                      strokePairs(manualBuckets[b], THEME.linkManual, depthAlpha * (hovering ? 0.12 : 1) * sceneAlpha, 1.4 * state.display.linkThickness);
                      const list = nodeBuckets[b];
                      for (let k = 0; k < list.length; k++) {
                        const i = list[k], n = nodes[i], r = pr[i];
                        if (px[i] < -r || py[i] < -r || px[i] > view.width + r || py[i] > view.height + r) continue;
                        const color = n.id === state.pendingLink ? THEME.nodeActive : n.color;
                        ctx.globalAlpha = (0.5 + 0.5 * (pd[i] + 1) / 2) * (emphasized(i) ? 1 : 0.16) * appearOf(n, now) * sceneAlpha;
                        ctx.drawImage(spriteFor(color), px[i] - r, py[i] - r, r * 2, r * 2);
                      }
                    }
                    strokePairs(highlighted, THEME.ink70, sceneAlpha, 2.2 * state.display.linkThickness);
                    ctx.globalAlpha = 1;

                    drawSphereFocus();
                    drawHoverRing();
                    drawPulses(now);
                    drawSessionLabels();
                    drawNoteLabels();
                  }

                  function strokePairs(pairs, color, alpha, width) {
                    if (pairs.length === 0 || alpha <= 0.002) return;
                    ctx.globalAlpha = alpha;
                    ctx.strokeStyle = color;
                    ctx.lineWidth = width;
                    ctx.beginPath();
                    for (let k = 0; k < pairs.length; k += 2) {
                      ctx.moveTo(px[pairs[k]], py[pairs[k]]);
                      ctx.lineTo(px[pairs[k + 1]], py[pairs[k + 1]]);
                    }
                    ctx.stroke();
                  }

                  // A faint circular outline marks the hovered sphere and, a little stronger, the session
                  // selected in the list — circular indicators are the one exception to square corners.
                  function drawSphereFocus() {
                    sphereScreen.forEach(function (p) {
                      const selected = p.sphere.id === state.selectedSession;
                      const hovered = p.sphere === state.hoverSphere && state.hoverNode < 0;
                      if (!selected && !hovered) return;
                      ctx.globalAlpha = selected ? 0.28 : 0.14;
                      ctx.strokeStyle = THEME.ink;
                      ctx.lineWidth = 1;
                      ctx.beginPath();
                      ctx.arc(p.x, p.y, p.r * SILHOUETTE + 6, 0, Math.PI * 2);
                      ctx.stroke();
                    });
                    ctx.globalAlpha = 1;
                  }
                  function drawHoverRing() {
                    const i = state.hoverNode;
                    if (i < 0) return;
                    ctx.globalAlpha = 0.9;
                    ctx.strokeStyle = THEME.ink;
                    ctx.lineWidth = 1.25;
                    ctx.beginPath();
                    ctx.arc(px[i], py[i], pr[i] + 2.5, 0, Math.PI * 2);
                    ctx.stroke();
                    ctx.globalAlpha = 1;
                  }
                  function drawPulses(now) {
                    if (state.pendingLink !== null) pulseRing(nodeIndex.get(state.pendingLink), THEME.nodeActive, 1, now);
                    const flash = state.rejectFlash;
                    if (flash) {
                      const remaining = flash.until - now;
                      if (remaining <= 0) state.rejectFlash = null;
                      else {
                        pulseRing(nodeIndex.get(flash.from), THEME.danger, remaining / flash.duration, now);
                        pulseRing(nodeIndex.get(flash.to), THEME.danger, remaining / flash.duration, now);
                      }
                    }
                  }
                  function pulseRing(i, color, fade, now) {
                    if (i === undefined) return;
                    const pulse = state.subtle ? 1 : 1 + 0.25 * Math.sin((now % 1200) / 1200 * Math.PI * 2);
                    ctx.globalAlpha = 0.75 * fade;
                    ctx.strokeStyle = color;
                    ctx.lineWidth = 1.6;
                    ctx.beginPath();
                    ctx.arc(px[i], py[i], Math.max(6, pr[i] * 1.55) * pulse, 0, Math.PI * 2);
                    ctx.stroke();
                    ctx.globalAlpha = 1;
                  }

                  // Session names stay upright and still: they are placed from each sphere's centre and
                  // radius only, never from its rotating notes, so they read as a fixed caption.
                  let labelRects = [];
                  function reserveLabel(x, y, w, h) {
                    if (x < 8 || y < 8 || x + w > view.width - 8 || y + h > view.height - 8) return false;
                    for (const r of labelRects)
                      if (x < r.x + r.w + 4 && x + w + 4 > r.x && y < r.y + r.h + 4 && y + h + 4 > r.y) return false;
                    labelRects.push({ x: x, y: y, w: w, h: h });
                    return true;
                  }
                  function drawSessionLabels() {
                    labelRects = [];
                    ctx.font = "700 11px 'Space Grotesk', sans-serif";
                    ctx.textBaseline = 'top';
                    sphereScreen.forEach(function (p) {
                      const s = p.sphere;
                      const text = (s.name || '').toUpperCase();
                      const width = Math.min(220, ctx.measureText(text).width + 20);
                      const x = p.x - width / 2;
                      const extent = p.r * SILHOUETTE;
                      const strong = s.id === state.selectedSession || s === state.hoverSphere;
                      for (const y of [p.y - extent - 30, p.y + extent + 10]) {
                        if (!reserveLabel(x, y, width, 22)) continue;
                        ctx.globalAlpha = 1;
                        ctx.fillStyle = THEME.labelBackground;
                        ctx.fillRect(x, y, width, 22);
                        ctx.fillStyle = s.color;
                        ctx.fillRect(x, y + 4, strong ? 3 : 2, 14);
                        ctx.fillStyle = strong ? THEME.ink : THEME.labelMuted;
                        ctx.fillText(text, x + 9, y + 5, width - 16);
                        break;
                      }
                    });
                  }
                  function drawNoteLabels() {
                    if (!state.display.labels) return;
                    const hovering = state.hoverNode >= 0;
                    if (!hovering && camera.scale < 0.9) return;
                    ctx.font = "11px 'Space Grotesk', sans-serif";
                    ctx.textBaseline = 'top';
                    ctx.fillStyle = THEME.tooltipForeground;
                    const candidates = hovering
                      ? neighbors[state.hoverNode].slice()
                      : Array.prototype.filter.call(drawOrder, function (i) { return pd[i] > 0.2; });
                    candidates.sort(function (a, b) { return nodes[b].degree - nodes[a].degree || pd[b] - pd[a]; });
                    let drawn = 0;
                    for (const i of candidates) {
                      if (drawn >= 60) break;
                      const n = nodes[i];
                      const width = Math.min(160, ctx.measureText(n.label).width);
                      const x = px[i] + pr[i] + 6, y = py[i] - 6;
                      if (!reserveLabel(x, y, width, 14)) continue;
                      ctx.globalAlpha = (hovering ? 0.95 : 0.65) * (0.55 + 0.45 * (pd[i] + 1) / 2);
                      ctx.fillText(n.label, x, y, width);
                      drawn++;
                    }
                    ctx.globalAlpha = 1;
                  }

                  // ===================== Loop =====================
                  // A single requestAnimationFrame loop for the whole scene, scheduled on demand: it stops
                  // entirely when nothing moves (rotation off, no camera tween, no pulse, no fade) and is
                  // re-armed by invalidate(). Ambient rotation runs at 30 Hz — at 0.1 rad/s that is visually
                  // continuous — while direct interaction gets 60 Hz (30 Hz in the subtle motion profile).
                  let framePending = false, needsDraw = true, lastDraw = null, failed = false;
                  function invalidate() { needsDraw = true; schedule(); }
                  function schedule() {
                    if (framePending || failed || document.hidden) return;
                    framePending = true;
                    requestAnimationFrame(frame);
                  }
                  function isAnimating(now) {
                    return (state.rotation && spheres.length > 0) || !!camera.anim || (state.pendingLink !== null && !state.subtle)
                      || !!state.rejectFlash || (!state.subtle && now - state.sceneAppearStart < APPEAR_MS)
                      || nodes.some(function (n) { return n.appearAt !== -Infinity && now - n.appearAt < APPEAR_MS; });
                  }
                  function frame(ts) {
                    framePending = false;
                    if (document.hidden) { lastDraw = null; return; }
                    const now = performance.now();
                    const animating = isAnimating(now);
                    if (!needsDraw && !animating) { lastDraw = null; return; }
                    const interval = needsDraw && !state.subtle ? 1000 / 60 : 1000 / 30;
                    if (lastDraw !== null && ts - lastDraw < interval - 1) { schedule(); return; }
                    // Delta time keeps the angular speed constant at any frame rate; the cap avoids a jump
                    // after the page was throttled or a long frame.
                    const dt = lastDraw === null ? 0 : Math.min(0.1, (ts - lastDraw) / 1000);
                    lastDraw = ts;
                    needsDraw = false;
                    try {
                      if (fitPending) fit(0);
                      stepCamera(now);
                      // A sphere holds still while it is pressed or one of its notes is under the pointer, so
                      // aiming at a note never races its motion; it resumes from the same orientation.
                      if (state.rotation) {
                        const held = state.hoverNode >= 0 ? nodes[state.hoverNode].sphere : null;
                        for (const s of spheres) if (!s.dragging && s !== held) s.orientation.yaw += CONFIG.rotationSpeed * dt;
                      }
                      draw(now);
                      // Notes keep moving under a still cursor while the spheres rotate: re-pick against
                      // this frame's projection so hover and tooltip follow what is actually under it.
                      if (state.rotation && lastPointer && !drag) updateHover(lastPointer);
                    } catch (error) {
                      failed = true;
                      post({ type: 'error', message: String(error && error.message || error) });
                      return;
                    }
                    if (isAnimating(now)) schedule();
                  }
                  document.addEventListener('visibilitychange', function () {
                    lastDraw = null;
                    if (!document.hidden) invalidate();
                  });

                  // ===================== Picking =====================
                  // Screen-space ray picking against the projected sphere impostors of the last frame: of
                  // every note whose disc contains the pointer, the nearest to the viewer wins, so a note in
                  // front is never shadowed by one behind it. A small minimum radius keeps tiny far notes
                  // clickable.
                  function pickNode(x, y) {
                    let best = -1, bestDepth = -Infinity, fallback = -1, fallbackDist = Infinity;
                    for (let i = 0; i < nodes.length; i++) {
                      const dx = px[i] - x, dy = py[i] - y, d2 = dx * dx + dy * dy;
                      const r = pr[i] + 3;
                      if (d2 <= r * r) { if (pd[i] > bestDepth) { best = i; bestDepth = pd[i]; } }
                      else if (d2 <= 121 && d2 < fallbackDist) { fallback = i; fallbackDist = d2; }
                    }
                    return best >= 0 ? best : fallback;
                  }
                  function pickSphere(x, y) {
                    let best = null, bestDist = Infinity;
                    sphereScreen.forEach(function (p) {
                      const d = Math.hypot(p.x - x, p.y - y);
                      if (d <= p.r * SILHOUETTE + 6 && d < bestDist) { best = p.sphere; bestDist = d; }
                    });
                    return best;
                  }

                  // ===================== Interaction =====================
                  // Clicks are resolved from pointerdown/pointerup positions directly, never from a hover
                  // cache built by idle mousemove events — that dependency proved unreliable under WPE.
                  let drag = null;   // { mode: 'node'|'sphere'|'pan', index, sphere, x, y, lastX, lastY, t, moved, depth }
                  let lastClick = { index: -1, at: 0 };
                  let lastHoverAt = 0;
                  let lastPointer = null;   // last position over the canvas while no button is held

                  function point(e) {
                    const rect = el.getBoundingClientRect();
                    return { x: e.clientX - rect.left, y: e.clientY - rect.top };
                  }
                  el.addEventListener('pointerdown', function (e) {
                    if (e.button !== 0) return;
                    const p = point(e);
                    fitPending = false;
                    camera.anim = null;
                    const index = pickNode(p.x, p.y);
                    const sphere = index >= 0 ? nodes[index].sphere : pickSphere(p.x, p.y);
                    drag = { mode: index >= 0 ? 'node' : sphere ? 'sphere' : 'pan', index: index, sphere: sphere,
                      x: p.x, y: p.y, lastX: p.x, lastY: p.y, t: performance.now(), moved: false,
                      depth: index >= 0 ? rotatedDepth(nodes[index]) : 0 };
                    if (sphere && drag.mode !== 'pan') sphere.dragging = true;
                    try { el.setPointerCapture(e.pointerId); } catch (err) {}
                    el.classList.add('pointer-active');
                    tooltipEl.style.display = 'none';
                    el.focus({ preventScroll: true });
                  });
                  el.addEventListener('pointermove', function (e) {
                    const p = point(e);
                    if (drag) {
                      if (!drag.moved && Math.hypot(p.x - drag.x, p.y - drag.y) > DRAG_THRESHOLD_PX) drag.moved = true;
                      if (drag.moved) {
                        const dx = p.x - drag.lastX, dy = p.y - drag.lastY;
                        if (drag.mode === 'node') moveNode(nodes[drag.index], p, drag.depth);
                        else if (drag.mode === 'sphere') {
                          const o = drag.sphere.orientation;
                          o.yaw += dx * CONFIG.manualRotation;
                          o.pitch = Math.max(-CONFIG.maxPitch, Math.min(CONFIG.maxPitch, o.pitch + dy * CONFIG.manualRotation));
                        } else {
                          camera.x -= dx / camera.scale;
                          camera.y -= dy / camera.scale;
                        }
                        drag.lastX = p.x; drag.lastY = p.y;
                        invalidate();
                      }
                      return;
                    }
                    lastPointer = { p: p, clientX: e.clientX, clientY: e.clientY };
                    const now = performance.now();
                    if (now - lastHoverAt < HOVER_THROTTLE_MS) return;
                    lastHoverAt = now;
                    updateHover(lastPointer);
                  });
                  function updateHover(pointer) {
                    const p = pointer.p;
                    const index = pickNode(p.x, p.y);
                    const sphere = index >= 0 ? nodes[index].sphere : pickSphere(p.x, p.y);
                    if (index !== state.hoverNode || sphere !== state.hoverSphere) invalidate();
                    state.hoverNode = index;
                    state.hoverSphere = sphere;
                    el.classList.toggle('node-hover', index >= 0);
                    el.classList.toggle('sphere-hover', index < 0 && !!sphere);
                    if (index < 0) { tooltipEl.style.display = 'none'; return; }
                    const n = nodes[index];
                    tooltipEl.textContent = n.label + ' — ' + n.session +
                      (n.tags.length ? ' · ' + n.tags.map(function (tag) { return '#' + tag; }).join(' ') : '');
                    tooltipEl.style.display = 'block';
                    const bounds = tooltipEl.getBoundingClientRect();
                    tooltipEl.style.left = Math.max(8, Math.min(pointer.clientX + 14, innerWidth - bounds.width - 8)) + 'px';
                    tooltipEl.style.top = Math.max(8, Math.min(pointer.clientY + 14, innerHeight - bounds.height - 8)) + 'px';
                  }
                  // Dragging a note moves it in the screen plane at its current depth, inverse-projected into
                  // its sphere's local frame and kept inside the sphere's volume.
                  function moveNode(n, p, depth) {
                    const s = n.sphere, o = s.orientation;
                    const distance = CONFIG.perspective * s.r;
                    const f = distance / Math.max(distance * 0.2, distance - depth);
                    const x2 = (p.x - worldToScreenX(s.cx)) / camera.scale / f;
                    const y2 = (p.y - worldToScreenY(s.cy)) / camera.scale / f;
                    const sinP = Math.sin(o.pitch), cosP = Math.cos(o.pitch), sinY = Math.sin(o.yaw), cosY = Math.cos(o.yaw);
                    const y1 = y2 * cosP - depth * sinP;
                    const z1 = y2 * sinP + depth * cosP;
                    let x = x2 * cosY - z1 * sinY, z = x2 * sinY + z1 * cosY, y = y1;
                    const length = Math.sqrt(x * x + y * y + z * z);
                    if (length > s.r) { const k = s.r / length; x *= k; y *= k; z *= k; }
                    n.lx = x; n.ly = y; n.lz = z;
                  }
                  function endDrag(e, cancelled) {
                    if (!drag) return;
                    const d = drag;
                    drag = null;
                    if (d.sphere) d.sphere.dragging = false;
                    el.classList.remove('pointer-active');
                    try { el.releasePointerCapture(e.pointerId); } catch (err) {}
                    if (cancelled) return;
                    if (d.moved) {
                      if (d.mode === 'node') reportPositions(d.sphere);
                      return;
                    }
                    if (performance.now() - d.t > 800) return;
                    if (d.mode === 'node') {
                      const n = nodes[d.index];
                      const now = performance.now();
                      if (lastClick.index === d.index && now - lastClick.at <= DOUBLE_CLICK_MS) {
                        // The second press belongs to the open gesture only; it is not also sent as
                        // nodeClick, so link mode can't pick a pair right before leaving the graph.
                        post({ type: 'nodeDoubleClick', path: n.id });
                        lastClick = { index: -1, at: 0 };
                      } else {
                        post({ type: 'nodeClick', path: n.id });
                        lastClick = { index: d.index, at: now };
                      }
                    } else if (d.mode === 'sphere') {
                      post({ type: 'sessionClick', sessionId: d.sphere.id });
                    }
                  }
                  el.addEventListener('pointerup', function (e) { if (e.button === 0) endDrag(e, false); });
                  el.addEventListener('pointercancel', function (e) { endDrag(e, true); invalidate(); });
                  el.addEventListener('pointerleave', function () {
                    if (drag) return;
                    lastPointer = null;
                    if (state.hoverNode >= 0 || state.hoverSphere) invalidate();
                    state.hoverNode = -1;
                    state.hoverSphere = null;
                    el.classList.remove('node-hover', 'sphere-hover');
                    tooltipEl.style.display = 'none';
                  });
                  el.addEventListener('wheel', function (e) {
                    e.preventDefault();
                    fitPending = false;
                    const p = point(e);
                    const delta = e.deltaMode === 1 ? e.deltaY * 16 : e.deltaY;
                    zoomAt(p.x, p.y, Math.pow(1.0015, -delta));
                  }, { passive: false });
                  el.addEventListener('keydown', function (e) {
                    if (e.key === '+' || e.key === '=') window.__enkiCamera('in');
                    else if (e.key === '-') window.__enkiCamera('out');
                    else if (e.key === 'Home') window.__enkiCamera('fit');
                    else if (e.key === 'ArrowLeft') { camera.x -= 40 / camera.scale; invalidate(); }
                    else if (e.key === 'ArrowRight') { camera.x += 40 / camera.scale; invalidate(); }
                    else if (e.key === 'ArrowUp') { camera.y -= 40 / camera.scale; invalidate(); }
                    else if (e.key === 'ArrowDown') { camera.y += 40 / camera.scale; invalidate(); }
                    else return;
                    e.preventDefault();
                  });

                  // Positions are stored normalised to the sphere radius, in the sphere's unrotated local
                  // frame, for every visible note of the session that changed (see SessionSphereLayout).
                  function reportPositions(sphere) {
                    const reported = nodes.filter(function (n) { return n.sphere === sphere; }).map(function (n) {
                      return { path: n.id, x: n.lx / sphere.r, y: n.ly / sphere.r, z: n.lz / sphere.r };
                    });
                    post({ type: 'positions', nodes: reported });
                  }

                  // ===================== Host API =====================
                  window.__enkiSetGraphData = function (json) {
                    state.pendingLink = null;
                    loadScene(JSON.parse(json), true);
                  };
                  window.__enkiSetPendingLink = function (id) { state.pendingLink = id || null; invalidate(); };
                  // Recolours one session in place: positions, orientation and camera stay where they are.
                  window.__enkiSetSessionColor = function (sessionId, color) {
                    nodes.forEach(function (n) { if (n.sessionId === sessionId) n.color = color; });
                    const s = sphereById.get(sessionId);
                    if (s) s.color = color;
                    invalidate();
                  };
                  window.__enkiLinkRejected = function (from, to) {
                    const now = performance.now();
                    state.rejectFlash = { from: from, to: to, until: now + 900, duration: 900 };
                    invalidate();
                  };
                  window.__enkiSetDisplay = function (value) { state.display = value; invalidate(); };
                  window.__enkiCamera = function (action) {
                    fitPending = false;
                    if (action === 'fit') { fit(180); return; }
                    const factor = action === 'in' ? 1.25 : 0.8;
                    const scale = Math.max(ZOOM_MIN, Math.min(ZOOM_MAX, camera.scale * factor));
                    animateCamera({ x: camera.x, y: camera.y, scale: scale }, 140);
                  };
                  // Pausing keeps every sphere's current orientation; resuming continues from it.
                  window.__enkiSetRotation = function (enabled) {
                    state.rotation = !!enabled;
                    lastDraw = null;
                    invalidate();
                  };
                  window.__enkiSetSelectedSession = function (sessionId) { state.selectedSession = sessionId || null; invalidate(); };
                  window.setSubtleMotion = function (value) { state.subtle = !!value; lastDraw = null; invalidate(); };

                  loadScene(INITIAL, false);
                  state.sceneAppearStart = performance.now();
                  resize();
                  // Tells the host the scene is built so it can dismiss its loading skeleton.
                  post({ type: 'ready' });
                })();
                </script>
                </body>
                </html>
                """;
    }

    /// <summary>Replaces the visible subset without reloading the document: orientation and camera stay put,
    /// and notes that were already on screen keep their positions (they come from the same layout cache).</summary>
    internal static string BuildSetGraphDataScript(SessionGraph graph, SessionSphereScene scene)
    {
        var json = JsonSerializer.Serialize(BuildPayload(graph, scene), DataJsonOptions);
        // The payload becomes a JS string literal argument, not raw script — passed through JsonSerializer once
        // more so any quote/backslash in a note title can't break out of it.
        var jsonLiteral = JsonSerializer.Serialize(json);
        return $"window.__enkiSetGraphData && window.__enkiSetGraphData({jsonLiteral});";
    }

    /// <summary>Pulses the given note as the pending first pick of a manual link, or clears the pulse when
    /// <paramref name="globalId"/> is null. Purely cosmetic — the link mechanics live host-side.</summary>
    public static string BuildSetPendingLinkScript(string? globalId)
    {
        var literal = globalId is null ? "null" : JsonSerializer.Serialize(globalId);
        return $"window.__enkiSetPendingLink && window.__enkiSetPendingLink({literal});";
    }

    /// <summary>Actualiza solo el color de los nodos de una sesión en el documento ya cargado, sin tocar
    /// layout, orientación ni cámara.</summary>
    public static string BuildSetSessionColorScript(string sessionId, string color)
    {
        var sessionLiteral = JsonSerializer.Serialize(sessionId);
        var colorLiteral = JsonSerializer.Serialize(color);
        return $"window.__enkiSetSessionColor && window.__enkiSetSessionColor({sessionLiteral}, {colorLiteral});";
    }

    /// <summary>Tells the already-loaded document a manual link attempt was refused host-side, so the click that
    /// triggered it still gets a visible reaction instead of silently doing nothing.</summary>
    public static string BuildLinkRejectedScript(string fromGlobalId, string toGlobalId)
    {
        var fromLiteral = JsonSerializer.Serialize(fromGlobalId);
        var toLiteral = JsonSerializer.Serialize(toGlobalId);
        return $"window.__enkiLinkRejected && window.__enkiLinkRejected({fromLiteral}, {toLiteral});";
    }

    /// <summary>Activa o detiene la rotación automática de todas las esferas a la vez, conservando su
    /// orientación actual; no reconstruye la escena.</summary>
    public static string BuildSetRotationScript(bool enabled) =>
        $"window.__enkiSetRotation && window.__enkiSetRotation({(enabled ? "true" : "false")});";

    /// <summary>Marca la esfera de la sesión seleccionada en la lista, o ninguna con <c>null</c>.</summary>
    public static string BuildSetSelectedSessionScript(string? sessionId)
    {
        var literal = sessionId is null ? "null" : JsonSerializer.Serialize(sessionId);
        return $"window.__enkiSetSelectedSession && window.__enkiSetSelectedSession({literal});";
    }

    /// <summary>Datos de la escena para el documento: esferas (centro, radio, fase), notas con su posición
    /// local sin rotar, enlaces reales y la malla decorativa como pares de índices de nota.</summary>
    private static object BuildPayload(SessionGraph graph, SessionSphereScene scene)
    {
        var nodes = graph.Nodes.Where(n => scene.Local.ContainsKey(n.GlobalId) && scene.Spheres.ContainsKey(n.SessionId))
            .ToArray();
        var index = nodes.Select((n, i) => (n.GlobalId, i)).ToDictionary(t => t.GlobalId, t => t.i, StringComparer.Ordinal);
        var spheres = nodes.GroupBy(n => n.SessionId, StringComparer.Ordinal).Select(g =>
        {
            var sphere = scene.Spheres[g.Key];
            var first = g.First();
            return new
            {
                id = g.Key, name = first.SessionName, color = NodeBrightness.ColorFor(first.SessionId, first.SessionColor),
                x = Round(sphere.Center.X), y = Round(sphere.Center.Y), r = Round(sphere.Radius), phase = Round(sphere.Phase)
            };
        });
        return new
        {
            spheres,
            nodes = nodes.Select(n =>
            {
                var p = scene.Local[n.GlobalId];
                return new
                {
                    id = n.GlobalId, name = n.Title, sessionId = n.SessionId, session = n.SessionName, tags = n.Tags,
                    color = NodeBrightness.ColorFor(n.SessionId, n.SessionColor), x = Round(p.X), y = Round(p.Y), z = Round(p.Z)
                };
            }),
            links = graph.Edges.Select(e => new { source = e.FromGlobalId, target = e.ToGlobalId, manual = e.IsManual }),
            mesh = scene.Mesh.Where(m => index.ContainsKey(m.From) && index.ContainsKey(m.To))
                .Select(m => new[] { index[m.From], index[m.To] })
        };

        static double Round(double value) => Math.Round(value, 3);
    }

    private sealed class GraphTheme
    {
        public required string Background;
        public required string NodeActive;
        public required string Link;
        public required string LinkManual;
        public required string Danger;
        public required string Ink;
        public required string Ink70;
        public required string Ink80;
        public required string TooltipBackground;
        public required string TooltipForeground;
        public required string TooltipBorder;
        public required string LabelBackground;
        public required string LabelMuted;

        public static GraphTheme FromApp() => new()
        {
            Background = BuildPremiumBackground(
                FindColor("AppBackgroundAltBrush", "#0D0D0D"),
                FindColor("InkBrush", "#FFFFFF", overrideAlpha: 0.05)),
            NodeActive = FindColor("InkBrush", "#FFFFFF"),
            // An auto-derived [[wikilink]] edge is real, deliberate data: at a barely-there alpha a freshly
            // linked pair looked exactly like two unrelated notes. Depth fog multiplies on top of this.
            Link = FindColor("InkBrush", "#FFFFFF", overrideAlpha: 0.6),
            // A manually drawn link is the one thing a user created explicitly; it must read as present.
            LinkManual = FindColor("InkBrush", "#FFFFFF", overrideAlpha: 0.9),
            Danger = FindColor("DangerBrush", "#F87171"),
            Ink = FindColor("InkBrush", "#FFFFFF"),
            Ink70 = FindColor("InkBrush", "#FFFFFF", overrideAlpha: 0.7),
            Ink80 = FindColor("InkBrush", "#FFFFFF", overrideAlpha: 0.8),
            TooltipBackground = FindColor("PanelBackgroundBrush", "#1B1B1B"),
            TooltipForeground = FindColor("TextPrimaryBrush", "#F5F5F5"),
            TooltipBorder = FindColor("Ink20", "rgba(255,255,255,0.2)"),
            // Session-name cards use the app's elevated surface and dim mono ink, so they read as UI.
            LabelBackground = FindColor("CardBackgroundHoverBrush", "#1F1F1F"),
            LabelMuted = FindColor("Ink40", "rgba(255,255,255,0.4)"),
        };

        /// <summary>Layers a faint dot-grid texture and a soft top-of-frame bloom over the app's own background
        /// colour — the same backdrop the loading overlay and the native renderer paint, so there is no seam.
        /// Uses the active theme's <c>InkBrush</c> so the texture respects light and dark themes.</summary>
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

    /// <summary>Reads a font file from app resources and returns a base64 data URI, cached across renders, so
    /// the same font the Avalonia UI uses is available to the WebView's canvas via <c>@font-face</c>.</summary>
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

    /// <summary>Deterministic per-group node color. Every session gets a stable, distinguishable hue derived from
    /// a hash of its id (fixed saturation and lightness, only hue varies). An explicit
    /// <see cref="SessionManifest.Color"/> overrides the auto-derived hue.</summary>
    public static class NodeBrightness
    {
        private const double Saturation = 0.55;
        private const double Lightness = 0.70;

        /// <summary>Returns an opaque rgba() string for the given session id. If <paramref name="explicitColor"/>
        /// is a valid hex string, it's used directly; otherwise a stable hue is derived from
        /// <paramref name="sessionId"/>.</summary>
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
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                LogService.Debug($"Color hexadecimal no válido '{hex}': {ex}");
                return false;
            }
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
