using System;
using System.Collections.Generic;
using System.Linq;

namespace enki.Views.Sessions;

/// <summary>Distribución volumétrica de las notas de una sesión dentro de su esfera. Determinista: la misma
/// sesión, notas y enlaces producen siempre las mismas coordenadas, sin depender del orden de entrada.
///
/// <para>Combina cuatro fases. (1) Comunidades por propagación de etiquetas sobre los enlaces reales de la
/// sesión. (2) Reparto radial híbrido: alrededor del <see cref="SessionSphereParameters.ShellFraction"/> de
/// las notas en una corteza con variación radial y el resto a distintas profundidades del interior, que
/// reciben preferentemente las notas más conectadas para que sus enlaces atraviesen el volumen. (3) Siembra
/// en una espiral de Fibonacci ordenada por comunidad, de modo que cada comunidad ocupe una región contigua.
/// (4) Refinado por fuerzas —repulsión de corto alcance, resortes en los enlaces, cohesión de comunidad y
/// restricción radial— con enfriamiento progresivo y una pasada final de separación mínima. Las fuerzas
/// solo se ejecutan aquí, una vez por cambio de notas: la rotación nunca vuelve a calcular el layout.</para></summary>
internal static class SessionSphereDistribution
{
    private const double RepulsionStrength = 0.35;
    private const double LinkStrength = 0.05;
    private const double CommunityStrength = 0.015;
    private const double RadialStrength = 0.22;

    /// <param name="seed">Identidad estable de la sesión; fija la variación orgánica de su forma.</param>
    /// <param name="ids">Identificadores de nota, ya ordenados de forma estable por el llamador.</param>
    /// <param name="edges">Enlaces reales entre índices de <paramref name="ids"/>; no se añade ninguno.</param>
    /// <param name="pinned">Notas con posición local ya conocida (arrastre del usuario o caché), que no se mueven.</param>
    public static Point3[] Create(string seed, IReadOnlyList<string> ids, IReadOnlyList<(int A, int B)> edges,
        double radius, IReadOnlyDictionary<string, Point3> pinned)
    {
        var n = ids.Count;
        var points = new Point3[n];
        var fixedNodes = new bool[n];
        for (var i = 0; i < n; i++)
            if (pinned.TryGetValue(ids[i], out var p))
            {
                points[i] = p.ClampLength(radius);
                fixedNodes[i] = true;
            }
        var freeCount = fixedNodes.Count(f => !f);
        if (freeCount == 0) return points;
        if (n == 1) return [new Point3(0, 0, 0)];

        var random = new StableRandom(seed);
        var adjacency = BuildAdjacency(n, edges);
        var degree = adjacency.Select(a => a.Count).ToArray();
        var hashRank = ids.Select(id => StableRandom.Hash(seed + "\u001f" + id)).ToArray();
        var community = DetectCommunities(adjacency);
        var averageDegree = degree.Sum() / (double)n;

        // Las notas más conectadas (los nudos de la red) pasan al interior; sin enlaces, el reparto es
        // pseudoaleatorio pero estable. Las fijadas conservan su profundidad actual.
        var interiorCount = (int)Math.Round(freeCount * (1 - ShellFractionFor(n, averageDegree)));
        var interior = new bool[n];
        foreach (var index in Enumerable.Range(0, n).Where(i => !fixedNodes[i])
                     .OrderByDescending(i => degree[i]).ThenBy(i => hashRank[i]).Take(interiorCount))
            interior[index] = true;

        var targetRadius = new double[n];
        for (var i = 0; i < n; i++)
            targetRadius[i] = fixedNodes[i] ? points[i].Length
                : interior[i] ? radius * Math.Max(SessionSphereParameters.CoreInnerRatio,
                    SessionSphereParameters.CoreOuterRatio * Math.Cbrt(random.Next()))
                : radius * (SessionSphereParameters.ShellInnerRatio
                    + (1 - SessionSphereParameters.ShellInnerRatio) * random.Next());

        SeedShell(points, fixedNodes, interior, targetRadius, community, degree, hashRank, random);
        SeedInterior(points, fixedNodes, interior, targetRadius, adjacency, random);

        var spacing = EffectiveSpacing(radius, n);
        var iterations = n < 60 ? 220 : n < 400 ? 160 : n < 1000 ? 110 : 80;
        Relax(points, fixedNodes, targetRadius, adjacency, community, radius, spacing, iterations);
        Separate(points, fixedNodes, radius, spacing * 0.85, passes: 6);

        return points;
    }

    /// <summary>Fracción de corteza para una sesión: todas las notas de una sesión diminuta forman la silueta
    /// (un punto interior no aporta profundidad legible), y las redes densas ceden algo más al interior.</summary>
    internal static double ShellFractionFor(int count, double averageDegree)
    {
        if (count <= 4) return 1;
        if (count < 12) return 0.85 - (count - 5) / 7.0 * 0.2;
        var density = Math.Clamp((averageDegree - 2) / 6, 0, 1);
        return SessionSphereParameters.ShellFraction - 0.1 * density;
    }

    /// <summary>Separación objetivo: la nominal, salvo que la corteza no tenga superficie suficiente.</summary>
    private static double EffectiveSpacing(double radius, int count)
    {
        var shellArea = 4 * Math.PI * radius * radius;
        return Math.Min(SessionSphereParameters.NodeSpacing,
            0.9 * Math.Sqrt(shellArea / Math.Max(1, count * SessionSphereParameters.ShellFraction)));
    }

    private static List<int>[] BuildAdjacency(int n, IReadOnlyList<(int A, int B)> edges)
    {
        var sets = Enumerable.Range(0, n).Select(_ => new SortedSet<int>()).ToArray();
        foreach (var (a, b) in edges)
        {
            if (a == b || a < 0 || b < 0 || a >= n || b >= n) continue;
            sets[a].Add(b);
            sets[b].Add(a);
        }
        return sets.Select(s => s.ToList()).ToArray();
    }

    /// <summary>Propagación de etiquetas síncrona en orden de índice; los empates eligen la etiqueta menor.</summary>
    private static int[] DetectCommunities(List<int>[] adjacency)
    {
        var label = Enumerable.Range(0, adjacency.Length).ToArray();
        var counts = new Dictionary<int, int>();
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var changed = false;
            for (var i = 0; i < adjacency.Length; i++)
            {
                if (adjacency[i].Count == 0) continue;
                counts.Clear();
                foreach (var neighbor in adjacency[i])
                    counts[label[neighbor]] = counts.GetValueOrDefault(label[neighbor]) + 1;
                var best = label[i];
                var bestCount = counts.GetValueOrDefault(best);
                foreach (var (candidate, count) in counts)
                    if (count > bestCount || (count == bestCount && candidate < best))
                    {
                        best = candidate;
                        bestCount = count;
                    }
                if (best == label[i]) continue;
                label[i] = best;
                changed = true;
            }
            if (!changed) break;
        }
        return label;
    }

    /// <summary>Siembra la corteza sobre una espiral de Fibonacci con ruido. Cada comunidad (de mayor a menor)
    /// reclama las direcciones libres más próximas a su propio ancla, de modo que ocupa un casquete contiguo
    /// de la esfera; las notas sin comunidad toman las direcciones restantes. Repartir por casquetes, y no por
    /// índices consecutivos de la espiral (que forman bandas de latitud), mantiene la silueta isótropa.</summary>
    private static void SeedShell(Point3[] points, bool[] fixedNodes, bool[] interior, double[] targetRadius,
        int[] community, int[] degree, uint[] hashRank, StableRandom random)
    {
        var shell = Enumerable.Range(0, points.Length).Where(i => !fixedNodes[i] && !interior[i]).ToArray();
        var count = shell.Length;
        if (count == 0) return;
        var twist = random.Next() * Math.Tau;
        var directions = FibonacciDirections(count, twist, random, jitter: true);
        var taken = new bool[count];
        var groups = shell.GroupBy(i => community[i])
            .Select(g => g.OrderByDescending(i => degree[i]).ThenBy(i => hashRank[i]).ToArray())
            .OrderByDescending(g => g.Length > 1 ? g.Length : 0).ThenBy(g => g.Length > 1 ? community[g[0]] : int.MaxValue)
            .ThenBy(g => hashRank[g[0]]).ToArray();
        var clustered = groups.Count(g => g.Length > 1);
        var anchors = FibonacciDirections(Math.Max(1, clustered), twist + 1.1, random, jitter: false);
        var next = 0;
        for (var c = 0; c < groups.Length; c++)
        {
            var members = groups[c];
            if (members.Length > 1)
            {
                var anchor = anchors[c];
                var nearest = Enumerable.Range(0, count).Where(k => !taken[k])
                    .OrderBy(k => (directions[k] - anchor).SquaredLength).ThenBy(k => k).Take(members.Length).ToArray();
                for (var m = 0; m < members.Length; m++)
                {
                    taken[nearest[m]] = true;
                    points[members[m]] = directions[nearest[m]] * targetRadius[members[m]];
                }
                continue;
            }
            while (taken[next]) next++;
            taken[next] = true;
            points[members[0]] = directions[next] * targetRadius[members[0]];
        }
    }

    private static Point3[] FibonacciDirections(int count, double twist, StableRandom random, bool jitter)
    {
        var golden = Math.PI * (3 - Math.Sqrt(5));
        var result = new Point3[count];
        for (var k = 0; k < count; k++)
        {
            var y = Math.Clamp(1 - 2 * (k + 0.5 + (jitter ? (random.Next() - 0.5) * 0.6 : 0)) / count, -0.999, 0.999);
            var ring = Math.Sqrt(1 - y * y);
            var theta = twist + k * golden + (jitter ? (random.Next() - 0.5) * 0.45 : 0);
            result[k] = new Point3(Math.Cos(theta) * ring, y, Math.Sin(theta) * ring);
        }
        return result;
    }

    /// <summary>Cada nota interior se siembra bajo sus vecinas ya colocadas, o en una dirección estable si no
    /// tiene ninguna; así un nudo de la red queda entre las notas que enlaza.</summary>
    private static void SeedInterior(Point3[] points, bool[] fixedNodes, bool[] interior, double[] targetRadius,
        List<int>[] adjacency, StableRandom random)
    {
        for (var i = 0; i < points.Length; i++)
        {
            if (fixedNodes[i] || !interior[i]) continue;
            var sum = new Point3(0, 0, 0);
            foreach (var neighbor in adjacency[i])
                if (!interior[neighbor] || fixedNodes[neighbor]) sum += points[neighbor];
            var direction = sum.Length > 1e-6 ? sum * (1 / sum.Length) : RandomDirection(random);
            // Ligera desviación para que dos nudos con las mismas vecinas no queden alineados.
            direction += RandomDirection(random) * 0.35;
            var length = direction.Length;
            points[i] = direction * (targetRadius[i] / Math.Max(length, 1e-6));
        }
    }

    private static Point3 RandomDirection(StableRandom random)
    {
        var y = 2 * random.Next() - 1;
        var theta = random.Next() * Math.Tau;
        var ring = Math.Sqrt(Math.Max(0, 1 - y * y));
        return new Point3(Math.Cos(theta) * ring, y, Math.Sin(theta) * ring);
    }

    private static void Relax(Point3[] points, bool[] fixedNodes, double[] targetRadius, List<int>[] adjacency,
        int[] community, double radius, double spacing, int iterations)
    {
        var n = points.Length;
        var displacement = new Point3[n];
        var nearby = new List<int>();
        var grid = new SpatialGrid(spacing * 2);
        var restLength = spacing * 1.8;
        var cutoff = spacing * 2;
        var communities = community.Select((c, i) => (c, i)).GroupBy(t => t.c)
            .Where(g => g.Count() > 1).Select(g => g.Select(t => t.i).ToArray()).ToArray();

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var alpha = 1 - iteration / (double)iterations;
            Array.Clear(displacement);
            grid.Rebuild(points);

            for (var i = 0; i < n; i++)
            {
                grid.CollectNeighbors(points[i], nearby);
                foreach (var j in nearby)
                {
                    if (j <= i) continue;
                    var delta = points[i] - points[j];
                    var distanceSquared = delta.SquaredLength;
                    if (distanceSquared >= cutoff * cutoff) continue;
                    var distance = Math.Sqrt(distanceSquared);
                    var direction = distance > 1e-9 ? delta * (1 / distance) : Jitter(i, j);
                    var push = direction * ((cutoff - distance) * 0.5 * RepulsionStrength);
                    displacement[i] += push;
                    displacement[j] -= push;
                }
            }

            for (var i = 0; i < n; i++)
                foreach (var j in adjacency[i])
                {
                    if (j <= i) continue;
                    var delta = points[j] - points[i];
                    var distance = delta.Length;
                    if (distance < 1e-9) continue;
                    var pull = delta * ((distance - restLength) / distance * 0.5 * LinkStrength);
                    displacement[i] += pull;
                    displacement[j] -= pull;
                }

            foreach (var members in communities)
            {
                var center = new Point3(0, 0, 0);
                foreach (var m in members) center += points[m];
                center *= 1.0 / members.Length;
                foreach (var m in members) displacement[m] += (center - points[m]) * CommunityStrength;
            }

            var maxStep = spacing * 0.6 * Math.Max(alpha, 0.15);
            for (var i = 0; i < n; i++)
            {
                if (fixedNodes[i]) continue;
                var length = points[i].Length;
                if (length > 1e-9) displacement[i] += points[i] * ((targetRadius[i] - length) / length * RadialStrength);
                points[i] = (points[i] + displacement[i].ClampLength(maxStep)).ClampLength(radius);
            }
        }
    }

    /// <summary>Resuelve solapes residuales moviendo solo notas libres; acota cada empuje al radio.</summary>
    private static void Separate(Point3[] points, bool[] fixedNodes, double radius, double minimum, int passes)
    {
        var grid = new SpatialGrid(minimum);
        var nearby = new List<int>();
        for (var pass = 0; pass < passes; pass++)
        {
            grid.Rebuild(points);
            var moved = false;
            for (var i = 0; i < points.Length; i++)
            {
                grid.CollectNeighbors(points[i], nearby);
                foreach (var j in nearby)
                {
                    if (j <= i) continue;
                    var delta = points[i] - points[j];
                    var distance = delta.Length;
                    if (distance >= minimum) continue;
                    var direction = distance > 1e-9 ? delta * (1 / distance) : Jitter(i, j);
                    var overlap = minimum - distance;
                    var share = fixedNodes[i] || fixedNodes[j] ? 1.0 : 0.5;
                    if (!fixedNodes[i]) points[i] = (points[i] + direction * (overlap * share)).ClampLength(radius);
                    if (!fixedNodes[j]) points[j] = (points[j] - direction * (overlap * share)).ClampLength(radius);
                    moved = true;
                }
            }
            if (!moved) break;
        }
    }

    /// <summary>Dirección determinista para separar dos notas coincidentes.</summary>
    private static Point3 Jitter(int i, int j)
    {
        var angle = (i * 92821 + j * 68917) % 360 * Math.PI / 180;
        return new Point3(Math.Cos(angle), Math.Sin(angle * 0.5) * 0.5, Math.Sin(angle));
    }

    /// <summary>Rejilla uniforme para consultas de vecindad en O(n): índices ordenados por celda y una tabla
    /// de inicio por celda, reutilizada entre iteraciones sin reservar memoria nueva.</summary>
    private sealed class SpatialGrid(double cellSize)
    {
        private readonly Dictionary<long, (int Start, int Count)> _cells = new();
        private long[] _keys = [];
        private int[] _order = [];
        private long[] _sorted = [];

        public void Rebuild(Point3[] points)
        {
            if (_keys.Length != points.Length)
            {
                _keys = new long[points.Length];
                _order = new int[points.Length];
            }
            for (var i = 0; i < points.Length; i++)
            {
                _keys[i] = Key(Cell(points[i].X), Cell(points[i].Y), Cell(points[i].Z));
                _order[i] = i;
            }
            if (_sorted.Length != _keys.Length) _sorted = new long[_keys.Length];
            Array.Copy(_keys, _sorted, _keys.Length);
            var sortedKeys = _sorted;
            Array.Sort(sortedKeys, _order);
            _cells.Clear();
            for (var i = 0; i < sortedKeys.Length; i++)
            {
                if (i > 0 && sortedKeys[i] == sortedKeys[i - 1]) continue;
                var end = i;
                while (end < sortedKeys.Length && sortedKeys[end] == sortedKeys[i]) end++;
                _cells[sortedKeys[i]] = (i, end - i);
            }
        }

        /// <summary>Índices de las notas en las 27 celdas alrededor de <paramref name="point"/>.</summary>
        public void CollectNeighbors(Point3 point, List<int> into)
        {
            into.Clear();
            int cx = Cell(point.X), cy = Cell(point.Y), cz = Cell(point.Z);
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (!_cells.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out var range)) continue;
                for (var k = range.Start; k < range.Start + range.Count; k++) into.Add(_order[k]);
            }
        }

        private int Cell(double value) => (int)Math.Floor(value / cellSize);

        private static long Key(int x, int y, int z) =>
            ((long)(x + 1_048_576) << 42) | ((long)(y + 1_048_576) << 21) | (long)(z + 1_048_576);
    }
}

/// <summary>Generador xorshift sembrado con FNV-1a: estable entre ejecuciones y plataformas, a diferencia de
/// <see cref="string.GetHashCode()"/> o <see cref="Random"/> sin semilla.</summary>
internal sealed class StableRandom(string seed)
{
    private uint _state = Math.Max(1u, Hash(seed));

    public double Next()
    {
        _state ^= _state << 13;
        _state ^= _state >> 17;
        _state ^= _state << 5;
        return _state / (double)uint.MaxValue;
    }

    public static uint Hash(string value)
    {
        uint hash = 2166136261;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16777619;
        }
        return hash;
    }
}
