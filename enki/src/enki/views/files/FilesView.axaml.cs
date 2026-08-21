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

public partial class FilesView : UserControl
{
    private static readonly DataFormat<string> FileDragFormat =
        DataFormat.CreateStringApplicationFormat("com.enki.file-path");

    private PointerPressedEventArgs? _dragPressArgs;
    private Point _dragStartPoint;
    private bool _dragging;

    public FilesView()
    {
        InitializeComponent();
    }

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

    // ===================== PDF page rail (lazy thumbnails) =====================

    private void PdfPageThumb_Initialized(object? sender, EventArgs e)
    {
        if (sender is not Control { DataContext: PdfPageThumbnailViewModel page } control) return;
        if (control.FindAncestorOfType<ItemsControl>()?.DataContext is PdfDocumentViewModel document)
            document.EnsurePageThumbnailLoaded(page);
    }

    // ===================== Drop targets: folder rows =====================

    private void FolderRow_DragEnter(object? sender, DragEventArgs e) => UpdateFolderDragOver(sender, e, true);
    private void FolderRow_DragOver(object? sender, DragEventArgs e) => UpdateFolderDragOver(sender, e, true);
    private void FolderRow_DragLeave(object? sender, DragEventArgs e) => UpdateFolderDragOver(sender, e, false);

    private static void UpdateFolderDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Border { DataContext: FileFolderViewModel folder }) return;

        var valid = e.DataTransfer.Contains(FileDragFormat);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        folder.IsDropTarget = isOver && valid;
    }

    private void FolderRow_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: FileFolderViewModel folder }) return;
        folder.IsDropTarget = false;
        if (Vm is not { } vm) return;

        var sourcePath = e.DataTransfer.TryGetValue(FileDragFormat);
        if (sourcePath is null) return;

        var card = FindCard(vm, sourcePath);
        if (card is not null) vm.MoveFileToFolder(card, folder.FullPath);
    }

    // ===================== Drop target: root ("TODOS") row =====================

    private void RootFolderRow_DragEnter(object? sender, DragEventArgs e) => UpdateRootDragOver(e, true);
    private void RootFolderRow_DragOver(object? sender, DragEventArgs e) => UpdateRootDragOver(e, true);
    private void RootFolderRow_DragLeave(object? sender, DragEventArgs e) => UpdateRootDragOver(e, false);

    private void UpdateRootDragOver(DragEventArgs e, bool isOver)
    {
        var valid = e.DataTransfer.Contains(FileDragFormat);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        if (Vm is { } vm) vm.IsRootDropTarget = isOver && valid;
    }

    private void RootFolderRow_Drop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.IsRootDropTarget = false;

        var sourcePath = e.DataTransfer.TryGetValue(FileDragFormat);
        if (sourcePath is null) return;

        var card = FindCard(vm, sourcePath);
        if (card is not null) vm.MoveFileToFolder(card, vm.EffectiveRootPath);
    }

    private static FileCardViewModel? FindCard(FilesViewModel vm, string fullPath)
    {
        foreach (var card in vm.Files)
        {
            if (string.Equals(card.File.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)) return card;
        }
        return null;
    }

    // ===================== Drop target: file grid (import from the OS file explorer) =====================
    // No precedent elsewhere in the app (Notas/Kanban/Calendario only ever handle *internal* drag-drop —
    // see docs/seccion-archivos.md §1/§5): DataFormat.File is the format Avalonia's DataTransfer exposes
    // for items dropped in from the system explorer, resolved to local paths and handed to the same
    // VaultService.ImportFile call the "IMPORTAR" button already uses (FilesViewModel.ImportPaths).

    private void FilesGrid_DragEnter(object? sender, DragEventArgs e) => UpdateFilesGridDragOver(e, true);
    private void FilesGrid_DragOver(object? sender, DragEventArgs e) => UpdateFilesGridDragOver(e, true);
    private void FilesGrid_DragLeave(object? sender, DragEventArgs e) => UpdateFilesGridDragOver(e, false);

    private void UpdateFilesGridDragOver(DragEventArgs e, bool isOver)
    {
        var valid = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = valid ? DragDropEffects.Copy : DragDropEffects.None;
        if (Vm is { } vm) vm.IsGridDropTarget = isOver && valid;
    }

    private void FilesGrid_Drop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.IsGridDropTarget = false;

        var files = e.DataTransfer.TryGetFiles();
        if (files is null || files.Length == 0) return;

        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!);
        vm.ImportPaths(paths);
    }
}
