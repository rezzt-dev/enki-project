using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace enki.Views;

/// <summary>La shell: sidebar, barra de título, host de secciones y todo lo que hay dentro de ella (ver
/// <c>ShellView.axaml</c>).
///
/// <para>Vive en su propio control, y no dentro de <c>MainWindow.axaml</c>, para que su XAML no exista
/// durante el primer fotograma de la aplicación: <c>MainWindow</c> la construye cuando la pantalla de
/// arranque ya está delante (ver <c>MainWindow.BuildShell</c>). La ventana sigue siendo su dueña y sigue
/// mandándole lo que le corresponde —su <c>DataContext</c> es el mismo <c>MainWindowViewModel</c>, heredado
/// de la ventana— así que esto no es una frontera de arquitectura, solo el orden en el que se paga el
/// trabajo de arranque.</para></summary>
public partial class ShellView : UserControl
{
    private bool _resizingAssistantPanel;
    private double _assistantResizeStartX;
    private double _assistantResizeStartWidth;

    private ViewModels.MainWindowViewModel? _subscribed;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null) _subscribed.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribed = DataContext as ViewModels.MainWindowViewModel;
            if (_subscribed is null) return;
            _subscribed.PropertyChanged += OnViewModelPropertyChanged;
            EnsureAssistantSurfaces();
        };
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModels.MainWindowViewModel.IsAssistantPanelVisible)
            or nameof(ViewModels.MainWindowViewModel.CurrentPage)) EnsureAssistantSurfaces();
    }

    /// <summary>Construye el panel lateral y los diálogos del asistente la primera vez que hacen falta: al abrir
    /// el panel o al entrar en la sección. Su contexto sigue enlazado a <c>Assistant</c> por si la composición
    /// lo vuelve a crear.</summary>
    private void EnsureAssistantSurfaces()
    {
        if (_subscribed is not { } vm) return;
        var assistantShown = vm.IsAssistantPanelVisible || vm.CurrentPage is ViewModels.AssistantViewModel;
        if (vm.IsAssistantPanelVisible && AssistantPanelHost.Children.Count == 1)
        {
            var panel = new AssistantPanelView();
            panel.Bind(DataContextProperty, new Avalonia.Data.Binding(nameof(ViewModels.MainWindowViewModel.Assistant)));
            AssistantPanelHost.Children.Insert(0, panel);
        }
        if (assistantShown && AssistantDialogsHost.Children.Count == 0)
        {
            var dialogs = new AssistantDialogs();
            dialogs.Bind(DataContextProperty, new Avalonia.Data.Binding(nameof(ViewModels.MainWindowViewModel.Assistant)));
            AssistantDialogsHost.Children.Add(dialogs);
        }
    }

    /// <summary>Construye la vista de <paramref name="page"/> sin mostrarla, para que al navegar hacia ella
    /// solo haya que hacerla visible (ver <c>SectionHost.Preload</c>). Lo pide el ViewModel por
    /// <c>SectionPreloadRequested</c>, que la ventana traduce a esta llamada.</summary>
    public void Preload(object page) => PageHost.Preload(page);

    /// <summary>Pinta el logo del brand header. Quien decide qué imagen toca —el personalizado del usuario
    /// o el icono adaptativo al tema— es <c>MainWindow.UpdateBrandIcon</c>, que es también quien se lo pone a
    /// la pantalla de arranque; aquí solo se recibe.</summary>
    public void SetBrandIcon(IImage? logo) => BrandIcon.Source = logo;

    /// <summary>Arrastrar la ventana desde la cabecera del sidebar, con la barra de título propia activada.
    /// El arrastre es cosa de la ventana, no de este control, así que se le pide al <see cref="Window"/> en
    /// el que vive la shell — misma forma que usa <c>VaultSetupView</c>, que tampoco tiene una ventana
    /// propia. Doble clic maximiza y restaura, como en cualquier barra de título del sistema.</summary>
    // ===================== Ancho del panel del asistente =====================

    private void AssistantPanelGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Panel grip || DataContext is not ViewModels.MainWindowViewModel vm) return;
        _resizingAssistantPanel = true;
        _assistantResizeStartX = e.GetPosition(this).X;
        _assistantResizeStartWidth = vm.AssistantPanelWidth;
        e.Pointer.Capture(grip);
    }

    /// <summary>El panel está a la derecha: arrastrar su borde hacia la izquierda lo ensancha.</summary>
    private void AssistantPanelGrip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizingAssistantPanel || DataContext is not ViewModels.MainWindowViewModel vm) return;
        vm.AssistantPanelWidth = _assistantResizeStartWidth - (e.GetPosition(this).X - _assistantResizeStartX);
    }

    private void AssistantPanelGrip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizingAssistantPanel) return;
        _resizingAssistantPanel = false;
        e.Pointer.Capture(null);
        (DataContext as ViewModels.MainWindowViewModel)?.PersistAssistantPanelWidth();
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window) return;

        if (e.ClickCount == 2)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) window.BeginMoveDrag(e);
    }
}
