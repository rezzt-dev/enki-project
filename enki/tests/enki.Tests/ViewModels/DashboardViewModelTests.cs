using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="DashboardViewModel"/> against real (throwaway-directory) services, same
/// full-stack pattern as <see cref="KanbanViewModelTests"/> — needed here too since the constructor takes
/// a real <see cref="CalendarAggregationService"/> (03-problemas-y-deuda-tecnica.md §8 flags this
/// viewmodel as untested). Every navigation/action callback is a no-op unless a test needs to observe it.</summary>
public class DashboardViewModelTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public DashboardViewModelTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private sealed record Harness(
        DashboardViewModel Dashboard, VaultService Vault, KanbanService Kanban,
        ReminderService Reminders, NotificationService Notifications);

    private Harness CreateViewModel()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vault = new VaultService(settingsService);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessionService = new SessionService(vault);
        var kanbanService = new KanbanService(_storageDirectory);
        var authService = new PolarAuthService(settingsService);
        var taskService = new PolarTaskService(_storageDirectory);
        var reminderService = new ReminderService(_storageDirectory);
        var notificationService = new NotificationService(settingsService, _storageDirectory);
        var calendarAggregationService = new CalendarAggregationService(kanbanService, taskService, reminderService, authService);

        var dashboard = new DashboardViewModel(
            vault, sessionService, kanbanService, taskService, authService, notificationService, calendarAggregationService,
            openNote: _ => { }, newNote: () => { }, navigateToKanban: () => { }, navigateToSessions: () => { },
            navigateToNotes: () => { }, navigateToTrash: () => { }, calendarEnabled: true, navigateToCalendar: () => { },
            openReminders: () => { }, openNotifications: () => { }, openSettings: () => { }, showScrollbar: true);

        return new Harness(dashboard, vault, kanbanService, reminderService, notificationService);
    }

    [Fact]
    public void Reload_PopulatesVaultStats_ForFreshVault()
    {
        var h = CreateViewModel();

        Assert.Equal(_vaultPath, h.Dashboard.VaultPath);
        Assert.Equal(0, h.Dashboard.TotalNoteCount);
        Assert.False(h.Dashboard.HasFeaturedNote);
    }

    [Fact]
    public void Reload_PopulatesFeaturedNote_AndOthers_FromVaultNotes()
    {
        var h = CreateViewModel();
        File.WriteAllText(Path.Combine(_vaultPath, "Nota 1.md"), "# Nota 1");
        File.WriteAllText(Path.Combine(_vaultPath, "Nota 2.md"), "# Nota 2");

        h.Dashboard.Reload();

        Assert.Equal(2, h.Dashboard.TotalNoteCount);
        Assert.True(h.Dashboard.HasFeaturedNote);
    }

    [Fact]
    public void Reload_SplitsBoardCardsAcrossCoreColumns_ByRole()
    {
        var h = CreateViewModel();
        var backlogColumn = h.Kanban.Board.Columns.First(c => c.Role == CoreColumnRole.Backlog);
        backlogColumn.Cards.Add(new KanbanCard { Id = "p1", Title = "Pendiente" });

        h.Dashboard.Reload();

        Assert.True(h.Dashboard.PendingTaskCount >= 1);
        Assert.Contains(h.Dashboard.PendingTasks, c => c.Id == "p1");
    }

    [Fact]
    public void Reload_SplitsBoardCardsAcrossCoreColumns_ByRole_EvenWhenReordered()
    {
        var h = CreateViewModel();
        // Move the backlog column to the end of the board — the dashboard must still find it by Role.
        var backlogColumn = h.Kanban.Board.Columns.First(c => c.Role == CoreColumnRole.Backlog);
        h.Kanban.Board.Columns.Remove(backlogColumn);
        h.Kanban.Board.Columns.Add(backlogColumn);
        backlogColumn.Cards.Add(new KanbanCard { Id = "p1", Title = "Pendiente" });

        h.Dashboard.Reload();

        Assert.True(h.Dashboard.PendingTaskCount >= 1);
        Assert.Contains(h.Dashboard.PendingTasks, c => c.Id == "p1");
    }

    [Fact]
    public void Reload_BoardProgressPercent_IsZero_WhenBoardHasNoCards()
    {
        var h = CreateViewModel();
        foreach (var column in h.Kanban.Board.Columns) column.Cards.Clear();
        h.Kanban.Board.Columns[0].Cards.Clear();

        h.Dashboard.Reload();

        Assert.Equal(0, h.Dashboard.BoardTotalCount);
        Assert.Equal(0, h.Dashboard.BoardProgressPercent);
    }

    [Fact]
    public void Reload_IsPolarLinked_IsFalse_WhenNotLinked()
    {
        var h = CreateViewModel();

        Assert.False(h.Dashboard.IsPolarLinked);
        Assert.True(h.Dashboard.HasNoActivity);
    }

    [Fact]
    public void Reload_UnreadNotificationCount_ReflectsNotificationService()
    {
        var h = CreateViewModel();
        h.Notifications.Push("Título", "Mensaje", NotificationSeverity.Info);

        h.Dashboard.Reload();

        Assert.Equal(1, h.Dashboard.UnreadNotificationCount);
        Assert.True(h.Dashboard.HasUnreadNotifications);
        Assert.False(h.Dashboard.HasNoActivity);
    }

    [Fact]
    public void NotificationService_MarkAsRead_UpdatesDashboardCount_InRealTime()
    {
        var h = CreateViewModel();
        var notification = h.Notifications.Push("Título", "Mensaje", NotificationSeverity.Info);

        Assert.Equal(1, h.Dashboard.UnreadNotificationCount);
        Assert.True(h.Dashboard.HasUnreadNotifications);

        h.Notifications.MarkAsRead(notification.Id);

        Assert.Equal(0, h.Dashboard.UnreadNotificationCount);
        Assert.False(h.Dashboard.HasUnreadNotifications);
    }

    [Fact]
    public void Reload_DoesNotPushSizeWarning_OnTheHappyPath()
    {
        var h = CreateViewModel();
        File.WriteAllText(Path.Combine(_vaultPath, "Nota.md"), "# Nota");

        h.Dashboard.Reload();

        Assert.DoesNotContain(h.Notifications.Notifications, n => n.Title == "Tamaño del vault incompleto");
    }

    [Fact]
    public void NewNoteCommand_InvokesCallback()
    {
        var called = false;
        var settingsService = new SettingsService(_storageDirectory);
        var vault = new VaultService(settingsService);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessionService = new SessionService(vault);
        var kanbanService = new KanbanService(_storageDirectory);
        var authService = new PolarAuthService(settingsService);
        var taskService = new PolarTaskService(_storageDirectory);
        var reminderService = new ReminderService(_storageDirectory);
        var notificationService = new NotificationService(settingsService, _storageDirectory);
        var calendarAggregationService = new CalendarAggregationService(kanbanService, taskService, reminderService, authService);

        var dashboard = new DashboardViewModel(
            vault, sessionService, kanbanService, taskService, authService, notificationService, calendarAggregationService,
            openNote: _ => { }, newNote: () => called = true, navigateToKanban: () => { }, navigateToSessions: () => { },
            navigateToNotes: () => { }, navigateToTrash: () => { }, calendarEnabled: true, navigateToCalendar: () => { },
            openReminders: () => { }, openNotifications: () => { }, openSettings: () => { }, showScrollbar: true);

        dashboard.NewNoteCommand.Execute(null);

        Assert.True(called);
    }

    [Fact]
    public void SetCalendarEnabled_UpdatesIsCalendarEnabled()
    {
        var h = CreateViewModel();

        h.Dashboard.SetCalendarEnabled(false);
        Assert.False(h.Dashboard.IsCalendarEnabled);

        h.Dashboard.SetCalendarEnabled(true);
        Assert.True(h.Dashboard.IsCalendarEnabled);
    }
}
