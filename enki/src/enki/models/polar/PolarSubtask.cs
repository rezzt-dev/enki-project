using System;

namespace enki.Models;

/// <summary>Local mirror of Supabase's <c>subtasks</c> table. See <see cref="PolarTaskList"/> for why
/// <see cref="Id"/> doubles as the Supabase row id with no local/remote translation.</summary>
public sealed class PolarSubtask
{
    public required string Id { get; set; }

    /// <summary>= <see cref="PolarTask.Id"/> of the parent task.</summary>
    public required string TaskId { get; set; }

    public required string Title { get; set; }
    public bool Completed { get; set; }
    public DateTime? DueDateUtc { get; set; }

    /// <summary>New relative to Polar's current Room schema (which orders by insertion id) — added from
    /// the start here since it's already part of the Supabase contract (docs/supabase-sync/03, note on
    /// <c>subtasks.order_index</c>).</summary>
    public int OrderIndex { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public bool Dirty { get; set; } = true;

    /// <summary>See <see cref="PolarTaskList.UserId"/> — same local-only ownership marker, same
    /// account-isolation purpose (docs/analisis-proyecto/roadmap.md, Fase 4).</summary>
    public string? UserId { get; set; }
}
