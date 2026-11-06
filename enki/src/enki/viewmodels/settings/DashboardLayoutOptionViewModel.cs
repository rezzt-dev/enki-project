using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace enki.ViewModels;

/// <summary>One selectable card in Ajustes › Dashboard › Organización — a named layout template
/// (<see cref="Services.DashboardLayoutEngine"/>) plus a miniature diagram of its slot geometry
/// (<see cref="Blocks"/>), so the user can recognize the shape at a glance instead of guessing from the
/// name alone.</summary>
public sealed partial class DashboardLayoutOptionViewModel : ObservableObject
{
    /// <summary>One of <see cref="Services.DashboardLayoutEngine.AllStyles"/>.</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public string SearchText => Title + " " + Description;

    /// <summary>The layout's slot geometry against a representative full set of 7 sections, rendered as a
    /// tiny 6-column grid diagram on the option card.</summary>
    public required ObservableCollection<DashboardLayoutBlockViewModel> Blocks { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>True when this layout option has a thumbnail diagram to render (i.e. it's not the
    /// Custom option, which has no pre-defined geometry and instead shows a grid-icon placeholder).</summary>
    public bool HasBlocks => Blocks.Count > 0;
}

/// <summary>One rectangle in a <see cref="DashboardLayoutOptionViewModel"/>'s thumbnail diagram.</summary>
public sealed class DashboardLayoutBlockViewModel
{
    public required int GridRow { get; init; }
    public required int GridColumn { get; init; }
    public required int GridRowSpan { get; init; }
    public required int GridColumnSpan { get; init; }

    /// <summary>Wide slots are drawn a shade darker in the thumbnail so the diagram itself hints at which
    /// tiles are the "hero" content, without needing a legend.</summary>
    public required bool IsWide { get; init; }
}
