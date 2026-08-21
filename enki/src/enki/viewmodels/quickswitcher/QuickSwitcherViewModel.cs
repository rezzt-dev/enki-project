using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Buscador rápido de notas al estilo Obsidian/VS Code: Ctrl+P, tipear, Enter para abrir.</summary>
public sealed partial class QuickSwitcherViewModel : ObservableObject
{
    private readonly VaultService _vaultService;
    private readonly Action<NoteFile> _onOpenNote;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private int _selectedIndex;

    public ObservableCollection<QuickSwitcherItemViewModel> Results { get; } = [];

    public bool HasResults => Results.Count > 0;
    public bool ShowPrompt => string.IsNullOrWhiteSpace(SearchText) && Results.Count == 0;

    public QuickSwitcherViewModel(VaultService vaultService, Action<NoteFile> onOpenNote)
    {
        _vaultService = vaultService;
        _onOpenNote = onOpenNote;
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
        var note = Results[Math.Clamp(SelectedIndex, 0, Results.Count - 1)].Note;
        IsOpen = false;
        _onOpenNote(note);
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
    private void OpenNote(QuickSwitcherItemViewModel item)
    {
        IsOpen = false;
        _onOpenNote(item.Note);
    }

    private void RefreshResults()
    {
        Results.Clear();
        SelectedIndex = 0;

        var all = _vaultService.GetAllNotesRecursive(_vaultService.VaultPath);
        var filtered = FilterNotes(all, SearchText);
        foreach (var note in filtered.Take(20)) Results.Add(new QuickSwitcherItemViewModel(note));

        if (Results.Count > 0) Results[0].IsSelected = true;

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowPrompt));
    }

    private static IEnumerable<NoteFile> FilterNotes(IEnumerable<NoteFile> notes, string query)
    {
        var trimmed = query.Trim();
        if (string.IsNullOrEmpty(trimmed)) return notes.OrderByDescending(n => n.ModifiedUtc);

        return notes
            .Select(n => new { Note = n, Score = Score(n, trimmed) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Note.ModifiedUtc)
            .Select(x => x.Note);
    }

    private static int Score(NoteFile note, string query)
    {
        var title = note.Title;
        if (title.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 500;
        if (title.Contains(query, StringComparison.OrdinalIgnoreCase)) return 250;
        if (note.Preview.Contains(query, StringComparison.OrdinalIgnoreCase)) return 100;
        if (note.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase))) return 50;
        return 0;
    }
}
