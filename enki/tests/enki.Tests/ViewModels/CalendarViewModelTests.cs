using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Material.Icons;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the Fase 2 (docs/calendar-feature/12-plan-de-implementacion-por-fases.md) pure,
/// service-free calculators that back the Semana/Día/Agenda views: <see cref="WeekGridCalculator"/>,
/// <see cref="CalendarTimedItemViewModel"/>'s hour-grid pixel math, <see cref="CalendarWeekColumnViewModel"/>'s
/// all-day/timed split and "now" line, and <see cref="CalendarAgendaGroupViewModel"/>'s day grouping.
/// <see cref="CalendarViewModel"/> itself isn't covered here — unlike <see cref="ReminderService"/>,
/// <c>KanbanService</c> has no storage-directory override to point at a throwaway temp folder, so it
/// can't be constructed in a test without touching the real developer's <c>%AppData%/Enki</c> — same
/// reasoning already documented on <see cref="Services.CalendarAggregationService"/>'s constructor.</summary>
public class CalendarViewModelTests
{
    private static CalendarItemViewModel MakeItem(DateTime momentUtc, bool isAllDay, bool isCompleted = false, string title = "Item") => new()
    {
        SourceId = Guid.NewGuid().ToString(),
        SourceType = CalendarItemSourceType.Kanban,
        Title = title,
        MomentUtc = momentUtc,
        IsAllDay = isAllDay,
        IsCompleted = isCompleted,
        LayerColorHex = CalendarItemViewModel.KanbanLayerColorHex,
        LayerKey = "kanban",
        IconKind = MaterialIconKind.ViewColumnOutline
    };

    [Theory]
    [InlineData(2026, 3, 9, 2026, 3, 9)]   // A Monday maps to itself.
    [InlineData(2026, 3, 11, 2026, 3, 9)]  // A Wednesday maps back to that week's Monday.
    [InlineData(2026, 3, 15, 2026, 3, 9)]  // A Sunday maps back to the same week's Monday, not the next one.
    public void StartOfWeek_AlwaysReturnsThatWeeksMonday(int y, int m, int d, int expectedY, int expectedM, int expectedD)
    {
        var result = WeekGridCalculator.StartOfWeek(new DateTime(y, m, d));

        Assert.Equal(new DateTime(expectedY, expectedM, expectedD), result);
    }

    [Fact]
    public void TimedItem_TopOffset_IsProportionalToMinutesFromMidnight()
    {
        var localMoment = new DateTime(2026, 3, 9, 15, 30, 0);
        var item = MakeItem(localMoment.ToUniversalTime(), isAllDay: false);

        var timed = new CalendarTimedItemViewModel(item, hourRowHeight: 48, minimumVisualHeightMinutes: 30);

        // 15:30 = 15.5 hours from midnight * 48px/hour.
        Assert.Equal(15.5 * 48, timed.TopOffset, precision: 3);
        Assert.Equal(0.5 * 48, timed.Height, precision: 3);
    }

    [Fact]
    public void TimedItem_TopOffset_IsZero_AtMidnight()
    {
        var item = MakeItem(DateTime.Today.ToUniversalTime(), isAllDay: false);

        var timed = new CalendarTimedItemViewModel(item, hourRowHeight: 48, minimumVisualHeightMinutes: 30);

        Assert.Equal(0, timed.TopOffset);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]     // Rounds down to the nearest 5-minute grain.
    [InlineData(3, 5)]     // Rounds up once past the midpoint.
    [InlineData(47, 45)]
    [InlineData(48, 50)]
    public void RoundToFiveMinutes_SnapsToNearestFiveMinuteGrain(double raw, int expected)
    {
        Assert.Equal(expected, CalendarViewModel.RoundToFiveMinutes(raw));
    }

    [Fact]
    public void MaxDurationMinutesSameDay_IsMinutesUntilMidnight()
    {
        var item = MakeItem(new DateTime(2026, 3, 9, 23, 15, 0).ToUniversalTime(), isAllDay: false);

        // 23:15 -> 45 minutes left until local midnight.
        Assert.Equal(45, CalendarViewModel.MaxDurationMinutesSameDay(item));
    }

    [Fact]
    public void MaxDurationMinutesSameDay_NeverGoesBelowTheMinimumDuration()
    {
        // A card due at 23:58 only leaves 2 real minutes until midnight — the cap still floors at the
        // minimum resizable duration rather than returning something a resize couldn't actually use.
        var item = MakeItem(new DateTime(2026, 3, 9, 23, 58, 0).ToUniversalTime(), isAllDay: false);

        Assert.Equal(CalendarViewModel.MinimumDurationMinutes, CalendarViewModel.MaxDurationMinutesSameDay(item));
    }

    [Fact]
    public void WeekColumn_SplitsAllDayFromTimedItems()
    {
        var day = new DateTime(2026, 3, 9);
        var allDay = MakeItem(day.ToUniversalTime(), isAllDay: true, title: "Todo el día");
        var timed = MakeItem(day.AddHours(10).ToUniversalTime(), isAllDay: false, title: "Con hora");

        var column = new CalendarWeekColumnViewModel(day, [allDay, timed], nowLocal: day.AddDays(-1));

        Assert.Single(column.AllDayItems);
        Assert.Equal("Todo el día", column.AllDayItems[0].Title);
        Assert.Single(column.TimedItems);
        Assert.Equal("Con hora", column.TimedItems[0].Item.Title);
    }

    [Fact]
    public void WeekColumn_IsToday_OnlyWhenDateMatchesNow()
    {
        var day = new DateTime(2026, 3, 9);

        var todayColumn = new CalendarWeekColumnViewModel(day, [], nowLocal: day.AddHours(8));
        var otherColumn = new CalendarWeekColumnViewModel(day, [], nowLocal: day.AddDays(1));

        Assert.True(todayColumn.IsToday);
        Assert.NotNull(todayColumn.NowOffsetY);
        Assert.False(otherColumn.IsToday);
        Assert.Null(otherColumn.NowOffsetY);
    }

    [Fact]
    public void WeekColumn_RefreshNow_UpdatesOffsetWithoutNeedingReconstruction()
    {
        var day = new DateTime(2026, 3, 9);
        var column = new CalendarWeekColumnViewModel(day, [], nowLocal: day.AddHours(8));
        var firstOffset = column.NowOffsetY;

        column.RefreshNow(day.AddHours(9));

        Assert.NotEqual(firstOffset, column.NowOffsetY);
        Assert.Equal(9 * CalendarWeekColumnViewModel.HourRowHeight, column.NowOffsetY);
    }

    [Fact]
    public void AgendaGroups_SkipDaysWithoutAnyItem()
    {
        var day1 = new DateTime(2026, 3, 9);
        var day3 = new DateTime(2026, 3, 11);
        var items = new[] { MakeItem(day1.ToUniversalTime(), isAllDay: true), MakeItem(day3.ToUniversalTime(), isAllDay: true) };

        var groups = CalendarAgendaGroupViewModel.Build(items);

        Assert.Equal(2, groups.Count);
        Assert.Equal(day1, groups[0].Date);
        Assert.Equal(day3, groups[1].Date);
    }

    [Fact]
    public void AgendaGroups_OrderWithinADay_PutsAllDayItemsBeforeTimedOnes()
    {
        var day = new DateTime(2026, 3, 9);
        var timed = MakeItem(day.AddHours(9).ToUniversalTime(), isAllDay: false, title: "Con hora");
        var allDay = MakeItem(day.ToUniversalTime(), isAllDay: true, title: "Todo el día");
        // Deliberately unsorted input — Build() must not rely on the caller having pre-sorted the list.
        var items = new[] { timed, allDay };

        var groups = CalendarAgendaGroupViewModel.Build(items);

        Assert.Equal("Todo el día", groups.Single().Items[0].Title);
        Assert.Equal("Con hora", groups.Single().Items[1].Title);
    }

    // ===================== Fase 5: CalendarWeekStartsOnMonday/CalendarShowWeekends/CalendarTimeFormat24h
    // (docs/calendar-feature/09-configuracion-y-ajustes.md §1) =====================

    [Theory]
    [InlineData(2026, 3, 9, 2026, 3, 8)]   // A Monday's Sunday-first week starts the day before.
    [InlineData(2026, 3, 11, 2026, 3, 8)]  // A Wednesday maps back to that week's Sunday.
    [InlineData(2026, 3, 15, 2026, 3, 15)] // A Sunday maps to itself.
    public void StartOfWeek_SundayFirst_ReturnsThatWeeksSunday(int y, int m, int d, int expectedY, int expectedM, int expectedD)
    {
        var result = WeekGridCalculator.StartOfWeek(new DateTime(y, m, d), startOnMonday: false);

        Assert.Equal(new DateTime(expectedY, expectedM, expectedD), result);
    }

    [Fact]
    public void MonthGridCalculator_SundayFirst_FirstCellIsASunday()
    {
        // March 2026: the 1st is a Sunday, so the Monday-first grid pads with the 6 days before it while
        // the Sunday-first grid needs no padding at all — same month, different first cell.
        var dates = MonthGridCalculator.BuildDates(new DateTime(2026, 3, 1), startOnMonday: false);

        Assert.Equal(42, dates.Length);
        Assert.Equal(DayOfWeek.Sunday, dates[0].DayOfWeek);
        Assert.Equal(new DateTime(2026, 3, 1), dates[0]);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(9, "09:00")]
    [InlineData(23, "23:00")]
    public void HourRow_Label_Uses24hFormat_WhenEnabled(int hour, string expected)
    {
        var row = new CalendarHourRowViewModel(hour, isWorkingHour: false, use24h: true);

        Assert.Equal(expected, row.Label);
    }

    [Theory]
    [InlineData(0, "12 AM")]
    [InlineData(9, "9 AM")]
    [InlineData(12, "12 PM")]
    [InlineData(13, "1 PM")]
    [InlineData(23, "11 PM")]
    public void HourRow_Label_UsesAmPmFormat_WhenDisabled(int hour, string expected)
    {
        var row = new CalendarHourRowViewModel(hour, isWorkingHour: false, use24h: false);

        Assert.Equal(expected, row.Label);
    }

    [Fact]
    public void MonthDay_LastColumnAndLastRow_RespectCustomColumnCount()
    {
        // 30 cells (6 weeks × 5 weekday columns, CalendarShowWeekends off) — same "always 6 rows" shape
        // as the default 42-cell/7-column grid, just narrower.
        const int columns = 5;
        var firstCell = new CalendarMonthDayViewModel(new DateTime(2026, 3, 2), true, false, false, [], index: 0, columns);
        var lastColumnOfFirstRow = new CalendarMonthDayViewModel(new DateTime(2026, 3, 6), true, false, false, [], index: 4, columns);
        var firstCellOfLastRow = new CalendarMonthDayViewModel(new DateTime(2026, 3, 30), true, false, false, [], index: 25, columns);

        Assert.False(firstCell.IsLastColumn);
        Assert.False(firstCell.IsLastRow);
        Assert.True(lastColumnOfFirstRow.IsLastColumn);
        Assert.False(lastColumnOfFirstRow.IsLastRow);
        Assert.True(firstCellOfLastRow.IsLastRow);
    }

    // ===================== Fase 3: CalendarViewModel con servicios reales =====================

    private static (CalendarViewModel Vm, KanbanService Kanban, PolarTaskService Tasks, ReminderService Reminders) CreateViewModel(out string storageDirectory)
    {
        storageDirectory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
        var settingsService = new SettingsService(storageDirectory);
        var kanbanService = new KanbanService(storageDirectory);
        var authService = new PolarAuthService(settingsService, baseUrl: "https://test.example", anonKey: "test-key");
        var taskService = new PolarTaskService(storageDirectory);
        var reminderService = new ReminderService(storageDirectory);
        var notificationService = new NotificationService(settingsService, storageDirectory);
        var layerColorService = new CalendarLayerColorService(kanbanService, taskService, authService, settingsService);
        var aggregationService = new CalendarAggregationService(kanbanService, taskService, reminderService, authService, layerColorService);
        var vm = new CalendarViewModel(
            aggregationService, layerColorService, kanbanService, taskService, reminderService, authService,
            settingsService, notificationService, _ => { });
        return (vm, kanbanService, taskService, reminderService);
    }

    // ===================== Capas: colores por origen (doc 07 §1b) =====================

    [Fact]
    public void Layers_HangsOneColorRowPerKanbanColumnUnderTheKanbanRow()
    {
        var dir = string.Empty;
        try
        {
            var (vm, kanbanService, _, _) = CreateViewModel(out dir);

            var kanbanRow = vm.Layers[0];
            var childRows = vm.Layers.Where(l => l.IsChildRow).ToList();

            Assert.False(kanbanRow.IsChildRow);
            Assert.True(kanbanRow.ShowToggle);
            // La fila madre solo marca el grupo: el color real vive en cada columna.
            Assert.False(kanbanRow.CanEditColor);
            Assert.Equal(kanbanService.Board.Columns.Select(c => c.Title), childRows.Select(l => l.Label));
            Assert.All(childRows, row =>
            {
                Assert.True(row.CanEditColor);
                Assert.False(row.ShowToggle);
                Assert.Equal(CalendarAggregationService.KanbanLayerKey, row.Key);
            });
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Fact]
    public void Layers_KanbanChildRowsAreNotFiltersOfTheirOwn()
    {
        var dir = string.Empty;
        try
        {
            var (vm, kanbanService, _, _) = CreateViewModel(out dir);
            var column = kanbanService.Board.Columns[0];
            column.Cards.Clear();
            var localMoment = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Local);
            column.Cards.Add(new KanbanCard { Id = "card-1", Title = "Tarjeta", DueDateUtc = localMoment.ToUniversalTime() });
            kanbanService.Save();
            vm.ReferenceDate = new DateTime(2026, 3, 10);
            vm.SetViewCommand.Execute(CalendarViewMode.Day);

            Assert.Contains(vm.WeekColumns[0].TimedItems, i => i.Item.SourceId == "card-1");

            // Apagar la fila madre apaga todas las columnas a la vez — la visibilidad sigue siendo de
            // Kanban entero, como antes de que las columnas tuvieran color propio.
            vm.Layers.First(l => l is { IsChildRow: false, Key: CalendarAggregationService.KanbanLayerKey }).IsActive = false;

            Assert.DoesNotContain(vm.WeekColumns[0].TimedItems, i => i.Item.SourceId == "card-1");
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Fact]
    public void Layers_SelectingASwatchRepaintsTheItemsWithoutReplacingTheRow()
    {
        var dir = string.Empty;
        try
        {
            var (vm, kanbanService, _, _) = CreateViewModel(out dir);
            var column = kanbanService.Board.Columns[0];
            column.Cards.Clear();
            var localMoment = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Local);
            column.Cards.Add(new KanbanCard { Id = "card-1", Title = "Tarjeta", DueDateUtc = localMoment.ToUniversalTime() });
            kanbanService.Save();
            vm.ReferenceDate = new DateTime(2026, 3, 10);
            vm.SetViewCommand.Execute(CalendarViewMode.Day);

            var row = vm.Layers.First(l => l.IsChildRow && l.Label == column.Title);
            row.SelectSwatchCommand.Execute("#123ABC");

            // La misma instancia de fila sobrevive al repintado: si se reconstruyera la colección, el
            // emergente abierto se cerraría en cuanto se pulsa un color.
            Assert.Same(row, vm.Layers.First(l => l.IsChildRow && l.Label == column.Title));
            Assert.Equal("#123ABC", row.ColorHex);
            Assert.True(row.HasCustomColor);
            Assert.Equal("#123ABC", vm.WeekColumns[0].TimedItems.Single(i => i.Item.SourceId == "card-1").Item.EffectiveCalendarColorHex);

            row.UseAutomaticColorCommand.Execute(null);

            Assert.Equal(CalendarLayerPalette.BacklogColorHex, row.ColorHex);
            Assert.False(row.HasCustomColor);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    // ===================== Popup del botón "Hoy" =====================

    [Fact]
    public void OpenTodayPopover_ListsPendingFirstThenCompleted_EachAlphabetically()
    {
        var dir = string.Empty;
        try
        {
            var (vm, kanbanService, _, reminderService) = CreateViewModel(out dir);
            var todayAtNoonUtc = DateTime.Today.AddHours(12).ToUniversalTime();
            var column = kanbanService.Board.Columns[0];
            column.Cards.Clear();
            column.Cards.Add(new KanbanCard { Id = "card-1", Title = "zeta", DueDateUtc = todayAtNoonUtc });
            kanbanService.Save();
            reminderService.Create("Beta", string.Empty, todayAtNoonUtc, "Bell");
            var alfa = reminderService.Create("Alfa", string.Empty, todayAtNoonUtc, "Bell");
            var gamma = reminderService.Create("Gamma", string.Empty, todayAtNoonUtc, "Bell");
            reminderService.Create("Mañana", string.Empty, todayAtNoonUtc.AddDays(1), "Bell");
            reminderService.ToggleCompleted(alfa.Id);
            reminderService.ToggleCompleted(gamma.Id);
            // El resumen del día ignora "ocultar completadas": lo hecho también se lista, abajo.
            vm.HideCompletedItems = true;

            // Abrir el resumen no navega: la vista de detrás se queda donde estaba.
            var reference = DateTime.Today.AddMonths(-3);
            vm.ReferenceDate = reference;
            vm.OpenTodayPopoverCommand.Execute(null);

            Assert.True(vm.IsTodayPopoverOpen);
            Assert.Equal(reference, vm.ReferenceDate);
            Assert.Equal(["Beta", "zeta"], vm.TodayPendingItems.Select(i => i.Title));
            Assert.Equal(["Alfa", "Gamma"], vm.TodayCompletedItems.Select(i => i.Title));
            Assert.False(vm.HasNoTodayItems);

            // Completar desde el propio resumen mueve el item de grupo sin cerrar el diálogo.
            vm.ToggleCompleteItemCommand.Execute(vm.TodayPendingItems.Single(i => i.Title == "Beta"));
            Assert.Equal(["zeta"], vm.TodayPendingItems.Select(i => i.Title));
            Assert.Equal(["Alfa", "Beta", "Gamma"], vm.TodayCompletedItems.Select(i => i.Title));
            Assert.True(vm.IsTodayPopoverOpen);

            vm.ShowTodayInCalendarCommand.Execute(null);
            Assert.False(vm.IsTodayPopoverOpen);
            Assert.Equal(DateTime.Today, vm.ReferenceDate);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Fact]
    public void Layers_RenamingAKanbanColumnUpdatesItsRowLabelInPlace()
    {
        var dir = string.Empty;
        try
        {
            var (vm, kanbanService, _, _) = CreateViewModel(out dir);
            var column = kanbanService.Board.Columns[0];
            var row = vm.Layers.First(l => l.IsChildRow && l.Label == column.Title);

            column.Title = "Rebautizada";
            kanbanService.Save();

            Assert.Same(row, vm.Layers.First(l => l.IsChildRow && l.ColorKey == CalendarLayerColorService.KanbanColumnColorKey(column.Id)));
            Assert.Equal("Rebautizada", row.Label);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    [Fact]
    public void ResizeItem_UpdatesPolarTaskDurationMinutes()
    {
        var dir = string.Empty;
        try
        {
            var (vm, _, taskService, _) = CreateViewModel(out dir);
            var list = taskService.CreateList("Lista", "ic_list", "#7F52FF", false);
            var due = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
            var task = taskService.CreateTask(list, "Tarea", "", 0, due, PolarRecurrence.None, 0, []);

            vm.ResizeItem(CalendarItemSourceType.PolarTask, task.Id, 60);

            Assert.Equal(60, task.DurationMinutes);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Fact]
    public void ResizeItem_UpdatesReminderDurationMinutes()
    {
        var dir = string.Empty;
        try
        {
            var (vm, _, _, reminderService) = CreateViewModel(out dir);
            var due = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
            var reminder = reminderService.Create("Recordatorio", "", due, "BellRingOutline");

            vm.ResizeItem(CalendarItemSourceType.Reminder, reminder.Id, 25);

            Assert.Equal(25, reminder.DurationMinutes);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Fact]
    public void MoveItem_ReschedulesKanbanCardToDifferentDayKeepingTime()
    {
        var dir = string.Empty;
        try
        {
            var (vm, kanbanService, _, _) = CreateViewModel(out dir);
            var column = kanbanService.Board.Columns[0];
            column.Cards.Clear();
            var originalLocal = new DateTime(2026, 3, 1, 10, 30, 0, DateTimeKind.Local);
            var card = new KanbanCard { Id = "move-me", Title = "Mover", DueDateUtc = originalLocal.ToUniversalTime() };
            column.Cards.Add(card);
            kanbanService.Save();

            vm.MoveItem(CalendarItemSourceType.Kanban, card.Id, new DateTime(2026, 3, 5), null);

            var movedLocal = card.DueDateUtc!.Value.ToLocalTime();
            Assert.Equal(new DateTime(2026, 3, 5), movedLocal.Date);
            Assert.Equal(originalLocal.TimeOfDay, movedLocal.TimeOfDay);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Fact]
    public void DeleteKanbanCard_PreservesCardIndexInTrashedCard()
    {
        var dir = string.Empty;
        try
        {
            var (vm, kanbanService, _, _) = CreateViewModel(out dir);
            var column = kanbanService.Board.Columns[0];
            column.Cards.Clear();
            var card1 = new KanbanCard { Id = "c1", Title = "Primera" };
            var card2 = new KanbanCard { Id = "c2", Title = "Segunda" };
            column.Cards.Add(card1);
            column.Cards.Add(card2);
            kanbanService.Save();

            var item = new CalendarItemViewModel
            {
                SourceId = card2.Id,
                SourceType = CalendarItemSourceType.Kanban,
                Title = card2.Title,
                MomentUtc = DateTime.UtcNow,
                IsAllDay = true,
                IsCompleted = false,
                LayerColorHex = CalendarItemViewModel.KanbanLayerColorHex,
                LayerKey = CalendarAggregationService.KanbanLayerKey,
                IconKind = MaterialIconKind.ViewColumnOutline
            };
            vm.RequestDeleteItemCommand.Execute(item);
            vm.ConfirmDeleteItemCommand.Execute(null);

            var trashed = kanbanService.Board.TrashedCards.Single(t => t.Card.Id == "c2");
            Assert.Equal(1, trashed.CardIndex);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }
}
