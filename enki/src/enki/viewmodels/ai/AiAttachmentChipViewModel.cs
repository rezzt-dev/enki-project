using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Nota adjunta como contexto: pendiente en el compositor (con <see cref="Note"/>) o ya enviada en
/// un mensaje del historial (con lo que se guardó de ella).</summary>
public sealed partial class AiAttachmentChipViewModel : ObservableObject
{
    private readonly AiContextAttachment? _sent;

    public AiAttachmentChipViewModel(NoteFile note, string workspaceLabel)
    {
        Note = note;
        Title = note.Title;
        WorkspaceLabel = workspaceLabel;
    }

    public AiAttachmentChipViewModel(AiContextAttachment sent)
    {
        _sent = sent;
        Title = sent.Title;
        WorkspaceLabel = sent.RelativePath;
    }

    public NoteFile? Note { get; }
    public string Title { get; }

    /// <summary>Workspace de una nota pendiente, o ruta relativa de una ya enviada (para el tooltip).</summary>
    public string WorkspaceLabel { get; }

    public bool WasTruncated => _sent?.WasTruncated == true;

    /// <summary>"3,2 k car." o "3,2 k car. · recortada" para una nota enviada; vacío para una pendiente.</summary>
    public string Detail
    {
        get
        {
            if (_sent is null) return string.Empty;
            var size = string.Format(LocalizationService.Instance.CurrentCulture,
                LocalizationService.Instance["Assistant_Attachment_Chars"], FormatCount(_sent.SentChars));
            return _sent.WasTruncated
                ? $"{size} · {LocalizationService.Instance["Assistant_Attachment_Truncated"]}"
                : size;
        }
    }

    public bool HasDetail => _sent is not null;

    internal static string FormatCount(int count) => count >= 1000
        ? (count / 1000.0).ToString("0.#", LocalizationService.Instance.CurrentCulture) + " k"
        : count.ToString(LocalizationService.Instance.CurrentCulture);

    public void RefreshLocalizedLabels() => OnPropertyChanged(nameof(Detail));
}
