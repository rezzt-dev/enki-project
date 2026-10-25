using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Mecánica de vista del modo explorador de Skills: número de columnas de la rejilla, renombrado
/// en línea de carpetas y arrastrar-y-soltar (mover una skill a una carpeta, e importar archivos soltados
/// desde el explorador del sistema). Cualquier cosa que toque la biblioteca vive en
/// <see cref="SkillsViewModel"/>; aquí solo se traduce el gesto al comando.</summary>
public partial class SkillsBrowserView : UserControl
{
    /// <summary>Formato interno del arrastre de una tarjeta de skill. Transporta la ruta del archivo —lo
    /// único que identifica de forma estable a una skill desde que hay carpetas homónimas— y no el objeto,
    /// porque <c>DataTransfer</c> solo mueve cadenas entre controles.</summary>
    private static readonly DataFormat<string> SkillDragFormat =
        DataFormat.CreateStringApplicationFormat("com.enki.skill-path");

    private PointerPressedEventArgs? _dragPressArgs;
    private Point _dragStartPoint;
    private bool _dragging;

    public SkillsBrowserView()
    {
        InitializeComponent();

        // El número de columnas se calcula desde el ancho disponible (ver ApplyGridColumns), así que hay
        // que recalcularlo cada vez que ese ancho cambia — si no, quedaría un hueco irregular a la derecha
        // de cada fila en vez de estirarse las tarjetas para llenarlo.
        SkillsGridBorder.SizeChanged += (_, _) => ApplyGridColumns();
    }

    private SkillsViewModel? Vm => DataContext as SkillsViewModel;

    /// <summary>Elige cuántas columnas de igual ancho tiene la rejilla para que las tarjetas llenen la
    /// fila de lado a lado (248px es el ancho mínimo legible de una tarjeta antes de añadir otra columna).
    /// El UniformGrid vive dentro de un ItemsPanelTemplate, que tiene su propio namescope, así que su
    /// x:Name nunca enlaza con un campo de esta clase — hay que buscarlo a través del Presenter.Panel ya
    /// realizado del ItemsControl.</summary>
    private void ApplyGridColumns()
    {
        if (SkillsItemsControl.Presenter?.Panel is not UniformGrid grid) return;

        const double cardMinWidth = 248;
        const double cardSpacing = 16;
        var chrome = SkillsItemsControl.Margin.Left + SkillsItemsControl.Margin.Right;
        var available = SkillsGridBorder.Bounds.Width - chrome;
        grid.Columns = available > 0
            ? Math.Max(1, (int)((available + cardSpacing) / (cardMinWidth + cardSpacing)))
            : 1;
    }

    // ===================== Selección de carpeta =====================

    private void FolderRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: SkillFolderViewModel folder } && Vm is { } vm)
            vm.SelectFolderCommand.Execute(folder);
    }

    private void RootFolderRow_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        Vm?.SelectFolderCommand.Execute(null);

    // ===================== Renombrado en línea de carpetas =====================

    private void FolderNameEdit_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: SkillFolderViewModel folder }) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Vm?.CommitRenameFolderCommand.Execute(folder);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            folder.CancelRenameCommand.Execute(null);
        }
    }

    private void FolderNameEdit_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: SkillFolderViewModel { IsRenaming: true } folder })
            Vm?.CommitRenameFolderCommand.Execute(folder);
    }

    private void FolderNameEdit_Initialized(object? sender, EventArgs e)
    {
        if (sender is TextBox { IsVisible: true } textBox) FocusRenameBox(textBox);
    }

    private void FolderNameEdit_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty) return;
        if (sender is TextBox { IsVisible: true } textBox) FocusRenameBox(textBox);
    }

    private static void FocusRenameBox(TextBox textBox)
    {
        Dispatcher.UIThread.Post(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        });
    }

    // ===================== Origen del arrastre: tarjetas de skill =====================

    private void SkillCard_Initialized(object? sender, EventArgs e)
    {
        if (sender is not Control control) return;

        // handledEventsToo porque la tarjeta es un Button: sin esto, el Button consume el pressed y el
        // arrastre nunca llegaría a empezar.
        control.AddHandler(InputElement.PointerPressedEvent, SkillCard_PointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        control.AddHandler(InputElement.PointerMovedEvent, SkillCard_PointerMoved,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void SkillCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragPressArgs = e;
        _dragStartPoint = e.GetPosition(null);
        _dragging = false;
    }

    private async void SkillCard_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging || _dragPressArgs is null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { DataContext: SkillViewModel skill }) return;

        var current = e.GetPosition(null);
        var dx = current.X - _dragStartPoint.X;
        var dy = current.Y - _dragStartPoint.Y;
        if (dx * dx + dy * dy < 16) return; // ~4px de umbral

        _dragging = true;
        var pressArgs = _dragPressArgs;
        _dragPressArgs = null;

        try
        {
            using var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(SkillDragFormat, skill.FilePath));
            await DragDrop.DoDragDropAsync(pressArgs, transfer, DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
        }
    }

    // ===================== Destino del arrastre: filas de carpeta =====================

    private void FolderRow_DragEnter(object? sender, DragEventArgs e) => UpdateFolderDragOver(sender, e, true);
    private void FolderRow_DragOver(object? sender, DragEventArgs e) => UpdateFolderDragOver(sender, e, true);
    private void FolderRow_DragLeave(object? sender, DragEventArgs e) => UpdateFolderDragOver(sender, e, false);

    private static void UpdateFolderDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Border { DataContext: SkillFolderViewModel folder }) return;

        var valid = e.DataTransfer.Contains(SkillDragFormat) || e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = valid ? (e.DataTransfer.Contains(SkillDragFormat) ? DragDropEffects.Move : DragDropEffects.Copy) : DragDropEffects.None;
        e.Handled = true;
        folder.IsDropTarget = isOver && valid;
    }

    private void FolderRow_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: SkillFolderViewModel folder }) return;
        folder.IsDropTarget = false;
        MoveDraggedSkill(e, folder.FullPath);
    }

    // ===================== Destino del arrastre: tarjetas de carpeta de la rejilla =====================

    private void FolderCard_DragEnter(object? sender, DragEventArgs e) => UpdateCardDragOver(sender, e, true);
    private void FolderCard_DragOver(object? sender, DragEventArgs e) => UpdateCardDragOver(sender, e, true);

    private static void UpdateCardDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        e.DragEffects = e.DataTransfer.Contains(SkillDragFormat) ? DragDropEffects.Move :
            e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        if (sender is Control { DataContext: SkillFolderCardViewModel card }) card.IsDropTarget = isOver && e.DragEffects != DragDropEffects.None;
        e.Handled = true;
    }

    private void FolderCard_DragLeave(object? sender, DragEventArgs e) => UpdateCardDragOver(sender, e, false);

    private void FolderCard_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Control { DataContext: SkillFolderCardViewModel card }) return;
        card.IsDropTarget = false;
        if (string.IsNullOrEmpty(card.FullPath)) return;
        MoveDraggedSkill(e, card.FullPath);
    }

    // ===================== Destino del arrastre: fila raíz ("TODAS") =====================

    private void RootFolderRow_DragEnter(object? sender, DragEventArgs e) => UpdateRootDragOver(e, true);
    private void RootFolderRow_DragOver(object? sender, DragEventArgs e) => UpdateRootDragOver(e, true);
    private void RootFolderRow_DragLeave(object? sender, DragEventArgs e) => UpdateRootDragOver(e, false);

    private void UpdateRootDragOver(DragEventArgs e, bool isOver)
    {
        var valid = e.DataTransfer.Contains(SkillDragFormat) || e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = valid ? (e.DataTransfer.Contains(SkillDragFormat) ? DragDropEffects.Move : DragDropEffects.Copy) : DragDropEffects.None;
        e.Handled = true;
        if (Vm is { } vm) vm.IsRootDropTarget = isOver && valid;
    }

    private void RootFolderRow_Drop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.IsRootDropTarget = false;
        MoveDraggedSkill(e, vm.LibraryRootPath);
    }

    private void MoveDraggedSkill(DragEventArgs e, string destinationFolderPath)
    {
        if (Vm is not { } vm) return;

        var sourcePath = e.DataTransfer.TryGetValue(SkillDragFormat);
        e.Handled = true;
        vm.IsGridDropTarget = false;
        if (sourcePath is null)
        {
            var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths is { Count: > 0 }) vm.BeginImport(paths, destinationFolderPath);
            return;
        }

        var skill = vm.FindSkillByPath(sourcePath);
        if (skill is not null) vm.MoveSkillToFolder(skill, destinationFolderPath);
    }

    // ===================== Destino del arrastre: la rejilla (importar desde el SO) =====================
    // Mismo mecanismo que la rejilla de Archivos (docs/agent-docs/seccion-archivos.md §8): DataFormat.File
    // es el formato que DataTransfer expone para lo soltado desde el explorador del sistema, resuelto a
    // rutas locales y entregado al mismo camino de importación que usa el botón "IMPORTAR" — así que la
    // resolución de colisiones y la carpeta de destino son exactamente las mismas.

    private void SkillsGrid_DragEnter(object? sender, DragEventArgs e) => UpdateGridDragOver(e, true);
    private void SkillsGrid_DragOver(object? sender, DragEventArgs e) => UpdateGridDragOver(e, true);
    private void SkillsGrid_DragLeave(object? sender, DragEventArgs e) => UpdateGridDragOver(e, false);

    private void UpdateGridDragOver(DragEventArgs e, bool isOver)
    {
        var valid = e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(SkillDragFormat);
        e.DragEffects = valid ? (e.DataTransfer.Contains(SkillDragFormat) ? DragDropEffects.Move : DragDropEffects.Copy) : DragDropEffects.None;
        if (Vm is { } vm) vm.IsGridDropTarget = isOver && valid;
    }

    private void SkillsGrid_Drop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.IsGridDropTarget = false;

        MoveDraggedSkill(e, vm.CurrentFolderPath);
    }
}
