using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
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

    private (NotesView View, NotesViewModel ViewModel, Window Window) CreateView(KanbanService? kanban = null, Action<string>? onOpenLinkedCard = null)
    {
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.SetVaultPath(_vaultDir);
        var session = new SessionService(vault);
        var viewModel = new NotesViewModel(vault, session, kanban, onOpenLinkedCard, settingsService: settings);

        var view = new NotesView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();

        return (view, viewModel, window);
    }

    [Fact]
    public void LinkedCards_AreInHeader_AndPopupOpensTheOriginalTask()
    {
        var kanban = new KanbanService(_tempDir);
        var card = kanban.DefaultBoard.Columns[0].Cards[0];
        var path = Path.Combine(_vaultDir, "Vinculada.md");
        File.WriteAllText(path, "Contenido");
        card.LinkedNotePath = path;
        string? openedId = null;
        var (view, vm, window) = CreateView(kanban, id => openedId = id);
        try
        {
            vm.OpenCardCommand.Execute(vm.Notes.Single());
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var editorView = view.GetVisualDescendants().OfType<EditorView>().Single();
            var trigger = editorView.FindControl<ToggleButton>("LinkedCardsTrigger")!;
            var header = editorView.FindControl<Grid>("EditorHeader")!;
            Assert.Contains(header, trigger.GetVisualAncestors());
            Assert.False(vm.ActiveEditor!.IsLinkedCardsExpanded);
            trigger.IsChecked = true;
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.True(vm.ActiveEditor.IsLinkedCardsExpanded);
            var popup = editorView.FindControl<Popup>("LinkedCardsPopup")!;
            Assert.True(popup.IsOpen);
            var taskButton = popup.Child!.GetVisualDescendants().OfType<Button>()
                .Single(b => b.DataContext is LinkedCardItemViewModel);
            Assert.NotNull(taskButton.Command);
            taskButton.Command!.Execute(taskButton.CommandParameter);
            Assert.Equal(card.Id, openedId);
            Assert.False(vm.ActiveEditor.IsLinkedCardsExpanded);
            Assert.False(popup.IsOpen);
        }
        finally { vm.CloseAllEditorsCommand.Execute(null); window.Close(); }
    }

    private static Point TabPoint(NotesView view, Window window, EditorViewModel editor)
    {
        var tab = view.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("editor-tab") && b.DataContext == editor);
        return tab.TranslatePoint(new Point(18, tab.Bounds.Height / 2), window)!.Value;
    }

    [Fact]
    public void DragTab_ReordersLocally_AndReleaseClearsTheGesture()
    {
        var (view, vm, window) = CreateView();
        try
        {
            vm.NewNoteCommand.Execute(null);
            var first = vm.ActiveEditor!;
            vm.NewNoteCommand.Execute(null);
            var second = vm.ActiveEditor!;
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var start = TabPoint(view, window, first);
            var end = TabPoint(view, window, second);

            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            Assert.True(second.IsDropTarget);
            window.MouseUp(end, MouseButton.Left);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.Equal(new[] { second, first }, vm.OpenEditors);
            Assert.Same(first, vm.ActiveEditor);
            Assert.False(second.IsDropTarget);

            // Tras soltar, mover el ratón no reutiliza la pulsación anterior.
            window.MouseMove(start);
            Assert.Equal(new[] { second, first }, vm.OpenEditors);
        }
        finally { vm.CloseAllEditorsCommand.Execute(null); window.Close(); }
    }

    [Fact]
    public void ClosingTabDuringGesture_CancelsReorder_AndCanOpenAgain()
    {
        var (view, vm, window) = CreateView();
        try
        {
            vm.NewNoteCommand.Execute(null);
            var first = vm.ActiveEditor!;
            vm.NewNoteCommand.Execute(null);
            var second = vm.ActiveEditor!;
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var start = TabPoint(view, window, first);
            var end = TabPoint(view, window, second);
            window.MouseDown(start, MouseButton.Left);
            vm.CloseEditorTabCommand.Execute(first);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            Assert.Same(second, Assert.Single(vm.OpenEditors));
            Assert.False(second.IsDropTarget);

            vm.NewNoteCommand.Execute(null);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var reopened = vm.ActiveEditor!;
            var close = view.GetVisualDescendants().OfType<Button>().Single(b =>
                b.DataContext == reopened && b.Command == vm.CloseEditorTabCommand);
            var point = close.TranslatePoint(new Point(10, 10), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.Same(second, Assert.Single(vm.OpenEditors));
        }
        finally { vm.CloseAllEditorsCommand.Execute(null); window.Close(); }
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
