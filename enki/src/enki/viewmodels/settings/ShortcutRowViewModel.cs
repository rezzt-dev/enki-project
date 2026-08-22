using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;

namespace enki.ViewModels;

/// <summary>One row of Ajustes › Atajos (propuesta 29, docs/propuesta-nuevas-funcionalidades.md) — wraps a
/// <c>Services.ShortcutDefinition</c> with the bindable state its capture control needs (current gesture
/// text, whether it's mid-capture, a pending conflict). Kept as a plain data holder, same convention as
/// <c>CommandPaletteItemViewModel</c>: the actual remap/reset logic lives on <see cref="SettingsViewModel"/>,
/// invoked from <c>SettingsView.axaml.cs</c>'s key-capture handlers or from a bound
/// <c>$parent[ItemsControl].DataContext</c> command with this row as its parameter.</summary>
public sealed partial class ShortcutRowViewModel : ObservableObject
{
    public string Id { get; }
    private readonly string _labelKey;

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private string _gestureDisplay;

    [ObservableProperty]
    private bool _isCustomized;

    [ObservableProperty]
    private bool _isCapturing;

    /// <summary>Set while a just-captured gesture collides with another shortcut and is waiting for the
    /// user to either confirm it anyway or cancel — see <see cref="SettingsViewModel.ApplyCapturedGesture"/>.
    /// Always paired with a non-null <see cref="PendingGesture"/>; the confirm/cancel buttons in
    /// SettingsView.axaml bind their visibility to this being non-null.</summary>
    [ObservableProperty]
    private string? _conflictMessage;

    /// <summary>Transient validation hint shown while capturing (e.g. "must include Ctrl/Alt/Cmd") — unlike
    /// <see cref="ConflictMessage"/>, this never has a pending gesture behind it and never shows a
    /// confirm/cancel pair; the row just stays in capture mode so the user can try another combination.</summary>
    [ObservableProperty]
    private string? _captureHintMessage;

    /// <summary>The gesture that produced <see cref="ConflictMessage"/>, applied only if the user confirms
    /// "usar de todas formas". Not observable — nothing in the view binds to it directly.</summary>
    public KeyGesture? PendingGesture { get; set; }

    public ShortcutRowViewModel(string id, string labelKey, string gestureDisplay, bool isCustomized)
    {
        Id = id;
        _labelKey = labelKey;
        _label = LocalizationService.Instance[labelKey];
        _gestureDisplay = gestureDisplay;
        _isCustomized = isCustomized;
    }

    public void RefreshLabel() => Label = LocalizationService.Instance[_labelKey];
}
