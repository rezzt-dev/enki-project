using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Nota o archivo de texto adjunto como contexto: pendiente en el compositor (con
/// <see cref="FullPath"/>) o ya enviado en un mensaje del historial (con lo que se guardó de él).</summary>
public sealed partial class AiAttachmentChipViewModel : ObservableObject
{
    private readonly AiContextAttachment? _sent;

    public AiAttachmentChipViewModel(NoteFile note, string workspaceLabel)
    {
        Note = note;
        Kind = AiAttachmentKind.Note;
        FullPath = note.FullPath;
        Title = note.Title;
        WorkspaceLabel = workspaceLabel;
    }

    public AiAttachmentChipViewModel(VaultFile file, string workspaceLabel)
    {
        Kind = AiAttachmentKind.File;
        FullPath = file.FullPath;
        Title = file.FileName;
        WorkspaceLabel = workspaceLabel;
    }

    public AiAttachmentChipViewModel(AiContextAttachment sent)
    {
        _sent = sent;
        Kind = sent.Kind;
        Title = sent.Title;
        WorkspaceLabel = sent.RelativePath;
    }

    public AiAttachmentKind Kind { get; }

    /// <summary>La nota de un adjunto pendiente; <see langword="null"/> en un archivo o en uno ya enviado.</summary>
    public NoteFile? Note { get; }

    /// <summary>Ruta completa de un adjunto pendiente; <see langword="null"/> en uno ya enviado.</summary>
    public string? FullPath { get; }

    public string Title { get; }

    /// <summary>Workspace de un adjunto pendiente, o ruta relativa de uno ya enviado (para el tooltip).</summary>
    public string WorkspaceLabel { get; }

    public bool IsFile => Kind == AiAttachmentKind.File;

    public MaterialIconKind Icon => IsFile ? MaterialIconKind.FileDocumentOutline : MaterialIconKind.NoteTextOutline;

    public bool WasTruncated => _sent?.WasTruncated == true;

    /// <summary>Motivo por el que un adjunto pendiente no se puede enviar (se borró, ya no es texto, es
    /// demasiado grande, quedó fuera del ámbito). El chip lo enseña y el envío se detiene hasta retirarlo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    /// <summary>"3,2 k car." o "3,2 k car. · recortada" para un adjunto enviado; vacío para uno pendiente.</summary>
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
