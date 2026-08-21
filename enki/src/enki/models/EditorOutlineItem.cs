namespace enki.Models;

/// <summary>Una entrada del índice/outline de una nota Markdown.</summary>
public sealed class EditorOutlineItem
{
    public required string Title { get; init; }
    public required int Level { get; init; }
    public required int Offset { get; init; }
    public required int LineNumber { get; init; }
}
