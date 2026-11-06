using System.IO;

namespace enki.ViewModels;

/// <summary>Referencia derivada del Markdown actual; nunca mantiene una copia del archivo ni de la nota.</summary>
public sealed record NoteAttachmentItemViewModel(string Target, string Label, int Offset, int Length)
{
    public bool IsSelected { get; init; }
    public string FileName => string.IsNullOrWhiteSpace(Label) ? Path.GetFileName(Target) : enki.Services.NoteAttachmentService.UnescapeLabel(Label);
}
