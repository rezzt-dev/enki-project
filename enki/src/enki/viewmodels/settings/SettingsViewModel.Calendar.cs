using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Ajustes › Calendario (docs/calendar-feature/09-configuracion-y-ajustes.md §1): vista por
/// defecto, primer día de semana, fin de semana visible, horario laboral y formato de hora, más la barra de
/// desplazamiento del sidebar del propio Calendario — split out of <c>SettingsViewModel.cs</c> so this
/// page's properties and handlers live together instead of interleaved with Apariencia/Editor/
/// Notificaciones. Still the very same <see cref="SettingsViewModel"/> class — partial types share every
/// field/property regardless of which file declares them — so no behavior change.</summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    private bool _calendarShowSidebarScrollbar;

    // ===================== Calendario (docs/calendar-feature/09-configuracion-y-ajustes.md §1) =====================

    [ObservableProperty]
    private CalendarViewMode _calendarDefaultView;

    [ObservableProperty]
    private bool _calendarWeekStartsOnMonday;

    [ObservableProperty]
    private bool _calendarShowWeekends;

    [ObservableProperty]
    private double _calendarWorkingHoursStart;

    [ObservableProperty]
    private double _calendarWorkingHoursEnd;

    [ObservableProperty]
    private bool _calendarTimeFormat24h;

    partial void OnCalendarShowSidebarScrollbarChanged(bool value)
    {
        _settingsService.Settings.CalendarShowSidebarScrollbar = value;
        _settingsService.Save();
        _onCalendarShowSidebarScrollbarChanged(value);
    }

    /// <summary>Only ever changed from <see cref="SetCalendarDefaultView"/> below (a segmented control,
    /// not a two-way toggle) — doesn't call <see cref="_onCalendarSettingsChanged"/> since it only affects
    /// which view the Calendario page opens on next, never anything about an already-open page.</summary>
    partial void OnCalendarDefaultViewChanged(CalendarViewMode value)
    {
        _settingsService.Settings.CalendarDefaultView = value.ToString();
        _settingsService.Save();
    }

    [RelayCommand]
    private void SetCalendarDefaultView(CalendarViewMode mode) => CalendarDefaultView = mode;

    partial void OnCalendarWeekStartsOnMondayChanged(bool value)
    {
        _settingsService.Settings.CalendarWeekStartsOnMonday = value;
        _settingsService.Save();
        _onCalendarSettingsChanged();
    }

    partial void OnCalendarShowWeekendsChanged(bool value)
    {
        _settingsService.Settings.CalendarShowWeekends = value;
        _settingsService.Save();
        _onCalendarSettingsChanged();
    }

    /// <summary>Un horario laboral invertido o vacío (inicio ≥ fin) dejaría al Calendario sin franja que
    /// resaltar, así que en vez de guardarlo tal cual, el otro extremo se adelanta/retrasa lo justo para
    /// mantener <c>fin &gt; inicio</c> — mismo criterio de "no permitir un estado absurdo" que ya aplica
    /// <c>KanbanCardEditorViewModel</c> a sus propios campos. El extremo ajustado dispara su propio
    /// <c>On*Changed</c>, que ya persiste y notifica —
    /// por eso esta rama sale con <c>return</c> sin duplicar el <c>Save()</c>.</summary>
    partial void OnCalendarWorkingHoursStartChanged(double value)
    {
        _settingsService.Settings.CalendarWorkingHoursStart = (int)value;
        if (value >= CalendarWorkingHoursEnd)
        {
            CalendarWorkingHoursEnd = Math.Min(24, value + 1);
            return;
        }
        DebouncedSave();
        _onCalendarSettingsChanged();
    }

    partial void OnCalendarWorkingHoursEndChanged(double value)
    {
        _settingsService.Settings.CalendarWorkingHoursEnd = (int)value;
        if (value <= CalendarWorkingHoursStart)
        {
            CalendarWorkingHoursStart = Math.Max(0, value - 1);
            return;
        }
        DebouncedSave();
        _onCalendarSettingsChanged();
    }

    partial void OnCalendarTimeFormat24hChanged(bool value)
    {
        _settingsService.Settings.CalendarTimeFormat24h = value;
        _settingsService.Save();
        _onCalendarSettingsChanged();
    }
}
