using System.Collections.Generic;
using System.Collections.ObjectModel;
using enki.Models;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>One list section in the "Gestión de tareas" home feed (see
/// <see cref="PolarTaskManagementViewModel.HomeGroups"/>) — groups a list's pending tasks under its own
/// title, icon and color, mirroring how Polar's mobile home screen stacks every space's to-dos into a
/// single scrollable overview instead of requiring the sidebar-driven "select one list at a time" flow
/// the "Listas" view already offers.</summary>
public sealed class PolarHomeGroupViewModel
{
    public string Id { get; }
    public string Title { get; }
    public MaterialIconKind IconKind { get; }
    public string Color { get; }
    public ObservableCollection<PolarTaskRowViewModel> Tasks { get; }

    public PolarHomeGroupViewModel(PolarTaskList list, IEnumerable<PolarTaskRowViewModel> tasks)
    {
        Id = list.Id;
        Title = list.Title;
        IconKind = PolarIconMap.Resolve(list.Icon);
        Color = list.Color;
        Tasks = new ObservableCollection<PolarTaskRowViewModel>(tasks);
    }
}
