namespace enki.Models;

/// <summary>Una nota que enlaza a la nota activa mediante un wikilink.</summary>
public sealed class EditorBacklink
{
    public required string FullPath { get; init; }
    public required string Title { get; init; }
    public required string Context { get; init; }
}
