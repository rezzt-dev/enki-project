using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using enki.ViewModels;

namespace enki.Views;

public partial class KanbanView : UserControl
{
    private static readonly DataFormat<string> CardDragFormat =
        DataFormat.CreateStringApplicationFormat("com.enki.kanban-card-id");

    private PointerPressedEventArgs? _dragPressArgs;
    private Point _dragStartPoint;
    private bool _dragging;

    private KanbanViewModel? _subscribedVm;

    public KanbanView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private KanbanViewModel? Vm => DataContext as KanbanViewModel;

    /// <summary>Grabs initial keyboard focus so the shortcuts below work the moment the page opens,
    /// without requiring the user to click something on it first — same pattern as CalendarView_Loaded.</summary>
    private void KanbanView_Loaded(object? sender, RoutedEventArgs e) => Focus();

    /// <summary>Atajos de teclado del tablero:
    /// N nueva tarjeta, C nueva columna (Fase 7 de docs/analisis-proyecto/roadmap.md), Delete borrar la
    /// tarjeta enfocada, Esc cerrar el diálogo abierto, Ctrl+F saltar al buscador. Enter ya abre el
    /// detalle sin código adicional: cada tarjeta es un Button enfocable, así que Avalonia dispara su
    /// Command con Enter/Espacio de forma nativa.</summary>
    private void KanbanView_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            KanbanSearchBox.Focus();
            KanbanSearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // No secuestrar los campos de texto (buscador, título de tarjeta/columna, etiquetas) — Escape
        // sigue cerrando el diálogo abierto, pero el resto de atajos de una sola tecla solo aplican
        // cuando el usuario no está escribiendo.
        if (e.Source is TextBox) { if (e.Key != Key.Escape) return; }

        switch (e.Key)
        {
            case Key.N when vm.DisplayMode == KanbanDisplayMode.Board && vm.CardEditor is null && vm.CardDetail is null:
                if (vm.Columns.Count > 0) vm.OpenCreateCardCommand.Execute(vm.Columns[0]);
                e.Handled = true;
                break;

            case Key.C when vm.DisplayMode == KanbanDisplayMode.Board && vm.CardEditor is null && vm.CardDetail is null:
                vm.AddColumnCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Delete:
                if (e.Source is Control { DataContext: KanbanCardViewModel card })
                {
                    vm.RequestDeleteCardCommand.Execute(card);
                    e.Handled = true;
                }
                break;

            case Key.Escape:
                if (vm.CardPendingDelete is not null) { vm.CancelDeleteCardCommand.Execute(null); e.Handled = true; }
                else if (vm.CardEditor is not null) { vm.CancelCardEditorCommand.Execute(null); e.Handled = true; }
                else if (vm.CardDetail is not null) { vm.CloseCardDetailCommand.Execute(null); e.Handled = true; }
                break;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _subscribedVm = Vm;
        if (_subscribedVm is null) return;
        _subscribedVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void Unsubscribe()
    {
        if (_subscribedVm is null) return;
        _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
        _subscribedVm = null;
    }

    // Focuses the title field the moment the create/edit popup opens, same "ready to type
    // immediately" courtesy as every other inline editor in this view.
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(KanbanViewModel.CardEditor)) return;
        if (Vm is not { CardEditor: not null }) return;

        Dispatcher.UIThread.Post(() =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        });
    }

    // ===================== Drag source: cards =====================

    // Button/Border click-gesture tracking marks PointerPressed/PointerMoved as handled, so a plain
    // XAML event attribute on the card never sees them — same issue NotesView works around for note
    // cards and editor tabs, and the same fix (AddHandler with handledEventsToo:true) applies here.
    private void CardBody_Initialized(object? sender, EventArgs e)
    {
        if (sender is not Control control) return;
        control.AddHandler(InputElement.PointerPressedEvent, CardBody_PointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        control.AddHandler(InputElement.PointerMovedEvent, CardBody_PointerMoved,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void CardBody_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragPressArgs = e;
        _dragStartPoint = e.GetPosition(null);
        _dragging = false;
    }

    private async void CardBody_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging || _dragPressArgs is null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { DataContext: KanbanCardViewModel card }) return;

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
            transfer.Add(DataTransferItem.Create(CardDragFormat, card.Id));
            await DragDrop.DoDragDropAsync(pressArgs, transfer, DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
        }
    }

    // ===================== Drop target: a card (insert before it) =====================

    private void CardBody_DragEnter(object? sender, DragEventArgs e) => UpdateCardDragOver(sender, e, true);
    private void CardBody_DragOver(object? sender, DragEventArgs e) => UpdateCardDragOver(sender, e, true);
    private void CardBody_DragLeave(object? sender, DragEventArgs e) => UpdateCardDragOver(sender, e, false);

    private static void UpdateCardDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Control { DataContext: KanbanCardViewModel target }) return;

        var valid = e.DataTransfer.Contains(CardDragFormat);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        target.IsDropTarget = isOver && valid;

        // Stop the drag-over from also bubbling up to the column body below, which would light up
        // both the targeted card and its whole column at once.
        e.Handled = true;
    }

    private void CardBody_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Control { DataContext: KanbanCardViewModel target } || Vm is not { } vm) return;
        target.IsDropTarget = false;
        e.Handled = true;

        var sourceId = e.DataTransfer.TryGetValue(CardDragFormat);
        if (sourceId is null) return;

        var source = FindCard(vm, sourceId);
        if (source is null || ReferenceEquals(source, target)) return;

        var targetColumn = FindColumn(vm, target);
        if (targetColumn is not null) vm.MoveCard(source, targetColumn, target);
    }

    // ===================== Drop target: column body (append at the end) =====================

    private void ColumnBody_DragEnter(object? sender, DragEventArgs e) => UpdateColumnDragOver(sender, e, true);
    private void ColumnBody_DragOver(object? sender, DragEventArgs e) => UpdateColumnDragOver(sender, e, true);
    private void ColumnBody_DragLeave(object? sender, DragEventArgs e) => UpdateColumnDragOver(sender, e, false);

    private static void UpdateColumnDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Control { DataContext: KanbanColumnViewModel column }) return;

        var valid = e.DataTransfer.Contains(CardDragFormat);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        column.IsDropTarget = isOver && valid;
    }

    private void ColumnBody_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Control { DataContext: KanbanColumnViewModel column } || Vm is not { } vm) return;
        column.IsDropTarget = false;
        if (e.Handled) return; // a card inside this column already handled the drop

        var sourceId = e.DataTransfer.TryGetValue(CardDragFormat);
        if (sourceId is null) return;

        var source = FindCard(vm, sourceId);
        if (source is not null) vm.MoveCard(source, column, null);
    }

    private static KanbanCardViewModel? FindCard(KanbanViewModel vm, string id)
    {
        foreach (var column in vm.Columns)
            foreach (var card in column.Cards)
                if (string.Equals(card.Id, id, StringComparison.Ordinal))
                    return card;
        return null;
    }

    private static KanbanColumnViewModel? FindColumn(KanbanViewModel vm, KanbanCardViewModel card)
    {
        foreach (var column in vm.Columns)
            if (column.Cards.Contains(card)) return column;
        return null;
    }

    // ===================== Inline editing: column title =====================

    private void ColumnTitleEdit_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: KanbanColumnViewModel column }) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Vm?.CommitRenameColumnCommand.Execute(column);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            column.CancelRenameCommand.Execute(null);
        }
    }

    private void ColumnTitleEdit_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: KanbanColumnViewModel { IsRenaming: true } column })
            Vm?.CommitRenameColumnCommand.Execute(column);
    }

    private void ColumnTitleEdit_Initialized(object? sender, EventArgs e)
    {
        if (sender is TextBox { IsVisible: true } textBox) FocusAndSelect(textBox);
    }

    private void ColumnTitleEdit_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty) return;
        if (sender is TextBox { IsVisible: true } textBox) FocusAndSelect(textBox);
    }

    // ===================== Card editor popup: new subtask field =====================

    private void NewSubtaskBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: KanbanCardEditorViewModel editor }) return;
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        if (editor.AddSubtaskCommand.CanExecute(null)) editor.AddSubtaskCommand.Execute(null);
    }

    // ===================== "Gestión de tareas" task editor popup: new subtask field =====================

    private void PolarNewSubtaskBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: PolarTaskEditorViewModel editor }) return;
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        if (editor.AddSubtaskCommand.CanExecute(null)) editor.AddSubtaskCommand.Execute(null);
    }

    private static void FocusAndSelect(TextBox textBox)
    {
        Dispatcher.UIThread.Post(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        });
    }
}
