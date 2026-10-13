using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using enki.Models;

namespace enki.Views.Sessions;

/// <summary>Distribución determinista de sesiones para el grafo nativo. Recupera coordenadas
/// degeneradas o mezcladas sin modificar los manifiestos ni ejecutar física en el hilo de UI.</summary>
internal static class NativeSessionGraphLayout
{
    public static Dictionary<string, Point> Create(SessionGraph graph)
    {
        var stored = graph.Nodes.Where(n => n.HasStoredPosition && double.IsFinite(n.X) && double.IsFinite(n.Y))
            .ToDictionary(n => n.GlobalId, n => new Point(n.X, n.Y), StringComparer.Ordinal);
        if (stored.Count == graph.Nodes.Count && IsReadable(graph, stored)) return stored;

        var result = new Dictionary<string, Point>(StringComparer.Ordinal);
        var groups = CreateGroups(graph);
        if (groups.Length == 0) return result;

        // Cada grupo reserva un sector proporcional a su tamaño. La búsqueda del radio impide
        // solapes incluso cuando una sesión contiene muchas más notas que las demás.
        var lower = groups.Max(g => g.Radius);
        var upper = groups.Sum(g => g.Radius);
        for (var iteration = 0; iteration < 48; iteration++)
        {
            var candidate = (lower + upper) / 2;
            if (groups.Sum(g => 2 * Math.Asin(g.Radius / candidate)) > Math.Tau) lower = candidate;
            else upper = candidate;
        }
        var ringRadius = groups.Length == 1 ? 0 : upper;
        var angle = -Math.PI / 2;
        var previousHalfAngle = 0d;
        foreach (var group in groups)
        {
            var halfAngle = ringRadius == 0 ? 0 : Math.Asin(group.Radius / ringRadius);
            angle += previousHalfAngle + halfAngle;
            var center = new Point(Math.Cos(angle) * ringRadius, Math.Sin(angle) * ringRadius);
            var points = group.Cells.Select(ToPoint).ToArray();
            var mean = new Vector(points.Average(p => p.X), points.Average(p => p.Y));
            for (var i = 0; i < group.Nodes.Length; i++)
                result[group.Nodes[i].GlobalId] = center + (Vector)points[i] - mean;
            previousHalfAngle = halfAngle;
        }
        return result;
    }

    /// <summary>Malla visual de vecinos espaciales, sin añadir enlaces al modelo ni al vault.
    /// Se calcula una vez por cambio de nodos, nunca durante el repintado o el zoom.</summary>
    public static IReadOnlyList<(string From, string To)> CreateMesh(SessionGraph graph, IReadOnlyDictionary<string, Point> positions)
    {
        var pairs = new List<(string From, string To)>();
        var groups = CreateGroups(graph);
        foreach (var group in groups)
        {
            // La triangulación usa las posiciones visibles: sigue siendo una malla de vecinos
            // después de filtrar notas o recuperar una distribución manual anterior.
            foreach (var (from, to) in NativeSessionGraphMesh.Create(group.Nodes.Select(n => positions[n.GlobalId]).ToArray()))
                pairs.Add((group.Nodes[from].GlobalId, group.Nodes[to].GlobalId));
        }
        return pairs;
    }

    private const double Spacing = 38;
    private static readonly (int Q, int R)[] Directions = [(1, 0), (0, 1), (-1, 1), (-1, 0), (0, -1), (1, -1)];
    private sealed record Group(SessionGraphNode[] Nodes, (int Q, int R)[] Cells, double Radius);

    private static Group[] CreateGroups(SessionGraph graph) => graph.Nodes.GroupBy(n => n.SessionId)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g =>
        {
            // El orden no depende de los enlaces: añadir un wikilink no mueve la nota ni cambia la malla.
            var nodes = g.OrderBy(n => n.GlobalId, StringComparer.Ordinal).ToArray();
            var cells = HexCells(nodes.Length);
            var points = cells.Select(ToPoint).ToArray();
            var center = new Point(points.Average(p => p.X), points.Average(p => p.Y));
            // El margen incluye el nombre de sesión y la separación entre grupos.
            var radius = points.Max(p => ((Vector)(p - center)).Length) + 90;
            return new Group(nodes, cells, radius);
        }).ToArray();

    private static Point ToPoint((int Q, int R) cell) =>
        new(Spacing * (cell.Q + cell.R / 2d), Spacing * Math.Sqrt(3) / 2 * cell.R);

    private static (int Q, int R)[] HexCells(int count)
    {
        var result = new List<(int Q, int R)> { (0, 0) };
        for (var radius = 1; result.Count < count; radius++)
        {
            var ring = new List<(int Q, int R)>();
            var q = 0;
            var r = -radius;
            foreach (var direction in Directions)
            for (var step = 0; step < radius; step++)
            {
                ring.Add((q, r));
                q += direction.Q;
                r += direction.R;
            }
            // Reparte también un anillo incompleto por todo el perímetro, evitando colas laterales.
            var take = Math.Min(ring.Count, count - result.Count);
            for (var i = 0; i < take; i++) result.Add(ring[i * ring.Count / take]);
        }
        return result.ToArray();
    }

    private static bool IsReadable(SessionGraph graph, Dictionary<string, Point> positions)
    {
        // Índice espacial: detecta notas apiladas sin comparar cada par del vault.
        const double spacing = 24;
        var cells = new Dictionary<(double X, double Y), List<Point>>();
        foreach (var point in positions.Values)
        {
            var cell = (X: Math.Floor(point.X / spacing), Y: Math.Floor(point.Y / spacing));
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (cells.TryGetValue((cell.X + dx, cell.Y + dy), out var neighbors) &&
                    neighbors.Any(p => ((p.X - point.X) * (p.X - point.X) + (p.Y - point.Y) * (p.Y - point.Y)) < spacing * spacing)) return false;
            if (!cells.TryGetValue(cell, out var bucket)) cells[cell] = bucket = [];
            bucket.Add(point);
        }
        var bounds = graph.Nodes.GroupBy(n => n.SessionId).Select(g =>
        {
            var points = g.Select(n => positions[n.GlobalId]).ToArray();
            return new Rect(new Point(points.Min(p => p.X) - 20, points.Min(p => p.Y) - 48),
                new Point(points.Max(p => p.X) + 20, points.Max(p => p.Y) + 20));
        }).OrderBy(r => r.Left).ToArray();
        for (var i = 0; i < bounds.Length; i++)
        for (var j = i + 1; j < bounds.Length && bounds[j].Left < bounds[i].Right; j++)
            if (bounds[i].Intersects(bounds[j])) return false;
        return true;
    }
}
