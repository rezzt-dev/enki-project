using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Code-behind del modo árbol de Archivos: selección y renombrado en línea de las filas, arrastrar
/// un archivo a otra carpeta, importar lo que se suelte desde el explorador del sistema y el separador que
/// ajusta el ancho del panel lateral. Mismo reparto que <see cref="SkillsTreeView"/> y
/// <see cref="SimpleNotesView"/>, de los que esta vista es la adaptación: solo mecánica de vista — todo lo
/// que toca el vault vive en <see cref="FilesViewModel"/>.</summary>
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

    public FilesTreeView()
    {
        InitializeComponent();

        AddHandler(PointerPressedEvent, OnTreePreviewPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        DataContextChanged += (_, _) => SubscribeToViewModel();
        Loaded += (_, _) => SubscribeToViewModel();
        Unloaded += (_, _) =>
        {
            if (Vm is { } vm) vm.PropertyChanged -= OnViewModelPropertyChanged;
        };
    }

    private FilesViewModel? Vm => DataContext as FilesViewModel;

    private void SubscribeToViewModel()
    {
        if (Vm is not { } vm) return;
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>Entrar en modo búsqueda pone el cursor en el cuadro: pulsar la lupa es inequívocamente
    /// "quiero escribir una búsqueda", y obligar a un segundo clic para empezar a teclear sería fricción
    /// gratuita.</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FilesViewModel.IsTreeSearchMode)) return;
        if (Vm is not { IsTreeSearchMode: true }) return;

        Dispatcher.UIThread.Post(() =>
        {
            TreeSearchBox.Focus();
            TreeSearchBox.SelectAll();
        }, DispatcherPriority.Input);
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
        if (sender is not Border { DataContext: FileTreeItemViewModel node } || Vm is not { } vm) return;
        if (e.GetCurrentPoint(sender as Control).Properties.IsRightButtonPressed) return;

        // Mientras la fila está en modo renombrar es una superficie de edición, no un objetivo de clic: el
        // cuadro de texto solo ocupa una franja estrecha de una fila ancha, así que casi cualquier clic
        // para colocar el cursor cae en el Border, no en el TextBox.
        if (node.IsRenaming)
        {
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

        vm.SelectTreeItemCommand.Execute(node);

        // Solo los archivos se arrastran: mover una carpeta entera arrastra consigo todo su contenido y
        // aquí no hay ninguna confirmación de por medio.
        if (!node.IsFile) return;
        _dragPressArgs = e;
        _dragStartPoint = e.GetPosition(null);
        _dragging = false;
    }

    private async void TreeRow_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging || _dragPressArgs is null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not Border { DataContext: FileTreeItemViewModel node } || !node.IsFile || node.IsRenaming) return;

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
        }
    }

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

        var effects = node.IsFolder ? EffectsFor(e) : DragDropEffects.None;
        var valid = effects != DragDropEffects.None;
        node.IsDropTarget = isOver && valid;
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

        if (e.DataTransfer.TryGetValue(FilesBrowserView.FileDragFormat) is { } sourcePath)
        {
            vm.DropOnTreeNode(node, sourcePath);
            e.Handled = true;
            return;
        }

        if (TryGetDroppedPaths(e) is not { } paths) return;
        vm.ImportPaths(paths, node.FullPath);
        e.Handled = true;
    }

    // ===================== Fondo del árbol: soltar en la raíz efectiva =====================

    /// <summary>El fondo acepta las dos clases de arrastre que llegan hasta aquí: el interno de una fila
    /// (mover el archivo a la raíz efectiva) y el del explorador del sistema (importar), igual que la
    /// rejilla del modo explorador.</summary>
    private void TreeBackground_DragOver(object? sender, DragEventArgs e)
    {
        var effects = EffectsFor(e);
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

        if (e.DataTransfer.TryGetValue(FilesBrowserView.FileDragFormat) is { } sourcePath)
        {
            vm.DropOnTreeRoot(sourcePath);
            e.Handled = true;
            return;
        }

        if (TryGetDroppedPaths(e) is not { } paths) return;
        vm.ImportPaths(paths, vm.EffectiveRootPath);
        e.Handled = true;
    }

    // ===================== La vista entera: importar desde el explorador del sistema =====================

    /// <summary>Todo lo que las filas y el fondo del árbol no recogen acaba aquí: la previsualización de la
    /// derecha, el modo búsqueda del panel, su pie y el raíl del panel contraído. Un arrastre entrante del
    /// explorador del sistema se importa en la raíz efectiva se suelte donde se suelte, porque en esta
    /// disposición el árbol ocupa una franja estrecha y la previsualización casi toda la ventana: con solo
    /// el árbol como destino, soltar archivos aquí parecía sencillamente no funcionar.
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

        if (TryGetDroppedPaths(e) is { } paths) vm.ImportPaths(paths, vm.EffectiveRootPath);
    }

    /// <summary>Qué significa el arrastre que pasa por encima: mover un archivo del propio árbol, copiar
    /// (importar) lo que llega del explorador del sistema, o nada reconocible. Con una sesión protegida y
    /// bloqueada elegida no se acepta nada: escribir ahí dentro necesita una clave que no está en memoria,
    /// así que es mejor no ofrecer el gesto que aceptarlo y contestar con un aviso de error.</summary>
    private DragDropEffects EffectsFor(DragEventArgs e)
    {
        if (Vm is not { IsSelectedSessionLocked: false }) return DragDropEffects.None;
        if (e.DataTransfer.Contains(FilesBrowserView.FileDragFormat)) return DragDropEffects.Move;
        if (e.DataTransfer.Contains(DataFormat.File)) return DragDropEffects.Copy;
        return DragDropEffects.None;
    }

    /// <summary>Las rutas locales de un arrastre entrante del explorador del sistema, o null si no trae
    /// archivos o ninguno de ellos vive en disco (un adjunto remoto arrastrado desde el navegador, por
    /// ejemplo, no tiene ruta que importar).</summary>
    private static string[]? TryGetDroppedPaths(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null || files.Length == 0) return null;

        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToArray();
        return paths.Length > 0 ? paths : null;
    }

    // ===================== Resultados de búsqueda =====================

    private void SearchResult_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
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
        }
        else if (e.Key == Key.Escape)
        {
            node.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void TreeNameEdit_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileTreeItemViewModel node } box || !node.IsRenaming) return;

        if (_keepRenamingThroughLostFocus)
        {
            FocusRenameBox(box);
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
