namespace enki.ViewModels;

/// <summary>One row in the "Tareas vinculadas" section of <see cref="EditorViewModel"/> — the reverse side
/// of a Kanban card's <see cref="Models.KanbanCard.LinkedNotePath"/> (note↔task link, "Capa 1").</summary>
public sealed class LinkedCardItemViewModel(string id, string title)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
}
