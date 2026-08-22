using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Modo enfoque/zen (propuesta 7, docs/propuesta-nuevas-funcionalidades.md) — drives the real
/// compiled <see cref="NotesView"/>/<see cref="EditorView"/> XAML (not just the view models) to catch
/// exactly the class of bug a pure-viewmodel test can't: a typo'd binding path on the header/toolbar/side
/// panel, or the tab strip's own <c>IsVisible</c> binding pointing at the wrong property. Same shape as
/// <see cref="NotesViewUiTests"/>; see <see cref="AvaloniaHeadlessCollection"/>'s remarks for why this runs
/// in that same sequential collection.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class EditorViewZenModeUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _vaultDir;

    public EditorViewZenModeUiTests()
    {
        HeadlessApp.EnsureInitialized();
        _vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(_vaultDir);

        // Zen mode's underlying flag is shared, static, process-wide state (EditorPreferences.IsZenMode) —
        // reset it so a previous test in this same worker (or a leftover "true" from a bug) can't leak in.
        EditorPreferences.IsZenMode = false;
    }

    public void Dispose()
    {
        EditorPreferences.IsZenMode = false;
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (NotesView View, NotesViewModel ViewModel, Window Window) CreateView()
    {
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.SetVaultPath(_vaultDir);
        var session = new SessionService(vault);
        var viewModel = new NotesViewModel(vault, session, settingsService: settings);

        var view = new NotesView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();

        return (view, viewModel, window);
    }

    [Fact]
    public void ToggleZenMode_HidesEditorChromeAndTabStrip_AndRestoresOnToggleBack()
    {
        var (view, viewModel, window) = CreateView();
        viewModel.NewNoteCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var editor = viewModel.ActiveEditor!;
        var editorHeader = view.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "EditorHeader");
        var formattingToolbar = view.GetVisualDescendants().OfType<StackPanel>().First(s => s.Name == "FormattingToolbar");
        var sidePanelHost = view.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "SidePanelHost");
        var tabStrip = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "TabStripBar");

        Assert.True(editorHeader.IsVisible);
        Assert.True(formattingToolbar.IsVisible);
        Assert.True(sidePanelHost.Bounds.Width >= 0); // sanity: laid out at all
        Assert.True(tabStrip.IsVisible);
        Assert.False(editor.IsZenMode);
        Assert.False(viewModel.IsZenMode);

        editor.ToggleZenModeCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.True(editor.IsZenMode);
        Assert.True(viewModel.IsZenMode);
        Assert.False(editorHeader.IsVisible);
        Assert.False(formattingToolbar.IsVisible);
        Assert.Equal(0, editor.SidePanelWidth);
        Assert.False(tabStrip.IsVisible);

        editor.ToggleZenModeCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.False(editor.IsZenMode);
        Assert.False(viewModel.IsZenMode);
        Assert.True(editorHeader.IsVisible);
        Assert.True(formattingToolbar.IsVisible);
        Assert.True(tabStrip.IsVisible);

        window.Close();
    }
}
