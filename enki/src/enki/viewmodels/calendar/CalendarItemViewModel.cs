using System;
using System.Collections.Generic;
using System.Globalization;
using enki.Localization;
using enki.Models;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Read projection of one subtask/child shown in a <see cref="CalendarItemViewModel"/>'s detail
/// popover (docs/calendar-feature/06-creacion-y-edicion-de-items.md §5) — listed read-only except for the
/// completed checkbox, which round-trips through <see cref="CalendarViewModel.ToggleSubtaskCommand"/> to
/// whichever real service owns it (Kanban or Polar). Never persisted itself.</summary>
public sealed record CalendarSubtaskViewModel
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required bool IsDone { get; init; }

    /// <summary>Which service actually owns this row — <see cref="CalendarItemSourceType.Kanban"/> for a
    /// <c>KanbanSubtask</c>, <see cref="CalendarItemSourceType.PolarTask"/> for a <c>PolarSubtask</c>.
    /// Lets <see cref="CalendarViewModel.ToggleSubtaskCommand"/> route to the right service without this
    /// record needing a back-reference to its parent's live model.</summary>
    public required CalendarItemSourceType ParentSourceType { get; init; }

    public MaterialIconKind CheckIcon => IsDone ? MaterialIconKind.CheckboxMarkedOutline : MaterialIconKind.CheckboxBlankOutline;
}

/// <summary>See docs/calendar-feature/03-glosario-y-modelo-de-datos.md §3 — one of the three original
/// domains (Kanban/Polar/Reminder). Determines which editor Fase 3 will navigate to on open.</summary>
public enum CalendarItemSourceType
{
    Kanban,
    PolarTask,
    PolarSubtask,
    Reminder
}

/// <summary>In-memory projection unifying a <see cref="KanbanCard"/>, <see cref="Models.PolarTask"/>/
/// <see cref="Models.PolarSubtask"/> or <see cref="Reminder"/> into one shape the calendar views render —
/// see docs/calendar-feature/03-glosario-y-modelo-de-datos.md §3. Never persisted, never mutated in place:
/// <see cref="Services.CalendarAggregationService"/> rebuilds the whole list from scratch on every
/// refresh, so this is a plain immutable record rather than an <c>ObservableObject</c> (nothing about an
/// individual item ever changes without the whole list being rebuilt around it anyway). Read-only in
/// Fase 1 (docs/calendar-feature/12-plan-de-implementacion-por-fases.md) — dragging/editing arrive in
/// Fase 3, ghost recurrence occurrences in Fase 4.</summary>
public sealed record CalendarItemViewModel
{
    // Fixed calendar-layer colors (docs/calendar-feature/03-glosario-y-modelo-de-datos.md §5): Kanban
    // cards have no per-card color today and Reminder has none either, so both get a constant from the
    // calendar's own palette instead of reusing PolarTaskList.Color (which only Polar tasks/subtasks have).
    public const string KanbanLayerColorHex = "#E5E5E5";
    public const string ReminderLayerColorHex = "#818CF8";

    /// <summary>Same value as <c>DangerColor</c>/<c>DangerBrush</c> in styles/Colors.axaml (docs/
    /// calendar-feature/08-notificaciones-e-integracion.md §3: "el calendario debe aplicar esa misma
    /// fórmula... nunca tres criterios ligeramente distintos"). Duplicated as a plain constant rather than
    /// looked up via <c>Application.Current.Resources</c> on purpose — this record has no Avalonia
    /// dependency today (it's built and compared in xUnit tests without a running Application), and a
    /// resource lookup here would introduce one just for a color that never actually needs to change at
    /// runtime.</summary>
    private const string DangerColorHex = "#F87171";

    public required string SourceId { get; init; }
    public required CalendarItemSourceType SourceType { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = string.Empty;

    /// <summary>The instant that positions this item — <c>DueDateUtc</c> for Kanban/Polar or
    /// <c>NotifyAtUtc</c> for Reminder.</summary>
    public required DateTime MomentUtc { get; init; }

    /// <summary><c>true</c> when <see cref="MomentUtc"/>, converted to local time, falls exactly at
    /// local midnight — same criterion <see cref="KanbanCard.IsPastDue"/>/<c>FormatDueLabel</c> already
    /// use to distinguish "day only" from "day + time".</summary>
    public required bool IsAllDay { get; init; }

    public required bool IsCompleted { get; init; }
    public required string LayerColorHex { get; init; }
    public required MaterialIconKind IconKind { get; init; }
    public bool HasSubtasksOrChildren { get; init; }

    /// <summary>Minutes this item occupies starting at <see cref="MomentUtc"/> — only ever set for
    /// <see cref="CalendarItemSourceType.Kanban"/> (mirrors <c>KanbanCard.DurationMinutes</c>, docs/
    /// calendar-feature/04-vistas-y-navegacion.md §5). <c>null</c> means "sin duración", drawn with the
    /// same fixed minimum visual height every timed item used before this field existed.</summary>
    public int? DurationMinutes { get; init; }

    /// <summary>Which layer toggle (docs/calendar-feature/07-filtros-busqueda-y-capas.md §1) this item
    /// belongs to: <c>"kanban"</c>, <c>"reminder"</c>, or a <c>PolarTaskList.Id</c> for Polar tasks/
    /// subtasks — a <see cref="CalendarItemSourceType.PolarSubtask"/> uses its parent task's list, since
    /// subtasks aren't a separately filterable layer of their own.</summary>
    public required string LayerKey { get; init; }

    /// <summary>Mapped from <c>KanbanCard.Priority</c>/<c>PolarTask.Priority</c> (docs/calendar-feature/
    /// 07-filtros-busqueda-y-capas.md §3) — always <see cref="CalendarItemPriority.None"/> for
    /// <see cref="CalendarItemSourceType.Reminder"/> and <see cref="CalendarItemSourceType.PolarSubtask"/>
    /// (neither model carries its own priority).</summary>
    public CalendarItemPriority Priority { get; init; } = CalendarItemPriority.None;

    /// <summary>Used by the Dashboard "HOY" list to decide whether to render the priority dot — a
    /// <see cref="CalendarItemPriority.None"/> item leaves the icon column clean.</summary>
    public bool HasPriority => Priority != CalendarItemPriority.None;

    /// <summary>Copied from <c>PolarTask.Tags</c> — empty for every other source (docs/calendar-feature/
    /// 03-glosario-y-modelo-de-datos.md §2.2: subtasks don't carry their own tags).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>True only for a calculated future occurrence of a recurring <c>PolarTask</c> (docs/
    /// calendar-feature/03-glosario-y-modelo-de-datos.md §4) — never persisted, never draggable/
    /// completable/deletable (see <see cref="CanToggleComplete"/>/<see cref="CanDelete"/>), built by
    /// <see cref="Services.CalendarAggregationService.BuildGhostOccurrences"/>.</summary>
    public bool IsGhostOccurrence { get; init; }

    /// <summary>True only while an active search (docs/calendar-feature/07-filtros-busqueda-y-capas.md §2)
    /// matches this item's title/description — purely a presentation flag recomputed on every rebuild, it
    /// never hides an item, only highlights it (search is "resalta, no navega automáticamente").</summary>
    public bool IsSearchMatch { get; init; }

    /// <summary>Parent Polar task/list title, shown as secondary context in the detail popover — empty
    /// for Kanban/Reminder items.</summary>
    public string ParentContextLabel { get; init; } = string.Empty;

    /// <summary>The parent <see cref="Models.PolarTask.Id"/> for a <see cref="CalendarItemSourceType.PolarSubtask"/>
    /// item — used by "Editar" (docs/calendar-feature/06-creacion-y-edicion-de-items.md §3) to open the
    /// parent task's editor, since a subtask has no editor of its own. Empty for every other source.</summary>
    public string ParentSourceId { get; init; } = string.Empty;

    /// <summary>Read-only list shown in the detail popover (docs/calendar-feature/
    /// 06-creacion-y-edicion-de-items.md §5) — Kanban's <c>KanbanSubtask</c>/Polar's <c>PolarSubtask</c>
    /// children of this item, never a <see cref="CalendarItemSourceType.PolarSubtask"/>'s own children
    /// (subtasks don't nest further).</summary>
    public IReadOnlyList<CalendarSubtaskViewModel> Subtasks { get; init; } = [];

    /// <summary>Kanban cards have no "completado" field of their own (docs/calendar-feature/
    /// 03-glosario-y-modelo-de-datos.md §3) — the popover simply doesn't offer the checkbox for them.
    /// A ghost occurrence is never real enough to complete either (docs/calendar-feature/
    /// 03-glosario-y-modelo-de-datos.md §4: "no se puede... completar").</summary>
    public bool CanToggleComplete => SourceType != CalendarItemSourceType.Kanban && !IsGhostOccurrence;

    /// <summary>Same "no se puede eliminar" rule as <see cref="CanToggleComplete"/> for a ghost occurrence
    /// — real items are always deletable through the popover/context menu.</summary>
    public bool CanDelete => !IsGhostOccurrence;

    /// <summary>Backs the resize handle on the Semana/Día timed chip (docs/calendar-feature/
    /// 05-interacciones-y-microinteracciones.md §2b) — any real, timed item can persist a dragged duration
    /// via its source service (Kanban/Polar/Reminder). All-day and ghost occurrences never show the handle.</summary>
    public bool CanResize => SourceType != CalendarItemSourceType.PolarSubtask && !IsGhostOccurrence && !IsAllDay;

    /// <summary>Transparent regardless of <see cref="LayerColorHex"/> for a ghost occurrence (docs/
    /// calendar-feature/10-diseno-visual-y-componentes.md §4: "sin fondo relleno" — the alpha-zero hex
    /// stays zero through <c>HexColorToBrushConverter</c>'s opacity multiplication no matter what
    /// <c>ConverterParameter</c> the chip binds with).</summary>
    public string ChipBackgroundColorHex => IsGhostOccurrence ? "#00000000" : LayerColorHex;

    /// <summary>Shown instead of the usual complete/delete actions in the detail popover for a ghost
    /// occurrence (docs/calendar-feature/03-glosario-y-modelo-de-datos.md §4: "al hacer clic... con una
    /// nota tipo 'próxima ocurrencia estimada: fecha'"). Empty for a real item.</summary>
    public string GhostOccurrenceNoteLabel => IsGhostOccurrence
        ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Calendar_Item_GhostOccurrenceNote"], MomentLocal.ToString("d MMM", CultureInfo.CurrentCulture))
        : string.Empty;

    /// <summary>Backs the "Completar" action in the detail popover (docs/calendar-feature/
    /// 05-interacciones-y-microinteracciones.md §5) — same icon-toggles-label pattern already used by
    /// <c>PolarTaskDetailViewModel.CompletedIcon</c>/<c>CompletedLabel</c>.</summary>
    public MaterialIconKind CompletedToggleIcon => IsCompleted ? MaterialIconKind.CheckboxMarkedOutline : MaterialIconKind.CheckboxBlankOutline;
    public string CompletedToggleLabel => IsCompleted
        ? LocalizationService.Instance["Calendar_Item_Completed"]
        : LocalizationService.Instance["Calendar_Item_MarkCompleted"];

    public DateTime MomentLocal => MomentUtc.ToLocalTime();

    /// <summary>Reuses <see cref="KanbanCard.IsPastDue"/> verbatim for all three sources, applied to
    /// whichever moment positions this item — see docs/calendar-feature/03-glosario-y-modelo-de-datos.md
    /// §3. A completed item is never shown as overdue regardless of its date.</summary>
    public bool IsPastDue => !IsCompleted && KanbanCard.IsPastDue(MomentUtc);

    /// <summary>"Vencido siempre gana visualmente sobre el color de origen" (docs/calendar-feature/
    /// 10-diseno-visual-y-componentes.md §4) — only the chip's border switches to the danger color; the
    /// low-opacity fill keeps using <see cref="LayerColorHex"/> so the origin is still identifiable.</summary>
    public string EffectiveBorderColorHex => IsPastDue ? DangerColorHex : LayerColorHex;

    /// <summary>"HH:mm" for a plain point-in-time item, "HH:mm–HH:mm" once <see cref="DurationMinutes"/>
    /// gives it a real franja horaria — used by Mes/Agenda/the detail popover, where there's no vertical
    /// span to spatially convey "empieza aquí, termina aquí" the way the Semana/Día timed chip can (see
    /// <see cref="HasDuration"/>/<see cref="EndTimeLabel"/> below).</summary>
    public string TimeLabel => IsAllDay
        ? string.Empty
        : DurationMinutes is { } duration
            ? $"{MomentLocal:HH:mm}–{MomentLocal.AddMinutes(duration):HH:mm}"
            : MomentLocal.ToString("HH:mm", CultureInfo.CurrentCulture);

    /// <summary>Addendum post-Fase 5: <c>true</c> only for a real, timed franja horaria — backs the
    /// Semana/Día timed chip's separate start/end labels (docs/calendar-feature/
    /// 10-diseno-visual-y-componentes.md §4) so the two times sit where they actually happen (start
    /// pinned to the top edge, end pinned to the bottom edge) instead of both crammed into one string at
    /// the top, which stopped being legible once the block is taller than a couple of lines.</summary>
    public bool HasDuration => !IsAllDay && DurationMinutes is > 0;

    /// <summary>"HH:mm" of <see cref="MomentLocal"/> plus <see cref="DurationMinutes"/> — empty when
    /// <see cref="HasDuration"/> is false.</summary>
    public string EndTimeLabel => HasDuration
        ? MomentLocal.AddMinutes(DurationMinutes!.Value).ToString("HH:mm", CultureInfo.CurrentCulture)
        : string.Empty;
    public string DueDateLabel => KanbanCard.FormatDueLabel(MomentUtc);
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasParentContext => !string.IsNullOrWhiteSpace(ParentContextLabel);

    public string SourceLabel => SourceType switch
    {
        CalendarItemSourceType.Kanban => LocalizationService.Instance["Calendar_Label_Kanban"],
        CalendarItemSourceType.PolarTask => LocalizationService.Instance["Calendar_Source_PolarTask"],
        CalendarItemSourceType.PolarSubtask => LocalizationService.Instance["Calendar_Source_PolarSubtask"],
        CalendarItemSourceType.Reminder => LocalizationService.Instance["Calendar_Label_Reminder"],
        _ => string.Empty
    };
}
