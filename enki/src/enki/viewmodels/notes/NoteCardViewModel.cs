using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class NoteCardViewModel : ObservableObject
{
    [ObservableProperty] private bool _isCut;

    public NoteFile Note { get; private set; }

    public string Title => Note.Title;
    public string Preview => string.IsNullOrWhiteSpace(Note.Preview) ? LocalizationService.Instance["Notes_EmptyNotePreview"] : Note.Preview;
    public string ModifiedLabel => Note.ModifiedLabel;
    public string WordCountLabel => Plural.Get("Notes_WordCount", Note.WordCount, Note.WordCount);
    public string FileNameLabel => Note.FileNameWithoutExtension;

    public bool HasTags => Note.Tags.Count > 0;
    public string TagsLabel => string.Join(", ", Note.Tags);

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _editingName = string.Empty;

    public NoteCardViewModel(NoteFile note)
    {
        Note = note;
    }

    public void UpdateNote(NoteFile note)
    {
        Note = note;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(ModifiedLabel));
        OnPropertyChanged(nameof(WordCountLabel));
        OnPropertyChanged(nameof(FileNameLabel));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(TagsLabel));
    }

    [RelayCommand]
    private void BeginRename()
    {
        EditingName = Title;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private void RevealInSystem() => FolderOpener.RevealFile(Note.FullPath);
}
