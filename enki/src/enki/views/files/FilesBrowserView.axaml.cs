using System;
using System.ComponentModel;
using System.IO;
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

/// <summary>Mecánica de vista del modo explorador de Archivos: selección y renombrado en línea de filas y
/// tarjetas, atajos de teclado de la rejilla, arrastrar una tarjeta a otra carpeta e importar archivos
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

    public FilesBrowserView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Bubble);
    }

    private FilesViewModel? Vm => DataContext as FilesViewModel;

    /// <summary>Retroceso sube un nivel mientras se navega la rejilla, como en un explorador de archivos. No se
    /// atiende mientras se escribe en un cuadro de texto (búsqueda, renombrar) ni con la previsualización
    /// abierta, que tiene sus propios atajos.</summary>
    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Source is TextBox || Vm is not { ActivePreview: null } vm) return;
        if (e.Key != Key.Back || e.KeyModifiers != KeyModifiers.None) return;

        vm.NavigateUpCommand.Execute(null);
        e.Handled = true;
    }

    // ===================== Folder selection =====================

    private void FolderRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: FileFolderViewModel folder } row || Vm is not { } vm) return;
        if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return; // el clic derecho abre el menú contextual

        // Los botones de la fila (flecha, renombrar, borrar) hacen lo suyo; si además navegaran a la carpeta,
        // pedir borrarla metería primero al usuario dentro de ella.
        if (folder.IsRenaming || (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null) return;

        vm.SelectFolderCommand.Execute(folder);
    }

    private void RootFolderRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control row && e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            Vm?.SelectFolderCommand.Execute(null);
    }

    // ===================== Inline rename =====================

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

    private void FolderCardNameEdit_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileFolderCardViewModel card }) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Vm?.CommitRenameFolderCardCommand.Execute(card);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            card.CancelRenameCommand.Execute(null);
        }
    }

    private void FolderCardNameEdit_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: FileFolderCardViewModel { IsRenaming: true } card })
            Vm?.CommitRenameFolderCardCommand.Execute(card);
    }

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

    private void RenameBox_Initialized(object? sender, EventArgs e)
    {
        if (sender is TextBox { IsVisible: true } textBox) FocusRenameBox(textBox);
    }

    /// <summary>El cuadro de renombrar ya está en el árbol visual cuando <c>IsRenaming</c> pasa a true (solo
    /// cambia su visibilidad), así que Initialized no vuelve a dispararse: hay que darle el foco también aquí.</summary>
    private void RenameBox_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty) return;
        if (sender is TextBox { IsVisible: true } textBox) FocusRenameBox(textBox);
    }

    /// <summary>Da el foco al cuadro de renombrar y selecciona lo que se quiere cambiar. En un archivo, solo el
    /// nombre y no la extensión —casi nunca se renombra para cambiarla, y seleccionarla entera obligaba a
    /// reescribirla—, igual que hacen los exploradores de archivos. Compartido con el modo árbol.</summary>
    internal static void FocusRenameBox(TextBox textBox)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!textBox.IsVisible) return;
            textBox.Focus();

            var text = textBox.Text ?? string.Empty;
            var isFile = textBox.DataContext is FileCardViewModel or FileTreeItemViewModel { IsFile: true };
            var selectionLength = isFile ? Path.GetFileNameWithoutExtension(text).Length : text.Length;
            if (selectionLength == 0) selectionLength = text.Length; // ".gitignore": todo el nombre es la "extensión"

            textBox.SelectionStart = 0;
            textBox.SelectionEnd = selectionLength;
        }, DispatcherPriority.Input);
    }

    // ===================== Keyboard on cards =====================

    private void FileCard_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox || sender is not Control { DataContext: FileCardViewModel card } || Vm is not { } vm) return;

        switch (e.Key)
        {
            case Key.F2:
                card.BeginRenameCommand.Execute(null);
                break;
            case Key.Delete:
                vm.RequestDeleteFileCommand.Execute(card);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void FolderCard_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox || sender is not Control { DataContext: FileFolderCardViewModel { IsParent: false } card }
            || Vm is not { } vm)
            return;

        switch (e.Key)
        {
            case Key.F2:
                card.BeginRenameCommand.Execute(null);
                break;
            case Key.Delete:
                vm.RequestDeleteFolderCommand.Execute(card);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>La tarjeta ".." no es una carpeta sobre la que actuar: sin menú contextual.</summary>
    private void FolderCardMenu_Opening(object? sender, CancelEventArgs e)
    {
        if (sender is ContextMenu { DataContext: FileFolderCardViewModel { IsParent: true } }) e.Cancel = true;
    }

    // ===================== Drag source: file cards (move into a folder) =====================

    private void FileCard_Initialized(object? sender, EventArgs e)
    {
        if (sender is not Control control) return;
        if (control.DataContext is FileCardViewModel card) card.EnsureThumbnailLoaded();

        // La tarjeta se recicla al cambiar de carpeta: la miniatura se pide también para el viewmodel nuevo.
        control.DataContextChanged += (_, _) => (control.DataContext as FileCardViewModel)?.EnsureThumbnailLoaded();

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
        if (sender is not Control { DataContext: FileCardViewModel { IsRenaming: false } card }) return;

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
    // importar ahí dentro lo que venga del explorador del sistema.

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
        DropInto(e, folder.FullPath);
    }

    // ===================== Drop targets: folder cards in the grid =====================
    // Mismo reparto que las filas: soltar sobre una tarjeta de carpeta mueve o importa ahí dentro, y sobre la
    // tarjeta ".", a la carpeta padre. Se marca el evento como manejado para que la rejilla de detrás —que
    // importa en la carpeta que se está viendo— no lo recoja también.

    private void FolderCard_DragEnter(object? sender, DragEventArgs e) => UpdateFolderCardDragOver(sender, e, true);
    private void FolderCard_DragOver(object? sender, DragEventArgs e) => UpdateFolderCardDragOver(sender, e, true);
    private void FolderCard_DragLeave(object? sender, DragEventArgs e) => UpdateFolderCardDragOver(sender, e, false);

    private void UpdateFolderCardDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Control { DataContext: FileFolderCardViewModel card }) return;

        var effects = EffectsFor(e);
        card.IsDropTarget = isOver && effects != DragDropEffects.None;
        if (effects == DragDropEffects.None) return;

        e.DragEffects = effects;
        if (Vm is { } vm) vm.IsGridDropTarget = false;
        e.Handled = true;
    }

    private void FolderCard_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Control { DataContext: FileFolderCardViewModel card }) return;
        card.IsDropTarget = false;
        if (EffectsFor(e) == DragDropEffects.None) return;

        DropInto(e, card.FullPath);
        e.Handled = true;
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
        DropInto(e, vm.EffectiveRootPath);
    }

    /// <summary>Lo que significa soltar en <paramref name="folderPath"/>: mover ahí una tarjeta de la sección, o
    /// importar ahí lo que venga del explorador del sistema.</summary>
    private void DropInto(DragEventArgs e, string folderPath)
    {
        if (Vm is not { IsSelectedSessionLocked: false } vm) return;

        if (e.DataTransfer.TryGetValue(FileDragFormat) is { } sourcePath)
        {
            if (vm.FindTreeCardByPath(sourcePath) is { } card) vm.MoveFileToFolder(card, folderPath);
            return;
        }

        if (TryGetDroppedPaths(e) is { } paths) vm.ImportPaths(paths, folderPath);
    }

    /// <summary>Qué significa el arrastre que pasa por encima de una fila o tarjeta de carpeta: mover una
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
    internal static string[]? TryGetDroppedPaths(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null || files.Length == 0) return null;

        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToArray();
        return paths.Length > 0 ? paths : null;
    }

    // ===================== Drop target: file grid (import from the OS file explorer) =====================
    // DataFormat.File is the format Avalonia's DataTransfer exposes for items dropped in from the system
    // explorer, resolved to local paths and handed to the same VaultService.ImportFile call the "IMPORTAR"
    // button already uses (FilesViewModel.ImportPaths).

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
