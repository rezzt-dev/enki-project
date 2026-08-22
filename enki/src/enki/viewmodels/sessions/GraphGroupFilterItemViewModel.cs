using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace enki.ViewModels;

/// <summary>One row of the graph's group-filter popover — a single session, or the vault-root pseudo-group
/// (<see cref="Models.SessionGraph.RootGroupId"/>) — letting the user isolate the global vault graph down
/// to a subset of groups (e.g. only "sin sesión", or exactly two sessions to see just what connects them).
/// Toggling calls back into <see cref="SessionsViewModel"/> (rather than the view model listening to its
/// own <c>PropertyChanged</c>) so the graph re-render happens from one place, same shape as
/// <c>CalendarLayerViewModel</c>'s constructor-supplied callback elsewhere in the app.</summary>
public sealed partial class GraphGroupFilterItemViewModel : ObservableObject
{
    private readonly Action _onToggled;

    public string GroupId { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isActive;

    public GraphGroupFilterItemViewModel(string groupId, string label, bool isActive, Action onToggled)
    {
        GroupId = groupId;
        Label = label;
        _isActive = isActive;
        _onToggled = onToggled;
    }

    partial void OnIsActiveChanged(bool value) => _onToggled();
}
