using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Mecánica de vista del compositor: Enter envía, Shift+Enter (o Ctrl+Enter donde un IME intercepta
/// Shift) inserta un salto de línea, y el foco vuelve al cuadro de texto al abrir un chat o terminar una
/// respuesta — solo en la superficie que se está viendo.</summary>
public partial class AssistantComposerView : UserControl
{
    private AssistantViewModel? _subscribed;

    public AssistantComposerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        ComposerBox.AddHandler(KeyDownEvent, ComposerBox_KeyDown, RoutingStrategies.Tunnel);
    }

    private void Subscribe()
    {
        Unsubscribe();
        _subscribed = DataContext as AssistantViewModel;
        if (_subscribed is null) return;
        _subscribed.ComposerFocusRequested += FocusComposer;
        _subscribed.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void Unsubscribe()
    {
        if (_subscribed is null) return;
        _subscribed.ComposerFocusRequested -= FocusComposer;
        _subscribed.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribed = null;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Al terminar una respuesta, el foco vuelve al cuadro de texto para seguir la conversación.
        if (e.PropertyName == nameof(AssistantViewModel.IsSending) && _subscribed?.IsSending == false) FocusComposer();
    }

    private void FocusComposer() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!ComposerBox.IsEffectivelyVisible || !ComposerBox.IsEffectivelyEnabled) return;
            ComposerBox.Focus();
            ComposerBox.CaretIndex = ComposerBox.Text?.Length ?? 0;
        }, DispatcherPriority.Input);

    private void ComposerHelp_Click(object? sender, RoutedEventArgs e)
    {
        ComposerHelpPopup.IsOpen = !ComposerHelpPopup.IsOpen;
        if (ComposerHelpPopup.IsOpen) ComposerHelpPopup.Child?.Focus();
    }

    private void ComposerBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _subscribed is null) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;
        if (_subscribed.SendCommand.CanExecute(null)) _subscribed.SendCommand.Execute(null);
    }
}
