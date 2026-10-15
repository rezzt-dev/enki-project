using System.Collections.Generic;

namespace enki.Models;

/// <summary>Vista guardada: criterios reutilizables; sus resultados se calculan siempre desde las notas.</summary>
public sealed class NoteQuery
{
    public string Name { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public List<string> SelectedTags { get; set; } = [];
    public string PropertyKey { get; set; } = string.Empty;
    public string PropertyValue { get; set; } = string.Empty;
    public string ModifiedFrom { get; set; } = string.Empty;
    public string ModifiedTo { get; set; } = string.Empty;
    public bool IncludeSubfolders { get; set; }
    public NoteQuery Clone()
    {
        var copy = (NoteQuery)MemberwiseClone();
        copy.SelectedTags = [..(SelectedTags ?? [])];
        return copy;
    }
    public override string ToString() => Name;
}
