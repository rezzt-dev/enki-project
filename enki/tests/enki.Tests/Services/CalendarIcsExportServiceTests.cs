using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Exportación del calendario a iCalendar (MED-04): el archivo se vuelve a leer con el mismo
/// analizador que usa la aplicación para suscribirse a calendarios, y con el lector de importación.</summary>
public sealed class CalendarIcsExportServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enki-ics-" + Guid.NewGuid().ToString("N"));

    public CalendarIcsExportServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static TimeZoneInfo? Madrid()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid"); }
        catch (TimeZoneNotFoundException) { return null; }
    }

    private static Calendar Parse(string ics) => Calendar.Load(ics)!;

    [Fact]
    public void Write_TimedEventWithDuration_IsUtcWithTheSameInstantAndLength()
    {
        var start = new DateTime(2026, 11, 20, 9, 30, 0, DateTimeKind.Utc);

        var ics = CalendarIcsExportService.Write(
            [new IcsEntry("kanban-1@enki", "Entrega, final; v2", "Línea 1\nLínea 2", start, false, 90, PolarRecurrence.None)],
            TimeZoneInfo.Utc, Now);

        Assert.StartsWith("BEGIN:VCALENDAR", ics);
        Assert.Contains("VERSION:2.0", ics);
        Assert.Contains("UID:kanban-1@enki", ics);
        var item = Assert.Single(Parse(ics).Events);
        Assert.Equal("Entrega, final; v2", item.Summary);
        Assert.Equal("Línea 1\nLínea 2", item.Description!.Replace("\r\n", "\n"));
        Assert.Equal(start, item.Start!.AsUtc);
        Assert.Equal(start.AddMinutes(90), item.End!.AsUtc);
        Assert.Null(item.RecurrenceRule);
        Assert.DoesNotContain("BEGIN:VTIMEZONE", ics);
    }

    [Fact]
    public void Write_AllDayEvent_UsesDateValueAndTheLocalDay()
    {
        var zone = Madrid() ?? TimeZoneInfo.Utc;
        // Medianoche local del 15 de octubre = 22:00 UTC del 14 en Madrid.
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 15), zone);

        var ics = CalendarIcsExportService.Write(
            [new IcsEntry("reminder-1@enki", "Cumpleaños", "", startUtc, true, 60, PolarRecurrence.None)], zone, Now);

        Assert.Contains("DTSTART;VALUE=DATE:20261015", ics);
        Assert.Contains("DTEND;VALUE=DATE:20261016", ics);
        var item = Assert.Single(Parse(ics).Events);
        Assert.True(item.IsAllDay);
        Assert.Equal(new DateTime(2026, 10, 15), item.Start!.Value.Date);
    }

    [Fact]
    public void Write_EventWithoutDuration_HasNoEnd()
    {
        var ics = CalendarIcsExportService.Write(
            [new IcsEntry("reminder-2@enki", "Aviso", "", new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc), false, null, PolarRecurrence.None)],
            TimeZoneInfo.Utc, Now);

        Assert.DoesNotContain("DTEND", ics);
        Assert.DoesNotContain("DURATION", ics);
    }

    [Theory]
    [InlineData(PolarRecurrence.Daily, "FREQ=DAILY")]
    [InlineData(PolarRecurrence.Weekly, "FREQ=WEEKLY")]
    [InlineData(PolarRecurrence.Monthly, "FREQ=MONTHLY")]
    [InlineData(PolarRecurrence.MonWed, "BYDAY=MO,WE")]
    [InlineData(PolarRecurrence.FirstDayMonth, "BYMONTHDAY=1")]
    public void Write_Recurrence_BecomesAnRRule(PolarRecurrence recurrence, string expected)
    {
        var ics = CalendarIcsExportService.Write(
            [new IcsEntry("polar-1@enki", "Repetida", "", new DateTime(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc), false, 30, recurrence)],
            TimeZoneInfo.Utc, Now);

        Assert.Contains("RRULE:", ics);
        Assert.Contains(expected, ics);
    }

    [Fact]
    public void Write_RecurringEvent_KeepsTheWallClockHourAcrossDaylightSavingChanges()
    {
        if (Madrid() is not { } zone) return;
        // Todos los días a las 09:00 de Madrid, empezando antes del cambio de hora de octubre de 2026.
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 20, 9, 0, 0), zone);

        var ics = CalendarIcsExportService.Write(
            [new IcsEntry("polar-2@enki", "Diaria", "", startUtc, false, 30, PolarRecurrence.Daily)], zone, Now);

        Assert.Contains("BEGIN:VTIMEZONE", ics);
        Assert.Contains("TZID=Europe/Madrid", ics);
        var occurrences = Parse(ics).GetOccurrences(new CalDateTime(2026, 10, 20, 0, 0, 0, "UTC"))
            .Take(12).Select(o => o.Period.StartTime.AsUtc).ToList();
        foreach (var occurrence in occurrences)
            Assert.Equal(9, TimeZoneInfo.ConvertTimeFromUtc(occurrence, zone).Hour);
        // Antes (octubre 25 aún CEST) y después del cambio (UTC distinto) la hora local es la misma.
        Assert.Contains(occurrences, o => o.Hour == 7);
        Assert.Contains(occurrences, o => o.Hour == 8);
    }

    [Fact]
    public void Write_RoundTripsThroughTheImportReader_WithTheSameHours()
    {
        var zone = Madrid() ?? TimeZoneInfo.Utc;
        var timed = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 12, 3, 17, 45, 0), zone);
        var allDay = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 12, 24), zone);
        var recurring = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 12, 7, 8, 15, 0), zone);

        var ics = CalendarIcsExportService.Write(
        [
            new IcsEntry("a@enki", "Con hora", "", timed, false, 45, PolarRecurrence.None),
            new IcsEntry("b@enki", "Todo el día", "", allDay, true, null, PolarRecurrence.None),
            new IcsEntry("c@enki", "Semanal", "", recurring, false, 20, PolarRecurrence.Weekly)
        ], zone, Now);
        var file = Path.Combine(_root, "export.ics");
        File.WriteAllText(file, ics);

        var plan = ExternalImportService.ReadGoogleCalendar(file);

        Assert.Equal(3, plan.Events.Count);
        Assert.Equal(timed, plan.Events.Single(e => e.Title == "Con hora").StartUtc);
        Assert.Equal(45, plan.Events.Single(e => e.Title == "Con hora").DurationMinutes);
        Assert.Equal(allDay, plan.Events.Single(e => e.Title == "Todo el día").StartUtc);
        Assert.Equal(recurring, plan.Events.Single(e => e.Title == "Semanal").StartUtc);
        Assert.Equal(1, plan.RecurringSeries);
    }

    [Fact]
    public void Write_LongNonAsciiText_IsFoldedAndSurvivesARoundTrip()
    {
        var title = string.Concat(Enumerable.Repeat("Reunión de diseño ñandú ", 12)).Trim();

        var ics = CalendarIcsExportService.Write(
            [new IcsEntry("x@enki", title, "", new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc), false, null, PolarRecurrence.None)],
            TimeZoneInfo.Utc, Now);

        Assert.All(ics.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75, line));
        Assert.Equal(title, Parse(ics).Events.Single().Summary);
    }

    [Fact]
    public void IanaZoneId_ReturnsAnIanaNameOrNull()
    {
        Assert.Equal("UTC", CalendarIcsExportService.IanaZoneId(TimeZoneInfo.Utc));
        if (Madrid() is { } madrid) Assert.Equal("Europe/Madrid", CalendarIcsExportService.IanaZoneId(madrid));
    }

    [Fact]
    public void CollectEntries_ReadsRealEntities_SkipsCompletedAndKeepsRecurrence()
    {
        var kanban = new KanbanService(_root);
        var reminders = new ReminderService(_root);
        var polar = new PolarTaskService(_root);
        var aggregation = new CalendarAggregationService(kanban, polar, reminders, new PolarAuthService(new SettingsService(_root)));
        var start = DateTime.UtcNow.AddDays(3).Date.AddHours(8);
        var column = kanban.DefaultBoard.Columns[0];
        column.Cards.Add(new KanbanCard { Id = "abierta", Title = "Tarjeta abierta", DueDateUtc = start, DurationMinutes = 30 });
        column.Cards.Add(new KanbanCard { Id = "hecha", Title = "Tarjeta hecha", DueDateUtc = start, CompletedAtUtc = DateTime.UtcNow });
        column.Cards.Add(new KanbanCard { Id = "rango", Title = "Con rango", StartDateUtc = start, DueDateUtc = start.AddDays(2) });
        column.Cards.Add(new KanbanCard { Id = "sinfecha", Title = "Sin fecha" });
        var repeated = reminders.Create("Tomar medicina", "", start.AddHours(1), "BellRingOutline", 15, PolarRecurrence.Daily);

        var entries = new CalendarIcsExportService(aggregation, reminders, polar).CollectEntries();

        Assert.DoesNotContain(entries, e => e.Title.Contains("hecha", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Title.Contains("Sin fecha", StringComparison.Ordinal));
        var reminder = entries.Single(e => e.Title == "Tomar medicina");
        Assert.Equal(PolarRecurrence.Daily, reminder.Recurrence);
        Assert.Equal($"reminder-{repeated.Id}@enki", reminder.Uid);
        Assert.Equal(15, reminder.DurationMinutes);
        var open = entries.Single(e => e.Title == "Tarjeta abierta");
        Assert.Equal("kanban-abierta@enki", open.Uid);
        Assert.Equal(PolarRecurrence.None, open.Recurrence);
        Assert.Equal(2, entries.Count(e => e.Uid.StartsWith("kanban-rango", StringComparison.Ordinal)));
        Assert.Contains(entries, e => e.Uid == "kanban-rango-start@enki");
        Assert.Equal(entries.OrderBy(e => e.StartUtc).Select(e => e.Uid), entries.Select(e => e.Uid));
        // Una repetición no se expande: un solo evento con RRULE, aunque el rango sea de un siglo.
        Assert.Single(entries, e => e.Title == "Tomar medicina");
    }
}
