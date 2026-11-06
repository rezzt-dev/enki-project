using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
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
    public void GraphWithoutBrowser_RendersClicksDragsInDepthAndZooms(bool light)
    {
        HeadlessApp.EnsureInitialized();
        var previousTheme = Application.Current!.RequestedThemeVariant;
        Application.Current.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var graph = CreateGraph();
        var control = new NativeSessionGraph();
        control.SetRotationEnabled(false);
        control.SetGraph(graph, reset: true);
        var window = new Window { Width = 900, Height = 650, Content = control };
        try
        {
            window.Show();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            // Render real del control: comprueba también colores CSS compartidos, fuentes y recursos.
            using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(900, 650)))
                bitmap.Render(control);
            Assert.Equal(graph.Nodes.Count, control.NodeCount);
            // La nota más interior de la primera sesión: se puede mover sin chocar con el borde de la esfera.
            // Sin otra nota a menos de 24 px en pantalla, para que el clic no pueda resolverse a una vecina.
            var id = graph.Nodes.Where(n => n.SessionId == "one").OrderBy(n => control.LocalPosition(n.GlobalId).Length)
                .First(n => graph.Nodes.All(o => o == n
                    || Distance(control.NodeScreenPosition(o.GlobalId), control.NodeScreenPosition(n.GlobalId)) > 24)).GlobalId;
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
            var before = control.LocalPosition(id);
            var end = start + new Vector(9, 6);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            Assert.NotNull(saved);
            // Se guardan todas las notas visibles de la sesión, normalizadas al radio y en 3D.
            Assert.Equal(graph.Nodes.Count(n => n.SessionId == "one"), saved!.Count);
            Assert.All(saved, p => Assert.True(Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z) <= 1 + 1e-9));
            Assert.Contains(saved, p => Math.Abs(p.Z) > 1e-6);
            Assert.NotEqual(before, control.LocalPosition(id));
            var landed = control.NodeScreenPosition(id);
            Assert.True(Distance(landed, end) < 0.01, $"{landed} != {end}");
            Assert.Null(clicked);
            Assert.Null(opened);

            // Filtrar y recolorear conserva las posiciones actuales de los nodos que quedan visibles.
            control.SetGraph(new SessionGraph { Nodes = graph.Nodes.Where(n => n.SessionId == "one").ToList() }, reset: false);
            Assert.True(Distance(control.NodeScreenPosition(id), end) < 0.01);
            control.SetSessionColor("one", "#ff8800");
            control.SetPendingLink(id);
            control.ShowRejectedLink(id, graph.Nodes[1].GlobalId);
            control.SetSelectedSession("one");
            using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(900, 650)))
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
    public void Rotation_TurnsEverySphereAtConstantSpeed_AndPausesWithoutJumps()
    {
        HeadlessApp.EnsureInitialized();
        var graph = CreateGraph();
        var control = new NativeSessionGraph();
        control.SetGraph(graph, reset: true);
        control.Measure(new Size(900, 650));
        control.Arrange(new Rect(0, 0, 900, 650));
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(900, 650));
        bitmap.Render(control);
        Assert.True(control.IsRotationEnabled);
        var sessions = new[] { "one", "two" };
        var initial = sessions.ToDictionary(s => s, control.OrientationOf);
        // Cada esfera arranca con su propia fase, pero todas comparten la misma inclinación.
        Assert.NotEqual(initial["one"].Yaw, initial["two"].Yaw);
        Assert.Equal(initial["one"].Pitch, initial["two"].Pitch);
        var local = graph.Nodes.ToDictionary(n => n.GlobalId, n => control.LocalPosition(n.GlobalId));
        var centers = sessions.ToDictionary(s => s, control.SphereScreenCenter);
        var labels = control.LabelBounds.ToArray();
        var screen = graph.Nodes.ToDictionary(n => n.GlobalId, n => control.NodeScreenPosition(n.GlobalId));

        // Delta time: dos medios segundos equivalen a un segundo entero, a 0,10 rad/s.
        control.AdvanceRotation(0.5);
        control.AdvanceRotation(0.5);
        bitmap.Render(control);
        foreach (var session in sessions)
        {
            Assert.Equal(initial[session].Yaw + SessionSphereParameters.RotationSpeed, control.OrientationOf(session).Yaw, 9);
            Assert.Equal(initial[session].Pitch, control.OrientationOf(session).Pitch);
            Assert.Equal(centers[session], control.SphereScreenCenter(session));
        }
        // Gira la proyección, no la estructura: posiciones locales intactas y etiquetas de sesión inmóviles.
        foreach (var node in graph.Nodes) Assert.Equal(local[node.GlobalId], control.LocalPosition(node.GlobalId));
        Assert.Contains(graph.Nodes, n => control.NodeScreenPosition(n.GlobalId) != screen[n.GlobalId]);
        Assert.Equal(labels.Take(2), control.LabelBounds.Take(2));

        control.SetRotationEnabled(false);
        var paused = sessions.ToDictionary(s => s, control.OrientationOf);
        var pausedScreen = graph.Nodes.ToDictionary(n => n.GlobalId, n => control.NodeScreenPosition(n.GlobalId));
        control.AdvanceRotation(3);
        foreach (var session in sessions) Assert.Equal(paused[session], control.OrientationOf(session));
        foreach (var node in graph.Nodes) Assert.Equal(pausedScreen[node.GlobalId], control.NodeScreenPosition(node.GlobalId));

        // Reanudar continúa desde la orientación conservada.
        control.SetRotationEnabled(true);
        control.AdvanceRotation(0.25);
        foreach (var session in sessions)
            Assert.Equal(paused[session].Yaw + SessionSphereParameters.RotationSpeed * 0.25, control.OrientationOf(session).Yaw, 9);
    }

    [Fact]
    public void HoveringANote_HoldsItsSphereStill_WhileTheOthersKeepTurning()
    {
        HeadlessApp.EnsureInitialized();
        var graph = CreateGraph();
        var control = new NativeSessionGraph();
        control.SetGraph(graph, reset: true);
        var window = new Window { Width = 900, Height = 650, Content = control };
        try
        {
            window.Show();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(900, 650));
            bitmap.Render(control);
            var id = graph.Nodes.First(n => n.SessionId == "one"
                && graph.Nodes.All(o => o == n || Distance(control.NodeScreenPosition(o.GlobalId), control.NodeScreenPosition(n.GlobalId)) > 24)).GlobalId;
            window.MouseMove(control.NodeScreenPosition(id));
            // Con dibujo real también llegan fotogramas del bucle: se mide a partir del hover ya registrado
            // y se comprueba la cota inferior del giro de la otra esfera.
            var held = control.OrientationOf("one");
            var point = control.NodeScreenPosition(id);
            var other = control.OrientationOf("two");

            control.AdvanceRotation(1);
            Assert.Equal(held, control.OrientationOf("one"));
            Assert.Equal(point, control.NodeScreenPosition(id));
            Assert.True(control.OrientationOf("two").Yaw >= other.Yaw + SessionSphereParameters.RotationSpeed - 1e-9);

            window.MouseMove(new Point(4, 4));
            var released = control.OrientationOf("one");
            control.AdvanceRotation(0.5);
            Assert.True(control.OrientationOf("one").Yaw >= released.Yaw + SessionSphereParameters.RotationSpeed * 0.5 - 1e-9);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public void DraggingASphere_RotatesItByHand_AndClickingItSelectsTheSession()
    {
        HeadlessApp.EnsureInitialized();
        var graph = CreateGraph();
        var control = new NativeSessionGraph();
        control.SetRotationEnabled(false);
        control.SetGraph(graph, reset: true);
        var window = new Window { Width = 900, Height = 650, Content = control };
        try
        {
            window.Show();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(900, 650));
            bitmap.Render(control);
            string? session = null;
            string? note = null;
            control.SessionClicked += value => session = value;
            control.NodeClicked += value => note = value;
            // Un punto del volumen de la esfera sin ninguna nota debajo.
            var center = control.SphereScreenCenter("two");
            var empty = Enumerable.Range(0, 400).Select(i => center + new Vector(Math.Cos(i) * (i % 20), Math.Sin(i) * (i % 20)))
                .First(p => graph.Nodes.All(n => Distance(control.NodeScreenPosition(n.GlobalId), p) > 16));
            var before = control.OrientationOf("two");
            var other = control.OrientationOf("one");
            window.MouseDown(empty, MouseButton.Left);
            window.MouseMove(empty + new Vector(40, 20), RawInputModifiers.LeftMouseButton);
            window.MouseUp(empty + new Vector(40, 20), MouseButton.Left);
            var after = control.OrientationOf("two");
            Assert.Equal(before.Yaw + 40 * SessionSphereParameters.ManualRotationPerPixel, after.Yaw, 9);
            Assert.Equal(before.Pitch + 20 * SessionSphereParameters.ManualRotationPerPixel, after.Pitch, 9);
            Assert.Equal(other, control.OrientationOf("one"));
            Assert.Equal(center, control.SphereScreenCenter("two"));
            Assert.Null(session);

            window.MouseDown(empty, MouseButton.Left);
            window.MouseUp(empty, MouseButton.Left);
            Assert.Equal("two", session);
            Assert.Null(note);

            // Arrastrar fuera de las esferas desplaza la cámara sin girar ninguna.
            var outside = new Point(8, 8);
            window.MouseDown(outside, MouseButton.Left);
            window.MouseMove(outside + new Vector(30, 0), RawInputModifiers.LeftMouseButton);
            window.MouseUp(outside + new Vector(30, 0), MouseButton.Left);
            Assert.True(Distance(center + new Vector(30, 0), control.SphereScreenCenter("two")) < 1e-6);
            Assert.Equal(after, control.OrientationOf("two"));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public void LinkUpdate_PreservesZoomNodePositionsAndOrientation()
    {
        HeadlessApp.EnsureInitialized();
        var graph = CreateGraph();
        var control = new NativeSessionGraph();
        control.SetRotationEnabled(false);
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
        control.SetRotationEnabled(false);
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
            Assert.True(Distance(before[i] + new Vector(100, 50), control.NodeScreenPosition(graph.Nodes[i].GlobalId)) < 1e-6);
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
    public void CrowdedGraph_LabelsStayInsideViewportWithoutOverlapping(int width, int height)
    {
        HeadlessApp.EnsureInitialized();
        var graph = new SessionGraph();
        for (var i = 0; i < 150; i++)
            graph.Nodes.Add(new SessionGraphNode
            {
                SessionId = $"session-{i % 9}", SessionName = $"Session with a long name {i % 9}",
                RelativePath = $"{i}.md", Title = new string('X', 300)
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
            control.AdvanceRotation(2);
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

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static SessionGraph CreateGraph()
    {
        var graph = new SessionGraph();
        foreach (var session in new[] { "one", "two" })
            for (var i = 0; i < 14; i++)
                graph.Nodes.Add(new SessionGraphNode
                {
                    SessionId = session, SessionName = session == "one" ? "One" : "Two", RelativePath = $"{i}.md", Title = $"{session} {i}"
                });
        graph.Edges.Add(new SessionGraphEdge { FromSessionId = "one", ToSessionId = "two", FromRelativePath = "0.md", ToRelativePath = "0.md" });
        graph.Edges.Add(new SessionGraphEdge { FromSessionId = "one", ToSessionId = "one", FromRelativePath = "1.md", ToRelativePath = "2.md", IsManual = true });
        return graph;
    }
}
