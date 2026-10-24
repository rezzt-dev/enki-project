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

/// <summary>Sección "Asistente": chat con Claude a través del CLI oficial que el usuario tiene instalado y
/// con su sesión iniciada (ver <see cref="ClaudeCliService"/>), con historial de conversaciones, notas del
/// vault como contexto y skills como instrucciones de partida. Este archivo lleva el estado del CLI, el
/// historial y la conversación abierta; el compositor y el envío de turnos viven en
/// <c>AssistantViewModel.Composer.cs</c>. Ver <c>.docs/project-info/asistente-ia.md</c>.</summary>
public sealed partial class AssistantViewModel : ViewModelBase
{
    private readonly ClaudeCliService _cli;
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
        Func<bool>? isPolarLinked = null)
    {
        _vaultTools = vaultTools;
        _openKanbanCard = openKanbanCard ?? (_ => { });
        _isVisible = isVisible ?? (() => true);
        _isPolarLinked = isPolarLinked ?? (() => false);
        _cli = cli;
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

        ModelOptions = new ObservableCollection<AiModelOption>(AiModelOption.Build());
        _selectedModel = FindModel(_settingsService.Settings.Ai.DefaultModel);
        Cli = cliStatus ?? new ClaudeCliStatusViewModel(cli, _dispatch);
        Cli.PropertyChanged += OnCliPropertyChanged;
        _chats.ChatsChanged += () => _dispatch(OnChatsChanged);

        Messages.CollectionChanged += (_, _) => RefreshConversationState();
        PendingAttachments.CollectionChanged += (_, _) => RefreshComposerState();
        RebuildSessionList();
        RefreshScope();
    }

    /// <summary>Pide a la vista que baje hasta el último mensaje (al enviar y según llega la respuesta).</summary>
    public event Action? ScrollToEndRequested;

    // ===================== Estado del CLI =====================

    /// <summary>Estado compartido con Ajustes › Inteligencia artificial.</summary>
    public ClaudeCliStatusViewModel Cli { get; }

    /// <summary>Panel de puesta en marcha (instalar, iniciar sesión, actualizar) en lugar del chat.</summary>
    public bool ShowSetupPanel => Cli.IsKnown && !Cli.IsReady;

    private void OnCliPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ClaudeCliStatusViewModel.Status)) return;
        OnPropertyChanged(nameof(ShowSetupPanel));
        OnPropertyChanged(nameof(ShowWelcome));
        OnPropertyChanged(nameof(VaultToolsAvailable));
        OnPropertyChanged(nameof(ScopeSummary));
        OnPropertyChanged(nameof(ScopeWarning));
        OnPropertyChanged(nameof(HasScopeWarning));
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
            if (_chats.Find(_settingsService.Settings.Ai.LastChatId) is { } last) OpenSession(last);
        }

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
    private static void OpenSetupDocumentation() => UrlOpener.Open(ClaudeCliService.SetupDocumentationUrl);

    [RelayCommand]
    private static void OpenTerms() => UrlOpener.Open(ClaudeCliService.LegalAndComplianceUrl);

    [RelayCommand]
    private static async Task CopyTextAsync(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    // ===================== Historial =====================

    public ObservableCollection<AiChatSessionItemViewModel> Sessions { get; } = [];

    [ObservableProperty]
    private string _sessionSearchText = string.Empty;

    public bool HasAnySessions => _chats.Sessions.Count > 0;
    public bool HasNoSearchMatches => HasAnySessions && Sessions.Count == 0;

    partial void OnSessionSearchTextChanged(string value) => RebuildSessionList();

    private void RebuildSessionList()
    {
        var query = SessionSearchText.Trim();
        var matches = _chats.Sessions.Where(s => query.Length == 0
            || s.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (s.SkillName?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || s.Messages.Any(m => m.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)));

        Sessions.Clear();
        foreach (var session in matches)
            Sessions.Add(new AiChatSessionItemViewModel(session) { IsActive = session == _activeSession });
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
        StartNewChat();
        ComposerFocusRequested?.Invoke();
    }

    private void StartNewChat()
    {
        _activeSession = null;
        Messages.Clear();
        DraftSkillName = null;
        DraftSkillInstructions = null;
        PendingAttachments.Clear();
        ClearError();
        IsRenamingTitle = false;
        _settingsService.Settings.Ai.LastChatId = null;
        SelectedModel = FindModel(_settingsService.Settings.Ai.DefaultModel);
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
        OpenSession(item.Session);
    }

    private void OpenSession(AiChatSession session)
    {
        _activeSession = session;
        Messages.Clear();
        foreach (var message in session.Messages) Messages.Add(new AiChatMessageViewModel(message));
        PendingAttachments.Clear();
        ClearError();
        IsRenamingTitle = false;
        SelectedModel = FindModel(session.Model);
        foreach (var item in Sessions) item.IsActive = item.Session == session;
        RememberActiveChat();
        RefreshHeader();
        RefreshScope();
        ScrollToEndRequested?.Invoke();
    }

    private void RememberActiveChat()
    {
        if (_settingsService.Settings.Ai.LastChatId == _activeSession?.Id) return;
        _settingsService.Settings.Ai.LastChatId = _activeSession?.Id;
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
            if (wasActive) StartNewChat();
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

    [ObservableProperty]
    private AiModelOption? _selectedModel;

    partial void OnSelectedModelChanged(AiModelOption? value)
    {
        if (value is null || _activeSession is null || _activeSession.Model == value.Alias) return;
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

    /// <summary>Apagar la IA desde Ajustes corta en seco cualquier respuesta en curso.</summary>
    public void CancelAll() => _turnCancellation?.Cancel();

    public void RefreshLocalizedLabels()
    {
        var alias = SelectedModel?.Alias;
        ModelOptions.Clear();
        foreach (var option in AiModelOption.Build()) ModelOptions.Add(option);
        SelectedModel = FindModel(alias);

        OnPropertyChanged(nameof(ActiveNoteLabel));
        RefreshHeader();
        RefreshScopeLocalizedLabels();
        foreach (var session in Sessions) session.RefreshLocalizedLabels();
        foreach (var message in Messages) message.RefreshLocalizedLabels();
        if (_lastFailure is { } failure) ShowFailure(failure);
    }
}
