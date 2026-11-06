using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using enki.ViewModels;

namespace enki;

public partial class QuickCaptureWindow : Window
{
    private QuickCaptureViewModel? _subscribedViewModel;

    public QuickCaptureWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SubscribeToViewModel();
        Activated += (_, _) => Dispatcher.UIThread.Post(FocusEditor, DispatcherPriority.Input);
        Closed += (_, _) => UnsubscribeFromViewModel();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void SubscribeToViewModel()
    {
        UnsubscribeFromViewModel();
        if (DataContext is not QuickCaptureViewModel viewModel) return;
        _subscribedViewModel = viewModel;
        viewModel.CloseRequested += CloseFromViewModel;
        Width = 560 * viewModel.UiZoomScale;
        Height = 350 * viewModel.UiZoomScale;
    }

    private void UnsubscribeFromViewModel()
    {
        if (_subscribedViewModel is not null) _subscribedViewModel.CloseRequested -= CloseFromViewModel;
        _subscribedViewModel = null;
    }

    private void CloseFromViewModel() => Close();

    private void FocusEditor()
    {
        var editor = this.FindControl<TextBox>("CaptureTextBox");
        editor?.Focus();
        editor?.CaretIndex = editor.Text?.Length ?? 0;
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
