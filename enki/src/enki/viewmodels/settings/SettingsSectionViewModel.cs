using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;

namespace enki.ViewModels;

public sealed partial class SettingsSectionViewModel : ObservableObject
{
    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _groupLabel = string.Empty;

    public required string GroupKey { get; init; }
    public bool StartsGroup { get; init; }

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
    Dashboard,
    About,
    Navigation,
    Vault,
    Backups,
    Kanban,
    Maintenance,
    Ai
}
