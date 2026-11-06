using System.Collections.Generic;
using System.Linq;

namespace enki.Models;

/// <summary>Una nota que comparte etiquetas o enlaces con la nota activa sin estar enlazada con ella.
/// La calcula <see cref="Services.NoteRelationsService.FindRelated"/> y se muestra en el bloque
/// «relacionadas» del panel lateral del editor.</summary>
public sealed class RelatedNote
{
    public required string FullPath { get; init; }
    public required string Title { get; init; }

    /// <summary>Nombre con el que se enlaza sin ambigüedad (nombre de archivo o título), o
    /// <see langword="null"/> si dos notas lo comparten y no hay forma de apuntar a esta.</summary>
    public string? LinkName { get; init; }

    public required IReadOnlyList<string> SharedTags { get; init; }

    /// <summary>Títulos de las notas a las que enlazan tanto esta nota como la activa.</summary>
    public required IReadOnlyList<string> SharedLinks { get; init; }

    public int Score => SharedTags.Count + SharedLinks.Count * 2;

    public bool CanLink => LinkName is not null;

    /// <summary>Resumen de por qué se relaciona: etiquetas compartidas (<c>#a #b</c>) y enlaces comunes.</summary>
    public string Reason
    {
        get
        {
            var parts = new List<string>();
            if (SharedTags.Count > 0) parts.Add(string.Join(' ', SharedTags.Select(t => "#" + t)));
            if (SharedLinks.Count > 0) parts.Add(string.Join(", ", SharedLinks));
            return string.Join(" · ", parts);
        }
    }
}
