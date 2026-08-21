using System;
using System.Collections.Generic;

namespace enki.Models;

/// <summary>Exact values from docs/supabase-sync/01-modelo-de-datos-local.md /
/// 05-contrato-interoperabilidad.md — the wire string (<c>NONE</c>, <c>MON_WED</c>, …) must match these
/// names exactly, see <c>PolarWireDtos.RecurrenceToWire</c>/<c>WireToRecurrence</c>.</summary>
public enum PolarRecurrence
{
    None,
    Daily,
    Weekly,
    Monthly,
    MonWed,
    FirstDayMonth
}

/// <summary>Local mirror of Supabase's <c>tasks</c> table. See <see cref="PolarTaskList"/> for why there's
/// no separate local autoincrement id — <see cref="Id"/> doubles as the Supabase row id.</summary>
public sealed class PolarTask
{
    public required string Id { get; set; }

    /// <summary>= <see cref="PolarTaskList.Id"/> of the parent list. Stored directly as the same uuid
    /// string used on the wire — no local/remote id translation needed (see <see cref="PolarTaskList"/>).</summary>
    public required string ListId { get; set; }

    public required string Title { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool Completed { get; set; }
    public List<string> Tags { get; set; } = [];
    public DateTime? DueDateUtc { get; set; }
    public int OrderIndex { get; set; }
    public PolarRecurrence Recurrence { get; set; } = PolarRecurrence.None;

    /// <summary>0=Ninguna, 1=Baja, 2=Media, 3=Alta — same 4-value scale as the wire contract.</summary>
    public int Priority { get; set; }

    /// <summary>Path within the Supabase Storage <c>task-images</c> bucket, not a local file URI — Enki
    /// doesn't build an attach-image UI (out of scope for this feature), but the field round-trips so a
    /// value set by Polar is never lost when Enki later edits the same task (docs/supabase-sync/05, rule
    /// 5: preserve fields you don't use).</summary>
    public string? ImagePath { get; set; }

    public int TimeEstimate { get; set; }

    /// <summary>Duración local de la franja horaria en el calendario (minutos). No forma parte del
    /// contrato de sincronización Polar/Supabase documentado en docs/supabase-sync/; se almacena solo en
    /// <c>polar-tasks.json</c> y se pierde si el servidor no añade una columna equivalente en el futuro.
    /// Permite extender el redimensionado del calendario a tareas Polar sin romper el wire contract.</summary>
    public int? DurationMinutes { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public bool Dirty { get; set; } = true;

    /// <summary>See <see cref="PolarTaskList.UserId"/> — same local-only ownership marker, same
    /// account-isolation purpose (docs/analisis-proyecto/roadmap.md, Fase 4).</summary>
    public string? UserId { get; set; }
}
