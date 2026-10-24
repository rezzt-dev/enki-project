using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace enki.Services;

/// <summary>Lee propiedades del frontmatter de la nota sin almacenar una segunda copia persistente.
/// Las ediciones sustituyen solo la clave indicada y conservan el resto del YAML y del Markdown.</summary>
public static class NotePropertyHelper
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();
    private static readonly Regex KeyPattern = new("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.Compiled);

    public static Dictionary<string, string> Extract(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        content = (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        if (!TryFindBlock(content, out var start, out var end)) return result;
        try
        {
            var yaml = content[start..end];
            var map = Deserializer.Deserialize<Dictionary<object, object>>(yaml);
            if (map is null) return result;
            foreach (var (rawKey, rawValue) in map)
            {
                var key = Convert.ToString(rawKey, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(key) || key.Equals("tags", StringComparison.OrdinalIgnoreCase)) continue;
                result[key] = Format(rawValue);
            }
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // Una nota externa con YAML incompleto sigue abriéndose; el editor Markdown puede repararla.
        }
        return result;
    }

    public static string Set(string content, string key, string? value)
    {
        key = (key ?? string.Empty).Trim();
        if (!KeyPattern.IsMatch(key) || key.Equals("tags", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Nombre de propiedad no válido.", nameof(key));
        content ??= string.Empty;
        var withoutCrLf = content.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        var usesCrLf = content.Contains("\r\n", StringComparison.Ordinal) &&
                       !withoutCrLf.Contains('\n') && !withoutCrLf.Contains('\r');
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var hasBlock = TryFindBlock(normalized, out var start, out var end);
        if (!hasBlock)
            return value is null ? content : RestoreNewlines($"---\n{key}: {JsonSerializer.Serialize(value)}\n---\n\n{normalized}", usesCrLf);

        try { Deserializer.Deserialize<object>(normalized[start..end]); }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new FormatException("El frontmatter YAML debe repararse antes de editar propiedades.", ex);
        }
        var lines = normalized[start..end].Split('\n').ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!IsKeyLine(lines[i], key)) continue;
            var last = i + 1;
            while (last < lines.Count && !IsTopLevelKey(lines[last]) &&
                   (string.IsNullOrWhiteSpace(lines[last]) || char.IsWhiteSpace(lines[last][0]))) last++;
            lines.RemoveRange(i, last - i);
            if (value is not null) lines.Insert(i, $"{key}: {JsonSerializer.Serialize(value)}");
            return RestoreNewlines(Rebuild(normalized, start, end, lines), usesCrLf);
        }
        if (value is null) return content;
        lines.Add($"{key}: {JsonSerializer.Serialize(value)}");
        return RestoreNewlines(Rebuild(normalized, start, end, lines), usesCrLf);
    }

    private static string RestoreNewlines(string content, bool usesCrLf) =>
        usesCrLf ? content.Replace("\n", "\r\n", StringComparison.Ordinal) : content;

    private static string Rebuild(string content, int start, int end, List<string> lines)
    {
        var yaml = string.Join('\n', lines).Trim('\n');
        var suffix = content[(end + 4)..];
        if (!string.IsNullOrWhiteSpace(yaml)) return $"---\n{yaml}\n---{suffix}";
        // Al retirar la última clave se elimina solo el separador del bloque, no los saltos
        // adicionales que el usuario haya puesto al principio del cuerpo.
        if (suffix.StartsWith("\n\n", StringComparison.Ordinal)) return suffix[2..];
        if (suffix.StartsWith('\n')) return suffix[1..];
        return suffix;
    }

    private static bool TryFindBlock(string content, out int start, out int end)
    {
        start = 4;
        end = -1;
        if (!content.StartsWith("---\n", StringComparison.Ordinal)) return false;
        var lines = content.IndexOf("\n---", start, StringComparison.Ordinal);
        if (lines < 0 || (lines + 4 < content.Length && content[lines + 4] != '\n')) return false;
        end = lines;
        return true;
    }

    private static bool IsTopLevelKey(string line) => line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.Contains(':');
    private static bool IsKeyLine(string line, string key)
    {
        if (!IsTopLevelKey(line)) return false;
        return line[..line.IndexOf(':')].Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        DateTime date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IDictionary map => "{" + string.Join(", ", map.Keys.Cast<object>().Select(key => $"{Format(key)}: {Format(map[key])}")) + "}",
        IEnumerable sequence => string.Join(", ", sequence.Cast<object?>().Select(Format)),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };
}
