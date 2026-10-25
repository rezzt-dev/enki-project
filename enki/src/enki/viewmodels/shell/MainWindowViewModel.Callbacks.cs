using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using enki.Localization;
using enki.Models;
using enki.Services;

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
        TagManager?.RefreshLocalizedLabels();
        Reminders?.RefreshLocalizedLabels();
        Notifications?.RefreshLocalizedLabels();
        Settings?.RefreshLocalizedLabels();
        Habits?.RefreshLocalizedLabels();
        Statistics?.RefreshLocalizedLabels();
        Assistant?.RefreshLocalizedLabels();
        AiCliStatus.RefreshLocalizedLabels();
        NotificationCenter.RefreshLocalizedLabels();
        ReminderInbox.RefreshLocalizedLabels();
        VaultSwitcher.RefreshLocalizedLabels();
        QuickCapture.RefreshLocalizedLabels();

        RebuildNavItems();
        CommandPalette.RefreshLocalizedLabels();
        GlobalSearch.RefreshLocalizedLabels();
    }

    /// <summary>Whether the sidebar section for <paramref name="page"/> is currently shown — one flag per
    /// key in <see cref="SidebarSectionCatalog.AllKeys"/>, read by <see cref="RebuildNavItems"/> regardless
    /// of which group (main or bottom) the section currently lives in.</summary>
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
        AppPage.TagManager => TagManagerPanelEnabled,
        AppPage.Habits => HabitsPanelEnabled,
        AppPage.Reminders => RemindersSidebarEnabled,
        AppPage.Notifications => NotificationsSidebarEnabled,
        AppPage.Statistics => StatisticsPanelEnabled,
        AppPage.Assistant => AssistantPanelEnabled,
        _ => true
    };

    /// <summary>Rebuilds <see cref="NavItems"/>/<see cref="BottomNavItems"/> from scratch off
    /// <see cref="Models.AppSettings.SidebarMainSectionOrder"/>/<see cref="Models.AppSettings.SidebarBottomSectionOrder"/>
    /// (resolved via <see cref="SidebarSectionCatalog.Resolve"/>, so a missing/stale order list still falls
    /// back to sane defaults) and the current *PanelEnabled/*SidebarEnabled flags — used at startup
    /// (<see cref="InitializeShell"/>), every time a visibility toggle changes
    /// (<see cref="OnSidebarSectionChanged"/>), every drag/pin reorder in Ajustes › General › Secciones de
    /// la barra lateral (<see cref="SettingsViewModel"/>'s own <c>_onSidebarSectionOrderChanged</c>
    /// callback), and the UI language changes (<see cref="RefreshAllLocalizedLabels"/>).</summary>
    private void RebuildNavItems()
    {
        var (mainOrder, bottomOrder) = SidebarSectionCatalog.Resolve(
            _settingsService.Settings.SidebarMainSectionOrder, _settingsService.Settings.SidebarBottomSectionOrder);
        var activePages = NavItems.Concat(BottomNavItems).Where(n => n.IsActive).Select(n => n.Page).ToHashSet();

        NavItems.Clear();
        foreach (var key in mainOrder)
        {
            var page = Enum.Parse<AppPage>(key);
            if (!IsSidebarSectionEnabled(page)) continue;
            var meta = SidebarSectionCatalog.Meta[key];
            NavItems.Add(new NavItemViewModel { Label = LocalizationService.Instance[meta.LabelKey], Icon = meta.Icon, Page = page });
        }

        BottomNavItems.Clear();
        foreach (var key in bottomOrder)
        {
            var page = Enum.Parse<AppPage>(key);
            if (!IsSidebarSectionEnabled(page)) continue;
            var meta = SidebarSectionCatalog.Meta[key];
            BottomNavItems.Add(new NavItemViewModel { Label = LocalizationService.Instance[meta.LabelKey], Icon = meta.Icon, Page = page });
        }

        foreach (var item in NavItems) item.IsActive = activePages.Contains(item.Page);
        foreach (var item in BottomNavItems) item.IsActive = activePages.Contains(item.Page);
        UpdateCalendarBadge();
        UpdateTrashBadge();
        UpdateRemindersBadge();
        UpdateNotificationsBadge();
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
        Assistant?.RefreshScope();
        InvalidatePages(AppPage.Dashboard, AppPage.Sessions, AppPage.Statistics, AppPage.Trash);
    }

    /// <summary>El asistente acaba de cambiar algo del vault. Las acciones llegan en ráfagas (una por
    /// herramienta), así que el refresco de Notas y de las secciones que dependen del contenido se agrupa en
    /// uno solo, en segundo plano. Kanban, Polar y recordatorios ya se refrescan por sus propios eventos.</summary>
    private void OnAssistantChangedVault()
    {
        if (_assistantRefreshQueued) return;
        _assistantRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _assistantRefreshQueued = false;
            if (_shutdownCompleted) return;
            Notes?.Reload();
            InvalidatePages(AppPage.Dashboard, AppPage.Files, AppPage.Sessions, AppPage.Trash,
                AppPage.TagManager, AppPage.Statistics, AppPage.Skills);
        }, DispatcherPriority.Background);
    }

    private void OnTitleBarModeChanged(bool value) => UseCustomTitleBar = value;

    private void OnNotificationsPanelModeChanged(bool value) => NotificationsPanelEnabled = value;

    private void OnRemindersPanelModeChanged(bool value) => RemindersPanelEnabled = value;

    private void OnInboxPanelModeChanged(bool value) => InboxPanelEnabled = value;

    private void OnVaultSwitcherModeChanged(bool value) => VaultSwitcherEnabled = value;

    private void OnCalendarShowSidebarScrollbarModeChanged(bool value) => Calendar!.ShowSidebarScrollbar = value;

    private void OnDashboardShowScrollbarModeChanged(bool value) => Dashboard!.ShowScrollbar = value;

    private void OnKanbanBoardOrientationChanged(string value)
    {
        if (Enum.TryParse<KanbanBoardOrientation>(value, out var parsed)) Kanban!.BoardOrientation = parsed;
    }

    private void OnPolarShowTaskTagsChanged(bool value) => Kanban!.TaskManagement.ShowTagFilter = value;

    /// <summary>Single callback for every sidebar-section visibility toggle (Ajustes › General › Secciones
    /// de la barra lateral) — applies the page-specific side effect a couple of sections need (Calendario
    /// tells Dashboard's own calendar widget to hide too; Skills seeds the vault's default skills the first
    /// time it's turned on), then rebuilds <see cref="NavItems"/>/<see cref="BottomNavItems"/> and, if the
    /// section that just got hidden was the one on screen, redirects to whichever section is now first
    /// (main group, then bottom group) — or does nothing if the user somehow got down to zero
    /// (<see cref="SettingsViewModel"/> already blocks disabling the last one in the main group, so that's
    /// only a defensive fallback here).</summary>
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
            case AppPage.TagManager: TagManagerPanelEnabled = value; break;
            case AppPage.Habits: HabitsPanelEnabled = value; break;
            case AppPage.Reminders: RemindersSidebarEnabled = value; break;
            case AppPage.Notifications: NotificationsSidebarEnabled = value; break;
            case AppPage.Statistics: StatisticsPanelEnabled = value; break;
            case AppPage.Assistant:
                AssistantPanelEnabled = value;
                if (Skills is not null) Skills.IsAssistantAvailable = value;
                if (Sessions is not null) Sessions.IsAssistantAvailable = value;
                if (!value)
                {
                    // Apagar la IA corta la respuesta en curso y deja de escuchar: ningún CLI puede tocar ya el vault.
                    Assistant?.CancelAll();
                    _aiVaultTools?.Stop();
                }
                break;
        }

        var wasActive = NavItems.Concat(BottomNavItems).FirstOrDefault(n => n.Page == page)?.IsActive ?? false;

        RebuildNavItems();

        if (!value && wasActive && (NavItems.FirstOrDefault() ?? BottomNavItems.FirstOrDefault())?.Page is { } fallback)
            Navigate(fallback);
    }

    /// <summary>A drag/pin reorder in Ajustes › General › Secciones de la barra lateral changed which
    /// section lives in which group, or their order within it, without touching any single section's own
    /// visibility flag — so <see cref="OnSidebarSectionChanged"/>'s rebuild wouldn't otherwise fire. Simply
    /// rebuilds <see cref="NavItems"/>/<see cref="BottomNavItems"/> off the freshly-saved order; no active-page
    /// redirect needed since a pure reorder never hides the page currently on screen.</summary>
    private void OnSidebarSectionOrderChanged() => RebuildNavItems();

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

    /// <summary>Lands on the Recordatorios page with a standalone reminder's own detail dialog already
    /// open — the inbox's ("<see cref="ReminderInbox"/>") equivalent of <see cref="OpenKanbanCard"/> for the
    /// items it now also surfaces that come from <see cref="RemindersViewModel"/> instead of the Kanban
    /// board (docs/agent-docs/notificaciones-y-recordatorios.md).</summary>
    private void OpenReminder(string reminderId)
    {
        Navigate(AppPage.Reminders);
        Reminders?.OpenReminderById(reminderId);
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
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Notification_LinkedNoteMissing_Title"),
                LocalizedTextFormatter.Format("Notification_LinkedNoteMissing_Message"),
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
                // CaretOffset viene relleno cuando la coincidencia estaba en el cuerpo de la nota, para
                // abrirla directamente en esa línea (ver GlobalSearchResultViewModel.CaretOffset).
                if (result.Payload is NoteFile note) Notes?.OpenNote(note, result.CaretOffset);
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
        // Ninguna sesión cifrada del vault anterior puede seguir desbloqueada al otro lado del cambio, ni
        // dejar un temporal en claro por el camino (T4, .docs/project-info/multi-vault-en-caliente.md).
        // Aquí y no en SwitchToVaultAsync porque este método es el punto por el que pasan *todos* los
        // cambios de vault, incluido el de "restaurar copia de seguridad" de Ajustes. Las DEK viven en
        // memoria indexadas por la raíz de su sesión, así que soltarlas después de adoptar la ruta nueva
        // es igual de efectivo que antes y cubre un camino más.
        _vaultService.EncryptionService.LockAll();
        VaultService.CleanupStaleDecryptedTempFiles();

        QuickCapture.CloseForVaultChange();
        Kanban?.CloseForVaultChange();
        Assistant?.CloseForVaultChange();
        _aiVaultTools?.Stop();
        Reminders?.CloseForVaultChange();
        ReloadVaultData();
        // Settings' "change vault folder" can point at any folder, old-version vault included — same
        // guarantee as a fresh app startup.
        _sessionService.EnsureDefaultSession();

        _vaultSettingsService.Reload();
        Assistant?.ReloadForVault();
        UiZoomScale = _vaultSettingsService.Settings.UiZoomPercent / 100.0;
        QuickCapture.RefreshLocalizedLabels();
        QuickCapture.ReloadDestinations();
        // Libera el vigilante del vault anterior y crea el del nuevo: sus notas registradas eran las
        // pestañas que se cierran unas líneas más abajo.
        _vaultChangeWatcher.Start(_vaultService.VaultPath);
        ApplyVaultPreferences();
        _habitService.Reload();

        // Any open editor tabs belong to the previous vault; keeping them open would show stale or
        // missing notes after the switch. Close them before reloading so the new vault starts clean.
        Notes!.CloseAllEditorsCommand.Execute(null);

        Dashboard!.Reload();
        Notes!.Reload();
        Notes!.ReloadSessions(restoreStoredSelection: true);
        Kanban!.Reload();
        Kanban.TaskManagement.Reload();
        Reminders?.Reload();
        Notifications?.Reload();
        Files!.Reload();
        Files!.ReloadSessions(restoreStoredSelection: true);
        Skills!.Reload();
        Sessions!.Reload();
        Trash!.Reload();
        TagManager!.Reload();
        Habits!.Reload();
        Statistics!.Reload();
        Calendar!.ReloadPreferences();
        UpdateTrashBadge();

        _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Notification_VaultChanged_Title"),
            LocalizedTextFormatter.Format("Notification_VaultChanged_Message"),
            NotificationSeverity.Success);
    }

    /// <summary>Aplica las preferencias del vault recién abierto a la shell y a Ajustes.</summary>
    private void ApplyVaultPreferences()
    {
        var settings = _settingsService.Settings;
        if (!string.IsNullOrWhiteSpace(settings.Language))
            LocalizationService.Instance.SetLanguage(settings.Language);
        ShortcutRegistry.Initialize(settings.ShortcutOverrides);
        _customThemeService.ApplyActiveOnStartup();
        IsSidebarExpanded = settings.SidebarExpanded;
        SidebarWidth = IsSidebarExpanded ? 248 : 76;
        UseCustomTitleBar = settings.UseCustomTitleBar;
        RefreshWindowControls();
        NotificationsPanelEnabled = settings.NotificationsPanelEnabled;
        RemindersPanelEnabled = settings.RemindersPanelEnabled;
        InboxPanelEnabled = settings.InboxPanelEnabled;
        VaultSwitcherEnabled = settings.VaultSwitcherEnabled;
        NotificationsSidebarEnabled = settings.NotificationsSidebarEnabled;
        RemindersSidebarEnabled = settings.RemindersSidebarEnabled;
        DashboardPanelEnabled = settings.DashboardPanelEnabled;
        CalendarPanelEnabled = settings.CalendarPanelEnabled;
        KanbanPanelEnabled = settings.KanbanPanelEnabled;
        NotesPanelEnabled = settings.NotesPanelEnabled;
        FilesPanelEnabled = settings.FilesPanelEnabled;
        SkillsPanelEnabled = settings.SkillsPanelEnabled;
        SessionsPanelEnabled = settings.SessionsPanelEnabled;
        TrashPanelEnabled = settings.TrashPanelEnabled;
        TagManagerPanelEnabled = settings.TagManagerPanelEnabled;
        HabitsPanelEnabled = settings.HabitsPanelEnabled;
        StatisticsPanelEnabled = settings.StatisticsPanelEnabled;
        AssistantPanelEnabled = settings.Ai.Enabled;
        Dashboard!.ShowScrollbar = settings.DashboardShowScrollbar;
        Dashboard.SetCalendarEnabled(settings.CalendarPanelEnabled);
        OnKanbanBoardOrientationChanged(settings.KanbanBoardOrientation);
        Kanban!.TaskManagement.ShowTagFilter = settings.PolarShowTaskTags;
        Settings!.ReloadForVault();
        if (Avalonia.Application.Current is not null && settings.ActiveCustomThemeId is null)
            Avalonia.Application.Current.RequestedThemeVariant = settings.Theme == "Light"
                ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        ReloadBrandLogo();
        RefreshAllLocalizedLabels();
        RebuildNavItems();
    }

    /// <summary>Carga los datos propios del vault antes de refrescar sus vistas.</summary>
    private void ReloadVaultData()
    {
        VaultDataStorage.MigrateLegacyFiles(_vaultService);
        _kanbanService.Reload();
        _polarTaskService.Reload();
        _reminderService.Reload();
        _dismissedReminderService.Reload();
        _snoozedReminderService.Reload();
        _notificationService.Reload();
        if (_kanbanService.LoadedFromCorruptData)
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Notification_KanbanCorrupt_Title"),
                LocalizedTextFormatter.Format("Kanban_Multi_LoadFailed"), NotificationSeverity.Warning);
    }

    /// <summary>Pops every reminder whose scheduled time has arrived and turns each into a real
    /// notification — see <see cref="ReminderService.PopDueReminders"/>. Runs on a 30s
    /// <see cref="Avalonia.Threading.DispatcherTimer"/> (see the constructor) plus once at startup, so a
    /// reminder that came due while the app was closed still fires as soon as it launches.</summary>
    private void CheckDueReminders()
    {
        CheckDueSnoozes();

        var due = _reminderService.PopDueReminders();
        if (due.Count == 0) return;

        foreach (var reminder in due)
            _notificationService.PushDue(
                LocalizedTextFormatter.Format("Notification_ReminderDue_Title", reminder.Title),
                LocalizedTextFormatter.Literal(reminder.Description),
                new SnoozeTarget(SnoozeSourceKind.Reminder, reminder.Id, reminder.NotifyAtUtc));

        Reminders?.Reload();
        Notifications?.Reload();
    }

    /// <summary>Contrapartida de <see cref="CheckDueReminders"/> para los avisos aplazados (T5): vuelve a
    /// anunciar cada aplazamiento cuya hora ha llegado, en el mismo ciclo de 30 s del temporizador — ni
    /// un segundo temporizador ni un segundo motor de avisos.
    ///
    /// <para>Antes de reavisar se limpian los aplazamientos huérfanos: el criterio EARS "si el elemento de
    /// origen se elimina o se completa, el sistema deberá descartar su aplazamiento" no lo cubre solo el
    /// <c>CardPermanentlyDeleted</c> cableado en la composición —una tarjeta completada, un recordatorio
    /// borrado o una fecha reprogramada desde el calendario también dejan el aplazamiento sin sentido—,
    /// así que aquí se revalida contra los almacenes reales, que son los que mandan.</para></summary>
    private void CheckDueSnoozes()
    {
        _snoozedReminderService.PruneWhere(snooze => ResolveSnoozeTitle(snooze) is null);

        var due = _snoozedReminderService.PopDue();
        if (due.Count == 0) return;

        foreach (var snooze in due)
        {
            if (ResolveSnoozeTitle(snooze) is not { } title) continue;

            _notificationService.PushDue(
                LocalizedTextFormatter.Format("Notification_ReminderSnoozeElapsed_Title", title),
                LocalizedTextFormatter.Format("Notification_ReminderSnoozeElapsed_Message"),
                new SnoozeTarget(snooze.SourceKind, snooze.SourceId, snooze.DueUtcSnapshot));
        }

        ReminderInbox?.Reload();
        Notifications?.Reload();
    }

    /// <summary>Título vigente del origen de un aplazamiento, o <see langword="null"/> si ese origen ya no
    /// existe, está completado/borrado, o su fecha ha cambiado desde que se aplazó — en cuyo caso el
    /// aplazamiento ya no corresponde al aviso que el usuario pospuso (ver
    /// <see cref="Models.SnoozedReminder.DueUtcSnapshot"/>). Se resuelve contra la entidad real en cada
    /// pasada en vez de copiar el título al aplazar, para que un renombrado se refleje en el reaviso.</summary>
    private string? ResolveSnoozeTitle(SnoozedReminder snooze)
    {
        if (snooze.SourceKind == SnoozeSourceKind.Reminder)
        {
            var reminder = _reminderService.ActiveReminders.FirstOrDefault(r => r.Id == snooze.SourceId);
            return reminder is { IsCompleted: false } && reminder.NotifyAtUtc == snooze.DueUtcSnapshot
                ? reminder.Title
                : null;
        }

        var card = _kanbanService.AllColumns
            .SelectMany(column => column.Cards)
            .FirstOrDefault(c => c.Id == snooze.SourceId);
        return card is { CompletedAtUtc: null } && card.DueDateUtc == snooze.DueUtcSnapshot
            ? card.Title
            : null;
    }

    /// <summary>Comprobación de versión nueva del arranque (T9). Tres reglas del criterio de aceptación
    /// viven aquí y no en <see cref="UpdateCheckService"/>, que solo sabe de red y de versiones:
    ///
    /// <list type="bullet">
    /// <item>"no deberá interrumpir el arranque ni mostrar errores en la comprobación automática": el
    /// resultado que no sea "hay novedad" no produce absolutamente nada visible, ni siquiera un fallo de
    /// red.</item>
    /// <item>El aviso lleva el enlace de descarga a la página de la release, que el servicio ya validó
    /// contra el repositorio oficial.</item>
    /// <item>Ajustes se queda sembrado con la misma release, para que abrirlo después del aviso la enseñe
    /// sin volver a pedirla.</item>
    /// </list></summary>
    private async Task CheckForUpdatesOnStartupAsync()
    {
        var result = await _updateCheckService.CheckOnStartupAsync();
        if (result is not { Outcome: UpdateCheckOutcome.UpdateAvailable, Update: { } update }) return;

        _notificationService.PushWithLink(
            LocalizedTextFormatter.Format("Notification_UpdateAvailable_Title", update.Version),
            LocalizedTextFormatter.Format("Notification_UpdateAvailable_Message"),
            update.Url);

        Settings?.ShowAvailableUpdate(update);
    }

    /// <summary>Callback pasado a <see cref="SettingsViewModel"/> (mismo patrón que
    /// <see cref="OnDashboardShowScrollbarModeChanged"/> etc.) — aplica en vivo el nuevo porcentaje al
    /// <c>ScaleTransform</c> que ya está en pantalla, sin esperar a la próxima navegación.</summary>
    private void OnUiZoomChanged(int percent) => UiZoomScale = percent / 100.0;

    /// <summary>"Usar en el asistente" desde el detalle de una skill: abre un chat nuevo con ella.</summary>
    private void StartAssistantChatWithSkill(SkillFile skill)
    {
        if (!AssistantPanelEnabled || Assistant is null) return;
        Assistant.StartChatWithSkill(skill);
        ShowAssistant();
    }

    /// <summary>"Preguntar al asistente" en Sesiones: un chat nuevo con el ámbito de esa sesión.</summary>
    private void StartAssistantChatInSession(Session session)
    {
        if (!AssistantPanelEnabled || Assistant is null) return;
        Assistant.StartChatInSession(session);
        ShowAssistant();
    }

    /// <summary>"Preguntar al asistente sobre esta nota" de la paleta de comandos.</summary>
    private void StartAssistantChatAboutActiveNote()
    {
        if (!AssistantPanelEnabled || Assistant is null || Notes?.ActiveEditor?.Note is not { } note) return;
        Assistant.StartChatAboutNote(note);
        ShowAssistant();
    }

    private void OpenAiSettings()
    {
        OpenSettings();
        Settings?.SelectSectionCommand.Execute(SettingsSection.Ai);
    }

    private void OpenNoteFromQuickSwitcher(NoteFile note)
    {
        Navigate(AppPage.Notes);
        Notes?.OpenNote(note);
    }
}
