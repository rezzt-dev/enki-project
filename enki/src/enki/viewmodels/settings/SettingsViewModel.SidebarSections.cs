using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;

namespace enki.ViewModels;

/// <summary>Secciones de la barra lateral (Ajustes › General): every toggleable sidebar-section flag
/// (Dashboard/Calendario/Kanban/Notas/Archivos/Sesiones/Papelera/Hábitos/Estadísticas/Recordatorios/
/// Notificaciones/Skills, plus the "mostrar etiquetas" toggle for Gestión de tareas), the two drag-and-drop
/// ordered lists (<see cref="MainSidebarSections"/>/<see cref="BottomSidebarSections"/>) backing them, and
/// the guard that refuses to hide the last section still visible in the main group — split out of
/// <c>SettingsViewModel.cs</c> purely to keep that file from being one long list of near-identical toggle
/// handlers. Still the very same <see cref="SettingsViewModel"/> class — partial types share every
/// field/property regardless of which file declares them — so no wiring or persistence behavior changed by
/// this split.</summary>
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

    /// <summary>Ajustes › General › Secciones de la barra lateral: the main scrollable group, ordered and
    /// filtered by <see cref="Models.AppSettings.SidebarMainSectionOrder"/>. Dragging a row here from
    /// <see cref="BottomSidebarSections"/> (or its own pin/unpin button) moves it between groups.</summary>
    public ObservableCollection<SidebarSectionConfigViewModel> MainSidebarSections { get; } = [];

    /// <summary>The fixed bottom group (max <see cref="SidebarSectionCatalog.MaxBottomSections"/>, next to
    /// Ajustes), ordered and filtered by <see cref="Models.AppSettings.SidebarBottomSectionOrder"/>.</summary>
    public ObservableCollection<SidebarSectionConfigViewModel> BottomSidebarSections { get; } = [];

    /// <summary>Minimum number of sections that must stay visible in <see cref="MainSidebarSections"/> — an
    /// app with the whole main group hidden would still technically work (Ajustes itself is reachable from
    /// its own title-bar button, not a <c>NavItems</c> entry), but would look broken, so
    /// <see cref="BlockIfLastEnabledSidebarSection"/> refuses to turn off the last one still on. The bottom
    /// group has no such minimum — hiding all of it still leaves the full main group and Ajustes
    /// reachable — which is why this guard only ever fires for whichever key currently lives in
    /// <see cref="MainSidebarSections"/>, not a fixed set of pages.</summary>
    private const int MinEnabledSidebarSections = 1;

    /// <summary>Shared guard for every sidebar-section toggle below: refuses to turn off the last section
    /// still visible in <see cref="MainSidebarSections"/> — reverting the just-set
    /// <see cref="ObservableProperty"/> back to <c>true</c> (which re-enters this same partial method with
    /// <paramref name="value"/> true, a no-op for this check) and surfacing why via a toast — but only when
    /// <paramref name="key"/> currently lives in the main group; a key the user has dragged into
    /// <see cref="BottomSidebarSections"/> has no minimum. <paramref name="key"/>'s own row hasn't been
    /// synced to <paramref name="value"/> yet at this point (see <see cref="SyncSidebarSectionRowVisibility"/>),
    /// so the remaining count deliberately excludes it and asks directly: would turning this one off leave
    /// zero *other* visible rows in the main group?</summary>
    private bool BlockIfLastEnabledSidebarSection(string key, bool value, Action revert)
    {
        if (value) return false;
        if (MainSidebarSections.All(s => s.Key != key)) return false;
        if (MainSidebarSections.Any(s => s.Key != key && s.IsVisible)) return false;

        revert();
        _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Settings_General_Sidebar_CannotHideLast_Title"),
            LocalizedTextFormatter.Format("Settings_General_Sidebar_CannotHideLast_Message"),
            Models.NotificationSeverity.Warning);
        return true;
    }

    partial void OnDashboardPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection("Dashboard", value, () => DashboardPanelEnabled = true)) return;
        _settingsService.Settings.DashboardPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Dashboard, value);
        SyncSidebarSectionRow("Dashboard", value);
    }

    partial void OnCalendarPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection("Calendar", value, () => CalendarPanelEnabled = true)) return;
        _settingsService.Settings.CalendarPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Calendar, value);
        SyncSidebarSectionRow("Calendar", value);
    }

    partial void OnKanbanPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection("Kanban", value, () => KanbanPanelEnabled = true)) return;
        _settingsService.Settings.KanbanPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Kanban, value);
        SyncSidebarSectionRow("Kanban", value);
    }

    partial void OnNotesPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection("Notes", value, () => NotesPanelEnabled = true)) return;
        _settingsService.Settings.NotesPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Notes, value);
        SyncSidebarSectionRow("Notes", value);
    }

    partial void OnFilesPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection("Files", value, () => FilesPanelEnabled = true)) return;
        _settingsService.Settings.FilesPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Files, value);
        SyncSidebarSectionRow("Files", value);
    }

    partial void OnSkillsPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection("Skills", value, () => SkillsPanelEnabled = true)) return;
        _settingsService.Settings.SkillsPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Skills, value);
        SyncSidebarSectionRow("Skills", value);
    }

    partial void OnSessionsPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection("Sessions", value, () => SessionsPanelEnabled = true)) return;
        _settingsService.Settings.SessionsPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Sessions, value);
        SyncSidebarSectionRow("Sessions", value);
    }

    partial void OnTrashPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection("Trash", value, () => TrashPanelEnabled = true)) return;
        _settingsService.Settings.TrashPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Trash, value);
        SyncSidebarSectionRow("Trash", value);
    }

    partial void OnHabitsPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection("Habits", value, () => HabitsPanelEnabled = true)) return;
        _settingsService.Settings.HabitsPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Habits, value);
        SyncSidebarSectionRow("Habits", value);
    }

    partial void OnStatisticsPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection("Statistics", value, () => StatisticsPanelEnabled = true)) return;
        _settingsService.Settings.StatisticsPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Statistics, value);
        SyncSidebarSectionRow("Statistics", value);
    }

    partial void OnRemindersSidebarEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection("Reminders", value, () => RemindersSidebarEnabled = true)) return;
        _settingsService.Settings.RemindersSidebarEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Reminders, value);
        SyncSidebarSectionRow("Reminders", value);
    }

    partial void OnNotificationsSidebarEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection("Notifications", value, () => NotificationsSidebarEnabled = true)) return;
        _settingsService.Settings.NotificationsSidebarEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Notifications, value);
        SyncSidebarSectionRow("Notifications", value);
    }

    partial void OnPolarShowTaskTagsChanged(bool value)
    {
        _settingsService.Settings.PolarShowTaskTags = value;
        _settingsService.Save();
        _onPolarShowTaskTagsChanged(value);
    }

    /// <summary>Keeps a section's row in <see cref="MainSidebarSections"/>/<see cref="BottomSidebarSections"/>
    /// in sync with its underlying flag — called from every <c>On*Changed</c> handler above, since those
    /// flags are also settable directly from elsewhere in Ajustes (Calendario's and Skills' own pages both
    /// bind straight to <see cref="CalendarPanelEnabled"/>/<see cref="SkillsPanelEnabled"/>, bypassing
    /// <see cref="ToggleSidebarSectionVisibility"/> entirely), so this is the one place guaranteed to run
    /// no matter which surface changed the flag. A no-op before <see cref="BuildSidebarSections"/> has run
    /// (rows not built yet) or for a key that isn't a real section.</summary>
    private void SyncSidebarSectionRow(string key, bool value)
    {
        var row = MainSidebarSections.FirstOrDefault(s => s.Key == key) ?? BottomSidebarSections.FirstOrDefault(s => s.Key == key);
        if (row is not null) row.IsVisible = value;
    }

    /// <summary>Current on/off flag for a sidebar section key — the single switch statement every
    /// row-level command below goes through instead of each duplicating it.</summary>
    private bool GetSidebarSectionFlag(string key) => key switch
    {
        "Dashboard" => DashboardPanelEnabled,
        "Calendar" => CalendarPanelEnabled,
        "Kanban" => KanbanPanelEnabled,
        "Notes" => NotesPanelEnabled,
        "Files" => FilesPanelEnabled,
        "Sessions" => SessionsPanelEnabled,
        "Trash" => TrashPanelEnabled,
        "Habits" => HabitsPanelEnabled,
        "Skills" => SkillsPanelEnabled,
        "Reminders" => RemindersSidebarEnabled,
        "Notifications" => NotificationsSidebarEnabled,
        "Statistics" => StatisticsPanelEnabled,
        _ => true
    };

    /// <summary>Routes through the real <see cref="ObservableProperty"/> setter for <paramref name="key"/>
    /// so its own <c>On*Changed</c> handler above runs exactly as if the user had flipped that specific
    /// toggle directly — same guard, same save, same <see cref="_onSidebarSectionChanged"/> callback —
    /// instead of duplicating any of that here.</summary>
    private void SetSidebarSectionFlag(string key, bool value)
    {
        switch (key)
        {
            case "Dashboard": DashboardPanelEnabled = value; break;
            case "Calendar": CalendarPanelEnabled = value; break;
            case "Kanban": KanbanPanelEnabled = value; break;
            case "Notes": NotesPanelEnabled = value; break;
            case "Files": FilesPanelEnabled = value; break;
            case "Sessions": SessionsPanelEnabled = value; break;
            case "Trash": TrashPanelEnabled = value; break;
            case "Habits": HabitsPanelEnabled = value; break;
            case "Skills": SkillsPanelEnabled = value; break;
            case "Reminders": RemindersSidebarEnabled = value; break;
            case "Notifications": NotificationsSidebarEnabled = value; break;
            case "Statistics": StatisticsPanelEnabled = value; break;
        }
    }

    /// <summary>Builds <see cref="MainSidebarSections"/>/<see cref="BottomSidebarSections"/> from
    /// <see cref="Models.AppSettings.SidebarMainSectionOrder"/>/<see cref="Models.AppSettings.SidebarBottomSectionOrder"/>
    /// via <see cref="SidebarSectionCatalog.Resolve"/>.</summary>
    private void BuildSidebarSections()
    {
        var settings = _settingsService.Settings;
        var (main, bottom) = SidebarSectionCatalog.Resolve(settings.SidebarMainSectionOrder, settings.SidebarBottomSectionOrder);

        MainSidebarSections.Clear();
        foreach (var key in main) MainSidebarSections.Add(BuildSidebarSectionRow(key));

        BottomSidebarSections.Clear();
        foreach (var key in bottom) BottomSidebarSections.Add(BuildSidebarSectionRow(key));

        RefreshSidebarSectionsSummary();
    }

    private SidebarSectionConfigViewModel BuildSidebarSectionRow(string key)
    {
        var meta = SidebarSectionCatalog.Meta[key];
        return new SidebarSectionConfigViewModel
        {
            Key = key,
            Title = LocalizationService.Instance[meta.LabelKey],
            Icon = meta.Icon,
            IsVisible = GetSidebarSectionFlag(key)
        };
    }

    /// <summary>Recomputes every row's <c>Position</c>/<c>IsFirst</c>/<c>IsLast</c> within its own group —
    /// called after every build/reorder/move so the "01/02/…" index badges and the up/down button states
    /// never fall out of sync with <see cref="MainSidebarSections"/>/<see cref="BottomSidebarSections"/>.</summary>
    private void RefreshSidebarSectionsSummary()
    {
        for (var i = 0; i < MainSidebarSections.Count; i++)
        {
            MainSidebarSections[i].Position = i + 1;
            MainSidebarSections[i].IsFirst = i == 0;
            MainSidebarSections[i].IsLast = i == MainSidebarSections.Count - 1;
        }

        for (var i = 0; i < BottomSidebarSections.Count; i++)
        {
            BottomSidebarSections[i].Position = i + 1;
            BottomSidebarSections[i].IsFirst = i == 0;
            BottomSidebarSections[i].IsLast = i == BottomSidebarSections.Count - 1;
        }
    }

    /// <summary>Persists the current group membership/order of both lists.</summary>
    private void SaveSidebarSectionsLayout()
    {
        _settingsService.Settings.SidebarMainSectionOrder = MainSidebarSections.Select(s => s.Key).ToList();
        _settingsService.Settings.SidebarBottomSectionOrder = BottomSidebarSections.Select(s => s.Key).ToList();
        _settingsService.Save();
        RefreshSidebarSectionsSummary();
        _onSidebarSectionOrderChanged();
    }

    private ObservableCollection<SidebarSectionConfigViewModel>? FindSidebarSectionList(string key)
    {
        if (MainSidebarSections.Any(s => s.Key == key)) return MainSidebarSections;
        if (BottomSidebarSections.Any(s => s.Key == key)) return BottomSidebarSections;
        return null;
    }

    private void WarnBottomSidebarGroupFull() => _notificationService.PushLocalized(
        LocalizedTextFormatter.Format("Settings_General_Sidebar_BottomGroupFull_Title"),
        LocalizedTextFormatter.Format("Settings_General_Sidebar_BottomGroupFull_Message"),
        Models.NotificationSeverity.Warning);

    /// <summary>Moves <paramref name="sourceKey"/> to the position of <paramref name="targetKey"/>,
    /// inserting it before the target — within the same group if both keys already live in it, or across
    /// groups (removing from one, inserting into the other) otherwise. Used by the drag-and-drop reordering
    /// in Ajustes › General › Secciones de la barra lateral. A cross-group move into the bottom group is
    /// refused once it's already at <see cref="SidebarSectionCatalog.MaxBottomSections"/>.</summary>
    public void MoveSidebarSection(string sourceKey, string targetKey, bool targetIsBottomGroup)
    {
        if (sourceKey == targetKey) return;
        var sourceList = FindSidebarSectionList(sourceKey);
        var source = sourceList?.FirstOrDefault(s => s.Key == sourceKey);
        if (source is null) return;

        var targetList = targetIsBottomGroup ? BottomSidebarSections : MainSidebarSections;

        if (ReferenceEquals(sourceList, targetList))
        {
            var target = targetList.FirstOrDefault(s => s.Key == targetKey);
            if (target is null) return;
            var oldIndex = targetList.IndexOf(source);
            var newIndex = targetList.IndexOf(target);
            targetList.RemoveAt(oldIndex);
            if (newIndex > oldIndex) newIndex--;
            targetList.Insert(newIndex, source);
        }
        else
        {
            if (targetIsBottomGroup && BottomSidebarSections.Count >= SidebarSectionCatalog.MaxBottomSections)
            {
                WarnBottomSidebarGroupFull();
                return;
            }

            sourceList!.Remove(source);
            var target = targetList.FirstOrDefault(s => s.Key == targetKey);
            var insertIndex = target is null ? targetList.Count : targetList.IndexOf(target);
            targetList.Insert(insertIndex, source);
        }

        SaveSidebarSectionsLayout();
    }

    /// <summary>Appends <paramref name="sourceKey"/> to the end of the other group — used when a row is
    /// dropped in the empty space of an <c>ItemsControl</c> rather than precisely onto another row (most
    /// often when the target group is empty, or the drop lands past the last row).</summary>
    public void MoveSidebarSectionToGroupEnd(string sourceKey, bool targetIsBottomGroup)
    {
        var sourceList = FindSidebarSectionList(sourceKey);
        var source = sourceList?.FirstOrDefault(s => s.Key == sourceKey);
        if (source is null) return;

        var targetList = targetIsBottomGroup ? BottomSidebarSections : MainSidebarSections;
        if (ReferenceEquals(sourceList, targetList)) return;

        if (targetIsBottomGroup && BottomSidebarSections.Count >= SidebarSectionCatalog.MaxBottomSections)
        {
            WarnBottomSidebarGroupFull();
            return;
        }

        sourceList!.Remove(source);
        targetList.Add(source);
        SaveSidebarSectionsLayout();
    }

    /// <summary>Swaps a section one slot earlier within its own group — the keyboard/mouse-accessible
    /// counterpart to dragging the row's handle, exposed as its own button per row.</summary>
    [RelayCommand]
    private void MoveSidebarSectionUp(string key)
    {
        var list = FindSidebarSectionList(key);
        var item = list?.FirstOrDefault(s => s.Key == key);
        if (list is null || item is null) return;

        var index = list.IndexOf(item);
        if (index <= 0) return;

        list.Move(index, index - 1);
        SaveSidebarSectionsLayout();
    }

    /// <summary>Swaps a section one slot later within its own group — see <see cref="MoveSidebarSectionUp"/>.</summary>
    [RelayCommand]
    private void MoveSidebarSectionDown(string key)
    {
        var list = FindSidebarSectionList(key);
        var item = list?.FirstOrDefault(s => s.Key == key);
        if (list is null || item is null) return;

        var index = list.IndexOf(item);
        if (index < 0 || index >= list.Count - 1) return;

        list.Move(index, index + 1);
        SaveSidebarSectionsLayout();
    }

    /// <summary>Pins a main-group section to the bottom group — the button-accessible counterpart to
    /// dragging it across lists. Refused once the bottom group is already full.</summary>
    [RelayCommand]
    private void PinSidebarSectionToBottom(string key)
    {
        var source = MainSidebarSections.FirstOrDefault(s => s.Key == key);
        if (source is null) return;

        if (BottomSidebarSections.Count >= SidebarSectionCatalog.MaxBottomSections)
        {
            WarnBottomSidebarGroupFull();
            return;
        }

        MainSidebarSections.Remove(source);
        BottomSidebarSections.Add(source);
        SaveSidebarSectionsLayout();
    }

    /// <summary>Moves a bottom-group section back into the main scrollable list — see
    /// <see cref="PinSidebarSectionToBottom"/>.</summary>
    [RelayCommand]
    private void UnpinSidebarSectionFromBottom(string key)
    {
        var source = BottomSidebarSections.FirstOrDefault(s => s.Key == key);
        if (source is null) return;

        BottomSidebarSections.Remove(source);
        MainSidebarSections.Add(source);
        SaveSidebarSectionsLayout();
    }

    /// <summary>Toggles visibility of a sidebar section, routed through <see cref="SetSidebarSectionFlag"/>
    /// so the existing per-key guard/save/callback fires exactly as if the user had flipped that section's
    /// own <see cref="ObservableProperty"/> toggle — including <see cref="SyncSidebarSectionRow"/>, which
    /// that same property's <c>On*Changed</c> handler already calls, so a block (guard fired, value
    /// reverted) is reflected back in the row instead of leaving the switch visually toggled.</summary>
    [RelayCommand]
    private void ToggleSidebarSectionVisibility(string key) => SetSidebarSectionFlag(key, !GetSidebarSectionFlag(key));
}
