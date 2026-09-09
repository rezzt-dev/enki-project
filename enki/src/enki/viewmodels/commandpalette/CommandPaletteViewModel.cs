using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace enki.ViewModels;

/// <summary>Paleta de comandos unificada (propuesta 25, docs/propuesta-nuevas-funcionalidades.md):
/// Ctrl+Shift+K, tipear el nombre de una acción, Enter para ejecutarla — mismo patrón de popup + búsqueda
/// difusa por substring que <see cref="QuickSwitcherViewModel"/> (Ctrl+P, que solo abre notas), pero
/// ejecuta cualquier acción que el shell registre en vez de limitarse a navegar. El registro de comandos
/// vive fuera de esta clase (<c>_itemsProvider</c>, provisto por <c>MainWindowViewModel</c>) porque cada
/// módulo aporta los suyos y varios dependen de estado que cambia en caliente (sección de sidebar activa,
/// pestaña de nota abierta, cuenta Polar vinculada) — se reconstruye en cada apertura en vez de una sola
/// vez al arrancar.</summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<CommandPaletteItem>> _itemsProvider;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private int _selectedIndex;

    public ObservableCollection<CommandPaletteItemViewModel> Results { get; } = [];

    public bool HasResults => Results.Count > 0;
    public bool ShowNoResults => !HasResults && !string.IsNullOrWhiteSpace(SearchText);

    public CommandPaletteViewModel(Func<IReadOnlyList<CommandPaletteItem>> itemsProvider)
    {
        _itemsProvider = itemsProvider;
    }

    partial void OnSearchTextChanged(string value)
    {
        RefreshResults();
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            SearchText = string.Empty;
            SelectedIndex = 0;
            RefreshResults();
        }
    }

    partial void OnSelectedIndexChanged(int value)
    {
        var count = Results.Count;
        if (count == 0)
        {
            SelectedIndex = 0;
            return;
        }

        var newIndex = value;
        if (newIndex < 0) newIndex = count - 1;
        if (newIndex >= count) newIndex = 0;
        if (newIndex != value) SelectedIndex = newIndex;

        for (var i = 0; i < count; i++) Results[i].IsSelected = i == SelectedIndex;
    }

    [RelayCommand]
    private void OpenSelected()
    {
        if (Results.Count == 0) return;
        ExecuteItem(Results[Math.Clamp(SelectedIndex, 0, Results.Count - 1)]);
    }

    [RelayCommand]
    private void MoveSelectionDown()
    {
        if (Results.Count == 0) return;
        SelectedIndex = (SelectedIndex + 1) % Results.Count;
    }

    [RelayCommand]
    private void MoveSelectionUp()
    {
        if (Results.Count == 0) return;
        SelectedIndex = (SelectedIndex - 1 + Results.Count) % Results.Count;
    }

    [RelayCommand]
    private void ExecuteItem(CommandPaletteItemViewModel item)
    {
        IsOpen = false;
        item.Item.Execute();
    }

    /// <summary>Actualiza los comandos visibles conservando la consulta al cambiar de idioma.</summary>
    public void RefreshLocalizedLabels()
    {
        if (IsOpen) RefreshResults();
    }

    private void RefreshResults()
    {
        Results.Clear();
        SelectedIndex = 0;

        var all = _itemsProvider();
        var filtered = Filter(all, SearchText);
        foreach (var item in filtered) Results.Add(new CommandPaletteItemViewModel(item));

        if (Results.Count > 0) Results[0].IsSelected = true;

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowNoResults));
    }

    private static IEnumerable<CommandPaletteItem> Filter(IReadOnlyList<CommandPaletteItem> items, string query)
    {
        var trimmed = query.Trim();
        if (string.IsNullOrEmpty(trimmed)) return items;

        return items
            .Select(i => new { Item = i, Score = Score(i, trimmed) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item);
    }

    private static int Score(CommandPaletteItem item, string query) => TitleFuzzyScorer.ScoreTitle(item.Title, query);
}
