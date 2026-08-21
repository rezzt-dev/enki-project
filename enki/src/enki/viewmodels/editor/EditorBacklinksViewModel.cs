using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class EditorBacklinksViewModel : ObservableObject
{
    private readonly VaultService _vaultService;
    private readonly Action<NoteFile> _onOpenNote;

    public ObservableCollection<EditorBacklink> Items { get; } = [];

    [ObservableProperty]
    private bool _isEmpty;

    public EditorBacklinksViewModel(NoteFile note, VaultService vaultService, Action<NoteFile> onOpenNote)
    {
        _vaultService = vaultService;
        _onOpenNote = onOpenNote;
        Refresh(note);
    }

    /// <summary>Recomputes <see cref="Items"/> against <paramref name="currentNote"/> — the caller
    /// (<c>ViewModels.EditorViewModel</c>) always passes its own, current <c>Note</c>, since resolution now
    /// matches by path (see <see cref="EditorBacklinksService.FindBacklinks"/>) and a stale path from before
    /// a rename would never match any note the fresh vault scan below finds
    /// (docs/analisis-proyecto/roadmap.md, Fase 5).</summary>
    public void Refresh(NoteFile currentNote)
    {
        var allNotesWithContent = _vaultService.GetAllNotesRecursiveWithContent(_vaultService.VaultPath);
        var backlinks = EditorBacklinksService.FindBacklinks(allNotesWithContent, currentNote);

        Items.Clear();
        foreach (var link in backlinks) Items.Add(link);
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private void Open(EditorBacklink backlink)
    {
        var note = _vaultService.GetNote(backlink.FullPath);
        if (note is not null) _onOpenNote(note);
    }
}
