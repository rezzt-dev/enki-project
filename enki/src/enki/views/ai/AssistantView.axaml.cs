using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Sección Asistente. El hilo, el compositor y el historial son controles compartidos con el panel
/// lateral, cada uno con su propia mecánica de vista; aquí solo quedan el renombrado del título en línea y el
/// hueco del compositor (centrado en un chat vacío, fijo bajo el hilo con mensajes). Todo lo demás vive en
/// <see cref="AssistantViewModel"/>.</summary>
public partial class AssistantView : UserControl
{
    private AssistantViewModel? _subscribed;

    public AssistantView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
    }

    private void Subscribe()
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribed = DataContext as AssistantViewModel;
        if (_subscribed is not null) _subscribed.PropertyChanged += OnViewModelPropertyChanged;
        PlaceComposer();
    }

    /// <summary>Con el chat vacío el compositor va al centro de la bienvenida; con mensajes, bajo el hilo. Es un
    /// solo control que cambia de hueco —no dos compositores sobre el mismo borrador—, y si tenía el foco lo
    /// recupera al llegar, para seguir escribiendo tras enviar el primer mensaje.</summary>
    private void PlaceComposer()
    {
        var centered = _subscribed?.ShowWelcome == true;
        Composer.Classes.Set("hero", centered);
        Decorator target = centered ? Welcome.Slot : DockedComposerHost;
        if (ReferenceEquals(Composer.Parent, target)) return;

        var hadFocus = Composer.IsKeyboardFocusWithin;
        if (Composer.Parent is Decorator current) current.Child = null;
        target.Child = Composer;
        if (hadFocus) Composer.FocusInput();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssistantViewModel.ShowWelcome))
        {
            PlaceComposer();
            return;
        }
        if (e.PropertyName != nameof(AssistantViewModel.IsRenamingTitle) || _subscribed?.IsRenamingTitle != true) return;
        Dispatcher.UIThread.Post(() =>
        {
            TitleEditor.Focus();
            TitleEditor.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void TitleEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_subscribed is null) return;
        if (e.Key == Key.Enter)
        {
            _subscribed.CommitRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _subscribed.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void TitleEditor_LostFocus(object? sender, RoutedEventArgs e) => _subscribed?.CommitRenameCommand.Execute(null);
}
