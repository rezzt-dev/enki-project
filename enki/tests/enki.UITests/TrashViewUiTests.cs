using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using enki.Localization;
using enki.Models;
using enki.Services;

using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Smoke-renders the real <see cref="TrashView"/> XAML bound to a real
/// <see cref="TrashViewModel"/> and opens its "vaciar categoría" and "borrado permanente individual"
/// confirmation dialogs (docs/version-alfa-3/02-roadmap-implementacion.md Fase 5).</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class TrashViewUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _vaultPath;

    public TrashViewUiTests()
    {
        HeadlessApp.EnsureInitialized();
        _vaultPath = Path.Combine(_tempDir, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (TrashView View, TrashViewModel ViewModel, KanbanService Kanban, Window Window) CreateView()
    {
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _tempDir);
        var kanbanService = new KanbanService(_tempDir);
        var polarTaskService = new PolarTaskService(_tempDir);
        var reminderService = new ReminderService(_tempDir);
        var viewModel = new TrashViewModel(vault, kanbanService, polarTaskService, reminderService, notifications, sessions);

        var view = new TrashView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();

        return (view, viewModel, kanbanService, window);
    }

    private static string RenderedText(Window window) => string.Join(
        " | ",
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)));

    [Fact]
    public void TrashView_RendersWithoutThrowing()
    {
        var (_, viewModel, _, window) = CreateView();

        Assert.NotNull(viewModel);
        window.Close();
    }

    [Fact]
    public void EmptyOverview_GivesWayToThePopulatedSection()
    {
        var (_, viewModel, kanban, window) = CreateView();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var overview = Assert.Single(window.GetVisualDescendants().OfType<Border>(),
            b => b.Classes.Contains("trash-overview-empty"));
        Assert.True(overview.IsVisible);
        Assert.Contains(LocalizationService.Instance["Trash_OverviewEmptyTitle"],
            RenderedText(window), StringComparison.OrdinalIgnoreCase);

        kanban.DefaultBoard.TrashedCards.Add(new KanbanTrashedCard
        {
            Card = new KanbanCard { Id = "c1", Title = "Tarea borrada" },
            ColumnId = "col-1",
            ColumnTitle = "Pendiente"
        });
        viewModel.Reload();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.False(overview.IsVisible);
        Assert.Single(window.GetVisualDescendants().OfType<Border>(),
            b => b.Classes.Contains("trash-card"));

        window.Close();
    }

    [Fact]
    public void RequestEmptyDocuments_OpensEmptyConfirmationDialog()
    {
        var (_, viewModel, _, window) = CreateView();

        viewModel.RequestEmptyDocumentsCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.Equal(TrashEmptyTarget.Documents, viewModel.PendingEmptyTarget);
        Assert.Contains(viewModel.PendingEmptyMessage, RenderedText(window));
        Assert.Contains(
            LocalizationService.Instance["Trash_EmptyButton"], RenderedText(window),
            StringComparison.OrdinalIgnoreCase);

        window.Close();
    }

    [Fact]
    public void CancelEmpty_ClosesTheDialogWithoutClearingAnything()
    {
        var (_, viewModel, kanban, window) = CreateView();
        kanban.DefaultBoard.TrashedCards.Add(new KanbanTrashedCard
        {
            Card = new KanbanCard { Id = "c1", Title = "Borrada" },
            ColumnId = "col-1",
            ColumnTitle = "Pendiente"
        });
        viewModel.Reload();
        var countBefore = viewModel.Tasks.Count;
        viewModel.RequestEmptyDocumentsCommand.Execute(null);

        viewModel.CancelEmptyCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.Null(viewModel.PendingEmptyTarget);
        Assert.Equal(countBefore, viewModel.Tasks.Count);

        window.Close();
    }
}
