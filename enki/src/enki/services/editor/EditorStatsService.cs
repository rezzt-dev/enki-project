using System;
using System.IO;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>Calcula estadísticas de lectura y metadatos de una nota Markdown.</summary>
public static class EditorStatsService
{
    private const int WordsPerMinute = 200;

    public static EditorStats Compute(string markdown, string fullPath)
    {
        var text = NoteTagHelper.StripFrontMatter(markdown);

        var lines = text.Split('\n');
        var nonEmptyLines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var paragraphs = nonEmptyLines.Count(l => !l.TrimStart().StartsWith('#'));

        var words = CountWords(text);
        var chars = text.Length;
        var charsNoSpaces = text.Count(c => !char.IsWhiteSpace(c));

        DateTime createdUtc = DateTime.MinValue;
        DateTime modifiedUtc = DateTime.MinValue;
        try
        {
            var info = new FileInfo(fullPath);
            if (info.Exists)
            {
                createdUtc = info.CreationTimeUtc;
                modifiedUtc = info.LastWriteTimeUtc;
            }
        }
        catch
        {
            // Si la ruta no es válida o no existe, usamos DateTime.MinValue.
        }

        return new EditorStats
        {
            WordCount = words,
            CharCount = chars,
            CharCountNoSpaces = charsNoSpaces,
            ParagraphCount = paragraphs,
            LineCount = lines.Length,
            ReadingTimeMinutes = Math.Max(1, (int)Math.Ceiling(words / (double)WordsPerMinute)),
            CreatedUtc = createdUtc,
            ModifiedUtc = modifiedUtc
        };
    }

    /// <summary>Cuenta palabras delimitadas por espacios como antes, salvo en scripts CJK (chino/japonés/
    /// coreano) — que no usan espacios entre palabras — donde cada carácter cuenta como su propia
    /// "palabra" en vez de que un párrafo entero se cuente como una sola (análisis §4.2, Fase 8: sin este
    /// tratamiento, el tiempo de lectura de una nota en CJK salía completamente erróneo).</summary>
    private static int CountWords(string text)
    {
        var span = text.AsSpan();
        var count = 0;
        var inWord = false;
        foreach (var c in span)
        {
            if (IsCjkCharacter(c))
            {
                count++;
                inWord = false;
            }
            else if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }
        return count;
    }

    private static bool IsCjkCharacter(char c) =>
        (c >= '぀' && c <= 'ヿ')   // Hiragana, Katakana
        || (c >= '㐀' && c <= '䶿') // CJK Unified Ideographs Extension A
        || (c >= '一' && c <= '鿿') // CJK Unified Ideographs
        || (c >= '豈' && c <= '﫿') // CJK Compatibility Ideographs
        || (c >= '가' && c <= '힣'); // Hangul Syllables
}
