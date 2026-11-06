namespace enki.Models;

/// <summary>Una nota que nombra a la nota activa en texto plano, fuera de código, enlaces y propiedades,
/// sin haberla enlazado. La calcula <see cref="Services.NoteRelationsService.FindUnlinkedMentions"/>.</summary>
public sealed class UnlinkedMention
{
    public required string FullPath { get; init; }
    public required string Title { get; init; }

    /// <summary>Línea que contiene la primera mención, recortada.</summary>
    public required string Context { get; init; }

    /// <summary>Texto exacto encontrado, tal y como está escrito en la nota (conserva mayúsculas).</summary>
    public required string MatchedText { get; init; }

    /// <summary>Destino que se escribe dentro de <c>[[…]]</c>: nombre de archivo o título de la nota
    /// mencionada, el que la resuelve sin ambigüedad.</summary>
    public required string LinkName { get; init; }
}
