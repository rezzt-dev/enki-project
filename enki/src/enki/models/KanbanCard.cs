using System;
using System.Collections.Generic;
using System.Globalization;

namespace enki.Models;

public sealed class KanbanCard
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = string.Empty;
    public KanbanPriority Priority { get; set; } = KanbanPriority.Medium;
    public DateTime? DueDateUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<KanbanSubtask> Subtasks { get; set; } = [];

    /// <summary>Etiquetas de texto libre compartidas con el resto del sistema (Notas, Polar). La
    /// serialización JSON del tablero las persiste automáticamente.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Ruta completa (<see cref="Models.NoteFile.FullPath"/>) de la nota vinculada a esta tarjeta,
    /// o <c>null</c> si no tiene ninguna — enlace nota↔tarea, "Capa 1". Es la mitad "tarea" del enlace
    /// nota↔tarea; la nota no guarda ningún campo recíproco, la
    /// sección "Tareas vinculadas" del editor de notas se deriva buscando este campo en todas las tarjetas
    /// (<see cref="Services.KanbanService"/>). Se mantiene sincronizado al renombrar/mover la nota
    /// (<see cref="Services.KanbanService.RelinkNotePath"/>); si la nota se elimina, el enlace queda
    /// apuntando a una ruta que ya no existe — mismo comportamiento tolerante que el resto del vault ante
    /// una nota borrada (ver <see cref="Services.VaultService.GetNote"/>).</summary>
    public string? LinkedNotePath { get; set; }

    /// <summary>Optional franja horaria (docs/calendar-feature/04-vistas-y-navegacion.md §5): minutes the
    /// card occupies starting at <see cref="DueDateUtc"/>, e.g. a 21:30 due time with
    /// <c>DurationMinutes = 45</c> renders as 21:30–22:15 in the calendar's Semana/Día grid. <c>null</c> or
    /// <c>0</c> means "sin duración" — same point-in-time behavior as before this field existed. Only
    /// meaningful when <see cref="DueDateUtc"/> carries a time of day (not local midnight); a duration on an
    /// all-day card is never rendered. Kanban-only by design: <c>PolarTask</c>/<c>Reminder</c> don't carry
    /// this field (see the calendar docs for why).</summary>
    public int? DurationMinutes { get; set; }

    /// <summary>True once <paramref name="dueUtc"/> is behind us. A due date carrying a specific time of
    /// day (anything but local midnight — the calendar-only picker always leaves it at midnight) is
    /// compared against the current instant; a plain calendar date is compared whole-day, so "vence hoy"
    /// stays true for the entire day instead of flipping to overdue the instant the clock passes 00:00.
    /// Shared by <see cref="ViewModels.KanbanCardViewModel.IsOverdue"/> and
    /// <see cref="ViewModels.ReminderItemViewModel"/>'s bucketing so both agree on the same cutoff.</summary>
    public static bool IsPastDue(DateTime dueUtc)
    {
        var local = dueUtc.ToLocalTime();
        return local.TimeOfDay != TimeSpan.Zero ? local < DateTime.Now : local.Date < DateTime.Now.Date;
    }

    public static string FormatDueLabel(DateTime dueUtc)
    {
        var local = dueUtc.ToLocalTime();
        return local.TimeOfDay != TimeSpan.Zero
            ? local.ToString("dd MMM HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("dd MMM", CultureInfo.CurrentCulture);
    }
}
