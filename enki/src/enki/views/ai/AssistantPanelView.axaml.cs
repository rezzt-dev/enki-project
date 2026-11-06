using Avalonia.Controls;
using Avalonia.Input;

namespace enki.Views;

/// <summary>Panel lateral del asistente. Solo mecánica de vista: Escape con el historial desplegado lo pliega y
/// devuelve el foco a su botón. Todo lo demás vive en <see cref="ViewModels.AssistantViewModel"/> y en los
/// controles compartidos con la sección.</summary>
public partial class AssistantPanelView : UserControl
{
    public AssistantPanelView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || DataContext is not ViewModels.AssistantViewModel { IsPanelHistoryOpen: true } vm) return;
            vm.IsPanelHistoryOpen = false;
            PanelHistoryToggle.Focus(NavigationMethod.Tab);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }
}
