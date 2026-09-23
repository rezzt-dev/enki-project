using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Code-behind de la vista completa de Notas (ver FullNotesView.axaml): selección de carpeta,
/// renombrado en línea de carpetas y tarjetas, y arrastrar una nota a otra carpeta. Todo esto vivía en
/// NotesView.axaml.cs hasta que la página se partió en dos disposiciones; NotesView se quedó solo con lo
/// común a ambas (atajos y elección de disposición).</summary>
public partial class FullNotesView : UserControl
{
    private static readonly DataFormat<string> NoteDragFormat =
        DataFormat.CreateStringApplicationFormat("com.enki.note-path");

    private PointerPressedEventArgs? _dragPressArgs;
    private Point _dragStartPoint;
    private bool _dragging;

    public FullNotesView()
    {
        InitializeComponent();
    }

    private NotesViewModel? Vm => DataContext as NotesViewModel;

    // ===================== Folder selection =====================

    private void FolderRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: NoteFolderViewModel folder } && Vm is { } vm)
            vm.SelectFolderCommand.Execute(folder);
    }

    private void RootFolderRow_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        Vm?.SelectFolderCommand.Execute(null);

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
        _dragPressArgs = e;
        _dragStartPoint = e.GetPosition(null);
        _dragging = false;
    }

    private async void NoteCard_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging || _dragPressArgs is null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { DataContext: NoteCardViewModel card }) return;

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
            transfer.Add(DataTransferItem.Create(NoteDragFormat, card.Note.FullPath));
            await DragDrop.DoDragDropAsync(pressArgs, transfer, DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
        }
    }

    // ===================== Drop targets: folder rows =====================

    private void FolderRow_DragEnter(object? sender, DragEventArgs e) => UpdateFolderDragOver(sender, e, true);
    private void FolderRow_DragOver(object? sender, DragEventArgs e) => UpdateFolderDragOver(sender, e, true);
    private void FolderRow_DragLeave(object? sender, DragEventArgs e) => UpdateFolderDragOver(sender, e, false);

    private static void UpdateFolderDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Border { DataContext: NoteFolderViewModel folder }) return;

        var valid = e.DataTransfer.Contains(NoteDragFormat);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        folder.IsDropTarget = isOver && valid;
    }

    private void FolderRow_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: NoteFolderViewModel folder }) return;
        folder.IsDropTarget = false;
        if (Vm is not { } vm) return;

        var sourcePath = e.DataTransfer.TryGetValue(NoteDragFormat);
        if (sourcePath is null) return;

        var card = FindCard(vm, sourcePath);
        if (card is not null) vm.MoveNoteToFolder(card, folder.FullPath);
    }

    // ===================== Drop target: root ("TODAS") row =====================

    private void RootFolderRow_DragEnter(object? sender, DragEventArgs e) => UpdateRootDragOver(e, true);
    private void RootFolderRow_DragOver(object? sender, DragEventArgs e) => UpdateRootDragOver(e, true);
    private void RootFolderRow_DragLeave(object? sender, DragEventArgs e) => UpdateRootDragOver(e, false);

    private void UpdateRootDragOver(DragEventArgs e, bool isOver)
    {
        var valid = e.DataTransfer.Contains(NoteDragFormat);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        if (Vm is { } vm) vm.IsRootDropTarget = isOver && valid;
    }

    private void RootFolderRow_Drop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.IsRootDropTarget = false;

        var sourcePath = e.DataTransfer.TryGetValue(NoteDragFormat);
        if (sourcePath is null) return;

        var card = FindCard(vm, sourcePath);
        if (card is not null) vm.MoveNoteToFolder(card, vm.EffectiveRootPath);
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
