using System;

namespace enki.Models;

/// <summary>Estadísticas de lectura y tamaño de una nota Markdown.</summary>
public sealed class EditorStats
{
    public int WordCount { get; init; }
    public int CharCount { get; init; }
    public int CharCountNoSpaces { get; init; }
    public int ParagraphCount { get; init; }
    public int LineCount { get; init; }
    public int ReadingTimeMinutes { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime ModifiedUtc { get; init; }
}
