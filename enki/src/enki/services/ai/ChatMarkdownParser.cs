using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace enki.Services;

public enum ChatBlockKind
{
    Paragraph,
    Heading,
    ListItem,
    Quote,
    Code,
    Table,
    Rule
}

/// <summary>Un bloque de una respuesta del asistente. <see cref="Text"/> es Markdown en línea (negrita,
/// cursiva, código, enlaces) salvo en los bloques de código, donde es literal.</summary>
public sealed record ChatBlock(ChatBlockKind Kind, string Text)
{
    /// <summary>Nivel del título (1–6) o sangría de un elemento de lista (0, 1, 2…).</summary>
    public int Level { get; init; }

    /// <summary>Viñeta de un elemento de lista: <c>•</c> o el número con su punto.</summary>
    public string? Marker { get; init; }

    /// <summary>Casilla de una lista de tareas: <see langword="null"/> si no lo es.</summary>
    public bool? IsChecked { get; init; }

    /// <summary>Lenguaje anunciado en la apertura del bloque de código.</summary>
    public string? Language { get; init; }

    /// <summary>Filas de la tabla, cabecera incluida en la primera.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = [];
}

public enum ChatInlineStyle
{
    Plain,
    Bold,
    Italic,
    BoldItalic,
    Code,
    Link
}

public sealed record ChatInline(string Text, ChatInlineStyle Style, string? Url = null);

/// <summary>Subconjunto de Markdown suficiente para leer cómodamente las respuestas del asistente dentro de
/// la burbuja del chat — no es el renderizador del editor (<c>views/editor/Markdown</c>), que trabaja sobre
/// un documento editable y es demasiado pesado para pintar decenas de mensajes. Tolera Markdown a medio
/// escribir: durante el streaming un bloque de código sin cerrar se pinta igualmente como código.</summary>
public static partial class ChatMarkdownParser
{
    public static List<ChatBlock> ParseBlocks(string markdown)
    {
        var blocks = new List<ChatBlock>();
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new ChatBlock(ChatBlockKind.Paragraph, string.Join('\n', paragraph)));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (FenceRegex().Match(trimmed) is { Success: true } fence)
            {
                FlushParagraph();
                var marker = fence.Groups["fence"].Value;
                var code = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith(marker, StringComparison.Ordinal); i++)
                    code.Append(lines[i]).Append('\n');
                var language = fence.Groups["lang"].Value.Trim();
                blocks.Add(new ChatBlock(ChatBlockKind.Code, code.ToString().TrimEnd('\n'))
                    { Language = language.Length == 0 ? null : language });
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (HeadingRegex().Match(trimmed) is { Success: true } heading)
            {
                FlushParagraph();
                blocks.Add(new ChatBlock(ChatBlockKind.Heading, heading.Groups["text"].Value.TrimEnd('#', ' '))
                    { Level = heading.Groups["hashes"].Length });
                continue;
            }

            if (RuleRegex().IsMatch(trimmed))
            {
                FlushParagraph();
                blocks.Add(new ChatBlock(ChatBlockKind.Rule, string.Empty));
                continue;
            }

            if (trimmed.StartsWith('|') && i + 1 < lines.Length && TableSeparatorRegex().IsMatch(lines[i + 1].Trim()))
            {
                FlushParagraph();
                var rows = new List<IReadOnlyList<string>> { SplitTableRow(trimmed) };
                for (i += 2; i < lines.Length && lines[i].Trim().StartsWith('|'); i++) rows.Add(SplitTableRow(lines[i].Trim()));
                i--;
                var columns = rows.Max(r => r.Count);
                blocks.Add(new ChatBlock(ChatBlockKind.Table, string.Empty)
                {
                    Rows = rows.Select(r => (IReadOnlyList<string>)r.Concat(Enumerable.Repeat(string.Empty, columns - r.Count)).ToList()).ToList()
                });
                continue;
            }

            if (ListItemRegex().Match(line) is { Success: true } item)
            {
                FlushParagraph();
                var indent = item.Groups["indent"].Value.Replace("\t", "    ", StringComparison.Ordinal).Length / 2;
                var bullet = item.Groups["bullet"].Value;
                var text = item.Groups["text"].Value;
                bool? isChecked = null;
                if (TaskRegex().Match(text) is { Success: true } task)
                {
                    isChecked = task.Groups["state"].Value is "x" or "X";
                    text = task.Groups["text"].Value;
                }
                blocks.Add(new ChatBlock(ChatBlockKind.ListItem, text)
                {
                    Level = Math.Min(indent, 4),
                    Marker = char.IsDigit(bullet[0]) ? bullet.TrimEnd(')', '.') + "." : "•",
                    IsChecked = isChecked
                });
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quote = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('>'); i++)
                    quote.Add(lines[i].Trim().TrimStart('>').TrimStart());
                i--;
                blocks.Add(new ChatBlock(ChatBlockKind.Quote, string.Join('\n', quote)));
                continue;
            }

            paragraph.Add(trimmed);
        }

        FlushParagraph();
        return blocks;
    }

    /// <summary>Divide Markdown en línea en tramos con estilo. Lo que no es una marca reconocida —o una marca
    /// sin cerrar mientras se escribe— se queda como texto plano.</summary>
    public static List<ChatInline> ParseInlines(string text)
    {
        var result = new List<ChatInline>();
        var position = 0;
        foreach (Match match in InlineRegex().Matches(text))
        {
            if (match.Index > position) result.Add(new ChatInline(text[position..match.Index], ChatInlineStyle.Plain));
            if (match.Groups["code"].Success) result.Add(new ChatInline(match.Groups["code"].Value, ChatInlineStyle.Code));
            else if (match.Groups["bolditalic"].Success) result.Add(new ChatInline(match.Groups["bolditalic"].Value, ChatInlineStyle.BoldItalic));
            else if (match.Groups["bold"].Success) result.Add(new ChatInline(match.Groups["bold"].Value, ChatInlineStyle.Bold));
            else if (match.Groups["bold2"].Success) result.Add(new ChatInline(match.Groups["bold2"].Value, ChatInlineStyle.Bold));
            else if (match.Groups["italic"].Success) result.Add(new ChatInline(match.Groups["italic"].Value, ChatInlineStyle.Italic));
            else if (match.Groups["italic2"].Success) result.Add(new ChatInline(match.Groups["italic2"].Value, ChatInlineStyle.Italic));
            else if (match.Groups["linktext"].Success)
                result.Add(new ChatInline(match.Groups["linktext"].Value, ChatInlineStyle.Link, match.Groups["url"].Value));
            position = match.Index + match.Length;
        }
        if (position < text.Length) result.Add(new ChatInline(text[position..], ChatInlineStyle.Plain));
        return result;
    }

    private static List<string> SplitTableRow(string row)
    {
        var inner = row.Trim();
        if (inner.StartsWith('|')) inner = inner[1..];
        if (inner.EndsWith('|')) inner = inner[..^1];
        return inner.Split('|').Select(cell => cell.Trim()).ToList();
    }

    [GeneratedRegex(@"^(?<fence>`{3,}|~{3,})(?<lang>[^`]*)$")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^(?<hashes>#{1,6})\s+(?<text>.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(?:-{3,}|\*{3,}|_{3,})$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableSeparatorRegex();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<bullet>[-*+]|\d{1,3}[.)])\s+(?<text>.*)$")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"^\[(?<state>[ xX])\]\s+(?<text>.*)$")]
    private static partial Regex TaskRegex();

    [GeneratedRegex(@"`(?<code>[^`\n]+)`|\*\*\*(?<bolditalic>[^*\n]+)\*\*\*|\*\*(?<bold>[^*\n]+)\*\*|__(?<bold2>[^_\n]+)__|(?<![\w*])\*(?<italic>[^*\s][^*\n]*?)\*(?![\w*])|(?<![\w_])_(?<italic2>[^_\s][^_\n]*?)_(?![\w_])|\[(?<linktext>[^\]\n]+)\]\((?<url>[^)\s]+)\)")]
    private static partial Regex InlineRegex();
}
