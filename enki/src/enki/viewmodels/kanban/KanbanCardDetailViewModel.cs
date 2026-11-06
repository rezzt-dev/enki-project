using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Read-only view of a <see cref="KanbanCardViewModel"/> shown when a card is clicked —
/// every field just mirrors <see cref="SourceCard"/>; the only interactive part is toggling subtasks
/// (writes straight back into the model and persists immediately, no separate "save" step) and opening
/// the linked note, if any.</summary>
public sealed partial class KanbanCardDetailViewModel : ObservableObject
{
    private readonly Action _persist;
    private readonly Action<string>? _onOpenLinkedNote;

    public KanbanCardViewModel SourceCard { get; }

    public string Title => SourceCard.Title;
    public string Description => SourceCard.Description;
    public bool HasDescription => SourceCard.HasDescription;

    public KanbanPriority Priority => SourceCard.Priority;
    public string PriorityLabel => SourceCard.PriorityLabel;

    public bool HasDueDate => SourceCard.HasDueDate;
    public bool IsOverdue => SourceCard.IsOverdue;
    public string DueDateLabel => SourceCard.DueDateLabel;

    public bool HasSubtasks => Subtasks.Count > 0;
    public string SubtaskProgressLabel => $"{Subtasks.Count(s => s.IsDone)}/{Subtasks.Count}";

    public bool HasTags => SourceCard.HasTags;
    public string TagsLabel => SourceCard.TagsLabel;

    /// <summary>Columna en la que vive la tarjeta. El diálogo se abre desde sitios que no son el tablero
    /// (buzón de recordatorios, calendario), donde no hay ninguna columna alrededor que lo diga — así que
    /// el dato viaja con el detalle en vez de darse por evidente.</summary>
    public string ColumnTitle { get; }
    public bool HasColumnTitle => !string.IsNullOrWhiteSpace(ColumnTitle);

    /// <summary>Estado de archivado de la tarjeta, para que el diálogo pueda ofrecer "archivar" o
    /// "desarchivar" según toque y marcar como tal una tarjeta abierta desde la ventana de archivadas
    /// (docs/agent-docs/kanban-tareas-archivadas.md). Son de solo lectura como el resto del detalle: la
    /// acción la ejecuta <see cref="KanbanViewModel"/>, que es quien mueve la tarjeta.</summary>
    public bool IsArchived => SourceCard.IsArchived;
    public string ArchivedAtLabel => SourceCard.ArchivedAtLabel;

    /// <summary>La tarjeta está en la columna "Hecho" y todavía sin archivar — el diálogo es la vía que
    /// no depende del clic derecho ni del hover, igual que ya pasa con editar y eliminar.</summary>
    public bool CanArchive => SourceCard.CanArchive && !IsArchived;

    public bool HasLinkedNote => SourceCard.HasLinkedNote;
    public string LinkedNoteLabel => SourceCard.LinkedNoteLabel;
    public bool IsLinkedNoteMissing => SourceCard.IsLinkedNoteMissing;
    public Material.Icons.MaterialIconKind LinkedNoteIcon => SourceCard.LinkedNoteIcon;
    public string? LinkedNoteTooltip => SourceCard.LinkedNoteTooltip;

    public ObservableCollection<KanbanSubtaskItemViewModel> Subtasks { get; } = [];

    public KanbanCardDetailViewModel(KanbanCardViewModel sourceCard, Action persist,
        Action<string>? onOpenLinkedNote = null, string columnTitle = "")
    {
        SourceCard = sourceCard;
        ColumnTitle = columnTitle;
        _persist = persist;
        _onOpenLinkedNote = onOpenLinkedNote;

        foreach (var subtask in sourceCard.Card.Subtasks)
        {
            var item = new KanbanSubtaskItemViewModel(subtask.Id, subtask.Title, subtask.IsDone);
            item.PropertyChanged += OnSubtaskPropertyChanged;
            Subtasks.Add(item);
        }
    }

    [RelayCommand(CanExecute = nameof(HasLinkedNote))]
    private void OpenLinkedNote()
    {
        if (SourceCard.Card.LinkedNotePath is { } path) _onOpenLinkedNote?.Invoke(path);
    }

    private void OnSubtaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(KanbanSubtaskItemViewModel.IsDone)) return;
        if (sender is not KanbanSubtaskItemViewModel item) return;

        var model = SourceCard.Card.Subtasks.FirstOrDefault(s => s.Id == item.Id);
        if (model is null) return;

        model.IsDone = item.IsDone;
        SourceCard.RefreshFromModel();
        OnPropertyChanged(nameof(SubtaskProgressLabel));
        _persist();
    }
}
