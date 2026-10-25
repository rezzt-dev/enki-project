using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>One icon offered by the reminder editor's icon picker. Selection state is precomputed here
/// (refreshed by <see cref="ReminderEditorViewModel"/> whenever <c>SelectedIcon</c> changes) rather than
/// worked out in XAML via a converter — same approach <see cref="KanbanCalendarDayViewModel.IsSelected"/>
/// already uses for the date-picker's day cells, since Avalonia's <c>ConverterParameter</c> can't itself be
/// a per-item binding.</summary>
public sealed partial class ReminderIconOptionViewModel : ObservableObject
{
    public MaterialIconKind Icon { get; }

    [ObservableProperty]
    private bool _isSelected;

    public ReminderIconOptionViewModel(MaterialIconKind icon, bool isSelected)
    {
        Icon = icon;
        _isSelected = isSelected;
    }
}
