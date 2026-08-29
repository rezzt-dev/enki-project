using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Controls.Primitives;
using enki.Localization;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="KanbanViewModel"/> against a real (throwaway-directory) <see cref="KanbanService"/>
/// — same <c>storageDirectory</c> pattern as <see cref="Services.KanbanServiceTests"/>, wired through the full
/// Polar chain (<see cref="PolarAuthService"/>/<see cref="PolarTaskService"/>/<see cref="PolarSyncService"/>)
/// since the constructor requires a real <see cref="PolarTaskManagementViewModel"/>.</summary>
public class KanbanViewModelTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private KanbanViewModel CreateViewModel(out KanbanService kanbanService)
    {
        var settingsService = new SettingsService(_storageDirectory);
        var authService = new PolarAuthService(settingsService);
        var restClient = new SupabaseRestClient(authService);
        var taskService = new PolarTaskService(_storageDirectory);
        var reminderService = new ReminderService(_storageDirectory);
        var syncService = new PolarSyncService(authService, restClient, taskService, reminderService, settingsService);
        var taskManagement = new PolarTaskManagementViewModel(taskService, syncService);
        kanbanService = new KanbanService(_storageDirectory);
        return new KanbanViewModel(kanbanService, authService, taskManagement);
    }

    /// <summary>Variante con <see cref="NotificationService"/> real para verificar los avisos que la
    /// Fase 3 añadió al borrado de columnas (docs/analisis-proyecto/roadmap.md).</summary>
    private KanbanViewModel CreateViewModel(out KanbanService kanbanService, out NotificationService notificationService)
    {
        var settingsService = new SettingsService(_storageDirectory);
        var authService = new PolarAuthService(settingsService);
        var restClient = new SupabaseRestClient(authService);
        var taskService = new PolarTaskService(_storageDirectory);
        var reminderService = new ReminderService(_storageDirectory);
        var syncService = new PolarSyncService(authService, restClient, taskService, reminderService, settingsService);
        var taskManagement = new PolarTaskManagementViewModel(taskService, syncService);
        kanbanService = new KanbanService(_storageDirectory);
        notificationService = new NotificationService(settingsService, _storageDirectory);
        return new KanbanViewModel(kanbanService, authService, taskManagement, notificationService: notificationService);
    }

    [Fact]
    public void Reload_PopulatesColumns_FromSeedBoard()
    {
        var vm = CreateViewModel(out var kanbanService);

        Assert.Equal(kanbanService.Board.Columns.Count, vm.Columns.Count);
        Assert.Equal(kanbanService.Board.Name, vm.BoardName);
        Assert.Equal(vm.Columns.Sum(c => c.Cards.Count), vm.TotalCards);
    }

    [Fact]
    public void AddColumn_AppendsNonCoreColumn_AndPersists()
    {
        var vm = CreateViewModel(out var kanbanService);
        var before = vm.Columns.Count;

        vm.AddColumnCommand.Execute(null);

        Assert.Equal(before + 1, vm.Columns.Count);
        Assert.False(vm.Columns[^1].IsCore);
        Assert.Equal(before + 1, kanbanService.Board.Columns.Count);
    }

    [Fact]
    public void RequestDeleteColumn_OnCoreColumn_IsNoOp()
    {
        var vm = CreateViewModel(out var kanbanService);
        var core = vm.Columns.First(c => c.IsCore);
        var before = vm.Columns.Count;

        vm.RequestDeleteColumnCommand.Execute(core);

        Assert.Equal(before, vm.Columns.Count);
        Assert.Equal(before, kanbanService.Board.Columns.Count);
        Assert.Null(vm.ColumnPendingDelete);
    }

    /// <summary>Fase 3 de docs/analisis-proyecto/roadmap.md: borrar una columna ahora pide confirmación —
    /// el comando público solo abre el diálogo; la columna y sus tarjetas no se tocan hasta confirmar.</summary>
    [Fact]
    public void RequestDeleteColumn_OnNonCoreColumn_OpensDialog_WithoutTouchingAnything()
    {
        var vm = CreateViewModel(out var kanbanService);
        vm.AddColumnCommand.Execute(null);
        var added = vm.Columns[^1];
        var before = vm.Columns.Count;

        vm.RequestDeleteColumnCommand.Execute(added);

        Assert.Same(added, vm.ColumnPendingDelete);
        Assert.Equal(before, vm.Columns.Count);
        Assert.Contains(kanbanService.Board.Columns, c => c.Id == added.Id);
        Assert.Empty(kanbanService.Board.TrashedCards);
    }

    [Fact]
    public void CancelDeleteColumn_ClearsPending_WithoutDeleting()
    {
        var vm = CreateViewModel(out var kanbanService);
        vm.AddColumnCommand.Execute(null);
        var added = vm.Columns[^1];

        vm.RequestDeleteColumnCommand.Execute(added);
        vm.CancelDeleteColumnCommand.Execute(null);

        Assert.Null(vm.ColumnPendingDelete);
        Assert.Contains(kanbanService.Board.Columns, c => c.Id == added.Id);
    }

    [Fact]
    public void ConfirmDeleteColumn_OnEmptyColumn_RemovesIt()
    {
        var vm = CreateViewModel(out var kanbanService);
        vm.AddColumnCommand.Execute(null);
        var added = vm.Columns[^1];
        var before = vm.Columns.Count;

        vm.RequestDeleteColumnCommand.Execute(added);
        vm.ConfirmDeleteColumnCommand.Execute(null);

        Assert.Null(vm.ColumnPendingDelete);
        Assert.Equal(before - 1, vm.Columns.Count);
        Assert.DoesNotContain(kanbanService.Board.Columns, c => c.Id == added.Id);
    }

    /// <summary>Top 10 #4 del análisis (docs/analisis-proyecto/analisis-deficiencias-proyecto.md §4): antes
    /// las tarjetas de una columna borrada se perdían para siempre. Ahora pasan una a una a
    /// <see cref="KanbanBoard.TrashedCards"/> — recuperables desde la Papelera — conservando su posición.</summary>
    [Fact]
    public void ConfirmDeleteColumn_WithCards_MovesThemAllToTrashedCards_PreservingIndex()
    {
        var vm = CreateViewModel(out var kanbanService);
        vm.AddColumnCommand.Execute(null);
        var added = vm.Columns[^1];
        var first = new KanbanCardViewModel(new KanbanCard { Id = "c1", Title = "Primera" });
        var second = new KanbanCardViewModel(new KanbanCard { Id = "c2", Title = "Segunda" });
        added.Cards.Add(first);
        added.Cards.Add(second);
        added.Column.Cards.Add(first.Card);
        added.Column.Cards.Add(second.Card);

        vm.RequestDeleteColumnCommand.Execute(added);
        vm.ConfirmDeleteColumnCommand.Execute(null);

        Assert.DoesNotContain(kanbanService.Board.Columns, c => c.Id == added.Id);
        var trashedFirst = kanbanService.Board.TrashedCards.Single(t => t.Card.Id == "c1");
        var trashedSecond = kanbanService.Board.TrashedCards.Single(t => t.Card.Id == "c2");
        Assert.Equal(added.Id, trashedFirst.ColumnId);
        Assert.Equal(added.Title, trashedFirst.ColumnTitle);
        Assert.Equal(0, trashedFirst.CardIndex);
        Assert.Equal(1, trashedSecond.CardIndex);
    }

    [Fact]
    public void ConfirmDeleteColumn_WithCards_PushesNotification()
    {
        var vm = CreateViewModel(out var kanbanService, out var notificationService);
        vm.AddColumnCommand.Execute(null);
        var added = vm.Columns[^1];
        added.Column.Cards.Add(new KanbanCard { Id = "c1", Title = "Tarjeta" });

        vm.RequestDeleteColumnCommand.Execute(added);
        vm.ConfirmDeleteColumnCommand.Execute(null);

        Assert.Single(notificationService.Notifications);
    }

    /// <summary>Con "preguntar antes de borrar" desactivado, el borrado de columna salta directo sin abrir
    /// el diálogo — mismo criterio que <see cref="KanbanViewModel.RequestDeleteCard"/> (y las tarjetas
    /// siguen yendo a la papelera).</summary>
    [Fact]
    public void RequestDeleteColumn_WithConfirmBeforeDeleteOff_DeletesImmediatelyWithoutOpeningDialog()
    {
        var settingsService = new SettingsService(_storageDirectory);
        settingsService.Settings.ConfirmBeforeDelete = false;
        var authService = new PolarAuthService(settingsService);
        var restClient = new SupabaseRestClient(authService);
        var taskService = new PolarTaskService(_storageDirectory);
        var reminderService = new ReminderService(_storageDirectory);
        var syncService = new PolarSyncService(authService, restClient, taskService, reminderService, settingsService);
        var taskManagement = new PolarTaskManagementViewModel(taskService, syncService, settingsService);
        var kanbanService = new KanbanService(_storageDirectory);
        var vm = new KanbanViewModel(kanbanService, authService, taskManagement, settingsService: settingsService);

        vm.AddColumnCommand.Execute(null);
        var added = vm.Columns[^1];
        added.Column.Cards.Add(new KanbanCard { Id = "c1", Title = "Tarjeta" });
        var before = vm.Columns.Count;

        vm.RequestDeleteColumnCommand.Execute(added);

        Assert.Null(vm.ColumnPendingDelete);
        Assert.Equal(before - 1, vm.Columns.Count);
        Assert.DoesNotContain(kanbanService.Board.Columns, c => c.Id == added.Id);
        Assert.Contains(kanbanService.Board.TrashedCards, t => t.Card.Id == "c1");
    }

    [Fact]
    public void MoveCard_BetweenColumns_UpdatesBothViewModelAndModel()
    {
        var vm = CreateViewModel(out var kanbanService);
        var source = vm.Columns[0];
        var target = vm.Columns[1];
        var card = new KanbanCardViewModel(new KanbanCard { Id = "moved", Title = "Movida" });
        source.Cards.Add(card);
        source.Column.Cards.Add(card.Card);

        vm.MoveCard(card, target, beforeCard: null);

        Assert.DoesNotContain(card, source.Cards);
        Assert.Contains(card, target.Cards);
        Assert.DoesNotContain(source.Column.Cards, c => c.Id == "moved");
        Assert.Contains(target.Column.Cards, c => c.Id == "moved");
    }

    /// <summary>Fase 7 de docs/analisis-proyecto/roadmap.md: el editor de tarjeta expone un selector de
    /// columna destino como vía alternativa al arrastre — cambiarlo y guardar debe mover la tarjeta igual
    /// que el drag&amp;drop, sin tocarla si el usuario no cambia la selección.</summary>
    [Fact]
    public void SaveCardEditor_WithDifferentSelectedColumn_MovesCardLikeDragAndDrop()
    {
        var vm = CreateViewModel(out var kanbanService);
        var source = vm.Columns[0];
        var target = vm.Columns[1];
        var card = new KanbanCardViewModel(new KanbanCard { Id = "moved", Title = "Movida" });
        source.Cards.Add(card);
        source.Column.Cards.Add(card.Card);

        vm.OpenEditCardCommand.Execute(card);
        vm.CardEditor!.SelectedColumn = target;
        vm.SaveCardEditorCommand.Execute(null);

        Assert.DoesNotContain(card, source.Cards);
        Assert.Contains(card, target.Cards);
        Assert.DoesNotContain(source.Column.Cards, c => c.Id == "moved");
        Assert.Contains(target.Column.Cards, c => c.Id == "moved");
        Assert.Null(vm.CardEditor);
    }

    [Fact]
    public void SaveCardEditor_WithUnchangedSelectedColumn_LeavesCardInPlace()
    {
        var vm = CreateViewModel(out var kanbanService);
        var source = vm.Columns[0];
        var card = new KanbanCardViewModel(new KanbanCard { Id = "kept", Title = "Se queda" });
        source.Cards.Add(card);
        source.Column.Cards.Add(card.Card);

        vm.OpenEditCardCommand.Execute(card);
        vm.SaveCardEditorCommand.Execute(null);

        Assert.Contains(card, source.Cards);
        Assert.Contains(source.Column.Cards, c => c.Id == "kept");
    }

    [Fact]
    public void MoveCard_ToDoneCoreColumn_MarksAllSubtasksDone()
    {
        var vm = CreateViewModel(out _);
        var doneColumn = vm.Columns.First(c => c.Column.Role == CoreColumnRole.Done);
        var backlogColumn = vm.Columns.First(c => c.Column.Role == CoreColumnRole.Backlog);

        var card = new KanbanCard
        {
            Id = "with-subtasks",
            Title = "Con subtareas",
            Subtasks =
            [
                new KanbanSubtask { Id = "s1", Title = "a", IsDone = false },
                new KanbanSubtask { Id = "s2", Title = "b", IsDone = false }
            ]
        };
        var cardVm = new KanbanCardViewModel(card);
        backlogColumn.Cards.Add(cardVm);
        backlogColumn.Column.Cards.Add(card);

        vm.MoveCard(cardVm, doneColumn, beforeCard: null);

        Assert.All(card.Subtasks, s => Assert.True(s.IsDone));
    }

    [Fact]
    public void MoveCard_ToBacklogCoreColumn_MarksAllSubtasksUndone()
    {
        var vm = CreateViewModel(out _);
        var doneColumn = vm.Columns.First(c => c.Column.Role == CoreColumnRole.Done);
        var backlogColumn = vm.Columns.First(c => c.Column.Role == CoreColumnRole.Backlog);

        var card = new KanbanCard
        {
            Id = "with-subtasks",
            Title = "Con subtareas",
            Subtasks =
            [
                new KanbanSubtask { Id = "s1", Title = "a", IsDone = true },
                new KanbanSubtask { Id = "s2", Title = "b", IsDone = true }
            ]
        };
        var cardVm = new KanbanCardViewModel(card);
        doneColumn.Cards.Add(cardVm);
        doneColumn.Column.Cards.Add(card);

        vm.MoveCard(cardVm, backlogColumn, beforeCard: null);

        Assert.All(card.Subtasks, s => Assert.False(s.IsDone));
    }

    [Fact]
    public void ConfirmDeleteCard_MovesCardToTrashedCards()
    {
        var vm = CreateViewModel(out var kanbanService);
        var column = vm.Columns[0];
        var card = new KanbanCardViewModel(new KanbanCard { Id = "to-trash", Title = "A borrar" });
        column.Cards.Add(card);
        column.Column.Cards.Add(card.Card);

        vm.RequestDeleteCardCommand.Execute(card);
        vm.ConfirmDeleteCardCommand.Execute(null);

        Assert.DoesNotContain(column.Cards, c => c.Id == "to-trash");
        Assert.Contains(kanbanService.Board.TrashedCards, t => t.Card.Id == "to-trash");
        Assert.Null(vm.CardPendingDelete);
    }

    [Fact]
    public void ConfirmDeleteCard_PreservesCardIndex()
    {
        var vm = CreateViewModel(out var kanbanService);
        var column = vm.Columns[0];
        column.Cards.Clear();
        column.Column.Cards.Clear();
        var first = new KanbanCardViewModel(new KanbanCard { Id = "first", Title = "Primera" });
        var second = new KanbanCardViewModel(new KanbanCard { Id = "second", Title = "Segunda" });
        column.Cards.Add(first);
        column.Cards.Add(second);
        column.Column.Cards.Add(first.Card);
        column.Column.Cards.Add(second.Card);

        vm.RequestDeleteCardCommand.Execute(second);
        vm.ConfirmDeleteCardCommand.Execute(null);

        var trashed = kanbanService.Board.TrashedCards.Single(t => t.Card.Id == "second");
        Assert.Equal(1, trashed.CardIndex);
    }

    [Fact]
    public void CancelDeleteCard_ClearsPendingWithoutTrashing()
    {
        var vm = CreateViewModel(out var kanbanService);
        var column = vm.Columns[0];
        var card = new KanbanCardViewModel(new KanbanCard { Id = "keep", Title = "Se queda" });
        column.Cards.Add(card);
        column.Column.Cards.Add(card.Card);

        vm.RequestDeleteCardCommand.Execute(card);
        vm.CancelDeleteCardCommand.Execute(null);

        Assert.Null(vm.CardPendingDelete);
        Assert.Contains(column.Cards, c => c.Id == "keep");
        Assert.DoesNotContain(kanbanService.Board.TrashedCards, t => t.Card.Id == "keep");
    }

    /// <summary>docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md §3 — con "preguntar antes de
    /// borrar" desactivado, RequestDeleteCard debe borrar directamente sin abrir el diálogo.</summary>
    [Fact]
    public void RequestDeleteCard_WithConfirmBeforeDeleteOff_DeletesImmediatelyWithoutOpeningDialog()
    {
        var settingsService = new SettingsService(_storageDirectory);
        settingsService.Settings.ConfirmBeforeDelete = false;
        var authService = new PolarAuthService(settingsService);
        var restClient = new SupabaseRestClient(authService);
        var taskService = new PolarTaskService(_storageDirectory);
        var reminderService = new ReminderService(_storageDirectory);
        var syncService = new PolarSyncService(authService, restClient, taskService, reminderService, settingsService);
        var taskManagement = new PolarTaskManagementViewModel(taskService, syncService, settingsService);
        var kanbanService = new KanbanService(_storageDirectory);
        var vm = new KanbanViewModel(kanbanService, authService, taskManagement, settingsService: settingsService);

        var column = vm.Columns[0];
        var card = new KanbanCardViewModel(new KanbanCard { Id = "instant", Title = "Sin confirmar" });
        column.Cards.Add(card);
        column.Column.Cards.Add(card.Card);

        vm.RequestDeleteCardCommand.Execute(card);

        Assert.Null(vm.CardPendingDelete);
        Assert.DoesNotContain(column.Cards, c => c.Id == "instant");
        Assert.Contains(kanbanService.Board.TrashedCards, t => t.Card.Id == "instant");
    }

    [Fact]
    public void OpenCardById_OpensDetail_ForExistingCard()
    {
        var vm = CreateViewModel(out _);
        var card = vm.Columns.SelectMany(c => c.Cards).First();

        vm.OpenCardById(card.Id);

        Assert.NotNull(vm.CardDetail);
    }

    [Fact]
    public void OpenCardById_UnknownId_DoesNothing()
    {
        var vm = CreateViewModel(out _);

        vm.OpenCardById("no-existe");

        Assert.Null(vm.CardDetail);
    }

    [Fact]
    public void OpenCardDetail_CarriesTheColumnTitle_SoTheDialogCanNameTheStateItIsIn()
    {
        var vm = CreateViewModel(out _);
        var column = vm.Columns.First(c => c.Cards.Count > 0);
        var card = column.Cards[0];

        vm.OpenCardById(card.Id);

        Assert.Equal(column.Title, vm.CardDetail!.ColumnTitle);
        Assert.True(vm.CardDetail.HasColumnTitle);
    }

    [Fact]
    public void EditCardFromDetail_ClosesTheDetail_AndOpensTheEditorOnTheSameCard()
    {
        var vm = CreateViewModel(out _);
        var card = vm.Columns.SelectMany(c => c.Cards).First();
        vm.OpenCardById(card.Id);

        vm.EditCardFromDetailCommand.Execute(null);

        Assert.Null(vm.CardDetail);
        Assert.NotNull(vm.CardEditor);
        Assert.Same(card, vm.CardEditor!.SourceCard);
    }

    [Fact]
    public void DeleteCardFromDetail_ClosesTheDetail_AndAsksForConfirmation()
    {
        var vm = CreateViewModel(out _);
        var card = vm.Columns.SelectMany(c => c.Cards).First();
        vm.OpenCardById(card.Id);

        vm.DeleteCardFromDetailCommand.Execute(null);

        Assert.Null(vm.CardDetail);
        Assert.Same(card, vm.CardPendingDelete);
    }

    // ===================== Filtros rápidos, resumen y estados vacíos =====================

    [Fact]
    public void ToggleQuickFilter_HighPriority_HidesEveryOtherCard_AndTogglingAgainRestoresThem()
    {
        var vm = CreateViewModel(out var kanbanService);
        var column = vm.Columns[0];
        column.Column.Cards.Clear();
        column.Cards.Clear();
        AddCard(vm, column, "alta", KanbanPriority.High);
        AddCard(vm, column, "media", KanbanPriority.Medium);

        vm.ToggleQuickFilterCommand.Execute(KanbanQuickFilter.HighPriority);

        Assert.Equal(KanbanQuickFilter.HighPriority, vm.QuickFilter);
        Assert.True(vm.IsHighPriorityFilterActive);
        Assert.True(column.Cards.Single(c => c.Id == "alta").IsVisible);
        Assert.False(column.Cards.Single(c => c.Id == "media").IsVisible);

        vm.ToggleQuickFilterCommand.Execute(KanbanQuickFilter.HighPriority);

        Assert.Equal(KanbanQuickFilter.None, vm.QuickFilter);
        Assert.True(column.Cards.All(c => c.IsVisible));
        Assert.NotNull(kanbanService.Board);
    }

    [Fact]
    public void ClearFilters_ResetsSearch_TagsAndQuickFilter_InOneGo()
    {
        var vm = CreateViewModel(out _);
        var column = vm.Columns[0];
        AddCard(vm, column, "etiquetada", KanbanPriority.High, tag: "urgente");
        vm.Reload();

        vm.SearchText = "etiquet";
        vm.ToggleQuickFilterCommand.Execute(KanbanQuickFilter.HighPriority);
        vm.TagFilter.Items.First(i => i.Tag == "urgente").ToggleCommand.Execute(null);
        Assert.True(vm.HasActiveFilters);

        vm.ClearFiltersCommand.Execute(null);

        Assert.False(vm.HasActiveFilters);
        Assert.Equal(string.Empty, vm.SearchText);
        Assert.Equal(KanbanQuickFilter.None, vm.QuickFilter);
        Assert.DoesNotContain(vm.TagFilter.Items, i => i.IsSelected);
        Assert.True(vm.Columns.SelectMany(c => c.Cards).All(c => c.IsVisible));
    }

    [Fact]
    public void HasNoFilterMatches_IsTrue_OnlyWhenTheBoardHasCardsButNoneSurviveTheFilter()
    {
        var vm = CreateViewModel(out _);
        Assert.False(vm.IsBoardEmpty);
        Assert.False(vm.HasNoFilterMatches);

        vm.SearchText = "esto-no-existe-en-ninguna-tarjeta";

        Assert.True(vm.HasNoFilterMatches);

        vm.ClearFiltersCommand.Execute(null);

        Assert.False(vm.HasNoFilterMatches);
    }

    [Fact]
    public void BoardSummaryLabel_FallsBackToTheInstructions_OnlyWhileTheBoardIsEmpty()
    {
        var vm = CreateViewModel(out var kanbanService);

        foreach (var column in kanbanService.Board.Columns) column.Cards.Clear();
        vm.Reload();

        Assert.True(vm.IsBoardEmpty);
        Assert.Equal(LocalizationService.Instance["Kanban_Board_Subtitle"], vm.BoardSummaryLabel);
    }

    [Fact]
    public void BoardSummaryLabel_CountsColumnsAndCards_AndOnlyMentionsOverdueWhenThereIsAny()
    {
        var vm = CreateViewModel(out _);

        Assert.False(vm.IsBoardEmpty);
        Assert.Contains(vm.TotalColumns.ToString(CultureInfo.CurrentCulture), vm.BoardSummaryLabel, StringComparison.Ordinal);
        Assert.Contains(vm.TotalCards.ToString(CultureInfo.CurrentCulture), vm.BoardSummaryLabel, StringComparison.Ordinal);

        var overdueFragment = Plural.Get("Kanban_Summary_Overdue", vm.OverdueCount, vm.OverdueCount);
        Assert.Equal(vm.OverdueCount > 0, vm.BoardSummaryLabel.Contains(overdueFragment, StringComparison.Ordinal));
    }

    [Fact]
    public void QuickFilterChips_AreOnlyOffered_WhenThereIsSomethingToFilter()
    {
        var vm = CreateViewModel(out var kanbanService);

        foreach (var column in kanbanService.Board.Columns) column.Cards.Clear();
        vm.Reload();
        Assert.False(vm.HasHighPriorityCards);
        Assert.False(vm.HasOverdueCards);

        AddCard(vm, vm.Columns[0], "alta", KanbanPriority.High);
        vm.Reload();

        Assert.True(vm.HasHighPriorityCards);
    }

    [Fact]
    public void Columns_ShareTheAvailableWidth_WhileThereAreFewEnoughOfThem()
    {
        var vm = CreateViewModel(out _);

        Assert.Equal(3, vm.TotalColumns);
        Assert.True(vm.ColumnsShareAvailableWidth);
        // NaN = "sin ancho propio": lo decide el reparto del UniformGrid en la vista.
        Assert.True(double.IsNaN(vm.BoardColumnWidth));
        Assert.Equal(ScrollBarVisibility.Disabled, vm.BoardHorizontalScrollBarVisibility);
    }

    [Fact]
    public void Columns_FallBackToAFixedWidthAndHorizontalScroll_OnceThereAreTooManyToShare()
    {
        var vm = CreateViewModel(out var kanbanService);

        // El quinto reparto (1180/5 ≈ 236px) todavía entra; el sexto ya no.
        while (vm.TotalColumns < 5) vm.AddColumnCommand.Execute(null);
        Assert.True(vm.ColumnsShareAvailableWidth);
        Assert.True(double.IsNaN(vm.BoardColumnWidth));

        vm.AddColumnCommand.Execute(null);

        Assert.Equal(6, vm.TotalColumns);
        Assert.False(vm.ColumnsShareAvailableWidth);
        Assert.False(double.IsNaN(vm.BoardColumnWidth));
        Assert.True(vm.BoardColumnWidth > 0);
        Assert.Equal(ScrollBarVisibility.Auto, vm.BoardHorizontalScrollBarVisibility);
        Assert.NotNull(kanbanService.Board);
    }

    /// <summary>Añade una tarjeta a la vez al modelo y al viewmodel de la columna, que es lo que hace
    /// SaveCardEditor — las pruebas de arriba necesitan tarjetas con prioridad/etiquetas concretas sin
    /// pasar por el formulario completo.</summary>
    private static void AddCard(KanbanViewModel vm, KanbanColumnViewModel column, string id,
        KanbanPriority priority, string? tag = null)
    {
        var card = new KanbanCard { Id = id, Title = id, Priority = priority };
        if (tag is not null) card.Tags.Add(tag);

        column.Column.Cards.Add(card);
        column.Cards.Add(new KanbanCardViewModel(card));
        _ = vm;
    }
}
