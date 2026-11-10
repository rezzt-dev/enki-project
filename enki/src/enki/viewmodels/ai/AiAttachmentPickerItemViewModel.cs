using enki.Localization;
using enki.Models;
using enki.Services;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Fila del selector de adjuntos del asistente: una nota o un archivo del ámbito del chat. Los archivos
/// que el asistente no puede leer (audio, vídeo, formatos binarios desconocidos o demasiado grandes) se listan
/// igualmente, deshabilitados y con el motivo, para que el usuario sepa por qué no puede elegirlos en vez de no
/// encontrarlos (ver <see cref="AiFileReader"/>).</summary>
public sealed class AiAttachmentPickerItemViewModel
{
    public AiAttachmentPickerItemViewModel(NoteFile note, string workspaceLabel)
    {
        Note = note;
        Title = note.Title;
        WorkspaceLabel = workspaceLabel;
    }

    public AiAttachmentPickerItemViewModel(VaultFile file, string workspaceLabel)
    {
        File = file;
        Title = file.FileName;
        WorkspaceLabel = workspaceLabel;
        Problem = AiFileReader.Check(file);
    }

    public NoteFile? Note { get; }
    public VaultFile? File { get; }
    public string Title { get; }
    public string WorkspaceLabel { get; }
    public AiFileProblem Problem { get; }

    public bool IsEnabled => Problem == AiFileProblem.None;

    public MaterialIconKind Icon => File is null ? MaterialIconKind.NoteTextOutline : AiFileReader.FormatOf(File) switch
    {
        AiFileFormat.Image => MaterialIconKind.FileImageOutline,
        AiFileFormat.Pdf => MaterialIconKind.FilePdfBox,
        AiFileFormat.Spreadsheet => MaterialIconKind.FileTableOutline,
        AiFileFormat.Media => File.Kind == VaultFileKind.Audio ? MaterialIconKind.FileMusicOutline : MaterialIconKind.FileVideoOutline,
        AiFileFormat.Text => MaterialIconKind.FileCodeOutline,
        _ => MaterialIconKind.FileDocumentOutline
    };

    /// <summary>Espacio y, en un archivo, tamaño o motivo por el que no se puede adjuntar.</summary>
    public string Detail => File is null
        ? WorkspaceLabel
        : Problem switch
        {
            AiFileProblem.Unsupported => $"{WorkspaceLabel} · {LocalizationService.Instance["Assistant_Attachment_NotText"]}",
            AiFileProblem.TooLarge => $"{WorkspaceLabel} · {LocalizationService.Instance["Assistant_Attachment_TooLarge"]}",
            _ => $"{WorkspaceLabel} · {File.SizeLabel}"
        };
}
