using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Asistente: chat con Claude, OpenAI o modelos locales (ver <see cref="AiBackendService"/>), con
/// historial de conversaciones separado por vault, notas y archivos del vault como contexto y skills como
/// instrucciones de partida. Una sola instancia sirve a las dos superficies —la sección Asistente y el panel
/// lateral que se abre junto a cualquier otra sección—, así que cambiar de una a otra conserva chat, borrador,
/// adjuntos, permisos y respuesta en curso. Este archivo lleva el estado del CLI, el historial y la
/// conversación abierta; el resto vive en sus parciales (<c>.Composer</c>, <c>.Scope</c>, <c>.Drafts</c>,
/// <c>.Vault</c>, <c>.Activity</c>, <c>.NoteInsert</c>, <c>.Effort</c>). Ver
/// <c>.docs/project-info/asistente-ia.md</c>.</summary>
public sealed partial class AssistantViewModel : ViewModelBase
{
    private readonly AiBackendService _cli;
    private readonly AiChatService _chats;
    private readonly SettingsService _settingsService;
    private readonly VaultService _vaultService;
    private readonly SessionService? _sessionService;
    private readonly NotificationService _notificationService;
    private readonly Func<NoteFile?> _activeNoteProvider;
    private readonly Action _flushPendingEdits;
    private readonly Action<NoteFile> _openNote;
    private readonly Action _openSettings;

    /// <summary>Servidor MCP de las herramientas del vault, o <see langword="null"/> en los tests que no lo
    /// componen (el asistente se queda en modo solo texto).</summary>
    private readonly EnkiMcpServer? _vaultTools;

    private readonly Action<string> _openKanbanCard;
    private readonly Func<bool> _isVisible;
    private readonly Func<bool> _isPolarLinked;
    /// <summary>Identidad del vault abierto: con <see langword="false"/> la consulta (puede no tenerla todavía);
    /// con <see langword="true"/> la crea si hace falta. Ver <c>AssistantViewModel.Vault.cs</c>.</summary>
    private readonly Func<bool, string?> _vaultIdentity;

    /// <summary>Lleva al hilo de interfaz lo que llega desde el hilo de fondo del proceso del CLI. Inyectable
    /// para que los tests lo ejecuten en línea sin una aplicación Avalonia levantada.</summary>
    private readonly Action<Action> _dispatch;

    /// <summary>Conversación abierta. <see langword="null"/> en un chat nuevo que aún no se ha enviado: no se
    /// guarda nada en el historial hasta el primer mensaje.</summary>
    private AiChatSession? _activeSession;

    private bool _restoredLastChat;

    public AssistantViewModel(
        ClaudeCliService cli,
        AiChatService chats,
        SettingsService settingsService,
        VaultService vaultService,
        NotificationService notificationService,
        ClaudeCliStatusViewModel? cliStatus = null,
        SessionService? sessionService = null,
        Func<NoteFile?>? activeNoteProvider = null,
        Action? flushPendingEdits = null,
        Action<NoteFile>? openNote = null,
        Action? openSettings = null,
        Action<Action>? dispatch = null,
        EnkiMcpServer? vaultTools = null,
        Action<string>? openKanbanCard = null,
        Func<bool>? isVisible = null,
        Func<bool>? isPolarLinked = null,
        Func<bool, string?>? vaultIdentity = null,
        Func<string, string, NoteDiskStamp, NoteFile>? appendToNote = null)
    {
        _vaultIdentity = vaultIdentity ?? (_ => VaultSettingsService.PathIdentity(vaultService.VaultPath));
        _appendToNote = appendToNote;
        _vaultTools = vaultTools;
        _openKanbanCard = openKanbanCard ?? (_ => { });
        _isVisible = isVisible ?? (() => true);
        _isPolarLinked = isPolarLinked ?? (() => false);
        _cli = cliStatus?.Backend ?? new AiBackendService(cli, settingsService);
        _chats = chats;
        _settingsService = settingsService;
        _vaultService = vaultService;
        _sessionService = sessionService;
        _notificationService = notificationService;
        _activeNoteProvider = activeNoteProvider ?? (() => null);
        _flushPendingEdits = flushPendingEdits ?? (() => { });
        _openNote = openNote ?? (_ => { });
        _openSettings = openSettings ?? (() => { });
        _dispatch = dispatch ?? (action => Dispatcher.UIThread.Post(action));

        ModelOptions = new ObservableCollection<AiModelOption>(AiModelOption.Build(_cli.Provider, _cli.LocalModels));
        _selectedModel = FindModel(_settingsService.Settings.Ai.ModelFor(_cli.Provider));
        Cli = cliStatus ?? new ClaudeCliStatusViewModel(cli, _dispatch, _cli);
        Cli.PropertyChanged += OnCliPropertyChanged;
        _cli.ProviderChanged += () =>
        {
            CancelAll();
            StartNewChat();
            RestoreDraft();
            OnPropertyChanged(nameof(SelectedProvider));
            if (_settingsService.Settings.Ai.Enabled) _ = Cli.RefreshAsync();
        };
        _chats.ChatsChanged += () => _dispatch(OnChatsChanged);

        Messages.CollectionChanged += (_, _) => RefreshConversationState();
        PendingAttachments.CollectionChanged += (_, _) => RefreshComposerState();
        RefreshVaultIdentity();
        RebuildSessionList();
        RefreshScope();
        RestoreDraft();
    }

    /// <summary>Pide a la vista que baje hasta el último mensaje (al enviar y según llega la respuesta).</summary>
    public event Action? ScrollToEndRequested;

    /// <summary>Pide a la superficie que se acaba de enseñar (sección o panel) que retome el punto de lectura
    /// de <see cref="TranscriptScrollOffset"/>, o el final al abrir otra conversación.</summary>
    public event Action? TranscriptRestoreRequested;

    // ===================== Estado del CLI =====================

    /// <summary>Estado compartido con Ajustes › Inteligencia artificial.</summary>
    public ClaudeCliStatusViewModel Cli { get; }

    /// <summary>Panel de puesta en marcha (instalar, iniciar sesión, actualizar) en lugar del chat.</summary>
    public bool ShowSetupPanel => Cli.IsKnown && !Cli.IsReady;

    /// <summary>Claude con clave de API propia y sin clave guardada: lo que falta se arregla en Ajustes.</summary>
    public bool ShowApiKeySetup => Cli.NeedsApiKey;

    /// <summary>Sin sesión iniciada en el CLI: se enseña la orden de inicio de sesión para una terminal.</summary>
    public bool ShowLoginSetup => Cli.NeedsLogin;

    private void OnCliPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClaudeCliStatusViewModel.IsChecking))
        {
            RefreshComposerState();
            return;
        }
        if (e.PropertyName != nameof(ClaudeCliStatusViewModel.Status)) return;
        RefreshModels();
        OnPropertyChanged(nameof(ShowSetupPanel));
        OnPropertyChanged(nameof(ShowWelcome));
        OnPropertyChanged(nameof(ShowTranscript));
        OnPropertyChanged(nameof(ShowApiKeySetup));
        OnPropertyChanged(nameof(ShowLoginSetup));
        OnPropertyChanged(nameof(VaultToolsAvailable));
        OnPropertyChanged(nameof(ScopeSummary));
        OnPropertyChanged(nameof(ScopeWarning));
        OnPropertyChanged(nameof(HasScopeWarning));
        OnPropertyChanged(nameof(CanChangeEffort));
        OnPropertyChanged(nameof(ShowEffortUnavailable));
        OnPropertyChanged(nameof(EffortSummary));
        RefreshComposerState();
    }

    /// <summary>Lo llama el shell cada vez que se navega a la sección: comprueba el CLI si todavía no está
    /// listo (el usuario puede volver de una terminal donde acaba de iniciar sesión), restaura el último chat
    /// la primera vez y actualiza el botón de adjuntar la nota activa.</summary>
    public void OnActivated()
    {
        if (!_restoredLastChat)
        {
            _restoredLastChat = true;
            if (_activeSession is null
                && _settingsService.Settings.Ai.LastChatByVault.TryGetValue(VaultKey, out var lastId)
                && _chats.Find(lastId) is { } last && BelongsToVault(last))
            {
                SaveDraft();
                OpenSession(last);
            }
        }
        MarkConversationSeen();
        TranscriptRestoreRequested?.Invoke();

        var stale = DateTime.UtcNow - Cli.LastCheckedUtc > TimeSpan.FromSeconds(5);
        if (!IsSending && !Cli.IsChecking && (!Cli.IsReady || stale)) _ = Cli.RefreshAsync();
        // Una sesión puede haberse bloqueado, borrado o renombrado mientras se estaba en otra sección.
        RefreshScope();
        if (_chats.UnreadableFileCount > 0) NotifyUnreadableChats();
    }

    private bool _unreadableChatsNotified;

    private void NotifyUnreadableChats()
    {
        if (_unreadableChatsNotified) return;
        _unreadableChatsNotified = true;
        _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Assistant_Notify_UnreadableChats_Title"),
            LocalizedTextFormatter.PluralFormat("Assistant_Notify_UnreadableChats_Message", _chats.UnreadableFileCount,
                _chats.UnreadableFileCount),
            NotificationSeverity.Warning);
    }

    [RelayCommand]
    private void OpenSettings() => _openSettings();

    [RelayCommand]
    private void OpenSetupDocumentation() => UrlOpener.Open(Cli.SetupDocumentationUrl);

    [RelayCommand]
    private void OpenTerms() => UrlOpener.Open(Cli.TermsUrl);

    [RelayCommand]
    private static async Task CopyTextAsync(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    // ===================== Historial =====================

    /// <summary>Pliega el historial para dejar más espacio a la conversación sin perder su estado.</summary>
    [ObservableProperty]
    private bool _isHistoryCollapsed;

    [RelayCommand]
    private void ToggleHistory() => IsHistoryCollapsed = !IsHistoryCollapsed;

    public ObservableCollection<AiChatSessionItemViewModel> Sessions { get; } = [];

    public ObservableCollection<AiChatGroupViewModel> ChatGroups { get; } = [];

    [ObservableProperty]
    private string _sessionSearchText = string.Empty;

    /// <summary>Hay conversaciones que enseñar en este vault (las suyas o las antiguas sin vault asignado).</summary>
    public bool HasAnySessions => _chats.Sessions.Any(s => BelongsToVault(s) || s.VaultId is null);
    public bool HasNoSearchMatches => HasAnySessions && Sessions.Count == 0;
    public bool HasSessionSearch => SessionSearchText.Length > 0;

    partial void OnSessionSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSessionSearch));
        RebuildSessionList();
    }

    [RelayCommand]
    private void ClearSessionSearch() => SessionSearchText = string.Empty;

    /// <summary>El historial del vault abierto, agrupado en «Vault» y una entrada por sesión, y al final las
    /// conversaciones antiguas sin vault asignado. Buscar filtra sin cambiar el ámbito de nada; un grupo sin
    /// coincidencias desaparece.</summary>
    private void RebuildSessionList()
    {
        var query = SessionSearchText.Trim();
        bool Matches(AiChatSession s) => query.Length == 0
            || s.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (s.SessionId is { } id && ScopeNameFor(id, s.SessionName).Contains(query, StringComparison.CurrentCultureIgnoreCase))
            || (s.SkillName?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || s.Messages.Any(m => m.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase));

        Sessions.Clear();
        foreach (var session in _chats.Sessions.Where(BelongsToVault).Concat(_chats.UnassignedSessions).Where(Matches))
            Sessions.Add(new AiChatSessionItemViewModel(session) { IsActive = session == _activeSession });
        RefreshSessionActivity();

        ChatGroups.Clear();
        foreach (var group in Sessions.Where(item => !item.IsUnassigned).GroupBy(item => item.Session.SessionId)
                     .OrderBy(group => group.Key is null ? 0 : 1))
        {
            var title = group.Key is null
                ? LocalizationService.Instance["Assistant_Scope_Vault"]
                : ScopeNameFor(group.Key, group.First().Session.SessionName);
            ChatGroups.Add(new AiChatGroupViewModel(title, group.ToList()));
        }
        if (Sessions.Where(item => item.IsUnassigned).ToList() is { Count: > 0 } unassigned)
            ChatGroups.Add(new AiChatGroupViewModel(LocalizationService.Instance["Assistant_History_Unassigned"], unassigned,
                IsUnassigned: true));
        OnPropertyChanged(nameof(HasAnySessions));
        OnPropertyChanged(nameof(HasNoSearchMatches));
    }

    private void OnChatsChanged()
    {
        // "Borrar historial" desde Ajustes puede llevarse por delante el chat abierto.
        if (_activeSession is not null && _chats.Find(_activeSession.Id) is null && !IsSending) StartNewChat();
        RebuildSessionList();
    }

    [RelayCommand]
    private void NewChat()
    {
        if (IsSending) return;
        SaveDraft();
        StartNewChat();
        RestoreDraft();
        IsPanelHistoryOpen = false;
        ComposerFocusRequested?.Invoke();
    }

    /// <param name="forgetLastChat">Deja de recordar el último chat del vault (lo que quiere el usuario al pulsar
    /// "nuevo chat"); al cambiar de vault no, para volver a él al regresar.</param>
    private void StartNewChat(bool forgetLastChat = true)
    {
        _activeSession = null;
        Messages.Clear();
        DraftSkillName = null;
        DraftSkillInstructions = null;
        _restoringDraft = true;
        DraftText = string.Empty;
        _restoringDraft = false;
        PendingAttachments.Clear();
        DraftMissingAttachments = 0;
        ClearError();
        _retryTurn = null;
        IsRenamingTitle = false;
        if (forgetLastChat && _settingsService.Settings.Ai.LastChatByVault.Remove(VaultKey)) TrySaveSettings();
        RefreshModels();
        SelectedModel = FindModel(_settingsService.Settings.Ai.ModelFor(_cli.Provider));
        RestoreEffort("medium");
        // Un chat nuevo nace siempre en el vault y sin acceso a las sesiones.
        ResetDraftScope();
        RefreshHeader();
        RefreshScope();
        foreach (var item in Sessions) item.IsActive = false;
    }

    [RelayCommand]
    private void SelectSession(AiChatSessionItemViewModel? item)
    {
        if (item is null || IsSending || item.Session == _activeSession) return;
        SaveDraft();
        OpenSession(item.Session);
        IsPanelHistoryOpen = false;
    }

    private void OpenSession(AiChatSession session)
    {
        if (!Enum.IsDefined(session.Provider)) return;
        _cli.SelectProvider(session.Provider);
        _activeSession = session;
        RefreshModels();
        Messages.Clear();
        foreach (var message in session.Messages) Messages.Add(new AiChatMessageViewModel(message));
        PendingAttachments.Clear();
        ClearError();
        _retryTurn = null;
        IsRenamingTitle = false;
        SelectedModel = FindModel(session.Model);
        RestoreEffort(session.Effort);
        foreach (var item in Sessions) item.IsActive = item.Session == session;
        RememberActiveChat();
        RefreshHeader();
        RefreshScope();
        RestoreDraft();
        TranscriptScrollOffset = null;
        TranscriptRestoreRequested?.Invoke();
    }

    private void RememberActiveChat()
    {
        // Una conversación sin vault asignado no es "la última de este vault" hasta que se asocie.
        if (_activeSession is not { } session || !BelongsToVault(session)) return;
        var last = _settingsService.Settings.Ai.LastChatByVault;
        if (last.TryGetValue(VaultKey, out var id) && id == session.Id) return;
        last[VaultKey] = session.Id;
        TrySaveSettings();
    }

    [ObservableProperty]
    private AiChatSessionItemViewModel? _pendingDeleteSession;

    [RelayCommand]
    private void RequestDeleteSession(AiChatSessionItemViewModel? item)
    {
        if (item is null || (IsSending && item.Session == _activeSession)) return;
        PendingDeleteSession = item;
    }

    [RelayCommand]
    private void CancelDeleteSession() => PendingDeleteSession = null;

    [RelayCommand]
    private void ConfirmDeleteSession()
    {
        if (PendingDeleteSession is not { } item) return;
        PendingDeleteSession = null;
        try
        {
            var wasActive = item.Session == _activeSession;
            _chats.Delete(item.Id);
            if (wasActive)
            {
                StartNewChat();
                RestoreDraft();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Notify_DeleteFailed_Title"),
                VaultFailureReasons.DescribeLocalized(ex), NotificationSeverity.Error);
        }
    }

    // ===================== Conversación abierta =====================

    public ObservableCollection<AiChatMessageViewModel> Messages { get; } = [];

    public bool HasMessages => Messages.Count > 0;

    /// <summary>Pantalla de bienvenida de un chat vacío (con el CLI listo).</summary>
    public bool ShowWelcome => Cli.IsReady && !HasMessages;

    /// <summary>La conversación tiene mensajes y el proveedor está listo: se enseña el hilo.</summary>
    public bool ShowTranscript => Cli.IsReady && HasMessages;

    public bool IsDraft => _activeSession is null;

    public string ChatTitle => _activeSession is { Title.Length: > 0 } session
        ? session.Title
        : LocalizationService.Instance["Assistant_Chat_New"];

    /// <summary>Skill del chat abierto, o la elegida para el que se va a empezar.</summary>
    public string? ActiveSkillName => _activeSession is not null ? _activeSession.SkillName : DraftSkillName;

    public bool HasActiveSkill => !string.IsNullOrWhiteSpace(ActiveSkillName);

    [ObservableProperty]
    private bool _isRenamingTitle;

    [ObservableProperty]
    private string _titleDraft = string.Empty;

    [RelayCommand]
    private void BeginRename()
    {
        if (_activeSession is null) return;
        TitleDraft = _activeSession.Title;
        IsRenamingTitle = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        if (!IsRenamingTitle) return;
        IsRenamingTitle = false;
        var title = TitleDraft.Trim();
        if (_activeSession is null || title.Length == 0 || title == _activeSession.Title) return;
        _activeSession.Title = title.Length > 120 ? title[..120] : title;
        TrySaveSession(_activeSession, touch: false);
        RefreshHeader();
    }

    [RelayCommand]
    private void CancelRename() => IsRenamingTitle = false;

    // ===================== Modelo =====================

    public ObservableCollection<AiModelOption> ModelOptions { get; }
    public IReadOnlyList<AiProviderOption> ProviderOptions { get; } = AiProviderOption.Build();
    public AiProviderOption SelectedProvider
    {
        get => ProviderOptions.First(p => p.Provider == _cli.Provider);
        set
        {
            if (value is null || IsSending) return;
            _settingsService.Settings.Ai.Provider = value.Provider;
            _cli.SelectProvider(value.Provider);
            TrySaveSettings();
        }
    }

    private void RefreshModels()
    {
        var alias = _activeSession?.Model ?? _settingsService.Settings.Ai.ModelFor(_cli.Provider);
        var options = AiModelOption.Build(_cli.Provider, _cli.LocalModels, alias);
        if (ModelOptions.SequenceEqual(options)) return;
        ModelOptions.Clear();
        foreach (var option in options) ModelOptions.Add(option);
        SelectedModel = FindModel(alias);
    }

    [ObservableProperty]
    private AiModelOption? _selectedModel;

    partial void OnSelectedModelChanged(AiModelOption? value)
    {
        if (value is null) return;
        var modelChanged = _cli.Model != value.Alias;
        _cli.Model = value.Alias;
        if (modelChanged && _cli.Provider == AiProvider.Ollama && !IsSending && _settingsService.Settings.Ai.Enabled) _ = Cli.RefreshAsync();
        if (_activeSession is null || _activeSession.Model == value.Alias) return;
        // El cambio se aplica a los turnos siguientes del mismo chat: el CLI retoma la conversación con el
        // modelo nuevo sin perder el contexto.
        _activeSession.Model = value.Alias;
        TrySaveSession(_activeSession, touch: false);
    }

    private AiModelOption FindModel(string? alias) =>
        ModelOptions.FirstOrDefault(m => string.Equals(m.Alias, alias ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        ?? ModelOptions[0];

    // ===================== Acciones sobre una respuesta =====================

    [RelayCommand]
    private static async Task CopyMessageAsync(AiChatMessageViewModel? message)
    {
        if (message is null || !message.HasText) return;
        await CopyTextAsync(message.Text);
        message.IsCopied = true;
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        message.IsCopied = false;
    }

    /// <summary>"Guardar como nota": crea una nota nueva con la respuesta en la raíz del espacio del chat —el
    /// vault, o la sesión en la que se abrió— y la abre. Es siempre una nota nueva, nunca una escritura sobre
    /// otra existente, así que no compite con lo que el usuario pueda tener abierto.</summary>
    [RelayCommand]
    private void SaveMessageAsNote(AiChatMessageViewModel? message)
    {
        if (message is not { CanAct: true }) return;
        var title = _activeSession is { Title.Length: > 0 } session
            ? session.Title
            : LocalizationService.Instance["Assistant_Note_DefaultTitle"];
        RefreshScope();
        var targetSession = IsSessionChat ? _scopeSession : null;
        try
        {
            if (IsSessionChat && targetSession is null) throw new DirectoryNotFoundException();
            if (targetSession is not null && !_sessionService!.IsUnlocked(targetSession))
                throw new SessionLockedException(targetSession.RootPath);
            var note = _vaultService.CreateNote(title, folderPath: targetSession?.RootPath,
                initialContent: $"# {title}\n\n{message.Text.Trim()}\n");
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Notify_NoteSaved_Title"),
                targetSession is null
                    ? LocalizedTextFormatter.Format("Assistant_Notify_NoteSaved_Message", note.Title)
                    : LocalizedTextFormatter.Format("Assistant_Notify_NoteSaved_SessionMessage", note.Title, targetSession.Name),
                NotificationSeverity.Success);
            _openNote(note);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultUnavailableException or SessionLockedException)
        {
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Notify_NoteSaveFailed_Title"),
                VaultFailureReasons.DescribeLocalized(ex), NotificationSeverity.Error);
        }
    }

    /// <summary>Abre lo que tocó una acción del asistente: la nota en Documentos o la tarjeta en Kanban.</summary>
    [RelayCommand]
    private void OpenActivity(AiToolActivityViewModel? activity)
    {
        if (activity is not { CanOpen: true } || activity.Model.TargetId is not { } id) return;
        switch (activity.Model.TargetKind)
        {
            case AiToolTargetKind.Note when VaultService.IsWithinFolder(id, _vaultService.VaultPath)
                && !_vaultService.IsPathInLockedSession(id) && _vaultService.GetNote(id) is { } note:
                _openNote(note);
                break;
            case AiToolTargetKind.Note:
                _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Notify_ActivityTargetMissing_Title"),
                    LocalizedTextFormatter.Format("Assistant_Notify_ActivityTargetMissing_Message", activity.Target), NotificationSeverity.Info);
                break;
            case AiToolTargetKind.KanbanCard:
                _openKanbanCard(id);
                break;
        }
    }

    // ===================== Utilidades =====================

    private void RefreshHeader()
    {
        OnPropertyChanged(nameof(ActivityTooltip));
        OnPropertyChanged(nameof(IsUnassignedChat));
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsDraft));
        OnPropertyChanged(nameof(ChatTitle));
        OnPropertyChanged(nameof(ActiveSkillName));
        OnPropertyChanged(nameof(HasActiveSkill));
        OnPropertyChanged(nameof(CanPickSkill));
        OnPropertyChanged(nameof(CanChangeWorkspace));
    }

    private void RefreshConversationState()
    {
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(ShowWelcome));
        OnPropertyChanged(nameof(ShowTranscript));
        OnPropertyChanged(nameof(CanPickSkill));
        OnPropertyChanged(nameof(CanChangeWorkspace));
    }

    /// <summary>Guarda la conversación; un fallo de disco se avisa pero no interrumpe el chat, que sigue en
    /// memoria y se volverá a intentar guardar en el siguiente turno.</summary>
    private bool TrySaveSession(AiChatSession session, bool touch = true)
    {
        if (touch) session.UpdatedUtc = DateTime.UtcNow;
        try
        {
            _chats.Save(session);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Notify_SaveFailed_Title"),
                VaultFailureReasons.DescribeLocalized(ex), NotificationSeverity.Error);
            return false;
        }
    }

    private void TrySaveSettings()
    {
        try
        {
            _settingsService.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            LogService.Warning("No se pudo recordar el último chat del asistente", ex);
        }
    }

    /// <summary>Apagar la IA desde Ajustes (o cerrar Enki) corta en seco cualquier respuesta en curso. Lo que
    /// el usuario tuviera escrito se guarda antes como borrador.</summary>
    public void CancelAll()
    {
        SaveDraft();
        _turnCancellation?.Cancel();
    }

    public void RefreshLocalizedLabels()
    {
        var alias = SelectedModel?.Alias;
        ModelOptions.Clear();
        foreach (var option in AiModelOption.Build(_cli.Provider, _cli.LocalModels, alias)) ModelOptions.Add(option);
        SelectedModel = FindModel(alias);

        OnPropertyChanged(nameof(ActiveNoteLabel));
        OnPropertyChanged(nameof(AppendToActiveNoteLabel));
        RefreshHeader();
        RefreshScopeLocalizedLabels();
        RefreshActivityLocalizedLabels();
        OnPropertyChanged(nameof(RetryLabel));
        OnPropertyChanged(nameof(DraftNotice));
        foreach (var session in Sessions) session.RefreshLocalizedLabels();
        foreach (var message in Messages) message.RefreshLocalizedLabels();
        OnPropertyChanged(nameof(EffortLabel));
        OnPropertyChanged(nameof(EffortSummary));
        RebuildSessionList();
        if (_lastFailure is { } failure) ShowFailure(failure);
    }
}
