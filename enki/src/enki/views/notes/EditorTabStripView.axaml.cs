using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Barra compartida de pestañas: selección, cierre con clic medio y reordenación local.
/// El gesto permanece en la barra y no inicia un arrastre nativo del sistema operativo.</summary>
public partial class EditorTabStripView : UserControl
{
    private EditorViewModel? _dragSource;
    private EditorViewModel? _dropTarget;
    private IPointer? _dragPointer;
    private Point _dragStart;
    private bool _dragging;

    public EditorTabStripView()
    {
        InitializeComponent();
        AddHandler(PointerMovedEvent, OnTabPointerMoved, RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        PointerCaptureLost += (_, _) => ResetDrag();
        DetachedFromVisualTree += (_, _) => ResetDrag();
        DataContextChanged += (_, _) => ResetDrag();
    }

    private NotesViewModel? Vm => DataContext as NotesViewModel;

    private void EditorTab_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: EditorViewModel editor } || Vm is not { } vm) return;
        // El botón de cerrar tiene su propio gesto; no debe seleccionar ni iniciar una reordenación.
        if (e.Source is Visual source &&
            (source is Button || source.GetVisualAncestors().OfType<Button>().Any())) return;

        ResetDrag();
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsMiddleButtonPressed)
        {
            vm.CloseEditorTabCommand.Execute(editor);
            e.Handled = true;
            return;
        }
        if (!properties.IsLeftButtonPressed || !vm.OpenEditors.Contains(editor)) return;

        vm.SelectEditorCommand.Execute(editor);
        _dragSource = editor;
        _dragStart = e.GetPosition(this);
        _dragPointer = e.Pointer;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSource is null || e.Pointer != _dragPointer) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            Vm is not { } vm || !vm.OpenEditors.Contains(_dragSource))
        {
            ResetDrag();
            return;
        }

        var delta = e.GetPosition(this) - _dragStart;
        if (!_dragging && delta.X * delta.X + delta.Y * delta.Y < 9) return;
        _dragging = true;
        SetDropTarget(FindTabAt(e));
        e.Handled = true;
    }

    private EditorViewModel? FindTabAt(PointerEventArgs e) =>
        !new Rect(Bounds.Size).Contains(e.GetPosition(this)) ? null :
        this.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(tab => tab.Classes.Contains("editor-tab") &&
                new Rect(tab.Bounds.Size).Contains(e.GetPosition(tab)))?.DataContext as EditorViewModel;

    private void SetDropTarget(EditorViewModel? target)
    {
        if (_dropTarget is not null) _dropTarget.IsDropTarget = false;
        _dropTarget = target == _dragSource ? null : target;
        if (_dropTarget is not null) _dropTarget.IsDropTarget = true;
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragSource is null || e.Pointer != _dragPointer) return;
        var source = _dragSource;
        var target = _dragging && e.InitialPressMouseButton == MouseButton.Left ? FindTabAt(e) : null;
        ResetDrag();
        if (target is not null && Vm is { } vm &&
            vm.OpenEditors.Contains(source) && vm.OpenEditors.Contains(target))
            vm.ReorderEditor(source, target);
        e.Handled = true;
    }

    private void ResetDrag()
    {
        var pointer = _dragPointer;
        _dragPointer = null;
        _dragSource = null;
        _dragging = false;
        SetDropTarget(null);
        if (pointer?.Captured == this) pointer.Capture(null);
    }
}
