using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Models;
using enki.Views.Sessions;
using Xunit;

namespace enki.UITests;

[Collection(AvaloniaHeadlessCollection.Name)]
public class NativeSessionGraphUiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GraphWithoutBrowser_RendersClicksDragsAndZooms(bool light)
    {
        HeadlessApp.EnsureInitialized();
        var previousTheme = Application.Current!.RequestedThemeVariant;
        Application.Current.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var graph = CreateGraph();
        var control = new NativeSessionGraph();
        control.SetGraph(graph, reset: true);
        var window = new Window { Width = 800, Height = 600, Content = control };
        try
        {
            window.Show();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            // Render real del control: comprueba también colores CSS compartidos, fuentes y recursos.
            using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(800, 600)))
                bitmap.Render(control);
            Assert.Equal(2, control.NodeCount);
            var id = graph.Nodes[0].GlobalId;
            var start = control.NodeScreenPosition(id);
            Assert.True(new Rect(control.Bounds.Size).Contains(start));
            string? clicked = null;
            string? opened = null;
            IReadOnlyList<(string RelativePath, double X, double Y, double Z)>? saved = null;
            control.NodeClicked += value => clicked = value;
            control.NodeDoubleClicked += value => opened = value;
            control.PositionsChanged += value => saved = value;
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            window.MouseUp(start, MouseButton.Left);
            Assert.Equal(id, clicked);
            window.MouseDown(start, MouseButton.Left);
            window.MouseUp(start, MouseButton.Left);
            Assert.Equal(id, opened);
            Assert.Null(saved);

            clicked = opened = null;
            var end = start + new Vector(50, 30);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            Assert.NotNull(saved);
            var moved = saved!.Single(p => p.RelativePath == id);
            Assert.NotEqual(graph.Nodes[0].X, moved.X);
            Assert.Equal(0, moved.Z);
            Assert.Equal(end, control.NodeScreenPosition(id));
            Assert.Null(clicked);
            Assert.Null(opened);

            // Filtrar y recolorear conserva las posiciones actuales de los nodos que quedan visibles.
            control.SetGraph(new SessionGraph { Nodes = [graph.Nodes[0]] }, reset: false);
            Assert.Equal(end, control.NodeScreenPosition(id));
            control.SetSessionColor("one", "#ff8800");
            control.SetPendingLink(id);
            control.ShowRejectedLink(id, graph.Nodes[1].GlobalId);
            using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(800, 600)))
                bitmap.Render(control);
            var beforeZoom = control.NodeScreenPosition(id);
            window.MouseWheel(new Point(50, 50), new Vector(0, 1));
            Assert.NotEqual(beforeZoom, control.NodeScreenPosition(id));
            control.SetGraph(new SessionGraph(), reset: true);
            Assert.Equal(0, control.NodeCount);
        }
        finally
        {
            window.Close();
            Application.Current.RequestedThemeVariant = previousTheme;
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public void LinkUpdate_PreservesZoomAndNodePositions()
    {
        HeadlessApp.EnsureInitialized();
        var graph = CreateGraph();
        var control = new NativeSessionGraph();
        control.SetGraph(graph, reset: true);
        var window = new Window { Width = 800, Height = 600, Content = control };
        try
        {
            window.Show();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(800, 600));
            bitmap.Render(control);
            window.MouseWheel(new Point(200, 200), new Vector(0, 2));
            var before = graph.Nodes.Select(n => control.NodeScreenPosition(n.GlobalId)).ToArray();
            graph.Edges.Clear();
            control.SetGraph(graph, reset: false);
            bitmap.Render(control);
            Assert.Equal(before, graph.Nodes.Select(n => control.NodeScreenPosition(n.GlobalId)).ToArray());
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public void Resizing_PreservesZoomAndWorldCenter()
    {
        HeadlessApp.EnsureInitialized();
        var graph = CreateGraph();
        var control = new NativeSessionGraph();
        control.SetGraph(graph, true);
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(1000, 700));
        bitmap.Render(control);
        control.MoveCamera("in");
        var before = graph.Nodes.Select(n => control.NodeScreenPosition(n.GlobalId)).ToArray();
        control.Measure(new Size(1000, 700));
        control.Arrange(new Rect(0, 0, 1000, 700));
        bitmap.Render(control);
        for (var i = 0; i < graph.Nodes.Count; i++)
            Assert.Equal(before[i] + new Vector(100, 50), control.NodeScreenPosition(graph.Nodes[i].GlobalId));
    }

    [Fact]
    public void Layout_HandlesLargeGraphsAndInvalidStoredCoordinates()
    {
        HeadlessApp.EnsureInitialized();
        var graph = new SessionGraph();
        for (var i = 0; i < 2000; i++)
            graph.Nodes.Add(new SessionGraphNode
            {
                SessionId = (i % 10).ToString(System.Globalization.CultureInfo.InvariantCulture),
                SessionName = "Session", RelativePath = $"{i}.md", Title = "Note",
                HasStoredPosition = true, X = double.NaN, Y = double.PositiveInfinity
            });
        var control = new NativeSessionGraph();
        control.SetGraph(graph, reset: true);
        control.Measure(new Size(1000, 700));
        control.Arrange(new Rect(0, 0, 1000, 700));
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(1000, 700));
        bitmap.Render(control);
        Assert.Equal(2000, control.NodeCount);
        foreach (var node in graph.Nodes)
        {
            var point = control.NodeScreenPosition(node.GlobalId);
            Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y));
            Assert.True(new Rect(control.Bounds.Size).Contains(point));
        }
    }

    [Theory]
    [InlineData(616, 508)]
    [InlineData(360, 300)]
    public void CrowdedStoredGraph_LabelsStayInsideViewportWithoutOverlapping(int width, int height)
    {
        HeadlessApp.EnsureInitialized();
        var graph = new SessionGraph();
        for (var i = 0; i < 150; i++)
            graph.Nodes.Add(new SessionGraphNode
            {
                SessionId = $"session-{i % 9}", SessionName = $"Session with a long name {i % 9}",
                RelativePath = $"{i}.md", Title = new string('X', 300),
                HasStoredPosition = true, X = Math.Cos(i) * 100, Y = Math.Sin(i) * 100
            });
        var control = new NativeSessionGraph();
        var saved = false;
        control.PositionsChanged += _ => saved = true;
        control.SetGraph(graph, reset: true);
        var window = new Window { Width = width, Height = height, Content = control };
        try
        {
            window.Show();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(width, height));
            bitmap.Render(control);
            Assert.NotEmpty(control.LabelBounds);
            AssertLabels();
            var point = control.NodeScreenPosition(graph.Nodes[0].GlobalId);
            window.MouseMove(point);
            bitmap.Render(control);
            Assert.Null(ToolTip.GetTip(control));
            AssertLabels();
            window.MouseWheel(point, new Vector(0, 3));
            bitmap.Render(control);
            AssertLabels();
            Assert.False(saved);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        void AssertLabels()
        {
            var labels = control.LabelBounds;
            var viewport = new Rect(control.Bounds.Size);
            for (var i = 0; i < labels.Count; i++)
            {
                Assert.True(viewport.Contains(labels[i]));
                for (var j = i + 1; j < labels.Count; j++)
                    Assert.False(labels[i].Intersects(labels[j]));
                foreach (var node in graph.Nodes)
                    Assert.False(labels[i].Contains(control.NodeScreenPosition(node.GlobalId)));
            }
        }
    }

    private static SessionGraph CreateGraph() => new()
    {
        Nodes =
        [
            new() { SessionId = "one", SessionName = "One", RelativePath = "same.md", Title = "First", HasStoredPosition = true, X = -100, Y = 0 },
            new() { SessionId = "two", SessionName = "Two", RelativePath = "same.md", Title = "Second", HasStoredPosition = true, X = 100, Y = 0 }
        ],
        Edges = [new() { FromSessionId = "one", ToSessionId = "two", FromRelativePath = "same.md", ToRelativePath = "same.md" }]
    };
}
