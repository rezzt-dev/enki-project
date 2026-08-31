using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Code-behind del modo árbol de Skills: selección y renombrado en línea de las filas, arrastrar
/// una skill a otra carpeta y el separador que ajusta el ancho del panel lateral. Mismo reparto que
/// <see cref="SimpleNotesView"/>, del que esta vista es la adaptación: solo mecánica de vista — todo lo
/// que toca la biblioteca vive en <see cref="SkillsViewModel"/>.</summary>
public partial class SkillsTreeView : UserControl
{
    /// <summary>Formato propio del arrastre de skills, distinto del de notas y del de archivos: una skill
    /// solo se puede soltar dentro de la biblioteca de skills, así que no debe aceptarse en los árboles de
    /// las otras secciones ni al revés.</summary>
    private static readonly DataFormat<string> SkillDragFormat =
        DataFormat.CreateStringApplicationFormat("com.enki.skill-path");

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
    /// la vista simplificada de Notas.</summary>
    private bool _keepRenamingThroughLostFocus;

    public SkillsTreeView()
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

    private SkillsViewModel? Vm => DataContext as SkillsViewModel;

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
        if (e.PropertyName != nameof(SkillsViewModel.IsTreeSearchMode)) return;
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
            if (ancestor is StyledElement { DataContext: SkillTreeItemViewModel { IsRenaming: true } })
            {
                _keepRenamingThroughLostFocus = true;
                Dispatcher.UIThread.Post(() => _keepRenamingThroughLostFocus = false, DispatcherPriority.Input);
                return;
            }
        }
    }

    private void TreeRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: SkillTreeItemViewModel node } || Vm is not { } vm) return;
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

        // Solo las skills se arrastran: mover una carpeta entera arrastra consigo todo su contenido y
        // aquí no hay ninguna confirmación de por medio — para eso está su menú contextual.
        if (!node.IsSkill) return;
        _dragPressArgs = e;
        _dragStartPoint = e.GetPosition(null);
        _dragging = false;
    }

    /// <summary>Doble clic sobre una skill abre su editor. La selección simple deja el detalle a la
    /// derecha, igual que en la rejilla: seleccionar y editar siguen siendo dos gestos distintos.</summary>
    private void TreeRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { DataContext: SkillTreeItemViewModel { IsSkill: true } node }) Vm?.OpenTreeItemCommand.Execute(node);
    }

    private async void TreeRow_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging || _dragPressArgs is null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not Border { DataContext: SkillTreeItemViewModel node } || !node.IsSkill || node.IsRenaming) return;

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
            transfer.Add(DataTransferItem.Create(SkillDragFormat, node.FullPath));
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

    /// <summary>Solo las carpetas aceptan una skill soltada encima — una skill sobre otra skill no
    /// significa nada, así que ni se resalta ni se marca el gesto como válido.</summary>
    private static void UpdateTreeRowDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Border { DataContext: SkillTreeItemViewModel node }) return;

        var valid = node.IsFolder && e.DataTransfer.Contains(SkillDragFormat);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        node.IsDropTarget = isOver && valid;
    }

    private void TreeRow_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: SkillTreeItemViewModel node }) return;
        node.IsDropTarget = false;
        if (!node.IsFolder || Vm is not { } vm) return;

        var sourcePath = e.DataTransfer.TryGetValue(SkillDragFormat);
        if (sourcePath is not null) vm.DropOnTreeNode(node, sourcePath);
    }

    // ===================== Fondo del árbol: soltar en la raíz de la biblioteca =====================

    private static void TreeBackground_DragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(SkillDragFormat) ? DragDropEffects.Move : DragDropEffects.None;

    private void TreeBackground_Drop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;

        var sourcePath = e.DataTransfer.TryGetValue(SkillDragFormat);
        if (sourcePath is not null) vm.DropOnTreeRoot(sourcePath);
    }

    // ===================== Resultados de búsqueda =====================

    private void SearchResult_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: SkillViewModel skill }) Vm?.SelectSkillCommand.Execute(skill);
    }

    // ===================== Renombrado en línea =====================

    private void TreeNameEdit_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: SkillTreeItemViewModel node }) return;

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
        if (sender is not TextBox { DataContext: SkillTreeItemViewModel node } box || !node.IsRenaming) return;

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
            SkillsViewModel.TreeSidebarMinWidth, SkillsViewModel.TreeSidebarMaxWidth);
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
