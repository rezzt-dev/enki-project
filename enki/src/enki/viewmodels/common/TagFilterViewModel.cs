using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace enki.ViewModels;

/// <summary>Filtro de etiquetas reutilizable para Notas, Kanban y Polar. Mantiene una colección de
/// chips toggleables y dispara <see cref="Changed"/> cada vez que cambia la selección, para que el
/// viewmodel consumidor aplique el filtro sobre la lista visible.</summary>
public sealed partial class TagFilterViewModel : ObservableObject
{
    public ObservableCollection<TagFilterItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>Con la lista plegada solo se ve la primera fila de chips. Es estado de vista y vive
    /// aquí —no en el panel— porque el botón que lo alterna es hermano del panel, no hijo suyo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCollapsed))]
    private bool _isExpanded;

    /// <summary>Chips que la vista plegada deja fuera. Lo escribe <c>TagChipsPanel</c> al medirse,
    /// que es el único que sabe cuántos caben de verdad en una línea.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHiddenTags))]
    [NotifyPropertyChangedFor(nameof(HiddenBadge))]
    private int _hiddenCount;

    public bool IsCollapsed => !IsExpanded;

    /// <summary>Solo hay algo que desplegar si la lista no cabe en una línea.</summary>
    public bool HasHiddenTags => HiddenCount > 0;

    public string HiddenBadge => $"+{HiddenCount}";

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Etiquetas actualmente seleccionadas. Si está vacío, el consumidor debe mostrar todo.</summary>
    public IEnumerable<string> SelectedTags => Items.Where(i => i.IsSelected).Select(i => i.Tag);

    /// <summary>Se dispara cada vez que un chip cambia de estado.</summary>
    public event Action? Changed;

    /// <summary>Reconstruye los chips a partir de <paramref name="allTags"/>, conservando las
    /// etiquetas que ya estaban seleccionadas si aún existen en el nuevo conjunto. Las etiquetas vacías
    /// o con solo espacios se ignoran, y el resultado se deduplica y ordena de forma insensible a mayúsculas.</summary>
    public void Reload(IEnumerable<string> allTags)
    {
        var previouslySelected = Items
            .Where(i => i.IsSelected)
            .Select(i => i.Tag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Items.Clear();

        var distinctTags = allTags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var tag in distinctTags)
        {
            Items.Add(new TagFilterItemViewModel(tag, OnItemToggled)
            {
                IsSelected = previouslySelected.Contains(tag)
            });
        }

        IsEmpty = Items.Count == 0;
    }

    /// <summary>Deselecciona todos los chips sin reconstruir la lista — respalda el "limpiar filtros" de
    /// la barra de herramientas del tablero Kanban, que apaga buscador, etiquetas y filtro rápido de una
    /// sola acción. Solo dispara <see cref="Changed"/> si algo estaba realmente seleccionado, para no
    /// provocar un refiltrado inútil cuando ya no había nada que limpiar.</summary>
    public void ClearSelection()
    {
        var changed = false;
        foreach (var item in Items)
        {
            if (!item.IsSelected) continue;
            item.IsSelected = false;
            changed = true;
        }

        if (changed) Changed?.Invoke();
    }

    private void OnItemToggled(TagFilterItemViewModel _) => Changed?.Invoke();
}
