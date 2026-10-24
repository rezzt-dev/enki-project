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
    public ShellView() => InitializeComponent();

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
