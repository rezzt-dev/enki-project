using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Full "Hábitos" page — propuesta 13 (docs/propuesta-nuevas-funcionalidades.md §13): a short list
/// of user-defined habits with a daily done/not-done mark and a discrete consistency grid, deliberately
/// without streak counters, points or any other gamification.</summary>
public sealed partial class HabitsViewModel : ViewModelBase
{
    /// <summary>Width of the consistency grid: 10 weeks (70 days), 7 columns — a compact strip that still
    /// reads as "the last couple of months" without needing to scroll or paginate.</summary>
    private const int ConsistencyWeeks = 10;

    private readonly HabitService _habitService;
    private readonly SettingsService _settingsService;

    /// <summary>Non-null while editing this habit; null while <see cref="Editor"/> is building a brand-new
    /// one — same "one popup, two modes" shape <c>KanbanCardEditorViewModel</c> already uses.</summary>
    private Habit? _editingHabit;

    public ObservableCollection<HabitItemViewModel> Habits { get; } = [];

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>Non-null while the "nuevo/editar hábito" popup is open.</summary>
    [ObservableProperty]
    private HabitEditorViewModel? _editor;

    /// <summary>Non-null while the "archivar hábito" confirmation dialog is open.</summary>
    [ObservableProperty]
    private HabitItemViewModel? _habitPendingArchive;

    public HabitsViewModel(HabitService habitService, SettingsService settingsService)
    {
        _habitService = habitService;
        _settingsService = settingsService;
        Reload();
    }

    public void Reload()
    {
        Habits.Clear();
        var today = DateOnly.FromDateTime(DateTime.Today);
        foreach (var habit in _habitService.Store.Habits.Where(h => !h.Archived).OrderBy(h => h.CreatedUtc))
            Habits.Add(BuildItem(habit, today));

        IsEmpty = Habits.Count == 0;
    }

    /// <summary>Builds the read projection for one habit, including its consistency grid window ending
    /// <paramref name="today"/> — shared by this page's own <see cref="Reload"/> and
    /// <c>DashboardViewModel</c>'s compact card, so both read exactly the same
    /// "done/not-done/not-applicable" logic. Static — <see cref="HabitService.IsCompleted"/>/
    /// <see cref="HabitService.IsApplicable"/> are themselves static (pure functions of a
    /// <see cref="Habit"/>), so no live service instance is needed either.</summary>
    public static HabitItemViewModel BuildItem(Habit habit, DateOnly today)
    {
        var days = new ObservableCollection<ConsistencyDayViewModel>();
        var windowStart = today.AddDays(-(ConsistencyWeeks * 7 - 1));
        for (var date = windowStart; date <= today; date = date.AddDays(1))
        {
            days.Add(new ConsistencyDayViewModel
            {
                Date = date,
                IsDone = HabitService.IsCompleted(habit, date),
                IsApplicable = HabitService.IsApplicable(habit, date)
            });
        }

        return new HabitItemViewModel(habit)
        {
            IsDoneToday = HabitService.IsCompleted(habit, today),
            ConsistencyDays = days
        };
    }

    // ===================== Marcar hecho/no hecho =====================

    [RelayCommand]
    private void ToggleToday(HabitItemViewModel item)
    {
        _habitService.ToggleCompletion(item.Habit, DateOnly.FromDateTime(DateTime.Today));
        Reload();
    }

    // ===================== Crear / editar =====================

    [RelayCommand]
    private void NewHabit()
    {
        if (Editor is not null) return;
        _editingHabit = null;
        Editor = new HabitEditorViewModel();
    }

    [RelayCommand]
    private void EditHabit(HabitItemViewModel item)
    {
        if (Editor is not null) return;
        _editingHabit = item.Habit;
        Editor = new HabitEditorViewModel(item.Habit);
    }

    [RelayCommand]
    private void CancelEditor()
    {
        Editor = null;
        _editingHabit = null;
    }

    [RelayCommand]
    private void SaveEditor()
    {
        if (Editor is not { CanSave: true } editor) return;

        var name = editor.Name.Trim();
        var days = editor.BuildSelectedDays();

        if (_editingHabit is { } habit) _habitService.UpdateHabit(habit, name, editor.Frequency, days);
        else _habitService.AddHabit(name, editor.Frequency, days);

        Editor = null;
        _editingHabit = null;
        Reload();
    }

    // ===================== Archivar =====================
    // docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md §3 — "preguntar antes de borrar" salta
    // directo a la acción cuando está desactivado, mismo patrón que KanbanViewModel.RequestDeleteCard.

    [RelayCommand]
    private void RequestArchiveHabit(HabitItemViewModel item)
    {
        if (_settingsService.Settings.ConfirmBeforeDelete) HabitPendingArchive = item;
        else ArchiveHabit(item);
    }

    [RelayCommand]
    private void CancelArchiveHabit() => HabitPendingArchive = null;

    [RelayCommand]
    private void ConfirmArchiveHabit()
    {
        if (HabitPendingArchive is not { } item) return;
        ArchiveHabit(item);
        HabitPendingArchive = null;
    }

    private void ArchiveHabit(HabitItemViewModel item)
    {
        _habitService.ArchiveHabit(item.Habit);
        Reload();
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina un
    /// cambio de idioma en tiempo real (i18n Fase 4) — mismo contrato que el resto de páginas.</summary>
    public void RefreshLocalizedLabels() => Reload();
}
