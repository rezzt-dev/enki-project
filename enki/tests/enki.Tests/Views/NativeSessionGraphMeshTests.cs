using System;
using System.Linq;
using Avalonia;
using enki.Views.Sessions;
using Xunit;

namespace enki.Tests.Views;

public class NativeSessionGraphMeshTests
{
    [Fact]
    public void StoredOrFilteredPositions_ProduceMeshWithoutCrossings()
    {
        // El orden de las notas no coincide con su orden espacial.
        var points = Enumerable.Range(0, 43).Select(i =>
            new Point(Math.Cos(i * 2.399963229728653) * Math.Sqrt(i) * 38,
                Math.Sin(i * 2.399963229728653) * Math.Sqrt(i) * 38)).Reverse().ToArray();
        var edges = NativeSessionGraphMesh.Create(points);
        Assert.Equal(points.Length, edges.SelectMany(e => new[] { e.From, e.To }).Distinct().Count());
        for (var i = 0; i < edges.Count; i++)
        for (var j = i + 1; j < edges.Count; j++)
        {
            var a = edges[i];
            var b = edges[j];
            if (a.From == b.From || a.From == b.To || a.To == b.From || a.To == b.To) continue;
            Assert.False(Cross(points[a.From], points[a.To], points[b.From]) * Cross(points[a.From], points[a.To], points[b.To]) < 0 &&
                Cross(points[b.From], points[b.To], points[a.From]) * Cross(points[b.From], points[b.To], points[a.To]) < 0);
        }
    }

    [Fact]
    public void CollinearAndCoincidentNotes_AreAllConnected()
    {
        Point[] points = [new(10, 10), new(0, 0), new(10, 10), new(30, 30), new(20, 20)];
        var edges = NativeSessionGraphMesh.Create(points);
        Assert.Equal(points.Length - 1, edges.Count);
        var visited = new System.Collections.Generic.HashSet<int> { 0 };
        for (var i = 0; i < points.Length; i++)
        foreach (var (from, to) in edges)
            if (visited.Contains(from) || visited.Contains(to)) { visited.Add(from); visited.Add(to); }
        Assert.Equal(points.Length, visited.Count);
    }

    private static double Cross(Point a, Point b, Point c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
}
