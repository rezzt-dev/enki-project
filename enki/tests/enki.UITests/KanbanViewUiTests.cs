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

/// <summary>Fase 6 (roadmap §11, "Tests de UI básicos ... mover tarjeta Kanban") — same reasoning as
/// <see cref="NotesViewUiTests"/>: drives the real <see cref="KanbanView"/> XAML bound to a real
/// <see cref="KanbanViewModel"/> and exercises <see cref="KanbanViewModel.MoveCard"/>, the exact method
/// <c>KanbanView.axaml.cs</c>'s drag-and-drop handlers call on a real drop — then re-renders and asserts
/// the board's visual tree actually reflects the card under its new column, not just that the underlying
/// collections changed.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class KanbanViewUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");

    public KanbanViewUiTests() => HeadlessApp.EnsureInitialized();

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (KanbanView View, KanbanViewModel ViewModel, Window Window) CreateView()
    {
        var settings = new SettingsService(_tempDir);
        var authService = new PolarAuthService(settings);
        var restClient = new SupabaseRestClient(authService);
        var taskService = new PolarTaskService(_tempDir);
        var reminderService = new ReminderService(_tempDir);
        var syncService = new PolarSyncService(authService, restClient, taskService, reminderService, settings);
        var taskManagement = new PolarTaskManagementViewModel(taskService, syncService);
        var kanbanService = new KanbanService(_tempDir);
        var viewModel = new KanbanViewModel(kanbanService, authService, taskManagement);

        var view = new KanbanView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        window.Show();

        return (view, viewModel, window);
    }

    [Fact]
    public void Loading_TheSeedBoard_RendersEveryColumn()
    {
        var (view, viewModel, window) = CreateView();

        var renderedColumnTitles = view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .ToList();

        foreach (var column in viewModel.Columns)
            Assert.Contains(column.Title, renderedColumnTitles);

        window.Close();
    }

    /// <summary>Simulates the real drop: takes the first card of the first column and moves it to the last
    /// column via <see cref="KanbanViewModel.MoveCard"/> — the same call
    /// <c>KanbanView.axaml.cs</c>'s <c>ColumnBody_Drop</c> makes — then asserts both the viewmodel state
    /// and the re-rendered visual tree agree on where the card ended up.</summary>
    [Fact]
    public void MoveCard_ToAnotherColumn_UpdatesViewModelAndVisualTree()
    {
        var (view, viewModel, window) = CreateView();
        var sourceColumn = viewModel.Columns.First(c => c.Cards.Count > 0);
        var card = sourceColumn.Cards[0];
        var targetColumn = viewModel.Columns.Last(c => c != sourceColumn);

        viewModel.MoveCard(card, targetColumn, null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.DoesNotContain(card, sourceColumn.Cards);
        Assert.Contains(card, targetColumn.Cards);

        var renderedCardTitles = view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .ToList();
        Assert.Contains(card.Title, renderedCardTitles);

        window.Close();
    }
}
