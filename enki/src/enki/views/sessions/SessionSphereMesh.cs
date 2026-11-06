using System;
using System.Collections.Generic;
using System.Linq;

namespace enki.Views.Sessions;

/// <summary>Malla decorativa de una esfera: une cada nota con sus vecinas espaciales más próximas en 3D y
/// añade solo los puentes imprescindibles para que la sesión quede conectada. No son enlaces: no se guardan,
/// no cuentan en los resúmenes y se pueden ocultar con la opción «Malla de sesión». Se calcula al cambiar las
/// notas visibles, en espacio local, así que la rotación no la altera.</summary>
internal static class SessionSphereMesh
{
    public static IReadOnlyList<(int From, int To)> Create(IReadOnlyList<Point3> points)
    {
        var n = points.Count;
        if (n < 2) return [];
        if (n == 2) return [(0, 1)];

        var neighbors = Math.Min(SessionSphereParameters.MeshNeighbors, n - 1);
        var candidates = new HashSet<(int, int)>();
        var nearest = new List<double>(n);
        var best = new int[neighbors];
        var bestDistance = new double[neighbors];
        for (var i = 0; i < n; i++)
        {
            // Una sesión llega como mucho a unos miles de notas: el barrido lineal con una selección parcial de
            // los k menores es exacto, determinista y O(n²) sin ordenar, y solo corre al cambiar las notas.
            Array.Fill(best, -1);
            Array.Fill(bestDistance, double.PositiveInfinity);
            for (var j = 0; j < n; j++)
            {
                if (j == i) continue;
                var distance = (points[i] - points[j]).SquaredLength;
                if (distance >= bestDistance[neighbors - 1]) continue;
                var slot = neighbors - 1;
                while (slot > 0 && bestDistance[slot - 1] > distance)
                {
                    bestDistance[slot] = bestDistance[slot - 1];
                    best[slot] = best[slot - 1];
                    slot--;
                }
                bestDistance[slot] = distance;
                best[slot] = j;
            }
            foreach (var j in best) candidates.Add(i < j ? (i, j) : (j, i));
            nearest.Add(Math.Sqrt(bestDistance[0]));
        }

        nearest.Sort();
        var maxLength = Math.Max(1e-6, nearest[nearest.Count / 2] * 2.5);
        var parent = Enumerable.Range(0, n).ToArray();
        var result = new List<(int From, int To)>();
        foreach (var (from, to, length) in candidates
                     .Select(e => (e.Item1, e.Item2, Length: (points[e.Item1] - points[e.Item2]).Length))
                     .OrderBy(e => e.Length).ThenBy(e => e.Item1).ThenBy(e => e.Item2))
        {
            var a = Root(from);
            var b = Root(to);
            if (length > maxLength && a == b) continue;
            result.Add((from, to));
            parent[a] = b;
        }

        // Componentes aisladas tras los k vecinos (grupos lejanos): se unen por su par más próximo.
        while (true)
        {
            var anchor = Root(0);
            var stray = Enumerable.Range(0, n).FirstOrDefault(i => Root(i) != anchor, -1);
            if (stray < 0) break;
            var component = Root(stray);
            var bridge = (From: -1, To: -1, Distance: double.PositiveInfinity);
            for (var i = 0; i < n; i++)
            {
                if (Root(i) != component) continue;
                for (var j = 0; j < n; j++)
                {
                    if (Root(j) == component) continue;
                    var distance = (points[i] - points[j]).SquaredLength;
                    if (distance < bridge.Distance) bridge = (i, j, distance);
                }
            }
            result.Add(bridge.From < bridge.To ? (bridge.From, bridge.To) : (bridge.To, bridge.From));
            parent[Root(bridge.From)] = Root(bridge.To);
        }

        return result.Select(e => e.From < e.To ? e : (From: e.To, To: e.From)).Distinct()
            .OrderBy(e => e.From).ThenBy(e => e.To).ToArray();

        int Root(int index)
        {
            while (parent[index] != index) index = parent[index] = parent[parent[index]];
            return index;
        }
    }
}
