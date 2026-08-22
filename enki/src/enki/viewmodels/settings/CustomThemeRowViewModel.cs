using CommunityToolkit.Mvvm.ComponentModel;

namespace enki.ViewModels;

/// <summary>One row of Ajustes › Apariencia › Tema personalizado — wraps a
/// <c>Models.CustomThemeInfo</c> with whether it's the currently-active theme, so the view can bind an
/// "Activo" pill/disabled state without recomputing it in a converter on every render. Rebuilt by
/// <c>SettingsViewModel.RefreshCustomThemes</c> whenever the underlying list or the active theme changes —
/// same "plain data holder" convention as <see cref="ShortcutRowViewModel"/>.</summary>
public sealed partial class CustomThemeRowViewModel : ObservableObject
{
    public string Id { get; }
    public string Name { get; }

    [ObservableProperty]
    private bool _isActive;

    public CustomThemeRowViewModel(string id, string name, bool isActive)
    {
        Id = id;
        Name = name;
        _isActive = isActive;
    }
}
