using System;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Borradores del compositor. Cada conversación conserva lo que se estaba escribiendo en ella
/// (<see cref="AiChatSession.Draft"/>) y cada vault y ámbito, el de su chat nuevo
/// (<see cref="AiChatService.FindNewChatDraft"/>): texto, adjuntos —como referencias—, skill y capacidad. Se
/// guardan al cambiar de chat, de vault o de superficie, al cerrar Enki y, mientras se escribe, poco después de
/// la última pulsación. Al restaurarlos se vuelve a comprobar cada adjunto: uno borrado, bloqueado o fuera del
/// ámbito se retira y el compositor lo dice.</summary>
public sealed partial class AssistantViewModel
{
    /// <summary>Lo que se espera desde la última edición antes de guardar el borrador.</summary>
    private static readonly TimeSpan DraftSaveDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>Mientras se carga o se vacía un borrador, los cambios del compositor no son ediciones del usuario.</summary>
    private bool _restoringDraft;

    private DispatcherTimer? _draftSaveTimer;

    /// <summary>Adjuntos de un borrador restaurado que ya no se pudieron recuperar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraftNotice), nameof(HasDraftNotice))]
    private int _draftMissingAttachments;

    public string? DraftNotice => DraftMissingAttachments > 0
        ? Plural.Get("Assistant_Draft_MissingAttachments", DraftMissingAttachments, DraftMissingAttachments)
        : null;

    public bool HasDraftNotice => DraftMissingAttachments > 0;

    [RelayCommand]
    private void DismissDraftNotice() => DraftMissingAttachments = 0;

    /// <summary>Clave del borrador del chat nuevo en el vault y el espacio elegidos.</summary>
    private string NewChatDraftKey => VaultKey + "|" + (_draftSessionId ?? AiWorkspace.VaultId);

    private void ScheduleDraftSave()
    {
        if (_restoringDraft) return;
        // Cada pulsación vuelve a empezar la espera, como el autoguardado del editor.
        if (_draftSaveTimer is null)
        {
            _draftSaveTimer = new DispatcherTimer { Interval = DraftSaveDelay };
            _draftSaveTimer.Tick += (_, _) => SaveDraft();
        }
        _draftSaveTimer.Stop();
        _draftSaveTimer.Start();
    }

    private void CancelScheduledDraftSave() => _draftSaveTimer?.Stop();

    /// <summary>Guarda ya el borrador de la conversación abierta (o del chat nuevo). Lo llama el shell al salir de
    /// la superficie del asistente y al cerrar Enki; un fallo de disco se registra sin interrumpir nada.</summary>
    public void SaveDraft()
    {
        CancelScheduledDraftSave();
        if (_restoringDraft || IsUnassignedChat) return;
        var draft = CaptureDraft();
        if (_activeSession is { } session)
        {
            if (SameDraft(session.Draft, draft)) return;
            session.Draft = draft.IsEmpty ? null : draft;
            TrySaveSession(session, touch: false);
            return;
        }
        try
        {
            if (SameDraft(_chats.FindNewChatDraft(NewChatDraftKey), draft)) return;
            // Un borrador vacío no se guarda: la identidad del vault solo se crea cuando hay algo que guardar.
            if (!draft.IsEmpty)
            {
                var previousKey = NewChatDraftKey;
                EnsureVaultIdentity();
                if (previousKey != NewChatDraftKey) _chats.SaveNewChatDraft(previousKey, null);
            }
            _chats.SaveNewChatDraft(NewChatDraftKey, draft);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            LogService.Warning("No se pudo guardar el borrador del asistente", ex);
        }
    }

    private AiChatDraft CaptureDraft() => new()
    {
        Text = DraftText,
        Attachments = PendingAttachments.Where(chip => chip.FullPath is not null)
            .Select(chip => new AiDraftAttachment
            {
                Kind = chip.Kind,
                RelativePath = Path.GetRelativePath(_vaultService.VaultPath, chip.FullPath!).Replace('\\', '/')
            }).ToList(),
        // La skill y la capacidad de un chat empezado ya viven en el propio chat.
        SkillName = _activeSession is null ? DraftSkillName : null,
        SkillInstructions = _activeSession is null ? DraftSkillInstructions : null,
        Effort = _activeSession is null && SelectedEffort != "medium" ? SelectedEffort : null
    };

    private static bool SameDraft(AiChatDraft? saved, AiChatDraft current) =>
        saved is null
            ? current.IsEmpty
            : saved.Text == current.Text && saved.SkillName == current.SkillName && saved.Effort == current.Effort
              && saved.Attachments.Select(a => (a.Kind, a.RelativePath)).SequenceEqual(current.Attachments.Select(a => (a.Kind, a.RelativePath)));

    /// <summary>Carga en el compositor el borrador de la conversación abierta o del chat nuevo del ámbito.</summary>
    private void RestoreDraft()
    {
        CancelScheduledDraftSave();
        var draft = _activeSession is { } session ? session.Draft : _chats.FindNewChatDraft(NewChatDraftKey);
        _restoringDraft = true;
        try
        {
            DraftText = draft?.Text ?? string.Empty;
            PendingAttachments.Clear();
            var missing = 0;
            foreach (var attachment in (draft?.Attachments ?? []).Take(MaxAttachments))
            {
                if (ResolveDraftAttachment(attachment) is { } chip) PendingAttachments.Add(chip);
                else missing++;
            }
            DraftMissingAttachments = missing;
            if (_activeSession is null && draft is not null)
            {
                if (draft.SkillName is not null)
                {
                    DraftSkillName = draft.SkillName;
                    DraftSkillInstructions = draft.SkillInstructions;
                }
                if (draft.Effort is not null) RestoreEffort(draft.Effort);
            }
        }
        finally
        {
            _restoringDraft = false;
        }
        RefreshHeader();
        RefreshComposerState();
        CancelScheduledDraftSave();
    }

    /// <summary>Vuelve a encontrar un adjunto del borrador: tiene que seguir existiendo, dentro del vault y del
    /// ámbito del chat, en una sesión desbloqueada y, si es un archivo, seguir siendo texto enviable.</summary>
    private AiAttachmentChipViewModel? ResolveDraftAttachment(AiDraftAttachment attachment)
    {
        if (!_vaultService.IsVaultConfigured || attachment.RelativePath.Length == 0) return null;
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(_vaultService.VaultPath, attachment.RelativePath));
            if (!IsPathInScope(fullPath)) return null;
            if (attachment.Kind == AiAttachmentKind.Note)
                return _vaultService.GetNote(fullPath) is { } note
                    ? new AiAttachmentChipViewModel(note, WorkspaceLabelFor(fullPath))
                    : null;
            if (!File.Exists(fullPath)) return null;
            var file = AiTextFiles.Describe(fullPath);
            return AiTextFiles.Check(file) == AiTextFileProblem.None
                ? new AiAttachmentChipViewModel(file, WorkspaceLabelFor(fullPath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or VaultUnavailableException)
        {
            return null;
        }
    }

    private void ClearNewChatDraft(string key)
    {
        try
        {
            _chats.SaveNewChatDraft(key, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            LogService.Warning("No se pudo borrar el borrador del asistente", ex);
        }
    }
}
