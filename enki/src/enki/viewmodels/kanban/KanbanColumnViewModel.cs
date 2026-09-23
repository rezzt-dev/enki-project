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

    /// <summary>Columna "Hecho" del flujo base, identificada por su rol y no por su nombre (que el
    /// usuario puede cambiar) — es la única desde la que se archiva.</summary>
    public bool IsDoneColumn => Column.Role == CoreColumnRole.Done;

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

    /// <summary>Hay algo que archivar en bloque desde esta columna — respalda el botón "archivar
    /// completadas" de la cabecera, que solo existe en "Hecho" y solo mientras tenga tarjetas.</summary>
    public bool CanArchiveAllCards => IsDoneColumn && CardCount > 0;

    [ObservableProperty]
    private bool _isDropTarget;

    partial void OnVisibleCardCountChanged(int value) => RefreshCountLabels();
    partial void OnIsFilteredChanged(bool value) => RefreshCountLabels();
    partial void OnCardCountChanged(int value) => RefreshCountLabels();

    private void RefreshCountLabels()
    {
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(CanArchiveAllCards));
    }

    public KanbanColumnViewModel(KanbanColumn column)
    {
        Column = column;
        // Las tarjetas archivadas se quedan fuera del tablero pero siguen en Column.Cards
        // (docs/agent-docs/kanban-tareas-archivadas.md): esta es la única frontera entre "existe en el
        // modelo" y "se dibuja en la columna", y por eso Calendario/Estadísticas no se enteran de nada.
        foreach (var card in column.Cards.Where(c => !c.IsArchived)) Cards.Add(new KanbanCardViewModel(card));
        CardCount = Cards.Count;
        VisibleCardCount = Cards.Count;
        SyncCardArchivability();

        // IsEmpty has no setter of its own, so its dependents (the "sin tarjetas" placeholder)
        // only redraw if something explicitly announces the change — Cards.Add/Remove alone
        // doesn't raise ObservableObject.PropertyChanged for a derived property like this.
        Cards.CollectionChanged += (_, _) =>
        {
            CardCount = Cards.Count;
            VisibleCardCount = Cards.Count(c => c.IsVisible);
            SyncCardArchivability();
            OnPropertyChanged(nameof(IsEmpty));
        };
    }

    /// <summary>Sella <see cref="KanbanCardViewModel.CanArchive"/> en las tarjetas de esta columna. Se
    /// llama desde el propio CollectionChanged de <see cref="Cards"/> porque esa colección es el único
    /// sitio por el que una tarjeta entra o sale de una columna —arrastrarla, moverla desde el editor,
    /// crearla o desarchivarla acaban todos ahí—, así que ningún camino puede olvidarse de actualizarla.</summary>
    private void SyncCardArchivability()
    {
        foreach (var card in Cards) card.CanArchive = IsDoneColumn;
    }

    /// <summary>Vuelve a meter en el tablero una tarjeta que estaba archivada, en el sitio que le
    /// corresponde según el orden real del modelo — la tarjeta nunca se movió de
    /// <see cref="KanbanColumn.Cards"/> al archivarse, así que desarchivar es solo devolverle su
    /// viewmodel a la columna. El índice se cuenta saltándose las archivadas que haya por delante,
    /// que son justo las que la vista no dibuja.</summary>
    public void RestoreArchivedCard(KanbanCardViewModel card)
    {
        var modelIndex = Column.Cards.IndexOf(card.Card);
        if (modelIndex < 0)
        {
            Column.Cards.Add(card.Card);
            Cards.Add(card);
            return;
        }

        var index = 0;
        for (var i = 0; i < modelIndex; i++)
        {
            if (!Column.Cards[i].IsArchived) index++;
        }

        Cards.Insert(Math.Min(index, Cards.Count), card);
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

        // Column.Cards también guarda las tarjetas archivadas, que no tienen viewmodel en Cards: se
        // rescatan antes de reconstruir la lista del modelo y se vuelven a añadir al final conservando
        // su orden relativo. Sin esto, ordenar el tablero borraría del disco todo lo archivado.
        var archived = Column.Cards.Where(c => c.IsArchived).ToList();

        Cards.Clear();
        Column.Cards.Clear();
        foreach (var card in sorted)
        {
            Cards.Add(card);
            Column.Cards.Add(card.Card);
        }
        foreach (var card in archived) Column.Cards.Add(card);
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
