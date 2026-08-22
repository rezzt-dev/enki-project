using System;
using System.IO;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>Persists <see cref="HabitStore"/> inside the active vault, mirroring
/// <see cref="VaultSettingsService"/>'s own <c>&lt;vault&gt;/.enki/settings.json</c> pattern — a habit is
/// the user's own content, not app-wide state, so it travels with the vault instead of living in
/// <c>%AppData%/Enki</c> like <see cref="KanbanService"/>. Same reasoning for the explicit
/// <see cref="Reload"/>: this caches its loaded state in <see cref="Store"/>, so it needs re-reading when
/// the active vault changes underneath it (see <c>ViewModels.MainWindowViewModel.OnVaultChanged</c>/
/// <c>InitializeShell</c>).</summary>
public sealed class HabitService
{
    private readonly VaultService _vaultService;

    public HabitStore Store { get; private set; }

    /// <summary>Raised after every successful <see cref="Save"/> — same "let read-only panels stay live
    /// without a direct reference" purpose as <see cref="KanbanService.BoardChanged"/>, for the Dashboard's
    /// compact "Habits" card.</summary>
    public event Action? HabitsChanged;

    public HabitService(VaultService vaultService)
    {
        _vaultService = vaultService;
        Store = Load();
    }

    private string StorePath => Path.Combine(_vaultService.VaultPath, ".enki", "habits.json");

    private HabitStore Load()
    {
        if (!_vaultService.IsVaultConfigured) return new HabitStore();
        return JsonFileStore.TryLoad<HabitStore>(StorePath, out var loaded) ? loaded! : new HabitStore();
    }

    /// <summary>Re-reads habits for whichever vault <see cref="VaultService"/> now points at — called from
    /// the same two points <see cref="VaultSettingsService.Reload"/> already is.</summary>
    public void Reload() => Store = Load();

    public void Save()
    {
        if (!_vaultService.IsVaultConfigured) return;

        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        JsonFileStore.Save(StorePath, Store);
        HabitsChanged?.Invoke();
    }

    public Habit AddHabit(string name, HabitFrequency frequency, System.Collections.Generic.List<DayOfWeek> specificDays)
    {
        var habit = new Habit
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Frequency = frequency,
            SpecificDays = frequency == HabitFrequency.SpecificDays ? specificDays : []
        };
        Store.Habits.Add(habit);
        Save();
        return habit;
    }

    public void UpdateHabit(Habit habit, string name, HabitFrequency frequency, System.Collections.Generic.List<DayOfWeek> specificDays)
    {
        habit.Name = name;
        habit.Frequency = frequency;
        habit.SpecificDays = frequency == HabitFrequency.SpecificDays ? specificDays : [];
        Save();
    }

    /// <summary>Soft-hides the habit (see <see cref="Habit.Archived"/>) instead of removing it, so its
    /// <see cref="Habit.CompletedDates"/> history is never discarded.</summary>
    public void ArchiveHabit(Habit habit)
    {
        habit.Archived = true;
        Save();
    }

    /// <summary>Adds or removes <paramref name="date"/> from <see cref="Habit.CompletedDates"/> — the only
    /// mutation a completion mark ever gets, no count, no streak bookkeeping.</summary>
    public void ToggleCompletion(Habit habit, DateOnly date)
    {
        if (!habit.CompletedDates.Remove(date)) habit.CompletedDates.Add(date);
        Save();
    }

    public static bool IsCompleted(Habit habit, DateOnly date) => habit.CompletedDates.Contains(date);

    /// <summary>Whether <paramref name="habit"/> is expected to be done on <paramref name="date"/> at all —
    /// <see cref="HabitFrequency.Daily"/>/<see cref="HabitFrequency.Weekly"/> apply to every day (weekly
    /// habits aren't tied to one particular weekday, only to "at least once this week"),
    /// <see cref="HabitFrequency.SpecificDays"/> only to the days the user picked. Drives which cells of the
    /// consistency grid render as "not applicable" instead of "missed".</summary>
    public static bool IsApplicable(Habit habit, DateOnly date) => habit.Frequency switch
    {
        HabitFrequency.SpecificDays => habit.SpecificDays.Contains(DayOfWeekOf(date)),
        _ => true
    };

    private static DayOfWeek DayOfWeekOf(DateOnly date) => date.DayOfWeek;
}
