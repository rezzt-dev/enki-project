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
        NotificationCenter.RefreshLocalizedLabels();
        ReminderInbox.RefreshLocalizedLabels();

        var activePages = NavItems.Where(n => n.IsActive).Select(n => n.Page).ToHashSet();
        NavItems.Clear();
        NavItems.Add(new NavItemViewModel { Label = LocalizationService.Instance["Nav_Dashboard"], Icon = MaterialIconKind.ViewDashboardOutline, Page = AppPage.Dashboard });
        if (CalendarPanelEnabled)
            NavItems.Add(new NavItemViewModel { Label = LocalizationService.Instance["Nav_Calendar"], Icon = MaterialIconKind.CalendarBlankOutline, Page = AppPage.Calendar });
        NavItems.Add(new NavItemViewModel { Label = LocalizationService.Instance["Nav_Kanban"], Icon = MaterialIconKind.ViewColumnOutline, Page = AppPage.Kanban });
        NavItems.Add(new NavItemViewModel { Label = LocalizationService.Instance["Nav_Documents"], Icon = MaterialIconKind.NoteTextOutline, Page = AppPage.Notes });
        NavItems.Add(new NavItemViewModel { Label = LocalizationService.Instance["Nav_Files"], Icon = MaterialIconKind.FileImageOutline, Page = AppPage.Files });
        if (SkillsPanelEnabled)
            NavItems.Add(new NavItemViewModel { Label = LocalizationService.Instance["Nav_Skills"], Icon = MaterialIconKind.Brain, Page = AppPage.Skills });
        NavItems.Add(new NavItemViewModel { Label = LocalizationService.Instance["Nav_Sessions"], Icon = MaterialIconKind.SitemapOutline, Page = AppPage.Sessions });
        NavItems.Add(new NavItemViewModel { Label = LocalizationService.Instance["Nav_Trash"], Icon = MaterialIconKind.TrashCanOutline, Page = AppPage.Trash });
        foreach (var item in NavItems) item.IsActive = activePages.Contains(item.Page);
        UpdateCalendarBadge();
        UpdateTrashBadge();
    }

    private void OpenNoteFromDashboard(NoteFile note)
    {
        Navigate(AppPage.Notes);
        Notes!.OpenNote(note);
    }

    /// <summary>A node in the session graph was clicked (open that note), or "Abrir en notas" was pressed
    /// (just switch context) — either way, land on the Notes page scoped to that session.</summary>
    private void OpenSessionInNotes(Session session, NoteFile? note)
    {
        Navigate(AppPage.Notes);
        Notes!.SelectSession(session.Id);
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

    /// <summary>Shows/hides the "Calendario" nav entry entirely (docs/calendar-feature/
    /// 09-configuracion-y-ajustes.md §1) — filters <see cref="NavItems"/> itself rather than adding a
    /// per-item visibility flag to <see cref="NavItemViewModel"/>, since that's the only member of the
    /// collection that's ever conditionally shown; every other entry is unconditional. Redirects to
    /// Dashboard if the user happened to be on the Calendario page when it gets disabled.</summary>
    private void OnCalendarPanelModeChanged(bool value)
    {
        CalendarPanelEnabled = value;
        Dashboard!.SetCalendarEnabled(value);
        var existing = NavItems.FirstOrDefault(n => n.Page == AppPage.Calendar);

        if (value && existing is null)
        {
            NavItems.Insert(1, new NavItemViewModel { Label = LocalizationService.Instance["Nav_Calendar"], Icon = MaterialIconKind.CalendarBlankOutline, Page = AppPage.Calendar });
            UpdateCalendarBadge();
        }
        else if (!value && existing is not null)
        {
            var wasActive = existing.IsActive;
            NavItems.Remove(existing);
            if (wasActive) Navigate(AppPage.Dashboard);
        }
    }

    /// <summary>Shows/hides la entrada "Skills" del sidebar (docs/ia-skills/especificacion-desarrollo.md
    /// §8/§9) — mismo patrón "filtra NavItems directamente" que <see cref="OnCalendarPanelModeChanged"/>.
    /// La primera vez que se activa, copia las skills por defecto al vault activo.</summary>
    private void OnSkillsPanelModeChanged(bool value)
    {
        SkillsPanelEnabled = value;
        var existing = NavItems.FirstOrDefault(n => n.Page == AppPage.Skills);

        if (value && existing is null)
        {
            var filesIndex = NavItems.ToList().FindIndex(n => n.Page == AppPage.Files);
            var insertAt = filesIndex >= 0 ? filesIndex + 1 : NavItems.Count;
            NavItems.Insert(insertAt, new NavItemViewModel { Label = LocalizationService.Instance["Nav_Skills"], Icon = MaterialIconKind.Brain, Page = AppPage.Skills });
            Skills?.EnsureDefaultSkills();
        }
        else if (!value && existing is not null)
        {
            var wasActive = existing.IsActive;
            NavItems.Remove(existing);
            if (wasActive) Navigate(AppPage.Dashboard);
        }
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
