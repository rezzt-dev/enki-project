using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Single row in Ajustes › General › Secciones de la barra lateral: a sidebar section that can be
/// shown/hidden, reordered via drag & drop (or the up/down buttons), and moved between the main scrollable
/// group and the fixed bottom group (max <see cref="SidebarSectionCatalog.MaxBottomSections"/>, alongside
/// Ajustes) — either by dragging it into the other list or via its own pin/unpin button. Same shape as
/// <see cref="DashboardSectionConfigViewModel"/>, minus the live-preview geometry that page needs and this
/// one doesn't.</summary>
public sealed partial class SidebarSectionConfigViewModel : ObservableObject
{
    /// <summary>Persistence key shared with <see cref="Models.AppSettings.SidebarMainSectionOrder"/>/
    /// <see cref="Models.AppSettings.SidebarBottomSectionOrder"/> — a <see cref="AppPage"/> name.</summary>
    public required string Key { get; init; }

    /// <summary>Localized display name.</summary>
    public required string Title { get; init; }

    /// <summary>Same glyph the real sidebar button uses for this section.</summary>
    public required MaterialIconKind Icon { get; init; }

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isDropTarget;

    /// <summary>1-based rank within whichever of <see cref="SettingsViewModel.MainSidebarSections"/>/
    /// <see cref="SettingsViewModel.BottomSidebarSections"/> currently holds this row, kept in sync after
    /// every reorder/rebuild/move — drives the "01/02/…" index badge and whether the up/down buttons are
    /// enabled at the ends of its own list.</summary>
    [ObservableProperty]
    private int _position;

    [ObservableProperty]
    private bool _isFirst;

    [ObservableProperty]
    private bool _isLast;
}
