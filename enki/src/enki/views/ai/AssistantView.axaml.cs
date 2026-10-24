using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Sección Asistente. Solo mecánica de vista: Enter para enviar, bajar hasta el último mensaje
/// mientras llega la respuesta (sin arrastrar al usuario si ha subido a releer algo), el foco del cuadro de
/// texto y el renombrado del título en línea. Todo lo demás vive en <see cref="AssistantViewModel"/>.</summary>
public partial class AssistantView : UserControl
{
    /// <summary>Distancia al final por debajo de la cual se considera que el usuario "está abajo".</summary>
    private const double StickToBottomThreshold = 48;

    private AssistantViewModel? _subscribed;
    private bool _stickToBottom = true;

    public AssistantView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
        ComposerBox.AddHandler(KeyDownEvent, ComposerBox_KeyDown, RoutingStrategies.Tunnel);
        TranscriptScroll.ScrollChanged += (_, _) =>
        {
            var distance = TranscriptScroll.Extent.Height - TranscriptScroll.Viewport.Height - TranscriptScroll.Offset.Y;
            _stickToBottom = distance <= StickToBottomThreshold;
        };
    }

    private void Subscribe()
    {
        if (_subscribed is not null)
        {
            _subscribed.ScrollToEndRequested -= OnScrollToEndRequested;
            _subscribed.ComposerFocusRequested -= FocusComposer;
            _subscribed.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _subscribed = DataContext as AssistantViewModel;
        if (_subscribed is null) return;
        _subscribed.ScrollToEndRequested += OnScrollToEndRequested;
        _subscribed.ComposerFocusRequested += FocusComposer;
        _subscribed.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // Al enviar, el usuario quiere ver su mensaje y la respuesta: se vuelve a pegar al final.
            case nameof(AssistantViewModel.IsSending) when _subscribed?.IsSending == true:
                _stickToBottom = true;
                break;
            // Al terminar, el foco vuelve al cuadro de texto para seguir la conversación.
            case nameof(AssistantViewModel.IsSending):
                FocusComposer();
                break;
            case nameof(AssistantViewModel.IsRenamingTitle) when _subscribed?.IsRenamingTitle == true:
                Dispatcher.UIThread.Post(() =>
                {
                    TitleEditor.Focus();
                    TitleEditor.SelectAll();
                }, DispatcherPriority.Input);
                break;
        }
    }

    private void OnScrollToEndRequested()
    {
        if (!_stickToBottom) return;
        Dispatcher.UIThread.Post(() => TranscriptScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void FocusComposer() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!ComposerBox.IsEffectivelyVisible) return;
            ComposerBox.Focus();
            ComposerBox.CaretIndex = ComposerBox.Text?.Length ?? 0;
        }, DispatcherPriority.Input);

    /// <summary>Enter envía; Shift+Enter (o Ctrl+Enter en sistemas donde Shift lo intercepta un IME) inserta
    /// un salto de línea como en cualquier chat.</summary>
    private void ComposerBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _subscribed is null) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;
        if (_subscribed.SendCommand.CanExecute(null)) _subscribed.SendCommand.Execute(null);
    }

    private void SessionRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: AiChatSessionItemViewModel item } control) return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        _stickToBottom = true;
        _subscribed?.SelectSessionCommand.Execute(item);
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
