using System;
using System.Collections.Generic;
using System.Linq;

namespace enki.Services;

/// <summary>Parser/escritor manual del frontmatter YAML de etiquetas en notas Markdown. No depende de
/// bibliotecas externas: solo reconoce el bloque <c>---...---</c> inicial y la clave <c>tags</c>.</summary>
public static class NoteTagHelper
{
    /// <summary>Extrae la lista de etiquetas del frontmatter inicial de una nota. Si no hay frontmatter,
    /// si no se cierra, o no contiene una clave <c>tags</c>, devuelve una lista vacía.</summary>
    public static List<string> ExtractTags(string content)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(content)) return result;

        var normalized = NormalizeLineEndings(content);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return result;

        var close = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (close < 0) return result;

        var frontMatter = normalized.Substring(4, close - 4);
        var lines = frontMatter.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!IsTagsKey(line)) continue;

            var value = line[(line.IndexOf(':') + 1)..].Trim();
            if (!string.IsNullOrEmpty(value))
            {
                // Forma inline: tags: [a, b, c]
                if (value.StartsWith('[') && value.EndsWith(']'))
                {
                    foreach (var item in value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        AddIfValid(result, item);
                    break;
                }
            }

            // Forma lista YAML: líneas que empiezan con "- item" hasta el final del frontmatter.
            for (var j = i + 1; j < lines.Length; j++)
            {
                var listLine = lines[j].TrimStart();
                if (!listLine.StartsWith("- ", StringComparison.Ordinal) && listLine != "-") break;
                var item = listLine.StartsWith("- ", StringComparison.Ordinal)
                    ? listLine[2..].Trim()
                    : string.Empty;
                AddIfValid(result, item);
            }

            break;
        }

        return result;
    }

    /// <summary>Devuelve una copia del contenido con el frontmatter de etiquetas actualizado. Si
    /// <paramref name="tags"/> está vacío, quita solo la clave <c>tags</c> (y su lista YAML, si la tenía en
    /// forma de lista) conservando el resto de claves del frontmatter (<c>aliases</c>, <c>created</c>, ...);
    /// solo si no queda ninguna otra clave se retira el bloque <c>---...---</c> por completo. Antes de la
    /// Fase 5 (docs/analisis-proyecto/roadmap.md) esto borraba siempre el frontmatter entero al vaciar las
    /// etiquetas, perdiendo cualquier otra clave que hubiera. Si no hay frontmatter, lo inserta al inicio
    /// del documento.</summary>
    public static string SetTags(string content, IReadOnlyList<string> tags)
    {
        var normalized = NormalizeLineEndings(content ?? string.Empty);
        var cleanTags = NormalizeTags(tags);
        var existingFrontMatter = ExtractFrontMatter(normalized);

        if (cleanTags.Count == 0)
            return existingFrontMatter is null ? normalized : RemoveTagsKey(normalized, existingFrontMatter);

        var tagLine = "tags: [" + string.Join(", ", cleanTags) + "]";

        if (existingFrontMatter is null)
            return "---\n" + tagLine + "\n---\n\n" + normalized;

        var lines = existingFrontMatter.Split('\n').ToList();
        var index = FindTagsKeyLineIndex(lines);

        if (index >= 0)
            ReplaceTagsKeyLines(lines, index, tagLine);
        else
            lines.Insert(1, tagLine); // justo debajo del --- de apertura.

        var newFrontMatter = string.Join('\n', lines);
        var rest = StripFrontMatter(normalized);
        return "---\n" + newFrontMatter + "\n---\n\n" + rest;
    }

    /// <summary>Removes just the <c>tags</c> key (and any YAML-list continuation lines under it) from
    /// <paramref name="frontMatter"/>, keeping every other key intact. Falls back to stripping the whole
    /// frontmatter block only when nothing else is left in it.</summary>
    private static string RemoveTagsKey(string normalized, string frontMatter)
    {
        var lines = frontMatter.Split('\n').ToList();
        var index = FindTagsKeyLineIndex(lines);
        if (index < 0) return normalized; // sin clave tags que quitar: el frontmatter no cambia.

        RemoveTagsKeyLines(lines, index);

        var rest = StripFrontMatter(normalized);
        return lines.Any(l => !string.IsNullOrWhiteSpace(l))
            ? "---\n" + string.Join('\n', lines) + "\n---\n\n" + rest
            : rest;
    }

    private static void ReplaceTagsKeyLines(List<string> lines, int index, string tagLine)
    {
        RemoveTagsKeyLines(lines, index);
        lines.Insert(index, tagLine);
    }

    /// <summary>Removes the <c>tags:</c> line at <paramref name="index"/> plus, if it introduced a YAML
    /// list (no inline <c>[...]</c> value), every following <c>"- item"</c> continuation line — otherwise
    /// those lines would survive as orphaned, key-less scalars once the <c>tags:</c> line above them is
    /// gone or replaced.</summary>
    private static void RemoveTagsKeyLines(List<string> lines, int index)
    {
        var end = index + 1;
        while (end < lines.Count && IsListContinuation(lines[end])) end++;
        lines.RemoveRange(index, end - index);
    }

    private static bool IsListContinuation(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-";
    }

    private static int FindTagsKeyLineIndex(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsTagsKey(lines[i])) return i;
        }
        return -1;
    }

    /// <summary>Devuelve el contenido sin el bloque <c>---...---</c> inicial ni la línea en blanco
    /// que le sigue. Si no hay frontmatter, devuelve el contenido original.</summary>
    public static string StripFrontMatter(string content)
    {
        var normalized = NormalizeLineEndings(content ?? string.Empty);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return normalized;

        var close = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (close < 0) return normalized;

        var afterClose = close + 4;
        if (afterClose < normalized.Length && normalized[afterClose] == '\n') afterClose++;

        return normalized[afterClose..].TrimStart();
    }

    private static string? ExtractFrontMatter(string normalized)
    {
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return null;

        var close = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (close < 0) return null;

        return normalized.Substring(4, close - 4);
    }

    private static bool IsTagsKey(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0) return false;
        var key = line[..colon].Trim();
        return string.Equals(key, "tags", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddIfValid(List<string> result, string raw)
    {
        var item = raw.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(item)) return;
        if (!result.Contains(item, StringComparer.OrdinalIgnoreCase)) result.Add(item);
    }

    private static List<string> NormalizeTags(IReadOnlyList<string>? tags)
    {
        var result = new List<string>();
        if (tags is null) return result;
        foreach (var t in tags)
        {
            var clean = t?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(clean)) continue;
            if (!result.Contains(clean, StringComparer.Ordinal)) result.Add(clean);
        }
        return result;
    }

    private static string NormalizeLineEndings(string content) => content.Replace("\r\n", "\n").Replace('\r', '\n');
}
