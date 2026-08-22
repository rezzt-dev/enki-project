using System.Collections.Generic;

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

    // ===================== Dashboard configurable (propuesta 27) =====================

    /// <summary>Ordered list of dashboard section keys (e.g. "QuickAccess", "Today", "Vault",
    /// "Activity", "Sessions", "BoardTasks", "RecentNotes"). Missing sections are appended at the end
    /// in their default order; unknown keys are ignored. An empty list means "use default order".</summary>
    public List<string> DashboardSectionOrder { get; set; } = [];

    /// <summary>Keys of dashboard sections the user has hidden. Hidden sections are skipped when
    /// building the dashboard; an empty list means "show all".</summary>
    public List<string> DashboardHiddenSections { get; set; } = [];

    /// <summary>Which of the named layout templates (<c>Services.DashboardLayoutEngine</c>) arranges the
    /// visible sections on screen — "Classic" (one full-width row per section, the original behaviour)
    /// or one of the grid-based arrangements ("InvertedPyramid", "Lateral", "ModularGrid", "Mosaic").
    /// <see cref="DashboardSectionOrder"/>/<see cref="DashboardHiddenSections"/> still decide which
    /// sections appear and in what priority; this only decides the geometry they're poured into.</summary>
    public string DashboardLayoutStyle { get; set; } = "Classic";
}
