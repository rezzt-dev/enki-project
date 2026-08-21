using System;
using System.IO;
using System.Linq;
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
}
