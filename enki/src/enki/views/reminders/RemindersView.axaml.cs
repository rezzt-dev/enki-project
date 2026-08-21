using Avalonia.Controls;
using Avalonia.Interactivity;

namespace enki.Views;

public partial class RemindersView : UserControl
{
    public RemindersView()
    {
        InitializeComponent();
    }

    /// <summary>Toma el foco de teclado al abrirse la página para que el atajo Ctrl+N funcione sin tener
    /// que hacer clic antes en ella — mismo patrón que <c>KanbanView_Loaded</c>/<c>CalendarView_Loaded</c>.</summary>
    private void RemindersView_Loaded(object? sender, RoutedEventArgs e) => Focus();
}
