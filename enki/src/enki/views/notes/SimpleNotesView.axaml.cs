using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Code-behind de la vista simplificada de Notas: selección y renombrado en línea de las filas
/// del árbol, arrastrar una nota a otra carpeta, y el separador que ajusta el ancho del panel lateral.
/// Solo mecánica de vista — cualquier cosa que toque el vault vive en <see cref="NotesViewModel"/>.</summary>
public partial class SimpleNotesView : UserControl
{
    // Mismo formato de arrastre que la rejilla de la vista completa (NotesView.axaml.cs), a propósito:
    // así una nota arrastrada desde cualquiera de las dos vistas se puede soltar en la otra sin que el
    // destino tenga que saber de dónde salió.
    private static readonly DataFormat<string> NoteDragFormat =
        DataFormat.CreateStringApplicationFormat("com.enki.note-path");

    private PointerPressedEventArgs? _dragPressArgs;
    private Point _dragStartPoint;
    private bool _dragging;

    private bool _resizingSidebar;
    private double _resizeStartX;
    private double _resizeStartWidth;

    public SimpleNotesView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => SubscribeToViewModel();
        Loaded += (_, _) => SubscribeToViewModel();
        Unloaded += (_, _) =>
        {
            if (Vm is { } vm) vm.PropertyChanged -= OnViewModelPropertyChanged;
        };
    }

    private NotesViewModel? Vm => DataContext as NotesViewModel;

    private void SubscribeToViewModel()
    {
        if (Vm is not { } vm) return;
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>Entrar en modo búsqueda pone el cursor en el cuadro: pulsar la lupa es inequívocamente
    /// "quiero escribir una búsqueda", y obligar a un segundo clic para empezar a teclear es justo el tipo
    /// de fricción que esta vista existe para quitar.</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NotesViewModel.IsSimpleSearchMode)) return;
        if (Vm is not { IsSimpleSearchMode: true }) return;

        Dispatcher.UIThread.Post(() =>
        {
            SimpleSearchBox.Focus();
            SimpleSearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    // ===================== Filas del árbol =====================

    private void TreeRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: NoteTreeItemViewModel node } || Vm is not { } vm) return;
        if (e.GetCurrentPoint(sender as Control).Properties.IsRightButtonPressed) return;

        vm.ActivateTreeItemCommand.Execute(node);

        // Solo las notas se arrastran: mover carpetas enteras es una operación bastante más destructiva
        // y aquí no hay ninguna confirmación de por medio.
        if (!node.IsNote) return;
        _dragPressArgs = e;
        _dragStartPoint = e.GetPosition(null);
        _dragging = false;
    }

    private async void TreeRow_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging || _dragPressArgs is null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not Border { DataContext: NoteTreeItemViewModel node } || !node.IsNote) return;

        var current = e.GetPosition(null);
        var dx = current.X - _dragStartPoint.X;
        var dy = current.Y - _dragStartPoint.Y;
        if (dx * dx + dy * dy < 16) return; // ~4px, mismo umbral que las tarjetas de la vista completa

        _dragging = true;
        var pressArgs = _dragPressArgs;
        _dragPressArgs = null;

        try
        {
            using var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(NoteDragFormat, node.FullPath));
            await DragDrop.DoDragDropAsync(pressArgs, transfer, DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
        }
    }

    private static void TreeRow_DragEnter(object? sender, DragEventArgs e) => UpdateTreeRowDragOver(sender, e, true);
    private static void TreeRow_DragOver(object? sender, DragEventArgs e) => UpdateTreeRowDragOver(sender, e, true);
    private static void TreeRow_DragLeave(object? sender, DragEventArgs e) => UpdateTreeRowDragOver(sender, e, false);

    /// <summary>Solo las carpetas aceptan una nota soltada encima — una nota sobre otra nota no significa
    /// nada, así que ni se resalta ni se marca el gesto como válido.</summary>
    private static void UpdateTreeRowDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Border { DataContext: NoteTreeItemViewModel node }) return;

        var valid = node.IsFolder && e.DataTransfer.Contains(NoteDragFormat);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        node.IsDropTarget = isOver && valid;
    }

    private void TreeRow_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: NoteTreeItemViewModel node }) return;
        node.IsDropTarget = false;
        if (!node.IsFolder || Vm is not { } vm) return;

        var sourcePath = e.DataTransfer.TryGetValue(NoteDragFormat);
        if (sourcePath is not null) vm.MoveTreeNoteToFolder(sourcePath, node.FullPath);
    }

    // ===================== Fondo del árbol: soltar en la raíz de la sesión =====================

    private static void TreeRoot_DragEnter(object? sender, DragEventArgs e) => UpdateTreeRootDragOver(e);
    private static void TreeRoot_DragOver(object? sender, DragEventArgs e) => UpdateTreeRootDragOver(e);
    private static void TreeRoot_DragLeave(object? sender, DragEventArgs e) => e.DragEffects = DragDropEffects.None;

    private static void UpdateTreeRootDragOver(DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(NoteDragFormat) ? DragDropEffects.Move : DragDropEffects.None;

    private void TreeRoot_Drop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;

        var sourcePath = e.DataTransfer.TryGetValue(NoteDragFormat);
        if (sourcePath is not null) vm.MoveTreeNoteToFolder(sourcePath, vm.EffectiveRootPath);
    }

    // ===================== Resultados de búsqueda =====================

    private void SearchResult_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: NoteCardViewModel card }) Vm?.OpenCardCommand.Execute(card);
    }

    // ===================== Renombrado en línea =====================

    private void TreeNameEdit_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: NoteTreeItemViewModel node }) return;

        if (e.Key == Key.Enter)
        {
            Vm?.CommitRenameTreeItemCommand.Execute(node);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            node.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Perder el foco confirma, igual que Enter — mismo criterio que el renombrado en línea de la
    /// vista completa. <c>IsRenaming</c> ya en false significa que Enter/Escape se adelantaron.</summary>
    private void TreeNameEdit_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: NoteTreeItemViewModel node } || !node.IsRenaming) return;
        Vm?.CommitRenameTreeItemCommand.Execute(node);
    }

    private void TreeNameEdit_Initialized(object? sender, EventArgs e)
    {
        if (sender is TextBox box) FocusRenameBox(box);
    }

    /// <summary>El TextBox de renombrar ya está en el árbol cuando <c>IsRenaming</c> pasa a true (solo
    /// cambia su visibilidad), así que Initialized no vuelve a dispararse: hay que darle el foco también
    /// aquí, cuando se hace visible.</summary>
    private void TreeNameEdit_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty || sender is not TextBox box) return;
        if (e.GetNewValue<bool>()) FocusRenameBox(box);
    }

    private static void FocusRenameBox(TextBox box)
    {
        if (!box.IsVisible) return;
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    // ===================== Separador del panel lateral =====================

    private void SidebarGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Panel grip || Vm is not { } vm) return;

        _resizingSidebar = true;
        _resizeStartX = e.GetPosition(this).X;
        _resizeStartWidth = vm.SimpleSidebarWidth;
        e.Pointer.Capture(grip);
    }

    private void SidebarGrip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizingSidebar || Vm is not { } vm) return;

        var delta = e.GetPosition(this).X - _resizeStartX;
        vm.SimpleSidebarWidth = Math.Clamp(_resizeStartWidth + delta,
            NotesViewModel.SimpleSidebarMinWidth, NotesViewModel.SimpleSidebarMaxWidth);
    }

    /// <summary>El ancho se guarda al soltar, no en cada píxel del arrastre — escribir el JSON de ajustes
    /// decenas de veces por gesto no aportaría nada y sí ruido de E/S.</summary>
    private void SidebarGrip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizingSidebar) return;
        _resizingSidebar = false;
        e.Pointer.Capture(null);
        Vm?.PersistSimpleSidebarWidth();
    }
}
