using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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

    /// <summary>Tarjetas que sobreviven al filtro activo (buscador + etiquetas + filtro rápido). Se
    /// mantiene aparte de <see cref="CardCount"/> para que el contador de la cabecera pueda decir
    /// "2/5" mientras se filtra en vez de mentir con el total — ver <see cref="CountLabel"/>.</summary>
    [ObservableProperty]
    private int _visibleCardCount;

    /// <summary>True mientras haya algún filtro activo en el tablero, aunque esta columna en concreto no
    /// pierda ninguna tarjeta: es lo que decide si el contador se muestra como total o como "visibles/total".</summary>
    [ObservableProperty]
    private bool _isFiltered;

    /// <summary>Contador de la cabecera: el total a secas cuando no hay filtros, y "visibles/total"
    /// cuando sí — así una columna que se ve vacía por el filtro no parece vacía de verdad.</summary>
    public string CountLabel => IsFiltered
        ? string.Format(CultureInfo.CurrentCulture, "{0}/{1}", VisibleCardCount, CardCount)
        : CardCount.ToString(CultureInfo.CurrentCulture);

    /// <summary>La columna tiene tarjetas pero el filtro activo las esconde todas — estado distinto de
    /// <see cref="IsEmpty"/> (columna realmente vacía) y con su propio aviso en la vista, para no dejar
    /// al usuario mirando un hueco sin explicación.</summary>
    public bool HasNoMatches => CardCount > 0 && VisibleCardCount == 0;

    [ObservableProperty]
    private bool _isDropTarget;

    partial void OnVisibleCardCountChanged(int value) => RefreshCountLabels();
    partial void OnIsFilteredChanged(bool value) => RefreshCountLabels();
    partial void OnCardCountChanged(int value) => RefreshCountLabels();

    private void RefreshCountLabels()
    {
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(HasNoMatches));
    }

    public KanbanColumnViewModel(KanbanColumn column)
    {
        Column = column;
        foreach (var card in column.Cards) Cards.Add(new KanbanCardViewModel(card));
        CardCount = Cards.Count;
        VisibleCardCount = Cards.Count;

        // IsEmpty has no setter of its own, so its dependents (the "sin tarjetas" placeholder)
        // only redraw if something explicitly announces the change — Cards.Add/Remove alone
        // doesn't raise ObservableObject.PropertyChanged for a derived property like this.
        Cards.CollectionChanged += (_, _) =>
        {
            CardCount = Cards.Count;
            VisibleCardCount = Cards.Count(c => c.IsVisible);
            OnPropertyChanged(nameof(IsEmpty));
        };
    }

    public void ApplyTitle(string title)
    {
        Column.Title = title;
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>Actualiza la visibilidad de cada tarjeta según los tres filtros que puede tener activos el
    /// tablero a la vez — etiquetas, buscador interno y filtro rápido de la barra de herramientas. Oculta
    /// visualmente sin mutar la colección, para conservar drag &amp; drop, estadísticas y orden, y de paso
    /// deja el recuento de visibles listo para la cabecera de la columna.</summary>
    public void ApplyFilters(IEnumerable<string> selectedTags, string searchText,
        KanbanQuickFilter quickFilter = KanbanQuickFilter.None)
    {
        var selectedList = selectedTags as ICollection<string> ?? selectedTags.ToList();

        var visible = 0;
        foreach (var card in Cards)
        {
            card.IsVisible = card.MatchesTags(selectedList)
                && card.MatchesSearch(searchText)
                && card.MatchesQuickFilter(quickFilter);
            if (card.IsVisible) visible++;
        }

        VisibleCardCount = visible;
        IsFiltered = selectedList.Count > 0
            || !string.IsNullOrWhiteSpace(searchText)
            || quickFilter != KanbanQuickFilter.None;
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
