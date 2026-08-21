using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace enki.ViewModels;

/// <summary>One row of the sidebar "capas" panel (docs/calendar-feature/07-filtros-busqueda-y-capas.md
/// §1): a single Kanban/Recordatorios toggle, or one per <c>PolarTaskList</c>. <see cref="Key"/> matches
/// <see cref="CalendarItemViewModel.LayerKey"/> so <see cref="CalendarViewModel"/> can filter by simple set
/// membership. Toggling calls back into <see cref="CalendarViewModel"/> (rather than the view model
/// listening to its own <c>PropertyChanged</c>) so persistence to <c>AppSettings.CalendarHiddenLayerKeys</c>
/// and the resulting rebuild both happen from one place, same shape as <c>CalendarWeekColumnViewModel</c>'s
/// constructor-supplied dependencies elsewhere in this feature.</summary>
public sealed partial class CalendarLayerViewModel : ObservableObject
{
    private readonly Action<CalendarLayerViewModel> _onToggled;

    public string Key { get; }
    public string Label { get; }
    public string ColorHex { get; }

    [ObservableProperty]
    private bool _isActive;

    public CalendarLayerViewModel(string key, string label, string colorHex, bool isActive, Action<CalendarLayerViewModel> onToggled)
    {
        Key = key;
        Label = label;
        ColorHex = colorHex;
        _isActive = isActive;
        _onToggled = onToggled;
    }

    partial void OnIsActiveChanged(bool value) => _onToggled(this);
}
