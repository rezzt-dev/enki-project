using System.Collections.Generic;

namespace enki.Models;

/// <summary>Root object persisted to <c>%AppData%/Enki/polar-tasks.json</c> by
/// <c>PolarTaskService</c> — same single-file-per-domain pattern as <see cref="KanbanBoard"/>.</summary>
public sealed class PolarTaskStore
{
    public List<PolarTaskList> Lists { get; set; } = [];
    public List<PolarTask> Tasks { get; set; } = [];
    public List<PolarSubtask> Subtasks { get; set; } = [];
}
