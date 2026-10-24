using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.ViewModels;
using enki.Services;

namespace enki.Views;

/// <summary>Code-behind del modo árbol de Archivos: selección, teclado y renombrado en línea de las filas,
/// arrastrar archivos y carpetas, importar lo que se suelte desde el explorador del sistema y el separador
/// que ajusta el ancho del panel lateral. Mismo reparto que <see cref="SkillsTreeView"/> y
/// <see cref="SimpleNotesView"/>, de los que esta vista es la adaptación: solo mecánica de vista — todo lo
/// que toca el vault, y la propia selección, vive en <see cref="FilesViewModel"/>.</summary>
public partial class FilesTreeView : UserControl
{
    private PointerPressedEventArgs? _dragPressArgs;
    private Point _dragStartPoint;
    private bool _dragging;

    private bool _resizingSidebar;
    private double _resizeStartX;
    private double _resizeStartWidth;

    /// <summary>Alzado por <see cref="OnTreePreviewPointerPressed"/> cuando el clic cae en la fila que se
    /// está renombrando: le dice a <see cref="TreeNameEdit_LostFocus"/> que la pérdida de foco que viene a
    /// continuación es ese mismo clic —recolocar el cursor, o un roce con el Border que rodea al cuadro
    /// estrecho— y no un "clic fuera" que deba confirmar. Mismo mecanismo, y por el mismo motivo, que en
    /// la vista simplificada de Notas y en el árbol de Skills.</summary>
    private bool _keepRenamingThroughLostFocus;

    private FilesViewModel? _subscribedViewModel;

    public FilesTreeView()
    {
        InitializeComponent();
        _dragGhost = new ExplorerDragGhost(this, DragSurface, FilesBrowserView.FileDragFormat);
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || Vm is not { IsTreeNavigatorOpen: true } vm) return;
            vm.CloseTreeNavigatorCommand.Execute(null);
            e.Handled = true;
        };

        AddHandler(PointerPressedEvent, OnTreePreviewPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        DataContextChanged += (_, _) => SubscribeToViewModel();
        Loaded += (_, _) => SubscribeToViewModel();
        Unloaded += (_, _) =>
        {
            if (_subscribedViewModel is not null) _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        };
    }

    private readonly ExplorerDragGhost _dragGhost;

    private FilesViewModel? Vm => DataContext as FilesViewModel;

    private void SubscribeToViewModel()
    {
        if (ReferenceEquals(_subscribedViewModel, Vm)) return;
        if (_subscribedViewModel is not null) _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _subscribedViewModel = Vm;
        if (_subscribedViewModel is not null) _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (e.PropertyName == nameof(FilesViewModel.IsTreeNavigatorOpen) && !vm.IsTreeNavigatorOpen)
        {
            if (TreeNavigator.IsKeyboardFocusWithin)
                (vm.IsTreeSearchMode ? RailSearchButton : RailTreeButton).Focus();
            return;
        }

        switch (e.PropertyName)
        {
            // Entrar en modo búsqueda pone el cursor en el cuadro: pulsar la lupa es inequívocamente "quiero
            // escribir una búsqueda", y obligar a un segundo clic para empezar a teclear sería fricción gratuita.
            // Salir devuelve el foco al árbol, para seguir con el teclado donde se estaba.
            case nameof(FilesViewModel.IsTreeNavigatorOpen):
            case nameof(FilesViewModel.IsTreeSidebarVisible):
            case nameof(FilesViewModel.IsTreeSearchMode):
                Dispatcher.UIThread.Post(() =>
                {
                    if (!vm.IsTreeNavigatorVisible) return;
                    if (vm.IsTreeSearchMode)
                    {
                        TreeSearchBox.Focus();
                        TreeSearchBox.SelectAll();
                    }
                    else
                    {
                        TreeBody.Focus();
                    }
                }, DispatcherPriority.Input);
                break;

            // Abrir un archivo desde fuera del árbol (anterior/siguiente en la previsualización, importar) lo
            // deja seleccionado aquí; se trae a la vista para que el árbol no señale algo fuera de pantalla.
            case nameof(FilesViewModel.ActivePreview):
                if (!vm.IsTreeSearchMode && vm.SelectedTreeNode is { } node) BringNodeIntoView(node);
                break;
        }
    }

    private void NavigatorDismiss_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Vm?.CloseTreeNavigatorCommand.Execute(null);
        e.Handled = true;
    }

    // ===================== Filas del árbol =====================

    private void OnTreePreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; // el clic derecho abre el menú contextual

        foreach (var ancestor in source.GetSelfAndVisualAncestors())
        {
            if (ancestor is StyledElement { DataContext: FileTreeItemViewModel { IsRenaming: true } })
            {
                _keepRenamingThroughLostFocus = true;
                Dispatcher.UIThread.Post(() => _keepRenamingThroughLostFocus = false, DispatcherPriority.Input);
                return;
            }
        }
    }

    private void TreeRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: FileTreeItemViewModel node } row || Vm is not { } vm) return;
        if (e.GetCurrentPoint(row).Properties.IsRightButtonPressed) return;

        // Mientras la fila está en modo renombrar es una superficie de edición, no un objetivo de clic: el
        // cuadro de texto solo ocupa una franja estrecha de una fila ancha, así que casi cualquier clic
        // para colocar el cursor cae en el Border, no en el TextBox.
        if (node.IsRenaming)
        {
            if (row.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.IsVisible) is { } renameBox)
                RefocusRenameBox(renameBox);
            e.Handled = true;
            return;
        }

        vm.SelectTreeItemCommand.Execute(node);

        // El foco pasa al cuerpo del árbol, que es quien atiende las flechas, Intro, F2 y Supr: sin esto, tras
        // pulsar una fila el teclado seguía en lo último que tuvo el foco.
        TreeBody.Focus();

        // La validación del servicio impide mover una carpeta dentro de sí misma.
        _dragPressArgs = e;
        _dragStartPoint = e.GetPosition(null);
        _dragging = false;
    }

    private async void TreeRow_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging || _dragPressArgs is null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not Border { DataContext: FileTreeItemViewModel node } || node.IsRenaming) return;

        var current = e.GetPosition(null);
        var dx = current.X - _dragStartPoint.X;
        var dy = current.Y - _dragStartPoint.Y;
        if (dx * dx + dy * dy < 16) return; // ~4px, mismo umbral que el resto de la app

        _dragging = true;
        var pressArgs = _dragPressArgs;
        _dragPressArgs = null;

        try
        {
            using var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(FilesBrowserView.FileDragFormat, node.FullPath));
            await DragDrop.DoDragDropAsync(pressArgs, transfer, DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
            _dragGhost.Hide();
        }
    }

    // ===================== Teclado =====================

    /// <summary>Navegación del árbol con el teclado mientras su cuerpo tiene el foco. La selección y lo que
    /// significa cada tecla viven en el viewmodel; aquí solo se traduce la tecla y se lleva a la vista la fila
    /// que queda seleccionada.</summary>
    private void TreeBody_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Source is TextBox || Vm is not { } vm || e.KeyModifiers != KeyModifiers.None) return;

        FileTreeItemViewModel? selected = null;
        switch (e.Key)
        {
            case Key.Down:
                selected = vm.MoveTreeSelection(1);
                break;
            case Key.Up:
                selected = vm.MoveTreeSelection(-1);
                break;
            case Key.PageDown:
                selected = vm.MoveTreeSelection(10);
                break;
            case Key.PageUp:
                selected = vm.MoveTreeSelection(-10);
                break;
            case Key.Home:
                selected = vm.MoveTreeSelection(int.MinValue / 2);
                break;
            case Key.End:
                selected = vm.MoveTreeSelection(int.MaxValue / 2);
                break;
            case Key.Right:
                selected = vm.ExpandTreeSelection();
                break;
            case Key.Left:
                selected = vm.CollapseTreeSelection();
                break;
            case Key.Enter:
                vm.ActivateTreeSelection();
                break;
            case Key.F2:
                vm.SelectedTreeNode?.BeginRenameCommand.Execute(null);
                break;
            case Key.Delete:
                if (vm.SelectedTreeNode is { } node) vm.DeleteTreeItemCommand.Execute(node);
                break;
            default:
                return;
        }

        e.Handled = true;
        if (selected is not null) BringNodeIntoView(selected);
    }

    private void BringNodeIntoView(FileTreeItemViewModel node) => Dispatcher.UIThread.Post(() =>
    {
        var row = TreeBody.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("folder-row") && ReferenceEquals(b.DataContext, node));
        row?.BringIntoView();
    }, DispatcherPriority.Loaded);

    private void TreeSearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (e.Key == Key.Enter)
        {
            vm.OpenFirstTreeSearchResult();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (vm.IsTreeNavigatorOpen) vm.CloseTreeNavigatorCommand.Execute(null);
            else vm.ShowTreeBrowseCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ===================== Destinos de arrastre: filas =====================

    private void TreeRow_DragEnter(object? sender, DragEventArgs e) => UpdateTreeRowDragOver(sender, e, true);
    private void TreeRow_DragOver(object? sender, DragEventArgs e) => UpdateTreeRowDragOver(sender, e, true);
    private void TreeRow_DragLeave(object? sender, DragEventArgs e) => UpdateTreeRowDragOver(sender, e, false);

    /// <summary>Solo las carpetas aceptan algo soltado encima —un archivo sobre otro archivo no significa
    /// nada—, y aceptan las dos clases de arrastre por igual: mover ahí dentro un archivo del propio árbol,
    /// e importar ahí dentro lo que venga del explorador del sistema. Lo que la fila no acepta lo deja
    /// burbujear a propósito: detrás están el fondo del árbol y la vista entera, que lo recogen como "a la
    /// raíz efectiva".</summary>
    private void UpdateTreeRowDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Border { DataContext: FileTreeItemViewModel node }) return;

        var effects = node.IsFolder ? EffectsFor(e, node.FullPath) : DragDropEffects.None;
        var valid = effects != DragDropEffects.None;
        node.IsDropTarget = isOver && valid;
        if (node.IsFolder && e.DataTransfer.Contains(FilesBrowserView.FileDragFormat) && !valid)
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        if (!isOver || !valid) return;

        // Mientras la carpeta es el destino, el realce de la vista entera sobra: encendidos los dos a la
        // vez no dirían en cuál de los dos sitios va a caer el archivo.
        if (Vm is { } vm) vm.IsGridDropTarget = false;
        e.DragEffects = effects;
        e.Handled = true;
    }

    private void TreeRow_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: FileTreeItemViewModel node }) return;
        node.IsDropTarget = false;
        if (!node.IsFolder || Vm is not { } vm) return;
        if (EffectsFor(e, node.FullPath) == DragDropEffects.None)
        {
            if (e.DataTransfer.Contains(FilesBrowserView.FileDragFormat)) e.Handled = true;
            return;
        }

        if (e.DataTransfer.TryGetValue(FilesBrowserView.FileDragFormat) is { } sourcePath)
        {
            vm.DropOnTreeNode(node, sourcePath);
            e.Handled = true;
            return;
        }

        if (FilesBrowserView.TryGetDroppedPaths(e) is not { } paths) return;
        vm.ImportPaths(paths, node.FullPath);
        e.Handled = true;
    }

    // ===================== Fondo del árbol: soltar en la raíz efectiva =====================

    /// <summary>El fondo acepta las dos clases de arrastre que llegan hasta aquí: el interno de una fila
    /// (mover el elemento a la raíz efectiva) y el del explorador del sistema (importar), igual que la
    /// rejilla del modo explorador.</summary>
    private void TreeBackground_DragOver(object? sender, DragEventArgs e)
    {
        var effects = EffectsFor(e, Vm?.EffectiveRootPath);
        e.DragEffects = effects;
        if (effects == DragDropEffects.None) return;

        // El destino es el mismo que el de la vista entera —la raíz efectiva—, así que se enciende su mismo
        // realce en vez de uno propio del panel.
        if (Vm is { } vm) vm.IsGridDropTarget = effects == DragDropEffects.Copy;
        e.Handled = true;
    }

    private void TreeBackground_Drop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.IsGridDropTarget = false;
        if (EffectsFor(e, vm.EffectiveRootPath) == DragDropEffects.None) return;

        if (e.DataTransfer.TryGetValue(FilesBrowserView.FileDragFormat) is { } sourcePath)
        {
            vm.DropOnTreeRoot(sourcePath);
            e.Handled = true;
            return;
        }

        if (FilesBrowserView.TryGetDroppedPaths(e) is not { } paths) return;
        vm.ImportPaths(paths, vm.EffectiveRootPath);
        e.Handled = true;
    }

    // ===================== La vista entera: importar desde el explorador del sistema =====================

    /// <summary>Todo lo que las filas y el fondo del árbol no recogen acaba aquí: la previsualización de la
    /// derecha, el modo búsqueda del panel, su pie y el raíl del panel contraído. Un arrastre entrante del
    /// explorador del sistema se importa en la raíz efectiva se suelte donde se suelte, porque en esta
    /// disposición el árbol ocupa una franja estrecha y la previsualización casi toda la ventana.
    ///
    /// El arrastre interno de una fila no se acepta fuera del árbol —soltar un archivo del vault sobre su
    /// propia previsualización no significa nada—, y por eso este destino mira solo el
    /// <see cref="DataFormat.File"/> del SO.</summary>
    private void TreeView_DragEnter(object? sender, DragEventArgs e) => UpdateTreeViewDragOver(e, true);
    private void TreeView_DragOver(object? sender, DragEventArgs e) => UpdateTreeViewDragOver(e, true);
    private void TreeView_DragLeave(object? sender, DragEventArgs e) => UpdateTreeViewDragOver(e, false);

    private void UpdateTreeViewDragOver(DragEventArgs e, bool isOver)
    {
        var valid = EffectsFor(e) == DragDropEffects.Copy;
        e.DragEffects = valid ? DragDropEffects.Copy : DragDropEffects.None;
        if (Vm is { } vm) vm.IsGridDropTarget = isOver && valid;
    }

    private void TreeView_Drop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.IsGridDropTarget = false;
        if (EffectsFor(e) != DragDropEffects.Copy) return;

        if (FilesBrowserView.TryGetDroppedPaths(e) is { } paths) vm.ImportPaths(paths, vm.EffectiveRootPath);
    }

    /// <summary>Qué significa el arrastre que pasa por encima: mover un archivo del propio árbol, copiar
    /// (importar) lo que llega del explorador del sistema, o nada reconocible. Con una sesión protegida y
    /// bloqueada elegida no se acepta nada: escribir ahí dentro necesita una clave que no está en memoria,
    /// así que es mejor no ofrecer el gesto que aceptarlo y contestar con un aviso de error.</summary>
    private DragDropEffects EffectsFor(DragEventArgs e, string? destination = null)
    {
        if (Vm is not { IsSelectedSessionLocked: false }) return DragDropEffects.None;
        if (e.DataTransfer.TryGetValue(FilesBrowserView.FileDragFormat) is { } source)
        {
            if (destination is not null && Directory.Exists(source) && VaultService.IsWithinFolder(destination, source))
                return DragDropEffects.None;
            return DragDropEffects.Move;
        }
        if (e.DataTransfer.Contains(DataFormat.File)) return DragDropEffects.Copy;
        return DragDropEffects.None;
    }

    // ===================== Resultados de búsqueda =====================

    private void SearchResult_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control row || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
        row.Focus();
        if (sender is Border { DataContext: FileTreeItemViewModel node }) Vm?.SelectTreeItemCommand.Execute(node);
    }

    // ===================== Renombrado en línea =====================

    private void TreeNameEdit_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileTreeItemViewModel node }) return;

        if (e.Key == Key.Enter)
        {
            Vm?.CommitRenameTreeItemCommand.Execute(node);
            e.Handled = true;
            TreeBody.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            node.CancelRenameCommand.Execute(null);
            e.Handled = true;
            TreeBody.Focus();
        }
    }

    private void TreeNameEdit_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileTreeItemViewModel node } box || !node.IsRenaming) return;

        if (_keepRenamingThroughLostFocus)
        {
            RefocusRenameBox(box);
            return;
        }

        Vm?.CommitRenameTreeItemCommand.Execute(node);
    }

    private void TreeNameEdit_Initialized(object? sender, EventArgs e)
    {
        if (sender is TextBox box) FilesBrowserView.FocusRenameBox(box);
    }

    /// <summary>El TextBox de renombrar ya está en el árbol cuando <c>IsRenaming</c> pasa a true (solo
    /// cambia su visibilidad), así que Initialized no vuelve a dispararse: hay que darle el foco también
    /// aquí, cuando se hace visible.</summary>
    private void TreeNameEdit_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty || sender is not TextBox box) return;
        if (e.GetNewValue<bool>()) FilesBrowserView.FocusRenameBox(box);
    }

    /// <summary>Devuelve el foco al cuadro sin tocar la selección: el clic que lo quitó era para colocar el
    /// cursor, y volver a seleccionarlo todo deshacía justo eso.</summary>
    private static void RefocusRenameBox(TextBox box) =>
        Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Input);

    // ===================== Separador del panel lateral =====================

    private void SidebarGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Panel grip || Vm is not { } vm) return;

        _resizingSidebar = true;
        _resizeStartX = e.GetPosition(this).X;
        _resizeStartWidth = vm.TreeSidebarWidth;
        e.Pointer.Capture(grip);
    }

    private void SidebarGrip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizingSidebar || Vm is not { } vm) return;

        var delta = e.GetPosition(this).X - _resizeStartX;
        vm.TreeSidebarWidth = Math.Clamp(_resizeStartWidth + delta,
            FilesViewModel.TreeSidebarMinWidth, FilesViewModel.TreeSidebarMaxWidth);
    }

    /// <summary>El ancho se guarda al soltar, no en cada píxel del arrastre — escribir el JSON de ajustes
    /// decenas de veces por gesto no aportaría nada y sí ruido de E/S.</summary>
    private void SidebarGrip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizingSidebar) return;
        _resizingSidebar = false;
        e.Pointer.Capture(null);
        Vm?.PersistTreeSidebarWidth();
    }
}
