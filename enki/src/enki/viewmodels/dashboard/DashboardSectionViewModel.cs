using CommunityToolkit.Mvvm.ComponentModel;

namespace enki.ViewModels;

/// <summary>Base class for the configurable sections shown on the Dashboard. The concrete subtypes
/// exist only so <see cref="Views.DashboardView"/> can select a different XAML template per section
/// through Avalonia's typed <c>DataTemplate</c> mechanism.</summary>
public abstract partial class DashboardSectionViewModel : ObservableObject
{
    /// <summary>Stable key used for persistence and reordering — matches the values stored in
    /// <see cref="Models.VaultSettings.DashboardSectionOrder"/>.</summary>
    public required string Key { get; init; }

    /// <summary>Localized title shown in the settings list and, where applicable, the section header.
    /// Resolved from <see cref="LocalizationService"/> when the section is built.</summary>
    public required string Title { get; init; }

    [ObservableProperty]
    private bool _isVisible = true;

    // ===================== Organización del panel (Grid.Row/Column/RowSpan/ColumnSpan) =====================
    // Set by DashboardViewModel.RebuildSections() from Services.DashboardLayoutEngine.Compute() — bound
    // in DashboardView.axaml's ItemsControl.ItemContainerTheme (targeting ContentPresenter) rather than
    // read here, since these only affect layout once the section is placed inside the Dashboard's own
    // Grid ItemsPanel.

    [ObservableProperty]
    private int _gridRow;

    [ObservableProperty]
    private int _gridColumn;

    [ObservableProperty]
    private int _gridRowSpan = 1;

    [ObservableProperty]
    private int _gridColumnSpan = Services.DashboardLayoutEngine.DefaultColumns;
}

public sealed partial class QuickAccessSectionViewModel : DashboardSectionViewModel;
public sealed partial class TodaySectionViewModel : DashboardSectionViewModel;
public sealed partial class VaultSectionViewModel : DashboardSectionViewModel;
public sealed partial class ActivitySectionViewModel : DashboardSectionViewModel;
public sealed partial class SessionsSectionViewModel : DashboardSectionViewModel;
public sealed partial class BoardTasksSectionViewModel : DashboardSectionViewModel;
public sealed partial class RecentNotesSectionViewModel : DashboardSectionViewModel;
public sealed partial class HabitsSectionViewModel : DashboardSectionViewModel;
