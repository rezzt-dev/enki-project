using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>One icon offered by the list editor's icon picker — same precomputed-selection approach as
/// <see cref="ReminderIconOptionViewModel"/>. <see cref="Key"/> is the opaque Polar string
/// (docs/supabase-sync/01, e.g. <c>"ic_folder"</c>) that actually gets persisted/synced;
/// <see cref="IconKind"/> is only for display.</summary>
public sealed partial class PolarIconOptionViewModel : ObservableObject
{
    public string Key { get; }
    public MaterialIconKind IconKind { get; }

    [ObservableProperty]
    private bool _isSelected;

    public PolarIconOptionViewModel(string key, bool isSelected)
    {
        Key = key;
        IconKind = PolarIconMap.Resolve(key);
        _isSelected = isSelected;
    }
}
