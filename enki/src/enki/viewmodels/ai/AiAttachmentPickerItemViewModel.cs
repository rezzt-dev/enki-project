using enki.Localization;
using enki.Models;
using enki.Services;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Fila del selector de adjuntos del asistente: una nota o un archivo del ámbito del chat. Los archivos
/// que no se pueden enviar como texto (imágenes, PDF, demasiado grandes) se listan igualmente, deshabilitados
/// y con el motivo, para que el usuario sepa por qué no puede elegirlos en vez de no encontrarlos.</summary>
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
        Problem = AiTextFiles.Check(file);
    }

    public NoteFile? Note { get; }
    public VaultFile? File { get; }
    public string Title { get; }
    public string WorkspaceLabel { get; }
    public AiTextFileProblem Problem { get; }

    public bool IsEnabled => Problem == AiTextFileProblem.None;

    public MaterialIconKind Icon => File is null ? MaterialIconKind.NoteTextOutline : MaterialIconKind.FileDocumentOutline;

    /// <summary>Espacio y, en un archivo, tamaño o motivo por el que no se puede adjuntar.</summary>
    public string Detail => File is null
        ? WorkspaceLabel
        : Problem switch
        {
            AiTextFileProblem.NotText => $"{WorkspaceLabel} · {LocalizationService.Instance["Assistant_Attachment_NotText"]}",
            AiTextFileProblem.TooLarge => $"{WorkspaceLabel} · {LocalizationService.Instance["Assistant_Attachment_TooLarge"]}",
            _ => $"{WorkspaceLabel} · {File.SizeLabel}"
        };
}
