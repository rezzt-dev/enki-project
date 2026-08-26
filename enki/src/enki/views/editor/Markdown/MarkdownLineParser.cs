using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// Lightweight, line-oriented Markdown parser used to drive live-preview rendering.
/// It is intentionally not a full CommonMark implementation: it recognizes the
/// constructs Obsidian's live preview renders specially (headings, emphasis,
/// lists/tasks, blockquotes, inline code, links, highlights, rules, fences)
/// and is designed to be cheap enough to re-run per visible line on every redraw.
/// </summary>
public static class MarkdownLineParser
{
    private static readonly Regex HorizontalRuleRegex = new(@"^([-*_])[ \t]*(\1[ \t]*){2,}$", RegexOptions.Compiled);

    /// <summary>Scans the whole document text once to determine which lines sit inside/around fenced code blocks.</summary>
    public static Dictionary<int, FenceInfo> ComputeFences(string documentText)
    {
        var result = new Dictionary<int, FenceInfo>();
        if (string.IsNullOrEmpty(documentText)) return result;

        var lines = documentText.Split('\n');
        var inFence = false;
        string? fenceChar = null;
        string? language = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var raw = lines[i];
            var trimmed = raw.TrimStart(' ', '\t');
            var isFenceLine = trimmed.StartsWith("```", StringComparison.Ordinal) ||
                               trimmed.StartsWith("~~~", StringComparison.Ordinal);

            if (isFenceLine)
            {
                var thisChar = trimmed[..3];
                if (!inFence)
                {
                    inFence = true;
                    fenceChar = thisChar;
                    language = trimmed.TrimStart('`', '~').Trim();
                    if (language.Length == 0) language = null;
                    result[lineNumber] = new FenceInfo(isDelimiter: true, isInside: false, language);
                }
                else if (thisChar == fenceChar)
                {
                    inFence = false;
                    result[lineNumber] = new FenceInfo(isDelimiter: true, isInside: false, language);
                    fenceChar = null;
                    language = null;
                }
                else
                {
                    result[lineNumber] = new FenceInfo(isDelimiter: false, isInside: true, language);
                }
            }
            else if (inFence)
            {
                result[lineNumber] = new FenceInfo(isDelimiter: false, isInside: true, language);
            }
        }

        return result;
    }

    public static MdLine Parse(string text, int lineOffset, FenceInfo fence)
    {
        var model = new MdLine();
        var n = text.Length;

        if (fence.IsDelimiter)
        {
            model.Kind = MdBlockKind.CodeFence;
            model.FenceLanguage = fence.Language;
            model.MarkerStart = lineOffset;
            model.MarkerEnd = lineOffset + n;
            model.ContentStart = model.MarkerEnd;
            return model;
        }

        if (fence.IsInside)
        {
            model.Kind = MdBlockKind.CodeFence;
            model.FenceLanguage = fence.Language;
            model.MarkerStart = lineOffset;
            model.MarkerEnd = lineOffset;
            model.ContentStart = lineOffset;
            return model;
        }

        var indent = 0;
        while (indent < n && (text[indent] == ' ' || text[indent] == '\t')) indent++;

        // Heading: up to 3 leading spaces, 1-6 '#' followed by whitespace.
        if (indent <= 3)
        {
            var h = 0;
            var j = indent;
            while (j < n && text[j] == '#' && h < 6) { h++; j++; }
            if (h is >= 1 and <= 6 && (j >= n || text[j] == ' ' || text[j] == '\t'))
            {
                // El marcador es '#' más el primer espacio obligatorio; los espacios extra se
                // conservan como indentación visible, igual que en Obsidian.
                var markerEnd = j;
                if (markerEnd < n && (text[markerEnd] == ' ' || text[markerEnd] == '\t')) markerEnd++;
                var contentStart = markerEnd;
                while (contentStart < n && (text[contentStart] == ' ' || text[contentStart] == '\t')) contentStart++;
                model.Kind = MdBlockKind.Heading;
                model.HeadingLevel = h;
                model.MarkerStart = lineOffset + indent;
                model.MarkerEnd = lineOffset + markerEnd;
                model.ContentStart = lineOffset + contentStart;
                ScanInline(model, text, contentStart, n, lineOffset);
                return model;
            }
        }

        // Horizontal rule: ---, ***, ___ (optionally spaced), nothing else on the line.
        if (indent <= 3 && n - indent >= 3 && HorizontalRuleRegex.IsMatch(text[indent..]))
        {
            model.Kind = MdBlockKind.HorizontalRule;
            model.MarkerStart = lineOffset + indent;
            model.MarkerEnd = lineOffset + n;
            model.ContentStart = model.MarkerEnd;
            return model;
        }

        // Blockquote.
        if (indent <= 3 && indent < n && text[indent] == '>')
        {
            var j = indent + 1;
            if (j < n && text[j] == ' ') j++;
            model.Kind = MdBlockKind.Blockquote;
            model.MarkerStart = lineOffset + indent;
            model.MarkerEnd = lineOffset + j;
            model.ContentStart = lineOffset + j;
            ScanInline(model, text, j, n, lineOffset);
            return model;
        }

        // Bullet / task list. No indent cap (unlike headings/rules/blockquotes above, which keep the
        // CommonMark-ish 3-space limit): nested list items are routinely indented 4+ spaces per level,
        // and this parser has no indented-code-block construct to conflict with, so any indentation
        // still counts as a list marker.
        if (indent < n && (text[indent] == '-' || text[indent] == '*' || text[indent] == '+') &&
            indent + 1 < n && (text[indent + 1] == ' ' || text[indent + 1] == '\t'))
        {
            var spacesEnd = indent + 1;
            while (spacesEnd < n && (text[spacesEnd] == ' ' || text[spacesEnd] == '\t')) spacesEnd++;

            // Marcador = viñeta + el primer espacio obligatorio; el resto se conserva visible.
            var markerEnd = indent + 2;
            var j = spacesEnd;

            if (j + 2 < n && text[j] == '[' && (text[j + 1] == ' ' || text[j + 1] == 'x' || text[j + 1] == 'X') && text[j + 2] == ']')
            {
                var after = j + 3;
                model.Kind = MdBlockKind.TaskList;
                model.TaskChecked = text[j + 1] is 'x' or 'X';
                model.CheckboxCharOffset = lineOffset + j + 1;
                if (after < n && (text[after] == ' ' || text[after] == '\t')) after++;
                model.MarkerStart = lineOffset + indent;
                model.MarkerEnd = lineOffset + after;
                var contentStart = after;
                while (contentStart < n && (text[contentStart] == ' ' || text[contentStart] == '\t')) contentStart++;
                model.ContentStart = lineOffset + contentStart;
                ScanInline(model, text, contentStart, n, lineOffset);
                return model;
            }

            model.Kind = MdBlockKind.BulletList;
            model.MarkerStart = lineOffset + indent;
            var contentStartBullet = markerEnd;
            while (contentStartBullet < n && (text[contentStartBullet] == ' ' || text[contentStartBullet] == '\t')) contentStartBullet++;
            // A diferencia de encabezados/citas, aquí sí se traga todo el espacio extra tras el
            // marcador: la viñeta renderizada ("•  ") ya reserva un ancho fijo de 2 espacios, así
            // que dejar visibles los espacios originales del usuario duplicaría la separación.
            model.MarkerEnd = lineOffset + contentStartBullet;
            model.ContentStart = lineOffset + contentStartBullet;
            ScanInline(model, text, contentStartBullet, n, lineOffset);
            return model;
        }

        // Ordered list. Same no-cap reasoning as the bullet/task branch above.
        var jOrdered = indent;
        while (jOrdered < n && char.IsDigit(text[jOrdered])) jOrdered++;
        if (jOrdered > indent && jOrdered - indent <= 9 && jOrdered < n && (text[jOrdered] == '.' || text[jOrdered] == ')') &&
            jOrdered + 1 < n && (text[jOrdered + 1] == ' ' || text[jOrdered + 1] == '\t'))
        {
            // Marcador = número + puntuación + el primer espacio obligatorio.
            var markerEnd = jOrdered + 2;
            var contentStart = markerEnd;
            while (contentStart < n && (text[contentStart] == ' ' || text[contentStart] == '\t')) contentStart++;
            model.Kind = MdBlockKind.OrderedList;
            model.MarkerStart = lineOffset + indent;
            model.MarkerEnd = lineOffset + markerEnd;
            model.ContentStart = lineOffset + contentStart;
            ScanInline(model, text, contentStart, n, lineOffset);
            return model;
        }

        model.Kind = MdBlockKind.Paragraph;
        model.MarkerStart = lineOffset;
        model.MarkerEnd = lineOffset;
        model.ContentStart = lineOffset;
        ScanInline(model, text, 0, n, lineOffset);
        return model;
    }

    private static void ScanInline(MdLine model, string text, int from, int to, int lineOffset)
    {
        var i = from;
        while (i < to)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < to)
            {
                i += 2;
                continue;
            }

            if (c == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close != -1 && close < to && close > i + 1)
                {
                    model.Spans.Add(new MdSpan
                    {
                        Kind = MdSpanKind.InlineCode,
                        Start = lineOffset + i,
                        End = lineOffset + close + 1,
                        ContentStart = lineOffset + i + 1,
                        ContentEnd = lineOffset + close
                    });
                    i = close + 1;
                    continue;
                }
            }
            else if (c == '*' || c == '_')
            {
                var markerLen = 1;
                if (i + 2 < to && text[i + 1] == c && text[i + 2] == c) markerLen = 3;
                else if (i + 1 < to && text[i + 1] == c) markerLen = 2;

                // Underscore emphasis never opens between two word characters (CommonMark's intraword
                // rule) so identifiers like snake_case_name stay plain text instead of turning
                // "case" italic — asterisks keep opening intraword since *that* markup has no such
                // legitimate literal use to protect.
                var opensIntraword = c == '_' && i > 0 && char.IsLetterOrDigit(text[i - 1]);

                if (!opensIntraword && i + markerLen < to && !char.IsWhiteSpace(text[i + markerLen]))
                {
                    var marker = new string(c, markerLen);
                    var searchFrom = i + markerLen;
                    var close = FindClosingMarker(text, searchFrom, to, marker);
                    // Same intraword rule on the closing side: an underscore run immediately followed by
                    // another word character can't close emphasis either.
                    if (close != -1 && c == '_' && close + markerLen < to && char.IsLetterOrDigit(text[close + markerLen]))
                        close = -1;
                    if (close != -1)
                    {
                        var kind = markerLen == 3 ? MdSpanKind.BoldItalic : markerLen == 2 ? MdSpanKind.Bold : MdSpanKind.Italic;
                        model.Spans.Add(new MdSpan
                        {
                            Kind = kind,
                            Start = lineOffset + i,
                            End = lineOffset + close + markerLen,
                            ContentStart = lineOffset + searchFrom,
                            ContentEnd = lineOffset + close
                        });
                        i = close + markerLen;
                        continue;
                    }
                }
            }
            else if (c == '~' && i + 1 < to && text[i + 1] == '~')
            {
                var close = FindClosingMarker(text, i + 2, to, "~~");
                if (close != -1)
                {
                    model.Spans.Add(new MdSpan
                    {
                        Kind = MdSpanKind.Strikethrough,
                        Start = lineOffset + i,
                        End = lineOffset + close + 2,
                        ContentStart = lineOffset + i + 2,
                        ContentEnd = lineOffset + close
                    });
                    i = close + 2;
                    continue;
                }
            }
            else if (c == '=' && i + 1 < to && text[i + 1] == '=')
            {
                var close = FindClosingMarker(text, i + 2, to, "==");
                if (close != -1)
                {
                    model.Spans.Add(new MdSpan
                    {
                        Kind = MdSpanKind.Highlight,
                        Start = lineOffset + i,
                        End = lineOffset + close + 2,
                        ContentStart = lineOffset + i + 2,
                        ContentEnd = lineOffset + close
                    });
                    i = close + 2;
                    continue;
                }
            }
            else if (c == '[' && i + 1 < to && text[i + 1] == '[')
            {
                // Wikilink: [[Target]], [[Target|Alias]] or [[Target#Heading]] — checked before the plain
                // `[text](url)` branch below since that one would otherwise treat the first `[` of a
                // wikilink as the start of a (never-matching, since there's no following "](") plain link
                // and just skip past it doing nothing, which is exactly the "wikilinks muertos" gap this
                // closes (see docs/analisis-proyecto/roadmap.md, Fase 5).
                var innerStart = i + 2;
                var close = text.IndexOf("]]", innerStart, to - innerStart, StringComparison.Ordinal);
                if (close != -1)
                {
                    var inner = text[innerStart..close];
                    var pipeIdx = inner.IndexOf('|');
                    var target = (pipeIdx >= 0 ? inner[..pipeIdx] : inner.Split('#')[0]).Trim();

                    if (target.Length > 0)
                    {
                        // An alias (`|Alias`) hides the raw target and shows only the alias, matching
                        // Obsidian's live preview; a heading anchor (`#Heading`) or a bare target has
                        // nothing to hide beyond the brackets themselves.
                        var contentStart = pipeIdx >= 0 ? innerStart + pipeIdx + 1 : innerStart;
                        model.Spans.Add(new MdSpan
                        {
                            Kind = MdSpanKind.WikiLink,
                            Start = lineOffset + i,
                            End = lineOffset + close + 2,
                            ContentStart = lineOffset + contentStart,
                            ContentEnd = lineOffset + close,
                            LinkTarget = target
                        });
                        i = close + 2;
                        continue;
                    }
                }
            }
            else if (c == '[')
            {
                var closeBracket = text.IndexOf(']', i + 1);
                if (closeBracket != -1 && closeBracket < to && closeBracket > i + 1 &&
                    closeBracket + 1 < to && text[closeBracket + 1] == '(')
                {
                    var closeParen = text.IndexOf(')', closeBracket + 2);
                    if (closeParen != -1 && closeParen < to)
                    {
                        var rawUrl = text[(closeBracket + 2)..closeParen];
                        var spaceIdx = rawUrl.IndexOf(' ');
                        var url = spaceIdx >= 0 ? rawUrl[..spaceIdx] : rawUrl;
                        model.Spans.Add(new MdSpan
                        {
                            Kind = MdSpanKind.Link,
                            Start = lineOffset + i,
                            End = lineOffset + closeParen + 1,
                            ContentStart = lineOffset + i + 1,
                            ContentEnd = lineOffset + closeBracket,
                            LinkTarget = url
                        });
                        i = closeParen + 1;
                        continue;
                    }
                }
            }

            i++;
        }
    }

    private static int FindClosingMarker(string text, int from, int to, string marker)
    {
        var idx = from;
        while (idx < to)
        {
            var found = text.IndexOf(marker, idx, to - idx, StringComparison.Ordinal);
            if (found == -1 || found >= to) return -1;
            if (found > from && text[found - 1] != '\\' && !char.IsWhiteSpace(text[found - 1]))
                return found;
            idx = found + 1;
        }
        return -1;
    }

}
