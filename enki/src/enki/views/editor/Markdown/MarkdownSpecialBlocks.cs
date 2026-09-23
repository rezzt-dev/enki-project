using System;
using System.Collections.Generic;

namespace enki.Views.Editor.Markdown;

internal enum MarkdownSpecialBlockKind { Math, Mermaid }

internal sealed record MarkdownSpecialBlock(
    MarkdownSpecialBlockKind Kind,
    int StartLine,
    int EndLine,
    int StartOffset,
    int EndOffset,
    string Source);

/// <summary>Escanea construcciones que abarcan varias líneas y por tanto no caben en
/// <see cref="MarkdownLineParser"/>: matemáticas entre delimitadores <c>$$</c> y cercas Mermaid.</summary>
internal static class MarkdownSpecialBlocks
{
    public static IReadOnlyList<MarkdownSpecialBlock> Scan(string document,
        IReadOnlyDictionary<int, FenceInfo> fences)
    {
        if (string.IsNullOrEmpty(document)) return Array.Empty<MarkdownSpecialBlock>();

        var normalized = document.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var offsets = new int[lines.Length];
        var offset = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            offsets[i] = offset;
            offset += lines[i].Length + 1;
        }

        var blocks = new List<MarkdownSpecialBlock>();
        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            if (fences.TryGetValue(lineNumber, out var fence) && fence.IsDelimiter
                && string.Equals(fence.Language, "mermaid", StringComparison.OrdinalIgnoreCase))
            {
                var close = i + 1;
                while (close < lines.Length)
                {
                    if (fences.TryGetValue(close + 1, out var endFence) && endFence.IsDelimiter) break;
                    close++;
                }
                if (close < lines.Length)
                {
                    blocks.Add(new MarkdownSpecialBlock(MarkdownSpecialBlockKind.Mermaid,
                        lineNumber, close + 1, offsets[i], offsets[close] + lines[close].Length,
                        string.Join('\n', lines[(i + 1)..close])));
                    i = close;
                }
                continue;
            }

            if (fence.IsCode || !string.Equals(lines[i].Trim(), "$$", StringComparison.Ordinal)) continue;
            var mathClose = i + 1;
            while (mathClose < lines.Length && !string.Equals(lines[mathClose].Trim(), "$$", StringComparison.Ordinal))
                mathClose++;
            if (mathClose >= lines.Length) continue;

            blocks.Add(new MarkdownSpecialBlock(MarkdownSpecialBlockKind.Math,
                lineNumber, mathClose + 1, offsets[i], offsets[mathClose] + lines[mathClose].Length,
                string.Join('\n', lines[(i + 1)..mathClose])));
            i = mathClose;
        }
        return blocks;
    }
}
