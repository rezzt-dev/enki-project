namespace enki.Models;

/// <summary>Una fila del Gestor de etiquetas: el conteo de uso de una etiqueta a través de los tres
/// dominios que la comparten (Notas, Kanban, Polar), agrupada de forma insensible a mayúsculas — ver
/// <see cref="Services.TagManagerService.BuildEntries"/>. No es un segundo almacén de etiquetas: se
/// reconstruye desde cero en cada lectura a partir de <see cref="NoteFile.Tags"/>/<see cref="KanbanCard.Tags"/>/
/// <see cref="PolarTask.Tags"/>, igual que <c>Services.StatisticsAggregationService.Report.BuildTopTags</c>.</summary>
public sealed class TagManagerEntry
{
    public required string Name { get; init; }
    public int NoteCount { get; init; }
    public int KanbanCount { get; init; }
    public int PolarCount { get; init; }
    public int TotalCount => NoteCount + KanbanCount + PolarCount;
}
