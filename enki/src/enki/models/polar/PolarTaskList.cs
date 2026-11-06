using System;

namespace enki.Models;

/// <summary>Local mirror of Supabase's <c>task_lists</c> table (docs/supabase-sync/03-esquema-supabase.md)
/// — the Polar-compatible "Gestión de tareas" view in Kanban, entirely separate from
/// <see cref="KanbanBoard"/> (the local-only board keeps working unchanged; this is an additional data
/// domain that only exists once the user links Enki to Polar, see <c>PolarAuthService</c>).</summary>
public sealed class PolarTaskList
{
    /// <summary>Also the Supabase row's <c>id</c> — a client-generated uuid v4 string (docs/supabase-sync/
    /// 05-contrato-interoperabilidad.md, rule 3). Unlike Polar's Android/Room model there's no separate
    /// local autoincrement id to translate: Enki uses the uuid itself as the local primary key too.</summary>
    public required string Id { get; set; }

    public required string Title { get; set; }
    public string Icon { get; set; } = "ic_list";
    public string Color { get; set; } = "#7F52FF";
    public int OrderIndex { get; set; }
    public int HomeOrderIndex { get; set; }
    public bool IsDependencyChain { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Local-only — never sent to Supabase. True while this row has changes not yet confirmed
    /// pushed; see docs/supabase-sync/04-estrategia-sincronizacion.md.</summary>
    public bool Dirty { get; set; } = true;

    /// <summary>Local-only ownership marker: the Supabase <c>auth.uid()</c> that was linked the moment this
    /// row first became dirty (created, or edited while unowned), stamped by <c>PolarTaskService</c>/
    /// <c>PolarSyncService</c> — never sent on the wire itself. Null means "created while no account was
    /// linked, or predates this field" and is free to be claimed by whichever account links/pushes it next;
    /// once non-null, a row only ever pushes under a sync session for that same account, closing the
    /// cross-account leak where offline edits made under one Polar account got pushed under the next one
    /// linked on the same device (docs/analisis-proyecto/roadmap.md, Fase 4).</summary>
    public string? UserId { get; set; }
}
