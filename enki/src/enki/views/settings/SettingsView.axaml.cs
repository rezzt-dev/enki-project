using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using enki.ViewModels;

namespace enki.Views;

public partial class SettingsView : UserControl
{
    private static readonly DataFormat<string> DashboardSectionDragFormat =
        DataFormat.CreateStringApplicationFormat("com.enki.dashboard-section-key");

    private PointerPressedEventArgs? _sectionDragPressArgs;
    private Point _sectionDragStartPoint;
    private bool _sectionDragging;

    public SettingsView()
    {
        InitializeComponent();
    }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    /// <summary>Entra en modo captura para el atajo bajo el puntero (propuesta 29,
    /// docs/propuesta-nuevas-funcionalidades.md) — el siguiente KeyDown sobre este mismo control se
    /// interpreta como el nuevo gesto (ver <see cref="ShortcutCapture_KeyDown"/>). Pide el foco
    /// explícitamente porque el control no es un TextBox y no lo toma solo con el clic.</summary>
    private void ShortcutCapture_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ShortcutRowViewModel row } control || Vm is not { } vm) return;

        control.Focus();
        vm.BeginCapture(row);
        e.Handled = true;
    }

    /// <summary>Interpreta el próximo KeyDown como el nuevo gesto mientras la fila está en modo captura.
    /// Exige al menos un modificador real (Ctrl/Alt/Cmd) salvo para teclas de función, para no capturar por
    /// error una tecla que el usuario solo estaba tecleando con normalidad — mismo criterio que la mayoría
    /// de editores de atajos (VS Code, Obsidian).</summary>
    private void ShortcutCapture_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: ShortcutRowViewModel row } || Vm is not { } vm) return;
        if (!row.IsCapturing) return;

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            vm.CancelCapture(row);
            return;
        }

        if (IsModifierKey(e.Key)) return; // esperar a que llegue una tecla real, no solo el modificador

        var hasRealModifier = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
        var isFunctionKey = e.Key is >= Key.F1 and <= Key.F24;
        if (!hasRealModifier && !isFunctionKey)
        {
            vm.RejectCapturedGesture(row);
            return;
        }

        vm.ApplyCapturedGesture(row, new KeyGesture(e.Key, e.KeyModifiers));
    }

    private void ShortcutCapture_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ShortcutRowViewModel row } || Vm is not { } vm) return;
        if (row.IsCapturing) vm.CancelCapture(row);
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    // ===================== Dashboard section reordering (propuesta 27) =====================

    private void DashboardSectionHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _sectionDragPressArgs = e;
        _sectionDragStartPoint = e.GetPosition(null);
        _sectionDragging = false;
    }

    private async void DashboardSectionHandle_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_sectionDragging || _sectionDragPressArgs is null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { DataContext: DashboardSectionConfigViewModel section }) return;

        var current = e.GetPosition(null);
        var dx = current.X - _sectionDragStartPoint.X;
        var dy = current.Y - _sectionDragStartPoint.Y;
        if (dx * dx + dy * dy < 16) return; // ~4px threshold

        _sectionDragging = true;
        var pressArgs = _sectionDragPressArgs;
        _sectionDragPressArgs = null;

        try
        {
            using var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(DashboardSectionDragFormat, section.Key));
            await DragDrop.DoDragDropAsync(pressArgs, transfer, DragDropEffects.Move);
        }
        finally
        {
            _sectionDragging = false;
        }
    }

    private void DashboardSectionRow_DragEnter(object? sender, DragEventArgs e) => UpdateSectionDragOver(sender, e, true);
    private void DashboardSectionRow_DragOver(object? sender, DragEventArgs e) => UpdateSectionDragOver(sender, e, true);
    private void DashboardSectionRow_DragLeave(object? sender, DragEventArgs e) => UpdateSectionDragOver(sender, e, false);

    private static void UpdateSectionDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Control { DataContext: DashboardSectionConfigViewModel target }) return;

        var valid = e.DataTransfer.Contains(DashboardSectionDragFormat);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        target.IsDropTarget = isOver && valid;
        e.Handled = true;
    }

    private void DashboardSectionRow_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Control { DataContext: DashboardSectionConfigViewModel target } || Vm is not { } vm) return;
        target.IsDropTarget = false;
        e.Handled = true;

        var sourceKey = e.DataTransfer.TryGetValue(DashboardSectionDragFormat);
        if (sourceKey is null || sourceKey == target.Key) return;

        vm.MoveDashboardSection(sourceKey, target.Key);
    }
}
