using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Threading;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Input;
using enki.Controls;
using Xunit;

namespace enki.UITests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class SidePanelGridUiTests
{
    public SidePanelGridUiTests() => HeadlessApp.EnsureInitialized();

    [Theory]
    [InlineData("FullNotes")]
    [InlineData("SimpleNotes")]
    [InlineData("FilesBrowser")]
    [InlineData("FilesTree")]
    [InlineData("SkillsBrowser")]
    [InlineData("SkillsTree")]
    [InlineData("Assistant")]
    public void RealViews_BindToSharedPreference(string layout)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
        var shell = new MainWindowViewModel(new SettingsService(directory));
        Control view = layout switch
        {
            "FullNotes" => new FullNotesView(),
            "SimpleNotes" => new SimpleNotesView(),
            "FilesBrowser" => new FilesBrowserView(),
            "FilesTree" => new FilesTreeView(),
            "SkillsBrowser" => new SkillsBrowserView(),
            "SkillsTree" => new SkillsTreeView(),
            _ => new AssistantView()
        };
        // La vista tiene su propio contexto; solo el enlace a la preferencia atraviesa la shell.
        view.DataContext = null;
        var window = new Window { Width = 1200, Height = 800, DataContext = shell, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            var grid = view.GetVisualDescendants().OfType<SidePanelGrid>().Single();
            Assert.False(grid.IsPanelOnRight);
            shell.SidePanels.NotesOnRight = true;
            shell.SidePanels.FilesOnRight = true;
            shell.SidePanels.SkillsOnRight = true;
            shell.SidePanels.AssistantOnRight = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(grid.IsPanelOnRight);
            Assert.Equal(FlowDirection.RightToLeft, grid.FlowDirection);
        }
        finally
        {
            window.Close();
            shell.Shutdown();
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SplitterDrag_ResizesPanelOnEitherSide(bool onRight)
    {
        var grid = new SidePanelGrid
        {
            IsPanelOnRight = onRight,
            ColumnDefinitions = new ColumnDefinitions("260,Auto,*")
        };
        var panel = new Border();
        var splitter = new GridSplitter { Width = 7, ResizeDirection = GridResizeDirection.Columns };
        var content = new Border();
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(content, 2);
        grid.Children.AddRange([panel, splitter, content]);
        var window = new Window { Width = 1000, Height = 600, Content = grid };
        window.Show();
        try
        {
            window.UpdateLayout();
            var start = splitter.TranslatePoint(new Point(3, 100), window)!.Value;
            var end = start + new Vector(onRight ? -40 : 40, 0);
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            window.UpdateLayout();
            Assert.Equal(300, panel.Bounds.Width, 1);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void ChangingSide_MovesPanelAndOverlay_PreservesReadingDirectionAndWidth()
    {
        var grid = new SidePanelGrid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        var panel = new Border { Width = 260 };
        var grip = new Border { Width = 7 };
        var content = new TextBox { Text = "Contenido" };
        var overlay = new Border { Width = 240, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        Grid.SetColumn(grip, 1);
        Grid.SetColumn(content, 2);
        Grid.SetColumn(overlay, 2);
        grid.Children.AddRange([panel, grip, content, overlay]);
        var window = new Window { Width = 1000, Height = 600, Content = grid };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.Equal(0, panel.TranslatePoint(default, window)!.Value.X);
            Assert.Equal(FlowDirection.LeftToRight, content.FlowDirection);
            Assert.Equal(20, SidePanelGrid.GetResizeDelta(grip, 20));
            grid.IsPanelOnRight = true;
            window.UpdateLayout();
            Assert.Equal(740, panel.TranslatePoint(default, window)!.Value.X);
            Assert.Equal(0, content.TranslatePoint(default, window)!.Value.X);
            Assert.Equal(260, panel.Bounds.Width);
            Assert.Equal(FlowDirection.LeftToRight, content.FlowDirection);
            Assert.Equal(-20, SidePanelGrid.GetResizeDelta(grip, 20));
            grid.IsPanelOnRight = false;
            window.UpdateLayout();
            Assert.Equal(0, panel.TranslatePoint(default, window)!.Value.X);
        }
        finally { window.Close(); }
    }
}
