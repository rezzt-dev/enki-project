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

    public bool HasLinkedNote => SourceCard.HasLinkedNote;
    public string LinkedNoteLabel => SourceCard.LinkedNoteLabel;
    public bool IsLinkedNoteMissing => SourceCard.IsLinkedNoteMissing;
    public Material.Icons.MaterialIconKind LinkedNoteIcon => SourceCard.LinkedNoteIcon;
    public string? LinkedNoteTooltip => SourceCard.LinkedNoteTooltip;

    public ObservableCollection<KanbanSubtaskItemViewModel> Subtasks { get; } = [];

    public KanbanCardDetailViewModel(KanbanCardViewModel sourceCard, Action persist, Action<string>? onOpenLinkedNote = null)
    {
        SourceCard = sourceCard;
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
