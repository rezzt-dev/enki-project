using CommunityToolkit.Mvvm.ComponentModel;
using enki.Models;

namespace enki.ViewModels;

public sealed partial class QuickSwitcherItemViewModel : ObservableObject
{
    public NoteFile Note { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string Title => Note.Title;
    public string Preview => Note.Preview;

    public QuickSwitcherItemViewModel(NoteFile note)
    {
        Note = note;
    }
}
