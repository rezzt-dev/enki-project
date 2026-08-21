using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;

namespace enki.ViewModels;

public sealed partial class KanbanColumnViewModel : ObservableObject
{
    public KanbanColumn Column { get; }

    public ObservableCollection<KanbanCardViewModel> Cards { get; } = [];

    public string Id => Column.Id;
    public string Title => Column.Title;
    public bool IsEmpty => Cards.Count == 0;

    /// <summary>The seeded workflow columns ("Por hacer", "En progreso", "Hecho")
    /// can be renamed like any other column, but not deleted.</summary>
    public bool IsCore => Column.IsCore;

    [ObservableProperty]
    private int _cardCount;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _editingTitle = string.Empty;

    [ObservableProperty]
    private bool _isDropTarget;

    public KanbanColumnViewModel(KanbanColumn column)
    {
        Column = column;
        foreach (var card in column.Cards) Cards.Add(new KanbanCardViewModel(card));
        CardCount = Cards.Count;

        // IsEmpty has no setter of its own, so its dependents (the "sin tarjetas" placeholder)
        // only redraw if something explicitly announces the change — Cards.Add/Remove alone
        // doesn't raise ObservableObject.PropertyChanged for a derived property like this.
        Cards.CollectionChanged += (_, _) =>
        {
            CardCount = Cards.Count;
            OnPropertyChanged(nameof(IsEmpty));
        };
    }

    public void ApplyTitle(string title)
    {
        Column.Title = title;
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>Actualiza la visibilidad de cada tarjeta según el filtro de etiquetas y el buscador interno
    /// activos. Oculta visualmente sin mutar la
    /// colección, para conservar drag &amp; drop, estadísticas y orden.</summary>
    public void ApplyFilters(IEnumerable<string> selectedTags, string searchText)
    {
        var selectedList = selectedTags as ICollection<string> ?? selectedTags.ToList();
        foreach (var card in Cards) card.IsVisible = card.MatchesTags(selectedList) && card.MatchesSearch(searchText);
    }

    /// <summary>Reordena esta columna (vista + modelo, así el orden sobrevive a un guardado) según
    /// <paramref name="mode"/> — no-op en modo <see cref="KanbanCardSortMode.Manual"/>, que conserva
    /// cualquier orden que el usuario haya dejado con drag &amp; drop.</summary>
    public void Sort(KanbanCardSortMode mode)
    {
        if (mode == KanbanCardSortMode.Manual) return;

        IOrderedEnumerable<KanbanCardViewModel> ordered = mode switch
        {
            KanbanCardSortMode.Priority => Cards.OrderByDescending(c => c.Card.Priority),
            KanbanCardSortMode.DueDate => Cards.OrderBy(c => c.Card.DueDateUtc ?? DateTime.MaxValue),
            KanbanCardSortMode.Created => Cards.OrderByDescending(c => c.Card.CreatedUtc),
            _ => Cards.OrderBy(c => 0)
        };

        var sorted = ordered.ToList();
        Cards.Clear();
        Column.Cards.Clear();
        foreach (var card in sorted)
        {
            Cards.Add(card);
            Column.Cards.Add(card.Card);
        }
    }

    [RelayCommand]
    private void BeginRename()
    {
        EditingTitle = Title;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;
}
