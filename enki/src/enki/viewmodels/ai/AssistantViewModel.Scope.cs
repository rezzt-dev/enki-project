using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Ámbito de la conversación: vault o sesión fija. Solo los chats del vault pueden habilitar
/// acceso a sesiones sin protección; un chat de sesión nunca sale de su espacio. El ámbito se elige antes
/// del primer mensaje y se muestra siempre en la cabecera y bajo el compositor. Aquí vive también el permiso
/// de consulta del chat: si el asistente puede buscar y leer por su cuenta dentro de ese ámbito o solo ve lo
/// que se le adjunta.</summary>
public sealed partial class AssistantViewModel
{
    /// <summary>Ámbito elegido para el chat que se va a empezar.</summary>
    private string? _draftSessionId;
    private string? _draftSessionName;
    private bool _draftFullAccess;

    /// <summary>Permiso de consulta concedido desde la cabecera antes del primer mensaje; pasa al chat al crearlo.</summary>
    private AiReadGrant? _draftReadAccess;

    /// <summary>La sesión del ámbito tal como está ahora en el vault (se resuelve en <see cref="RefreshScope"/>
    /// y no en cada lectura: listar sesiones lee sus manifiestos del disco).</summary>
    private Session? _scopeSession;
    private readonly Dictionary<string, string> _scopeNames = new(StringComparer.Ordinal);

    private string? ScopeSessionId => _activeSession is not null ? _activeSession.SessionId : _draftSessionId;

    /// <summary>Ámbito con el que se enviará el siguiente turno.</summary>
    public AiAccessScope CurrentScope => new(ScopeSessionId, FullVaultAccess);

    public bool IsSessionChat => ScopeSessionId is not null;

    public bool FullVaultAccess
    {
        get => !IsSessionChat && (_activeSession?.FullVaultAccess ?? _draftFullAccess);
        set
        {
            if (value == FullVaultAccess || IsSending || IsSessionChat) return;
            if (_activeSession is { } session)
            {
                session.FullVaultAccess = value;
                TrySaveSession(session, touch: false);
            }
            else
            {
                _draftFullAccess = value;
            }
            RefreshScope();
        }
    }

    /// <summary>"Vault" o el nombre de la sesión; si la sesión ya no existe, el nombre que tenía.</summary>
    public string ScopeLabel => IsSessionChat
        ? _scopeSession?.Name ?? (_activeSession is not null ? _activeSession.SessionName : _draftSessionName) ?? string.Empty
        : LocalizationService.Instance["Assistant_Scope_Vault"];

    public MaterialIconKind ScopeIcon => IsSessionChat ? MaterialIconKind.SitemapOutline : MaterialIconKind.FolderOpenOutline;

    /// <summary>Distintivo junto al ámbito cuando el acceso está ampliado.</summary>
    public string? ScopeBadge => FullVaultAccess
        ? LocalizationService.Instance[IsSessionChat ? "Assistant_Scope_AutoBadge" : "Assistant_Scope_SessionsBadge"]
        : null;

    public bool HasScopeBadge => ScopeBadge is not null;

    public string FullAccessLabel => LocalizationService.Instance[IsSessionChat ? "Assistant_Scope_AutoMode" : "Assistant_Scope_AllowSessions"];

    public string FullAccessDescription =>
        LocalizationService.Instance[IsSessionChat ? "Assistant_Scope_AutoMode_Description" : "Assistant_Scope_AllowSessions_Description"];

    /// <summary>El espacio fija el ámbito por defecto de toda la conversación: solo se elige antes de empezarla.</summary>
    public bool CanChangeWorkspace => IsDraft && !HasMessages && !IsSending;

    public bool CanChangeAccess => !IsSending;

    /// <summary>El asistente puede actuar en el vault en este equipo (CLI compatible); si no, solo texto.</summary>
    public bool VaultToolsAvailable => Cli.SupportsVaultTools && _vaultTools is not null;

    /// <summary>Una línea bajo el compositor que resume, siempre visible, hasta dónde llega el asistente.</summary>
    public string ScopeSummary
    {
        get
        {
            if (!VaultToolsAvailable) return LocalizationService.Instance["Assistant_Scope_Summary_TextOnly"];
            var key = (IsSessionChat, FullVaultAccess) switch
            {
                (false, false) => "Assistant_Scope_Summary_Vault",
                (false, true) => "Assistant_Scope_Summary_VaultAndSessions",
                (true, false) => "Assistant_Scope_Summary_Session",
                (true, true) => "Assistant_Scope_Summary_Auto"
            };
            var summary = string.Format(LocalizationService.Instance.CurrentCulture, LocalizationService.Instance[key], ScopeLabel)
                + " · " + LocalizationService.Instance[ReadAccessGranted ? "Assistant_Scope_Summary_SearchAllowed" : "Assistant_Scope_Summary_SearchAsk"];
            return _settingsService.Settings.Ai.AllowVaultChanges
                ? summary
                : summary + " · " + LocalizationService.Instance["Assistant_Scope_Summary_ReadOnly"];
        }
    }

    /// <summary>Algo impide trabajar con el espacio del chat: la sesión ya no existe o está bloqueada.</summary>
    public string? ScopeWarning
    {
        get
        {
            if (!IsSessionChat || !VaultToolsAvailable) return null;
            if (_scopeSession is null)
                return string.Format(LocalizationService.Instance.CurrentCulture, LocalizationService.Instance["Assistant_Scope_Warning_Missing"], ScopeLabel);
            return _sessionService is not null && !_sessionService.IsUnlocked(_scopeSession)
                ? string.Format(LocalizationService.Instance.CurrentCulture, LocalizationService.Instance["Assistant_Scope_Warning_Locked"], ScopeLabel)
                : null;
        }
    }

    public bool HasScopeWarning => ScopeWarning is not null;

    // ----- Selector -----

    public ObservableCollection<AiScopeOptionViewModel> ScopeOptions { get; } = [];

    [ObservableProperty]
    private bool _isScopePickerOpen;

    [RelayCommand]
    private void ToggleScopePicker()
    {
        if (!IsScopePickerOpen) RebuildScopeOptions();
        IsScopePickerOpen = !IsScopePickerOpen;
    }

    /// <summary>Cambia el espacio del chat que se va a empezar. Lo escrito y la skill se conservan —el borrador
    /// pasa a ser el del espacio nuevo—, pero los adjuntos que queden fuera de él se retiran: adjuntar no puede
    /// ser una forma de saltarse el ámbito.</summary>
    [RelayCommand]
    private void PickScope(AiScopeOptionViewModel? option)
    {
        if (option is null || !option.IsEnabled || !CanChangeWorkspace) return;
        IsScopePickerOpen = false;
        if (option.Session?.Id == _draftSessionId) return;
        ClearNewChatDraft(NewChatDraftKey);
        _draftSessionId = option.Session?.Id;
        _draftSessionName = option.Session?.Name;
        // El acceso ampliado y el permiso de consulta significan cosas distintas en cada espacio: no se arrastran.
        _draftFullAccess = false;
        _draftReadAccess = null;
        RefreshScope();
        foreach (var chip in PendingAttachments.Where(c => c.FullPath is null || !IsPathInScope(c.FullPath)).ToList())
            PendingAttachments.Remove(chip);
        ScheduleDraftSave();
    }

    /// <summary>Abre un chat nuevo dentro de <paramref name="session"/> — "Preguntar al asistente" de Sesiones.</summary>
    public void StartChatInSession(Session session)
    {
        if (IsSending) return;
        SaveDraft();
        StartNewChat();
        _draftSessionId = session.Id;
        _draftSessionName = session.Name;
        RefreshScope();
        RestoreDraft();
        ComposerFocusRequested?.Invoke();
    }

    private void RebuildScopeOptions()
    {
        ScopeOptions.Clear();
        ScopeOptions.Add(new AiScopeOptionViewModel(null, isLocked: false) { IsSelected = !IsSessionChat });
        if (_sessionService is null) return;
        try
        {
            foreach (var session in _sessionService.List())
                ScopeOptions.Add(new AiScopeOptionViewModel(session, !_sessionService.IsUnlocked(session), ScopeNameFor(session.Id, session.Name))
                {
                    IsSelected = session.Id == ScopeSessionId
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning("No se pudieron listar las sesiones para el ámbito del asistente", ex);
        }
    }

    /// <summary>Vuelve a resolver la sesión del ámbito y a pintar todo lo que depende de él. Lo llaman el
    /// propio chat al cambiar de conversación o de ámbito, y el shell cuando cambian las sesiones.</summary>
    public void RefreshScope()
    {
        _scopeSession = null;
        _scopeNames.Clear();
        if (_sessionService is not null && _vaultService.IsVaultConfigured)
        {
            try
            {
                var sessions = _sessionService.List();
                foreach (var session in sessions) _scopeNames[session.Id] = session.Name;
                _scopeSession = sessions.FirstOrDefault(s => s.Id == ScopeSessionId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Warning("No se pudo resolver la sesión del chat del asistente", ex);
            }
        }

        OnPropertyChanged(nameof(IsSessionChat));
        OnPropertyChanged(nameof(FullVaultAccess));
        OnPropertyChanged(nameof(ScopeLabel));
        OnPropertyChanged(nameof(ScopeIcon));
        OnPropertyChanged(nameof(ScopeBadge));
        OnPropertyChanged(nameof(HasScopeBadge));
        OnPropertyChanged(nameof(FullAccessLabel));
        OnPropertyChanged(nameof(FullAccessDescription));
        OnPropertyChanged(nameof(CanChangeWorkspace));
        OnPropertyChanged(nameof(CanChangeAccess));
        OnPropertyChanged(nameof(VaultToolsAvailable));
        OnPropertyChanged(nameof(ScopeSummary));
        OnPropertyChanged(nameof(ScopeWarning));
        OnPropertyChanged(nameof(HasScopeWarning));
        RefreshReadAccessState();
        RefreshActiveNote();
        RebuildSessionList();
    }

    /// <summary>Si una nota cae dentro del ámbito del chat: la raíz del vault solo con un chat del vault; una sesión, si es la del chat o —sin contraseña— con acceso ampliado del vault,
    /// y siempre desbloqueada.
    /// Rige también qué notas se pueden adjuntar, para que adjuntar no sea una forma de saltarse el ámbito.</summary>
    private bool IsInScope(NoteFile note) => IsPathInScope(note.FullPath);

    /// <summary>Lo mismo para cualquier ruta del vault (archivos adjuntos, borradores restaurados). Nunca entra
    /// en las carpetas internas del vault (papelera, adjuntos de notas, configuración).</summary>
    private bool IsPathInScope(string fullPath)
    {
        if (!_vaultService.IsVaultConfigured || !VaultService.IsWithinFolder(fullPath, _vaultService.VaultPath)) return false;
        var relative = Path.GetRelativePath(_vaultService.VaultPath, fullPath).Replace('\\', '/');
        if (relative.Split('/').Any(segment => segment.StartsWith('.'))) return false;
        var scope = CurrentScope;
        var session = _sessionService?.List().FirstOrDefault(s => VaultService.IsWithinFolder(fullPath, s.RootPath));
        return session is null
            ? scope.IncludesVault && !_vaultService.IsPathInLockedSession(fullPath)
            : scope.Includes(session) && _sessionService!.IsUnlocked(session);
    }

    private AiScopeNotice BuildScopeNotice(AiChatSession session) => new(
        IsSessionChat ? ScopeLabel : null,
        SessionMissing: IsSessionChat && _scopeSession is null,
        FullVaultAccess,
        _settingsService.Settings.Ai.AllowVaultChanges,
        _isPolarLinked(),
        DateTimeOffset.Now,
        TimeZoneInfo.Local.Id,
        HasReadAccess(session));

    private void ResetDraftScope()
    {
        _draftSessionId = null;
        _draftSessionName = null;
        _draftFullAccess = false;
        _draftReadAccess = null;
    }

    // ===================== Permiso de consulta =====================

    /// <summary>El chat abierto (o el que se va a empezar) puede buscar y leer por su cuenta en su ámbito
    /// actual. Un permiso dado solo para la raíz no cubre el acceso a sesiones activado después.</summary>
    public bool ReadAccessGranted => _activeSession is { } session
        ? HasReadAccess(session)
        : _draftReadAccess is { } grant && Covers(grant, _draftSessionId is not null, _draftFullAccess);

    private static bool HasReadAccess(AiChatSession session) =>
        session.ReadAccess is { } grant && Covers(grant, session.SessionId is not null, session.FullVaultAccess);

    private static bool Covers(AiReadGrant grant, bool sessionChat, bool fullVaultAccess) =>
        sessionChat || !fullVaultAccess || grant.IncludesSessions;

    /// <summary>Qué se autoriza a consultar: el nombre de la sesión, el vault, o el vault con sus sesiones.</summary>
    private string ReadAccessTargetLabel(AiChatSession session) => session.SessionId is { } id
        ? ScopeNameFor(id, session.SessionName)
        : LocalizationService.Instance[session.FullVaultAccess ? "Assistant_ReadAccess_VaultAndSessions" : "Assistant_Scope_Vault"];

    /// <summary>Línea del menú de ámbito que dice qué puede hacer ahora el chat por su cuenta.</summary>
    public string ReadAccessStatus => string.Format(LocalizationService.Instance.CurrentCulture,
        LocalizationService.Instance[ReadAccessGranted ? "Assistant_ReadAccess_Status_Granted" : "Assistant_ReadAccess_Status_Ask"],
        _activeSession is { } session ? ReadAccessTargetLabel(session) : DraftReadAccessTargetLabel);

    private string DraftReadAccessTargetLabel => _draftSessionId is { } id
        ? ScopeNameFor(id, _draftSessionName)
        : LocalizationService.Instance[_draftFullAccess ? "Assistant_ReadAccess_VaultAndSessions" : "Assistant_Scope_Vault"];

    public bool CanChangeReadAccess => VaultToolsAvailable && !IsUnassignedChat;

    [RelayCommand]
    private void AllowReadAccess()
    {
        if (!CanChangeReadAccess || ReadAccessGranted) return;
        if (_activeSession is { } session) GrantReadAccess(session);
        else
        {
            _draftReadAccess = new AiReadGrant { IncludesSessions = _draftSessionId is null && _draftFullAccess };
            RefreshScope();
        }
    }

    /// <summary>Revocar impide las lecturas siguientes —también dentro de una respuesta en curso—, pero no borra
    /// lo que ya se envió al proveedor; el menú lo explica junto al botón.</summary>
    [RelayCommand]
    private void RevokeReadAccess()
    {
        if (_activeSession is { ReadAccess: not null } session)
        {
            session.ReadAccess = null;
            TrySaveSession(session, touch: false);
        }
        _draftReadAccess = null;
        RefreshScope();
    }

    private void GrantReadAccess(AiChatSession session)
    {
        session.ReadAccess = new AiReadGrant { IncludesSessions = session.SessionId is null && session.FullVaultAccess };
        TrySaveSession(session, touch: false);
        if (session == _activeSession) RefreshScope();
    }

    private void RefreshReadAccessState()
    {
        OnPropertyChanged(nameof(ReadAccessGranted));
        OnPropertyChanged(nameof(ReadAccessStatus));
        OnPropertyChanged(nameof(CanChangeReadAccess));
    }

    private void RefreshScopeLocalizedLabels()
    {
        RefreshReadAccessState();
        OnPropertyChanged(nameof(ScopeLabel));
        OnPropertyChanged(nameof(ScopeBadge));
        OnPropertyChanged(nameof(FullAccessLabel));
        OnPropertyChanged(nameof(FullAccessDescription));
        OnPropertyChanged(nameof(ScopeSummary));
        OnPropertyChanged(nameof(ScopeWarning));
        foreach (var option in ScopeOptions) option.RefreshLocalizedLabels();
    }
}

/// <summary>Una fila del selector de ámbito: el vault o una sesión.</summary>
public sealed partial class AiScopeOptionViewModel(Session? session, bool isLocked, string? displayName = null) : ObservableObject
{
    public Session? Session { get; } = session;

    public bool IsVault => Session is null;
    public bool IsLocked { get; } = isLocked;
    public bool IsProtected => Session?.IsEncrypted ?? false;

    /// <summary>Una sesión bloqueada no se puede elegir: su contenido no se puede leer hasta desbloquearla.</summary>
    public bool IsEnabled => !IsLocked;

    /// <summary>Nombre visible; con dos sesiones del mismo nombre lleva el principio de su identificador.</summary>
    public string Name => Session is null ? LocalizationService.Instance["Assistant_Scope_Vault"] : displayName ?? Session.Name;

    public string Detail => Session is null
        ? LocalizationService.Instance["Assistant_Scope_Vault_Detail"]
        : IsLocked
            ? LocalizationService.Instance["Assistant_Scope_Locked"]
            : Plural.Get("Assistant_Scope_Session_Detail", Session.NoteCount, Session.NoteCount.ToString(CultureInfo.CurrentCulture));

    public MaterialIconKind Icon => IsVault ? MaterialIconKind.FolderOpenOutline
        : IsLocked ? MaterialIconKind.LockOutline : MaterialIconKind.SitemapOutline;

    [ObservableProperty]
    private bool _isSelected;

    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Detail));
    }
}
