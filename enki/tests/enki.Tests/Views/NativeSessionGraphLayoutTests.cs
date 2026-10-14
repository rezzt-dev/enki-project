using System;
using System.Linq;
using System.Collections.Generic;
using Avalonia;
using enki.Models;
using enki.Views.Sessions;
using Xunit;

namespace enki.Tests.Views;

public class NativeSessionGraphLayoutTests
{
    [Fact]
    public void InterleavedStoredSessions_AreSeparatedWithoutChangingSourceCoordinates()
    {
        var graph = new SessionGraph();
        for (var i = 0; i < 150; i++)
            graph.Nodes.Add(new SessionGraphNode
            {
                SessionId = $"session-{i % 8}", SessionName = $"Session {i % 8}",
                RelativePath = $"{i}.md", Title = $"Note {i}",
                HasStoredPosition = true, X = Math.Cos(i) * 80, Y = Math.Sin(i) * 80
            });
        var original = graph.Nodes.Select(n => (n.X, n.Y)).ToArray();
        var layout = NativeSessionGraphLayout.Create(graph);
        var points = layout.Values.ToArray();
        for (var i = 0; i < points.Length; i++)
        for (var j = i + 1; j < points.Length; j++)
            Assert.True(Distance(points[i], points[j]) >= 23.9);
        var groups = graph.Nodes.GroupBy(n => n.SessionId).Select(g =>
        {
            var group = g.Select(n => layout[n.GlobalId]).ToArray();
            return new Rect(new Point(group.Min(p => p.X), group.Min(p => p.Y)),
                new Point(group.Max(p => p.X), group.Max(p => p.Y))).Inflate(20);
        }).ToArray();
        for (var i = 0; i < groups.Length; i++)
        for (var j = i + 1; j < groups.Length; j++)
            Assert.False(groups[i].Intersects(groups[j]));
        Assert.Equal(original, graph.Nodes.Select(n => (n.X, n.Y)).ToArray());
        graph.Nodes.Reverse();
        var reordered = NativeSessionGraphLayout.Create(graph);
        foreach (var (id, point) in layout) Assert.Equal(point, reordered[id]);
    }

    [Fact]
    public void ReadableStoredLayout_PreservesUserPositions()
    {
        var graph = new SessionGraph
        {
            Nodes =
            [
                new() { SessionId = "a", SessionName = "A", RelativePath = "1.md", Title = "One", HasStoredPosition = true, X = -120, Y = 40 },
                new() { SessionId = "a", SessionName = "A", RelativePath = "2.md", Title = "Two", HasStoredPosition = true, X = -60, Y = 90 },
                new() { SessionId = "b", SessionName = "B", RelativePath = "1.md", Title = "Three", HasStoredPosition = true, X = 160, Y = 10 }
            ]
        };
        var layout = NativeSessionGraphLayout.Create(graph);
        foreach (var node in graph.Nodes) Assert.Equal(new Point(node.X, node.Y), layout[node.GlobalId]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(1, 3)]
    [InlineData(1, 8)]
    [InlineData(1, 20)]
    [InlineData(6, 7)]
    [InlineData(9, 150)]
    [InlineData(10, 2000)]
    public void Mesh_ConnectsNodesOnlyWithinEachSession(int sessions, int count)
    {
        var graph = CreateGraph(sessions, count);
        var positions = NativeSessionGraphLayout.Create(graph);
        var mesh = NativeSessionGraphLayout.CreateMesh(graph, positions);
        Assert.Empty(graph.Edges);
        Assert.True(mesh.Count <= 3 * count);
        var owners = graph.Nodes.ToDictionary(n => n.GlobalId, n => n.SessionId);
        var unique = new HashSet<(string, string)>();
        var neighbors = graph.Nodes.ToDictionary(n => n.GlobalId, _ => new List<string>());
        foreach (var (from, to) in mesh)
        {
            Assert.NotEqual(from, to);
            Assert.Equal(owners[from], owners[to]);
            Assert.True(unique.Add(string.CompareOrdinal(from, to) < 0 ? (from, to) : (to, from)));
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
        graph.Nodes.Reverse();
        Assert.Equal(mesh, NativeSessionGraphLayout.CreateMesh(graph, positions));
    }

    [Fact]
    public void UnequalSessions_HaveCircularCentersAndSeparateEnvelopes()
    {
        var graph = CreateGraph(8, 80);
        graph.Nodes.AddRange(CreateGraph(1, 200).Nodes.Select(n => new SessionGraphNode
        {
            SessionId = "large", SessionName = "Large", RelativePath = n.RelativePath, Title = n.Title
        }));
        var positions = NativeSessionGraphLayout.Create(graph);
        var groups = graph.Nodes.GroupBy(n => n.SessionId).Select(g =>
        {
            var points = g.Select(n => positions[n.GlobalId]).ToArray();
            var center = new Point(points.Average(p => p.X), points.Average(p => p.Y));
            return (Center: center, Radius: points.Max(p => Distance(p, center)));
        }).ToArray();
        var ringRadius = Distance(default, groups[0].Center);
        foreach (var group in groups) Assert.Equal(ringRadius, Distance(default, group.Center), 6);
        for (var i = 0; i < groups.Length; i++)
        for (var j = i + 1; j < groups.Length; j++)
            Assert.True(Distance(groups[i].Center, groups[j].Center) > groups[i].Radius + groups[j].Radius + 100);
    }

    [Fact]
    public void OrganicMesh_IsLocallyConnectedAndRemainsStableWhenLinksChange()
    {
        var graph = CreateGraph(1, 127);
        var positions = NativeSessionGraphLayout.Create(graph);
        var mesh = NativeSessionGraphLayout.CreateMesh(graph, positions);
        foreach (var (from, to) in mesh) Assert.InRange(Distance(positions[from], positions[to]), 24, 200);
        Assert.True(mesh.Average(e => Distance(positions[e.From], positions[e.To])) < 65);
        // Sin filas de rejilla: la mayoría de las notas tiene una altura distinta.
        Assert.True(positions.Values.Select(p => Math.Round(p.Y, 3)).Distinct().Count() > graph.Nodes.Count * 0.9);
        foreach (var node in graph.Nodes) node.ConnectionCount = 100 - node.ConnectionCount;
        var updated = NativeSessionGraphLayout.Create(graph);
        foreach (var (id, point) in positions) Assert.Equal(point, updated[id]);
    }

    [Fact]
    public void Sessions_HaveDistinctStableShapesAndReadableSpacing()
    {
        var graph = new SessionGraph
        {
            Nodes = Enumerable.Range(0, 80).SelectMany(i => new[] { "alpha", "beta" }.Select(session =>
                new SessionGraphNode { SessionId = session, SessionName = session,
                    RelativePath = $"{i:000}.md", Title = $"Note {i}" })).ToList()
        };
        var first = NativeSessionGraphLayout.Create(graph);
        graph.Nodes.Reverse();
        var second = NativeSessionGraphLayout.Create(graph);
        foreach (var (id, point) in first) Assert.Equal(point, second[id]);

        var shapes = graph.Nodes.GroupBy(node => node.SessionId).ToDictionary(group => group.Key, group =>
        {
            var points = group.OrderBy(node => node.RelativePath)
                .Select(node => first[node.GlobalId]).ToArray();
            for (var i = 0; i < points.Length; i++)
            for (var j = i + 1; j < points.Length; j++)
                Assert.True(Distance(points[i], points[j]) >= 26.9);
            var center = new Point(points.Average(point => point.X), points.Average(point => point.Y));
            return points.Select(point => Distance(point, center)).ToArray();
        });
        Assert.True(shapes["alpha"].Zip(shapes["beta"]).Count(pair => Math.Abs(pair.First - pair.Second) > 5) > 40);
    }

    private static SessionGraph CreateGraph(int sessions, int count) => new()
    {
        Nodes = Enumerable.Range(0, count).Select(i => new SessionGraphNode
        {
            SessionId = $"session-{i % sessions}", SessionName = $"Session {i % sessions}",
            RelativePath = $"{i}.md", Title = $"Note {i}"
        }).ToList()
    };

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
