using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

public sealed partial class SessionListItemViewModel : ObservableObject
{
    public Session Session { get; private set; }

    public string Name => Session.Name;
    public int NoteCount => Session.NoteCount;
    public string NoteCountLabel => Plural.Get("Sessions_NoteCount", NoteCount, NoteCount);
    public bool IsDefault => Session.IsDefault;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _editingName = string.Empty;

    public SessionListItemViewModel(Session session)
    {
        Session = session;
    }

    public void UpdateSession(Session session)
    {
        Session = session;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(NoteCount));
        OnPropertyChanged(nameof(NoteCountLabel));
        OnPropertyChanged(nameof(IsDefault));
    }
}
