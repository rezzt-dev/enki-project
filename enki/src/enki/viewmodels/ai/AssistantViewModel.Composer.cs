using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Compositor del chat y ciclo de vida de un turno: texto, notas adjuntas, skill de partida,
/// confirmación de lo que se envía, streaming de la respuesta, cancelación, reintento y recuperación de una
/// conversación que el CLI ya no conserva.</summary>
public sealed partial class AssistantViewModel
{
    public const int MaxAttachments = 5;

    /// <summary>Historial que se reenvía, como mucho, cuando hay que reconstruir una conversación.</summary>
    private const int RecoveryHistoryChars = 60_000;

    private CancellationTokenSource? _turnCancellation;

    /// <summary>Último turno fallido, para "Reintentar" sin volver a escribir ni releer las notas.</summary>
    private (AiChatSession Session, string Prompt)? _retryTurn;

    private ClaudeTurnResult? _lastFailure;
    private ClaudeFailureKind _lastNotifiedFailure = ClaudeFailureKind.None;

    /// <summary>Contenido de las notas leído al pedir confirmación, para que se envíe exactamente lo que el
    /// usuario aprobó aunque la nota cambie mientras el diálogo está abierto.</summary>
    private List<AiContextNote>? _confirmedNotes;

    // ===================== Texto =====================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draftText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend), nameof(CanPickSkill), nameof(CanStartNewChat))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(RetryCommand))]
    private bool _isSending;

    public bool CanSend => Cli.IsReady && !Cli.IsChecking && !IsSending && !string.IsNullOrWhiteSpace(DraftText);

    public bool CanStartNewChat => !IsSending;

    /// <summary>Pide a la vista que lleve el foco al cuadro de texto (al abrir un chat nuevo o elegir una
    /// sugerencia), para poder escribir sin tocar el ratón.</summary>
    public event Action? ComposerFocusRequested;

    /// <summary>Sugerencias de la pantalla de bienvenida: rellenan el cuadro de texto sin enviarlo, para que
    /// el usuario pueda adjuntar una nota o ajustar la petición antes.</summary>
    [RelayCommand]
    private void UseSuggestion(string? resourceKey)
    {
        if (string.IsNullOrEmpty(resourceKey)) return;
        DraftText = LocalizationService.Instance[resourceKey];
        RefreshActiveNote();
        // Las sugerencias que trabajan sobre una nota (su clave termina en "Note") adjuntan la que esté
        // abierta en Documentos, si la hay y el usuario aún no ha adjuntado otra.
        if (CanAttachActiveNote && PendingAttachments.Count == 0 && resourceKey.EndsWith("Note", StringComparison.Ordinal))
            AttachActiveNote();
        ComposerFocusRequested?.Invoke();
    }

    // ===================== Notas adjuntas =====================

    public ObservableCollection<AiAttachmentChipViewModel> PendingAttachments { get; } = [];

    public bool HasPendingAttachments => PendingAttachments.Count > 0;
    public bool CanAttachMore => PendingAttachments.Count < MaxAttachments;

    private NoteFile? _activeNote;

    /// <summary>"Adjuntar «Título»" para la nota abierta en Documentos, si hay una y no está ya adjunta.</summary>
    public string ActiveNoteLabel => _activeNote is null
        ? string.Empty
        : string.Format(LocalizationService.Instance.CurrentCulture,
            LocalizationService.Instance["Assistant_Composer_AttachActiveNote"], _activeNote.Title);

    public bool CanAttachActiveNote => _activeNote is not null && CanAttachMore
        && PendingAttachments.All(a => !SamePath(a.Note, _activeNote)) && IsInScope(_activeNote);

    partial void OnIsSendingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanChangeWorkspace));
        OnPropertyChanged(nameof(CanChangeAccess));
        OnPropertyChanged(nameof(CanChangeEffort));
    }

    private void RefreshActiveNote()
    {
        _activeNote = _activeNoteProvider();
        OnPropertyChanged(nameof(ActiveNoteLabel));
        OnPropertyChanged(nameof(CanAttachActiveNote));
    }

    private void RefreshComposerState()
    {
        OnPropertyChanged(nameof(HasPendingAttachments));
        OnPropertyChanged(nameof(CanAttachMore));
        OnPropertyChanged(nameof(CanAttachActiveNote));
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AttachActiveNote()
    {
        RefreshActiveNote();
        if (_activeNote is { } note && CanAttachActiveNote) Attach(note, WorkspaceLabelFor(note));
    }

    /// <summary>Abre un chat nuevo con <paramref name="note"/> ya adjunta — "Preguntar al asistente sobre esta
    /// nota" de la paleta de comandos. El chat se abre en el espacio de la nota: si vive en una sesión, en esa
    /// sesión, para que el asistente pueda trabajar con ella y con lo que la rodea.</summary>
    public void StartChatAboutNote(NoteFile note)
    {
        if (IsSending) return;
        StartNewChat();
        if (_sessionService?.List().FirstOrDefault(s => VaultService.IsWithinFolder(note.FullPath, s.RootPath)) is { } session)
        {
            _draftSessionId = session.Id;
            _draftSessionName = session.Name;
            RefreshScope();
        }
        Attach(note, WorkspaceLabelFor(note));
        ComposerFocusRequested?.Invoke();
    }

    private void Attach(NoteFile note, string workspaceLabel)
    {
        if (!CanAttachMore || !IsInScope(note) || PendingAttachments.Any(a => SamePath(a.Note, note))) return;
        PendingAttachments.Add(new AiAttachmentChipViewModel(note, workspaceLabel));
    }

    /// <summary>Las notas pendientes de adjuntar son del vault que se acaba de cerrar: se descartan antes de
    /// que nadie pueda enviarlas creyendo que son del nuevo. Una respuesta en curso se corta, porque sus
    /// herramientas pasarían a actuar sobre el vault recién abierto. El historial no cambia, no depende del vault.</summary>
    public void CloseForVaultChange()
    {
        _turnCancellation?.Cancel();
        PendingAttachments.Clear();
        IsNotePickerOpen = false;
        IsSkillPickerOpen = false;
        IsConfirmSendOpen = false;
        IsScopePickerOpen = false;
        IsEffortPickerOpen = false;
        _confirmedNotes = null;
        _allNotesCache = [];
        if (_activeSession is null) ResetDraftScope();
        RefreshScope();
    }

    [RelayCommand]
    private void RemoveAttachment(AiAttachmentChipViewModel? chip)
    {
        if (chip is not null) PendingAttachments.Remove(chip);
    }

    private static bool SamePath(NoteFile? a, NoteFile? b) =>
        a is not null && b is not null && VaultService.PathsEqual(a.FullPath, b.FullPath);

    private string WorkspaceLabelFor(NoteFile note) =>
        _sessionService?.ListAllNotesByWorkspace().FirstOrDefault(t => SamePath(t.Note, note)).WorkspaceLabel
        ?? LocalizationService.Instance["Kanban_NotePicker_VaultWorkspaceLabel"];

    // ----- Selector de notas (mismo comportamiento que el de vincular nota de Kanban) -----

    private List<(NoteFile Note, string Workspace)> _allNotesCache = [];

    public ObservableCollection<NotePickerItemViewModel> NotePickerResults { get; } = [];

    [ObservableProperty]
    private bool _isNotePickerOpen;

    [ObservableProperty]
    private string _notePickerSearchText = string.Empty;

    public bool HasNoNotePickerResults => IsNotePickerOpen && NotePickerResults.Count == 0;

    [RelayCommand]
    private void ToggleNotePicker()
    {
        if (!IsNotePickerOpen)
        {
            _allNotesCache = CollectNotes();
            NotePickerSearchText = string.Empty;
            RefreshNotePickerResults();
        }
        IsNotePickerOpen = !IsNotePickerOpen;
        OnPropertyChanged(nameof(HasNoNotePickerResults));
    }

    /// <summary>Las notas que se pueden adjuntar: las de los espacios del ámbito del chat (ver
    /// <see cref="IsInScope"/>), recorridas espacio a espacio.</summary>
    private List<(NoteFile, string)> CollectNotes()
    {
        try
        {
            var scope = CurrentScope;
            var notes = new List<(NoteFile, string)>();
            if (scope.IncludesVault)
                notes.AddRange(_vaultService.GetAllNotesRecursive(_vaultService.VaultPath)
                    .Select(n => (n, LocalizationService.Instance["Kanban_NotePicker_VaultWorkspaceLabel"])));
            if (_sessionService is not null)
                foreach (var session in _sessionService.List().Where(s => scope.Includes(s) && _sessionService.IsUnlocked(s)))
                    notes.AddRange(_vaultService.GetAllNotesRecursive(session.RootPath).Select(n => (n, session.Name)));
            return notes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultUnavailableException)
        {
            LogService.Warning("No se pudieron listar las notas para el asistente", ex);
            return [];
        }
    }

    partial void OnNotePickerSearchTextChanged(string value) => RefreshNotePickerResults();

    private void RefreshNotePickerResults()
    {
        NotePickerResults.Clear();
        var query = NotePickerSearchText.Trim();
        var matches = query.Length == 0
            ? _allNotesCache
            : _allNotesCache.Where(t => t.Note.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        foreach (var (note, workspace) in matches.Take(40))
            NotePickerResults.Add(new NotePickerItemViewModel(note, workspace));
        OnPropertyChanged(nameof(HasNoNotePickerResults));
    }

    [RelayCommand]
    private void PickNote(NotePickerItemViewModel? item)
    {
        if (item is not null) Attach(item.Note, item.WorkspaceLabel);
        IsNotePickerOpen = false;
    }

    // ===================== Skill de partida =====================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveSkillName), nameof(HasActiveSkill))]
    private string? _draftSkillName;

    private string? DraftSkillInstructions { get; set; }

    public ObservableCollection<SkillFile> SkillOptions { get; } = [];

    [ObservableProperty]
    private bool _isSkillPickerOpen;

    public bool HasNoSkills => IsSkillPickerOpen && SkillOptions.Count == 0;

    /// <summary>La skill fija el prompt de sistema de toda la conversación, así que solo se elige antes del
    /// primer mensaje.</summary>
    public bool CanPickSkill => IsDraft && !HasMessages && !IsSending;

    [RelayCommand]
    private void ToggleSkillPicker()
    {
        if (!IsSkillPickerOpen)
        {
            SkillOptions.Clear();
            try
            {
                var service = new SkillService(_vaultService);
                if (service.IsInitialized)
                    foreach (var skill in service.LoadSkills().OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
                        SkillOptions.Add(skill);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Warning("No se pudieron leer las skills para el asistente", ex);
            }
        }
        IsSkillPickerOpen = !IsSkillPickerOpen;
        OnPropertyChanged(nameof(HasNoSkills));
    }

    [RelayCommand]
    private void PickSkill(SkillFile? skill)
    {
        IsSkillPickerOpen = false;
        if (skill is null || !CanPickSkill) return;
        DraftSkillName = skill.Name;
        DraftSkillInstructions = SkillService.GetMarkdownBody(skill.Content);
        RefreshHeader();
    }

    [RelayCommand]
    private void ClearSkill()
    {
        if (!CanPickSkill) return;
        DraftSkillName = null;
        DraftSkillInstructions = null;
        RefreshHeader();
    }

    /// <summary>"Usar en el asistente" desde el detalle de una skill.</summary>
    public void StartChatWithSkill(SkillFile skill)
    {
        if (IsSending) return;
        StartNewChat();
        DraftSkillName = skill.Name;
        DraftSkillInstructions = SkillService.GetMarkdownBody(skill.Content);
        RefreshHeader();
        ComposerFocusRequested?.Invoke();
    }

    // ===================== Confirmación de lo que se envía =====================

    [ObservableProperty]
    private bool _isConfirmSendOpen;

    /// <summary>Filas del diálogo: título de cada nota y cuánto se enviará de ella.</summary>
    public ObservableCollection<AiAttachmentChipViewModel> ConfirmSendItems { get; } = [];

    [ObservableProperty]
    private string _confirmSendSummary = string.Empty;

    /// <summary>"No volver a preguntar" del diálogo.</summary>
    [ObservableProperty]
    private bool _skipFutureConfirmations;

    [RelayCommand]
    private async Task ConfirmSendAsync()
    {
        IsConfirmSendOpen = false;
        if (SkipFutureConfirmations)
        {
            _settingsService.Settings.Ai.ConfirmBeforeSendingContext = false;
            TrySaveSettings();
        }
        await SendCoreAsync();
    }

    [RelayCommand]
    private void CancelSend()
    {
        IsConfirmSendOpen = false;
        _confirmedNotes = null;
    }

    private bool PrepareConfirmation()
    {
        if (!TryReadAttachments(out var notes)) return false;
        var (_, attachments) = AiPromptBuilder.BuildUserTurn(DraftText, notes, _settingsService.Settings.Ai.MaxContextChars);
        _confirmedNotes = notes;
        ConfirmSendItems.Clear();
        foreach (var attachment in attachments) ConfirmSendItems.Add(new AiAttachmentChipViewModel(attachment));
        var total = attachments.Sum(a => a.SentChars) + DraftText.Trim().Length;
        ConfirmSendSummary = string.Format(LocalizationService.Instance.CurrentCulture,
            LocalizationService.Instance["Assistant_Confirm_Summary"], AiAttachmentChipViewModel.FormatCount(total));
        SkipFutureConfirmations = false;
        return true;
    }

    // ===================== Envío =====================

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (!CanSend) return;
        if (PendingAttachments.Count > 0 && _settingsService.Settings.Ai.ConfirmBeforeSendingContext)
        {
            if (PrepareConfirmation()) IsConfirmSendOpen = true;
            return;
        }
        await SendCoreAsync();
    }

    private async Task SendCoreAsync()
    {
        if (!CanSend) return;
        var notes = _confirmedNotes;
        _confirmedNotes = null;
        if (notes is null && !TryReadAttachments(out notes)) return;

        var text = DraftText.Trim();
        var (prompt, attachments) = AiPromptBuilder.BuildUserTurn(text, notes, _settingsService.Settings.Ai.MaxContextChars);

        var session = _activeSession;
        if (session is null)
        {
            session = new AiChatSession
            {
                Provider = _cli.Provider,
                Title = AiPromptBuilder.SuggestTitle(text),
                Model = SelectedModel?.Alias ?? string.Empty,
                Effort = SelectedEffort,
                SkillName = DraftSkillName,
                SkillInstructions = DraftSkillInstructions,
                SessionId = _draftSessionId,
                SessionName = _scopeSession?.Name ?? _draftSessionName,
                FullVaultAccess = _draftFullAccess
            };
            _activeSession = session;
            ResetDraftScope();
            RefreshScope();
        }

        var userMessage = new AiChatMessage { Role = AiChatRole.User, Text = text, Attachments = attachments };
        session.Messages.Add(userMessage);
        TrySaveSession(session);
        Messages.Add(new AiChatMessageViewModel(userMessage));
        DraftText = string.Empty;
        PendingAttachments.Clear();
        RememberActiveChat();
        RefreshHeader();
        RebuildSessionList();

        await RunTurnAsync(session, prompt);
    }

    /// <summary>Lee las notas pendientes de adjuntar. Antes vuelca lo que el editor tenga sin guardar, para
    /// que se envíe lo que el usuario ve y no la última versión en disco.</summary>
    private bool TryReadAttachments(out List<AiContextNote> notes)
    {
        notes = [];
        if (PendingAttachments.Count == 0) return true;
        _flushPendingEdits();
        try
        {
            foreach (var chip in PendingAttachments)
            {
                var note = chip.Note!;
                if (!IsInScope(note)) throw new SessionLockedException(note.FullPath);
                var relative = Path.GetRelativePath(_vaultService.VaultPath, note.FullPath).Replace('\\', '/');
                notes.Add(new AiContextNote(note.Title, relative, _vaultService.ReadContent(note)));
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException or VaultUnavailableException)
        {
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Notify_AttachmentFailed_Title"),
                VaultFailureReasons.DescribeLocalized(ex), NotificationSeverity.Error);
            notes = [];
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsSending))]
    private void Stop() => _turnCancellation?.Cancel();

    public bool CanRetry => _retryTurn is { } retry && retry.Session == _activeSession && !IsSending;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync()
    {
        if (_retryTurn is not { } retry || retry.Session != _activeSession || IsSending) return;
        await RunTurnAsync(retry.Session, retry.Prompt);
    }

    private async Task RunTurnAsync(AiChatSession session, string prompt)
    {
        ClearError();
        var assistant = new AiChatMessage { Role = AiChatRole.Assistant };
        var bubble = new AiChatMessageViewModel(assistant) { IsStreaming = true };
        Messages.Add(bubble);
        ScrollToEndRequested?.Invoke();

        IsSending = true;
        using var cancellation = new CancellationTokenSource();
        _turnCancellation = cancellation;

        // Con herramientas el asistente lee y escribe el disco: primero se vuelca lo que el editor tenga sin
        // guardar, para que trabaje sobre lo que el usuario ve y no pise un cambio pendiente.
        var vaultTools = VaultToolsAvailable;
        var activitySignal = new CliActivitySignal();
        ClaudeToolAccess? toolAccess = null;

        // Los fragmentos llegan por el hilo de fondo del proceso, a veces decenas por segundo: se acumulan
        // aquí y se pinta como mucho una vez por vuelta del hilo de interfaz, no una por fragmento.
        var buffer = new StringBuilder();
        var renderPending = 0;
        void OnEvent(ClaudeStreamEvent streamEvent)
        {
            if (streamEvent is not ClaudeTextDelta delta) return;
            lock (buffer) buffer.Append(delta.Text);
            if (Interlocked.Exchange(ref renderPending, 1) == 1) return;
            _dispatch(() =>
            {
                Interlocked.Exchange(ref renderPending, 0);
                if (!bubble.IsStreaming) return;
                lock (buffer) bubble.Text = buffer.ToString();
                ScrollToEndRequested?.Invoke();
            });
        }

        var systemPrompt = AiPromptBuilder.BuildSystemPrompt(LocalizationService.Instance.CurrentCulture.EnglishName,
            session.SkillName, session.SkillInstructions, vaultTools: vaultTools);
        var model = string.IsNullOrWhiteSpace(session.Model) ? null : session.Model;
        ClaudeTurnResult? result = null;
        try
        {
            if (vaultTools) _flushPendingEdits();
            toolAccess = vaultTools ? RegisterTurnTools(session, bubble, activitySignal, cancellation.Token) : null;
            var turnPrompt = toolAccess is null ? prompt : AiPromptBuilder.WithScope(BuildScopeNotice(), prompt);
            if (session.Provider != AiProvider.Claude)
            {
                var lastUser = session.Messages.FindLastIndex(m => m.Role == AiChatRole.User);
                // En un reintento se incluyen también las acciones de la respuesta interrumpida:
                // ya ocurrieron en el vault aunque el modelo no conserve una sesión remota.
                turnPrompt = AiPromptBuilder.BuildRecoveryPrompt(session.Messages.Where((_, index) => index != lastUser).ToList(),
                    turnPrompt, RecoveryHistoryChars);
            }
            result = await _cli.RunTurnAsync(new ClaudeTurnRequest(turnPrompt, systemPrompt, session.CliSessionId, model,
                toolAccess, activitySignal, session.Effort), OnEvent, cancellation.Token);
            if (result.Failure == ClaudeFailureKind.SessionNotFound && session.CliSessionId is not null)
            {
                // El CLI ya no tiene esta conversación (se borró su historial, o el chat viene de otra
                // instalación): se abre una nueva con lo que Enki guardó, sin que el usuario tenga que hacer nada.
                LogService.Debug("[ia] la sesión del CLI no existe; se reconstruye desde el historial de Enki");
                session.CliSessionId = null;
                lock (buffer) buffer.Clear();
                var lastUser = session.Messages.FindLastIndex(m => m.Role == AiChatRole.User);
                var history = session.Messages.Take(Math.Max(0, lastUser)).ToList();
                var recovery = AiPromptBuilder.BuildRecoveryPrompt(history, turnPrompt, RecoveryHistoryChars);
                result = await _cli.RunTurnAsync(new ClaudeTurnRequest(recovery, systemPrompt, null, model, toolAccess, activitySignal, session.Effort),
                    OnEvent, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            result = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            LogService.Warning("No se pudo preparar el turno del asistente", ex);
            result = new ClaudeTurnResult(string.Empty, null, ClaudeFailureKind.Failed);
        }
        finally
        {
            // El token del turno deja de valer aquí: un CLI que siguiera vivo ya no puede tocar nada.
            if (toolAccess is not null) _vaultTools!.Unregister(toolAccess.BearerToken);
            bubble.PendingApproval?.Cancel();
            bubble.PendingApproval = null;
            _turnCancellation = null;
            bubble.IsStreaming = false;
            IsSending = false;
        }

        string partial;
        lock (buffer) partial = buffer.ToString();

        if (result is { Succeeded: true })
        {
            bubble.Text = result.Text.Length > 0 ? result.Text : partial;
            // Una respuesta que solo actuó (sin texto) también es una respuesta: sus acciones son lo que cuenta.
            if (bubble.HasText || bubble.HasActivities)
            {
                session.CliSessionId = result.SessionId ?? session.CliSessionId;
                session.Messages.Add(assistant);
                _retryTurn = null;
                _lastNotifiedFailure = ClaudeFailureKind.None;
                TrySaveSession(session);
                FinishTurn();
                return;
            }
            result = new ClaudeTurnResult(string.Empty, result.SessionId, ClaudeFailureKind.Failed,
                LocalizationService.Instance["Assistant_Error_EmptyResponse"]);
        }

        // Cancelado o fallido: lo que llegara —texto o acciones ya hechas en el vault— se conserva marcado como
        // interrumpido; si no llegó nada, la burbuja vacía se retira.
        if (partial.Length > 0 || bubble.HasActivities)
        {
            bubble.Text = partial;
            bubble.IsInterrupted = true;
            session.Messages.Add(assistant);
            if (result is { SessionId: { } sessionId }) session.CliSessionId = sessionId;
            TrySaveSession(session);
        }
        else
        {
            Messages.Remove(bubble);
        }

        if (result is not null)
        {
            _retryTurn = (session, prompt);
            ShowFailure(result);
            NotifyFailure(result);
            if (result.Failure is ClaudeFailureKind.SignedOut or ClaudeFailureKind.NotInstalled) _ = Cli.RefreshAsync();
        }
        FinishTurn();
    }

    /// <summary>Da de alta el turno en el servidor del vault con el ámbito del chat. Las acciones y las
    /// confirmaciones llegan desde el hilo en que se ejecutan las herramientas y se pintan en la burbuja.</summary>
    private ClaudeToolAccess RegisterTurnTools(AiChatSession session, AiChatMessageViewModel bubble, CliActivitySignal activity,
        CancellationToken cancellation) =>
        _vaultTools!.Register(new AiToolContext
        {
            Scope = new AiAccessScope(session.SessionId, session.FullVaultAccess),
            ActivityReported = reported => _dispatch(() =>
            {
                bubble.ReportActivity(reported);
                ScrollToEndRequested?.Invoke();
            }),
            ApprovalRequested = (request, token) => RequestApprovalAsync(bubble, request, token),
            Activity = activity,
            Cancellation = cancellation
        });

    /// <summary>Enseña la tarjeta de confirmación en la respuesta en curso y espera a que el usuario decida. Si
    /// no está mirando el Asistente, se le avisa: el turno queda parado hasta entonces.</summary>
    private async Task<bool> RequestApprovalAsync(AiChatMessageViewModel bubble, AiApprovalRequest request, CancellationToken cancellation)
    {
        var approval = new AiApprovalViewModel(request);
        _dispatch(() =>
        {
            bubble.PendingApproval = approval;
            ScrollToEndRequested?.Invoke();
            if (!_isVisible())
                _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Notify_ApprovalPending_Title"),
                    request.Title, NotificationSeverity.Warning);
        });
        using (cancellation.Register(approval.Cancel))
        {
            try
            {
                return await approval.Decision;
            }
            finally
            {
                _dispatch(() =>
                {
                    if (bubble.PendingApproval == approval) bubble.PendingApproval = null;
                });
            }
        }
    }

    private void FinishTurn()
    {
        OnPropertyChanged(nameof(CanRetry));
        RetryCommand.NotifyCanExecuteChanged();
        RebuildSessionList();
        ScrollToEndRequested?.Invoke();
    }

    // ===================== Errores =====================

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _errorShowsLoginHelp;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    private void ClearError()
    {
        _lastFailure = null;
        ErrorMessage = null;
        ErrorShowsLoginHelp = false;
        OnPropertyChanged(nameof(CanRetry));
        RetryCommand.NotifyCanExecuteChanged();
    }

    private void ShowFailure(ClaudeTurnResult result)
    {
        _lastFailure = result;
        ErrorMessage = LocalizedTextFormatter.Resolve(DescribeFailure(result));
        ErrorShowsLoginHelp = result.Failure == ClaudeFailureKind.SignedOut;
        OnPropertyChanged(nameof(CanRetry));
    }

    /// <summary>Un mismo tipo de fallo se notifica una vez hasta que vuelva a haber un turno correcto: si se
    /// agota el límite de uso, cada reintento no debe sumar otro aviso a la campana.</summary>
    private void NotifyFailure(ClaudeTurnResult result)
    {
        if (result.Failure == _lastNotifiedFailure) return;
        _lastNotifiedFailure = result.Failure;
        _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Notify_TurnFailed_Title"),
            DescribeFailure(result), result.Failure == ClaudeFailureKind.RateLimited
                ? NotificationSeverity.Warning : NotificationSeverity.Error);
    }

    private LocalizedText DescribeFailure(ClaudeTurnResult result) => result.Failure switch
    {
        ClaudeFailureKind.NotInstalled => LocalizedTextFormatter.Format(_cli.Provider == AiProvider.Claude ? "Assistant_Error_NotInstalled" : "Ai_NotInstalled"),
        ClaudeFailureKind.SignedOut => LocalizedTextFormatter.Format("Assistant_Error_SignedOut", Cli.LoginCommand),
        ClaudeFailureKind.RateLimited when result.RateLimitResetsAt is { } resetsAt =>
            LocalizedTextFormatter.Format("Assistant_Error_RateLimitedUntil",
                RelativeTimeFormatter.FormatAbsoluteShort(resetsAt.UtcDateTime)),
        ClaudeFailureKind.RateLimited => LocalizedTextFormatter.Format("Assistant_Error_RateLimited"),
        ClaudeFailureKind.ModelUnavailable => LocalizedTextFormatter.Format("Assistant_Error_ModelUnavailable",
            SelectedModel?.Label ?? string.Empty),
        ClaudeFailureKind.Overloaded => LocalizedTextFormatter.Format("Assistant_Error_Overloaded"),
        ClaudeFailureKind.Timeout => LocalizedTextFormatter.Format("Assistant_Error_Timeout",
            _settingsService.Settings.Ai.IdleTimeoutSeconds),
        _ => LocalizedTextFormatter.Format("Assistant_Error_Failed",
            result.FailureDetail ?? LocalizationService.Instance["Common_Error_Unexpected"])
    };
}
