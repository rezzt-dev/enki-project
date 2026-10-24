using System;
using System.Collections.Generic;

namespace enki.Models;

public sealed class KanbanBoard
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string Name { get; set; }
    public List<KanbanColumn> Columns { get; set; } = [];
    public List<KanbanTrashedCard> TrashedCards { get; set; } = [];
}
