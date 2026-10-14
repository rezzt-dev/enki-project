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
            var points = group.Nodes.Select(n => positions[n.GlobalId]).ToArray();
            var edges = NativeSessionGraphMesh.Create(points)
                .Select(edge => (edge.From, edge.To, Length: ((Vector)(points[edge.From] - points[edge.To])).Length))
                .OrderBy(edge => edge.Length).ToArray();
            var parent = Enumerable.Range(0, points.Length).ToArray();
            int Root(int index)
            {
                while (parent[index] != index) index = parent[index] = parent[parent[index]];
                return index;
            }
            // Mantiene las conexiones cercanas y añade solo los puentes largos imprescindibles.
            foreach (var (from, to, length) in edges)
            {
                var a = Root(from);
                var b = Root(to);
                if (length > 150 && a == b) continue;
                pairs.Add((group.Nodes[from].GlobalId, group.Nodes[to].GlobalId));
                parent[a] = b;
            }
        }
        return pairs;
    }

    private sealed record Group(SessionGraphNode[] Nodes, Point[] Points, double Radius);

    private static Group[] CreateGroups(SessionGraph graph) => graph.Nodes.GroupBy(n => n.SessionId)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g =>
        {
            var nodes = g.OrderBy(n => n.GlobalId, StringComparer.Ordinal).ToArray();
            var points = CreateOrganicPoints(g.Key, nodes.Length);
            var center = new Point(points.Average(p => p.X), points.Average(p => p.Y));
            var radius = points.Max(p => ((Vector)(p - center)).Length) + 70;
            return new Group(nodes, points, radius);
        }).ToArray();

    /// <summary>Crea una silueta distinta para cada sesión sin que cambie entre recargas.
    /// La deformación suave produce lóbulos; la separación mínima evita nodos superpuestos.</summary>
    private static Point[] CreateOrganicPoints(string sessionId, int count)
    {
        var points = new Point[count];
        var cells = new Dictionary<(int X, int Y), List<Point>>();
        var random = StableSeed(sessionId);
        var rotation = Next() * Math.Tau;
        var phaseA = Next() * Math.Tau;
        var phaseB = Next() * Math.Tau;
        var stretch = 0.88 + Next() * 0.24;
        var lobes = 3 + (int)(Next() * 3);
        const double spacing = 27;
        for (var i = 0; i < count; i++)
        {
            var angle = i * Math.PI * (3 - Math.Sqrt(5)) + (Next() - 0.5) * 0.34;
            var wave = 1 + 0.15 * Math.Sin(lobes * angle + phaseA)
                + 0.07 * Math.Sin((lobes + 2) * angle + phaseB);
            var radius = i == 0 ? 0 : 30 * Math.Sqrt(i) * wave;
            Point point;
            var attempt = 0;
            do
            {
                var candidateAngle = angle + attempt * 0.14;
                var candidateRadius = radius + attempt * 2.5;
                var x = Math.Cos(candidateAngle) * candidateRadius * stretch;
                var y = Math.Sin(candidateAngle) * candidateRadius / stretch;
                point = new Point(x * Math.Cos(rotation) - y * Math.Sin(rotation),
                    x * Math.Sin(rotation) + y * Math.Cos(rotation));
                attempt++;
            } while (TooClose(point));

            points[i] = point;
            var cell = Cell(point);
            if (!cells.TryGetValue(cell, out var bucket)) cells[cell] = bucket = [];
            bucket.Add(point);
        }
        return points;

        bool TooClose(Point point)
        {
            var cell = Cell(point);
            for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++)
                if (cells.TryGetValue((cell.X + x, cell.Y + y), out var neighbors) &&
                    neighbors.Any(other => ((Vector)(other - point)).SquaredLength < spacing * spacing)) return true;
            return false;
        }

        static (int X, int Y) Cell(Point point) =>
            ((int)Math.Floor(point.X / spacing), (int)Math.Floor(point.Y / spacing));

        double Next()
        {
            random ^= random << 13;
            random ^= random >> 17;
            random ^= random << 5;
            return random / (double)uint.MaxValue;
        }
    }

    private static uint StableSeed(string value)
    {
        uint hash = 2166136261;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16777619;
        }
        return hash == 0 ? 1u : hash;
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
