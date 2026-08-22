using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using enki.Services;
using enki.ViewModels;

namespace enki.Views;

public partial class TrashView : UserControl
{
    public TrashView()
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
        if (DataContext is not TrashViewModel vm) return;

        KeyBindings.Clear();
        KeyBindings.Add(new KeyBinding { Gesture = ShortcutRegistry.GetGesture(ShortcutIds.TrashEmpty), Command = vm.RequestEmptyTrashCommand });
    }

    /// <summary>Toma el foco de teclado al abrirse la página para que el atajo de "vaciar papelera"
    /// funcione sin tener que hacer clic antes en ella — mismo patrón que <c>KanbanView_Loaded</c>.</summary>
    private void TrashView_Loaded(object? sender, RoutedEventArgs e) => Focus();
}
