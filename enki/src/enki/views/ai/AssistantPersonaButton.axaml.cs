using Avalonia.Controls;

namespace enki.Views;

/// <summary>Botón de personalidad de la cabecera del asistente con su menú. Sin lógica propia: todo vive en
/// <see cref="ViewModels.AssistantViewModel"/> (parcial <c>.Persona</c>).</summary>
public partial class AssistantPersonaButton : UserControl
{
    public AssistantPersonaButton() => InitializeComponent();
}
