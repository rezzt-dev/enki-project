using System;
using System.Collections.Generic;

namespace enki.Services;

public enum DiffLineKind { Unchanged, Added, Removed }

public sealed record DiffLine(DiffLineKind Kind, string Text);

/// <summary>Line-based diff between two versions of a note's Markdown — powers the history panel's compare
/// view (historial local de versiones). Deliberately line-granularity, not word/character: a Markdown note
/// is read and edited a paragraph at a time, and a character-level diff on prose produces a scattering of
/// tiny changes that's harder to scan than "this whole line changed."</summary>
public static class TextDiffService
{
    /// <summary>Classic dynamic-programming LCS (longest common subsequence) over lines, backtracked into a
    /// linear list of <see cref="DiffLine"/>s in original order — removed lines from <paramref name="oldText"/>
    /// first, then added lines from <paramref name="newText"/>, exactly where each divergence occurs, with
    /// every line the two versions share in between marked unchanged. Quadratic in line count, which is fine
    /// for a single note's history (thousands of lines at most) but would need a smarter algorithm (Myers)
    /// for anything larger.</summary>
    public static List<DiffLine> Compare(string oldText, string newText)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        var lcs = BuildLcsTable(oldLines, newLines);

        var result = new List<DiffLine>();
        Backtrack(lcs, oldLines, newLines, 0, 0, result);
        return result;
    }

    private static string[] SplitLines(string text) =>
        string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n").Split('\n');

    private static int[,] BuildLcsTable(string[] a, string[] b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        for (var j = b.Length - 1; j >= 0; j--)
        {
            table[i, j] = a[i] == b[j]
                ? table[i + 1, j + 1] + 1
                : Math.Max(table[i + 1, j], table[i, j + 1]);
        }
        return table;
    }

    private static void Backtrack(int[,] lcs, string[] a, string[] b, int i, int j, List<DiffLine> result)
    {
        while (true)
        {
            if (i < a.Length && j < b.Length && a[i] == b[j])
            {
                result.Add(new DiffLine(DiffLineKind.Unchanged, a[i]));
                i++; j++;
            }
            else if (j < b.Length && (i == a.Length || lcs[i, j + 1] >= lcs[i + 1, j]))
            {
                result.Add(new DiffLine(DiffLineKind.Added, b[j]));
                j++;
            }
            else if (i < a.Length)
            {
                result.Add(new DiffLine(DiffLineKind.Removed, a[i]));
                i++;
            }
            else
            {
                return;
            }
        }
    }
}
