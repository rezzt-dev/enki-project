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
    private IReadOnlyList<(string From, string To)> _mesh = [];
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

    private readonly List<Rect> _labelBounds = [];
    internal IReadOnlyList<Rect> LabelBounds => _labelBounds;

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
        var membershipChanged = graph.Nodes.Count != _positions.Count || graph.Nodes.Any(n => !_positions.ContainsKey(n.GlobalId));
        var hasNewNodes = graph.Nodes.Any(n => !_positions.ContainsKey(n.GlobalId));
        // No mezcla coordenadas de dos distribuciones al añadir notas o ampliar un filtro.
        if (reset || hasNewNodes) _positions.Clear();
        _graph = graph;
        _nodes.Clear();
        _colors.Clear();
        var visible = graph.Nodes.Select(n => n.GlobalId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _positions.Keys.Where(id => !visible.Contains(id)).ToArray())
            _positions.Remove(id);
        if (_pending is not null && !visible.Contains(_pending)) _pending = null;

        var layout = NativeSessionGraphLayout.Create(graph);
        foreach (var node in graph.Nodes)
        {
            _nodes[node.GlobalId] = node;
            _colors[node.GlobalId] = new SolidColorBrush(Color.Parse(SessionGraphRenderer.NodeBrightness.ColorFor(node.SessionId, node.SessionColor)));
            _positions.TryAdd(node.GlobalId, layout[node.GlobalId]);
        }
        _mesh = NativeSessionGraphLayout.CreateMesh(graph, _positions);
        _needsFit |= reset || membershipChanged;
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
        var meshPen = new Pen(Brush("Ink20", Brushes.DimGray), 0.65);
        var edgePen = new Pen(Brush("Ink60", Brushes.Gray), 1.6);
        var manualPen = new Pen(Brush("Ink80", Brushes.Gray), 1.9);
        var highlightPen = new Pen(ink, 2.4);
        foreach (var (from, to) in _mesh)
            context.DrawLine(meshPen, ToScreen(_positions[from]), ToScreen(_positions[to]));
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
            var radius = Math.Clamp(7 * _scale, 0.75, 5) * (node.GlobalId == _pending ? 1.6 : 1);
            context.DrawEllipse(_colors[node.GlobalId], null, point, radius, radius);
            if (node.GlobalId == _pending || node.GlobalId == _hovered || _rejected.Contains(node.GlobalId))
                context.DrawEllipse(null, new Pen(_rejected.Contains(node.GlobalId) ? Brush("DangerBrush", Brushes.Red) : ink, 1.5), point, radius + 4, radius + 4);
        }
        var font = this.TryFindResource("MonoFontFamily", out var resource) && resource is FontFamily family ? family : FontFamily.Default;
        _labelBounds.Clear();
        var occupied = new List<Rect>();
        var nodeBounds = _graph.Nodes.Select(n => new Rect(NodeScreenPosition(n.GlobalId) - new Vector(9, 9), new Size(18, 18))).ToArray();
        // Reserva primero la ficha enfocada para que ninguna etiqueta la tape.
        FormattedText? detail = null;
        Rect detailBounds = default;
        if (_hovered is { } id && _nodes.TryGetValue(id, out var hovered) && rect.Contains(NodeScreenPosition(id)) && Bounds.Width > 24 && Bounds.Height > 24)
        {
            detail = new FormattedText($"{hovered.Title}\n{hovered.SessionName}", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface(font), 12, ink)
            {
                MaxTextWidth = Math.Max(1, Math.Min(320, Bounds.Width - 32)),
                MaxTextHeight = Math.Max(1, Math.Min(100, Bounds.Height - 32)),
                Trimming = TextTrimming.CharacterEllipsis
            };
            var point = NodeScreenPosition(id);
            var size = new Size(detail.Width + 16, detail.Height + 12);
            var top = point.Y + 16;
            if (top + size.Height > Bounds.Height - 8) top = point.Y - size.Height - 16;
            detailBounds = new Rect(new Point(Math.Clamp(point.X + 16, 8, Math.Max(8, Bounds.Width - size.Width - 8)),
                Math.Clamp(top, 8, Math.Max(8, Bounds.Height - size.Height - 8))), size);
            occupied.Add(detailBounds.Inflate(4));
        }
        foreach (var group in _graph.Nodes.GroupBy(n => n.SessionId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var points = group.Select(n => NodeScreenPosition(n.GlobalId)).Where(rect.Contains).ToArray();
            if (points.Length == 0 || Bounds.Width < 40) continue;
            var text = new FormattedText(group.First().SessionName, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(font), 11, ink)
            {
                MaxTextWidth = Math.Max(1, Math.Min(180, Bounds.Width - 28)),
                MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis
            };
            var size = new Size(text.Width + 12, text.Height + 6);
            var left = Math.Clamp(points.Average(p => p.X) - size.Width / 2, 8, Math.Max(8, Bounds.Width - size.Width - 8));
            // Solo posiciones cercanas al grupo: si no caben, el zoom o la ficha permiten identificarlo.
            foreach (var top in new[] { points.Min(p => p.Y) - size.Height - 14, points.Max(p => p.Y) + 14 })
            {
                var label = new Rect(new Point(left, top), size);
                if (!rect.Contains(label) || occupied.Any(r => r.Intersects(label.Inflate(4))) || nodeBounds.Any(r => r.Intersects(label))) continue;
                occupied.Add(label);
                _labelBounds.Add(label);
                context.FillRectangle(Brush("GraphChipBackgroundBrush", Brushes.Black), label);
                context.DrawText(text, label.TopLeft + new Vector(6, 3));
                break;
            }
        }
        if (detail is not null)
        {
            context.FillRectangle(Brush("GraphChipBackgroundBrush", Brushes.Black), detailBounds);
            context.DrawRectangle(new Pen(Brush("Ink20", Brushes.Gray), 1), detailBounds);
            context.DrawText(detail, detailBounds.TopLeft + new Vector(8, 6));
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
        _scale = Math.Clamp(Math.Min(Math.Max(1, Bounds.Width - 80) / Math.Max(100, maxX - minX),
            Math.Max(1, Bounds.Height - 140) / Math.Max(100, maxY - minY)), 0.001, 4);
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
            _hovered = HitNode(point);
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
