using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="StatisticsViewModel"/> — propuesta 15 (docs/propuesta-nuevas-funcionalidades.md
/// §15). Same throwaway-vault-directory + real-services harness as <see cref="HabitsViewModelTests"/>.</summary>
public class StatisticsViewModelTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private sealed record Harness(StatisticsViewModel Vm, KanbanService Kanban, HabitService Habits, SettingsService Settings);

    private Harness CreateViewModel()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vaultService = new VaultService(settingsService);
        vaultService.InitializeVault(Path.Combine(_storageDirectory, "vault"), seedWelcomeNote: false);

        var kanbanService = new KanbanService(_storageDirectory);
        var taskService = new PolarTaskService(_storageDirectory);
        var authService = new PolarAuthService(settingsService, baseUrl: "https://test.example", anonKey: "test-key");
        var habitService = new HabitService(vaultService);

        var aggregationService = new StatisticsAggregationService(vaultService, kanbanService, taskService, authService, habitService);
        var vm = new StatisticsViewModel(aggregationService, authService);
        return new Harness(vm, kanbanService, habitService, settingsService);
    }

    [Fact]
    public void Reload_EmptyVault_HasEnoughDataIsFalse()
    {
        var h = CreateViewModel();

        // BuildWeeklySeries always returns one point per week in range (zeroed, not omitted) — the
        // empty state is driven by HasEnoughData, not by an empty Weeks collection. See
        // StatisticsAggregationServiceTests.BuildWeeklySeries_EmptyVault_ReturnsZeroedWeeksWithNoHabitData.
        Assert.False(h.Vm.HasEnoughData);
        Assert.All(h.Vm.Weeks, w => Assert.Equal(0, w.NotesTotal));
        Assert.All(h.Vm.Weeks, w => Assert.Equal(0, w.TasksCompleted));
    }

    [Fact]
    public void Reload_WithACompletedCardThisWeek_HasEnoughDataIsTrue()
    {
        var h = CreateViewModel();
        h.Kanban.Board.Columns[0].Cards.Add(new KanbanCard { Id = "c1", Title = "Completada", CompletedAtUtc = DateTime.UtcNow });

        h.Vm.Reload();

        Assert.True(h.Vm.HasEnoughData);
        Assert.Equal(1, h.Vm.TasksCompletedThisWeek);
    }

    [Fact]
    public void Reload_NotPolarLinked_IsPolarLinkedIsFalse()
    {
        var h = CreateViewModel();

        Assert.False(h.Vm.IsPolarLinked);
    }

    [Fact]
    public void Reload_NoActiveHabits_HasHabitDataIsFalse()
    {
        var h = CreateViewModel();

        Assert.False(h.Vm.HasHabitData);
        Assert.Null(h.Vm.HabitCompletionPercent);
    }

    [Fact]
    public void Reload_OnlyHabitsCompleted_NoNotesOrTasks_HasEnoughDataIsTrue()
    {
        // análisis §4.4, Media: un usuario que solo usa Hábitos (sin notas ni tareas) debe seguir viendo
        // su gráfico de consistencia en vez del estado vacío "vuelve cuando tengas más actividad".
        var h = CreateViewModel();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var habit = h.Habits.AddHabit("Meditar", HabitFrequency.SpecificDays, [today.DayOfWeek]);
        h.Habits.ToggleCompletion(habit, today);

        h.Vm.Reload();

        Assert.True(h.Vm.HasHabitData);
        Assert.NotNull(h.Vm.HabitCompletionPercent);
        Assert.True(h.Vm.HasEnoughData);
        Assert.Equal(0, h.Vm.TasksCompletedThisWeek);
    }

    [Fact]
    public void SelectRange_ChangesRangeAndReloads()
    {
        var h = CreateViewModel();

        h.Vm.SelectRangeCommand.Execute("AllTime");

        Assert.Equal(StatisticsRange.AllTime, h.Vm.Range);
    }
}
