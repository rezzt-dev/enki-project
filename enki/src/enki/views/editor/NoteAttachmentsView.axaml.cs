using Avalonia.Controls;
using Avalonia.Input;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Comunica al visor PDF el tamaño disponible; la persistencia y las anotaciones viven en el viewmodel.</summary>
public partial class NoteAttachmentsView : UserControl
{
    public NoteAttachmentsView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;
            ResizeToWindow(top);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (IsVisible) Focus();
            });
            top.SizeChanged += OnWindowSizeChanged;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_window is { } top) top.SizeChanged -= OnWindowSizeChanged;
            _window = null;
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || DataContext is not EditorViewModel vm) return;
            vm.CloseAttachmentsCommand.Execute(null);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PreviewHost.SizeChanged += (_, _) => ReportViewport();
        PreviewHost.LayoutUpdated += (_, _) => ReportViewport();
    }

    private TopLevel? _window;

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_window is { } top) ResizeToWindow(top);
    }

    private void ResizeToWindow(TopLevel top)
    {
        _window = top;
        Width = System.Math.Max(320, System.Math.Min(960, top.Bounds.Width - 100));
        Height = System.Math.Max(280, System.Math.Min(720, top.Bounds.Height - 120));
    }

    private void ReportViewport()
    {
        if (DataContext is EditorViewModel { AttachmentPreview.PdfDocument: { } pdf })
            pdf.SetViewport(PreviewHost.Bounds.Width, PreviewHost.Bounds.Height - 40,
                TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
    }
}
