using CommunityToolkit.Mvvm.ComponentModel;
using enki.Models;

namespace enki.ViewModels;

public sealed partial class EditorOutlineItemViewModel : ObservableObject
{
    public EditorOutlineItem Item { get; }

    public int Level => Item.Level;
    public string Title => Item.Title;

    [ObservableProperty]
    private bool _isActive;

    public EditorOutlineItemViewModel(EditorOutlineItem item)
    {
        Item = item;
    }
}
