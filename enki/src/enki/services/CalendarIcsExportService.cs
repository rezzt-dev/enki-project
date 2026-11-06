using System;
using System.Collections.Generic;
using System.Linq;
using enki.Models;
using enki.ViewModels;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;

namespace enki.Services;

/// <summary>Un elemento de agenda listo para escribirse como <c>VEVENT</c>.</summary>
/// <param name="Uid">Identificador estable: volver a exportar el mismo elemento actualiza el evento en el
/// otro calendario en vez de duplicarlo.</param>
/// <param name="Recurrence">Recurrencia de la entidad real (tarea Polar o recordatorio); se escribe como
/// <c>RRULE</c> en lugar de expandir las repeticiones.</param>
public sealed record IcsEntry(string Uid, string Title, string Description, DateTime StartUtc, bool IsAllDay,
    int? DurationMinutes, PolarRecurrence Recurrence);

/// <summary>Exporta la agenda de Enki a un archivo iCalendar (<c>.ics</c>) para llevar las fechas a otro
/// calendario (MED-04). Es una acción manual y sin servidor: lee lo que ya agrega
/// <see cref="CalendarAggregationService"/> —tarjetas Kanban con fecha, tareas y subtareas de Polar y
/// recordatorios— y no escribe nada en ninguna entidad.
///
/// <para><b>Qué se exporta.</b> Lo pendiente: lo completado se deja fuera porque en otro calendario sería
/// ruido. Los eventos de calendarios externos suscritos tampoco: ya viven en otro calendario. Cada tarjeta
/// con fecha de inicio y de fin produce dos eventos, igual que en la vista de calendario.</para>
///
/// <para><b>Horas y repeticiones.</b> Los elementos sin repetición se escriben en UTC, así que son el
/// mismo instante en cualquier calendario. Los repetidos se escriben con la zona horaria del sistema y su
/// <c>VTIMEZONE</c>, porque Enki repite sobre la hora de pared local (ver
/// <see cref="PolarTaskService.CalculateNextDueDate"/>): «todos los días a las 09:00» sigue siendo las
/// 09:00 tras un cambio de horario, y en UTC se movería una hora. Los elementos de día completo son
/// eventos <c>VALUE=DATE</c>. Sin duración, el evento es puntual (sin <c>DTEND</c>), como en Enki.</para>
///
/// <para>Una diferencia conocida: «mensual» de Enki suma un mes sobre la fecha anterior (31 de enero →
/// 28 de febrero → 28 de marzo); <c>FREQ=MONTHLY</c> de iCalendar omite los meses que no tienen ese día.</para></summary>
public sealed class CalendarIcsExportService(
    CalendarAggregationService aggregation, ReminderService reminders, PolarTaskService polarTasks)
{
    /// <summary>Margen alrededor de hoy dentro del que se buscan fechas. No hay recorrido por días, así
    /// que un rango enorme no cuesta más que uno pequeño.</summary>
    private const int RangeYears = 100;

    /// <summary>Elementos que se exportarían ahora, ordenados por fecha.</summary>
    public IReadOnlyList<IcsEntry> CollectEntries()
    {
        var now = DateTime.UtcNow;
        var items = aggregation.BuildItems(now.AddYears(-RangeYears), now.AddYears(RangeYears),
            includeGhostOccurrences: false, includeExternalEvents: false);

        var recurrences = new Dictionary<string, PolarRecurrence>(StringComparer.Ordinal);
        foreach (var reminder in reminders.ActiveReminders.Where(r => !r.IsDeleted && r.Recurrence != PolarRecurrence.None))
            recurrences["reminder:" + reminder.Id] = reminder.Recurrence;
        foreach (var task in polarTasks.Store.Tasks.Where(t => !t.IsDeleted && t.Recurrence != PolarRecurrence.None))
            recurrences["polar:" + task.Id] = task.Recurrence;

        var entries = new List<IcsEntry>();
        foreach (var item in items)
        {
            if (item.IsCompleted || item.IsGhostOccurrence || item.SourceType == CalendarItemSourceType.ExternalCalendar) continue;

            var kind = item.SourceType switch
            {
                CalendarItemSourceType.Reminder => "reminder",
                CalendarItemSourceType.PolarTask => "polar",
                CalendarItemSourceType.PolarSubtask => "polar-subtask",
                _ => "kanban"
            };
            var recurrence = recurrences.GetValueOrDefault(kind + ":" + item.SourceId, PolarRecurrence.None);
            var uid = $"{kind}-{item.SourceId}{(item.IsOpeningMilestone ? "-start" : string.Empty)}@enki";
            entries.Add(new IcsEntry(uid, item.Title, ComposeDescription(item), item.MomentUtc, item.IsAllDay,
                item.DurationMinutes, recurrence));
        }

        return entries.OrderBy(e => e.StartUtc).ToList();
    }

    /// <summary>Texto del archivo con lo que hay ahora en la agenda.</summary>
    public string Build() => Write(CollectEntries(), TimeZoneInfo.Local, DateTime.UtcNow);

    private static string ComposeDescription(CalendarItemViewModel item)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Description)) parts.Add(item.Description.Trim());
        if (item.Subtasks.Count > 0)
            parts.Add(string.Join('\n', item.Subtasks.Select(s => (s.IsDone ? "[x] " : "[ ] ") + s.Title)));
        return string.Join("\n\n", parts);
    }

    /// <summary>Serializa <paramref name="entries"/> como un calendario iCalendar. Pura: no lee ningún
    /// servicio, así que se prueba con una zona y una hora inyectadas.</summary>
    public static string Write(IEnumerable<IcsEntry> entries, TimeZoneInfo zone, DateTime nowUtc)
    {
        var calendar = new Calendar { ProductId = "-//Enki//Calendar export//ES" };
        calendar.AddProperty("X-WR-CALNAME", "Enki");
        var zoneId = IanaZoneId(zone);
        var usesZone = false;

        foreach (var entry in entries)
        {
            var calendarEvent = new CalendarEvent
            {
                Uid = entry.Uid,
                Summary = entry.Title,
                Description = string.IsNullOrWhiteSpace(entry.Description) ? null : entry.Description,
                DtStamp = new CalDateTime(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc))
            };

            var startUtc = DateTime.SpecifyKind(entry.StartUtc, DateTimeKind.Utc);
            var local = TimeZoneInfo.ConvertTimeFromUtc(startUtc, zone);
            if (entry.IsAllDay)
            {
                var day = local.Date;
                calendarEvent.Start = new CalDateTime(day.Year, day.Month, day.Day);
                calendarEvent.End = new CalDateTime(day.AddDays(1).Year, day.AddDays(1).Month, day.AddDays(1).Day);
            }
            else
            {
                var recurring = entry.Recurrence != PolarRecurrence.None && zoneId is not null;
                calendarEvent.Start = recurring
                    ? new CalDateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second, zoneId)
                    : new CalDateTime(startUtc);
                usesZone |= recurring;
                if (entry.DurationMinutes is > 0 and var minutes)
                {
                    var end = startUtc.AddMinutes(minutes);
                    var endLocal = TimeZoneInfo.ConvertTimeFromUtc(end, zone);
                    calendarEvent.End = recurring
                        ? new CalDateTime(endLocal.Year, endLocal.Month, endLocal.Day, endLocal.Hour, endLocal.Minute, endLocal.Second, zoneId)
                        : new CalDateTime(end);
                }
            }

            if (RecurrenceRule(entry.Recurrence) is { } rule) calendarEvent.RecurrenceRule = rule;
            calendar.Events.Add(calendarEvent);
        }

        if (usesZone) calendar.AddTimeZone(VTimeZone.FromSystemTimeZone(zone));
        return new CalendarSerializer().SerializeToString(calendar) ?? string.Empty;
    }

    private static RecurrencePattern? RecurrenceRule(PolarRecurrence recurrence) => recurrence switch
    {
        PolarRecurrence.Daily => new RecurrencePattern(FrequencyType.Daily, 1),
        PolarRecurrence.Weekly => new RecurrencePattern(FrequencyType.Weekly, 1),
        PolarRecurrence.Monthly => new RecurrencePattern(FrequencyType.Monthly, 1),
        PolarRecurrence.MonWed => new RecurrencePattern(FrequencyType.Weekly, 1)
        {
            ByDay = [new WeekDay(DayOfWeek.Monday), new WeekDay(DayOfWeek.Wednesday)]
        },
        PolarRecurrence.FirstDayMonth => new RecurrencePattern(FrequencyType.Monthly, 1) { ByMonthDay = [1] },
        _ => null
    };

    /// <summary>Identificador IANA de la zona (<c>Europe/Madrid</c>), que es lo que entienden los demás
    /// calendarios; en Windows el identificador de la zona es otro y se convierte. <see langword="null"/>
    /// si no hay equivalente: entonces los repetidos se escriben en UTC.</summary>
    internal static string? IanaZoneId(TimeZoneInfo zone)
    {
        if (zone.Id.Contains('/', StringComparison.Ordinal) || zone.Id == "UTC") return zone.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null;
    }
}
