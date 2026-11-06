using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace enki.ViewModels;

/// <summary>One row of Ajustes › Apariencia › Tema personalizado — wraps either a
/// <c>Models.CustomThemeInfo</c> (user-imported) or a <c>Models.BuiltInThemeDefinition</c> (ships with
/// Enki, <see cref="IsBuiltIn"/>) with whether it's the currently-active theme, so the view can bind an
/// "Activo" pill/disabled state without recomputing it in a converter on every render. Rebuilt by
/// <c>SettingsViewModel.RefreshCustomThemes</c> whenever the underlying list or the active theme changes —
/// same "plain data holder" convention as <see cref="ShortcutRowViewModel"/>.</summary>
public sealed partial class CustomThemeRowViewModel : ObservableObject
{
    public string Id { get; }
    public string Name { get; }

    /// <summary>Whether this row is a compiled-in theme (<see cref="Services.BuiltInThemeCatalog"/>) —
    /// those can be activated and duplicated but never edited, renamed or deleted, so the view hides those
    /// actions when this is <c>true</c>.</summary>
    public bool IsBuiltIn { get; }

    /// <summary>Two-tone swatch (background + accent) shown only for built-in rows — a user's own themes
    /// skip this to avoid reading every theme's <c>.css</c> file off disk on every list refresh just to
    /// paint a preview. <c>null</c> when there's nothing to preview.</summary>
    public IBrush? PreviewBackgroundBrush { get; }

    public IBrush? PreviewAccentBrush { get; }

    public bool HasPreview => PreviewBackgroundBrush is not null;

    [ObservableProperty]
    private bool _isActive;

    public CustomThemeRowViewModel(string id, string name, bool isActive, bool isBuiltIn = false,
        string? previewBackgroundHex = null, string? previewAccentHex = null)
    {
        Id = id;
        Name = name;
        _isActive = isActive;
        IsBuiltIn = isBuiltIn;
        PreviewBackgroundBrush = previewBackgroundHex is not null ? new SolidColorBrush(Color.Parse(previewBackgroundHex)) : null;
        PreviewAccentBrush = previewAccentHex is not null ? new SolidColorBrush(Color.Parse(previewAccentHex)) : null;
    }
}
