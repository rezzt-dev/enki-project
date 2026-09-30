using System.Globalization;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

public sealed class TrashedTaskCardViewModel(KanbanTrashedCard trashedCard, string? boardName = null)
{
    public KanbanTrashedCard TrashedCard { get; } = trashedCard;
    public string Title => TrashedCard.Card.Title;
    public string ColumnTitle => boardName is null ? TrashedCard.ColumnTitle : $"{boardName} · {TrashedCard.ColumnTitle}";

    /// <summary>"Was in: {0}" — the origin Kanban column, shown on the trashed-task card. Composed here
    /// instead of via XAML StringFormat because word order around the placeholder isn't fixed across
    /// languages — see docs/agent-docs/internacionalizacion-i18n.md §1.3. El nombre de la columna va en
    /// mayúsculas y el resto de la frase no: es la misma regla que aplica el tablero, donde una columna se
    /// dibuja siempre en mayúsculas escriba el usuario lo que escriba, y aquí no puede hacerla el
    /// conversor de la vista porque solo afecta a un fragmento de la frase.</summary>
    public string WasInColumnLabel => string.Format(
        CultureInfo.CurrentCulture,
        LocalizationService.Instance["Trash_Task_WasInColumn"],
        ColumnTitle.ToUpper(CultureInfo.CurrentCulture));

    public string DeletedLabel => RelativeTimeFormatter.Format(TrashedCard.DeletedUtc);
}
