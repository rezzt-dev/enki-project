using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using enki.Localization;
using enki.Services;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Composition root for the shell (Fase 6, gap 31 — "reducir tamaño de MainWindowViewModel,
/// manteniendo la composición manual"): every service and page-level viewmodel Enki constructs without a DI
/// container lives in this file's constructor/<see cref="InitializeShell"/>, split out of
/// <c>MainWindowViewModel.cs</c> purely to keep that file to the shell's runtime behavior (navigation,
/// zoom, badges, search) instead of also being the ~230-line bootstrap sequence. Still the very same
/// <see cref="MainWindowViewModel"/> class — partial types share every field/property regardless of which
/// file declares them — so no wiring, timing or field-initialization order changed by this split.</summary>
public sealed partial class MainWindowViewModel
{
    public SidePanelLayoutViewModel SidePanels { get; }

    public MainWindowViewModel() : this(new SettingsService()) { }

    public MainWindowViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        // Propuesta 29 (docs/propuesta-nuevas-funcionalidades.md): carga los overrides de atajos de teclado
        // guardados antes de que cualquier vista construya sus KeyBindings, para que la primera ventana
        // arranque ya con los gestos remapeados en vez de con los valores por defecto.
        ShortcutRegistry.Initialize(_settingsService.Settings.ShortcutOverrides);
        // Sistema de importación de temas personalizados (Ajustes › Apariencia): re-aplica el tema que
        // estuviera activo la sesión anterior antes de que cualquier ventana lea un recurso de color, mismo
        // criterio de orden que ShortcutRegistry.Initialize justo arriba.
        _customThemeService = new CustomThemeService(_settingsService);
        _customThemeService.ApplyActiveOnStartup();
        // Logo del sidebar (Ajustes › Apariencia): solo toca su propio archivo en %AppData%/Enki, así que
        // no depende de nada de lo de arriba; se construye aquí para que el logo esté resuelto antes
        // de que MainWindow pinte el brand header por primera vez.
        _brandLogoService = new BrandLogoService(_settingsService);
        _brandLogoService.LogoChanged += ReloadBrandLogo;
        _settingsService.SettingsChanged += RefreshBrandLogo;
        _settingsService.SettingsChanged += RefreshWindowControls;
        ReloadBrandLogo();
        // Fase 0 del roadmap de deficiencias: Program.Main ya inicializó LogService con el nivel Warning
        // por defecto antes de que este ajuste (persistido en settings.json) exista siquiera — aquí es
        // donde se sube a Debug si el usuario lo activó en una sesión anterior. OnDebugLoggingEnabledChanged
        // en SettingsViewModel hace lo mismo en caliente si lo cambia desde Ajustes.
        LogService.SetDebugEnabled(_settingsService.Settings.DebugLoggingEnabled);
        _vaultService = new VaultService(_settingsService);
        VaultDataStorage.MigrateLegacyFiles(_vaultService);
        // Búsqueda de texto completo sobre el contenido real de las notas (búsqueda global, quick switcher
        // y los dos filtros de la vista de Notas). Sin estado propio ni persistencia: lee a través de la
        // caché de notas del vault, así que puede construirse aquí sin orden particular.
        _noteSearchService = new NoteSearchService(_vaultService);
        _vaultBackupService = new VaultBackupService(_vaultService);
        // Solo se construye aquí; empieza a mirar el disco en InitializeShell, cuando ya se sabe qué vault
        // hay (en el primer arranque todavía no hay ninguno) — y se reinicia en OnVaultChanged.
        _vaultChangeWatcher = new VaultChangeWatcher(_vaultService, _settingsService);
        _vaultSettingsService = new VaultSettingsService(_vaultService);
        _noteTemplateService = new NoteTemplateService(_vaultService);
        _habitService = new HabitService(_vaultService);
        _sessionService = new SessionService(_vaultService);
        _kanbanService = new KanbanService(_vaultService, deferSeedCreation: SupportedLanguages.Find(_settingsService.Settings.Language) is null);
        _notificationService = new NotificationService(_settingsService, _vaultService);
        SidePanels = new SidePanelLayoutViewModel(_settingsService, _notificationService);
        _settingsService.SettingsChanged += SidePanels.Reload;
        void NotifyCorruptVaultSettings(string backupPath) => _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Notification_VaultSettingsCorrupt_Title"),
            LocalizedTextFormatter.Format("Notification_VaultSettingsCorrupt_Message", backupPath),
            Models.NotificationSeverity.Error);
        _settingsService.VaultSettingsRecoveredFromCorruptData += NotifyCorruptVaultSettings;
        if (_settingsService.CorruptVaultSettingsBackupPath is { } corruptVaultSettingsBackup)
            NotifyCorruptVaultSettings(corruptVaultSettingsBackup);
        void NotifyCredentialStoreFailure() => _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Notification_PolarCredentialsUnavailable_Title"),
            LocalizedTextFormatter.Format("Notification_PolarCredentialsUnavailable_Message"),
            Models.NotificationSeverity.Error);
        _settingsService.CredentialStoreFailed += NotifyCredentialStoreFailure;
        if (_settingsService.CredentialStoreUnavailable) NotifyCredentialStoreFailure();
        _dismissedReminderService = new DismissedReminderService(_vaultService);
        // Fase 10 (docs/analisis-proyecto/roadmap.md): una tarjeta borrada para siempre no debe dejar un
        // descarte de recordatorio huérfano apuntando a un id que ya no existe.
        _kanbanService.CardPermanentlyDeleted += _dismissedReminderService.RemoveForCard;
        // T5: mismo razonamiento para los aplazamientos — un aplazamiento huérfano volvería a avisar de
        // una tarjeta que ya no existe. Ver también CheckDueSnoozes, que cubre el resto de los motivos
        // por los que un origen deja de existir (completado, recordatorio borrado, otro vault…).
        _snoozedReminderService = new SnoozedReminderService(_vaultService);
        _kanbanService.CardPermanentlyDeleted += _snoozedReminderService.Cancel;
        _polarAuthService = new PolarAuthService(_settingsService);
        // currentUserIdProvider stamps which Polar account was linked at the moment each row first became
        // dirty, so PolarSyncService can later refuse to push it under a different account
        // (docs/analisis-proyecto/roadmap.md, Fase 4).
        _reminderService = new ReminderService(_vaultService, currentUserIdProvider: () => _polarAuthService.UserId);
        _supabaseRestClient = new SupabaseRestClient(_polarAuthService);
        _polarTaskService = new PolarTaskService(_vaultService, currentUserIdProvider: () => _polarAuthService.UserId);
        _polarSyncService = new PolarSyncService(_polarAuthService, _supabaseRestClient, _polarTaskService, _reminderService, _settingsService, _vaultSettingsService);
        _polarRealtimeService = new PolarRealtimeService(_polarAuthService, _polarSyncService);
        _claudeCliService = new ClaudeCliService(_settingsService);
        _aiChatService = new AiChatService();
        AiCliStatus = new ClaudeCliStatusViewModel(_claudeCliService, backend: new AiBackendService(_claudeCliService, _settingsService));
        InitializePageRefresh();

        // Fase 1 (docs/analisis-proyecto/roadmap.md): extiende el patrón de KanbanService.LoadedFromCorruptData
        // (antes el único caso) al resto de los stores JSON de arranque, para que un archivo dañado avise
        // al usuario en vez de resetear el estado en silencio — incluido, para settings.json, el vínculo
        // con Polar que se pierde junto con el resto de los ajustes.
        if (_settingsService.LoadedFromCorruptData)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Notification_SettingsCorrupt_Title"),
                LocalizedTextFormatter.Format("Notification_SettingsCorrupt_Message"),
                Models.NotificationSeverity.Error);
        }
        if (_kanbanService.LoadedFromCorruptData)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Notification_KanbanCorrupt_Title"),
                LocalizedTextFormatter.Format("Kanban_Multi_LoadFailed"),
                Models.NotificationSeverity.Error);
        }
        if (_notificationService.LoadedFromCorruptData)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Notification_NotificationsCorrupt_Title"),
                LocalizedTextFormatter.Format("Notification_NotificationsCorrupt_Message"),
                Models.NotificationSeverity.Warning);
        }
        if (_reminderService.LoadedFromCorruptData)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Notification_RemindersCorrupt_Title"),
                LocalizedTextFormatter.Format("Notification_RemindersCorrupt_Message"),
                Models.NotificationSeverity.Error);
        }
        _sessionService.ManifestError += message => _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Notification_SessionError_Title"),
            _sessionService.LastManifestError ?? LocalizedTextFormatter.Literal(message),
            Models.NotificationSeverity.Warning);

        _isSidebarExpanded = _settingsService.Settings.SidebarExpanded;
        _sidebarWidth = _isSidebarExpanded ? 248 : 76;
        _useCustomTitleBar = _settingsService.Settings.UseCustomTitleBar;
        _notificationsPanelEnabled = _settingsService.Settings.NotificationsPanelEnabled;
        _remindersPanelEnabled = _settingsService.Settings.RemindersPanelEnabled;
        _inboxPanelEnabled = _settingsService.Settings.InboxPanelEnabled;
        _vaultSwitcherEnabled = _settingsService.Settings.VaultSwitcherEnabled;
        _notificationsSidebarEnabled = _settingsService.Settings.NotificationsSidebarEnabled;
        _remindersSidebarEnabled = _settingsService.Settings.RemindersSidebarEnabled;
        _calendarPanelEnabled = _settingsService.Settings.CalendarPanelEnabled;
        _skillsPanelEnabled = _settingsService.Settings.SkillsPanelEnabled;
        _dashboardPanelEnabled = _settingsService.Settings.DashboardPanelEnabled;
        _kanbanPanelEnabled = _settingsService.Settings.KanbanPanelEnabled;
        _notesPanelEnabled = _settingsService.Settings.NotesPanelEnabled;
        _filesPanelEnabled = _settingsService.Settings.FilesPanelEnabled;
        _sessionsPanelEnabled = _settingsService.Settings.SessionsPanelEnabled;
        _trashPanelEnabled = _settingsService.Settings.TrashPanelEnabled;
        _tagManagerPanelEnabled = _settingsService.Settings.TagManagerPanelEnabled;
        _habitsPanelEnabled = _settingsService.Settings.HabitsPanelEnabled;
        _statisticsPanelEnabled = _settingsService.Settings.StatisticsPanelEnabled;
        _assistantPanelEnabled = _settingsService.Settings.Ai.Enabled;
        _uiZoomScale = _vaultSettingsService.Settings.UiZoomPercent / 100.0;

        // Always live (not gated behind vault readiness): the kanban board and the notification log are
        // both independent of the vault folder, and the two title-bar buttons stay visible even on the
        // vault-setup screen.
        NotificationCenter = new NotificationCenterViewModel(_notificationService, _settingsService,
            snoozedReminderService: _snoozedReminderService);
        ReminderInbox = new ReminderInboxViewModel(_kanbanService, _reminderService, _settingsService, _dismissedReminderService, _snoozedReminderService, OpenKanbanCard, OpenReminder);
        ReminderInbox.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReminderInboxViewModel.OverdueCount)) UpdateCalendarBadge();
        };
        GlobalSearch = new GlobalSearchViewModel(
            _vaultService, _noteSearchService, _kanbanService, _polarTaskService, _reminderService, _polarAuthService,
            OnGlobalSearchResultSelected);
        QuickSwitcher = new QuickSwitcherViewModel(_vaultService, _noteSearchService, OpenNoteFromQuickSwitcher);
        // El proveedor se llama de nuevo cada vez que se abre la paleta (ver CommandPaletteViewModel), así
        // que puede referenciar con seguridad Notes/Kanban/Settings aunque todavía sean null aquí — para
        // cuando el usuario realmente pulse Ctrl+Shift+K, InitializeShell ya los habrá construido.
        CommandPalette = new CommandPaletteViewModel(BuildCommandPaletteItems);
        // Selector de vaults (T4): vive junto a la paleta y el quick switcher, fuera de la shell, porque
        // su lista es de la instalación y no del vault abierto — sigue teniendo sentido, y sigue siendo
        // la vía para salir de ahí, incluso en la pantalla de configuración inicial.
        VaultSwitcher = new VaultSwitcherViewModel(_settingsService, _vaultService,
            async path => await SwitchToVaultAsync(path), PickVaultFolderAsync);
        // Web clipper (T3). Notes todavía no existe aquí (lo crea InitializeShell), pero el callback solo
        // se invoca cuando el usuario guarda una página, mucho después — mismo criterio que el proveedor
        // de comandos de la paleta justo arriba.
        _webClipService = new WebClipService(_vaultService);
        WebClip = new WebClipViewModel(_webClipService, _vaultService, _notificationService,
            _ => Notes?.Reload());
        QuickCapture = new QuickCaptureViewModel(
            new QuickCaptureService(_vaultService, _kanbanService), _vaultService, _kanbanService,
            _vaultSettingsService, _notificationService,
            result =>
            {
                if (result.Destination == Models.QuickCaptureDestination.InboxNote) Notes?.Reload();
                else Kanban?.Reload();
                InvalidatePages(AppPage.Dashboard, AppPage.Statistics);
            });
        _globalHotkeyService = new GlobalHotkeyService();
        _shortcutChangedHandler = () => ApplyQuickCaptureHotkey(notifyOnFailure: true);
        ShortcutRegistry.Changed += _shortcutChangedHandler;
        if (SingleInstanceGuard.IsFirstInstance) ApplyQuickCaptureHotkey(notifyOnFailure: true);

        // Polls for reminders whose scheduled time has arrived and turns each into a real notification
        // (see CheckDueReminders) — independent of the vault folder, so it runs from app start regardless
        // of whether the shell has initialized yet.
        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _reminderTimer.Tick += (_, _) => CheckDueReminders();
        _reminderTimer.Start();
        CheckDueReminders();

        // Backup automático opcional (Ajustes › General, Fase 1 de docs/analisis-proyecto/roadmap.md) —
        // comprobado ya al arrancar (por si Enki lleva días sin abrirse) y luego cada 6 horas, para que el
        // intervalo configurado se cumpla aunque la app se quede abierta varios días seguidos.
        _autoBackupTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _autoBackupTimer.Tick += (_, _) => _vaultBackupService.RunScheduledBackupIfDue(_settingsService, _notificationService);
        _autoBackupTimer.Start();
        _vaultBackupService.RunScheduledBackupIfDue(_settingsService, _notificationService);

        // Aviso de versión nueva (T9). No bloquea el arranque —se lanza y se olvida— y, con el ajuste
        // apagado (lo de fábrica), ni siquiera construye el cliente HTTP: CheckOnStartupAsync sale antes
        // de tocar la red. Ver CheckForUpdatesOnStartup.
        _updateCheckService = new UpdateCheckService(_settingsService);
        _ = CheckForUpdatesOnStartupAsync();

        // Same "independent of vault readiness" reasoning as the two panels above — linking to Polar
        // and its background sync have nothing to do with which vault folder is open.
        _polarSyncTimer = new DispatcherTimer { Interval = PolarSyncBaseInterval };
        _polarSyncTimer.Tick += async (_, _) =>
        {
            await _polarSyncService.SyncNowAsync();

            // Fase 9 (docs/analisis-proyecto/roadmap.md): PolarRealtimeService only ever reconnected on the
            // next LinkStateChanged or app restart before this — a socket dropped by an idle timeout,
            // sleep/wake, or a network blip that never fully took the device offline stayed dark until
            // then. Piggybacking the retry on this same timer tick reuses its existing backoff for free:
            // the interval below already doubles while genuinely offline and resets the moment a sync
            // attempt reaches the server again, so a dead socket gets retried on that same cadence instead
            // of needing a second, parallel backoff timer of its own.
            if (_polarAuthService.IsLinked && !_polarRealtimeService.IsConnected)
                await _polarRealtimeService.ConnectAsync();
        };
        _polarSyncTimer.Start();
        _polarAuthService.LinkStateChanged += async () =>
        {
            if (_polarAuthService.IsLinked)
            {
                await _polarSyncService.SyncNowAsync();
                await _polarRealtimeService.ConnectAsync();
            }
            else
            {
                _polarRealtimeService.Disconnect();

                // Fase 4 (docs/analisis-proyecto/roadmap.md): sin cuenta vinculada, el store local de
                // "Gestión de tareas" (listas/tareas/subtareas Polar-compatibles) no tiene ningún propietario
                // válido al que pertenecer — a diferencia de los Recordatorios, esta vista solo existe una
                // vez que se vincula una cuenta (ver PolarTaskList.UserId), así que no hay ningún caso de uso
                // local-sin-cuenta que perder al limpiarla. Evita que esos datos queden huérfanos en disco y
                // reaparezcan bajo la siguiente cuenta distinta que se vincule en el mismo dispositivo.
                var hadData = _polarTaskService.Store.Lists.Count > 0 || _polarTaskService.Store.Tasks.Count > 0
                    || _polarTaskService.Store.Subtasks.Count > 0;
                if (hadData)
                {
                    _polarTaskService.ClearAll();
                    _notificationService.PushLocalized(
                        LocalizedTextFormatter.Format("Notification_PolarUnlinked_TasksCleared_Title"),
                        LocalizedTextFormatter.Format("Notification_PolarUnlinked_TasksCleared_Message"),
                        Models.NotificationSeverity.Info);
                }
            }
        };
        _polarAuthService.SessionExpired += () => _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Notification_PolarSessionExpired_Title"),
            LocalizedTextFormatter.Format("Notification_PolarSessionExpired_Message"),
            Models.NotificationSeverity.Warning);
        _polarSyncService.SyncCompleted += (_, _) =>
        {
            // Exponential backoff while genuinely offline (03-problemas-y-deuda-tecnica.md §4) — doubles
            // the wait after each network failure, capped at PolarSyncMaxInterval, and resets to the base
            // interval the moment a sync attempt actually reaches the server again (success or a real
            // server/data error, as opposed to no connection at all). Lives here instead of inside the
            // timer's own Tick handler so a manually-triggered sync (the "Sync now" buttons) also adjusts
            // the backoff and republishes NextSyncAttemptUtc, not just automatic ticks.
            _polarSyncTimer.Interval = _polarSyncService.LastErrorWasNetwork
                ? TimeSpan.FromTicks(Math.Min(_polarSyncTimer.Interval.Ticks * 2, PolarSyncMaxInterval.Ticks))
                : PolarSyncBaseInterval;
            _polarSyncService.NextSyncAttemptUtc = _polarSyncService.LastErrorWasNetwork
                ? DateTime.UtcNow + _polarSyncTimer.Interval
                : null;
            _polarRealtimeService.RefreshAuthIfNeeded();

            var isFailing = _polarSyncService.LastError is not null;
            var justStartedFailing = isFailing && !_polarSyncWasFailing;
            // Fase 9 (docs/analisis-proyecto/roadmap.md): re-notify while the same failure persists, not
            // just on the initial transition — a single dismissible notification from hours ago is easy to
            // forget while sync (and every edit made since) silently keeps failing.
            var stillFailingPastRenotifyWindow = isFailing && !justStartedFailing
                && _lastPolarSyncFailureNotifiedUtc is { } lastNotified
                && DateTime.UtcNow - lastNotified >= PolarSyncFailureRenotifyInterval;
            if (justStartedFailing || stillFailingPastRenotifyWindow)
            {
                _notificationService.PushLocalized(
                    LocalizedTextFormatter.Format("Notification_PolarSyncFailed_Title"),
                    _polarSyncService.LocalizedError ?? LocalizedTextFormatter.Format("Common_Error_Unexpected"),
                    Models.NotificationSeverity.Error);
                _lastPolarSyncFailureNotifiedUtc = DateTime.UtcNow;
            }
            if (!isFailing) _lastPolarSyncFailureNotifiedUtc = null;
            _polarSyncWasFailing = isFailing;
        };
        _polarSyncService.LocalEditsOverwritten += titles =>
        {
            // Fase 9 (docs/analisis-proyecto/roadmap.md): before this, losing a Last-Write-Wins conflict
            // silently discarded the local edit with no visible trace — the value just reverted the next
            // time someone looked at it. Batched into one notification per sync cycle (see
            // PolarSyncService.LocalEditsOverwritten's own remarks) rather than one per row.
            var itemsFragment = titles.Count == 1
                ? LocalizedTextFormatter.PluralFormat("Notification_PolarConflictOverwritten_Items", 1, titles[0])
                : LocalizedTextFormatter.PluralFormat("Notification_PolarConflictOverwritten_Items", titles.Count, titles.Count);
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Notification_PolarConflictOverwritten_Title"),
                LocalizedTextFormatter.Format("Notification_PolarConflictOverwritten_Message", itemsFragment),
                Models.NotificationSeverity.Warning);
        };
        if (_polarAuthService.IsLinked)
        {
            _ = _polarSyncService.SyncNowAsync();
            _ = _polarRealtimeService.ConnectAsync();
        }

        // El flujo de primer arranque: idioma primero, luego vault, luego shell. Ver
        // docs/agent-docs/internacionalizacion-i18n.md §3.1.
        if (SupportedLanguages.Find(_settingsService.Settings.Language) is null)
        {
            LanguageSetup = new LanguageSetupViewModel(_settingsService, OnLanguageSelected);
        }
        else
        {
            ShowVaultSetupOrShell();
        }
    }

    /// <summary>Registra o vuelve a registrar el gesto remapeable. Cualquier fallo queda convertido en un
    /// aviso y nunca interrumpe el arranque; bandeja, paleta y atajo local permanecen disponibles.
    ///
    /// <para>Una limitación del escritorio no se notifica: bajo Wayland ningún cliente puede reservar
    /// teclas globales (<see cref="GlobalHotkeyStatus.Unreliable"/>) y en una plataforma sin backend
    /// tampoco (<see cref="GlobalHotkeyStatus.Unsupported"/>). No es algo que el usuario pueda arreglar ni
    /// cambia entre arranques, así que queda en el log y la captura rápida sigue ofreciéndose por bandeja,
    /// paleta y atajo local. Solo se avisa de lo accionable —combinación ya ocupada u otro fallo— y aun eso
    /// una sola vez por gesto y motivo: la firma del último aviso vive en
    /// <c>AppSettings.QuickCaptureHotkeyWarningSignature</c>, así que cambiar el atajo en Ajustes › Atajos,
    /// o que el motivo pase a ser otro, sí vuelve a avisar.</para>
    /// </summary>
    private void ApplyQuickCaptureHotkey(bool notifyOnFailure)
    {
        var gesture = ShortcutRegistry.GetGesture(ShortcutIds.QuickCapture);
        var serializedGesture = gesture.ToString();
        if (string.Equals(_appliedQuickCaptureGesture, serializedGesture, StringComparison.Ordinal)) return;
        _appliedQuickCaptureGesture = serializedGesture;
        var result = _globalHotkeyService.Apply(gesture, OpenQuickCapture);

        var notifiable = result.Status is GlobalHotkeyStatus.Conflict or GlobalHotkeyStatus.Failed;
        if (!notifiable && result.Status != GlobalHotkeyStatus.Registered)
            LogService.Warning(
                $"Atajo global no disponible en este escritorio ({result.Status}): {serializedGesture}");

        var settings = _settingsService.Settings;
        var signature = notifiable ? $"{serializedGesture}|{result.Status}" : null;
        var alreadyWarned = string.Equals(settings.QuickCaptureHotkeyWarningSignature, signature,
            StringComparison.Ordinal);
        if (!alreadyWarned)
        {
            settings.QuickCaptureHotkeyWarningSignature = signature;
            _settingsService.Save();
        }

        if (!notifyOnFailure || signature is null || alreadyWarned) return;
        _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Notification_QuickCaptureHotkeyUnavailable_Title"),
            result.Reason ?? LocalizedTextFormatter.Format("QuickCapture_Hotkey_Failed"),
            Models.NotificationSeverity.Warning);
    }

    /// <summary>Continúa el arranque después de que el usuario elige un idioma en
    /// <see cref="LanguageSetup"/>.</summary>
    private void OnLanguageSelected() => ShowVaultSetupOrShell();

    /// <summary>Decide, una vez resuelto el idioma, si arrancar la shell normal o mostrar la pantalla de
    /// configuración de vault. El start screen debe reaparecer cuando el vault guardado falta o cuando se
    /// lanza una segunda instancia.</summary>
    private void ShowVaultSetupOrShell()
    {
        LanguageSetup = null;

        // La misma condición que consulta MainWindow para decidir si arranca con la pantalla de arranque
        // puesta (ver StartupGate): escrita una sola vez, porque la ventana decide antes de que este método
        // exista y las dos respuestas tienen que coincidir.
        if (StartupGate.OpensShellDirectly(_settingsService))
        {
            InitializeShell();
        }
        else
        {
            string? infoMessage = null;

            if (!SingleInstanceGuard.IsFirstInstance)
            {
                // Another Enki window is already running — routed here instead of silently reopening
                // whatever vault that window has, since two processes writing the same files without any
                // locking is how notes get corrupted. Explain that on-screen instead of leaving the user
                // guessing why they suddenly see the setup screen for what looks like a working install.
                infoMessage = LocalizationService.Instance["VaultSetup_AnotherInstance_Info"];
            }
            else if (!string.IsNullOrWhiteSpace(_settingsService.Settings.VaultPath))
            {
                // A vault path was saved before but the folder is gone now (moved/deleted/external drive
                // unplugged) — worth surfacing as a real failure, unlike a brand-new install where VaultPath
                // is simply empty and there's nothing to have failed yet.
                infoMessage = LocalizationService.Instance["VaultSetup_VaultMissing_Info"];
                _notificationService.PushLocalized(
                    LocalizedTextFormatter.Format("Shell_VaultLoadFailed_Title"),
                    LocalizedTextFormatter.Format("Shell_VaultLoadFailed_Message"),
                    Models.NotificationSeverity.Error);
            }

            VaultSetup = new VaultSetupViewModel(_vaultService, InitializeShell, infoMessage);
        }
    }

    private void InitializeShell()
    {
        // T4: el vault del arranque también cuenta como "abierto ahora", y este es el único punto por el
        // que pasa tanto el que venía guardado en settings.json como el que acaba de elegir
        // VaultSetupViewModel. Registrarlo aquí —y no en el constructor de VaultService— es lo que
        // distingue "hay una ruta guardada" de "se ha abierto de verdad": una segunda instancia llega al
        // constructor pero nunca hasta aquí (ver ShowVaultSetupOrShell).
        _settingsService.RegisterRecentVault(_vaultService.VaultPath);
        VaultSwitcher.Refresh();

        ReloadVaultData();
        _kanbanService.EnsureSeedBoard();
        // Guarantees every vault this shell ever opens has a "default" session — a brand-new vault, one
        // just pointed at for the first time, or one created by an older Enki build that predates
        // sessions entirely. EnsureDefaultSession is idempotent, so this is a no-op once it already exists.
        _sessionService.EnsureDefaultSession();
        // Siembra la carpeta de plantillas con sus dos ejemplos la primera vez que se abre cada vault —
        // aquí y no en el constructor porque el idioma de la interfaz ya está decidido a estas alturas
        // (mismo motivo por el que EnsureSeedBoard se llama justo arriba en vez de al construir
        // KanbanService), y las plantillas de ejemplo nacen traducidas.
        _noteTemplateService.EnsureInitialized();

        // Picks up this vault's own zoom level — needed here (not just the constructor) because this
        // method also runs after VaultSetupViewModel just pointed _vaultService at a brand-new vault,
        // which happened after _vaultSettingsService already loaded (see docs/funcionalidad-zoom-interfaz.md §2).
        _vaultSettingsService.Reload();
        UiZoomScale = _vaultSettingsService.Settings.UiZoomPercent / 100.0;

        // Empieza (o reinicia) la vigilancia del vault que se acaba de abrir. Idempotente: volver a
        // llamar libera el vigilante anterior antes de crear el nuevo, así que no filtra recursos si esta
        // inicialización se repite.
        _vaultChangeWatcher.Start(_vaultService.VaultPath);

        // Refrescar visibilidad del grupo fijo del sidebar por si cambió mientras el vault no estaba listo.
        NotificationsSidebarEnabled = _settingsService.Settings.NotificationsSidebarEnabled;
        RemindersSidebarEnabled = _settingsService.Settings.RemindersSidebarEnabled;

        // Un modo enfoque/zen activo en el vault anterior no debe seguir ocultando el
        // sidebar de este (propuesta 7, docs/propuesta-nuevas-funcionalidades.md). EditorPreferences.IsZenMode
        // is the real source of truth every EditorViewModel initializes from, so it — not just the mirrored
        // property below — has to be reset too, or the very first note opened in the new vault would read
        // the previous vault's leftover "true" and silently re-enter zen mode.
        EditorPreferences.IsZenMode = false;
        IsZenMode = false;

        var calendarLayerColorService = new CalendarLayerColorService(_kanbanService, _polarTaskService, _polarAuthService, _settingsService);
        var externalCalendarService = new ExternalCalendarService(System.IO.Path.GetDirectoryName(_settingsService.SettingsFilePath));
        externalCalendarService.RefreshFailed += _ => _notificationService.Push(
            LocalizationService.Instance["Calendar_External_Source"],
            LocalizationService.Instance["Calendar_External_Failed"], enki.Models.NotificationSeverity.Warning);
        var calendarAggregationService = new CalendarAggregationService(_kanbanService, _polarTaskService, _reminderService, _polarAuthService, calendarLayerColorService, externalCalendarService);
        Calendar = new CalendarViewModel(calendarAggregationService, calendarLayerColorService, _kanbanService, _polarTaskService, _reminderService, _polarAuthService, _settingsService, _notificationService, OpenCalendarItemEditor, externalCalendarService);
        Notes = new NotesViewModel(_vaultService, _sessionService, _kanbanService, OpenKanbanCard, _settingsService, _notificationService, _noteSearchService, _noteTemplateService, _vaultSettingsService,
            _habitService, () => Navigate(AppPage.Habits), () => Navigate(AppPage.Statistics), _vaultChangeWatcher);
        Notes.PropertyChanged += OnNotesPropertyChanged;
        // Restaurar pestañas abiertas al reiniciar (docs/ajustes-docs/02-productividad-como-app-de-escritorio.md §2) —
        // después de construir Notes pero antes de que la ventana quede lista, para que las pestañas ya
        // estén abiertas la primera vez que el usuario navega a Documentos.
        Notes.RestoreOpenTabs();
        Files = new FilesViewModel(_vaultService, _sessionService, _notificationService, _settingsService);
        Skills = new SkillsViewModel(_vaultService, _notificationService, _settingsService, StartAssistantChatWithSkill)
        {
            IsAssistantAvailable = AssistantPanelEnabled
        };
        // Herramientas del vault para el asistente: trabajan sobre los mismos servicios que las pantallas y
        // avisan a Notas antes y después de tocar una nota, igual que sus propias acciones. El servidor MCP no
        // escucha hasta el primer turno con la IA encendida.
        _aiVaultTools?.Stop();
        var aiVaultToolbox = new AiVaultToolbox(_vaultService, _sessionService, _kanbanService, _polarTaskService, _polarAuthService,
            _reminderService, _settingsService, calendarAggregationService, new AiVaultHost
            {
                PrepareNoteChange = path => Notes.PrepareAssistantNoteChange(path),
                NoteChanged = (oldPath, note) => Notes.ApplyAssistantNoteChange(oldPath, note),
                PrepareSessionRemoval = session => Notes.CloseEditorsUnderSession(session, out _),
                SessionsChanged = OnSessionsChanged,
                VaultChanged = OnAssistantChangedVault
            });
        _aiVaultTools = new EnkiMcpServer(aiVaultToolbox, work => Dispatcher.UIThread.InvokeAsync(work));
        Assistant = new AssistantViewModel(_claudeCliService, _aiChatService, _settingsService, _vaultService,
            _notificationService, AiCliStatus, _sessionService,
            activeNoteProvider: () => Notes?.ActiveEditor?.Note,
            flushPendingEdits: () => { foreach (var editor in Notes.OpenEditors) editor.FlushPendingSave(); },
            openNote: note => { Navigate(AppPage.Notes); Notes.OpenNote(note); },
            openSettings: OpenAiSettings,
            vaultTools: _aiVaultTools,
            openKanbanCard: OpenKanbanCard,
            isVisible: () => CurrentPage is AssistantViewModel,
            isPolarLinked: () => _polarAuthService.IsLinked);
        var taskManagement = new PolarTaskManagementViewModel(_polarTaskService, _polarSyncService, _settingsService)
        {
            ShowTagFilter = _settingsService.Settings.PolarShowTaskTags
        };
        Kanban = new KanbanViewModel(_kanbanService, _polarAuthService, taskManagement, _vaultService, _sessionService, OpenLinkedNoteFromKanban, _settingsService, _notificationService);
        Habits = new HabitsViewModel(_habitService, _settingsService);
        var statisticsAggregationService = new StatisticsAggregationService(_vaultService, _kanbanService, _polarTaskService, _polarAuthService, _habitService, _reminderService, _sessionService);
        Statistics = new StatisticsViewModel(statisticsAggregationService, _polarAuthService, _settingsService,
            loadOnCreate: false, loadInBackground: true, notificationService: _notificationService);
        Dashboard = new DashboardViewModel(_vaultService, _sessionService, _kanbanService, _polarTaskService,
            _polarAuthService, _notificationService, calendarAggregationService, _habitService, OpenNoteFromDashboard,
            () => Notes.NewNoteCommand.Execute(null), () => Navigate(AppPage.Kanban), () => Navigate(AppPage.Sessions),
            () => Navigate(AppPage.Notes), () => Navigate(AppPage.Trash),
            CalendarPanelEnabled, () => Navigate(AppPage.Calendar), OpenReminders, OpenNotifications, OpenSettings,
            () => Navigate(AppPage.Habits),
            _settingsService.Settings.DashboardShowScrollbar, _vaultSettingsService,
            loadOnCreate: false, loadInBackground: true);
        Sessions = new SessionsViewModel(_vaultService, _sessionService, _notificationService, OpenSessionInNotes, OnSessionsChanged,
            _kanbanService, OpenKanbanCard, Notes.CloseEditorsForLockedSession, StartAssistantChatInSession)
        {
            IsAssistantAvailable = AssistantPanelEnabled
        };
        Trash = new TrashViewModel(_vaultService, _kanbanService, _polarTaskService, _reminderService, _notificationService, _sessionService, OnSessionsChanged);
        Trash.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TrashViewModel.TotalCount)) UpdateTrashBadge();
        };
        var tagManagerService = new TagManagerService(_vaultService, _kanbanService, _polarTaskService);
        TagManager = new TagManagerViewModel(tagManagerService, _notificationService,
            () =>
            {
                foreach (var editor in Notes.OpenEditors) editor.FlushPendingSave();
                if (Notes.OpenEditors.All(editor => !editor.IsDirty)) return true;
                _notificationService.PushLocalized(
                    LocalizedTextFormatter.Format("TagManager_Error_Title"),
                    LocalizedTextFormatter.Format("TagManager_SaveBlocked"), Models.NotificationSeverity.Error);
                return false;
            },
            () =>
            {
                foreach (var editor in Notes.OpenEditors) editor.ReloadAfterTagChange();
                Notes.Reload();
                Kanban.Reload();
                Kanban.TaskManagement.Reload();
                InvalidatePages(AppPage.Dashboard, AppPage.Statistics);
            });
        Settings = new SettingsViewModel(_settingsService, _customThemeService, _brandLogoService, _vaultSettingsService, _vaultService, _vaultBackupService, _polarAuthService,
            _polarSyncService, _notificationService, SwitchToVaultAsync, OnTitleBarModeChanged,
            OnNotificationsPanelModeChanged, OnRemindersPanelModeChanged, OnCalendarShowSidebarScrollbarModeChanged,
            OnSidebarSectionChanged, OnSidebarSectionOrderChanged, OnCalendarSettingsChanged, OnDashboardShowScrollbarModeChanged,
            OnKanbanBoardOrientationChanged,
            OnPolarShowTaskTagsChanged, OnUiZoomChanged, RefreshAllLocalizedLabels, _noteTemplateService,
            OnInboxPanelModeChanged, OnVaultSwitcherModeChanged, _updateCheckService, _kanbanService,
            () => { Navigate(AppPage.Kanban); Kanban.DisplayMode = KanbanDisplayMode.Board; Kanban.IsBoardSelectorOpen = true; },
            _reminderService, () => { Notes.Reload(); Kanban.Reload(); }, externalCalendarService,
            AiCliStatus, _aiChatService, SidePanels);
        Reminders = new RemindersViewModel(_reminderService, _polarAuthService, _polarSyncService);
        Notifications = new NotificationsViewModel(_notificationService);
        Reminders.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RemindersViewModel.UnreadCount)) UpdateRemindersBadge();
        };
        Notifications.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NotificationsViewModel.UnreadCount)) UpdateNotificationsBadge();
        };

        RebuildNavItems();

        _dirtyPages.Clear();
        _dirtyPages.Add(AppPage.Statistics);
        _dirtyPages.Add(AppPage.Dashboard);

        var startupPage = ResolveStartupPage();
        CurrentPage = startupPage switch
        {
            AppPage.Calendar => Calendar,
            AppPage.Kanban => Kanban,
            AppPage.Notes => Notes,
            AppPage.Files => Files,
            AppPage.Sessions => Sessions,
            AppPage.TagManager => TagManager,
            AppPage.Habits => Habits,
            AppPage.Statistics => Statistics,
            _ => Dashboard
        };
        foreach (var item in NavItems) item.IsActive = item.Page == startupPage;
        foreach (var item in BottomNavItems) item.IsActive = item.Page == startupPage;
        VaultSetup = null;
        IsVaultReady = true;
        // La página de inicio se prepara antes que las demás y es la que fija en qué momento se retira la
        // pantalla de arranque: ver RunSectionWarmUpAsync.
        StartSectionWarmUp(startupPage);

        UpdateTrashBadge();
        UpdateCalendarBadge();
        UpdateRemindersBadge();
        UpdateNotificationsBadge();
    }

    /// <summary>Reads <c>AppSettings.StartupPage</c> (Ajustes › General) and falls back to
    /// <see cref="FallbackStartupPage"/> when the stored value isn't parseable, isn't one of the selectable
    /// start pages, or points at a page currently hidden by one of the sidebar-section toggles (Ajustes ›
    /// General › Secciones de la barra lateral) — an app that always failed to start would be worse than
    /// one that silently lands on a page that's actually visible. Recordatorios y Notificaciones no son
    /// páginas de inicio seleccionables; Estadísticas sigue siéndolo aunque ahora viva en el grupo fijo.</summary>
    private AppPage ResolveStartupPage()
    {
        if (!Enum.TryParse<AppPage>(_settingsService.Settings.StartupPage, out var page)) return FallbackStartupPage();

        var isSelectable = page is AppPage.Dashboard or AppPage.Calendar or AppPage.Kanban or AppPage.Notes
            or AppPage.Files or AppPage.Sessions or AppPage.Habits or AppPage.Statistics;
        if (!isSelectable) return FallbackStartupPage();
        if (page == AppPage.Statistics) return StatisticsPanelEnabled ? page : FallbackStartupPage();
        if (!IsSidebarSectionEnabled(page)) return FallbackStartupPage();

        return page;
    }

    /// <summary>Dashboard used to be a safe unconditional fallback; now that it's a toggleable sidebar
    /// section too, falls further back to whichever section is first in <see cref="NavItems"/> (built by
    /// <see cref="RebuildNavItems"/> right before this runs), and only to Dashboard itself if the whole nav
    /// somehow ended up empty.</summary>
    private AppPage FallbackStartupPage() =>
        DashboardPanelEnabled ? AppPage.Dashboard
        : NavItems.FirstOrDefault()?.Page ?? BottomNavItems.FirstOrDefault()?.Page ?? AppPage.Dashboard;
}
