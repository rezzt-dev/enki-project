using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.Media;
using Avalonia.VisualTree;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using System.Threading;
using enki.Models;
using enki.Views.Sessions;
using Xunit;

namespace enki.UITests;

[Collection(AvaloniaHeadlessCollection.Name)]
public class GraphPresentationUiTests
{
    private static SessionGraph CreateGraph()
    {
        var graph = new SessionGraph();
        for (var session = 0; session < 3; session++)
        for (var note = 0; note < 12; note++)
        {
            var id = $"session-{session}";
            graph.Nodes.Add(new SessionGraphNode { SessionId = id, SessionName = "Research",
                RelativePath = $"Note-{note}.md", Title = $"Note {note}" });
            if (note > 0)
                graph.Edges.Add(new SessionGraphEdge { FromSessionId = id, ToSessionId = id,
                    FromRelativePath = $"Note-{note - 1}.md", ToRelativePath = $"Note-{note}.md" });
        }
        return graph;
    }

    private static void AwaitLoad(SessionsViewModel vm)
    {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!vm.PendingLoad.IsCompleted && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }
            Assert.True(vm.PendingLoad.IsCompleted);
            vm.PendingLoad.GetAwaiter().GetResult();
            Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void Workspace_KeepsSessionControlsOutsideCanvas_AndAdaptsToNarrowWindows()
    {
        HeadlessApp.EnsureInitialized();
        var directory = Path.Combine(Path.GetTempPath(), "enki-graph-test-" + Guid.NewGuid().ToString("N"));
        Window? window = null;
        var previousTheme = Application.Current!.RequestedThemeVariant;
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        try
        {
            var settings = new SettingsService(directory);
            var vault = new VaultService(settings);
            vault.InitializeVault(Path.Combine(directory, "vault"), false);
            var sessions = new SessionService(vault);
            var names = new[] { "curso-especializacion", "minecraft-things", "nutricion-deporte", "personal-docs",
                "personal-things", "projects-docs", "radio-fantasy", "services-docs" };
            for (var i = 0; i < names.Length; i++)
            {
                var session = sessions.Create(names[i]);
                for (var j = 0; j < 8 + i * 4; j++)
                    File.WriteAllText(Path.Combine(session.RootPath, $"Nota {j}.md"),
                        j % 5 == 0 ? $"# Nota {j}\n[[Nota {j + 1}]]" : $"# Nota {j}");
            }
            var vm = new SessionsViewModel(vault, sessions, new NotificationService(settings, directory), (_, _) => { }, () => { });
            AwaitLoad(vm);
            Assert.True(vm.ShowGraphMesh);
            var view = new SessionsView(() => false) { DataContext = vm };
            window = new Window { Width = 1600, Height = 950, Content = view, Background = new SolidColorBrush(Color.Parse("#121212")) };
            window.Show();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var panel = view.FindControl<Border>("GraphSessionPanel")!;
            var graph = view.GetVisualDescendants().OfType<NativeSessionGraph>().Single();
            Assert.True(panel.IsVisible);
            var panelLeft = panel.TranslatePoint(default, view)!.Value.X;
            var canvasRight = graph.TranslatePoint(new Point(graph.Bounds.Width, 0), view)!.Value.X;
            Assert.True(canvasRight <= panelLeft);
            using (var bitmap = new RenderTargetBitmap(new PixelSize(1600, 950)))
            {
                bitmap.Render(view);
                if (Environment.GetEnvironmentVariable("ENKI_GRAPH_PREVIEW") is { Length: > 0 } preview)
                {
                    Directory.CreateDirectory(preview);
                    bitmap.Save(Path.Combine(preview, "workspace.png"), PngBitmapEncoderOptions.Default);
                }
            }
            var count = graph.NodeCount;
            vm.GraphGroupFilters[0].IsActive = false;
            Assert.True(graph.NodeCount < count);
            vm.ResetGraphFiltersCommand.Execute(null);
            Assert.Equal(count, graph.NodeCount);
            window.Width = 1000;
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.False(panel.IsVisible);
            Assert.True(graph.Bounds.Width > 600);
        }
        finally
        {
            window?.Close();
            Application.Current.RequestedThemeVariant = previousTheme;
            Dispatcher.UIThread.RunJobs();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void HiddenSessions_RestorePositions_AndCameraControlsWorkWithoutBrowser()
    {
        HeadlessApp.EnsureInitialized();
        var graph = CreateGraph();
        var control = new NativeSessionGraph();
        control.SetGraph(graph, true);
        var window = new Window { Width = 900, Height = 600, Content = control };
        try
        {
            window.Show();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            using var bitmap = new RenderTargetBitmap(new PixelSize(900, 600));
            bitmap.Render(control);
            var positions = graph.Nodes.ToDictionary(n => n.GlobalId, n => control.NodeScreenPosition(n.GlobalId));
            control.SetGraph(new SessionGraph { Nodes = graph.Nodes.Where(n => n.SessionId == "session-0").ToList() }, false);
            control.SetGraph(graph, false);
            bitmap.Render(control);
            foreach (var (id, point) in positions) Assert.Equal(point, control.NodeScreenPosition(id));
            control.SetDisplay(false, true, 1.5, 0.8);
            control.MoveCamera("in");
            Assert.NotEqual(positions[graph.Nodes[0].GlobalId], control.NodeScreenPosition(graph.Nodes[0].GlobalId));
            control.MoveCamera("fit");
            bitmap.Render(control);
            Assert.All(graph.Nodes, n => Assert.True(new Rect(control.Bounds.Size).Contains(control.NodeScreenPosition(n.GlobalId))));
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [Fact]
    public void WebDocument_ProvidesSharedLayout_AndPresentationControls()
    {
        HeadlessApp.EnsureInitialized();
        var html = SessionGraphRenderer.RenderDocument(CreateGraph());
        Assert.Contains("window.__enkiCamera", html);
        Assert.Contains("window.__enkiSetDisplay", html);
        Assert.Contains("bySession.get(n.sessionId)", html);
        // Exportación optativa para verificar el documento real en un navegador durante el desarrollo.
        if (Environment.GetEnvironmentVariable("ENKI_GRAPH_PREVIEW") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "graph.html"), html);
        }
    }
}
