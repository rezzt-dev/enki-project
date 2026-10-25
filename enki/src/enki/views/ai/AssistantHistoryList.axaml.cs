using Avalonia.Controls;
using Avalonia.Input;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Historial de conversaciones del asistente. Solo mecánica de vista: abrir un chat al pulsar su fila
/// (los botones de la fila y el menú contextual tienen sus propios comandos).</summary>
public partial class AssistantHistoryList : UserControl
{
    public AssistantHistoryList() => InitializeComponent();

    private void SessionRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: AiChatSessionItemViewModel item } control) return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        (DataContext as AssistantViewModel)?.SelectSessionCommand.Execute(item);
    }
}
