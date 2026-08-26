using Avalonia.Controls;
using Avalonia.Input;
using enki.Services;
using enki.ViewModels;

namespace enki.Views;

public partial class NotesView : UserControl
{
    /// <summary>Qué disposición hay montada ahora mismo en <c>LayoutHost</c>, o null si todavía ninguna.
    /// Se compara contra <see cref="NotesViewModel.IsSimplifiedView"/> para no reconstruir la vista (y con
    /// ella el <c>EditorView</c>, que es caro) en cada notificación de cambio de propiedad.</summary>
    private bool? _hostedLayoutIsSimplified;

    public NotesView()
    {
        InitializeComponent();

        // Atajos de gestión de pestañas (propuesta 29, docs/propuesta-nuevas-funcionalidades.md): se
        // reconstruyen cada vez que cambia el DataContext (una nueva instancia de NotesView por cada
        // vuelta a la página, ver ContentControl.Content en MainWindow.axaml) o que Ajustes › Atajos
        // remapea/restaura un gesto — suscrito solo mientras la vista está en el árbol visual para no
        // acumular handlers sobre instancias ya descartadas.
        DataContextChanged += (_, _) =>
        {
            BuildKeyBindings();
            ApplyLayout();
        };
        Loaded += (_, _) =>
        {
            ShortcutRegistry.Changed += BuildKeyBindings;
            BuildKeyBindings();
            ApplyLayout();
        };
        Unloaded += (_, _) => ShortcutRegistry.Changed -= BuildKeyBindings;
    }

    private NotesViewModel? Vm => DataContext as NotesViewModel;

    /// <summary>Monta la disposición elegida en Ajustes › Editor › Vista del editor y descarta la otra.
    /// Se hace aquí, montando una sola, en vez de dejar las dos en el XAML con IsVisible: cada una
    /// contiene su propio <c>EditorView</c>, y dos EditorView vivos sobre el mismo <c>EditorViewModel</c>
    /// se suscribirían los dos a sus eventos (comandos "/", saltos del esquema, buscar y reemplazar), con
    /// lo que cada acción se aplicaría dos veces sobre el documento.</summary>
    private void ApplyLayout()
    {
        if (Vm is not { } vm) return;

        // La suscripción se rehace en cada DataContext nuevo; quitarla antes evita duplicarla si esta
        // misma instancia se reutiliza con otro viewmodel.
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;

        if (_hostedLayoutIsSimplified == vm.IsSimplifiedView && LayoutHost.Content is not null) return;

        _hostedLayoutIsSimplified = vm.IsSimplifiedView;
        LayoutHost.Content = vm.IsSimplifiedView ? new SimpleNotesView() : (Control)new FullNotesView();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotesViewModel.IsSimplifiedView)) ApplyLayout();
    }

    private void BuildKeyBindings()
    {
        if (Vm is not { } vm) return;

        KeyBindings.Clear();
        KeyBindings.Add(new KeyBinding { Gesture = ShortcutRegistry.GetGesture(ShortcutIds.NotesCloseTab), Command = vm.CloseActiveEditorCommand });
        KeyBindings.Add(new KeyBinding { Gesture = ShortcutRegistry.GetGesture(ShortcutIds.NotesNextTab), Command = vm.SelectNextEditorCommand });
        KeyBindings.Add(new KeyBinding { Gesture = ShortcutRegistry.GetGesture(ShortcutIds.NotesPreviousTab), Command = vm.SelectPreviousEditorCommand });
        // Modo enfoque/zen (propuesta 7, docs/propuesta-nuevas-funcionalidades.md) — Escape para salir se
        // maneja aparte, en EditorView.axaml.cs (solo mientras el foco está en el propio editor de texto).
        KeyBindings.Add(new KeyBinding { Gesture = ShortcutRegistry.GetGesture(ShortcutIds.NotesToggleZenMode), Command = vm.ToggleZenModeCommand });

        // Alias fijos de PageUp/PageDown (no remapeables) junto al gesto remapeable de Tab.
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageDown, KeyModifiers.Control), Command = vm.SelectNextEditorCommand });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageUp, KeyModifiers.Control), Command = vm.SelectPreviousEditorCommand });
    }
}
