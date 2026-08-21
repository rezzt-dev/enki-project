using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;

namespace enki.ViewModels;

public sealed partial class SettingsSectionViewModel : ObservableObject
{
    [ObservableProperty]
    private string _label = string.Empty;

    public required MaterialIconKind Icon { get; init; }
    public required SettingsSection Section { get; init; }

    [ObservableProperty]
    private bool _isActive;
}

public enum SettingsSection
{
    General,
    Appearance,
    Editor,
    Notifications,
    Calendar,
    Account,
    Skills,
    Shortcuts,
    About
}
