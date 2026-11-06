using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace enki.ViewModels;

/// <summary>Estado de un chip individual del filtro de etiquetas. El comando
/// <see cref="ToggleCommand"/> invierte <see cref="IsSelected"/> y notifica al
/// <see cref="TagFilterViewModel"/> padre a través del callback recibido en el constructor.</summary>
public sealed partial class TagFilterItemViewModel : ObservableObject
{
    private readonly Action<TagFilterItemViewModel>? _onToggled;

    [ObservableProperty]
    private string _tag = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    public TagFilterItemViewModel(string tag, Action<TagFilterItemViewModel>? onToggled = null)
    {
        _tag = tag;
        _onToggled = onToggled;
    }

    [RelayCommand]
    private void Toggle()
    {
        IsSelected = !IsSelected;
        _onToggled?.Invoke(this);
    }
}
