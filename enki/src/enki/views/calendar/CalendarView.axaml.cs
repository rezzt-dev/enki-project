using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Fase 3 (docs/calendar-feature/12-plan-de-implementacion-por-fases.md) code-behind: drag &amp;
/// drop to reschedule (doc 05 §2) and click-to-create on an empty Semana/Día hour slot (doc 05 §3), the
/// two gestures that genuinely need code-behind rather than a pure XAML binding — a slot's Command needs
/// both the column's date and the row's hour, which live in two different nested DataContexts a single
/// CommandParameter binding can't combine (a Mes day cell only needs its own date, so that one is wired
/// directly in CalendarView.axaml via OpenQuickCreateForDayCommand). Mirrors the exact drag&amp;drop
/// mechanism <c>KanbanView.axaml.cs</c> already uses for cards (same <see cref="DataTransferItem"/>/
/// <see cref="DragDrop.DoDragDropAsync"/> APIs), per doc 05 §2's "reutilización técnica obligatoria".</summary>
public partial class CalendarView : UserControl
{
    private static readonly DataFormat<string> ItemDragFormat =
        DataFormat.CreateStringApplicationFormat("com.enki.calendar-item");

    private PointerPressedEventArgs? _dragPressArgs;
    private Point _dragStartPoint;
    private bool _dragging;

    // ===================== Franja horaria: redimensionar (doc 05 §2b) =====================
    // Separate pointer state machine from the drag-to-move one above — the resize handle sits inside the
    // same timed chip that already listens for a whole-chip move-drag (TimedChip_Initialized), so
    // Chip_PointerMoved below bails out early whenever _resizing is true instead of also starting a
    // DragDrop operation for the same gesture.
    private Panel? _resizeVisual;
    private CalendarTimedItemViewModel? _resizingItem;
    private double _resizeStartPointerY;
    private double _resizeStartHeight;
    private int _resizeLiveDurationMinutes;
    private bool _resizing;

    /// <summary>Only the source reference travels through the drag, same as the wire format itself
    /// (<see cref="ItemDragFormat"/>'s payload is just "type|id") — this is what lets a
    /// <see cref="CalendarUnscheduledItemViewModel"/> row (Fase 4, doc §"Panel de no programadas") share
    /// the exact same drag source plumbing as a positioned <see cref="CalendarItemViewModel"/> chip without
    /// either type needing to know about the other.</summary>
    private (CalendarItemSourceType SourceType, string SourceId)? _dragSource;

    private CalendarViewModel? _subscribedVm;

    public CalendarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private CalendarViewModel? Vm => DataContext as CalendarViewModel;

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

    /// <summary>Foco automático en el título al abrir el popover de creación rápida (doc 06 §1, punto 1)
    /// — mismo patrón que <c>KanbanView</c> con su editor de tarjeta y <c>SessionsView</c> con su diálogo
    /// de sesión nueva: sin esto el foco se quedaba en la página de detrás, así que "creación rápida"
    /// empezaba con un clic obligatorio en el campo y, de paso, las teclas sueltas de la página (T, M,
    /// S, D, A, N) seguían llegando al calendario con el modal delante.</summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CalendarViewModel.QuickCreate)) return;
        if (Vm is not { QuickCreate: not null }) return;

        Dispatcher.UIThread.Post(() =>
        {
            QuickCreateTitleBox.Focus();
            QuickCreateTitleBox.SelectAll();
        });
    }

    /// <summary>Enter crea directamente desde el campo de título: el caso de uso entero del popover es
    /// "escribo un título y confirmo", así que obligar a saltar al botón con el ratón o con Tab sobra.</summary>
    private void QuickCreateTitleBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { QuickCreate: { } draft } vm) return;

        // Escape no se trata aquí a propósito: lo resuelve CalendarView_KeyDown, que es quien sabe si
        // hay una capa intermedia (el selector de fecha) que cerrar antes que el popover entero.
        if (e.Key != Key.Enter || !draft.CanSave) return;
        vm.SaveQuickCreateCommand.Execute(null);
        e.Handled = true;
    }

    // ===================== Fase 5: atajos de teclado (docs/calendar-feature/04-vistas-y-navegacion.md
    // §10) — bubble up to the root UserControl from whatever inner control happens to have focus, so no
    // per-control wiring is needed; only text-entry controls opt out (typing "n" in a title shouldn't
    // jump to a new quick-create). =====================

    /// <summary>Grabs initial keyboard focus so the shortcuts work the moment the page opens, without
    /// requiring the user to click something on it first.</summary>
    private void CalendarView_Loaded(object? sender, RoutedEventArgs e) => Focus();

    private void CalendarView_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        // Con un overlay propio abierto (creación rápida o confirmar-borrado) el teclado es suyo: solo
        // Escape lo cierra. Antes las teclas sueltas de página seguían pasando — pulsar "D" con el
        // popover delante cambiaba la vista del calendario de detrás mientras el usuario creía estar
        // rellenando un formulario — porque el filtro de abajo solo excluye los campos de texto, y los
        // botones/ComboBox del propio popover no lo son.
        if (vm.QuickCreate is { } quickCreate)
        {
            if (e.Key != Key.Escape) return;
            // Escape cierra una capa cada vez: el selector de fecha del propio popover es un Popup con
            // light-dismiss, pero su Escape no se marca como manejado y llegaba hasta aquí, así que
            // desplegar el calendario y arrepentirse tiraba el formulario entero con lo ya escrito.
            if (quickCreate.IsCalendarOpen) quickCreate.IsCalendarOpen = false;
            else vm.CancelQuickCreateCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (vm.IsTodayPopoverOpen)
        {
            if (e.Key == Key.Escape) { vm.CloseTodayPopoverCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (vm.ItemPendingDelete is not null)
        {
            if (e.Key == Key.Escape) { vm.CancelDeleteItemCommand.Execute(null); e.Handled = true; }
            return;
        }

        // Don't hijack the search text fields — every shortcut only applies when the user isn't actively
        // typing.
        if (e.Source is TextBox or NumericUpDown) return;

        switch (e.Key)
        {
            case Key.Left:
                vm.PreviousPeriodCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right:
                vm.NextPeriodCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.T:
                vm.GoToTodayCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.M:
                vm.SetViewCommand.Execute(CalendarViewMode.Month);
                e.Handled = true;
                break;
            case Key.S:
                vm.SetViewCommand.Execute(CalendarViewMode.Week);
                e.Handled = true;
                break;
            case Key.D:
                vm.SetViewCommand.Execute(CalendarViewMode.Day);
                e.Handled = true;
                break;
            case Key.A:
                vm.SetViewCommand.Execute(CalendarViewMode.Agenda);
                e.Handled = true;
                break;
            case Key.N:
                vm.OpenQuickCreateForReferenceDateCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // ===================== Drag source: chips (Mes/franja "todo el día"/Agenda) =====================

    // Same handledEventsToo workaround KanbanView.axaml.cs uses: the ToggleButton's own click-gesture
    // tracking would otherwise swallow PointerPressed/PointerMoved before a plain XAML event attribute
    // ever saw them.
    private void Chip_Initialized(object? sender, EventArgs e)
    {
        if (sender is not Control { DataContext: CalendarItemViewModel } control) return;
        AttachDragHandlers(control);
    }

    private void TimedChip_Initialized(object? sender, EventArgs e)
    {
        if (sender is not Control { DataContext: CalendarTimedItemViewModel } control) return;
        AttachDragHandlers(control);
    }

    /// <summary>Fase 4 (doc §"Panel de no programadas"): a "sin programadas" row is draggable onto the
    /// grid exactly like a positioned chip — same handlers, different source shape.</summary>
    private void UnscheduledItem_Initialized(object? sender, EventArgs e)
    {
        if (sender is not Control { DataContext: CalendarUnscheduledItemViewModel } control) return;
        AttachDragHandlers(control);
    }

    private void AttachDragHandlers(Control control)
    {
        control.AddHandler(InputElement.PointerPressedEvent, Chip_PointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        control.AddHandler(InputElement.PointerMovedEvent, Chip_PointerMoved,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void Chip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragPressArgs = e;
        _dragStartPoint = e.GetPosition(null);
        _dragging = false;
        _dragSource = sender switch
        {
            // A ghost occurrence can't be dragged/rescheduled (docs/calendar-feature/
            // 03-glosario-y-modelo-de-datos.md §4) — no drag source means Chip_PointerMoved never starts
            // one, same effect as omitting AttachDragHandlers for it without needing a third code path.
            Control { DataContext: CalendarItemViewModel { IsGhostOccurrence: true } } => null,
            Control { DataContext: CalendarItemViewModel item } => (item.SourceType, item.SourceId),
            Control { DataContext: CalendarTimedItemViewModel { Item.IsGhostOccurrence: true } } => null,
            Control { DataContext: CalendarTimedItemViewModel timed } => (timed.Item.SourceType, timed.Item.SourceId),
            Control { DataContext: CalendarUnscheduledItemViewModel unscheduled } => (unscheduled.SourceType, unscheduled.SourceId),
            _ => null
        };
    }

    private async void Chip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_resizing) return; // the resize handle's own gesture owns this pointer right now
        if (_dragging || _dragPressArgs is null || _dragSource is not { } source) return;
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
            transfer.Add(DataTransferItem.Create(ItemDragFormat, $"{(int)source.SourceType}|{source.SourceId}"));
            await DragDrop.DoDragDropAsync(pressArgs, transfer, DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
            _dragSource = null;
        }
    }

    private static bool TryParseDrag(DragEventArgs e, out CalendarItemSourceType sourceType, out string sourceId)
    {
        sourceType = default;
        sourceId = string.Empty;

        var raw = e.DataTransfer.TryGetValue(ItemDragFormat);
        if (raw is null) return false;

        var parts = raw.Split('|', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var typeValue)) return false;

        sourceType = (CalendarItemSourceType)typeValue;
        sourceId = parts[1];
        return true;
    }

    // ===================== Drop target: Mes day cell — whole-day reschedule, keeps time =====================

    private void MonthCell_DragEnter(object? sender, DragEventArgs e) => UpdateMonthCellDragOver(sender, e, true);
    private void MonthCell_DragOver(object? sender, DragEventArgs e) => UpdateMonthCellDragOver(sender, e, true);
    private void MonthCell_DragLeave(object? sender, DragEventArgs e) => UpdateMonthCellDragOver(sender, e, false);

    private static void UpdateMonthCellDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Border { DataContext: CalendarMonthDayViewModel } border) return;
        var valid = e.DataTransfer.Contains(ItemDragFormat);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        border.Classes.Set("drag-over", isOver && valid);
    }

    private void MonthCell_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: CalendarMonthDayViewModel day } border || Vm is null) return;
        border.Classes.Set("drag-over", false);
        if (!TryParseDrag(e, out var sourceType, out var sourceId)) return;

        Vm.MoveItem(sourceType, sourceId, day.Date, null);
    }

    // ===================== Drop target: Semana/Día "todo el día" strip — same as a Mes cell =====================

    private void AllDayColumn_DragEnter(object? sender, DragEventArgs e) => UpdateWeekColumnDragOver(sender, e, true);
    private void AllDayColumn_DragOver(object? sender, DragEventArgs e) => UpdateWeekColumnDragOver(sender, e, true);
    private void AllDayColumn_DragLeave(object? sender, DragEventArgs e) => UpdateWeekColumnDragOver(sender, e, false);

    private static void UpdateWeekColumnDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Border { DataContext: CalendarWeekColumnViewModel } border) return;
        var valid = e.DataTransfer.Contains(ItemDragFormat);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        border.Classes.Set("drag-over", isOver && valid);
    }

    private void AllDayColumn_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: CalendarWeekColumnViewModel column } border || Vm is null) return;
        border.Classes.Set("drag-over", false);
        if (!TryParseDrag(e, out var sourceType, out var sourceId)) return;

        Vm.MoveItem(sourceType, sourceId, column.Date, null);
    }

    private void AllDayColumn_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: CalendarWeekColumnViewModel column } || Vm is null) return;
        Vm.OpenQuickCreate(column.Date);
    }

    // ===================== Drop target + click: Semana/Día hour cell =====================
    // The hour cell's own DataContext is only CalendarHourRowViewModel (it's rendered by an inner
    // ItemsControl shared by every day column) — the day it belongs to has to be recovered by walking up
    // to the nearest ancestor whose DataContext is the owning CalendarWeekColumnViewModel.

    private void HourCell_DragEnter(object? sender, DragEventArgs e) => UpdateHourCellDragOver(sender, e, true);
    private void HourCell_DragOver(object? sender, DragEventArgs e) => UpdateHourCellDragOver(sender, e, true);
    private void HourCell_DragLeave(object? sender, DragEventArgs e) => UpdateHourCellDragOver(sender, e, false);

    private static void UpdateHourCellDragOver(object? sender, DragEventArgs e, bool isOver)
    {
        if (sender is not Border { DataContext: CalendarHourRowViewModel } border) return;
        var valid = e.DataTransfer.Contains(ItemDragFormat);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        border.Classes.Set("drag-over", isOver && valid);
    }

    private void HourCell_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: CalendarHourRowViewModel hourRow } border || Vm is null) return;
        border.Classes.Set("drag-over", false);
        if (!TryParseDrag(e, out var sourceType, out var sourceId)) return;

        var column = FindAncestorDataContext<CalendarWeekColumnViewModel>(border);
        if (column is null) return;

        var time = RoundToQuarterHour(hourRow.Hour, e.GetPosition(border).Y / border.Bounds.Height);
        Vm.MoveItem(sourceType, sourceId, column.Date, time);
    }

    private void HourCell_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: CalendarHourRowViewModel hourRow } border || Vm is null) return;
        var column = FindAncestorDataContext<CalendarWeekColumnViewModel>(border);
        if (column is null) return;

        var time = RoundToQuarterHour(hourRow.Hour, e.GetPosition(border).Y / border.Bounds.Height);
        Vm.OpenQuickCreate(column.Date, time);
    }

    /// <summary>Rounds a pointer position within an hour cell to the nearest 15 minutes — doc 05 §2:
    /// "redondeado al intervalo de franja, p. ej. 15 o 30 minutos".</summary>
    private static TimeSpan RoundToQuarterHour(int hour, double fractionOfHour)
    {
        var clamped = Math.Clamp(fractionOfHour, 0, 1);
        var quarter = (int)Math.Round(clamped * 4);
        return quarter >= 4 ? new TimeSpan(hour + 1, 0, 0) : new TimeSpan(hour, quarter * 15, 0);
    }

    // ===================== Franja horaria: asa de redimensionado (doc 05 §2b) =====================
    // Deliberately drives the visual Height of the positioned Panel directly during the drag instead of
    // giving CalendarTimedItemViewModel a mutable/observable Height — that type is intentionally a plain,
    // pure, testable record (see its own doc comment), rebuilt wholesale by CalendarViewModel.Rebuild after
    // ResizeItem persists the real change, so the direct visual override here is only ever a transient
    // preview that the next rebuild's binding overwrites.

    private void ResizeHandle_Initialized(object? sender, EventArgs e)
    {
        if (sender is not Control control) return;
        control.AddHandler(InputElement.PointerPressedEvent, ResizeHandle_PointerPressed);
        control.AddHandler(InputElement.PointerMovedEvent, ResizeHandle_PointerMoved);
        control.AddHandler(InputElement.PointerReleasedEvent, ResizeHandle_PointerReleased);
    }

    private void ResizeHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle || handle.DataContext is not CalendarTimedItemViewModel timed) return;
        if (!timed.Item.CanResize) return;
        if (handle.GetVisualParent() is not Panel visual) return;

        _resizeVisual = visual;
        _resizingItem = timed;
        _resizeStartPointerY = e.GetPosition(null).Y;
        _resizeStartHeight = visual.Bounds.Height;
        _resizeLiveDurationMinutes = timed.EffectiveDurationMinutes;
        _resizing = true;
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void ResizeHandle_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing || _resizeVisual is null || _resizingItem is null) return;

        var dy = e.GetPosition(null).Y - _resizeStartPointerY;
        var rawMinutes = (_resizeStartHeight + dy) / CalendarWeekColumnViewModel.HourRowHeight * 60.0;
        var snapped = Math.Clamp(
            CalendarViewModel.RoundToFiveMinutes(rawMinutes),
            CalendarViewModel.MinimumDurationMinutes,
            CalendarViewModel.MaxDurationMinutesSameDay(_resizingItem.Item));

        _resizeLiveDurationMinutes = snapped;
        _resizeVisual.Height = snapped / 60.0 * CalendarWeekColumnViewModel.HourRowHeight;
        e.Handled = true;
    }

    private void ResizeHandle_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        e.Pointer.Capture(null);

        if (_resizingItem is { } timed && Vm is not null)
            Vm.ResizeItem(timed.Item.SourceType, timed.Item.SourceId, _resizeLiveDurationMinutes);

        _resizeVisual = null;
        _resizingItem = null;
        e.Handled = true;
    }

    private static T? FindAncestorDataContext<T>(Visual visual) where T : class
    {
        foreach (var ancestor in visual.GetVisualAncestors())
            if (ancestor is StyledElement { DataContext: T match }) return match;
        return null;
    }
}
