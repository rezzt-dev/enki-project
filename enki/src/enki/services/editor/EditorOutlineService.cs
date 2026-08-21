using System;
using System.Collections.Generic;
using enki.Models;

namespace enki.Services;

/// <summary>Extrae el índice de encabezados de una nota Markdown.</summary>
public static class EditorOutlineService
{
    public static IReadOnlyList<EditorOutlineItem> Extract(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return Array.Empty<EditorOutlineItem>();

        var result = new List<EditorOutlineItem>();
        var lineNumber = 1;
        var offset = 0;

        // Los encabezados atx deben cumplir CommonMark: hasta 3 espacios iniciales,
        // 1-6 '#' seguidos de espacio/tab o fin de línea, y nada después salvo espacios.
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.AsSpan();
            var trimmedStart = line.TrimStart(" \t".AsSpan());
            var leadingSpaces = line.Length - trimmedStart.Length;
            if (leadingSpaces > 3)
            {
                offset += raw.Length + 1;
                lineNumber++;
                continue;
            }

            var hashCount = 0;
            while (hashCount < trimmedStart.Length && hashCount < 6 && trimmedStart[hashCount] == '#')
                hashCount++;

            if (hashCount is >= 1 and <= 6 &&
                (hashCount >= trimmedStart.Length || char.IsWhiteSpace(trimmedStart[hashCount])))
            {
                var afterHashes = trimmedStart.Slice(hashCount);
                var content = afterHashes.TrimStart().TrimEnd(" \t#".AsSpan());
                if (content.Length > 0)
                {
                    var contentStart = offset + leadingSpaces + hashCount + (afterHashes.Length - content.Length);
                    result.Add(new EditorOutlineItem
                    {
                        Title = content.ToString(),
                        Level = hashCount,
                        Offset = contentStart,
                        LineNumber = lineNumber
                    });
                }
            }

            offset += raw.Length + 1;
            lineNumber++;
        }

        return result;
    }
}
