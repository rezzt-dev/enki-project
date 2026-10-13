using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace enki.Views.Sessions;

/// <summary>Triangulación de Delaunay para la malla decorativa. Se construye al cambiar el grafo;
/// conserva la vecindad espacial de distribuciones guardadas y de subconjuntos filtrados.</summary>
internal static class NativeSessionGraphMesh
{
    public static IReadOnlyList<(int From, int To)> Create(IReadOnlyList<Point> source)
    {
        var edges = new HashSet<(int From, int To)>();
        if (source.Count < 2) return [];
        var points = new List<Point>();
        var indices = new List<int>();
        var unique = new Dictionary<Point, int>();
        for (var i = 0; i < source.Count; i++)
        {
            if (unique.TryGetValue(source[i], out var twin)) edges.Add((twin, i));
            else
            {
                unique[source[i]] = i;
                indices.Add(i);
                points.Add(source[i]);
            }
        }
        var count = points.Count;
        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var extent = Math.Max(points.Max(p => p.X) - minX, points.Max(p => p.Y) - minY);
        if (extent == 0) return edges.ToArray();
        // Normaliza para que el cálculo no dependa del desplazamiento de cámara ni de la escala.
        for (var i = 0; i < count; i++) points[i] = new Point((points[i].X - minX) / extent, (points[i].Y - minY) / extent);
        points.AddRange([new Point(-32, -16), new Point(0.5, 32), new Point(32, -16)]);
        var triangles = new List<Triangle>();
        AddTriangle(count, count + 1, count + 2);
        for (var i = 0; i < count; i++)
        {
            var boundary = new HashSet<(int A, int B)>();
            for (var j = triangles.Count - 1; j >= 0; j--)
            {
                var triangle = triangles[j];
                var delta = (Vector)(points[i] - triangle.Center);
                if (delta.SquaredLength > triangle.RadiusSquared + 1e-12) continue;
                triangles.RemoveAt(j);
                Toggle(triangle.A, triangle.B);
                Toggle(triangle.B, triangle.C);
                Toggle(triangle.C, triangle.A);
            }
            foreach (var (a, b) in boundary) AddTriangle(a, b, i);

            void Toggle(int a, int b)
            {
                var edge = a < b ? (a, b) : (b, a);
                if (!boundary.Add(edge)) boundary.Remove(edge);
            }
        }
        foreach (var triangle in triangles)
        {
            if (triangle.A >= count || triangle.B >= count || triangle.C >= count) continue;
            AddEdge(triangle.A, triangle.B);
            AddEdge(triangle.B, triangle.C);
            AddEdge(triangle.C, triangle.A);
        }
        // Los puntos colineales no forman triángulos; únelos en orden, también si solo hay dos.
        if (!triangles.Any(t => t.A < count && t.B < count && t.C < count))
        {
            var sorted = Enumerable.Range(0, count).OrderBy(i => points[i].X).ThenBy(i => points[i].Y).ToArray();
            for (var i = 1; i < sorted.Length; i++) AddEdge(sorted[i - 1], sorted[i]);
        }
        return edges.OrderBy(e => e.From).ThenBy(e => e.To).ToArray();

        void AddEdge(int a, int b)
        {
            var from = indices[a];
            var to = indices[b];
            edges.Add(from < to ? (from, to) : (to, from));
        }

        void AddTriangle(int a, int b, int c)
        {
            var origin = points[a];
            var u = (Vector)(points[b] - origin);
            var v = (Vector)(points[c] - origin);
            var determinant = 2 * (u.X * v.Y - u.Y * v.X);
            if (Math.Abs(determinant) < 1e-14) return;
            var offset = new Vector((v.Y * u.SquaredLength - u.Y * v.SquaredLength) / determinant,
                (u.X * v.SquaredLength - v.X * u.SquaredLength) / determinant);
            triangles.Add(new Triangle(a, b, c, origin + offset, offset.SquaredLength));
        }
    }

    private readonly record struct Triangle(int A, int B, int C, Point Center, double RadiusSquared);
}
