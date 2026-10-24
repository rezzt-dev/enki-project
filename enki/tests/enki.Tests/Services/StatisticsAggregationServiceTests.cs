using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="StatisticsAggregationService"/> — propuesta 15 (docs/
/// propuesta-nuevas-funcionalidades.md §15). Assertions deliberately avoid pinning a specific date to a
/// specific week bucket (that depends on the test machine's local timezone, see
/// <see cref="StatisticsAggregationService.BuildWeeklySeries"/>'s local-date bucketing) and instead check
/// totals across the whole returned series, same "sum across weeks" approach kept timezone-safe.</summary>
public class StatisticsAggregationServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public StatisticsAggregationServiceTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private sealed record Harness(
        StatisticsAggregationService Service, VaultService Vault, KanbanService Kanban,
        PolarTaskService Tasks, PolarAuthService Auth, HabitService Habits);

    private Harness CreateHarness(bool polarLinked = false)
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vaultService = new VaultService(settingsService);
        vaultService.InitializeVault(_vaultPath, seedWelcomeNote: false);

        var kanbanService = new KanbanService(_storageDirectory);
        var taskService = new PolarTaskService(_storageDirectory);
        var authService = new PolarAuthService(settingsService, baseUrl: "https://test.example", anonKey: "test-key");
        settingsService.Settings.PolarLinked = polarLinked;

        var habitService = new HabitService(vaultService);

        var service = new StatisticsAggregationService(vaultService, kanbanService, taskService, authService, habitService);
        return new Harness(service, vaultService, kanbanService, taskService, authService, habitService);
    }

    private static (DateTime StartUtc, DateTime EndUtc) LastWeekRange() =>
        (DateTime.UtcNow.AddDays(-7), DateTime.UtcNow.AddDays(1));

    [Fact]
    public void BuildKanbanFlow_AggregatesRolesAcrossAllBoards_IncludingArchivedHistory()
    {
        var h = CreateHarness();
        foreach (var column in h.Kanban.DefaultBoard.Columns) column.Cards.Clear();
        var board = h.Kanban.CreateBoard("Histórico");
        board.Columns[0].Cards.Add(new() { Id = "pending", Title = "Pendiente" });
        board.Columns[2].Cards.Add(new() { Id = "archived", Title = "Hecha", ArchivedAtUtc = DateTime.UtcNow });
        Assert.Equal(new KanbanFlowSnapshot(1, 0, 1), h.Service.BuildKanbanFlow());
    }

    [Fact]
    public void BuildWeeklySeries_EmptyVault_ReturnsZeroedWeeksWithNoHabitData()
    {
        var h = CreateHarness();
        var (start, end) = LastWeekRange();

        var series = h.Service.BuildWeeklySeries(start, end);

        Assert.NotEmpty(series);
        Assert.All(series, w =>
        {
            Assert.Equal(0, w.NotesCreated);
            Assert.Equal(0, w.NotesEdited);
            Assert.Equal(0, w.TasksCompleted);
            Assert.Null(w.HabitCompletionPercent);
        });
    }

    [Fact]
    public void BuildWeeklySeries_NewNote_IsCountedAsCreatedAndEdited()
    {
        var h = CreateHarness();
        h.Vault.CreateNote("Nota de prueba");
        var (start, end) = LastWeekRange();

        var series = h.Service.BuildWeeklySeries(start, end);

        Assert.Equal(1, series.Sum(w => w.NotesCreated));
        Assert.Equal(1, series.Sum(w => w.NotesEdited));
    }

    [Fact]
    public void BuildWeeklySeries_KanbanCardsWithoutCompletedAt_AreNotCounted()
    {
        var h = CreateHarness();
        h.Kanban.DefaultBoard.Columns[0].Cards.Add(new KanbanCard { Id = "c1", Title = "Sin completar" });
        var (start, end) = LastWeekRange();

        var series = h.Service.BuildWeeklySeries(start, end);

        Assert.Equal(0, series.Sum(w => w.TasksCompleted));
    }

    [Fact]
    public void BuildWeeklySeries_KanbanCardsCompletedInRange_AreCounted()
    {
        var h = CreateHarness();
        h.Kanban.DefaultBoard.Columns[0].Cards.Add(new KanbanCard { Id = "c1", Title = "Completada", CompletedAtUtc = DateTime.UtcNow });
        h.Kanban.DefaultBoard.Columns[0].Cards.Add(new KanbanCard { Id = "c2", Title = "Completada 2", CompletedAtUtc = DateTime.UtcNow });
        var (start, end) = LastWeekRange();

        var series = h.Service.BuildWeeklySeries(start, end);

        Assert.Equal(2, series.Sum(w => w.TasksCompleted));
    }

    [Fact]
    public void BuildWeeklySeries_PolarTaskCompleted_ExcludedWhenNotLinked()
    {
        var h = CreateHarness(polarLinked: false);
        h.Tasks.Store.Tasks.Add(new PolarTask { Id = "t1", ListId = "l1", Title = "Tarea Polar", Completed = true, CompletedAtUtc = DateTime.UtcNow });
        var (start, end) = LastWeekRange();

        var series = h.Service.BuildWeeklySeries(start, end);

        Assert.Equal(0, series.Sum(w => w.TasksCompleted));
    }

    [Fact]
    public void BuildWeeklySeries_PolarTaskCompleted_IncludedWhenLinked()
    {
        var h = CreateHarness(polarLinked: true);
        h.Tasks.Store.Tasks.Add(new PolarTask { Id = "t1", ListId = "l1", Title = "Tarea Polar", Completed = true, CompletedAtUtc = DateTime.UtcNow });
        var (start, end) = LastWeekRange();

        var series = h.Service.BuildWeeklySeries(start, end);

        Assert.Equal(1, series.Sum(w => w.TasksCompleted));
    }

    [Fact]
    public void BuildWeeklySeries_NoActiveHabits_HabitCompletionPercentIsAlwaysNull()
    {
        var h = CreateHarness();
        var (start, end) = LastWeekRange();

        var series = h.Service.BuildWeeklySeries(start, end);

        Assert.All(series, w => Assert.Null(w.HabitCompletionPercent));
    }

    [Fact]
    public void BuildWeeklySeries_HabitDoneOnItsOnlyApplicableDayToday_IsFullyConsistentThisWeek()
    {
        var h = CreateHarness();
        var today = DateOnly.FromDateTime(DateTime.Today);
        // Solo aplica hoy, así el % de la semana en curso es determinista sin depender de qué otros días
        // de la semana ya han pasado.
        var habit = h.Habits.AddHabit("Meditar", HabitFrequency.SpecificDays, [today.DayOfWeek]);
        h.Habits.ToggleCompletion(habit, today);
        var (start, end) = LastWeekRange();

        var series = h.Service.BuildWeeklySeries(start, end);

        Assert.Equal(100.0, series.Last().HabitCompletionPercent);
    }

    [Fact]
    public void BuildWeeklySeries_ArchivedHabit_IsExcludedFromHabitCompletionPercent()
    {
        var h = CreateHarness();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var habit = h.Habits.AddHabit("Meditar", HabitFrequency.SpecificDays, [today.DayOfWeek]);
        h.Habits.ArchiveHabit(habit);
        var (start, end) = LastWeekRange();

        var series = h.Service.BuildWeeklySeries(start, end);

        Assert.All(series, w => Assert.Null(w.HabitCompletionPercent));
    }
}
