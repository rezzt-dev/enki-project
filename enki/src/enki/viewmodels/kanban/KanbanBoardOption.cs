using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Fila del selector, reconstruida también al cambiar el idioma.</summary>
public sealed record KanbanBoardOption(KanbanBoard Board, bool IsDefault, bool IsSelected = false)
{
    public string Label => IsDefault ? $"{Board.Name} · {LocalizationService.Instance["Kanban_Multi_Default"]}" : Board.Name;
}

