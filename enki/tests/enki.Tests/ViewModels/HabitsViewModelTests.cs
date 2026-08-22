using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="HabitsViewModel"/> — propuesta 13 (docs/propuesta-nuevas-funcionalidades.md
/// §13). Same throwaway-vault-directory + real-services harness as the rest of the ViewModel tests (see
/// <c>DashboardViewModelTests</c>'s class remarks).</summary>
public class HabitsViewModelTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private sealed record Harness(HabitsViewModel Vm, HabitService Habits, SettingsService Settings);

    private Harness CreateViewModel()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vaultService = new VaultService(settingsService);
        vaultService.InitializeVault(Path.Combine(_storageDirectory, "vault"), seedWelcomeNote: false);
        var habitService = new HabitService(vaultService);

        var vm = new HabitsViewModel(habitService, settingsService);
        return new Harness(vm, habitService, settingsService);
    }

    [Fact]
    public void Reload_ExcludesArchivedHabits()
    {
        var h = CreateViewModel();
        var visible = h.Habits.AddHabit("Meditar", HabitFrequency.Daily, []);
        var archived = h.Habits.AddHabit("Antiguo", HabitFrequency.Daily, []);
        h.Habits.ArchiveHabit(archived);

        h.Vm.Reload();

        Assert.Single(h.Vm.Habits);
        Assert.Equal(visible.Id, h.Vm.Habits[0].Id);
    }

    [Fact]
    public void NewHabit_ThenSaveEditor_AddsHabitAndClosesEditor()
    {
        var h = CreateViewModel();
        h.Vm.NewHabitCommand.Execute(null);
        h.Vm.Editor!.Name = "Leer 20 min";

        h.Vm.SaveEditorCommand.Execute(null);

        Assert.Null(h.Vm.Editor);
        Assert.Contains(h.Vm.Habits, item => item.Name == "Leer 20 min");
    }

    [Fact]
    public void SaveEditor_WithBlankName_DoesNothing()
    {
        var h = CreateViewModel();
        h.Vm.NewHabitCommand.Execute(null);
        h.Vm.Editor!.Name = "   ";

        h.Vm.SaveEditorCommand.Execute(null);

        Assert.NotNull(h.Vm.Editor);
        Assert.Empty(h.Vm.Habits);
    }

    [Fact]
    public void EditHabit_ThenSaveEditor_UpdatesExistingHabitInstance()
    {
        var h = CreateViewModel();
        var habit = h.Habits.AddHabit("Meditar", HabitFrequency.Daily, []);
        h.Vm.Reload();
        var item = h.Vm.Habits.Single();

        h.Vm.EditHabitCommand.Execute(item);
        h.Vm.Editor!.Name = "Meditar 10 min";
        h.Vm.SaveEditorCommand.Execute(null);

        Assert.Single(h.Vm.Habits);
        Assert.Equal("Meditar 10 min", habit.Name);
    }

    [Fact]
    public void ToggleToday_MarksThenUnmarksTodayAsDone()
    {
        var h = CreateViewModel();
        h.Habits.AddHabit("Meditar", HabitFrequency.Daily, []);
        h.Vm.Reload();
        var item = h.Vm.Habits.Single();
        Assert.False(item.IsDoneToday);

        h.Vm.ToggleTodayCommand.Execute(item);
        Assert.True(h.Vm.Habits.Single().IsDoneToday);

        h.Vm.ToggleTodayCommand.Execute(h.Vm.Habits.Single());
        Assert.False(h.Vm.Habits.Single().IsDoneToday);
    }

    [Fact]
    public void RequestArchiveHabit_WithConfirmBeforeDeleteOn_OpensConfirmationInsteadOfArchivingImmediately()
    {
        var h = CreateViewModel();
        h.Settings.Settings.ConfirmBeforeDelete = true;
        h.Habits.AddHabit("Meditar", HabitFrequency.Daily, []);
        h.Vm.Reload();
        var item = h.Vm.Habits.Single();

        h.Vm.RequestArchiveHabitCommand.Execute(item);

        Assert.NotNull(h.Vm.HabitPendingArchive);
        Assert.Single(h.Vm.Habits);

        h.Vm.ConfirmArchiveHabitCommand.Execute(null);
        Assert.Null(h.Vm.HabitPendingArchive);
        Assert.Empty(h.Vm.Habits);
    }

    [Fact]
    public void RequestArchiveHabit_WithConfirmBeforeDeleteOff_ArchivesImmediately()
    {
        var h = CreateViewModel();
        h.Settings.Settings.ConfirmBeforeDelete = false;
        h.Habits.AddHabit("Meditar", HabitFrequency.Daily, []);
        h.Vm.Reload();
        var item = h.Vm.Habits.Single();

        h.Vm.RequestArchiveHabitCommand.Execute(item);

        Assert.Null(h.Vm.HabitPendingArchive);
        Assert.Empty(h.Vm.Habits);
    }
}
