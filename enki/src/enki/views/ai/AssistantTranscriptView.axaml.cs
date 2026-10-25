using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Mecánica de vista del hilo del asistente: bajar hasta el último mensaje mientras llega la respuesta
/// sin arrastrar al usuario si ha subido a releer algo (entonces aparece "Ir al final"), y retomar el punto de
/// lectura al pasar de la sección al panel o al revés. Todo lo demás vive en <see cref="AssistantViewModel"/>.</summary>
public partial class AssistantTranscriptView : UserControl
{
    /// <summary>Distancia al final por debajo de la cual se considera que el usuario "está abajo".</summary>
    private const double StickToBottomThreshold = 48;

    private AssistantViewModel? _subscribed;
    private bool _stickToBottom = true;

    public AssistantTranscriptView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        AttachedToVisualTree += (_, _) =>
        {
            Subscribe();
            RestoreScroll();
        };
        TranscriptScroll.ScrollChanged += (_, _) =>
        {
            var distance = TranscriptScroll.Extent.Height - TranscriptScroll.Viewport.Height - TranscriptScroll.Offset.Y;
            _stickToBottom = distance <= StickToBottomThreshold;
            JumpToEndButton.IsVisible = !_stickToBottom;
            // Solo la superficie que se está viendo registra su punto de lectura.
            if (_subscribed is not null && IsEffectivelyVisible)
                _subscribed.TranscriptScrollOffset = _stickToBottom ? null : TranscriptScroll.Offset.Y;
        };
    }

    private void Subscribe()
    {
        Unsubscribe();
        _subscribed = DataContext as AssistantViewModel;
        if (_subscribed is null) return;
        _subscribed.ScrollToEndRequested += OnScrollToEndRequested;
        _subscribed.TranscriptRestoreRequested += RestoreScroll;
        _subscribed.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void Unsubscribe()
    {
        if (_subscribed is null) return;
        _subscribed.ScrollToEndRequested -= OnScrollToEndRequested;
        _subscribed.TranscriptRestoreRequested -= RestoreScroll;
        _subscribed.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribed = null;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Al enviar, el usuario quiere ver su mensaje y la respuesta: se vuelve a pegar al final.
        if (e.PropertyName == nameof(AssistantViewModel.IsSending) && _subscribed?.IsSending == true)
        {
            _stickToBottom = true;
            JumpToEndButton.IsVisible = false;
        }
    }

    private void OnScrollToEndRequested()
    {
        if (!_stickToBottom) return;
        Dispatcher.UIThread.Post(() => TranscriptScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>Vuelve al punto de lectura que dejó la otra superficie, o al final si estaba abajo. Espera a que
    /// el hilo esté medido: recién enseñado aún no tiene alto.</summary>
    private void RestoreScroll() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_subscribed is null || !IsEffectivelyVisible) return;
            if (_subscribed.TranscriptScrollOffset is { } offset)
            {
                _stickToBottom = false;
                TranscriptScroll.Offset = TranscriptScroll.Offset.WithY(offset);
            }
            else
            {
                _stickToBottom = true;
                TranscriptScroll.ScrollToEnd();
            }
        }, DispatcherPriority.Background);

    private void JumpToEndButton_Click(object? sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        JumpToEndButton.IsVisible = false;
        TranscriptScroll.ScrollToEnd();
    }
}
