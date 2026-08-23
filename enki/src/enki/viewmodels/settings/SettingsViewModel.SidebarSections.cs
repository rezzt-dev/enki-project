using System;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;

namespace enki.ViewModels;

/// <summary>Secciones de la barra lateral (Ajustes › General): every toggleable sidebar-section flag
/// (Dashboard/Calendario/Kanban/Notas/Archivos/Sesiones/Papelera/Hábitos/Estadísticas/Recordatorios/
/// Notificaciones/Skills, plus the "mostrar etiquetas" toggle for Gestión de tareas) and the guard that
/// refuses to hide the last one still visible — split out of <c>SettingsViewModel.cs</c> purely to keep
/// that file from being one long list of near-identical toggle handlers. Still the very same
/// <see cref="SettingsViewModel"/> class — partial types share every field/property regardless of which
/// file declares them — so no wiring or persistence behavior changed by this split.</summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    private bool _calendarPanelEnabled;

    [ObservableProperty]
    private bool _skillsPanelEnabled;

    // ===================== Secciones de la barra lateral (Ajustes › General) =====================

    [ObservableProperty]
    private bool _dashboardPanelEnabled;

    [ObservableProperty]
    private bool _kanbanPanelEnabled;

    [ObservableProperty]
    private bool _notesPanelEnabled;

    [ObservableProperty]
    private bool _filesPanelEnabled;

    [ObservableProperty]
    private bool _sessionsPanelEnabled;

    [ObservableProperty]
    private bool _trashPanelEnabled;

    [ObservableProperty]
    private bool _habitsPanelEnabled;

    [ObservableProperty]
    private bool _statisticsPanelEnabled;

    [ObservableProperty]
    private bool _remindersSidebarEnabled;

    [ObservableProperty]
    private bool _notificationsSidebarEnabled;

    [ObservableProperty]
    private bool _polarShowTaskTags;

    /// <summary>Minimum number of sidebar sections (Ajustes › General › Secciones de la barra lateral) that
    /// must stay visible — an app with every entry hidden would still technically work (Ajustes itself is
    /// reachable from its own title-bar button, not a <c>NavItems</c> entry), but would look broken, so each
    /// <c>OnXPanelEnabledChanged</c> below refuses to turn off the last one still on.</summary>
    private const int MinEnabledSidebarSections = 1;

    private int CountEnabledSidebarSections()
    {
        var s = _settingsService.Settings;
        var count = 0;
        if (s.DashboardPanelEnabled) count++;
        if (s.CalendarPanelEnabled) count++;
        if (s.KanbanPanelEnabled) count++;
        if (s.NotesPanelEnabled) count++;
        if (s.FilesPanelEnabled) count++;
        if (s.SkillsPanelEnabled) count++;
        if (s.SessionsPanelEnabled) count++;
        if (s.TrashPanelEnabled) count++;
        if (s.HabitsPanelEnabled) count++;
        if (s.RemindersSidebarEnabled) count++;
        if (s.NotificationsSidebarEnabled) count++;
        if (s.StatisticsPanelEnabled) count++;
        return count;
    }

    /// <summary>Shared guard for every sidebar-section toggle below: refuses to turn the last remaining one
    /// off, reverting the just-set <see cref="ObservableProperty"/> back to <c>true</c> (which re-enters this
    /// same partial method with <paramref name="value"/> true, a no-op for this check) and surfacing why via
    /// a toast, instead of silently emptying the sidebar down to nothing but the toggle the user is looking
    /// at right now.</summary>
    private bool BlockIfLastEnabledSidebarSection(bool value, Action revert)
    {
        if (value || CountEnabledSidebarSections() > MinEnabledSidebarSections) return false;

        revert();
        _notificationService.Push(
            LocalizationService.Instance["Settings_General_Sidebar_CannotHideLast_Title"],
            LocalizationService.Instance["Settings_General_Sidebar_CannotHideLast_Message"],
            Models.NotificationSeverity.Warning);
        return true;
    }

    partial void OnDashboardPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => DashboardPanelEnabled = true)) return;
        _settingsService.Settings.DashboardPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Dashboard, value);
    }

    partial void OnCalendarPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => CalendarPanelEnabled = true)) return;
        _settingsService.Settings.CalendarPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Calendar, value);
    }

    partial void OnKanbanPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => KanbanPanelEnabled = true)) return;
        _settingsService.Settings.KanbanPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Kanban, value);
    }

    partial void OnNotesPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => NotesPanelEnabled = true)) return;
        _settingsService.Settings.NotesPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Notes, value);
    }

    partial void OnFilesPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => FilesPanelEnabled = true)) return;
        _settingsService.Settings.FilesPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Files, value);
    }

    partial void OnSkillsPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => SkillsPanelEnabled = true)) return;
        _settingsService.Settings.SkillsPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Skills, value);
    }

    partial void OnSessionsPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => SessionsPanelEnabled = true)) return;
        _settingsService.Settings.SessionsPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Sessions, value);
    }

    partial void OnTrashPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => TrashPanelEnabled = true)) return;
        _settingsService.Settings.TrashPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Trash, value);
    }

    partial void OnHabitsPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => HabitsPanelEnabled = true)) return;
        _settingsService.Settings.HabitsPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Habits, value);
    }

    partial void OnStatisticsPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => StatisticsPanelEnabled = true)) return;
        _settingsService.Settings.StatisticsPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Statistics, value);
    }

    partial void OnRemindersSidebarEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => RemindersSidebarEnabled = true)) return;
        _settingsService.Settings.RemindersSidebarEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Reminders, value);
    }

    partial void OnNotificationsSidebarEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => NotificationsSidebarEnabled = true)) return;
        _settingsService.Settings.NotificationsSidebarEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Notifications, value);
    }

    partial void OnPolarShowTaskTagsChanged(bool value)
    {
        _settingsService.Settings.PolarShowTaskTags = value;
        _settingsService.Save();
        _onPolarShowTaskTagsChanged(value);
    }
}
