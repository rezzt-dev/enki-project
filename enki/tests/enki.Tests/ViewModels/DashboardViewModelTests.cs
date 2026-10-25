using System;
using System.IO;
using System.Linq;
using System.Threading;
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
        DashboardViewModel Dashboard, VaultService Vault, SessionService Sessions, KanbanService Kanban,
        ReminderService Reminders, NotificationService Notifications, VaultSettingsService VaultSettings,
        HabitService Habits);

    private Harness CreateViewModel()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vault = new VaultService(settingsService);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var vaultSettingsService = new VaultSettingsService(vault);
        var sessionService = new SessionService(vault);
        var kanbanService = new KanbanService(_storageDirectory);
        var authService = new PolarAuthService(settingsService);
        var taskService = new PolarTaskService(_storageDirectory);
        var reminderService = new ReminderService(_storageDirectory);
        var notificationService = new NotificationService(settingsService, _storageDirectory);
        var calendarAggregationService = new CalendarAggregationService(kanbanService, taskService, reminderService, authService);
        var habitService = new HabitService(vault);

        var dashboard = new DashboardViewModel(
            vault, sessionService, kanbanService, taskService, authService, notificationService, calendarAggregationService,
            habitService,
            openNote: _ => { }, newNote: () => { }, navigateToKanban: () => { }, navigateToSessions: () => { },
            navigateToNotes: () => { }, navigateToTrash: () => { }, calendarEnabled: true, navigateToCalendar: () => { },
            openReminders: () => { }, openNotifications: () => { }, openSettings: () => { }, navigateToHabits: () => { },
            showScrollbar: true, vaultSettingsService);

        return new Harness(dashboard, vault, sessionService, kanbanService, reminderService, notificationService, vaultSettingsService, habitService);
    }

    [Fact]
    public void Reload_PopulatesVaultStats_ForFreshVault()
    {
        var h = CreateViewModel();

        Assert.Equal(_vaultPath, h.Dashboard.VaultPath);
        Assert.Equal(0, h.Dashboard.TotalNoteCount);
        Assert.Equal(0, h.Dashboard.TotalFileCount);
        Assert.False(h.Dashboard.HasFeaturedNote);
    }

    [Fact]
    public void Reload_CountsFilesAcrossVaultAndSessions_WithoutInternalDataOrTrash()
    {
        var h = CreateViewModel();
        Assert.True(SpinWait.SpinUntil(() => !h.Dashboard.IsGraphLoading, TimeSpan.FromSeconds(5)));
        var session = h.Sessions.Create("Trabajo");
        foreach (var root in new[] { _vaultPath, session.RootPath })
        {
            File.WriteAllText(Path.Combine(root, "documento.pdf"), "pdf");
            File.WriteAllText(Path.Combine(root, "nota.md"), "# Nota");
            File.WriteAllText(Path.Combine(root, "mayusculas.MD"), "# Nota");
            File.WriteAllText(Path.Combine(root, ".oculto.json"), "{}");
            var folder = Directory.CreateDirectory(Path.Combine(root, "carpeta"));
            File.WriteAllText(Path.Combine(folder.FullName, "imagen.png"), "png");
            var trash = Directory.CreateDirectory(Path.Combine(root, ".trash"));
            File.WriteAllText(Path.Combine(trash.FullName, "borrado.pdf"), "pdf");
        }
        var tasks = Directory.CreateDirectory(Path.Combine(_vaultPath, ".tasks"));
        File.WriteAllText(Path.Combine(tasks.FullName, "reminders.json"), "[]");

        h.Dashboard.Reload();

        Assert.Equal(4, h.Dashboard.TotalFileCount);
        Assert.True(SpinWait.SpinUntil(() => !h.Dashboard.IsGraphLoading, TimeSpan.FromSeconds(5)));
        File.Delete(Path.Combine(session.RootPath, "documento.pdf"));
        h.Dashboard.Reload();
        Assert.Equal(3, h.Dashboard.TotalFileCount);
        Assert.True(SpinWait.SpinUntil(() => !h.Dashboard.IsGraphLoading, TimeSpan.FromSeconds(5)));
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
    public void Reload_CountsNotesInVaultFoldersAndEverySession()
    {
        var h = CreateViewModel();
        File.WriteAllText(Path.Combine(_vaultPath, "Raíz.md"), "# Raíz");
        var vaultFolder = Directory.CreateDirectory(Path.Combine(_vaultPath, "Carpeta"));
        File.WriteAllText(Path.Combine(vaultFolder.FullName, "Anidada.md"), "# Anidada");

        var firstSession = h.Sessions.Create("Primera");
        File.WriteAllText(Path.Combine(firstSession.RootPath, "Uno.md"), "# Uno");
        var sessionFolder = Directory.CreateDirectory(Path.Combine(firstSession.RootPath, "Subcarpeta"));
        File.WriteAllText(Path.Combine(sessionFolder.FullName, "Dos.md"), "# Dos");
        var secondSession = h.Sessions.Create("Segunda");
        File.WriteAllText(Path.Combine(secondSession.RootPath, "Tres.md"), "# Tres");

        h.Dashboard.Reload();

        Assert.Equal(5, h.Dashboard.TotalNoteCount);
        Assert.Equal(2, h.Dashboard.SessionCount);
        Assert.Equal("Raíz", h.Dashboard.FeaturedNote?.Title);
        Assert.True(SpinWait.SpinUntil(() => !h.Dashboard.IsGraphLoading, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Reload_SplitsBoardCardsAcrossCoreColumns_ByRole()
    {
        var h = CreateViewModel();
        var backlogColumn = h.Kanban.DefaultBoard.Columns.First(c => c.Role == CoreColumnRole.Backlog);
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
        var backlogColumn = h.Kanban.DefaultBoard.Columns.First(c => c.Role == CoreColumnRole.Backlog);
        h.Kanban.DefaultBoard.Columns.Remove(backlogColumn);
        h.Kanban.DefaultBoard.Columns.Add(backlogColumn);
        backlogColumn.Cards.Add(new KanbanCard { Id = "p1", Title = "Pendiente" });

        h.Dashboard.Reload();

        Assert.True(h.Dashboard.PendingTaskCount >= 1);
        Assert.Contains(h.Dashboard.PendingTasks, c => c.Id == "p1");
    }

    [Fact]
    public void Reload_BoardProgressPercent_IsZero_WhenBoardHasNoCards()
    {
        var h = CreateViewModel();
        foreach (var column in h.Kanban.DefaultBoard.Columns) column.Cards.Clear();
        h.Kanban.DefaultBoard.Columns[0].Cards.Clear();

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
        Assert.True(SpinWait.SpinUntil(() => !h.Dashboard.IsGraphLoading, TimeSpan.FromSeconds(5)));
        File.WriteAllText(Path.Combine(_vaultPath, "Nota.md"), "# Nota");

        h.Dashboard.Reload();

        Assert.DoesNotContain(h.Notifications.Notifications, n => n.Title == "Tamaño del vault incompleto");
        // El grafo puede escribir .graph.json; esperar su final evita competir con la limpieza del vault.
        Assert.True(SpinWait.SpinUntil(() => !h.Dashboard.IsGraphLoading, TimeSpan.FromSeconds(5)));
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
        var habitService = new HabitService(vault);

        var dashboard = new DashboardViewModel(
            vault, sessionService, kanbanService, taskService, authService, notificationService, calendarAggregationService,
            habitService,
            openNote: _ => { }, newNote: () => called = true, navigateToKanban: () => { }, navigateToSessions: () => { },
            navigateToNotes: () => { }, navigateToTrash: () => { }, calendarEnabled: true, navigateToCalendar: () => { },
            openReminders: () => { }, openNotifications: () => { }, openSettings: () => { }, navigateToHabits: () => { },
            showScrollbar: true);

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

    [Fact]
    public void Reload_Sections_AreInDefaultOrder_WhenNoVaultSetting()
    {
        var h = CreateViewModel();

        var keys = h.Dashboard.Sections.Select(s => s.Key).ToList();

        Assert.Equal(["QuickAccess", "Today", "Vault", "Activity", "Sessions", "BoardTasks", "RecentNotes", "Habits"], keys);
        Assert.All(h.Dashboard.Sections, s => Assert.True(s.IsVisible));
    }

    [Fact]
    public void Reload_Sections_FollowCustomOrder_FromVaultSettings()
    {
        var h = CreateViewModel();
        h.VaultSettings.Settings.DashboardSectionOrder = ["RecentNotes", "BoardTasks", "Today"];
        h.VaultSettings.Save();

        h.Dashboard.Reload();

        Assert.Equal(["RecentNotes", "BoardTasks", "Today", "QuickAccess", "Vault", "Activity", "Sessions", "Habits"], h.Dashboard.Sections.Select(s => s.Key));
    }

    [Fact]
    public void Reload_Sections_SkipsHiddenSections_FromVaultSettings()
    {
        var h = CreateViewModel();
        h.VaultSettings.Settings.DashboardHiddenSections = ["Vault", "Activity", "Sessions"];
        h.VaultSettings.Save();

        h.Dashboard.Reload();

        var keys = h.Dashboard.Sections.Select(s => s.Key).ToList();
        Assert.DoesNotContain("Vault", keys);
        Assert.DoesNotContain("Activity", keys);
        Assert.DoesNotContain("Sessions", keys);
        Assert.Contains("QuickAccess", keys);
        Assert.Contains("Today", keys);
        Assert.Contains("BoardTasks", keys);
        Assert.Contains("RecentNotes", keys);
    }

    // ===================== Fase 3: TodayItems no lo eclipsa el backlog vencido antiguo (análisis §4.4, Alta) =====================

    [Fact]
    public void Reload_TodayItems_AlwaysIncludesItemsDueToday_EvenWithOldOverdueBacklog()
    {
        var h = CreateViewModel();
        var backlogColumn = h.Kanban.DefaultBoard.Columns.First(c => c.Role == CoreColumnRole.Backlog);
        for (var i = 0; i < 6; i++)
        {
            backlogColumn.Cards.Add(new KanbanCard
            {
                Id = $"old-{i}", Title = $"Vieja {i}", DueDateUtc = DateTime.UtcNow.AddDays(-100 - i)
            });
        }
        var dueTodayCard = new KanbanCard { Id = "today-1", Title = "Vence hoy", DueDateUtc = DateTime.UtcNow };
        backlogColumn.Cards.Add(dueTodayCard);

        h.Dashboard.Reload();

        Assert.Contains(h.Dashboard.TodayItems, i => i.SourceId == dueTodayCard.Id);
    }

    [Fact]
    public void Reload_TodayItems_ExcludesOverdueBacklog_OlderThanBacklogWindow()
    {
        var h = CreateViewModel();
        var backlogColumn = h.Kanban.DefaultBoard.Columns.First(c => c.Role == CoreColumnRole.Backlog);
        var ancientCard = new KanbanCard { Id = "ancient-1", Title = "Muy vieja", DueDateUtc = DateTime.UtcNow.AddDays(-400) };
        backlogColumn.Cards.Add(ancientCard);

        h.Dashboard.Reload();

        Assert.DoesNotContain(h.Dashboard.TodayItems, i => i.SourceId == ancientCard.Id);
    }

    [Fact]
    public void Reload_TodayItems_ExcludesCompletedKanbanCards()
    {
        var h = CreateViewModel();
        var backlogColumn = h.Kanban.DefaultBoard.Columns.First(c => c.Role == CoreColumnRole.Backlog);
        var completedCard = new KanbanCard
        {
            Id = "completed-1", Title = "Terminada", DueDateUtc = DateTime.UtcNow.AddDays(-1),
            CompletedAtUtc = DateTime.UtcNow.AddHours(-1)
        };
        backlogColumn.Cards.Add(completedCard);

        h.Dashboard.Reload();

        Assert.DoesNotContain(h.Dashboard.TodayItems, i => i.SourceId == completedCard.Id);
    }

    [Fact]
    public void Reload_Sections_AppendsMissingKeysAtEnd()
    {
        var h = CreateViewModel();
        h.VaultSettings.Settings.DashboardSectionOrder = ["RecentNotes"];
        h.VaultSettings.Save();

        h.Dashboard.Reload();

        Assert.Equal("RecentNotes", h.Dashboard.Sections.Select(s => s.Key).First());
        Assert.Equal(8, h.Dashboard.Sections.Count);
    }
}
