using System.Globalization;
using System.Linq;
using enki.Localization;
using enki.Models;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Cross-module glue passed as delegates into the page-level viewmodels constructed in
/// <c>MainWindowViewModel.Composition.cs</c> (e.g. <c>OpenKanbanCard</c> handed to
/// <see cref="ReminderInboxViewModel"/>, <c>OnVaultChanged</c> handed to <see cref="SettingsViewModel"/>) —
/// split out of <c>MainWindowViewModel.cs</c> in Fase 6 (gap 31) so that file only has to hold the shell's
/// own runtime state and commands, not also every "navigate to X and open Y" callback every child viewmodel
/// needs. Same partial-class split as <c>MainWindowViewModel.Composition.cs</c>: one class, shared fields,
/// no behavior change.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Coordina el refresco de etiquetas localizadas en todo el shell cuando el usuario cambia de
    /// idioma desde Ajustes (i18n Fase 4). Se invoca desde <see cref="SettingsViewModel"/> en vez de que
    /// cada ViewModel se suscriba directamente al singleton <see cref="LocalizationService"/>, para evitar
    /// fallos intermitentes en tests paralelos causados por instancias obsoletas reaccionando a eventos
    /// globales.</summary>
    private void RefreshAllLocalizedLabels()
    {
        Dashboard?.RefreshLocalizedLabels();
        Calendar?.RefreshLocalizedLabels();
        Notes?.RefreshLocalizedLabels();
        Files?.RefreshLocalizedLabels();
        Skills?.RefreshLocalizedLabels();
        Sessions?.RefreshLocalizedLabels();
        Kanban?.RefreshLocalizedLabels();
        Kanban?.TaskManagement.RefreshLocalizedLabels();
        Trash?.RefreshLocalizedLabels();
        Reminders?.RefreshLocalizedLabels();
        Notifications?.RefreshLocalizedLabels();
        Settings?.RefreshLocalizedLabels();
        Habits?.RefreshLocalizedLabels();
        Statistics?.RefreshLocalizedLabels();
        NotificationCenter.RefreshLocalizedLabels();
        ReminderInbox.RefreshLocalizedLabels();

        RebuildNavItems();
    }

    /// <summary>Icon/label-key for every toggleable sidebar section in the first (scrollable) group
    /// (Ajustes › General › Secciones de la barra lateral), in the fixed order they always appear in
    /// <see cref="NavItems"/> — the single source of truth <see cref="RebuildNavItems"/> and
    /// <see cref="IsSidebarSectionEnabled"/> both read from. Recordatorios, Notificaciones, Estadísticas y
    /// Ajustes forman un segundo grupo fijo en <see cref="MainWindow.axaml"/>; Ajustes nunca se oculta.</summary>
    private static readonly (AppPage Page, MaterialIconKind Icon, string LabelKey)[] SidebarSectionDefinitions =
    [
        (AppPage.Dashboard, MaterialIconKind.ViewDashboardOutline, "Nav_Dashboard"),
        (AppPage.Calendar, MaterialIconKind.CalendarBlankOutline, "Nav_Calendar"),
        (AppPage.Kanban, MaterialIconKind.ViewColumnOutline, "Nav_Kanban"),
        (AppPage.Notes, MaterialIconKind.NoteTextOutline, "Nav_Documents"),
        (AppPage.Files, MaterialIconKind.FileImageOutline, "Nav_Files"),
        (AppPage.Sessions, MaterialIconKind.SitemapOutline, "Nav_Sessions"),
        (AppPage.Habits, MaterialIconKind.ClipboardCheckOutline, "Nav_Habits"),
        (AppPage.Trash, MaterialIconKind.TrashCanOutline, "Nav_Trash"),
        (AppPage.Skills, MaterialIconKind.Brain, "Nav_Skills")
    ];

    private bool IsSidebarSectionEnabled(AppPage page) => page switch
    {
        AppPage.Dashboard => DashboardPanelEnabled,
        AppPage.Calendar => CalendarPanelEnabled,
        AppPage.Kanban => KanbanPanelEnabled,
        AppPage.Notes => NotesPanelEnabled,
        AppPage.Files => FilesPanelEnabled,
        AppPage.Skills => SkillsPanelEnabled,
        AppPage.Sessions => SessionsPanelEnabled,
        AppPage.Trash => TrashPanelEnabled,
        AppPage.Habits => HabitsPanelEnabled,
        _ => true
    };

    /// <summary>Rebuilds <see cref="NavItems"/> from scratch off the current *PanelEnabled flags, in the
    /// fixed order of <see cref="SidebarSectionDefinitions"/> — used both at startup
    /// (<see cref="InitializeShell"/>) and every time a toggle changes (<see cref="OnSidebarSectionChanged"/>)
    /// or the UI language changes (<see cref="RefreshAllLocalizedLabels"/>), instead of the old approach of
    /// inserting/removing one item at a specific index per toggle, which didn't scale past the original two
    /// (Calendario/Skills).</summary>
    private void RebuildNavItems()
    {
        var activePages = NavItems.Where(n => n.IsActive).Select(n => n.Page).ToHashSet();
        NavItems.Clear();
        foreach (var def in SidebarSectionDefinitions)
        {
            if (!IsSidebarSectionEnabled(def.Page)) continue;
            NavItems.Add(new NavItemViewModel { Label = LocalizationService.Instance[def.LabelKey], Icon = def.Icon, Page = def.Page });
        }
        foreach (var item in NavItems) item.IsActive = activePages.Contains(item.Page);
        UpdateCalendarBadge();
        UpdateTrashBadge();
    }

    private void OpenNoteFromDashboard(NoteFile note)
    {
        Navigate(AppPage.Notes);
        Notes!.OpenNote(note);
    }

    /// <summary>A node in the global vault graph was clicked (open that note), or "Abrir en notas" was
    /// pressed (just switch context) — either way, land on the Notes page scoped to that session. A null
    /// <paramref name="session"/> means the note lives at the vault root outside any session — the Notes
    /// page already treats a null session id as "select the vault workspace" (<c>NotesViewModel.SelectSession</c>).</summary>
    private void OpenSessionInNotes(Session? session, NoteFile? note)
    {
        Navigate(AppPage.Notes);
        Notes!.SelectSession(session?.Id);
        if (note is not null) Notes!.OpenNote(note);
    }

    private void OnSessionsChanged()
    {
        Notes!.ReloadSessions();
        Files!.ReloadSessions();
    }

    private void OnTitleBarModeChanged(bool value) => UseCustomTitleBar = value;

    private void OnNotificationsPanelModeChanged(bool value) => NotificationsPanelEnabled = value;

    private void OnRemindersPanelModeChanged(bool value) => RemindersPanelEnabled = value;

    private void OnCalendarShowSidebarScrollbarModeChanged(bool value) => Calendar!.ShowSidebarScrollbar = value;

    private void OnDashboardShowScrollbarModeChanged(bool value) => Dashboard!.ShowScrollbar = value;

    private void OnPolarShowTaskTagsChanged(bool value) => Kanban!.TaskManagement.ShowTagFilter = value;

    /// <summary>Single callback for every sidebar-section visibility toggle (Ajustes › General › Secciones
    /// de la barra lateral) — applies the page-specific side effect a couple of sections need (Calendario
    /// tells Dashboard's own calendar widget to hide too; Skills seeds the vault's default skills the first
    /// time it's turned on), then rebuilds <see cref="NavItems"/> and, if the section that just got hidden
    /// was the one on screen, redirects to whichever section is now first in the nav (or does nothing if the
    /// user somehow got down to zero — <see cref="SettingsViewModel"/> already blocks disabling the last
    /// one, so that's only a defensive fallback here). Recordatorios, Notificaciones y Estadísticas se
    /// manejan fuera de <see cref="NavItems"/> como parte del grupo fijo del sidebar.</summary>
    private void OnSidebarSectionChanged(AppPage page, bool value)
    {
        switch (page)
        {
            case AppPage.Dashboard: DashboardPanelEnabled = value; break;
            case AppPage.Calendar:
                CalendarPanelEnabled = value;
                Dashboard!.SetCalendarEnabled(value);
                break;
            case AppPage.Kanban: KanbanPanelEnabled = value; break;
            case AppPage.Notes: NotesPanelEnabled = value; break;
            case AppPage.Files: FilesPanelEnabled = value; break;
            case AppPage.Skills:
                SkillsPanelEnabled = value;
                if (value) Skills?.EnsureDefaultSkills();
                break;
            case AppPage.Sessions: SessionsPanelEnabled = value; break;
            case AppPage.Trash: TrashPanelEnabled = value; break;
            case AppPage.Habits: HabitsPanelEnabled = value; break;
            case AppPage.Reminders: RemindersSidebarEnabled = value; break;
            case AppPage.Notifications: NotificationsSidebarEnabled = value; break;
            case AppPage.Statistics: StatisticsPanelEnabled = value; break;
        }

        var wasActive = page switch
        {
            AppPage.Reminders => IsRemindersActive,
            AppPage.Notifications => IsNotificationsActive,
            AppPage.Statistics => CurrentPage == Statistics,
            _ => NavItems.FirstOrDefault(n => n.Page == page)?.IsActive ?? false
        };

        RebuildNavItems();

        if (!value && wasActive && NavItems.FirstOrDefault()?.Page is { } fallback)
            Navigate(fallback);
    }

    /// <summary>Pushes every other live-reactive Calendario setting (docs/calendar-feature/
    /// 09-configuracion-y-ajustes.md §1) into the already-open <see cref="CalendarViewModel"/> — each
    /// property setter below triggers that viewmodel's own rebuild, same "explicit callback" pattern as
    /// <see cref="OnCalendarShowSidebarScrollbarModeChanged"/>.</summary>
    private void OnCalendarSettingsChanged()
    {
        var settings = _settingsService.Settings;
        Calendar!.WeekStartsOnMonday = settings.CalendarWeekStartsOnMonday;
        Calendar!.ShowWeekends = settings.CalendarShowWeekends;
        Calendar!.WorkingHoursStart = settings.CalendarWorkingHoursStart;
        Calendar!.WorkingHoursEnd = settings.CalendarWorkingHoursEnd;
        Calendar!.TimeFormat24h = settings.CalendarTimeFormat24h;
    }

    /// <summary>Lands on the Kanban page with a card's own read-only detail dialog already open, same one
    /// <c>KanbanViewModel.OpenCardDetailCommand</c> opens from the board itself — shared entry point for
    /// every "jump to this card" action that isn't already inside Kanban: the reminder inbox (see
    /// <see cref="ReminderInbox"/>'s constructor), a note's "Tareas vinculadas" section
    /// (<see cref="Notes"/>'s constructor) and a session's "Tarjetas vinculadas" panel
    /// (<see cref="Sessions"/>'s constructor).</summary>
    private void OpenKanbanCard(string cardId)
    {
        Navigate(AppPage.Kanban);
        Kanban?.OpenCardById(cardId);
    }

    /// <summary>"Ver nota vinculada" from the Kanban card editor/detail — opens the linked note on the
    /// Notes page, or surfaces a notification instead of failing silently if the saved path no
    /// longer points to a real file (the note was deleted, or moved outside what
    /// <see cref="KanbanService.RelinkNotePath"/> tracks).</summary>
    private void OpenLinkedNoteFromKanban(string notePath)
    {
        var note = _vaultService.GetNote(notePath);
        if (note is null)
        {
            _notificationService.Push(
                LocalizationService.Instance["Notification_LinkedNoteMissing_Title"],
                LocalizationService.Instance["Notification_LinkedNoteMissing_Message"],
                NotificationSeverity.Warning);
            return;
        }

        Navigate(AppPage.Notes);
        Notes!.OpenNote(note);
    }

    /// <summary>"Editar" from the calendar's detail popover, and "Ir a Kanban/Gestión de tareas" from its
    /// context menu (docs/calendar-feature/05-interacciones-y-microinteracciones.md §7,
    /// 06-creacion-y-edicion-de-items.md §3) — the calendar never reimplements a form of its own, it just
    /// navigates to whichever real screen owns this item's editor and opens it there, same
    /// navigate-then-open pattern as <see cref="OpenCardFromReminder"/>. A Polar subtask has no editor of
    /// its own, so it opens its parent task's editor instead (<see cref="CalendarItemViewModel.ParentSourceId"/>).</summary>
    private void OpenCalendarItemEditor(CalendarItemViewModel item)
    {
        switch (item.SourceType)
        {
            case CalendarItemSourceType.Kanban:
                Navigate(AppPage.Kanban);
                Kanban?.OpenCardEditorById(item.SourceId);
                break;
            case CalendarItemSourceType.PolarTask:
                Navigate(AppPage.Kanban);
                Kanban?.SetDisplayModeCommand.Execute("TaskManagement");
                Kanban?.TaskManagement.OpenTaskEditorById(item.SourceId);
                break;
            case CalendarItemSourceType.PolarSubtask:
                Navigate(AppPage.Kanban);
                Kanban?.SetDisplayModeCommand.Execute("TaskManagement");
                Kanban?.TaskManagement.OpenTaskEditorById(item.ParentSourceId);
                break;
            case CalendarItemSourceType.Reminder:
                OpenReminders();
                Reminders?.OpenReminderById(item.SourceId);
                break;
        }
    }

    /// <summary>Navigates to the module that owns the selected global search result and opens the
    /// specific item, reusing the same navigate-then-open pattern as <see cref="OpenCalendarItemEditor"/>.
    /// If the vault/shell hasn't initialized yet, the navigation still happens but the item won't open
    /// until its page is available.</summary>
    private void OnGlobalSearchResultSelected(GlobalSearchResultViewModel result)
    {
        switch (result.Type)
        {
            case GlobalSearchResultType.Note:
                Navigate(AppPage.Notes);
                if (result.Payload is NoteFile note) Notes?.OpenNote(note);
                break;
            case GlobalSearchResultType.KanbanCard:
                Navigate(AppPage.Kanban);
                Kanban?.OpenCardEditorById(result.Id);
                break;
            case GlobalSearchResultType.PolarTask:
                Navigate(AppPage.Kanban);
                Kanban?.SetDisplayModeCommand.Execute("TaskManagement");
                Kanban?.TaskManagement.OpenTaskEditorById(result.Id);
                break;
            case GlobalSearchResultType.Reminder:
                OpenReminders();
                Reminders?.OpenReminderById(result.Id);
                break;
        }
    }

    private void OnVaultChanged()
    {
        // Settings' "change vault folder" can point at any folder, old-version vault included — same
        // guarantee as a fresh app startup.
        _sessionService.EnsureDefaultSession();

        _vaultSettingsService.Reload();
        UiZoomScale = _vaultSettingsService.Settings.UiZoomPercent / 100.0;
        _habitService.Reload();

        // Any open editor tabs belong to the previous vault; keeping them open would show stale or
        // missing notes after the switch. Close them before reloading so the new vault starts clean.
        Notes!.CloseAllEditorsCommand.Execute(null);

        Dashboard!.Reload();
        Notes!.Reload();
        Notes!.ReloadSessions();
        Kanban!.Reload();
        Files!.Reload();
        Files!.ReloadSessions();
        Skills!.Reload();
        Sessions!.Reload();
        Trash!.Reload();
        Habits!.Reload();
        Statistics!.Reload();
        UpdateTrashBadge();

        _notificationService.Push(
            LocalizationService.Instance["Notification_VaultChanged_Title"],
            LocalizationService.Instance["Notification_VaultChanged_Message"],
            NotificationSeverity.Success);
    }

    /// <summary>Pops every reminder whose scheduled time has arrived and turns each into a real
    /// notification — see <see cref="ReminderService.PopDueReminders"/>. Runs on a 30s
    /// <see cref="Avalonia.Threading.DispatcherTimer"/> (see the constructor) plus once at startup, so a
    /// reminder that came due while the app was closed still fires as soon as it launches.</summary>
    private void CheckDueReminders()
    {
        var due = _reminderService.PopDueReminders();
        if (due.Count == 0) return;

        foreach (var reminder in due)
            _notificationService.Push(
                string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Notification_ReminderDue_Title"], reminder.Title),
                reminder.Description,
                NotificationSeverity.Info);

        Reminders?.Reload();
        Notifications?.Reload();
    }

    /// <summary>Callback pasado a <see cref="SettingsViewModel"/> (mismo patrón que
    /// <see cref="OnDashboardShowScrollbarModeChanged"/> etc.) — aplica en vivo el nuevo porcentaje al
    /// <c>ScaleTransform</c> que ya está en pantalla, sin esperar a la próxima navegación.</summary>
    private void OnUiZoomChanged(int percent) => UiZoomScale = percent / 100.0;

    private void OpenNoteFromQuickSwitcher(NoteFile note)
    {
        Navigate(AppPage.Notes);
        Notes?.OpenNote(note);
    }
}
