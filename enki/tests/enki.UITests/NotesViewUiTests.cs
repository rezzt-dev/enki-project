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

/// <summary>Fase 6 (roadmap §11, "Tests de UI básicos: usar Avalonia.Headless para flujos críticos —
/// crear nota"). Unlike <see cref="ViewModels.NotesViewModelOpenTabsTests"/> and friends, which exercise
/// <see cref="NotesViewModel"/> directly, this drives the real compiled <see cref="NotesView"/> XAML bound
/// to a real viewmodel — the layer that only breaks from a typo'd binding path, a missing converter, or a
/// <c>DataType</c> mismatch, none of which a pure-viewmodel test can ever catch.
///
/// The <see cref="CollectionAttribute"/> below pins this class (and the other headless UI test classes) to
/// run sequentially on one worker thread — see <see cref="AvaloniaHeadlessCollection"/>'s remarks for why
/// that's required.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class NotesViewUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _vaultDir;

    public NotesViewUiTests()
    {
        HeadlessApp.EnsureInitialized();
        _vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(_vaultDir);
    }

    public void Dispose()
    {
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
    public void Loading_WithAnEmptyVault_RendersWithoutThrowing()
    {
        var (view, viewModel, window) = CreateView();

        Assert.Empty(viewModel.Notes);
        Assert.NotNull(view.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault());

        window.Close();
    }

    /// <summary>The "crear nota" flow itself: invokes the exact <see cref="NotesViewModel.NewNoteCommand"/>
    /// the toolbar's "+" button is bound to, then re-runs layout and asserts the notes grid's visual tree
    /// actually grew a container for the new note — not just that the viewmodel collection grew (that part
    /// is already covered by plain viewmodel tests), but that the real <c>ItemsControl</c> binding in
    /// <c>NotesView.axaml</c> reacts to it.</summary>
    [Fact]
    public void NewNoteCommand_AddsANote_AndTheGridRendersIt()
    {
        var (view, viewModel, window) = CreateView();

        viewModel.NewNoteCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.Single(viewModel.Notes);
        Assert.True(File.Exists(viewModel.Notes[0].Note.FullPath));

        var renderedTitles = view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .ToList();
        Assert.Contains(viewModel.Notes[0].Title, renderedTitles);

        window.Close();
    }
}
