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
            var points = group.Points;
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

    private sealed record Group(SessionGraphNode[] Nodes, Point[] Points, double Radius);

    private static Group[] CreateGroups(SessionGraph graph) => graph.Nodes.GroupBy(n => n.SessionId)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g =>
        {
            // Una distribución de ángulo áureo evita las filas de una rejilla y mantiene una
            // densidad uniforme. El orden estable impide saltos al editar enlaces o reordenar notas.
            var nodes = g.OrderBy(n => n.GlobalId, StringComparer.Ordinal).ToArray();
            var points = Enumerable.Range(0, nodes.Length).Select(i =>
            {
                var angle = i * Math.PI * (3 - Math.Sqrt(5));
                var radius = 28 * Math.Sqrt(i);
                return new Point(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
            }).ToArray();
            var center = new Point(points.Average(p => p.X), points.Average(p => p.Y));
            var radius = points.Max(p => ((Vector)(p - center)).Length) + 70;
            return new Group(nodes, points, radius);
        }).ToArray();

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
