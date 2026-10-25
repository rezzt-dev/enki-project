using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using enki.Models;
using enki.Views.Sessions;
using Xunit;

namespace enki.Tests.Views;

public class SessionSphereLayoutTests
{
    [Fact]
    public void Notes_FillTheVolume_WithAnOrganicShellAndInteriorDepths()
    {
        var graph = CreateGraph(1, 160, linkEvery: 3);
        var scene = new SessionSphereLayout().Build(graph);
        var radius = scene.Spheres["session-0"].Radius;
        var points = scene.Local.Values.ToArray();
        var radii = points.Select(p => p.Length / radius).ToArray();

        Assert.All(radii, r => Assert.InRange(r, 0, 1 + 1e-9));
        // Corteza orientativa del 65 % con variación radial: ni una superficie perfecta ni una nube uniforme.
        var shell = radii.Count(r => r >= SessionSphereParameters.ShellInnerRatio - 0.03);
        Assert.InRange(shell / (double)points.Length, 0.5, 0.8);
        Assert.True(radii.Where(r => r >= 0.77).Select(r => Math.Round(r, 2)).Distinct().Count() > 10);
        // Interior a varias profundidades, también cerca del centro: no es una esfera hueca.
        Assert.True(radii.Count(r => r < 0.45) >= 8);
        Assert.True(radii.Count(r => r is >= 0.45 and < 0.75) >= 8);
        // Coordenadas X, Y y Z reales y silueta global esférica: dispersión parecida en los tres ejes.
        var spread = new[] { StdDev(points.Select(p => p.X)), StdDev(points.Select(p => p.Y)), StdDev(points.Select(p => p.Z)) };
        Assert.All(spread, s => Assert.True(s > radius * 0.3));
        Assert.True(spread.Max() / spread.Min() < 1.35);
        Assert.True(points.Select(p => Math.Round(p.Z, 3)).Distinct().Count() > points.Length * 0.9);
        // Centro geométrico en el origen local: la esfera gira sobre sí misma.
        Assert.True(new Point3(points.Average(p => p.X), points.Average(p => p.Y), points.Average(p => p.Z)).Length < radius * 0.05);
    }

    [Fact]
    public void Notes_KeepAMinimumSeparation()
    {
        foreach (var count in new[] { 6, 40, 300 })
        {
            var scene = new SessionSphereLayout().Build(CreateGraph(1, count, linkEvery: 2));
            var points = scene.Local.Values.ToArray();
            var minimum = double.PositiveInfinity;
            for (var i = 0; i < points.Length; i++)
            for (var j = i + 1; j < points.Length; j++)
                minimum = Math.Min(minimum, (points[i] - points[j]).Length);
            Assert.True(minimum >= SessionSphereParameters.NodeSpacing * 0.6, $"{count}: {minimum}");
        }
    }

    [Fact]
    public void Topology_HubsGoInside_AndLinkedNotesStayCloserThanUnlinkedOnes()
    {
        var graph = CreateGraph(1, 90);
        // Una nota central enlazada con otras 30 y varias cadenas cortas.
        for (var i = 1; i <= 30; i++) AddEdge(graph, "session-0", 0, i);
        for (var i = 40; i < 89; i++) if (i % 4 != 0) AddEdge(graph, "session-0", i, i + 1);
        var scene = new SessionSphereLayout().Build(graph);
        var radius = scene.Spheres["session-0"].Radius;
        Assert.True(scene.Local["session-0::0.md"].Length < radius * SessionSphereParameters.ShellInnerRatio);

        var linked = graph.Edges.Select(e => (scene.Local[e.FromGlobalId] - scene.Local[e.ToGlobalId]).Length).Average();
        var ids = scene.Local.Keys.ToArray();
        var unlinked = Enumerable.Range(0, 400).Select(i => (scene.Local[ids[i * 7 % ids.Length]] - scene.Local[ids[i * 13 % ids.Length]]).Length)
            .Where(d => d > 0).Average();
        Assert.True(linked < unlinked * 0.8, $"{linked} vs {unlinked}");
    }

    [Fact]
    public void Build_PreservesTheGraph_AndAddsNoNotesOrLinks()
    {
        var graph = CreateGraph(3, 45, linkEvery: 2);
        graph.Edges.Add(new SessionGraphEdge { FromSessionId = "session-0", ToSessionId = "session-1", FromRelativePath = "0.md", ToRelativePath = "1.md" });
        var snapshot = graph.Nodes.Select(n => (n.GlobalId, n.Title, n.X, n.Y, n.Z, n.HasStoredPosition)).ToArray();
        var edges = graph.Edges.Select(e => (e.FromGlobalId, e.ToGlobalId, e.IsManual)).ToArray();

        var scene = new SessionSphereLayout().Build(graph);

        Assert.Equal(snapshot, graph.Nodes.Select(n => (n.GlobalId, n.Title, n.X, n.Y, n.Z, n.HasStoredPosition)).ToArray());
        Assert.Equal(edges, graph.Edges.Select(e => (e.FromGlobalId, e.ToGlobalId, e.IsManual)).ToArray());
        Assert.Equal(graph.Nodes.Select(n => n.GlobalId).OrderBy(id => id), scene.Local.Keys.OrderBy(id => id));
        Assert.Equal(graph.Nodes.Select(n => n.SessionId).Distinct().OrderBy(id => id), scene.Spheres.Keys.OrderBy(id => id));
        var owners = graph.Nodes.ToDictionary(n => n.GlobalId, n => n.SessionId);
        Assert.All(scene.Mesh, m => Assert.Equal(owners[m.From], owners[m.To]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void SmallSessions_UseOnlyTheirRealNotes(int count)
    {
        var scene = new SessionSphereLayout().Build(CreateGraph(1, count));
        Assert.Equal(count, scene.Local.Count);
        Assert.Equal(count == 0 ? 0 : 1, scene.Spheres.Count);
        if (count == 0) return;
        var sphere = scene.Spheres.Values.Single();
        if (count == 1)
        {
            Assert.Equal(new Point3(0, 0, 0), scene.Local.Values.Single());
            Assert.Equal(SessionSphereParameters.SingleNoteRadius, sphere.Radius);
            Assert.Empty(scene.Mesh);
            return;
        }
        var points = scene.Local.Values.ToArray();
        Assert.Equal(count, points.Distinct().Count());
        Assert.All(points, p => Assert.True(p.Length <= sphere.Radius + 1e-9));
        // Incluso dos notas se separan en profundidad, no solo en el plano de la pantalla.
        Assert.True(points.Max(p => p.Z) - points.Min(p => p.Z) > 1);
        Assert.InRange(scene.Mesh.Count, count - 1, 3 * count);
    }

    [Fact]
    public void Layout_IsDeterministic_AndIndependentOfInputOrder()
    {
        var graph = CreateGraph(4, 120, linkEvery: 3);
        var first = new SessionSphereLayout().Build(graph);
        graph.Nodes.Reverse();
        graph.Edges.Reverse();
        var second = new SessionSphereLayout().Build(graph);
        foreach (var (id, point) in first.Local) Assert.Equal(point, second.Local[id]);
        foreach (var (id, sphere) in first.Spheres) Assert.Equal(sphere, second.Spheres[id]);
        Assert.Equal(first.Mesh, second.Mesh);
    }

    [Fact]
    public void Sessions_HaveDistinctStableShapes()
    {
        var scene = new SessionSphereLayout().Build(CreateGraph(2, 120));
        var a = scene.Local.Where(p => p.Key.StartsWith("session-0::")).Select(p => p.Value.Length).OrderBy(v => v).ToArray();
        var b = scene.Local.Where(p => p.Key.StartsWith("session-1::")).Select(p => p.Value.Length).OrderBy(v => v).ToArray();
        Assert.True(a.Zip(b).Count(pair => Math.Abs(pair.First - pair.Second) > 1) > a.Length / 3);
    }

    [Fact]
    public void Filtering_LinkEdits_AndNewNotes_KeepExistingGeometry()
    {
        var graph = CreateGraph(3, 60, linkEvery: 4);
        var layout = new SessionSphereLayout();
        var full = layout.Build(graph);

        var subset = new SessionGraph { Nodes = graph.Nodes.Where(n => n.SessionId != "session-1" && n.RelativePath != "3.md").ToList() };
        var filtered = layout.Build(subset);
        foreach (var (id, point) in filtered.Local) Assert.Equal(full.Local[id], point);
        foreach (var (id, sphere) in filtered.Spheres) Assert.Equal(full.Spheres[id], sphere);

        graph.Edges.Clear();
        var relinked = layout.Build(graph);
        foreach (var (id, point) in full.Local) Assert.Equal(point, relinked.Local[id]);

        graph.Nodes.Add(new SessionGraphNode { SessionId = "session-2", SessionName = "S2", RelativePath = "new.md", Title = "New" });
        var grown = layout.Build(graph);
        var radiusBefore = full.Spheres["session-2"].Radius;
        var scale = grown.Spheres["session-2"].Radius / radiusBefore;
        foreach (var (id, point) in full.Local.Where(p => p.Key.StartsWith("session-2::")))
            AssertClose(point * scale, grown.Local[id]);
        foreach (var (id, point) in full.Local.Where(p => !p.Key.StartsWith("session-2::")))
            Assert.Equal(point, grown.Local[id]);
        Assert.Contains("session-2::new.md", grown.Local.Keys);
    }

    [Fact]
    public void StoredPositions_AreNormalisedLocalCoordinates_AndLegacyValuesAreIgnored()
    {
        var graph = CreateGraph(1, 12);
        graph.Nodes[0].HasStoredPosition = true;
        (graph.Nodes[0].X, graph.Nodes[0].Y, graph.Nodes[0].Z) = (0.2, -0.3, 0.5);
        // Layout 2D anterior (Z = 0) y coordenadas de mundo: no tienen la forma normalizada.
        graph.Nodes[1].HasStoredPosition = true;
        (graph.Nodes[1].X, graph.Nodes[1].Y, graph.Nodes[1].Z) = (0.1, 0.1, 0);
        graph.Nodes[2].HasStoredPosition = true;
        (graph.Nodes[2].X, graph.Nodes[2].Y, graph.Nodes[2].Z) = (140, -60, 12);
        graph.Nodes[3].HasStoredPosition = true;
        (graph.Nodes[3].X, graph.Nodes[3].Y, graph.Nodes[3].Z) = (double.NaN, 0, 0.4);

        var scene = new SessionSphereLayout().Build(graph);
        var radius = scene.Spheres["session-0"].Radius;
        AssertClose(new Point3(0.2, -0.3, 0.5) * radius, scene.Local[graph.Nodes[0].GlobalId]);
        Assert.NotEqual(new Point3(0.1, 0.1, 0) * radius, scene.Local[graph.Nodes[1].GlobalId]);
        Assert.All(scene.Local.Values, p => Assert.True(double.IsFinite(p.Length) && p.Length <= radius + 1e-9));
        Assert.True(SessionSphereLayout.TryReadStoredPosition(graph.Nodes[0], out _));
        Assert.False(SessionSphereLayout.TryReadStoredPosition(graph.Nodes[1], out _));
        Assert.False(SessionSphereLayout.TryReadStoredPosition(graph.Nodes[2], out _));
        Assert.False(SessionSphereLayout.TryReadStoredPosition(graph.Nodes[3], out _));
    }

    [Fact]
    public void MovedNotes_AreKept_AndClampedToTheirSphere()
    {
        var graph = CreateGraph(1, 20);
        var layout = new SessionSphereLayout();
        var scene = layout.Build(graph);
        var radius = scene.Spheres["session-0"].Radius;
        var moved = layout.Move("session-0", "session-0::4.md", new Point3(radius * 3, 0, 0));
        AssertClose(new Point3(radius, 0, 0), moved);
        Assert.Equal(moved, layout.Build(graph).Local["session-0::4.md"]);
    }

    [Fact]
    public void Spheres_ScaleNonLinearly_AndNeverOverlap()
    {
        var graph = CreateGraph(8, 80);
        graph.Nodes.AddRange(CreateGraph(1, 400).Nodes.Select(n => new SessionGraphNode
        {
            SessionId = "large", SessionName = "Large", RelativePath = n.RelativePath, Title = n.Title
        }));
        graph.Nodes.Add(new SessionGraphNode { SessionId = "single", SessionName = "Single", RelativePath = "a.md", Title = "A" });
        var spheres = new SessionSphereLayout().Build(graph).Spheres.Values.ToArray();

        var large = spheres.Single(s => s.SessionId == "large");
        var small = spheres.Single(s => s.SessionId == "session-0");
        Assert.True(large.Radius > small.Radius && large.Radius < small.Radius * 400 / 10.0);
        Assert.InRange(large.Radius, SessionSphereParameters.MinRadius, SessionSphereParameters.MaxRadius);
        Assert.Equal(SessionSphereParameters.SingleNoteRadius, spheres.Single(s => s.SessionId == "single").Radius);
        for (var i = 0; i < spheres.Length; i++)
        for (var j = i + 1; j < spheres.Length; j++)
            Assert.True(Distance(spheres[i].Center, spheres[j].Center) >= spheres[i].Radius + spheres[j].Radius
                + SessionSphereParameters.SphereGap);
        Assert.True(SessionSphereLayout.SphereRadius(10, 0) < SessionSphereLayout.SphereRadius(100, 0));
        Assert.Equal(SessionSphereParameters.MaxRadius, SessionSphereLayout.SphereRadius(100_000, 0));
    }

    [Fact]
    public void RemovedSessions_StopReservingSpace_OnlyWhenRetained()
    {
        var layout = new SessionSphereLayout();
        var graph = CreateGraph(3, 30);
        var before = layout.Build(graph);
        var reduced = new SessionGraph { Nodes = graph.Nodes.Where(n => n.SessionId != "session-2").ToList() };
        Assert.Equal(before.Spheres["session-0"], layout.Build(reduced).Spheres["session-0"]);
        layout.Retain(new HashSet<string> { "session-0", "session-1" });
        Assert.NotEqual(before.Spheres["session-0"].Center, layout.Build(reduced).Spheres["session-0"].Center);
    }

    [Fact]
    public void LargeGraphs_AreLaidOutQuickly_WithFiniteBoundedCoordinates()
    {
        var watch = Stopwatch.StartNew();
        var scene = new SessionSphereLayout().Build(CreateGraph(1, 2000, linkEvery: 2));
        var many = new SessionSphereLayout().Build(CreateGraph(10, 2000, linkEvery: 3));
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), watch.Elapsed.ToString());
        foreach (var built in new[] { scene, many })
        foreach (var (id, point) in built.Local)
        {
            var radius = built.Spheres[id[..id.IndexOf("::", StringComparison.Ordinal)]].Radius;
            Assert.True(double.IsFinite(point.Length) && point.Length <= radius + 1e-9);
        }
        Assert.Equal(2000, scene.Local.Count);
        Assert.InRange(scene.Mesh.Count, 1999, 6000);
    }

    [Fact]
    public void Mesh_ConnectsEachSessionWithLocalNeighbours_WithoutDuplicates()
    {
        foreach (var (sessions, count) in new[] { (1, 3), (1, 20), (6, 7), (9, 150) })
        {
            var graph = CreateGraph(sessions, count);
            var scene = new SessionSphereLayout().Build(graph);
            Assert.True(scene.Mesh.Count <= 3 * count);
            Assert.Equal(scene.Mesh.Count, scene.Mesh.Distinct().Count());
            var neighbors = graph.Nodes.ToDictionary(n => n.GlobalId, _ => new List<string>());
            foreach (var (from, to) in scene.Mesh)
            {
                Assert.NotEqual(from, to);
                neighbors[from].Add(to);
                neighbors[to].Add(from);
            }
            foreach (var group in graph.Nodes.GroupBy(n => n.SessionId))
            {
                var visited = new HashSet<string>();
                var pending = new Stack<string>();
                pending.Push(group.First().GlobalId);
                while (pending.TryPop(out var id))
                    if (visited.Add(id)) foreach (var neighbor in neighbors[id]) pending.Push(neighbor);
                Assert.Equal(group.Select(n => n.GlobalId).OrderBy(id => id), visited.OrderBy(id => id));
            }
        }
    }

    [Fact]
    public void Mesh_JoinsDistantClustersWithASingleBridge()
    {
        var points = Enumerable.Range(0, 8).Select(i => new Point3(i % 4 * 10, i / 4 * 10, 0))
            .Concat(Enumerable.Range(0, 4).Select(i => new Point3(1000 + i * 10, 0, 5))).ToArray();
        var mesh = SessionSphereMesh.Create(points);
        Assert.Single(mesh, e => (e.From < 8) != (e.To < 8));
    }

    [Fact]
    public void Rotation_IsRigid_AndProjectionCanBeInverted()
    {
        var points = new SessionSphereLayout().Build(CreateGraph(1, 40, linkEvery: 2)).Local.Values.ToArray();
        var orientation = new SphereOrientation(1.234, -0.41);
        var rotated = points.Select(p => SessionSphereProjection.Rotate(p, orientation)).ToArray();
        for (var i = 0; i < points.Length; i++)
        {
            AssertClose(points[i], SessionSphereProjection.Unrotate(rotated[i], orientation));
            Assert.Equal(points[i].Length, rotated[i].Length, 9);
            for (var j = i + 1; j < points.Length; j++)
                Assert.Equal((points[i] - points[j]).Length, (rotated[i] - rotated[j]).Length, 9);
        }

        var radius = 120;
        var (offset, depth, scale) = SessionSphereProjection.Project(points[3], radius, orientation);
        Assert.InRange(depth, -1, 1);
        Assert.InRange(scale, 0.7, 1.5);
        AssertClose(points[3], SessionSphereProjection.FromScreenOffset(offset, rotated[3].Z, radius, orientation));
        // Más cerca del observador, mayor escala aparente.
        Assert.True(SessionSphereProjection.Perspective(radius * 0.9, radius) > 1.3);
        Assert.True(SessionSphereProjection.Perspective(-radius * 0.9, radius) < 0.8);
        // Un pitch positivo acerca el polo superior (Y negativa en pantalla) al observador.
        Assert.True(SessionSphereProjection.Rotate(new Point3(0, -10, 0), new SphereOrientation(0, SessionSphereParameters.AxisTilt)).Z > 0);
    }

    private static SessionGraph CreateGraph(int sessions, int count, int linkEvery = 0)
    {
        var graph = new SessionGraph
        {
            Nodes = Enumerable.Range(0, count).Select(i => new SessionGraphNode
            {
                SessionId = $"session-{i % Math.Max(1, sessions)}", SessionName = $"Session {i % Math.Max(1, sessions)}",
                RelativePath = $"{i}.md", Title = $"Note {i}"
            }).ToList()
        };
        if (linkEvery > 0)
            for (var i = 0; i + sessions < count; i++)
                if (i % linkEvery == 0)
                    graph.Edges.Add(new SessionGraphEdge
                    {
                        FromSessionId = graph.Nodes[i].SessionId, ToSessionId = graph.Nodes[i + sessions].SessionId,
                        FromRelativePath = graph.Nodes[i].RelativePath, ToRelativePath = graph.Nodes[i + sessions].RelativePath
                    });
        return graph;
    }

    private static void AddEdge(SessionGraph graph, string session, int from, int to) =>
        graph.Edges.Add(new SessionGraphEdge { FromSessionId = session, ToSessionId = session, FromRelativePath = $"{from}.md", ToRelativePath = $"{to}.md" });

    private static void AssertClose(Point3 expected, Point3 actual) => Assert.True((expected - actual).Length < 1e-6, $"{expected} != {actual}");

    private static double StdDev(IEnumerable<double> values)
    {
        var array = values.ToArray();
        var mean = array.Average();
        return Math.Sqrt(array.Average(v => (v - mean) * (v - mean)));
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
