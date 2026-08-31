using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Mecánica de vista del modo explorador de Archivos: selección y renombrado en línea de las
/// filas de carpeta, renombrado de las tarjetas, arrastrar una tarjeta a otra carpeta e importar archivos
/// soltados desde el explorador del sistema. Cualquier cosa que toque el vault vive en
/// <see cref="FilesViewModel"/>; aquí solo se traduce el gesto al comando.</summary>
public partial class FilesBrowserView : UserControl
{
    /// <summary>Formato interno del arrastre de una tarjeta de archivo, compartido con el modo árbol
    /// (<see cref="FilesTreeView"/>): transporta la ruta completa del archivo, y ser el mismo formato en
    /// las dos disposiciones es lo que hace que un arrastre valga igual en cualquiera de ellas. Distinto
    /// del <c>DataFormat.File</c> de los arrastres entrantes desde el SO.</summary>
    internal static readonly DataFormat<string> FileDragFormat =
        DataFormat.CreateStringApplicationFormat("com.enki.file-path");

    private PointerPressedEventArgs? _dragPressArgs;
    private Point _dragStartPoint;
    private bool _dragging;

    public FilesBrowserView() => InitializeComponent();

    private FilesViewModel? Vm => DataContext as FilesViewModel;

    // ===================== Folder selection =====================

    private void FolderRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: FileFolderViewModel folder } && Vm is { } vm)
            vm.SelectFolderCommand.Execute(folder);
    }

    private void RootFolderRow_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        Vm?.SelectFolderCommand.Execute(null);

    // ===================== Inline folder rename =====================

    private void FolderNameEdit_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileFolderViewModel folder }) return;

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
        if (sender is TextBox { DataContext: FileFolderViewModel { IsRenaming: true } folder })
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

    // ===================== Inline file rename =====================

    private void FileNameEdit_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileCardViewModel card }) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Vm?.CommitRenameFileCommand.Execute(card);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            card.CancelRenameCommand.Execute(null);
        }
    }

    private void FileNameEdit_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: FileCardViewModel { IsRenaming: true } card })
            Vm?.CommitRenameFileCommand.Execute(card);
    }

    private void FileNameEdit_Initialized(object? sender, EventArgs e)
    {
        if (sender is TextBox { IsVisible: true } textBox) FocusRenameBox(textBox);
    }

    private void FileNameEdit_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty) return;
        if (sender is TextBox { IsVisible: true } textBox) FocusRenameBox(textBox);
    }

    // ===================== Drag source: file cards (reorder into a folder) =====================

    private void FileCard_Initialized(object? sender, EventArgs e)
    {
        if (sender is not Control control) return;
        if (control.DataContext is FileCardViewModel card) card.EnsureThumbnailLoaded();

        control.AddHandler(InputElement.PointerPressedEvent, FileCard_PointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        control.AddHandler(InputElement.PointerMovedEvent, FileCard_PointerMoved,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void FileCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragPressArgs = e;
        _dragStartPoint = e.GetPosition(null);
        _dragging = false;
    }

    private async void FileCard_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging || _dragPressArgs is null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { DataContext: FileCardViewModel card }) return;

        var current = e.GetPosition(null);
        var dx = current.X - _dragStartPoint.X;
        var dy = current.Y - _dragStartPoint.Y;
        if (dx * dx + dy * dy < 16) return; // ~4px threshold

        _dragging = true;
        var pressArgs = _dragPressArgs;
        _dragPressArgs = null;

        try
        {
            using var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(FileDragFormat, card.File.FullPath));
            await DragDrop.DoDragDropAsync(pressArgs, transfer, DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
        }
    }

    // ===================== Drop targets: folder rows =====================
    // Las filas del panel de carpetas aceptan las dos clases de arrastre, igual que las filas del modo
    // árbol (FilesTreeView.UpdateTreeRowDragOver): mover ahí dentro una tarjeta de esta misma sección, e
    // importar ahí dentro lo que venga del explorador del sistema. Durante un tiempo solo aceptaron la
    // primera, y con el árbol importando a la carpeta sobre la que se suelta la asimetría no se sostenía:
    // la misma carpeta, arrastrada lo mismo desde el mismo sitio, aceptaba o rechazaba según la disposición
    // en la que se estuviera mirando.

    private void FolderRow_DragEnter(object? sender, DragEventArgs e) => UpdateFolderDragOver(sender, e, true);
    private void FolderRow_DragOver(object? sender, DragEventArgs e) => UpdateFolderDragOver(sender, e, true);
    private void FolderRow_DragLeave(object? sender, DragEventArgs e) => UpdateFolderDragOver(sender, e, false);

    private void UpdateFolderDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Border { DataContext: FileFolderViewModel folder }) return;

        var effects = EffectsFor(e);
        e.DragEffects = effects;
        folder.IsDropTarget = isOver && effects != DragDropEffects.None;
    }

    private void FolderRow_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: FileFolderViewModel folder }) return;
        folder.IsDropTarget = false;
        if (Vm is not { } vm) return;

        if (e.DataTransfer.TryGetValue(FileDragFormat) is { } sourcePath)
        {
            var card = vm.FindTreeCardByPath(sourcePath);
            if (card is not null) vm.MoveFileToFolder(card, folder.FullPath);
            return;
        }

        if (TryGetDroppedPaths(e) is { } paths) vm.ImportPaths(paths, folder.FullPath);
    }

    // ===================== Drop target: root ("TODOS") row =====================

    private void RootFolderRow_DragEnter(object? sender, DragEventArgs e) => UpdateRootDragOver(e, true);
    private void RootFolderRow_DragOver(object? sender, DragEventArgs e) => UpdateRootDragOver(e, true);
    private void RootFolderRow_DragLeave(object? sender, DragEventArgs e) => UpdateRootDragOver(e, false);

    private void UpdateRootDragOver(DragEventArgs e, bool isOver)
    {
        var effects = EffectsFor(e);
        e.DragEffects = effects;
        if (Vm is { } vm) vm.IsRootDropTarget = isOver && effects != DragDropEffects.None;
    }

    private void RootFolderRow_Drop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.IsRootDropTarget = false;

        if (e.DataTransfer.TryGetValue(FileDragFormat) is { } sourcePath)
        {
            var card = vm.FindTreeCardByPath(sourcePath);
            if (card is not null) vm.MoveFileToFolder(card, vm.EffectiveRootPath);
            return;
        }

        if (TryGetDroppedPaths(e) is { } paths) vm.ImportPaths(paths, vm.EffectiveRootPath);
    }

    /// <summary>Qué significa el arrastre que pasa por encima de una fila del panel de carpetas: mover una
    /// tarjeta de esta misma sección, copiar (importar) lo que llega del explorador del sistema, o nada
    /// reconocible. Gemelo de <c>FilesTreeView.EffectsFor</c>, incluido su motivo para no aceptar nada con
    /// una sesión protegida y bloqueada elegida: escribir ahí dentro necesita una clave que no está en
    /// memoria (ver <see cref="FilesViewModel.ImportPaths"/>), así que es mejor no ofrecer el gesto que
    /// aceptarlo y contestar con un aviso de error.</summary>
    private DragDropEffects EffectsFor(DragEventArgs e)
    {
        if (Vm is not { IsSelectedSessionLocked: false }) return DragDropEffects.None;
        if (e.DataTransfer.Contains(FileDragFormat)) return DragDropEffects.Move;
        if (e.DataTransfer.Contains(DataFormat.File)) return DragDropEffects.Copy;
        return DragDropEffects.None;
    }

    /// <summary>Las rutas locales de un arrastre entrante del explorador del sistema, o null si no trae
    /// archivos o ninguno de ellos vive en disco (un adjunto remoto arrastrado desde el navegador, por
    /// ejemplo, no tiene ruta que importar). Misma criba que la del modo árbol.</summary>
    private static string[]? TryGetDroppedPaths(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null || files.Length == 0) return null;

        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToArray();
        return paths.Length > 0 ? paths : null;
    }

    // ===================== Drop target: file grid (import from the OS file explorer) =====================
    // No precedent elsewhere in the app (Notas/Kanban/Calendario only ever handle *internal* drag-drop —
    // see docs/agent-docs/seccion-archivos.md §1/§5): DataFormat.File is the format Avalonia's DataTransfer
    // exposes for items dropped in from the system explorer, resolved to local paths and handed to the same
    // VaultService.ImportFile call the "IMPORTAR" button already uses (FilesViewModel.ImportPaths).

    private void FilesGrid_DragEnter(object? sender, DragEventArgs e) => UpdateFilesGridDragOver(e, true);
    private void FilesGrid_DragOver(object? sender, DragEventArgs e) => UpdateFilesGridDragOver(e, true);
    private void FilesGrid_DragLeave(object? sender, DragEventArgs e) => UpdateFilesGridDragOver(e, false);

    private void UpdateFilesGridDragOver(DragEventArgs e, bool isOver)
    {
        // Solo el arrastre entrante del SO: soltar sobre la rejilla una tarjeta que ya está en ella no
        // significa nada. La sesión bloqueada la descarta EffectsFor, por el mismo motivo que en las filas.
        var valid = EffectsFor(e) == DragDropEffects.Copy;
        e.DragEffects = valid ? DragDropEffects.Copy : DragDropEffects.None;
        if (Vm is { } vm) vm.IsGridDropTarget = isOver && valid;
    }

    private void FilesGrid_Drop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.IsGridDropTarget = false;

        if (TryGetDroppedPaths(e) is { } paths) vm.ImportPaths(paths);
    }
}
