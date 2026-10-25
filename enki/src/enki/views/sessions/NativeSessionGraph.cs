using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using enki.Models;

namespace enki.Views.Sessions;

/// <summary>Visor de esferas dibujado por Avalonia, sin navegador ni bibliotecas WebKit. Consume la misma
/// <see cref="SessionSphereScene"/> que el documento web —proyección en perspectiva, rotación por esfera,
/// orden de profundidad y sombreado de cada nota— y emite las mismas acciones; no persiste datos.</summary>
internal sealed class NativeSessionGraph : Control
{
    private const int DepthBuckets = 4;
    private const double NodeBaseRadius = 4.2;
    private const double NodeMaxRadius = 14;
    private const double NodeMinRadius = 2.2;
    private const double PendingScale = 1.5;
    private const double Silhouette = 1.12;
    private const double DragThreshold = 4;
    private const double FrameInterval = 1000 / 30.0;
    /// <summary>Malla decorativa: visible como trama de la esfera, siempre por debajo de los enlaces reales.</summary>
    private const double MeshOpacity = 0.24;

    private sealed class SphereState(SessionSphere sphere)
    {
        public SessionSphere Sphere { get; set; } = sphere;
        public double Yaw { get; set; } = sphere.Phase;
        public double Pitch { get; set; } = SessionSphereParameters.AxisTilt;
        public bool Dragging { get; set; }
        public SphereOrientation Orientation => new(Yaw, Pitch);
    }

    private enum DragMode { None, Node, Sphere, Pan }

    private readonly SessionSphereLayout _layout;
    private SessionGraph _graph = new();
    private readonly Dictionary<string, SessionGraphNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Point3> _local = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _colors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _neighbors = new(StringComparer.Ordinal);
    // La orientación sobrevive a filtros y recargas: una esfera oculta y vuelta a mostrar no recupera su fase
    // inicial. _spheres son las visibles; comparten instancia con _sphereStates.
    private readonly Dictionary<string, SphereState> _sphereStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SphereState> _spheres = new(StringComparer.Ordinal);
    private IReadOnlyList<(string From, string To)> _mesh = [];
    private readonly Dictionary<(string Color, int Bucket, bool Dim), IBrush> _nodeBrushes = new();

    // Proyección del último fotograma, en arrays paralelos indexados como _order.
    private string[] _ids = [];
    private double[] _px = [], _py = [], _pr = [], _pd = [];
    private int[] _order = [];
    private bool _projectionDirty = true;

    private bool _showMesh = true;
    private bool _showLabels = true;
    private double _nodeSize = 1;
    private double _linkThickness = 1;
    private double _scale = 1;
    private Point _camera;
    private bool _needsFit = true;
    private bool _rotationEnabled = true;
    private string? _selectedSession;
    private string? _hovered;
    private string? _hoveredSphere;
    private string? _pending;
    private HashSet<string> _rejected = new(StringComparer.Ordinal);
    private IDisposable? _rejectionReset;

    private DragMode _dragMode;
    private string? _pressedNode;
    private string? _pressedSphere;
    private double _pressedDepth;
    private Point _pressPoint;
    private Point _lastPoint;
    private bool _dragged;
    private int _clickCount;
    private IPointer? _pointer;
    private bool _frameRequested;
    private TimeSpan? _lastFrame;

    public event Action<string>? NodeClicked;
    public event Action<string>? NodeDoubleClicked;
    public event Action<string>? SessionClicked;
    public event Action<IReadOnlyList<(string RelativePath, double X, double Y, double Z)>>? PositionsChanged;

    private readonly List<Rect> _labelBounds = [];
    internal IReadOnlyList<Rect> LabelBounds => _labelBounds;
    internal int NodeCount => _nodes.Count;
    internal bool IsRotationEnabled => _rotationEnabled;

    internal Point NodeScreenPosition(string id)
    {
        EnsureProjection();
        var index = Array.IndexOf(_ids, id);
        return new Point(_px[index], _py[index]);
    }

    internal SphereOrientation OrientationOf(string sessionId) => _spheres[sessionId].Orientation;
    internal Point3 LocalPosition(string id) => _local[id];
    internal Point SphereScreenCenter(string sessionId) => ToScreen(_spheres[sessionId].Sphere.Center);

    public NativeSessionGraph() : this(new SessionSphereLayout()) { }

    public NativeSessionGraph(SessionSphereLayout layout)
    {
        _layout = layout;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        ClipToBounds = true;
        AttachedToVisualTree += (_, _) => RequestFrame();
        DetachedFromVisualTree += (_, _) =>
        {
            _pointer?.Capture(null);
            _rejectionReset?.Dispose();
            _rejectionReset = null;
            _rejected.Clear();
            _lastFrame = null;
        };
    }

    public void SetGraph(SessionGraph graph, bool reset)
    {
        _pointer?.Capture(null);
        _hovered = null;
        _hoveredSphere = null;
        _graph = graph;
        var scene = _layout.Build(graph);
        _nodes.Clear();
        _local.Clear();
        _colors.Clear();
        _neighbors.Clear();
        foreach (var node in graph.Nodes)
        {
            if (!scene.Local.TryGetValue(node.GlobalId, out var local)) continue;
            _nodes[node.GlobalId] = node;
            _local[node.GlobalId] = local;
            _colors[node.GlobalId] = SessionGraphRenderer.NodeBrightness.ColorFor(node.SessionId, node.SessionColor);
            _neighbors[node.GlobalId] = new HashSet<string>(StringComparer.Ordinal);
        }
        foreach (var edge in graph.Edges)
        {
            if (!_neighbors.ContainsKey(edge.FromGlobalId) || !_neighbors.ContainsKey(edge.ToGlobalId)) continue;
            _neighbors[edge.FromGlobalId].Add(edge.ToGlobalId);
            _neighbors[edge.ToGlobalId].Add(edge.FromGlobalId);
        }
        _spheres.Clear();
        foreach (var (sessionId, sphere) in scene.Spheres)
        {
            if (_sphereStates.TryGetValue(sessionId, out var state)) state.Sphere = sphere;
            else _sphereStates[sessionId] = state = new SphereState(sphere);
            _spheres[sessionId] = state;
        }
        _mesh = scene.Mesh;
        if (_pending is not null && !_nodes.ContainsKey(_pending)) _pending = null;
        _ids = _nodes.Keys.ToArray();
        _px = new double[_ids.Length];
        _py = new double[_ids.Length];
        _pr = new double[_ids.Length];
        _pd = new double[_ids.Length];
        _order = new int[_ids.Length];
        _needsFit |= reset;
        InvalidateScene();
        RequestFrame();
    }

    public void SetDisplay(bool mesh, bool labels, double nodeSize, double linkThickness)
    {
        _showMesh = mesh;
        _showLabels = labels;
        _nodeSize = Math.Clamp(nodeSize, 0.5, 2);
        _linkThickness = Math.Clamp(linkThickness, 0.5, 2);
        InvalidateScene();
    }

    /// <summary>Activa o detiene la rotación de todas las esferas; detenerla conserva su orientación actual.</summary>
    public void SetRotationEnabled(bool enabled)
    {
        _rotationEnabled = enabled;
        _lastFrame = null;
        RequestFrame();
    }

    public void SetSelectedSession(string? sessionId)
    {
        _selectedSession = sessionId;
        InvalidateVisual();
    }

    public void MoveCamera(string action)
    {
        if (action == "fit") _needsFit = true;
        else
        {
            if (_needsFit) Fit();
            _scale = Math.Clamp(_scale * (action == "in" ? 1.25 : 0.8), 0.03, 6);
        }
        InvalidateScene();
    }

    public void SetPendingLink(string? id)
    {
        _pending = id;
        InvalidateScene();
    }

    public void SetSessionColor(string sessionId, string color)
    {
        foreach (var node in _graph.Nodes.Where(n => n.SessionId == sessionId && _colors.ContainsKey(n.GlobalId)))
            _colors[node.GlobalId] = SessionGraphRenderer.NodeBrightness.ColorFor(sessionId, color);
        InvalidateVisual();
    }

    public void ShowRejectedLink(string from, string to)
    {
        _rejectionReset?.Dispose();
        _rejected = new HashSet<string>([from, to], StringComparer.Ordinal);
        InvalidateVisual();
        _rejectionReset = DispatcherTimer.RunOnce(() =>
        {
            _rejected.Clear();
            _rejectionReset = null;
            InvalidateVisual();
        }, TimeSpan.FromMilliseconds(700));
    }

    /// <summary>Avanza la rotación automática <paramref name="seconds"/> segundos (tiempo real entre fotogramas,
    /// no un incremento fijo por fotograma). Solo cambia la orientación de cada esfera, nunca el layout. La
    /// esfera que se está pulsando o que tiene una nota bajo el puntero se queda quieta: apuntar a una nota
    /// no compite con su movimiento, y al salir sigue girando desde donde estaba.</summary>
    internal void AdvanceRotation(double seconds)
    {
        if (!_rotationEnabled || seconds <= 0) return;
        var held = _hovered is { } hovered && _nodes.TryGetValue(hovered, out var node) ? node.SessionId : null;
        foreach (var (sessionId, state) in _spheres)
            if (!state.Dragging && sessionId != held) state.Yaw += SessionSphereParameters.RotationSpeed * seconds;
        InvalidateScene();
    }

    // ===================== Bucle de animación =====================

    // Un único bucle para todas las esferas, solo mientras haya algo que girar: con la rotación detenida no
    // se pide ningún fotograma y el visor solo se repinta por interacción.
    private void RequestFrame()
    {
        if (_frameRequested || !_rotationEnabled || _spheres.Count == 0 || !IsEffectivelyVisible) return;
        if (TopLevel.GetTopLevel(this) is not { } topLevel) return;
        _frameRequested = true;
        topLevel.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan time)
    {
        _frameRequested = false;
        if (!_rotationEnabled || TopLevel.GetTopLevel(this) is null || !IsEffectivelyVisible)
        {
            _lastFrame = null;
            return;
        }
        if (_lastFrame is { } last && (time - last).TotalMilliseconds < FrameInterval - 1)
        {
            RequestFrame();
            return;
        }
        // El salto se acota tras una pausa (ventana oculta, fotograma largo) para no girar de golpe.
        var seconds = _lastFrame is { } previous ? Math.Min(0.1, (time - previous).TotalSeconds) : 0;
        _lastFrame = time;
        AdvanceRotation(seconds);
        RequestFrame();
    }

    private void InvalidateScene()
    {
        _projectionDirty = true;
        InvalidateVisual();
    }

    // ===================== Proyección =====================

    private double NodeRadius(string id, double perspective)
    {
        var world = (NodeBaseRadius + Math.Min(3, Math.Sqrt(_neighbors[id].Count)) * 0.9) * _nodeSize
            * (id == _pending ? PendingScale : 1);
        return Math.Min(NodeMaxRadius, Math.Max(NodeMinRadius * _nodeSize * perspective, world * _scale * perspective));
    }

    private void EnsureProjection()
    {
        if (_needsFit) Fit();
        if (!_projectionDirty) return;
        _projectionDirty = false;
        for (var i = 0; i < _ids.Length; i++)
        {
            var id = _ids[i];
            var state = _spheres[_nodes[id].SessionId];
            var (offset, depth, perspective) = SessionSphereProjection.Project(_local[id], state.Sphere.Radius, state.Orientation);
            var center = ToScreen(state.Sphere.Center);
            _px[i] = center.X + offset.X * _scale;
            _py[i] = center.Y + offset.Y * _scale;
            _pd[i] = depth;
            _pr[i] = NodeRadius(id, perspective);
            _order[i] = i;
        }
        // Algoritmo del pintor: de atrás hacia delante, para que las notas cercanas tapen a las lejanas.
        Array.Sort(_order, (a, b) => _pd[a].CompareTo(_pd[b]));
    }

    private static int BucketOf(double depth) => Math.Clamp((int)Math.Floor((depth + 1) / 2 * DepthBuckets), 0, DepthBuckets - 1);

    private bool IsEmphasized(string id) => _hovered is null || id == _hovered ||
        (_neighbors.TryGetValue(_hovered, out var neighbors) && neighbors.Contains(id));

    // ===================== Pintado =====================

    private IBrush Brush(string key, IBrush fallback) => this.TryFindResource(key, out var value) && value is IBrush brush ? brush : fallback;

    private Color ColorOf(string key, Color fallback) => Brush(key, new SolidColorBrush(fallback)) is ISolidColorBrush solid ? solid.Color : fallback;

    /// <summary>Impostor esférico por color, profundidad y énfasis: degradado radial con la luz arriba a la
    /// izquierda, un reflejo discreto y el borde oscurecido. La niebla de profundidad va en el propio pincel.</summary>
    private IBrush NodeBrush(string color, int bucket, bool dim)
    {
        if (_nodeBrushes.TryGetValue((color, bucket, dim), out var cached)) return cached;
        var baseColor = Color.Parse(color);
        var alpha = (0.5 + 0.5 * (bucket + 0.5) / DepthBuckets) * (dim ? 0.16 : 1);
        Color Shade(double toward, double amount) => Color.FromArgb((byte)Math.Round(255 * alpha),
            (byte)Math.Round(baseColor.R + (toward - baseColor.R) * amount),
            (byte)Math.Round(baseColor.G + (toward - baseColor.G) * amount),
            (byte)Math.Round(baseColor.B + (toward - baseColor.B) * amount));
        var brush = new ImmutableRadialGradientBrush(
            [
                new ImmutableGradientStop(0, Shade(255, 0.62)),
                new ImmutableGradientStop(0.18, Shade(255, 0.22)),
                new ImmutableGradientStop(0.55, Shade(0, 0.04)),
                new ImmutableGradientStop(0.9, Shade(0, 0.34)),
                new ImmutableGradientStop(1, Shade(0, 0.5))
            ],
            center: RelativePoint.Center,
            gradientOrigin: new RelativePoint(0.34, 0.32, RelativeUnit.Relative),
            radiusX: new RelativeScalar(0.5, RelativeUnit.Relative),
            radiusY: new RelativeScalar(0.5, RelativeUnit.Relative));
        _nodeBrushes[(color, bucket, dim)] = brush;
        return brush;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        EnsureProjection();
        var rect = new Rect(Bounds.Size);
        context.FillRectangle(Brush("AppBackgroundAltBrush", Brushes.Black), rect);
        context.FillRectangle(Brush("GraphBackdropDotsBrush", Brushes.Transparent), rect);
        context.FillRectangle(Brush("GraphBackdropBloomBrush", Brushes.Transparent), rect);

        var ink = ColorOf("InkBrush", Colors.White);
        var index = new Dictionary<string, int>(_ids.Length, StringComparer.Ordinal);
        for (var i = 0; i < _ids.Length; i++) index[_ids[i]] = i;
        var hovering = _hovered is not null;

        // Las aristas se agrupan por profundidad media y estilo: una geometría por grupo en vez de una línea suelta.
        var meshGeometry = new StreamGeometryContext?[DepthBuckets];
        var linkGeometry = new StreamGeometryContext?[DepthBuckets];
        var manualGeometry = new StreamGeometryContext?[DepthBuckets];
        var geometries = new StreamGeometry?[DepthBuckets * 3];
        var highlighted = new List<(int A, int B)>();
        if (_showMesh)
            foreach (var (from, to) in _mesh)
                if (index.TryGetValue(from, out var a) && index.TryGetValue(to, out var b))
                    AddLine(meshGeometry, 0, a, b);
        foreach (var edge in _graph.Edges)
        {
            if (!index.TryGetValue(edge.FromGlobalId, out var a) || !index.TryGetValue(edge.ToGlobalId, out var b)) continue;
            if (hovering && (edge.FromGlobalId == _hovered || edge.ToGlobalId == _hovered)) highlighted.Add((a, b));
            else AddLine(edge.IsManual ? manualGeometry : linkGeometry, edge.IsManual ? 2 : 1, a, b);
        }
        foreach (var group in new[] { meshGeometry, linkGeometry, manualGeometry })
            foreach (var geometry in group) geometry?.Dispose();

        var buckets = Enumerable.Range(0, DepthBuckets).Select(_ => new List<int>()).ToArray();
        foreach (var i in _order) buckets[BucketOf(_pd[i])].Add(i);
        for (var b = 0; b < DepthBuckets; b++)
        {
            var depthAlpha = 0.34 + 0.66 * (b + 0.5) / DepthBuckets;
            DrawGeometry(geometries[b], ink, MeshOpacity * depthAlpha * (hovering ? 0.3 : 1), 0.55);
            DrawGeometry(geometries[DepthBuckets + b], ink, 0.6 * depthAlpha * (hovering ? 0.12 : 1), 0.9 * _linkThickness);
            DrawGeometry(geometries[DepthBuckets * 2 + b], ink, 0.9 * depthAlpha * (hovering ? 0.12 : 1), 1.4 * _linkThickness);
            foreach (var i in buckets[b])
            {
                var point = new Point(_px[i], _py[i]);
                if (!rect.Inflate(_pr[i]).Contains(point)) continue;
                var id = _ids[i];
                var color = id == _pending ? ColorToCss(ink) : _colors[id];
                context.DrawEllipse(NodeBrush(color, b, !IsEmphasized(id)), null, point, _pr[i], _pr[i]);
            }
        }
        if (highlighted.Count > 0)
        {
            var pen = new ImmutablePen(new ImmutableSolidColorBrush(ink, 0.7), 2.2 * _linkThickness, lineCap: PenLineCap.Round);
            foreach (var (a, b) in highlighted) context.DrawLine(pen, new Point(_px[a], _py[a]), new Point(_px[b], _py[b]));
        }

        DrawFocusRings(context, ink);
        foreach (var i in _order)
        {
            var id = _ids[i];
            if (id != _pending && id != _hovered && !_rejected.Contains(id)) continue;
            var ring = _rejected.Contains(id) ? Brush("DangerBrush", Brushes.Red) : new ImmutableSolidColorBrush(ink, 0.9);
            context.DrawEllipse(null, new Pen(ring, 1.5), new Point(_px[i], _py[i]), _pr[i] + 4, _pr[i] + 4);
        }
        DrawLabels(context, rect, ink);

        void AddLine(StreamGeometryContext?[] group, int style, int a, int b)
        {
            var bucket = BucketOf((_pd[a] + _pd[b]) / 2);
            if (group[bucket] is not { } stream)
            {
                var geometry = new StreamGeometry();
                geometries[style * DepthBuckets + bucket] = geometry;
                stream = group[bucket] = geometry.Open();
            }
            stream.BeginFigure(new Point(_px[a], _py[a]), false);
            stream.LineTo(new Point(_px[b], _py[b]));
            stream.EndFigure(false);
        }

        void DrawGeometry(StreamGeometry? geometry, Color color, double opacity, double thickness)
        {
            if (geometry is null) return;
            context.DrawGeometry(null, new ImmutablePen(new ImmutableSolidColorBrush(color, opacity), thickness, lineCap: PenLineCap.Round), geometry);
        }
    }

    private static string ColorToCss(Color color) => $"rgba({color.R},{color.G},{color.B},1.00)";

    /// <summary>Contorno circular discreto de la esfera señalada y, algo más marcado, de la sesión seleccionada.</summary>
    private void DrawFocusRings(DrawingContext context, Color ink)
    {
        foreach (var (sessionId, state) in _spheres)
        {
            var selected = sessionId == _selectedSession;
            var hovered = sessionId == _hoveredSphere && _hovered is null;
            if (!selected && !hovered) continue;
            var radius = state.Sphere.Radius * _scale * Silhouette + 6;
            context.DrawEllipse(null, new ImmutablePen(new ImmutableSolidColorBrush(ink, selected ? 0.28 : 0.14), 1),
                ToScreen(state.Sphere.Center), radius, radius);
        }
    }

    private void DrawLabels(DrawingContext context, Rect rect, Color ink)
    {
        var font = this.TryFindResource("MonoFontFamily", out var resource) && resource is FontFamily family ? family : FontFamily.Default;
        _labelBounds.Clear();
        var occupied = new List<Rect>();
        var nodeBounds = new Rect[_ids.Length];
        for (var i = 0; i < _ids.Length; i++)
            nodeBounds[i] = new Rect(_px[i] - _pr[i] - 3, _py[i] - _pr[i] - 3, _pr[i] * 2 + 6, _pr[i] * 2 + 6);
        var inkBrush = new ImmutableSolidColorBrush(ink, 0.9);

        // Reserva primero la ficha enfocada para que ninguna etiqueta la tape.
        FormattedText? detail = null;
        Rect detailBounds = default;
        if (_hovered is { } hoveredId && _nodes.TryGetValue(hoveredId, out var hovered) && Bounds.Width > 24 && Bounds.Height > 24)
        {
            var point = NodeScreenPosition(hoveredId);
            if (rect.Contains(point))
            {
                detail = new FormattedText($"{hovered.Title}\n{hovered.SessionName}", CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, new Typeface(font), 12, inkBrush)
                {
                    MaxTextWidth = Math.Max(1, Math.Min(320, Bounds.Width - 32)),
                    MaxTextHeight = Math.Max(1, Math.Min(100, Bounds.Height - 32)),
                    Trimming = TextTrimming.CharacterEllipsis
                };
                var size = new Size(detail.Width + 16, detail.Height + 12);
                var top = point.Y + 16;
                if (top + size.Height > Bounds.Height - 8) top = point.Y - size.Height - 16;
                detailBounds = new Rect(new Point(Math.Clamp(point.X + 16, 8, Math.Max(8, Bounds.Width - size.Width - 8)),
                    Math.Clamp(top, 8, Math.Max(8, Bounds.Height - size.Height - 8))), size);
                occupied.Add(detailBounds.Inflate(4));
            }
        }

        // Nombres de sesión fijos: se colocan desde el centro y el radio de la esfera, nunca desde sus notas en
        // movimiento, así que no giran con ella.
        var firstNodes = _nodes.Values.GroupBy(n => n.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var (sessionId, state) in _spheres.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            if (Bounds.Width < 40) break;
            if (!firstNodes.TryGetValue(sessionId, out var first)) continue;
            var strong = sessionId == _selectedSession || sessionId == _hoveredSphere;
            var text = new FormattedText(first.SessionName, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(font), 11, strong ? inkBrush : Brush("Ink70", Brushes.Gray))
            {
                MaxTextWidth = Math.Max(1, Math.Min(180, Bounds.Width - 28)),
                MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis
            };
            var center = ToScreen(state.Sphere.Center);
            var extent = state.Sphere.Radius * _scale * Silhouette;
            var size = new Size(text.Width + 16, text.Height + 6);
            var left = Math.Clamp(center.X - size.Width / 2, 8, Math.Max(8, Bounds.Width - size.Width - 8));
            foreach (var top in new[] { center.Y - extent - size.Height - 12, center.Y + extent + 10 })
            {
                var label = new Rect(new Point(left, top), size);
                if (!rect.Contains(label) || occupied.Any(r => r.Intersects(label.Inflate(4))) || nodeBounds.Any(r => r.Intersects(label))) continue;
                occupied.Add(label);
                _labelBounds.Add(label);
                context.FillRectangle(Brush("GraphChipBackgroundBrush", Brushes.Black), label);
                context.FillRectangle(new ImmutableSolidColorBrush(Color.Parse(_colors[first.GlobalId])),
                    new Rect(label.X, label.Y + 4, strong ? 3 : 2, Math.Max(1, label.Height - 8)));
                context.DrawText(text, label.TopLeft + new Vector(8, 3));
                break;
            }
        }

        if (_showLabels && (_hovered is not null || _scale >= 0.9))
        {
            var candidates = _hovered is { } focus
                ? _neighbors[focus].Select(id => Array.IndexOf(_ids, id))
                : _order.Where(i => _pd[i] > 0.2);
            var drawn = 0;
            foreach (var i in candidates.OrderByDescending(i => _neighbors[_ids[i]].Count).ThenByDescending(i => _pd[i]))
            {
                if (drawn >= 60) break;
                var node = _nodes[_ids[i]];
                var title = new FormattedText(node.Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(font), 11, Brush("Ink70", Brushes.Gray))
                    { MaxTextWidth = 160, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                var label = new Rect(new Point(_px[i] + _pr[i] + 6, _py[i] - title.Height / 2), new Size(title.Width, title.Height));
                if (!rect.Contains(label) || occupied.Any(r => r.Intersects(label.Inflate(3))) || nodeBounds.Any(r => r.Intersects(label))) continue;
                occupied.Add(label);
                _labelBounds.Add(label);
                context.DrawText(title, label.TopLeft);
                drawn++;
            }
        }

        if (detail is not null)
        {
            context.FillRectangle(Brush("GraphChipBackgroundBrush", Brushes.Black), detailBounds);
            context.DrawRectangle(new Pen(Brush("Ink20", Brushes.Gray), 1), detailBounds);
            context.DrawText(detail, detailBounds.TopLeft + new Vector(8, 6));
        }
    }

    // ===================== Cámara =====================

    private void Fit()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        _needsFit = false;
        _projectionDirty = true;
        if (_spheres.Count == 0) return;
        var minX = _spheres.Values.Min(s => s.Sphere.Center.X - s.Sphere.Radius * Silhouette);
        var maxX = _spheres.Values.Max(s => s.Sphere.Center.X + s.Sphere.Radius * Silhouette);
        var minY = _spheres.Values.Min(s => s.Sphere.Center.Y - s.Sphere.Radius * Silhouette - 40);
        var maxY = _spheres.Values.Max(s => s.Sphere.Center.Y + s.Sphere.Radius * Silhouette + 12);
        _scale = Math.Clamp(Math.Min(Math.Max(1, Bounds.Width - 96) / Math.Max(1, maxX - minX),
            Math.Max(1, Bounds.Height - 96) / Math.Max(1, maxY - minY)), 0.03, 2.5);
        _camera = new Point((minX + maxX) / 2, (minY + maxY) / 2);
    }

    private Point ToScreen(Point world) =>
        new((world.X - _camera.X) * _scale + Bounds.Width / 2, (world.Y - _camera.Y) * _scale + Bounds.Height / 2);

    private Point ToWorld(Point screen) =>
        new((screen.X - Bounds.Width / 2) / _scale + _camera.X, (screen.Y - Bounds.Height / 2) / _scale + _camera.Y);

    // ===================== Interacción =====================

    private static double DistanceSquared(Point a, Point b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

    /// <summary>Selección por rayo en pantalla: de todas las notas cuyo disco contiene el puntero gana la más
    /// cercana al observador; si ninguna lo contiene, la más próxima a menos de 11 px.</summary>
    private string? HitNode(Point point)
    {
        EnsureProjection();
        int best = -1, fallback = -1;
        double bestDepth = double.NegativeInfinity, fallbackDistance = 121;
        for (var i = 0; i < _ids.Length; i++)
        {
            var distance = DistanceSquared(new Point(_px[i], _py[i]), point);
            var radius = _pr[i] + 3;
            if (distance <= radius * radius)
            {
                if (_pd[i] > bestDepth) { best = i; bestDepth = _pd[i]; }
            }
            else if (distance <= fallbackDistance) { fallback = i; fallbackDistance = distance; }
        }
        var hit = best >= 0 ? best : fallback;
        return hit >= 0 ? _ids[hit] : null;
    }

    private string? HitSphere(Point point)
    {
        string? hit = null;
        var closest = double.PositiveInfinity;
        foreach (var (sessionId, state) in _spheres)
        {
            var distance = Math.Sqrt(DistanceSquared(ToScreen(state.Sphere.Center), point));
            if (distance > state.Sphere.Radius * _scale * Silhouette + 6 || distance >= closest) continue;
            closest = distance;
            hit = sessionId;
        }
        return hit;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        _pressPoint = _lastPoint = e.GetPosition(this);
        _pressedNode = HitNode(_pressPoint);
        _pressedSphere = _pressedNode is { } id ? _nodes[id].SessionId : HitSphere(_pressPoint);
        _dragMode = _pressedNode is not null ? DragMode.Node : _pressedSphere is not null ? DragMode.Sphere : DragMode.Pan;
        if (_pressedNode is { } pressed)
        {
            var state = _spheres[_nodes[pressed].SessionId];
            _pressedDepth = SessionSphereProjection.Rotate(_local[pressed], state.Orientation).Z;
        }
        if (_pressedSphere is { } held && _dragMode != DragMode.Pan) _spheres[held].Dragging = true;
        _clickCount = e.ClickCount;
        _dragged = false;
        _pointer = e.Pointer;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_pointer is not null)
        {
            if (!_dragged && DistanceSquared(point, _pressPoint) > DragThreshold * DragThreshold) _dragged = true;
            if (_dragged)
            {
                var delta = point - _lastPoint;
                switch (_dragMode)
                {
                    case DragMode.Node:
                        MoveNode(_pressedNode!, point);
                        break;
                    case DragMode.Sphere:
                        var state = _spheres[_pressedSphere!];
                        state.Yaw += delta.X * SessionSphereParameters.ManualRotationPerPixel;
                        state.Pitch = Math.Clamp(state.Pitch + delta.Y * SessionSphereParameters.ManualRotationPerPixel,
                            -SessionSphereParameters.MaxPitch, SessionSphereParameters.MaxPitch);
                        break;
                    default:
                        _camera -= delta / _scale;
                        break;
                }
                _lastPoint = point;
                InvalidateScene();
            }
            return;
        }
        var hovered = HitNode(point);
        var hoveredSphere = hovered is { } id ? _nodes[id].SessionId : HitSphere(point);
        if (hovered == _hovered && hoveredSphere == _hoveredSphere) return;
        _hovered = hovered;
        _hoveredSphere = hoveredSphere;
        InvalidateScene();
    }

    /// <summary>Mueve la nota en el plano de la pantalla a su profundidad actual, dentro del volumen de su esfera.</summary>
    private void MoveNode(string id, Point point)
    {
        var state = _spheres[_nodes[id].SessionId];
        var offset = (point - ToScreen(state.Sphere.Center)) / _scale;
        var local = SessionSphereProjection.FromScreenOffset(offset, _pressedDepth, state.Sphere.Radius, state.Orientation);
        _local[id] = _layout.Move(state.Sphere.SessionId, id, local);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pointer is null || e.InitialPressMouseButton != MouseButton.Left) return;
        var node = _pressedNode;
        var sphere = _pressedSphere;
        var mode = _dragMode;
        var dragged = _dragged;
        var clicks = _clickCount;
        e.Pointer.Capture(null);
        if (dragged)
        {
            if (mode == DragMode.Node && sphere is not null) ReportPositions(sphere);
        }
        else if (mode == DragMode.Node && node is not null && HitNode(e.GetPosition(this)) == node)
        {
            if (clicks % 2 == 0) NodeDoubleClicked?.Invoke(node);
            else NodeClicked?.Invoke(node);
        }
        else if (mode == DragMode.Sphere && sphere is not null)
        {
            SessionClicked?.Invoke(sphere);
        }
        e.Handled = true;
    }

    /// <summary>Guarda todas las notas visibles de la sesión, normalizadas a su radio: las vecinas conservan su
    /// lugar al recargar y el valor no depende del tamaño de la esfera.</summary>
    private void ReportPositions(string sessionId)
    {
        var radius = _spheres[sessionId].Sphere.Radius;
        PositionsChanged?.Invoke(_nodes.Values.Where(n => n.SessionId == sessionId).Select(n =>
        {
            var p = _local[n.GlobalId];
            return (n.GlobalId, p.X / radius, p.Y / radius, p.Z / radius);
        }).ToArray());
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_pressedSphere is { } sessionId && _spheres.TryGetValue(sessionId, out var state)) state.Dragging = false;
        _pointer = null;
        _pressedNode = null;
        _pressedSphere = null;
        _dragMode = DragMode.None;
        _dragged = false;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = null;
        _hoveredSphere = null;
        InvalidateScene();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Add: case Key.OemPlus: MoveCamera("in"); break;
            case Key.Subtract: case Key.OemMinus: MoveCamera("out"); break;
            case Key.Home: MoveCamera("fit"); break;
            case Key.Left: _camera -= new Vector(40 / _scale, 0); break;
            case Key.Right: _camera += new Vector(40 / _scale, 0); break;
            case Key.Up: _camera -= new Vector(0, 40 / _scale); break;
            case Key.Down: _camera += new Vector(0, 40 / _scale); break;
            default: return;
        }
        InvalidateScene();
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_needsFit) Fit();
        var point = e.GetPosition(this);
        var world = ToWorld(point);
        _scale = Math.Clamp(_scale * Math.Pow(1.15, e.Delta.Y), 0.03, 6);
        _camera = new Point(world.X - (point.X - Bounds.Width / 2) / _scale, world.Y - (point.Y - Bounds.Height / 2) / _scale);
        InvalidateScene();
        e.Handled = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateScene();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // El bucle se detiene mientras el visor no es visible; al volver, la rotación continúa sin salto.
        if (change.Property == IsVisibleProperty && IsVisible)
        {
            _lastFrame = null;
            RequestFrame();
        }
    }
}
