using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Single row in Ajustes › Dashboard: a dashboard section that can be shown/hidden and
/// reordered via drag & drop (or the up/down buttons). Also doubles as the item bound by the live
/// layout preview next to the list — same order, same visibility, same icon/title.</summary>
public sealed partial class DashboardSectionConfigViewModel : ObservableObject
{
    /// <summary>Persistence key shared with <see cref="Models.VaultSettings.DashboardSectionOrder"/>.</summary>
    public required string Key { get; init; }

    /// <summary>Localized display name.</summary>
    public required string Title { get; init; }

    /// <summary>Short localized blurb shown under the title — what this section actually shows on the
    /// Dashboard, so a user picks sections by what they contain rather than by name alone.</summary>
    public required string Description { get; init; }

    /// <summary>Same glyph the section header uses on the real Dashboard (<see cref="Views.DashboardView"/>),
    /// kept here purely for display — this row never renders anything conditionally on it.</summary>
    public required MaterialIconKind Icon { get; init; }

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isDropTarget;

    /// <summary>1-based rank within <see cref="SettingsViewModel.DashboardSections"/>, kept in sync by
    /// <see cref="SettingsViewModel"/> after every reorder/rebuild — drives the "01/02/…" index badge
    /// and whether the up/down buttons are enabled at the ends of the list.</summary>
    [ObservableProperty]
    private int _position;

    [ObservableProperty]
    private bool _isFirst;

    [ObservableProperty]
    private bool _isLast;

    // ===================== Vista previa: geometría según la organización elegida =====================
    // Calculadas por SettingsViewModel.RefreshDashboardSummary() vía Services.DashboardLayoutEngine —
    // mismos campos y mismo propósito que DashboardSectionViewModel en el Dashboard real, para que la
    // vista previa reproduzca exactamente la organización que se aplicará.

    [ObservableProperty]
    private int _gridRow;

    [ObservableProperty]
    private int _gridColumn;

    [ObservableProperty]
    private int _gridRowSpan = 1;

    [ObservableProperty]
    private int _gridColumnSpan = Services.DashboardLayoutEngine.DefaultColumns;

    // ===================== Editor de rejilla (modo Personalizado) =====================

    /// <summary>Ficha seleccionada en el editor: la que muestra los controles de mover/redimensionar y la
    /// que responde al teclado. Una sola a la vez, la mantiene <see cref="SettingsViewModel"/>.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Ficha que se está arrastrando ahora mismo, para atenuarla mientras su silueta de destino
    /// viaja por la rejilla.</summary>
    [ObservableProperty]
    private bool _isDragging;

    /// <summary>"3 × 2" (columnas × filas) — el tamaño de la ficha tal cual lo entiende el usuario, en
    /// celdas. Se muestra en la propia ficha del editor para poder ajustar tamaños sin contar celdas a
    /// ojo, y es la etiqueta que leen los lectores de pantalla junto al título.</summary>
    public string SizeLabel => $"{GridColumnSpan} × {GridRowSpan}";

    partial void OnGridColumnSpanChanged(int value) => OnPropertyChanged(nameof(SizeLabel));

    partial void OnGridRowSpanChanged(int value) => OnPropertyChanged(nameof(SizeLabel));
}

/// <summary>Una celda vacía del lienzo del editor de rejilla: solo geometría, sin estado propio. El
/// editor pinta una capa de estas por debajo de las fichas para que la rejilla se vea aunque esté medio
/// vacía — sin ellas, arrastrar una ficha a una zona libre sería apuntar a un espacio en blanco.</summary>
public sealed class DashboardGridCellViewModel
{
    public required int Row { get; init; }

    public required int Column { get; init; }
}
