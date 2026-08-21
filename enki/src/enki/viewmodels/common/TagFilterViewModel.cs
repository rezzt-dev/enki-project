using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace enki.ViewModels;

/// <summary>Filtro de etiquetas reutilizable para Notas, Kanban y Polar. Mantiene una colección de
/// chips toggleables y dispara <see cref="Changed"/> cada vez que cambia la selección, para que el
/// viewmodel consumidor aplique el filtro sobre la lista visible.</summary>
public sealed partial class TagFilterViewModel : ObservableObject
{
    public ObservableCollection<TagFilterItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private bool _isEmpty = true;

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

    private void OnItemToggled(TagFilterItemViewModel _) => Changed?.Invoke();
}
