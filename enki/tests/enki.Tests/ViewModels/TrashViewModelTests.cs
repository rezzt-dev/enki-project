using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="TrashViewModel"/> against real (throwaway-directory) services — same
/// pattern as <see cref="Services.VaultServiceTests"/> — since it's a thin aggregation layer over
/// <see cref="VaultService"/>/<see cref="KanbanService"/>/<see cref="ReminderService"/> with no store of
/// its own (03-problemas-y-deuda-tecnica.md §8 flags it as untested).</summary>
public class TrashViewModelTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public TrashViewModelTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (TrashViewModel Trash, VaultService Vault, KanbanService Kanban, PolarTaskService Polar, ReminderService Reminders, SessionService Sessions) CreateViewModel()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var kanban = new KanbanService(_storageDirectory);
        var polar = new PolarTaskService(_storageDirectory);
        var reminders = new ReminderService(_storageDirectory);
        var notifications = new NotificationService(settings, _storageDirectory);
        var sessions = new SessionService(vault);
        var trash = new TrashViewModel(vault, kanban, polar, reminders, notifications, sessions);
        return (trash, vault, kanban, polar, reminders, sessions);
    }

    [Fact]
    public void RestoreTask_FromOtherBoardWithDeletedColumn_StaysInOwnerBoard()
    {
        var (trash, _, kanban, _, _, _) = CreateViewModel();
        var board = kanban.CreateBoard("Histórico");
        var card = new KanbanCard { Id = "other-trash", Title = "Restaurar" };
        board.TrashedCards.Add(new() { Card = card, ColumnId = "removed", ColumnTitle = "Antigua" });
        kanban.Save();
        trash.Reload();
        var row = Assert.Single(trash.Tasks);
        Assert.Contains("Histórico", row.ColumnTitle);
        trash.RestoreTaskCommand.Execute(row);
        Assert.Same(card, board.Columns[0].Cards.Single());
        Assert.Empty(board.TrashedCards);
        Assert.DoesNotContain(kanban.DefaultBoard.Columns.SelectMany(c => c.Cards), c => c.Id == card.Id);
    }

    [Fact]
    public void Reload_StartsEmpty_ForFreshVault()
    {
        var (trash, _, _, _, _, _) = CreateViewModel();

        Assert.True(trash.IsEmpty);
        Assert.True(trash.IsTasksEmpty);
        Assert.True(trash.IsFilesEmpty);
        Assert.True(trash.IsRemindersEmpty);
        Assert.True(trash.IsSessionsEmpty);
        Assert.True(trash.IsFullyEmpty);
        Assert.Equal(0, trash.TotalCount);
    }

    [Fact]
    public void Reload_PopulatesTrashedNote()
    {
        var (trash, vault, _, _, _, _) = CreateViewModel();
        var path = Path.Combine(_vaultPath, "Nota.md");
        File.WriteAllText(path, "# Nota");
        var note = vault.GetNotes().Single();

        vault.DeleteNote(note);
        trash.Reload();

        Assert.False(trash.IsEmpty);
        Assert.Single(trash.Notes);
    }

    [Fact]
    public void TotalCount_ReflectsCombinedItemsAcrossSections_AndUpdatesAfterRestore()
    {
        var (trash, vault, _, _, _, _) = CreateViewModel();
        var path = Path.Combine(_vaultPath, "Nota.md");
        File.WriteAllText(path, "# Nota");
        var note = vault.GetNotes().Single();
        vault.DeleteNote(note);
        trash.Reload();

        Assert.Equal(1, trash.TotalCount);

        trash.RestoreCommand.Execute(trash.Notes.Single());

        Assert.Equal(0, trash.TotalCount);
    }

    [Fact]
    public void RestoreCommand_RestoresNote_AndReloads()
    {
        var (trash, vault, _, _, _, _) = CreateViewModel();
        var path = Path.Combine(_vaultPath, "Nota.md");
        File.WriteAllText(path, "# Nota");
        vault.DeleteNote(vault.GetNotes().Single());
        trash.Reload();
        var trashedNote = trash.Notes.Single();

        trash.RestoreCommand.Execute(trashedNote);

        Assert.True(trash.IsEmpty);
        Assert.Single(vault.GetNotes());
    }

    /// <summary>Fase 3 de docs/analisis-proyecto/roadmap.md: el borrado permanente individual pide
    /// confirmación igual que "vaciar papelera" — el comando público solo abre el diálogo.</summary>
    [Fact]
    public void RequestDeleteForever_OpensDialog_WithoutDeleting()
    {
        var (trash, vault, _, _, _, _) = CreateViewModel();
        var path = Path.Combine(_vaultPath, "Nota.md");
        File.WriteAllText(path, "# Nota");
        vault.DeleteNote(vault.GetNotes().Single());
        trash.Reload();
        var trashedNote = trash.Notes.Single();

        trash.RequestDeleteForeverCommand.Execute(trashedNote);

        Assert.Same(trashedNote, trash.PendingPermanentDelete);
        Assert.Single(vault.GetTrashedNotes());
    }

    [Fact]
    public void ConfirmPermanentDelete_ForNote_RemovesItPermanently()
    {
        var (trash, vault, _, _, _, _) = CreateViewModel();
        var path = Path.Combine(_vaultPath, "Nota.md");
        File.WriteAllText(path, "# Nota");
        vault.DeleteNote(vault.GetNotes().Single());
        trash.Reload();
        var trashedNote = trash.Notes.Single();

        trash.RequestDeleteForeverCommand.Execute(trashedNote);
        trash.ConfirmPermanentDeleteCommand.Execute(null);

        Assert.Null(trash.PendingPermanentDelete);
        Assert.True(trash.IsEmpty);
        Assert.Empty(vault.GetTrashedNotes());
    }

    [Fact]
    public void CancelPermanentDelete_ClearsPending_WithoutDeleting()
    {
        var (trash, vault, _, _, _, _) = CreateViewModel();
        var path = Path.Combine(_vaultPath, "Nota.md");
        File.WriteAllText(path, "# Nota");
        vault.DeleteNote(vault.GetNotes().Single());
        trash.Reload();
        var trashedNote = trash.Notes.Single();

        trash.RequestDeleteForeverCommand.Execute(trashedNote);
        trash.CancelPermanentDeleteCommand.Execute(null);

        Assert.Null(trash.PendingPermanentDelete);
        Assert.Single(vault.GetTrashedNotes());
    }

    [Fact]
    public void RestoreTaskCommand_MovesCardBackToItsOriginalColumn()
    {
        var (trash, _, kanban, _, _, _) = CreateViewModel();
        var column = kanban.DefaultBoard.Columns[0];
        var card = new KanbanCard { Id = "trashed-card", Title = "Tarjeta" };
        kanban.DefaultBoard.TrashedCards.Add(new KanbanTrashedCard
        {
            Card = card,
            ColumnId = column.Id,
            ColumnTitle = column.Title
        });
        trash.Reload();
        var trashedTask = trash.Tasks.Single();

        trash.RestoreTaskCommand.Execute(trashedTask);

        Assert.True(trash.IsTasksEmpty);
        Assert.Contains(column.Cards, c => c.Id == "trashed-card");
    }

    /// <summary>Restaurar significa "vuelve al tablero": una tarjeta que estaba archivada cuando se borró
    /// (docs/agent-docs/kanban-tareas-archivadas.md) pierde esa marca al restaurarla, porque si no
    /// aparecería en su columna sin que el tablero llegara a dibujarla.</summary>
    [Fact]
    public void RestoreTaskCommand_ClearsTheArchivedMark()
    {
        var (trash, _, kanban, _, _, _) = CreateViewModel();
        var column = kanban.DefaultBoard.Columns[0];
        var card = new KanbanCard
        {
            Id = "archived-card",
            Title = "Tarjeta archivada",
            ArchivedAtUtc = DateTime.UtcNow.AddDays(-3),
            CompletedAtUtc = DateTime.UtcNow.AddDays(-3)
        };
        kanban.DefaultBoard.TrashedCards.Add(new KanbanTrashedCard
        {
            Card = card,
            ColumnId = column.Id,
            ColumnTitle = column.Title
        });
        trash.Reload();

        trash.RestoreTaskCommand.Execute(trash.Tasks.Single());

        Assert.Contains(column.Cards, c => c.Id == "archived-card");
        Assert.False(card.IsArchived);
        Assert.NotNull(card.CompletedAtUtc);
    }

    [Fact]
    public void RestoreTaskCommand_InsertsCardAtOriginalIndex()
    {
        var (trash, _, kanban, _, _, _) = CreateViewModel();
        var column = kanban.DefaultBoard.Columns[0];
        column.Cards.Clear();
        column.Cards.Add(new KanbanCard { Id = "existing", Title = "Existente" });
        var card = new KanbanCard { Id = "trashed-card", Title = "Tarjeta" };
        kanban.DefaultBoard.TrashedCards.Add(new KanbanTrashedCard
        {
            Card = card,
            ColumnId = column.Id,
            ColumnTitle = column.Title,
            CardIndex = 0
        });
        trash.Reload();
        var trashedTask = trash.Tasks.Single();

        trash.RestoreTaskCommand.Execute(trashedTask);

        Assert.Equal("trashed-card", column.Cards[0].Id);
        Assert.Equal("existing", column.Cards[1].Id);
    }

    /// <summary>Fase 3 de docs/analisis-proyecto/roadmap.md (análisis §4): antes la tarjeta salía de
    /// <see cref="KanbanBoard.TrashedCards"/> ANTES de resolver la columna destino, así que con un tablero
    /// sin columnas quedaba fuera de ambas listas — perdida en silencio. Ahora permanece en la papelera,
    /// recuperable, y el usuario recibe el motivo por notificación.</summary>
    [Fact]
    public void RestoreTaskCommand_WithNoColumnsOnBoard_LeavesCardInTrash_AndNotifies()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var kanban = new KanbanService(_storageDirectory);
        var notifications = new NotificationService(settings, _storageDirectory);
        var trash = new TrashViewModel(
            vault, kanban, new PolarTaskService(_storageDirectory), new ReminderService(_storageDirectory),
            notifications, new SessionService(vault));

        kanban.DefaultBoard.Columns.Clear();
        var card = new KanbanCard { Id = "stuck", Title = "Sin destino" };
        kanban.DefaultBoard.TrashedCards.Add(new KanbanTrashedCard
        {
            Card = card,
            ColumnId = "columna-que-ya-no-existe",
            ColumnTitle = "Antigua"
        });
        trash.Reload();
        var trashedTask = trash.Tasks.Single();

        trash.RestoreTaskCommand.Execute(trashedTask);

        Assert.Contains(kanban.DefaultBoard.TrashedCards, t => t.Card.Id == "stuck");
        Assert.False(trash.IsTasksEmpty);
        Assert.Single(notifications.Notifications);
    }

    [Fact]
    public void ConfirmPermanentDelete_ForTask_RemovesFromTrashedCards()
    {
        var (trash, _, kanban, _, _, _) = CreateViewModel();
        var column = kanban.DefaultBoard.Columns[0];
        kanban.DefaultBoard.TrashedCards.Add(new KanbanTrashedCard
        {
            Card = new KanbanCard { Id = "gone", Title = "Se pierde" },
            ColumnId = column.Id,
            ColumnTitle = column.Title
        });
        trash.Reload();
        var trashedTask = trash.Tasks.Single();

        trash.RequestDeleteTaskForeverCommand.Execute(trashedTask);
        trash.ConfirmPermanentDeleteCommand.Execute(null);

        Assert.True(trash.IsTasksEmpty);
        Assert.Empty(kanban.DefaultBoard.TrashedCards);
    }

    // ===================== Categoría Tareas Polar (análisis alfa-3 §4.3) =====================

    [Fact]
    public void Reload_PopulatesTrashedPolarListsAndTasks()
    {
        var (trash, _, _, polar, _, _) = CreateViewModel();
        var list = polar.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var task = polar.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        polar.DeleteTask(task);
        var otherList = polar.CreateList("Otra lista", "ic_list", "#000", isDependencyChain: false);
        polar.DeleteList(otherList);

        trash.Reload();

        Assert.False(trash.IsPolarTasksEmpty);
        Assert.Contains(trash.PolarTasks, t => t.Task.Id == task.Id);
        Assert.Contains(trash.PolarLists, l => l.List.Id == otherList.Id);
    }

    [Fact]
    public void RestorePolarListCommand_UndeletesTheList()
    {
        var (trash, _, _, polar, _, _) = CreateViewModel();
        var list = polar.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        polar.DeleteList(list);
        trash.Reload();
        var trashedList = trash.PolarLists.Single();

        trash.RestorePolarListCommand.Execute(trashedList);

        Assert.True(trash.IsPolarTasksEmpty);
        Assert.False(polar.Store.Lists.Single(l => l.Id == list.Id).IsDeleted);
    }

    [Fact]
    public void RestorePolarTaskCommand_UndeletesTheTask()
    {
        var (trash, _, _, polar, _, _) = CreateViewModel();
        var list = polar.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var task = polar.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        polar.DeleteTask(task);
        trash.Reload();
        var trashedTask = trash.PolarTasks.Single();

        trash.RestorePolarTaskCommand.Execute(trashedTask);

        Assert.True(trash.IsPolarTasksEmpty);
        Assert.False(polar.Store.Tasks.Single(t => t.Id == task.Id).IsDeleted);
    }

    [Fact]
    public void RequestDeletePolarTaskForever_OpensDialog_WithoutDeleting()
    {
        var (trash, _, _, polar, _, _) = CreateViewModel();
        var list = polar.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var task = polar.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        polar.DeleteTask(task);
        trash.Reload();
        var trashedTask = trash.PolarTasks.Single();

        trash.RequestDeletePolarTaskForeverCommand.Execute(trashedTask);

        Assert.Same(trashedTask, trash.PendingPermanentDelete);
        Assert.Single(polar.Store.Tasks, t => t.IsDeleted);
    }

    [Fact]
    public void ConfirmPermanentDelete_ForPolarTask_RemovesItPermanently()
    {
        var (trash, _, _, polar, _, _) = CreateViewModel();
        var list = polar.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var task = polar.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        polar.DeleteTask(task);
        trash.Reload();
        var trashedTask = trash.PolarTasks.Single();

        trash.RequestDeletePolarTaskForeverCommand.Execute(trashedTask);
        trash.ConfirmPermanentDeleteCommand.Execute(null);

        Assert.Null(trash.PendingPermanentDelete);
        Assert.True(trash.IsPolarTasksEmpty);
        Assert.DoesNotContain(polar.Store.Tasks, t => t.Id == task.Id);
    }

    [Fact]
    public void ConfirmPermanentDelete_ForPolarList_RemovesListAndItsTasksPermanently()
    {
        var (trash, _, _, polar, _, _) = CreateViewModel();
        var list = polar.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        polar.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        polar.DeleteList(list);
        trash.Reload();
        var trashedList = trash.PolarLists.Single();

        trash.RequestDeletePolarListForeverCommand.Execute(trashedList);
        trash.ConfirmPermanentDeleteCommand.Execute(null);

        Assert.Null(trash.PendingPermanentDelete);
        Assert.True(trash.IsPolarTasksEmpty);
        Assert.DoesNotContain(polar.Store.Lists, l => l.Id == list.Id);
        Assert.Empty(polar.Store.Tasks);
    }

    [Fact]
    public void ConfirmEmpty_WithPolarTasksTarget_PurgesOnlyPolarTasks()
    {
        var (trash, vault, kanban, polar, _, _) = CreateViewModel();
        File.WriteAllText(Path.Combine(_vaultPath, "Nota.md"), "# Nota");
        vault.DeleteNote(vault.GetNotes().Single());
        var column = kanban.DefaultBoard.Columns[0];
        kanban.DefaultBoard.TrashedCards.Add(new KanbanTrashedCard
        {
            Card = new KanbanCard { Id = "t1", Title = "T" },
            ColumnId = column.Id,
            ColumnTitle = column.Title
        });
        var list = polar.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        polar.DeleteList(list);
        trash.Reload();

        trash.RequestEmptyPolarTasksCommand.Execute(null);
        trash.ConfirmEmptyCommand.Execute(null);

        Assert.True(trash.IsPolarTasksEmpty);
        Assert.DoesNotContain(polar.Store.Lists, l => l.Id == list.Id);
        Assert.Single(vault.GetTrashedNotes());
        Assert.Single(kanban.DefaultBoard.TrashedCards);
        Assert.False(trash.IsFullyEmpty);
    }

    [Fact]
    public void ConfirmEmpty_WithAllTarget_PurgesPolarTasksToo()
    {
        var (trash, _, _, polar, _, _) = CreateViewModel();
        var list = polar.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        polar.DeleteList(list);
        trash.Reload();

        trash.RequestEmptyTrashCommand.Execute(null);
        trash.ConfirmEmptyCommand.Execute(null);

        Assert.True(trash.IsFullyEmpty);
        Assert.DoesNotContain(polar.Store.Lists, l => l.Id == list.Id);
    }

    [Fact]
    public void PolarTasksCategory_AffectsIsFullyEmpty()
    {
        var (trash, _, _, polar, _, _) = CreateViewModel();
        var list = polar.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        polar.DeleteList(list);

        trash.Reload();

        Assert.False(trash.IsPolarTasksEmpty);
        Assert.False(trash.IsFullyEmpty);
    }

    [Fact]
    public void RestoreReminderCommand_ClearsTombstone()
    {
        var (trash, _, _, _, reminders, _) = CreateViewModel();
        var reminder = reminders.Create("Recordatorio", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");
        reminders.Delete(reminder.Id);
        trash.Reload();
        var trashedReminder = trash.Reminders.Single();

        trash.RestoreReminderCommand.Execute(trashedReminder);

        Assert.True(trash.IsRemindersEmpty);
        Assert.Contains(reminders.ActiveReminders, r => r.Id == reminder.Id);
    }

    // ===================== Categoría Sesiones (Fase 3, top 10 #10 del análisis) =====================

    [Fact]
    public void Reload_PopulatesTrashedSession_WithItsNoteCount()
    {
        var (trash, _, _, _, _, sessions) = CreateViewModel();
        var session = sessions.Create("Descartable");
        File.WriteAllText(Path.Combine(session.RootPath, "Nota.md"), "# Nota");
        sessions.Delete(session);

        trash.Reload();

        Assert.False(trash.IsSessionsEmpty);
        var card = trash.Sessions.Single();
        Assert.Equal("Descartable", card.Name);
        Assert.Equal(1, card.Session.NoteCount);
        Assert.Equal(1, trash.TotalCount);
    }

    [Fact]
    public void RestoreSessionCommand_MovesFolderBackToSessions_KeepingItsNotes()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var sessionsChangedCount = 0;
        var trash = new TrashViewModel(
            vault, new KanbanService(_storageDirectory), new PolarTaskService(_storageDirectory),
            new ReminderService(_storageDirectory), new NotificationService(settings, _storageDirectory), sessions,
            onSessionsChanged: () => sessionsChangedCount++);

        var session = sessions.Create("Recuperable");
        File.WriteAllText(Path.Combine(session.RootPath, "Nota.md"), "# Nota");
        sessions.Delete(session);
        trash.Reload();

        trash.RestoreSessionCommand.Execute(trash.Sessions.Single());

        Assert.True(trash.IsSessionsEmpty);
        var restored = sessions.List().Single(s => s.Name == "Recuperable");
        Assert.True(File.Exists(Path.Combine(restored.RootPath, "Nota.md")));
        Assert.Equal(1, sessionsChangedCount);
    }

    [Fact]
    public void ConfirmPermanentDelete_ForSession_RemovesFolderFromDisk()
    {
        var (trash, _, _, _, _, sessions) = CreateViewModel();
        var session = sessions.Create("Purgable");
        File.WriteAllText(Path.Combine(session.RootPath, "Nota.md"), "# Nota");
        sessions.Delete(session);
        trash.Reload();
        var trashedSession = trash.Sessions.Single();

        trash.RequestDeleteSessionForeverCommand.Execute(trashedSession);
        trash.ConfirmPermanentDeleteCommand.Execute(null);

        Assert.True(trash.IsSessionsEmpty);
        Assert.Empty(sessions.ListTrashedSessions());
        Assert.False(Directory.Exists(trashedSession.Session.RootPath));
    }

    [Fact]
    public void ConfirmEmpty_WithSessionsTarget_PurgesOnlySessions()
    {
        var (trash, vault, kanban, _, _, sessions) = CreateViewModel();
        File.WriteAllText(Path.Combine(_vaultPath, "Nota.md"), "# Nota");
        vault.DeleteNote(vault.GetNotes().Single());
        var column = kanban.DefaultBoard.Columns[0];
        kanban.DefaultBoard.TrashedCards.Add(new KanbanTrashedCard
        {
            Card = new KanbanCard { Id = "t1", Title = "T" },
            ColumnId = column.Id,
            ColumnTitle = column.Title
        });
        var session = sessions.Create("Purgable");
        sessions.Delete(session);
        trash.Reload();

        trash.RequestEmptySessionsCommand.Execute(null);
        trash.ConfirmEmptyCommand.Execute(null);

        Assert.True(trash.IsSessionsEmpty);
        Assert.Empty(sessions.ListTrashedSessions());
        Assert.Single(vault.GetTrashedNotes());
        Assert.Single(kanban.DefaultBoard.TrashedCards);
        Assert.False(trash.IsFullyEmpty);
    }

    [Fact]
    public void ConfirmEmpty_WithAllTarget_PurgesNotesTasksFilesAndReminders()
    {
        var (trash, vault, kanban, _, reminders, _) = CreateViewModel();
        File.WriteAllText(Path.Combine(_vaultPath, "Nota.md"), "# Nota");
        vault.DeleteNote(vault.GetNotes().Single());
        var column = kanban.DefaultBoard.Columns[0];
        kanban.DefaultBoard.TrashedCards.Add(new KanbanTrashedCard
        {
            Card = new KanbanCard { Id = "t1", Title = "T" },
            ColumnId = column.Id,
            ColumnTitle = column.Title
        });
        var reminder = reminders.Create("R", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");
        reminders.Delete(reminder.Id);
        trash.Reload();

        trash.RequestEmptyTrashCommand.Execute(null);
        trash.ConfirmEmptyCommand.Execute(null);

        Assert.True(trash.IsFullyEmpty);
        Assert.Empty(vault.GetTrashedNotes());
        Assert.Empty(kanban.DefaultBoard.TrashedCards);
        Assert.Empty(reminders.DeletedReminders);
    }

    [Fact]
    public void ConfirmEmpty_WithRemindersTarget_PurgesOnlyReminders()
    {
        var (trash, vault, kanban, _, reminders, _) = CreateViewModel();
        File.WriteAllText(Path.Combine(_vaultPath, "Nota.md"), "# Nota");
        vault.DeleteNote(vault.GetNotes().Single());
        var column = kanban.DefaultBoard.Columns[0];
        kanban.DefaultBoard.TrashedCards.Add(new KanbanTrashedCard
        {
            Card = new KanbanCard { Id = "t1", Title = "T" },
            ColumnId = column.Id,
            ColumnTitle = column.Title
        });
        var reminder = reminders.Create("R", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");
        reminders.Delete(reminder.Id);
        trash.Reload();

        trash.RequestEmptyRemindersCommand.Execute(null);
        trash.ConfirmEmptyCommand.Execute(null);

        Assert.True(trash.IsRemindersEmpty);
        Assert.Empty(reminders.DeletedReminders);
        Assert.Single(vault.GetTrashedNotes());
        Assert.Single(kanban.DefaultBoard.TrashedCards);
        Assert.False(trash.IsFullyEmpty);
    }

    [Fact]
    public void CancelEmpty_ClearsPendingTarget_WithoutDeletingAnything()
    {
        var (trash, vault, _, _, _, _) = CreateViewModel();
        File.WriteAllText(Path.Combine(_vaultPath, "Nota.md"), "# Nota");
        vault.DeleteNote(vault.GetNotes().Single());
        trash.Reload();

        trash.RequestEmptyDocumentsCommand.Execute(null);
        trash.CancelEmptyCommand.Execute(null);

        Assert.Null(trash.PendingEmptyTarget);
        Assert.False(trash.IsEmpty);
    }
}
