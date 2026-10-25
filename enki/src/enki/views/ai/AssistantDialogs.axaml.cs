using Avalonia.Controls;

namespace enki.Views;

/// <summary>Diálogos del asistente (borrar un chat, confirmar el envío, añadir a la nota activa). Sin lógica
/// propia: todo vive en <see cref="ViewModels.AssistantViewModel"/>.</summary>
public partial class AssistantDialogs : UserControl
{
    public AssistantDialogs() => InitializeComponent();
}
