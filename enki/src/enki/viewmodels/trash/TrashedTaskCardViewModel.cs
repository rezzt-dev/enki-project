using System.Globalization;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

public sealed class TrashedTaskCardViewModel(KanbanTrashedCard trashedCard)
{
    public KanbanTrashedCard TrashedCard { get; } = trashedCard;
    public string Title => TrashedCard.Card.Title;
    public string ColumnTitle => TrashedCard.ColumnTitle;

    /// <summary>"Was in: {0}" — the origin Kanban column, shown on the trashed-task card. Composed here
    /// instead of via XAML StringFormat because word order around the placeholder isn't fixed across
    /// languages — see docs/agent-docs/internacionalizacion-i18n.md §1.3.</summary>
    public string WasInColumnLabel => string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Trash_Task_WasInColumn"], ColumnTitle);

    public string DeletedLabel => RelativeTimeFormatter.Format(TrashedCard.DeletedUtc);
}
