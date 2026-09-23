using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;

namespace enki.ViewModels;

public sealed partial class CommandPaletteItemViewModel : ObservableObject
{
    public CommandPaletteItem Item { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string Title => Item.Title;
    public MaterialIconKind Icon => Item.Icon;

    public CommandPaletteItemViewModel(CommandPaletteItem item)
    {
        Item = item;
    }
}
