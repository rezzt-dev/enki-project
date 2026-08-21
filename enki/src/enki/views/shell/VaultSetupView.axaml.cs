using Avalonia.Controls;
using Avalonia.Input;

namespace enki.Views;

public partial class VaultSetupView : UserControl
{
    public VaultSetupView()
    {
        InitializeComponent();
    }

    /// <summary>This screen has no brand header to drag from (unlike the shell's sidebar — see
    /// MainWindow's TitleBar_PointerPressed), so its own background stands in while the custom title
    /// bar setting is on. Only starts the drag when the press lands on the background itself, not when
    /// it bubbles up from one of the "crear/abrir vault" buttons.</summary>
    private void Background_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        (TopLevel.GetTopLevel(this) as Window)?.BeginMoveDrag(e);
    }
}
