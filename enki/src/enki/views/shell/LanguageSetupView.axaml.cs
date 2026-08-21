using Avalonia.Controls;
using Avalonia.Input;

namespace enki.Views;

public partial class LanguageSetupView : UserControl
{
    public LanguageSetupView()
    {
        InitializeComponent();
    }

    /// <summary>Igual que <see cref="VaultSetupView"/>: esta pantalla no tiene sidebar ni cabecera de
    /// marca para arrastrar, así que su fondo sirve como superficie de arrastre mientras la barra de
    /// título personalizada esté activa.</summary>
    private void Background_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        (TopLevel.GetTopLevel(this) as Window)?.BeginMoveDrag(e);
    }
}
