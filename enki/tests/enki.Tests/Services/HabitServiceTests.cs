using System;
using System.IO;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="HabitService"/> — same per-vault persistence contract as
/// <see cref="VaultSettingsServiceTests"/> (<c>&lt;vault&gt;/.enki/habits.json</c>), plus the
/// completion/applicability rules propuesta 13 (docs/propuesta-nuevas-funcionalidades.md §13) is built on.</summary>
public class HabitServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public HabitServiceTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private VaultService CreateVault()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        return vault;
    }

    [Fact]
    public void AddHabit_PersistsAcrossInstancesOfTheSameVault()
    {
        var vault = CreateVault();
        var first = new HabitService(vault);
        first.AddHabit("Meditar", HabitFrequency.Daily, []);

        var second = new HabitService(vault);

        Assert.Single(second.Store.Habits);
        Assert.Equal("Meditar", second.Store.Habits[0].Name);
    }

    [Fact]
    public void Save_WritesInsideTheVaultFolder_AsItsOwnFile()
    {
        var vault = CreateVault();
        var service = new HabitService(vault);
        service.AddHabit("Leer", HabitFrequency.Daily, []);

        var habitsPath = Path.Combine(_vaultPath, ".enki", "habits.json");
        Assert.True(File.Exists(habitsPath));
        Assert.Contains("Leer", File.ReadAllText(habitsPath));
    }

    [Fact]
    public void ToggleCompletion_TwiceOnTheSameDate_AddsThenRemoves()
    {
        var service = new HabitService(CreateVault());
        var habit = service.AddHabit("Meditar", HabitFrequency.Daily, []);
        var date = new DateOnly(2026, 3, 1);

        service.ToggleCompletion(habit, date);
        Assert.True(HabitService.IsCompleted(habit, date));

        service.ToggleCompletion(habit, date);
        Assert.False(HabitService.IsCompleted(habit, date));
    }

    [Fact]
    public void ArchiveHabit_KeepsCompletedDates_ButMarksArchived()
    {
        var service = new HabitService(CreateVault());
        var habit = service.AddHabit("Meditar", HabitFrequency.Daily, []);
        var date = new DateOnly(2026, 3, 1);
        service.ToggleCompletion(habit, date);

        service.ArchiveHabit(habit);

        Assert.True(habit.Archived);
        Assert.True(HabitService.IsCompleted(habit, date));
    }

    [Theory]
    [InlineData(HabitFrequency.Daily)]
    [InlineData(HabitFrequency.Weekly)]
    public void IsApplicable_DailyOrWeekly_AppliesToEveryDay(HabitFrequency frequency)
    {
        var service = new HabitService(CreateVault());
        var habit = service.AddHabit("Hábito", frequency, []);

        Assert.True(HabitService.IsApplicable(habit, new DateOnly(2026, 3, 2))); // lunes
        Assert.True(HabitService.IsApplicable(habit, new DateOnly(2026, 3, 7))); // sábado
    }

    [Fact]
    public void IsApplicable_SpecificDays_OnlyAppliesToChosenWeekdays()
    {
        var service = new HabitService(CreateVault());
        var habit = service.AddHabit("Gimnasio", HabitFrequency.SpecificDays, [DayOfWeek.Monday, DayOfWeek.Wednesday]);

        Assert.True(HabitService.IsApplicable(habit, new DateOnly(2026, 3, 2)));  // lunes
        Assert.False(HabitService.IsApplicable(habit, new DateOnly(2026, 3, 3))); // martes
        Assert.True(HabitService.IsApplicable(habit, new DateOnly(2026, 3, 4)));  // miércoles
    }

    [Fact]
    public void UpdateHabit_ChangingFrequencyAwayFromSpecificDays_ClearsSpecificDays()
    {
        var service = new HabitService(CreateVault());
        var habit = service.AddHabit("Gimnasio", HabitFrequency.SpecificDays, [DayOfWeek.Monday]);

        service.UpdateHabit(habit, "Gimnasio", HabitFrequency.Daily, []);

        Assert.Empty(habit.SpecificDays);
    }

    [Fact]
    public void Reload_PicksUpTheNewlyActiveVaultsOwnHabits()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vaultService = new VaultService(settingsService);

        var vaultAPath = Path.Combine(_storageDirectory, "vault-a");
        var vaultBPath = Path.Combine(_storageDirectory, "vault-b");
        vaultService.InitializeVault(vaultAPath, seedWelcomeNote: false);
        var habitService = new HabitService(vaultService);
        habitService.AddHabit("Solo en A", HabitFrequency.Daily, []);

        vaultService.SetVaultPath(vaultBPath);
        habitService.Reload();

        Assert.Empty(habitService.Store.Habits);
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToEmptyStoreInsteadOfThrowing()
    {
        var vault = CreateVault();
        var enkiDir = Path.Combine(_vaultPath, ".enki");
        Directory.CreateDirectory(enkiDir);
        File.WriteAllText(Path.Combine(enkiDir, "habits.json"), "{ not valid json ");

        var service = new HabitService(vault);

        Assert.Empty(service.Store.Habits);
    }
}
