using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using enki.Services;
using enki.ViewModels;

namespace enki.Views;

public partial class RemindersView : UserControl
{
    public RemindersView()
    {
        InitializeComponent();

        // Atajo remapeable (propuesta 29, docs/propuesta-nuevas-funcionalidades.md) — ver el comentario
        // equivalente en NotesView.axaml.cs para por qué se suscribe/desuscribe en Loaded/Unloaded.
        DataContextChanged += (_, _) => BuildKeyBindings();
        Loaded += (_, _) =>
        {
            ShortcutRegistry.Changed += BuildKeyBindings;
            BuildKeyBindings();
        };
        Unloaded += (_, _) => ShortcutRegistry.Changed -= BuildKeyBindings;
    }

    private void BuildKeyBindings()
    {
        if (DataContext is not RemindersViewModel vm) return;

        KeyBindings.Clear();
        KeyBindings.Add(new KeyBinding { Gesture = ShortcutRegistry.GetGesture(ShortcutIds.RemindersNew), Command = vm.OpenCreateEditorCommand });
    }

    /// <summary>Toma el foco de teclado al abrirse la página para que el atajo de "nuevo recordatorio"
    /// funcione sin tener que hacer clic antes en ella — mismo patrón que
    /// <c>KanbanView_Loaded</c>/<c>CalendarView_Loaded</c>.</summary>
    private void RemindersView_Loaded(object? sender, RoutedEventArgs e) => Focus();
}
