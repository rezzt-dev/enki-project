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
    private readonly NoteSearchService _noteSearchService;
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

    public QuickSwitcherViewModel(VaultService vaultService, NoteSearchService noteSearchService, Action<NoteFile> onOpenNote)
    {
        _vaultService = vaultService;
        _noteSearchService = noteSearchService;
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

    /// <summary>Sin consulta, las notas más recientes del vault entero; con consulta, el conjunto que
    /// devuelve <see cref="NoteSearchService"/> reordenado por el scoring difuso de título que este
    /// switcher ya usaba.
    ///
    /// <para>La lista de partida es ahora <see cref="VaultService.GetAllNotesAcrossVault"/> y no
    /// <c>GetAllNotesRecursive(VaultPath)</c>: aquel recorrido excluye <c>.sessions/</c>, así que las notas
    /// guardadas dentro de una sesión nunca aparecían en Ctrl+P. Y el filtro por contenido ya no mira
    /// <see cref="NoteFile.Preview"/> (160 caracteres) sino el cuerpo completo de cada nota.</para></summary>
    private void RefreshResults()
    {
        Results.Clear();
        SelectedIndex = 0;

        var trimmed = SearchText.Trim();
        var filtered = string.IsNullOrEmpty(trimmed)
            ? _vaultService.GetAllNotesAcrossVault().OrderByDescending(n => n.ModifiedUtc)
            : RankHits(_noteSearchService.Search(trimmed, maxResults: 200), trimmed);

        foreach (var note in filtered.Take(20)) Results.Add(new QuickSwitcherItemViewModel(note));

        if (Results.Count > 0) Results[0].IsSelected = true;

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowPrompt));
    }

    /// <summary>Un acierto de título mantiene el peso difuso original (exacto &gt; empieza por &gt; contiene);
    /// uno de etiqueta o de contenido queda por debajo de cualquiera de ellos, con el contenido último —
    /// Ctrl+P sigue siendo, ante todo, un salto por nombre de nota.</summary>
    private static IEnumerable<NoteFile> RankHits(IEnumerable<NoteSearchHit> hits, string query) =>
        hits
            .Select(hit => new { hit.Note, Score = ScoreHit(hit, query) })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Note.ModifiedUtc)
            .Select(x => x.Note);

    private static int ScoreHit(NoteSearchHit hit, string query) => hit.Field switch
    {
        NoteSearchField.Title => TitleFuzzyScorer.ScoreTitle(hit.Note.Title, query),
        NoteSearchField.Tag => 50,
        _ => 25
    };
}
