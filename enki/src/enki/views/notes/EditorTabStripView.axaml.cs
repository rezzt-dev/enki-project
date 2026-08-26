using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Code-behind de la barra de pestañas compartida (ver EditorTabStripView.axaml): selección,
/// cierre con clic medio y reordenar arrastrando. Todo esto vivía en NotesView.axaml.cs hasta que la vista
/// simplificada necesitó exactamente la misma barra; está aquí, y no allí, para que las dos disposiciones
/// compartan el comportamiento en vez de tener cada una su copia.</summary>
public partial class EditorTabStripView : UserControl
{
    private static readonly DataFormat<string> EditorTabDragFormat =
        DataFormat.CreateStringApplicationFormat("com.enki.editor-tab-path");

    private PointerPressedEventArgs? _tabDragPressArgs;
    private Point _tabDragStartPoint;
    private bool _tabDragging;

    public EditorTabStripView()
    {
        InitializeComponent();
    }

    private NotesViewModel? Vm => DataContext as NotesViewModel;

    private void EditorTab_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: EditorViewModel editor } || Vm is not { } vm) return;

        // Middle-click closes the tab under the cursor without needing to first select it, same
        // convention as browser tab strips.
        if (e.GetCurrentPoint(sender as Control).Properties.IsMiddleButtonPressed)
        {
            vm.CloseEditorTabCommand.Execute(editor);
            return;
        }

        vm.SelectEditorCommand.Execute(editor);

        _tabDragPressArgs = e;
        _tabDragStartPoint = e.GetPosition(null);
        _tabDragging = false;
    }

    // Tabs sit flush against each other, so the drag threshold is smaller than the note-card one
    // (~3px vs ~4px) — a wider one would eat the first few pixels of an intentional reorder drag.
    private async void EditorTab_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tabDragging || _tabDragPressArgs is null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not Border { DataContext: EditorViewModel editor }) return;

        var current = e.GetPosition(null);
        var dx = current.X - _tabDragStartPoint.X;
        var dy = current.Y - _tabDragStartPoint.Y;
        if (dx * dx + dy * dy < 9) return;

        _tabDragging = true;
        var pressArgs = _tabDragPressArgs;
        _tabDragPressArgs = null;

        try
        {
            using var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(EditorTabDragFormat, editor.Note.FullPath));
            await DragDrop.DoDragDropAsync(pressArgs, transfer, DragDropEffects.Move);
        }
        finally
        {
            _tabDragging = false;
        }
    }

    private static void EditorTab_DragEnter(object? sender, DragEventArgs e) => UpdateEditorTabDragOver(sender, e, true);
    private static void EditorTab_DragOver(object? sender, DragEventArgs e) => UpdateEditorTabDragOver(sender, e, true);
    private static void EditorTab_DragLeave(object? sender, DragEventArgs e) => UpdateEditorTabDragOver(sender, e, false);

    private static void UpdateEditorTabDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Border { DataContext: EditorViewModel target }) return;

        var valid = e.DataTransfer.Contains(EditorTabDragFormat);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        target.IsDropTarget = isOver && valid;
    }

    private void EditorTab_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: EditorViewModel target } || Vm is not { } vm) return;
        target.IsDropTarget = false;

        var sourcePath = e.DataTransfer.TryGetValue(EditorTabDragFormat);
        if (sourcePath is null) return;

        var source = vm.OpenEditors.FirstOrDefault(ed =>
            string.Equals(ed.Note.FullPath, sourcePath, StringComparison.OrdinalIgnoreCase));
        if (source is not null) vm.ReorderEditor(source, target);
    }
}
