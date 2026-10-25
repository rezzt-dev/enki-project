using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Historial separado por vault. Cada chat lleva la identidad del vault en el que se creó
/// (<see cref="AiChatSession.VaultId"/>) y solo se enseñan los del vault abierto. Las conversaciones guardadas
/// antes de esta separación no tienen identidad: se listan aparte, se pueden leer, y para continuarlas el
/// usuario las asocia de forma expresa al vault abierto. Nunca se asignan solas al que esté abierto por
/// casualidad ni se borran.</summary>
public sealed partial class AssistantViewModel
{
    /// <summary>Identidad del vault abierto, si ya la tiene. Consultarla no la crea: el asistente existe aunque la
    /// IA esté apagada, y no debe escribir en el vault hasta que haya un chat o un borrador que guardar
    /// (<see cref="EnsureVaultIdentity"/>).</summary>
    private string? _vaultId;

    /// <summary>Identidad de reserva derivada de la ruta del vault (ver <see cref="VaultSettingsService.PathIdentity"/>).
    /// Los chats guardados con ella —un vault que no se pudo escribir— siguen siendo de este vault.</summary>
    private string _vaultPathId = string.Empty;

    private string _vaultName = string.Empty;

    /// <summary>Clave del vault para lo que se recuerda por vault (último chat, borradores nuevos).</summary>
    private string VaultKey => _vaultId ?? _vaultPathId;

    private void RefreshVaultIdentity()
    {
        _vaultId = _vaultIdentity(false);
        _vaultPathId = VaultSettingsService.PathIdentity(_vaultService.VaultPath);
        _vaultName = _vaultService.IsVaultConfigured ? Path.GetFileName(_vaultService.VaultPath.TrimEnd('/', '\\')) : string.Empty;
    }

    /// <summary>Crea la identidad del vault si aún no la tiene, justo antes de guardar algo que dependa de ella.</summary>
    private void EnsureVaultIdentity() => _vaultId ??= _vaultIdentity(true) ?? _vaultPathId;

    private bool BelongsToVault(AiChatSession session) =>
        session.VaultId is { } id && (id == _vaultId || id == _vaultPathId);

    /// <summary>La conversación abierta es de antes de separar el historial por vault: se lee, pero no se
    /// continúa hasta asociarla.</summary>
    public bool IsUnassignedChat => _activeSession is { VaultId: null };

    [RelayCommand]
    private void AssignActiveChatToVault()
    {
        if (_activeSession is { } session) AssignToVault(session);
    }

    [RelayCommand]
    private void AssignSessionToVault(AiChatSessionItemViewModel? item)
    {
        if (item is { IsUnassigned: true }) AssignToVault(item.Session);
    }

    private void AssignToVault(AiChatSession session)
    {
        if (session.VaultId is not null || IsSending) return;
        EnsureVaultIdentity();
        try
        {
            _chats.AssignToVault(session, VaultKey, _vaultName);
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Notify_ChatAssigned_Title"),
                LocalizedTextFormatter.Format("Assistant_Notify_ChatAssigned_Message", session.Title, _vaultName),
                NotificationSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Notify_SaveFailed_Title"),
                VaultFailureReasons.DescribeLocalized(ex), NotificationSeverity.Error);
            return;
        }
        if (session == _activeSession)
        {
            RememberActiveChat();
            RefreshHeader();
            RefreshScope();
        }
    }

    /// <summary>Nombre visible de una sesión en el historial y en el selector de ámbito. Dos sesiones pueden
    /// llamarse igual: entonces se añade el principio de su identificador, para que no se confundan.</summary>
    private string ScopeNameFor(string sessionId, string? savedName)
    {
        var name = _scopeNames.GetValueOrDefault(sessionId, savedName ?? sessionId);
        var duplicated = _scopeNames.Any(pair => pair.Key != sessionId
            && string.Equals(pair.Value, name, StringComparison.OrdinalIgnoreCase));
        return duplicated ? $"{name} · {sessionId[..Math.Min(6, sessionId.Length)]}" : name;
    }
}
