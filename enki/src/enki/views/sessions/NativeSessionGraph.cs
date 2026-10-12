using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using enki.Models;

namespace enki.Views.Sessions;

/// <summary>Grafo de compatibilidad dibujado por Avalonia, sin navegador ni bibliotecas WebKit.
/// Consume el mismo grafo y emite las mismas acciones que el renderizador web; no persiste datos.</summary>
internal sealed class NativeSessionGraph : Control
{
    private SessionGraph _graph = new();
    private readonly Dictionary<string, Point> _positions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IBrush> _colors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionGraphNode> _nodes = new(StringComparer.Ordinal);
    private double _scale = 1;
    private Vector _offset;
    private bool _needsFit = true;
    private string? _hovered;
    private string? _pending;
    private string? _pressedNode;
    private Point _pressPoint;
    private Point _lastPoint;
    private bool _dragged;
    private int _clickCount;
    private IPointer? _pointer;
    private HashSet<string> _rejected = new(StringComparer.Ordinal);
    private IDisposable? _rejectionReset;

    public event Action<string>? NodeClicked;
    public event Action<string>? NodeDoubleClicked;
    public event Action<IReadOnlyList<(string RelativePath, double X, double Y, double Z)>>? PositionsChanged;

    internal int NodeCount => _nodes.Count;
    internal Point NodeScreenPosition(string id) => ToScreen(_positions[id]);

    public NativeSessionGraph()
    {
        Focusable = true;
        ClipToBounds = true;
        DetachedFromVisualTree += (_, _) =>
        {
            _pointer?.Capture(null);
            _rejectionReset?.Dispose();
            _rejectionReset = null;
            _rejected.Clear();
        };
    }

    public void SetGraph(SessionGraph graph, bool reset)
    {
        _pointer?.Capture(null);
        _hovered = null;
        ToolTip.SetTip(this, null);
        if (reset) _positions.Clear();
        _graph = graph;
        _nodes.Clear();
        _colors.Clear();
        var visible = graph.Nodes.Select(n => n.GlobalId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _positions.Keys.Where(id => !visible.Contains(id)).ToArray())
            _positions.Remove(id);
        if (_pending is not null && !visible.Contains(_pending)) _pending = null;

        // Distribución determinista por sesión. Las posiciones guardadas o arrastradas tienen prioridad.
        // Coste lineal: también funciona con vaults grandes sin una simulación cuadrática en el hilo UI.
        var groups = graph.Nodes.GroupBy(n => n.SessionId).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
        var largestRadius = groups.Length == 0 ? 0 : 30 * Math.Sqrt(groups.Max(g => g.Count()));
        var orbit = groups.Length <= 1 ? 0 : (largestRadius + 100) / Math.Sin(Math.PI / groups.Length);
        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex];
            var angle = 2 * Math.PI * groupIndex / groups.Length;
            var center = new Point(Math.Cos(angle) * orbit, Math.Sin(angle) * orbit);
            var index = 0;
            foreach (var node in group.OrderBy(n => n.GlobalId, StringComparer.Ordinal))
            {
                _nodes[node.GlobalId] = node;
                _colors[node.GlobalId] = new SolidColorBrush(Color.Parse(SessionGraphRenderer.NodeBrightness.ColorFor(node.SessionId, node.SessionColor)));
                if (!_positions.ContainsKey(node.GlobalId))
                {
                    var radius = 30 * Math.Sqrt(index);
                    var theta = index * 2.399963229728653;
                    _positions[node.GlobalId] = node.HasStoredPosition && double.IsFinite(node.X) && double.IsFinite(node.Y)
                        ? new Point(node.X, node.Y)
                        : center + new Vector(Math.Cos(theta) * radius, Math.Sin(theta) * radius);
                }
                index++;
            }
        }
        _needsFit = true;
        InvalidateVisual();
    }

    public void SetPendingLink(string? id)
    {
        _pending = id;
        InvalidateVisual();
    }

    public void SetSessionColor(string sessionId, string color)
    {
        foreach (var node in _graph.Nodes.Where(n => n.SessionId == sessionId))
            _colors[node.GlobalId] = new SolidColorBrush(Color.Parse(SessionGraphRenderer.NodeBrightness.ColorFor(sessionId, color)));
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

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _needsFit = true;
        InvalidateVisual();
    }

    private IBrush Brush(string key, IBrush fallback) => this.TryFindResource(key, out var value) && value is IBrush brush ? brush : fallback;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_needsFit) Fit();
        var rect = new Rect(Bounds.Size);
        context.FillRectangle(Brush("AppBackgroundAltBrush", Brushes.Black), rect);
        context.FillRectangle(Brush("GraphBackdropDotsBrush", Brushes.Transparent), rect);
        context.FillRectangle(Brush("GraphBackdropBloomBrush", Brushes.Transparent), rect);
        var ink = Brush("Ink90", Brushes.White);
        var edgePen = new Pen(Brush("Ink20", Brushes.Gray), 1);
        var manualPen = new Pen(Brush("Ink60", Brushes.Gray), 1.5);
        var highlightPen = new Pen(ink, 1.5);
        foreach (var edge in _graph.Edges)
        {
            if (!_positions.TryGetValue(edge.FromGlobalId, out var from) || !_positions.TryGetValue(edge.ToGlobalId, out var to)) continue;
            var pen = edge.FromGlobalId == _hovered || edge.ToGlobalId == _hovered ? highlightPen : edge.IsManual ? manualPen : edgePen;
            context.DrawLine(pen, ToScreen(from), ToScreen(to));
        }
        foreach (var node in _graph.Nodes)
        {
            var point = NodeScreenPosition(node.GlobalId);
            if (!rect.Inflate(12).Contains(point)) continue;
            var radius = node.GlobalId == _pending ? 8 : 5;
            context.DrawEllipse(_colors[node.GlobalId], null, point, radius, radius);
            if (node.GlobalId == _pending || node.GlobalId == _hovered || _rejected.Contains(node.GlobalId))
                context.DrawEllipse(null, new Pen(_rejected.Contains(node.GlobalId) ? Brush("DangerBrush", Brushes.Red) : ink, 1.5), point, radius + 4, radius + 4);
        }
        var font = this.TryFindResource("MonoFontFamily", out var resource) && resource is FontFamily family ? family : FontFamily.Default;
        foreach (var group in _graph.Nodes.GroupBy(n => n.SessionId))
        {
            var points = group.Select(n => NodeScreenPosition(n.GlobalId)).ToArray();
            var text = new FormattedText(group.First().SessionName, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(font), 11, ink) { MaxTextWidth = 180, Trimming = TextTrimming.CharacterEllipsis };
            var origin = new Point(points.Average(p => p.X) - text.Width / 2, points.Min(p => p.Y) - text.Height - 18);
            context.FillRectangle(Brush("GraphChipBackgroundBrush", Brushes.Black), new Rect(origin - new Vector(6, 3), new Size(text.Width + 12, text.Height + 6)));
            context.DrawText(text, origin);
        }
        // El título enfocado también se muestra sin depender del retardo del tooltip del sistema.
        if (_hovered is { } id && _nodes.TryGetValue(id, out var hovered))
        {
            var text = new FormattedText(hovered.Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(font), 12, ink);
            var point = NodeScreenPosition(id);
            var origin = new Point(Math.Clamp(point.X + 14, 4, Math.Max(4, Bounds.Width - text.Width - 8)), Math.Max(4, point.Y - text.Height - 10));
            context.FillRectangle(Brush("GraphChipBackgroundBrush", Brushes.Black), new Rect(origin - new Vector(4, 2), new Size(text.Width + 8, text.Height + 4)));
            context.DrawText(text, origin);
        }
    }

    private void Fit()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        _needsFit = false;
        if (_positions.Count == 0) return;
        var minX = _positions.Values.Min(p => p.X);
        var maxX = _positions.Values.Max(p => p.X);
        var minY = _positions.Values.Min(p => p.Y);
        var maxY = _positions.Values.Max(p => p.Y);
        _scale = Math.Clamp(Math.Min(Math.Max(1, Bounds.Width - 180) / Math.Max(100, maxX - minX),
            Math.Max(1, Bounds.Height - 200) / Math.Max(100, maxY - minY)), 0.001, 4);
        _offset = new Vector(Bounds.Width / 2 - (minX + maxX) / 2 * _scale, Bounds.Height / 2 - (minY + maxY) / 2 * _scale);
    }

    private Point ToScreen(Point world) => new(world.X * _scale + _offset.X, world.Y * _scale + _offset.Y);
    private Point ToWorld(Point screen) => new((screen.X - _offset.X) / _scale, (screen.Y - _offset.Y) / _scale);

    private static double DistanceSquared(Point a, Point b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

    private string? HitNode(Point point)
    {
        string? hit = null;
        var closest = 14d * 14;
        foreach (var (id, position) in _positions)
        {
            var distance = DistanceSquared(ToScreen(position), point);
            if (distance > closest) continue;
            closest = distance;
            hit = id;
        }
        return hit;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        if (_needsFit) Fit();
        _pressPoint = _lastPoint = e.GetPosition(this);
        _pressedNode = HitNode(_pressPoint);
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
            if (!_dragged && DistanceSquared(point, _pressPoint) > 16) _dragged = true;
            if (_dragged)
            {
                if (_pressedNode is { } id) _positions[id] = ToWorld(point);
                else _offset += point - _lastPoint;
                _lastPoint = point;
            }
        }
        else
        {
            var hovered = HitNode(point);
            if (hovered != _hovered)
            {
                _hovered = hovered;
                ToolTip.SetTip(this, hovered is not null ? $"{_nodes[hovered].Title}\n{_nodes[hovered].SessionName}" : null);
            }
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pointer is null || e.InitialPressMouseButton != MouseButton.Left) return;
        var id = _pressedNode;
        var dragged = _dragged;
        var clicks = _clickCount;
        e.Pointer.Capture(null);
        if (id is not null)
        {
            if (dragged)
            {
                // Guarda todas las posiciones visibles: los vecinos mantienen su lugar al recargar.
                PositionsChanged?.Invoke(_positions.Select(p => (p.Key, p.Value.X, p.Value.Y, 0d)).ToArray());
            }
            else if (HitNode(e.GetPosition(this)) == id)
            {
                if (clicks % 2 == 0) NodeDoubleClicked?.Invoke(id);
                else NodeClicked?.Invoke(id);
            }
        }
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pointer = null;
        _pressedNode = null;
        _dragged = false;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = null;
        ToolTip.SetTip(this, null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_needsFit) Fit();
        var point = e.GetPosition(this);
        var world = ToWorld(point);
        _scale = Math.Clamp(_scale * Math.Pow(1.15, e.Delta.Y), 0.001, 12);
        _offset = new Vector(point.X - world.X * _scale, point.Y - world.Y * _scale);
        InvalidateVisual();
        e.Handled = true;
    }
}
