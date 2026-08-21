namespace enki.Models;

public sealed class KanbanSubtask
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public bool IsDone { get; set; }
}
