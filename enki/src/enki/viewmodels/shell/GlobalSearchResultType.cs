using enki.Models;

namespace enki.ViewModels;

/// <summary>
/// Origen de un resultado devuelto por la búsqueda global.
/// </summary>
public enum GlobalSearchResultType
{
    Note,
    KanbanCard,
    PolarTask,
    Reminder
}
