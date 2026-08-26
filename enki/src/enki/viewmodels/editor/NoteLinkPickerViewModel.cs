using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace enki.ViewModels;

/// <summary>Backs the "enlazar nota" popover the editor opens when the user types <c>[[</c>, picks the
/// toolbar's note-link button, or runs the <c>/nota</c> slash command (see <c>EditorView.axaml.cs</c>'s
/// <c>GetNoteLinkPickerContext</c>/<c>UpdateNoteLinkPicker</c>) — same shape as <see cref="SlashCommandsViewModel"/>
/// (filter-as-you-type, arrow-key navigation, Enter to confirm), but backed by every note in the vault
/// instead of a fixed command list.</summary>
public sealed partial class NoteLinkPickerViewModel : ObservableObject
{
    private List<NotePickerItemViewModel> _allNotes = [];

    public ObservableCollection<NotePickerItemViewModel> FilteredResults { get; } = [];

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private int _selectedIndex;

    [ObservableProperty]
    private bool _isOpen;

    public NotePickerItemViewModel? SelectedItem => FilteredResults.Count > 0 && SelectedIndex >= 0 && SelectedIndex < FilteredResults.Count
        ? FilteredResults[SelectedIndex]
        : null;

    /// <summary>Raised when the user confirms a note (click, or Enter on the highlighted row) — the view
    /// turns this into replacing the typed <c>[[filter</c> with a closed <c>[[FileName]]</c> wikilink.</summary>
    public event Action<NotePickerItemViewModel>? NoteSelected;

    partial void OnFilterChanged(string value) => Refresh();

    partial void OnSelectedIndexChanged(int value)
    {
        if (SelectedIndex < 0) SelectedIndex = FilteredResults.Count - 1;
        if (SelectedIndex >= FilteredResults.Count) SelectedIndex = 0;
        UpdateSelection();
    }

    /// <summary>Opens the picker against a freshly-collected note set — called once per open (not per
    /// keystroke), same caching rationale as <c>SlashCommandsViewModel</c>'s fixed command list: the vault's
    /// note set only changes from this tab's own actions while the picker is open.</summary>
    public void Open(IEnumerable<NotePickerItemViewModel> allNotes)
    {
        _allNotes = allNotes.ToList();
        // Setting Filter only re-runs Refresh() via OnFilterChanged when the value actually changes (the
        // generated setter no-ops on an unchanged value) — an explicit call covers the "already empty from
        // last time" case too, so _allNotes' new contents always show up regardless of the previous filter.
        Filter = string.Empty;
        Refresh();
        IsOpen = true;
    }

    [RelayCommand]
    private void Select(NotePickerItemViewModel item)
    {
        NoteSelected?.Invoke(item);
        IsOpen = false;
    }

    [RelayCommand]
    private void ConfirmSelected()
    {
        if (SelectedItem is { } item) Select(item);
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (FilteredResults.Count == 0) return;
        SelectedIndex = (SelectedIndex + 1) % FilteredResults.Count;
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (FilteredResults.Count == 0) return;
        SelectedIndex = (SelectedIndex - 1 + FilteredResults.Count) % FilteredResults.Count;
    }

    private void Refresh()
    {
        FilteredResults.Clear();
        SelectedIndex = 0;

        var trimmed = Filter.Trim();
        var matches = string.IsNullOrEmpty(trimmed)
            ? _allNotes
            : _allNotes.Where(n => n.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase));

        foreach (var item in matches.Take(40)) FilteredResults.Add(item);
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        for (var i = 0; i < FilteredResults.Count; i++)
            FilteredResults[i].IsSelected = i == SelectedIndex;
    }
}
