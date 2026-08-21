namespace enki.Models;

/// <summary>Settings scoped to a single vault, persisted at <c>&lt;vault&gt;/.enki/settings.json</c> —
/// unlike <see cref="AppSettings"/> (one global file shared by every vault the user ever opens), fields
/// here travel with the vault itself, so opening a different vault sees its own values instead of
/// whatever the last-open vault left behind. <see cref="UiZoomPercent"/> is the first field; future
/// per-vault preferences grow this class the same way <see cref="AppSettings"/> already grows.</summary>
public sealed class VaultSettings
{
    /// <summary>Global UI zoom (docs/funcionalidad-zoom-interfaz.md), applied as a
    /// <c>LayoutTransformControl</c> scale over the whole shell. Range enforced by
    /// <c>ViewModels.MainWindowViewModel</c> (50–200, steps of 10); 100 is "no zoom", the size the app
    /// already shipped at.</summary>
    public int UiZoomPercent { get; set; } = 100;
}
