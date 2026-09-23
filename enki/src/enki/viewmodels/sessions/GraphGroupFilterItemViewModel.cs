using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace enki.ViewModels;

/// <summary>Fila reutilizable del filtro del grafo: identifica una sesión, el grupo sin sesión o una
/// etiqueta. El estado y el callback pertenecen al viewmodel; no se modifica la entidad original.</summary>
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
