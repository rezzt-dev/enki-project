using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Code-behind de la vista simplificada de Notas: selección y renombrado en línea de las filas
/// del árbol, arrastrar notas y carpetas, y el separador que ajusta el ancho del panel lateral.
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

    /// <summary>Alzado por <see cref="TreeRow_PointerPressed"/> cuando el clic cae en la fila que se está
    /// renombrando: le dice a <see cref="TreeNameEdit_LostFocus"/> que la pérdida de foco que viene a
    /// continuación es ese mismo clic —recolocar el cursor, o un roce con el Border que rodea al cuadro
    /// estrecho— y no un "clic fuera" que deba confirmar. Se rearma solo en el siguiente ciclo del
    /// dispatcher, así que solo cubre el LostFocus inmediato.</summary>
    private bool _keepRenamingThroughLostFocus;

    public SimpleNotesView()
    {
        InitializeComponent();
        _dragGhost = new ExplorerDragGhost(this, DragSurface, NoteDragFormat);
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || Vm is not { IsSimpleNavigatorOpen: true } vm) return;
            vm.CloseSimpleNavigatorCommand.Execute(null);
            e.Handled = true;
        };

        // En túnel (antes que cualquier burbuja y antes de que Avalonia mueva el foco por el clic): si
        // el clic cae dentro de la fila que se está renombrando, se arma la bandera que impide que el
        // LostFocus del cuadro —que llega inmediatamente después— confirme el renombrado. El manejador
        // de burbuja por fila (TreeRow_PointerPressed) es quien luego se traga el evento y reenfoca.
        AddHandler(PointerPressedEvent, OnTreePreviewPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        DataContextChanged += (_, _) => SubscribeToViewModel();
        Loaded += (_, _) => SubscribeToViewModel();
        Unloaded += (_, _) =>
        {
            if (Vm is { } vm) vm.PropertyChanged -= OnViewModelPropertyChanged;
        };
    }

    private readonly ExplorerDragGhost _dragGhost;

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
        if (Vm is not { } vm) return;
        if (e.PropertyName == nameof(NotesViewModel.IsSimpleNavigatorOpen) && !vm.IsSimpleNavigatorOpen)
        {
            if (SimpleNavigator.IsKeyboardFocusWithin)
                (vm.IsSimpleSearchMode ? RailSearchButton : RailTreeButton).Focus();
            return;
        }
        if (e.PropertyName is not (nameof(NotesViewModel.IsSimpleSearchMode)
            or nameof(NotesViewModel.IsSimpleNavigatorOpen)
            or nameof(NotesViewModel.IsSimpleSidebarVisible))) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (Vm is not { IsSimpleNavigatorVisible: true } current) return;
            if (current.IsSimpleSearchMode)
            {
                SimpleSearchBox.Focus();
                SimpleSearchBox.SelectAll();
            }
            else if (current.IsSimpleNavigatorOpen)
            {
                foreach (var control in SimpleNavigator.GetVisualDescendants())
                    if (control is Border { IsEffectivelyVisible: true, DataContext: NoteTreeItemViewModel } row
                        && row.Classes.Contains("folder-row"))
                    {
                        row.Focus();
                        break;
                    }
            }
        }, DispatcherPriority.Input);
    }

    private void NavigatorDismiss_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Vm?.CloseSimpleNavigatorCommand.Execute(null);
        e.Handled = true;
    }

    private void TreeRow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)
            || sender is not Border { DataContext: NoteTreeItemViewModel { IsRenaming: false } node }) return;
        Vm?.ActivateTreeItemCommand.Execute(node);
        e.Handled = true;
    }

    private void SearchResult_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)
            || sender is not Border { DataContext: NoteCardViewModel card }) return;
        Vm?.OpenCardCommand.Execute(card);
        e.Handled = true;
    }

    // ===================== Filas del árbol =====================

    /// <summary>Ver el comentario del <c>AddHandler</c> en el constructor. Solo arma la bandera; no marca
    /// el evento como manejado para no romper el comportamiento normal de las filas que no se renombran.</summary>
    private void OnTreePreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; // el clic derecho abre el menú contextual

        foreach (var ancestor in source.GetSelfAndVisualAncestors())
        {
            if (ancestor is StyledElement { DataContext: NoteTreeItemViewModel { IsRenaming: true } })
            {
                _keepRenamingThroughLostFocus = true;
                Dispatcher.UIThread.Post(() => _keepRenamingThroughLostFocus = false, DispatcherPriority.Input);
                return;
            }
        }
    }

    private void TreeRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: NoteTreeItemViewModel node } || Vm is not { } vm) return;
        if (e.GetCurrentPoint(sender as Control).Properties.IsRightButtonPressed) return;

        // Mientras la fila está en modo renombrar es una superficie de edición, no un objetivo de clic:
        // el cuadro de texto solo ocupa una franja estrecha de una fila ancha, así que casi cualquier
        // clic para colocar el cursor cae en el Border, no en el TextBox. Se traga el evento y se
        // devuelve el foco al cuadro, para que ni se active el nodo (abrir la nota / plegar la carpeta)
        // ni el LostFocus del cuadro cierre el renombrado antes de haber escrito nada. Empezar a
        // renombrar viene del menú contextual, no de aquí, así que esto no impide iniciarlo.
        if (node.IsRenaming)
        {
            // La bandera ya la armó OnTreePreviewPointerPressed en túnel; aquí solo se reenfoca el
            // cuadro y se marca el evento como manejado para que el clic no haga nada más.
            foreach (var descendant in ((Visual)sender).GetVisualDescendants())
            {
                if (descendant is TextBox { IsVisible: true } renameBox)
                {
                    FocusRenameBox(renameBox);
                    break;
                }
            }
            e.Handled = true;
            return;
        }

        ((Control)sender).Focus();
        vm.ActivateTreeItemCommand.Execute(node);

        // La validación del servicio impide soltar una carpeta dentro de sí misma.
        _dragPressArgs = e;
        _dragStartPoint = e.GetPosition(null);
        _dragging = false;
    }

    private async void TreeRow_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging || _dragPressArgs is null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not Border { DataContext: NoteTreeItemViewModel node } || node.IsRenaming) return;

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
            _dragGhost.Hide();
        }
    }

    private void TreeRow_DragEnter(object? sender, DragEventArgs e) => UpdateTreeRowDragOver(sender, e, true);
    private void TreeRow_DragOver(object? sender, DragEventArgs e) => UpdateTreeRowDragOver(sender, e, true);
    private void TreeRow_DragLeave(object? sender, DragEventArgs e) => UpdateTreeRowDragOver(sender, e, false);

    /// <summary>Solo las carpetas aceptan una nota soltada encima — una nota sobre otra nota no significa
    /// nada, así que ni se resalta ni se marca el gesto como válido.</summary>
    private void UpdateTreeRowDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Border { DataContext: NoteTreeItemViewModel node }) return;

        var valid = node.IsFolder && CanDropInto(e, node.FullPath);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        node.IsDropTarget = isOver && valid;
        if (node.IsFolder && e.DataTransfer.Contains(NoteDragFormat) && !valid) e.Handled = true;
    }

    private void TreeRow_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: NoteTreeItemViewModel node }) return;
        node.IsDropTarget = false;
        if (!node.IsFolder || Vm is not { } vm) return;
        if (!CanDropInto(e, node.FullPath))
        {
            if (e.DataTransfer.Contains(NoteDragFormat)) e.Handled = true;
            return;
        }

        var sourcePath = e.DataTransfer.TryGetValue(NoteDragFormat);
        if (sourcePath is not null)
        {
            if (System.IO.Directory.Exists(sourcePath)) vm.MoveFolderTo(sourcePath, node.FullPath);
            else vm.MoveTreeNoteToFolder(sourcePath, node.FullPath);
            e.Handled = true;
        }
    }

    // ===================== Fondo del árbol: soltar en la raíz de la sesión =====================

    private void TreeRoot_DragEnter(object? sender, DragEventArgs e) => UpdateTreeRootDragOver(e);
    private void TreeRoot_DragOver(object? sender, DragEventArgs e) => UpdateTreeRootDragOver(e);
    private static void TreeRoot_DragLeave(object? sender, DragEventArgs e) => e.DragEffects = DragDropEffects.None;

    private void UpdateTreeRootDragOver(DragEventArgs e) =>
        e.DragEffects = Vm is { } vm && CanDropInto(e, vm.EffectiveRootPath)
            ? DragDropEffects.Move : DragDropEffects.None;

    private bool CanDropInto(DragEventArgs e, string destination)
    {
        if (Vm is not { IsSelectedSessionLocked: false }) return false;
        var source = e.DataTransfer.TryGetValue(NoteDragFormat);
        return source is not null && (!System.IO.Directory.Exists(source) ||
            !enki.Services.VaultService.IsWithinFolder(destination, source));
    }

    private void TreeRoot_Drop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;

        var sourcePath = e.DataTransfer.TryGetValue(NoteDragFormat);
        if (sourcePath is not null)
        {
            if (System.IO.Directory.Exists(sourcePath)) vm.MoveFolderTo(sourcePath, vm.EffectiveRootPath);
            else vm.MoveTreeNoteToFolder(sourcePath, vm.EffectiveRootPath);
        }
    }

    // ===================== Resultados de búsqueda =====================

    private void SearchResult_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control row || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
        row.Focus();
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

        // El clic fue en la propia fila que se renombra (ver TreeRow_PointerPressed): no es un "clic
        // fuera", así que se mantiene la edición y se devuelve el foco al cuadro en vez de confirmar.
        if (_keepRenamingThroughLostFocus)
        {
            if (sender is TextBox box) FocusRenameBox(box);
            return;
        }

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

        var delta = enki.Controls.SidePanelGrid.GetResizeDelta((Control)sender!,
            e.GetPosition(this).X - _resizeStartX);
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
