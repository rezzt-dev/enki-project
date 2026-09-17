using System.Collections.Generic;

namespace enki.Models;

/// <summary>Settings scoped to a single vault, persisted at <c>&lt;vault&gt;/.enki/settings.json</c> —
/// unlike <see cref="AppSettings"/> (one global file shared by every vault the user ever opens), fields
/// here travel with the vault itself, so opening a different vault sees its own values instead of
/// whatever the last-open vault left behind. <see cref="UiZoomPercent"/> is the first field; future
/// per-vault preferences grow this class the same way <see cref="AppSettings"/> already grows.</summary>
public sealed class VaultSettings
{
    /// <summary>Repairs any JSON-default garbage left by System.Text.Json after deserializing a vault
    /// that predates a newer property: value-type fields get <c>default(T)</c> (0 for ints, false for
    /// bools) instead of their C# property initializer, and reference types get <c>null</c>. Called
    /// once by <see cref="Services.VaultSettingsService.Load"/> before the settings ever reach a
    /// ViewModel, so the rest of the app never sees a 0-column grid or a null dictionary.</summary>
    public void Normalize()
    {
        // También por arriba: el editor no deja pasar de MaxGridDimension, pero un settings.json de otra
        // versión (o editado a mano) sí puede traer un número mayor, y una rejilla de 40 columnas no la
        // rechazaría nadie más — solo se vería un Dashboard de fichas ilegibles.
        DashboardGridRows = DashboardGridRows < 1
            ? Services.DashboardLayoutEngine.DefaultRows
            : Services.DashboardLayoutEngine.ClampDimension(DashboardGridRows);
        DashboardGridColumns = DashboardGridColumns < 1
            ? Services.DashboardLayoutEngine.DefaultColumns
            : Services.DashboardLayoutEngine.ClampDimension(DashboardGridColumns);
        DashboardCustomLayout ??= [];
        DashboardHiddenSections ??= [];
        DashboardSectionOrder ??= [];
        DashboardLayoutStyle ??= Services.DashboardLayoutEngine.Classic;
    }
    /// <summary>Global UI zoom (docs/funcionalidad-zoom-interfaz.md), applied as a
    /// <c>LayoutTransformControl</c> scale over the whole shell. Range enforced by
    /// <c>ViewModels.MainWindowViewModel</c> (50–200, steps of 10); 100 is "no zoom", the size the app
    /// already shipped at.</summary>
    public int UiZoomPercent { get; set; } = 100;

    /// <summary>Último instante sincronizado con Polar para este vault. Cada vault necesita su
    /// propio cursor para descargar los cambios que aún no tiene.</summary>
    public long PolarLastSyncAtUtcMs { get; set; }

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
    /// or one of the grid-based arrangements ("InvertedPyramid", "Lateral", "ModularGrid", "Mosaic",
    /// "Custom"). <see cref="DashboardSectionOrder"/>/<see cref="DashboardHiddenSections"/> still
    /// decide which sections appear and in what priority; this only decides the geometry they're poured
    /// into. When set to "Custom", <see cref="DashboardCustomLayout"/> supplies each section's exact
    /// row, column and span instead of the template engine.</summary>
    public string DashboardLayoutStyle { get; set; } = "Classic";

    /// <summary>Per-section grid positions when <see cref="DashboardLayoutStyle"/> is "Custom". Each
    /// entry maps a section key (e.g. "QuickAccess") to [Row, Column, RowSpan, ColumnSpan]. The layout
    /// engine reads this directly instead of running the template slot assignment — the user has placed
    /// every tile by hand via the interactive preview grid in Ajustes › Dashboard. Missing sections get a
    /// full-width row at the bottom (same belt-and-braces fallback as the template engine).</summary>
    public Dictionary<string, int[]> DashboardCustomLayout { get; set; } = [];

    /// <summary>Number of rows in the custom dashboard grid (1–6).</summary>
    public int DashboardGridRows { get; set; } = 4;

    /// <summary>Number of columns in the custom dashboard grid (1–6).</summary>
    public int DashboardGridColumns { get; set; } = 6;
}
