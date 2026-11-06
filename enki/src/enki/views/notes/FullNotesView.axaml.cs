using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Code-behind de la vista completa de Notas (ver FullNotesView.axaml): selección de carpeta,
/// renombrado en línea de carpetas y tarjetas, y arrastrar notas y carpetas. Todo esto vivía en
/// NotesView.axaml.cs hasta que la página se partió en dos disposiciones; NotesView se quedó solo con lo
/// común a ambas (atajos y elección de disposición).</summary>
public partial class FullNotesView : UserControl
{
    internal static readonly DataFormat<string> NoteDragFormat =
        DataFormat.CreateStringApplicationFormat("com.enki.note-path");

    private PointerPressedEventArgs? _dragPressArgs;
    private Point _dragStartPoint;
    private bool _dragging;
    private readonly ExplorerDragGhost _dragGhost;

    public FullNotesView()
    {
        InitializeComponent();
        _dragGhost = new ExplorerDragGhost(this, DragSurface, NoteDragFormat);
    }

    private NotesViewModel? Vm => DataContext as NotesViewModel;

    // ===================== Folder selection =====================

    private void FolderRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: NoteFolderViewModel { IsRenaming: false } folder } row || Vm is not { } vm)
            return;
        if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed ||
            (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null ||
            (e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
            return;
        vm.SelectFolderCommand.Execute(folder);
        ArmDrag(e);
    }

    private void FolderRow_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is Border { DataContext: NoteFolderViewModel folder })
            StartDrag(e, folder.FullPath);
    }

    private void RootFolderRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control row && e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            Vm?.SelectFolderCommand.Execute(null);
    }

    // ===================== Inline folder rename =====================

    private void FolderNameEdit_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: NoteFolderViewModel folder }) return;

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
        if (sender is TextBox { DataContext: NoteFolderViewModel { IsRenaming: true } folder })
            Vm?.CommitRenameFolderCommand.Execute(folder);
    }

    // Toggling IsVisible doesn't move keyboard focus, so without this the Enter/Escape keys typed
    // right after "Nueva carpeta"/"Renombrar" would still land on whatever button triggered it.
    // Both hooks are needed: a brand-new row's TextBox is already visible the moment it's realized
    // (IsRenaming was set true before the container existed), so no PropertyChanged fires for it —
    // only Initialized sees it. An existing row's TextBox toggles visibility later, after it already
    // exists, so only PropertyChanged sees that.
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

    // ===================== Inline note rename =====================

    private void NoteNameEdit_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: NoteCardViewModel card }) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Vm?.CommitRenameNoteCommand.Execute(card);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            card.CancelRenameCommand.Execute(null);
        }
    }

    private void NoteNameEdit_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: NoteCardViewModel { IsRenaming: true } card })
            Vm?.CommitRenameNoteCommand.Execute(card);
    }

    private void NoteNameEdit_Initialized(object? sender, EventArgs e)
    {
        if (sender is TextBox { IsVisible: true } textBox) FocusRenameBox(textBox);
    }

    private void NoteNameEdit_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty) return;
        if (sender is TextBox { IsVisible: true } textBox) FocusRenameBox(textBox);
    }

    // ===================== Drag source: note cards =====================

    // Button marks PointerPressed/PointerMoved as handled as part of its own click-gesture tracking,
    // so a plain XAML event attribute on the note-card Button never sees them. AddHandler with
    // handledEventsToo:true (wired once, when the card is realized) bypasses that.
    private void NoteCard_Initialized(object? sender, EventArgs e)
    {
        if (sender is not Control control) return;
        control.AddHandler(InputElement.PointerPressedEvent, NoteCard_PointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        control.AddHandler(InputElement.PointerMovedEvent, NoteCard_PointerMoved,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void NoteCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is Control { DataContext: NoteFolderCardViewModel { IsParent: true } }) return;
        ArmDrag(e);
    }

    private void ArmDrag(PointerPressedEventArgs e)
    {
        _dragPressArgs = e;
        _dragStartPoint = e.GetPosition(null);
        _dragging = false;
    }

    private void NoteCard_PointerMoved(object? sender, PointerEventArgs e)
    {
        var path = sender switch
        {
            Control { DataContext: NoteCardViewModel card } => card.Note.FullPath,
            Control { DataContext: NoteFolderCardViewModel { IsParent: false } folder } => folder.FullPath,
            _ => null
        };
        if (path is not null) StartDrag(e, path);
    }

    private async void StartDrag(PointerEventArgs e, string path)
    {
        if (_dragging || _dragPressArgs is null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;

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
            transfer.Add(DataTransferItem.Create(NoteDragFormat, path));
            await DragDrop.DoDragDropAsync(pressArgs, transfer, DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
            _dragGhost.Hide();
        }
    }

    // ===================== Drop targets: folder rows =====================

    private void FolderRow_DragEnter(object? sender, DragEventArgs e) => UpdateFolderDragOver(sender, e, true);
    private void FolderRow_DragOver(object? sender, DragEventArgs e) => UpdateFolderDragOver(sender, e, true);
    private void FolderRow_DragLeave(object? sender, DragEventArgs e) => UpdateFolderDragOver(sender, e, false);

    private void UpdateFolderDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Border { DataContext: NoteFolderViewModel folder }) return;

        var valid = CanDropInto(e, folder.FullPath);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        folder.IsDropTarget = isOver && valid;
    }

    private void FolderRow_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: NoteFolderViewModel folder }) return;
        folder.IsDropTarget = false;
        if (CanDropInto(e, folder.FullPath)) DropInto(e, folder.FullPath);
    }

    private void FolderCard_DragEnter(object? sender, DragEventArgs e) => UpdateFolderCardDragOver(sender, e, true);
    private void FolderCard_DragOver(object? sender, DragEventArgs e) => UpdateFolderCardDragOver(sender, e, true);
    private void FolderCard_DragLeave(object? sender, DragEventArgs e) => UpdateFolderCardDragOver(sender, e, false);

    private void UpdateFolderCardDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Button { DataContext: NoteFolderCardViewModel card }) return;
        var valid = !card.IsParent && CanDropInto(e, card.FullPath);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        card.IsDropTarget = isOver && valid;
    }

    private void FolderCard_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Button { DataContext: NoteFolderCardViewModel card }) return;
        card.IsDropTarget = false;
        if (!card.IsParent && CanDropInto(e, card.FullPath)) DropInto(e, card.FullPath);
    }

    // ===================== Drop target: root ("TODAS") row =====================

    private void RootFolderRow_DragEnter(object? sender, DragEventArgs e) => UpdateRootDragOver(e, true);
    private void RootFolderRow_DragOver(object? sender, DragEventArgs e) => UpdateRootDragOver(e, true);
    private void RootFolderRow_DragLeave(object? sender, DragEventArgs e) => UpdateRootDragOver(e, false);

    private void UpdateRootDragOver(DragEventArgs e, bool isOver)
    {
        var valid = Vm is { } vm && CanDropInto(e, vm.EffectiveRootPath);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        if (Vm is { } current) current.IsRootDropTarget = isOver && valid;
    }

    private bool CanDropInto(DragEventArgs e, string destination)
    {
        if (Vm is not { IsSelectedSessionLocked: false }) return false;
        var source = e.DataTransfer.TryGetValue(NoteDragFormat);
        return source is not null && (!System.IO.Directory.Exists(source) ||
            !enki.Services.VaultService.IsWithinFolder(destination, source));
    }

    private void RootFolderRow_Drop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.IsRootDropTarget = false;
        if (CanDropInto(e, vm.EffectiveRootPath)) DropInto(e, vm.EffectiveRootPath);
    }

    private void DropInto(DragEventArgs e, string destination)
    {
        if (Vm is not { IsSelectedSessionLocked: false } vm) return;
        var sourcePath = e.DataTransfer.TryGetValue(NoteDragFormat);
        if (sourcePath is null) return;

        if (System.IO.Directory.Exists(sourcePath))
        {
            vm.MoveFolderTo(sourcePath, destination);
            return;
        }

        var card = FindCard(vm, sourcePath);
        if (card is not null) vm.MoveNoteToFolder(card, destination);
    }

    private static NoteCardViewModel? FindCard(NotesViewModel vm, string fullPath)
    {
        foreach (var card in vm.Notes)
        {
            if (string.Equals(card.Note.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)) return card;
        }
        return null;
    }
}
