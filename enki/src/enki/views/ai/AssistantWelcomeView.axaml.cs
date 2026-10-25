using Avalonia.Controls;

namespace enki.Views;

/// <summary>Bienvenida de un chat vacío. Sin mecánica propia: la sección coloca su compositor en
/// <see cref="Slot"/> mientras el chat está vacío; en el panel el hueco queda vacío.</summary>
public partial class AssistantWelcomeView : UserControl
{
    public AssistantWelcomeView() => InitializeComponent();

    /// <summary>Hueco entre la cabecera y las sugerencias donde la sección centra el compositor.</summary>
    public Decorator Slot => ComposerSlot;
}
