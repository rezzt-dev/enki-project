using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using enki.Models;

namespace enki.Views.Sessions;

/// <summary>Esfera de una sesión en la escena: centro en el plano del visor, radio y fase inicial de giro.</summary>
internal sealed record SessionSphere(string SessionId, Point Center, double Radius, double Phase);

/// <summary>Escena 3D lista para pintar: una esfera por sesión visible, la posición local (relativa al centro
/// de su esfera y sin rotar) de cada nota visible y la malla decorativa. Ambos renderizadores la consumen tal
/// cual; ninguno vuelve a calcular el layout.</summary>
internal sealed class SessionSphereScene(
    IReadOnlyDictionary<string, SessionSphere> spheres,
    IReadOnlyDictionary<string, Point3> local,
    IReadOnlyList<(string From, string To)> mesh)
{
    public IReadOnlyDictionary<string, SessionSphere> Spheres { get; } = spheres;
    public IReadOnlyDictionary<string, Point3> Local { get; } = local;
    public IReadOnlyList<(string From, string To)> Mesh { get; } = mesh;
}

/// <summary>Construye la escena de esferas y conserva su geometría entre actualizaciones. Filtrar, editar
/// enlaces, recolorear o recargar el documento web reutiliza la forma de cada sesión y la posición de cada
/// esfera; solo una nota nueva hace recalcular su sesión, con el resto de notas fijadas donde estaban. No
/// modifica el <see cref="SessionGraph"/> recibido ni inventa notas ni enlaces.
///
/// <para>Las posiciones guardadas en el manifiesto se interpretan como coordenadas locales normalizadas al
/// radio de su esfera (módulo ≤ 1). Los valores de layouts anteriores —coordenadas 2D con Z = 0 o de mundo—
/// quedan fuera de esa forma y se ignoran sin tocar el archivo hasta el próximo arrastre.</para></summary>
internal sealed class SessionSphereLayout
{
    private sealed class Volume(double radius, Dictionary<string, Point3> local)
    {
        public double Radius { get; } = radius;
        public Dictionary<string, Point3> Local { get; } = local;

        /// <summary>Malla del último subconjunto visible; se reutiliza mientras no cambien ni ese subconjunto
        /// ni las posiciones (filtrar por texto la pediría en cada pulsación).</summary>
        public (string Key, IReadOnlyList<(int From, int To)> Edges)? Mesh { get; set; }
    }

    private readonly Dictionary<string, Volume> _volumes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Point> _centers = new(StringComparer.Ordinal);
    private string? _placementKey;

    public SessionSphereScene Build(SessionGraph graph)
    {
        var groups = graph.Nodes.GroupBy(n => n.SessionId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.GlobalId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var intraEdges = graph.Edges.Where(e => e.FromSessionId == e.ToSessionId)
            .GroupBy(e => e.FromSessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => (e.FromGlobalId, e.ToGlobalId)).ToArray(), StringComparer.Ordinal);

        foreach (var (sessionId, nodes) in groups)
        {
            _volumes.TryGetValue(sessionId, out var previous);
            if (previous is not null && nodes.All(n => previous.Local.ContainsKey(n.GlobalId))) continue;
            _volumes[sessionId] = CreateVolume(sessionId, nodes, intraEdges.GetValueOrDefault(sessionId) ?? [], previous);
        }
        Place();

        var local = new Dictionary<string, Point3>(StringComparer.Ordinal);
        var spheres = new Dictionary<string, SessionSphere>(StringComparer.Ordinal);
        var mesh = new List<(string From, string To)>();
        foreach (var (sessionId, nodes) in groups)
        {
            var volume = _volumes[sessionId];
            spheres[sessionId] = new SessionSphere(sessionId, _centers[sessionId], volume.Radius, Phase(sessionId));
            var points = nodes.Select(n => volume.Local[n.GlobalId]).ToArray();
            for (var i = 0; i < nodes.Length; i++) local[nodes[i].GlobalId] = points[i];
            var key = string.Join('\u001f', nodes.Select(n => n.GlobalId));
            if (volume.Mesh is not { } cached || cached.Key != key)
                volume.Mesh = cached = (key, SessionSphereMesh.Create(points));
            foreach (var (from, to) in cached.Edges)
                mesh.Add((nodes[from].GlobalId, nodes[to].GlobalId));
        }
        return new SessionSphereScene(spheres, local, mesh);
    }

    /// <summary>Registra una nota desplazada a mano para que las siguientes escenas la conserven. Devuelve la
    /// posición ya acotada al volumen de su esfera.</summary>
    public Point3 Move(string sessionId, string globalId, Point3 local)
    {
        if (!_volumes.TryGetValue(sessionId, out var volume)) return local;
        var clamped = local.ClampLength(volume.Radius);
        volume.Local[globalId] = clamped;
        volume.Mesh = null;
        return clamped;
    }

    /// <summary>Radio de la esfera de <paramref name="sessionId"/>; sirve para normalizar lo que se guarda.</summary>
    public double RadiusOf(string sessionId) => _volumes.TryGetValue(sessionId, out var volume) ? volume.Radius : 0;

    /// <summary>Olvida las sesiones que ya no existen para que no reserven hueco en el anillo. Solo debe
    /// llamarse con el conjunto completo de sesiones (sin filtros), nunca con un subconjunto visible.</summary>
    public void Retain(IReadOnlySet<string> sessionIds)
    {
        foreach (var sessionId in _volumes.Keys.Where(id => !sessionIds.Contains(id)).ToArray())
        {
            _volumes.Remove(sessionId);
            _centers.Remove(sessionId);
        }
    }

    /// <summary>Lee la posición guardada de una nota si tiene la forma normalizada del visor de esferas.</summary>
    public static bool TryReadStoredPosition(SessionGraphNode node, out Point3 unit)
    {
        unit = new Point3(node.X, node.Y, node.Z);
        return node.HasStoredPosition && double.IsFinite(node.X) && double.IsFinite(node.Y) && double.IsFinite(node.Z)
            && Math.Abs(node.Z) > 1e-9 && unit.Length <= SessionSphereParameters.StoredPositionTolerance;
    }

    /// <summary>Radio no lineal acotado: crece con el número de notas y, levemente, con su densidad de enlaces.</summary>
    internal static double SphereRadius(int count, int edgeCount)
    {
        if (count <= 1) return SessionSphereParameters.SingleNoteRadius;
        var density = Math.Clamp(2.0 * edgeCount / count / 6, 0, 1);
        return Math.Clamp(SessionSphereParameters.RadiusScale * Math.Pow(count, SessionSphereParameters.RadiusExponent)
            * (1 + 0.12 * density), SessionSphereParameters.MinRadius, SessionSphereParameters.MaxRadius);
    }

    /// <summary>Fase inicial de giro estable por sesión: evita que todas las esferas parezcan sincronizadas.</summary>
    internal static double Phase(string sessionId) => StableRandom.Hash("phase:" + sessionId) / (double)uint.MaxValue * Math.Tau;

    private static Volume CreateVolume(string sessionId, SessionGraphNode[] nodes, (string From, string To)[] edges, Volume? previous)
    {
        var known = previous is null ? Enumerable.Empty<string>() : previous.Local.Keys;
        var ids = known.Concat(nodes.Select(n => n.GlobalId))
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var index = ids.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => t.i, StringComparer.Ordinal);
        var links = edges.Where(e => index.ContainsKey(e.From) && index.ContainsKey(e.To))
            .Select(e => (A: index[e.From], B: index[e.To])).Where(e => e.A != e.B)
            .Select(e => e.A < e.B ? e : (e.B, e.A)).Distinct().OrderBy(e => e.Item1).ThenBy(e => e.Item2).ToArray();
        var radius = Math.Max(SphereRadius(ids.Length, links.Length), previous?.Radius ?? 0);

        var pinned = new Dictionary<string, Point3>(StringComparer.Ordinal);
        if (previous is not null)
        {
            var scale = radius / previous.Radius;
            foreach (var (id, point) in previous.Local) pinned[id] = point * scale;
        }
        foreach (var node in nodes)
            if (!pinned.ContainsKey(node.GlobalId) && TryReadStoredPosition(node, out var unit))
                pinned[node.GlobalId] = unit * radius;

        var points = SessionSphereDistribution.Create(sessionId, ids, links, radius, pinned);
        var local = new Dictionary<string, Point3>(StringComparer.Ordinal);
        for (var i = 0; i < ids.Length; i++) local[ids[i]] = points[i];
        return new Volume(radius, local);
    }

    /// <summary>Anillo de esferas, como el visor anterior: cada sesión reserva un sector proporcional a su
    /// envolvente (radio + etiqueta + separación) y una búsqueda binaria del radio del anillo impide solapes
    /// aunque una sesión sea mucho mayor que las demás. Solo se recalcula si cambian las sesiones o sus radios.</summary>
    private void Place()
    {
        var ordered = _volumes.OrderBy(v => v.Key, StringComparer.Ordinal).ToArray();
        var key = string.Join('|', ordered.Select(v => v.Key + ":" + v.Value.Radius.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        if (key == _placementKey) return;
        _placementKey = key;
        _centers.Clear();
        if (ordered.Length == 0) return;
        if (ordered.Length == 1)
        {
            _centers[ordered[0].Key] = default;
            return;
        }

        var envelopes = ordered.Select(v => v.Value.Radius + SessionSphereParameters.LabelClearance
            + SessionSphereParameters.SphereGap / 2).ToArray();
        var lower = envelopes.Max();
        var upper = envelopes.Sum();
        for (var iteration = 0; iteration < 48; iteration++)
        {
            var candidate = (lower + upper) / 2;
            if (envelopes.Sum(e => 2 * Math.Asin(Math.Min(1, e / candidate))) > Math.Tau) lower = candidate;
            else upper = candidate;
        }
        var angle = -Math.PI / 2;
        var previousHalfAngle = 0d;
        for (var i = 0; i < ordered.Length; i++)
        {
            var halfAngle = Math.Asin(Math.Min(1, envelopes[i] / upper));
            angle += previousHalfAngle + halfAngle;
            _centers[ordered[i].Key] = new Point(Math.Cos(angle) * upper, Math.Sin(angle) * upper);
            previousHalfAngle = halfAngle;
        }
    }
}
