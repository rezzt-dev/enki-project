using Avalonia.Controls;

namespace enki.Views;

/// <summary>Selector de modelo y de capacidad del chat. Sin lógica propia: todo vive en
/// <see cref="ViewModels.AssistantViewModel"/>.</summary>
public partial class AssistantModelControls : UserControl
{
    public AssistantModelControls() => InitializeComponent();
}
