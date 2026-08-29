using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

public sealed partial class KanbanCardViewModel : ObservableObject
{
    public KanbanCard Card { get; }

    public string Id => Card.Id;
    public string Title => Card.Title;
    public string Description => Card.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Card.Description);

    public KanbanPriority Priority => Card.Priority;
    public string PriorityLabel => Card.Priority switch
    {
        KanbanPriority.High => LocalizationService.Instance["Kanban_Priority_High"],
        KanbanPriority.Low => LocalizationService.Instance["Kanban_Priority_Low"],
        _ => LocalizationService.Instance["Kanban_Priority_Medium"]
    };

    /// <summary>La tarjeta solo escribe la palabra "alta" cuando de verdad lo es: repetir "MEDIA" en
    /// veinte tarjetas seguidas es ruido, y baja/media ya se distinguen por la intensidad del punto
    /// (docs/agent-docs/sistema-diseno-web.md §1.3, "más opaco = más prioridad"). Lo excepcional se
    /// nombra, lo normal se insinúa.</summary>
    public bool IsHighPriority => Card.Priority == KanbanPriority.High;

    /// <summary>Contrapartida de esconder la etiqueta en baja/media: el tooltip del punto siempre dice la
    /// prioridad actual además de qué hace el clic, así que la información sigue disponible sin abrir la
    /// tarjeta y sin depender de distinguir dos grises.</summary>
    public string PriorityTooltip => string.Format(
        CultureInfo.CurrentCulture,
        LocalizationService.Instance["Kanban_Card_PriorityTooltip"],
        PriorityLabel);

    public bool HasDueDate => Card.DueDateUtc is not null;
    public bool IsOverdue => Card.DueDateUtc is { } due && KanbanCard.IsPastDue(due);
    public string DueDateLabel => Card.DueDateUtc is { } due ? KanbanCard.FormatDueLabel(due) : string.Empty;

    public bool HasSubtasks => Card.Subtasks.Count > 0;
    private int DoneSubtaskCount => Card.Subtasks.Count(s => s.IsDone);
    public string SubtaskProgressLabel => $"{DoneSubtaskCount}/{Card.Subtasks.Count}";

    /// <summary>Star weights for the two-column grid that draws the subtask-progress bar in the card:
    /// column 0 (hecho) vs column 1 (restante). The track itself stretches to the full card width
    /// (respetando el padding de la tarjeta), so the fill is proportional rather than a fixed 54px stub —
    /// GridLength keeps the split declarative without a fraction converter.</summary>
    public GridLength SubtaskProgressDoneStar => new(DoneSubtaskCount, GridUnitType.Star);
    public GridLength SubtaskProgressRemainingStar => new(Card.Subtasks.Count - DoneSubtaskCount, GridUnitType.Star);

    public bool HasTags => Card.Tags.Count > 0;
    public string TagsLabel => string.Join(", ", Card.Tags);

    /// <summary>La tarjeta tiene algo que enseñar en la línea de metadatos del pie. Sin esta comprobación,
    /// una tarjeta que solo tiene título arrastraba el margen de una fila vacía debajo del texto.</summary>
    public bool HasMeta => HasSubtasks || HasDueDate || HasTags || HasLinkedNote;

    /// <summary>Enlace nota↔tarea, "Capa 1". El label es el nombre de archivo derivado de la ruta, no el
    /// título real de la nota: resolverlo
    /// contra disco requeriría inyectar <c>VaultService</c> en cada tarjeta del tablero solo para este
    /// caso, cuando en la práctica el nombre de archivo ya identifica la nota igual de bien (mismo criterio
    /// liviano que <see cref="TagsLabel"/>, que tampoco resuelve nada más allá de lo que ya trae el
    /// modelo).</summary>
    public bool HasLinkedNote => !string.IsNullOrEmpty(Card.LinkedNotePath);
    public string LinkedNoteLabel => Card.LinkedNotePath is { } path
        ? System.IO.Path.GetFileNameWithoutExtension(path)
        : string.Empty;

    /// <summary>True when <see cref="HasLinkedNote"/> is true but the file it points at is gone (deleted,
    /// moved outside the vault) — a plain <see cref="System.IO.File.Exists(string)"/> check is enough here,
    /// so this doesn't need the <see cref="VaultService"/> the class-level doc comment above deliberately
    /// avoids injecting. Drives the "broken link" icon/tooltip in the card-detail popover instead of the
    /// link silently doing nothing when clicked (docs/analisis-proyecto/roadmap.md, Fase 5, análisis §4).</summary>
    public bool IsLinkedNoteMissing => Card.LinkedNotePath is { } path && !System.IO.File.Exists(path);

    /// <summary>Which face the linked-note icon shows — an alert glyph once <see cref="IsLinkedNoteMissing"/>
    /// instead of a color change, so the board keeps its "one real color, reserved for overdue" rule
    /// (docs/sistema-diseno-web.md §1.3) while a broken link still reads differently at a glance.</summary>
    public MaterialIconKind LinkedNoteIcon => IsLinkedNoteMissing ? MaterialIconKind.NoteAlertOutline : MaterialIconKind.NoteTextOutline;

    /// <summary>Null (no tooltip) unless <see cref="IsLinkedNoteMissing"/> — explains why clicking the
    /// linked-note button won't do anything instead of leaving that silent (docs/analisis-proyecto/
    /// roadmap.md, Fase 5, análisis §4). Reuses the same title
    /// <c>MainWindowViewModel.OpenLinkedNoteFromKanban</c> already shows in a notification when the click
    /// itself fails, instead of adding a second string for the same fact.</summary>
    public string? LinkedNoteTooltip => IsLinkedNoteMissing ? LocalizationService.Instance["Notification_LinkedNoteMissing_Title"] : null;

    [ObservableProperty]
    private bool _isDropTarget;

    [ObservableProperty]
    private bool _isVisible = true;

    public KanbanCardViewModel(KanbanCard card)
    {
        Card = card;
    }

    /// <summary>Devuelve true si no hay etiquetas seleccionadas o si la tarjeta tiene al menos una
    /// etiqueta en común con <paramref name="selectedTags"/> (comparación insensible a mayúsculas).</summary>
    public bool MatchesTags(IEnumerable<string> selectedTags)
    {
        var selectedList = selectedTags.ToList();
        if (selectedList.Count == 0) return true;

        return Card.Tags.Any(tag =>
            selectedList.Contains(tag, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Filtro rápido de la barra de herramientas (alta prioridad / vencidas). Es un filtro más
    /// del mismo <see cref="KanbanColumnViewModel.ApplyFilters"/>, no un modo aparte: se combina en AND
    /// con el buscador y las etiquetas, igual que ellos entre sí.</summary>
    public bool MatchesQuickFilter(KanbanQuickFilter filter) => filter switch
    {
        KanbanQuickFilter.HighPriority => Card.Priority == KanbanPriority.High,
        KanbanQuickFilter.Overdue => IsOverdue,
        _ => true
    };

    /// <summary>Buscador interno del tablero —
    /// coincide por título, descripción o título de cualquier subtarea, insensible a mayúsculas.</summary>
    public bool MatchesSearch(string query) =>
        string.IsNullOrWhiteSpace(query)
        || Card.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Card.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Card.Subtasks.Any(s => s.Title.Contains(query, StringComparison.OrdinalIgnoreCase));

    public void CyclePriority()
    {
        Card.Priority = Card.Priority switch
        {
            KanbanPriority.Low => KanbanPriority.Medium,
            KanbanPriority.Medium => KanbanPriority.High,
            _ => KanbanPriority.Low
        };
        OnPropertyChanged(nameof(Priority));
        OnPropertyChanged(nameof(PriorityLabel));
        OnPropertyChanged(nameof(IsHighPriority));
        OnPropertyChanged(nameof(PriorityTooltip));
    }

    /// <summary>Every display property above reads straight through to <see cref="Card"/>, so mutating
    /// the model in place (from the full editor form) needs an explicit refresh — plain field assignment
    /// on <see cref="Card"/> raises no PropertyChanged of its own.</summary>
    public void RefreshFromModel()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(Priority));
        OnPropertyChanged(nameof(PriorityLabel));
        OnPropertyChanged(nameof(IsHighPriority));
        OnPropertyChanged(nameof(PriorityTooltip));
        OnPropertyChanged(nameof(HasDueDate));
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(DueDateLabel));
        OnPropertyChanged(nameof(HasSubtasks));
        OnPropertyChanged(nameof(SubtaskProgressLabel));
        OnPropertyChanged(nameof(SubtaskProgressDoneStar));
        OnPropertyChanged(nameof(SubtaskProgressRemainingStar));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(TagsLabel));
        OnPropertyChanged(nameof(HasMeta));
        OnPropertyChanged(nameof(HasLinkedNote));
        OnPropertyChanged(nameof(LinkedNoteLabel));
        OnPropertyChanged(nameof(IsLinkedNoteMissing));
        OnPropertyChanged(nameof(LinkedNoteIcon));
        OnPropertyChanged(nameof(LinkedNoteTooltip));
    }
}
