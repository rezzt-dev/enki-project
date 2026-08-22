using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;

namespace enki.ViewModels;

public sealed partial class NavItemViewModel : ObservableObject
{
    public required string Label { get; init; }
    public required MaterialIconKind Icon { get; init; }
    public required AppPage Page { get; init; }

    [ObservableProperty]
    private bool _isActive;

    /// <summary>Optional badge count — only
    /// "Papelera" and "Calendario" ever set this away from 0 (see MainWindowViewModel.UpdateTrashBadge/
    /// UpdateCalendarBadge); every other entry simply never shows a badge.</summary>
    [ObservableProperty]
    private int _count;

    public bool HasCount => Count > 0;

    /// <summary>True para la entrada "Papelera", que se pinta con el color de peligro del tema.</summary>
    public bool IsDanger => Page == AppPage.Trash;

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(HasCount));
}

public enum AppPage
{
    Dashboard,
    Notes,
    Sessions,
    Kanban,
    Trash,
    Settings,
    Reminders,
    Notifications,
    Calendar,
    Files,
    Skills,
    Habits,
    Statistics
}
