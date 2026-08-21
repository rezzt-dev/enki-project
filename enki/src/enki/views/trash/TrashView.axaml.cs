using Avalonia.Controls;
using Avalonia.Interactivity;

namespace enki.Views;

public partial class TrashView : UserControl
{
    public TrashView()
    {
        InitializeComponent();
    }

    /// <summary>Toma el foco de teclado al abrirse la página para que el atajo Ctrl+Shift+E funcione sin
    /// tener que hacer clic antes en ella — mismo patrón que <c>KanbanView_Loaded</c>.</summary>
    private void TrashView_Loaded(object? sender, RoutedEventArgs e) => Focus();
}
