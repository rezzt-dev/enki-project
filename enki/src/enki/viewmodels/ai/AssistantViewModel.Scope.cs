using System;
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

/// <summary>Ámbito del chat (ver <see cref="AiAccessScope"/>): en qué espacio se abrió —el vault, por
/// defecto, o una sesión— y si tiene acceso ampliado ("acceso a las sesiones" en un chat del vault, "modo
/// auto" en uno de sesión). El espacio se elige antes del primer mensaje; el acceso ampliado se puede cambiar
/// en cualquier momento y vale desde el turno siguiente. Lo que se ve en la cabecera y bajo el compositor
/// dice siempre, en claro, hasta dónde llega el asistente.</summary>
public sealed partial class AssistantViewModel
{
    /// <summary>Ámbito elegido para el chat que se va a empezar.</summary>
    private string? _draftSessionId;
    private string? _draftSessionName;
    private bool _draftFullAccess;

    /// <summary>La sesión del ámbito tal como está ahora en el vault (se resuelve en <see cref="RefreshScope"/>
    /// y no en cada lectura: listar sesiones lee sus manifiestos del disco).</summary>
    private Session? _scopeSession;

    private string? ScopeSessionId => _activeSession is not null ? _activeSession.SessionId : _draftSessionId;

    /// <summary>Ámbito con el que se enviará el siguiente turno.</summary>
    public AiAccessScope CurrentScope => new(ScopeSessionId, FullVaultAccess);

    public bool IsSessionChat => ScopeSessionId is not null;

    public bool FullVaultAccess
    {
        get => _activeSession?.FullVaultAccess ?? _draftFullAccess;
        set
        {
            if (value == FullVaultAccess || IsSending) return;
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
            var summary = string.Format(LocalizationService.Instance.CurrentCulture, LocalizationService.Instance[key], ScopeLabel);
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

    [RelayCommand]
    private void PickScope(AiScopeOptionViewModel? option)
    {
        if (option is null || !option.IsEnabled || !CanChangeWorkspace) return;
        _draftSessionId = option.Session?.Id;
        _draftSessionName = option.Session?.Name;
        // El acceso ampliado significa cosas distintas en cada clase de chat: no se arrastra de una a otra.
        _draftFullAccess = false;
        PendingAttachments.Clear();
        IsScopePickerOpen = false;
        RefreshScope();
    }

    /// <summary>Abre un chat nuevo dentro de <paramref name="session"/> — "Preguntar al asistente" de Sesiones.</summary>
    public void StartChatInSession(Session session)
    {
        if (IsSending) return;
        StartNewChat();
        _draftSessionId = session.Id;
        _draftSessionName = session.Name;
        RefreshScope();
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
                ScopeOptions.Add(new AiScopeOptionViewModel(session, !_sessionService.IsUnlocked(session))
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
        if (ScopeSessionId is { } id && _sessionService is not null && _vaultService.IsVaultConfigured)
        {
            try
            {
                _scopeSession = _sessionService.List().FirstOrDefault(s => s.Id == id);
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
        RefreshActiveNote();
    }

    /// <summary>Si una nota cae dentro del ámbito del chat: la raíz del vault solo con un chat del vault o en
    /// modo auto; una sesión, si es la del chat o —sin contraseña— con acceso ampliado, y siempre desbloqueada.
    /// Rige también qué notas se pueden adjuntar, para que adjuntar no sea una forma de saltarse el ámbito.</summary>
    private bool IsInScope(NoteFile note)
    {
        var scope = CurrentScope;
        var session = _sessionService?.List().FirstOrDefault(s => VaultService.IsWithinFolder(note.FullPath, s.RootPath));
        return session is null
            ? scope.IncludesVault && VaultService.IsWithinFolder(note.FullPath, _vaultService.VaultPath)
            : scope.Includes(session) && _sessionService!.IsUnlocked(session);
    }

    private AiScopeNotice BuildScopeNotice() => new(
        IsSessionChat ? ScopeLabel : null,
        SessionMissing: IsSessionChat && _scopeSession is null,
        FullVaultAccess,
        _settingsService.Settings.Ai.AllowVaultChanges,
        _isPolarLinked(),
        DateTimeOffset.Now,
        TimeZoneInfo.Local.Id);

    private void ResetDraftScope()
    {
        _draftSessionId = null;
        _draftSessionName = null;
        _draftFullAccess = false;
    }

    private void RefreshScopeLocalizedLabels()
    {
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
public sealed partial class AiScopeOptionViewModel(Session? session, bool isLocked) : ObservableObject
{
    public Session? Session { get; } = session;

    public bool IsVault => Session is null;
    public bool IsLocked { get; } = isLocked;
    public bool IsProtected => Session?.IsEncrypted ?? false;

    /// <summary>Una sesión bloqueada no se puede elegir: su contenido no se puede leer hasta desbloquearla.</summary>
    public bool IsEnabled => !IsLocked;

    public string Name => Session?.Name ?? LocalizationService.Instance["Assistant_Scope_Vault"];

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
