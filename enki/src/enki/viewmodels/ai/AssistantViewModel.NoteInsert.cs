using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>"Añadir a la nota activa": lleva una respuesta al final de la nota abierta en Documentos, por
/// decisión del usuario y después de enseñarle el cambio. Es distinto de "Guardar como nota nueva" (que crea
/// otra nota y la abre) y de lo que el asistente hace con sus herramientas (que el usuario no revisa antes): por
/// eso hay vista previa con las líneas que se añaden. La escritura pasa por el mismo camino que las
/// herramientas —historial de versiones, <c>WriteContentChecked</c> contra lo que se previsualizó, pestaña
/// abierta actualizada— y no navega a la nota.</summary>
public sealed partial class AssistantViewModel
{
    /// <summary>Líneas de contexto de la nota que se enseñan antes de lo añadido.</summary>
    private const int AppendPreviewContextLines = 3;

    private readonly Func<string, string, NoteDiskStamp, NoteFile>? _appendToNote;

    /// <summary>Lo que se va a añadir y la huella de la nota tal como se previsualizó.</summary>
    private (string Path, string Text, NoteDiskStamp Baseline)? _pendingAppend;

    public bool CanAppendToActiveNote => _appendToNote is not null && _activeNote is not null;

    public string AppendToActiveNoteLabel => _activeNote is null
        ? LocalizationService.Instance["Assistant_Message_AppendToNote"]
        : string.Format(LocalizationService.Instance.CurrentCulture,
            LocalizationService.Instance["Assistant_Message_AppendToNamedNote"], _activeNote.Title);

    [ObservableProperty]
    private bool _isAppendPreviewOpen;

    [ObservableProperty]
    private string _appendPreviewTitle = string.Empty;

    public ObservableCollection<DiffLine> AppendPreviewLines { get; } = [];

    [RelayCommand]
    private void RequestAppendToNote(AiChatMessageViewModel? message)
    {
        RefreshActiveNote();
        if (message is not { CanAct: true } || _activeNote is not { } note || _appendToNote is null) return;
        _flushPendingEdits();
        try
        {
            var current = _vaultService.ReadFromDisk(note.FullPath) ?? throw new FileNotFoundException(null, note.FullPath);
            var text = message.Text.Trim();
            var updated = AiVaultToolbox.AppendAtEnd(current.Content.Replace("\r\n", "\n", StringComparison.Ordinal), text);
            var diff = TextDiffService.Compare(current.Content.Replace("\r\n", "\n", StringComparison.Ordinal), updated);
            var firstChange = diff.FindIndex(line => line.Kind != DiffLineKind.Unchanged);
            AppendPreviewLines.Clear();
            foreach (var line in diff.Skip(Math.Max(0, firstChange - AppendPreviewContextLines))) AppendPreviewLines.Add(line);
            AppendPreviewTitle = current.Note.Title;
            _pendingAppend = (note.FullPath, text, current.Stamp);
            IsAppendPreviewOpen = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultUnavailableException or SessionLockedException)
        {
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Notify_AppendFailed_Title"),
                VaultFailureReasons.DescribeLocalized(ex), NotificationSeverity.Error);
        }
    }

    [RelayCommand]
    private void ConfirmAppendToNote()
    {
        if (_pendingAppend is not { } pending || _appendToNote is null) return;
        var title = AppendPreviewTitle;
        CancelAppendToNote();
        try
        {
            var note = _appendToNote(pending.Path, pending.Text, pending.Baseline);
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Notify_Appended_Title"),
                LocalizedTextFormatter.Format("Assistant_Notify_Appended_Message", note.Title), NotificationSeverity.Success);
        }
        catch (ExternalModificationException)
        {
            // La nota cambió desde la vista previa: no se escribe nada y se explica cómo seguir.
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Notify_AppendFailed_Title"),
                LocalizedTextFormatter.Format("Assistant_Notify_AppendConflict_Message", title), NotificationSeverity.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultUnavailableException
                                       or SessionLockedException or TimeoutException)
        {
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Notify_AppendFailed_Title"),
                VaultFailureReasons.DescribeLocalized(ex), NotificationSeverity.Error);
        }
    }

    [RelayCommand]
    private void CancelAppendToNote()
    {
        IsAppendPreviewOpen = false;
        _pendingAppend = null;
        AppendPreviewLines.Clear();
    }
}
