using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace enki.Views.Editor.Markdown;

/// <summary>Horizontal alignment declared by a block-level HTML tag — <c>&lt;div align="center"&gt;</c>,
/// <c>&lt;p style="text-align: justify"&gt;</c> or the legacy <c>&lt;center&gt;</c>.
/// <see cref="None"/> means the tag declared nothing and simply inherits whatever block encloses it.</summary>
public enum MdAlign
{
    None,
    Left,
    Center,
    Right,
    Justify
}

public enum HtmlTagKind
{
    Open,
    Close,
    SelfClosing
}

/// <summary>One recognized HTML tag inside a line, in absolute document offsets.
/// [<see cref="Start"/>, <see cref="End"/>) covers the whole tag including its angle brackets — that is
/// exactly the range the live preview conceals, the same way it conceals a Markdown marker.</summary>
public sealed class HtmlTagToken
{
    public required int Start { get; init; }
    public required int End { get; init; }
    /// <summary>Lower-cased tag name ("div", "b", "br"…).</summary>
    public required string Name { get; init; }
    public required HtmlTagKind Kind { get; init; }
    /// <summary>Alignment this tag declares (opening block tags only); <see cref="MdAlign.None"/> otherwise.</summary>
    public MdAlign Align { get; init; }
    /// <summary>Whether the tag is one of the block containers that can carry an alignment.</summary>
    public bool IsBlock { get; init; }
}

/// <summary>The HTML context one line renders under: the alignment of the block enclosing it, plus the
/// inline formatting tags a previous line opened and has not closed yet (in the order they were opened).
/// Both come from the document-wide scan in <see cref="MarkdownHtmlParser.ComputeLineStates"/>.</summary>
public sealed class HtmlLineState
{
    public static readonly HtmlLineState Empty = new(MdAlign.None, new List<string>());

    public HtmlLineState(MdAlign align, IReadOnlyList<string> openInlineTags)
    {
        Align = align;
        OpenInlineTags = openInlineTags;
    }

    public MdAlign Align { get; }
    public IReadOnlyList<string> OpenInlineTags { get; }
}

/// <summary>
/// The "simple HTML" half of the live preview: recognizes the small, fixed set of HTML tags a note may
/// legitimately contain (block containers used to align text, plus the basic inline formatting tags) so
/// the editor can conceal the tags themselves — like any other Markdown marker — and render what they
/// mean instead of printing <c>&lt;div align="center"&gt;</c> as literal text.
///
/// It is deliberately a whitelist, not an HTML parser: anything outside <see cref="BlockTags"/> /
/// <see cref="InlineTags"/> is left alone as plain text, so a note that happens to mention
/// <c>&lt;algo&gt;</c> keeps showing it verbatim instead of silently swallowing it. Attributes other than
/// <c>align</c>/<c>style="text-align:…"</c> are parsed only so the tag's own extent is known — they are
/// never honoured, and <see cref="SanitizedTag"/> drops them when exporting.
/// </summary>
public static class MarkdownHtmlParser
{
    /// <summary>Block containers whose alignment applies to every line they enclose.</summary>
    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal) { "div", "p", "center" };

    /// <summary>Inline tags concealed like a Markdown marker; those that map to a
    /// <see cref="MdSpanKind"/> in <see cref="StyleOf"/> also style the text they wrap.</summary>
    private static readonly HashSet<string> InlineTags = new(StringComparer.Ordinal)
    {
        "b", "strong", "i", "em", "u", "ins", "s", "strike", "del", "mark", "code", "span", "br"
    };

    private static readonly IReadOnlyList<string> EmptyTags = new List<string>();

    private static readonly Regex TagRegex = new(
        """<\s*(?<close>/)?\s*(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attrs>(?:\s+[a-zA-Z_:][-a-zA-Z0-9_:.]*(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s"'=<>`]+))?)*)\s*(?<self>/)?\s*>""",
        RegexOptions.Compiled);

    private static readonly Regex AlignAttrRegex = new(
        """\balign\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)'|(?<v>[^\s"'=<>`]+))""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TextAlignStyleRegex = new(
        @"\btext-align\s*:\s*(?<v>[a-zA-Z]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Recognized tags inside one line, left to right. Offsets are absolute
    /// (<paramref name="lineOffset"/> + index) so they can be compared against carets, selections and
    /// Markdown spans without further bookkeeping.</summary>
    public static List<HtmlTagToken> ParseTags(string text, int lineOffset)
    {
        var tokens = new List<HtmlTagToken>();
        if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0) return tokens;

        foreach (Match match in TagRegex.Matches(text))
        {
            // "\<div>" is the escape hatch for showing a tag literally, now that an unescaped one is
            // concealed — same backslash escape the Markdown scanner honours for its own markers.
            if (match.Index > 0 && text[match.Index - 1] == '\\') continue;

            var name = match.Groups["name"].Value.ToLowerInvariant();
            var isBlock = BlockTags.Contains(name);
            if (!isBlock && !InlineTags.Contains(name)) continue;

            var isClose = match.Groups["close"].Success;
            var kind = isClose
                ? HtmlTagKind.Close
                : match.Groups["self"].Success || name == "br"
                    ? HtmlTagKind.SelfClosing
                    : HtmlTagKind.Open;

            tokens.Add(new HtmlTagToken
            {
                Start = lineOffset + match.Index,
                End = lineOffset + match.Index + match.Length,
                Name = name,
                Kind = kind,
                IsBlock = isBlock,
                Align = isClose ? MdAlign.None : AlignOf(name, match.Groups["attrs"].Value)
            });
        }

        return tokens;
    }

    /// <summary>
    /// Scans the whole document once and resolves, for every line, the HTML context it renders under:
    /// which alignment encloses it, and which inline formatting tags are still open when the line starts.
    /// It is the block-level counterpart of <see cref="MarkdownLineParser.ComputeFences"/> — neither
    /// answer can be derived from the single line the renderer happens to be drawing, because both an
    /// alignment and an inline tag can open on one line and close several lines below:
    ///
    /// <code>
    /// &lt;div align="center"&gt;
    /// esta línea también va centrada, &lt;i&gt;y esto sigue en cursiva
    /// hasta aquí&lt;/i&gt;
    /// &lt;/div&gt;
    /// </code>
    ///
    /// Lines inside a fenced code block are skipped entirely — a <c>&lt;div&gt;</c> written inside ``` is
    /// sample code, and must neither be concealed nor open a block that would drag the rest of the note
    /// out of alignment. The scan reuses <see cref="MarkdownLineParser.Parse"/> rather than
    /// <see cref="ParseTags"/> directly so that a tag quoted in inline code (<c>`&lt;div&gt;`</c>) is
    /// dropped here exactly as it is when the line is drawn. Only lines that actually carry something are
    /// stored, so the common note (no HTML at all) produces an empty dictionary.
    /// </summary>
    public static Dictionary<int, HtmlLineState> ComputeLineStates(string documentText, IReadOnlyDictionary<int, FenceInfo> fences)
    {
        var result = new Dictionary<int, HtmlLineState>();
        if (string.IsNullOrEmpty(documentText) || documentText.IndexOf('<') < 0) return result;

        var lines = documentText.Split('\n');
        var blocks = new List<MdAlign>();
        var inline = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            fences.TryGetValue(lineNumber, out var fence);
            if (fence.IsCode) continue;

            var text = lines[i];
            var current = blocks.Count > 0 ? blocks[^1] : MdAlign.None;
            var lineAlign = current;
            var contentSeen = false;
            // Snapshot of what is open *before* this line: a tag that closes here still styles the text
            // in front of its closing tag, so the line needs the state it started with, not the one it
            // ends with.
            IReadOnlyList<string> openAtLineStart = inline.Count > 0 ? new List<string>(inline) : EmptyTags;

            var tags = MarkdownLineParser.Parse(text, 0, fence).HtmlTags;
            var cursor = 0;
            foreach (var tag in tags)
            {
                if (!contentSeen && HasContent(text, cursor, tag.Start))
                {
                    lineAlign = current;
                    contentSeen = true;
                }
                cursor = tag.End;

                // Inline tags are tracked separately, in ApplyInlineTags below.
                if (!tag.IsBlock || tag.Kind == HtmlTagKind.SelfClosing) continue;

                if (tag.Kind == HtmlTagKind.Open)
                {
                    current = tag.Align != MdAlign.None ? tag.Align : current;
                    blocks.Add(current);
                }
                else if (blocks.Count > 0)
                {
                    blocks.RemoveAt(blocks.Count - 1);
                    current = blocks.Count > 0 ? blocks[^1] : MdAlign.None;
                }
            }

            ApplyInlineTags(inline, tags);

            if (!contentSeen && HasContent(text, cursor, text.Length)) lineAlign = current;

            if (lineAlign != MdAlign.None || openAtLineStart.Count > 0)
                result[lineNumber] = new HtmlLineState(lineAlign, openAtLineStart);
        }

        return result;
    }

    /// <summary>Advances <paramref name="open"/> — the inline formatting tags left open by the previous
    /// lines — over one line's tags, leaving it as the state that line ends with. Shared by the
    /// document-wide scan and by the exporter, which needs the same answer to reopen an unfinished tag
    /// inside the next block it emits.</summary>
    public static void ApplyInlineTags(List<string> open, IReadOnlyList<HtmlTagToken> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.IsBlock || tag.Kind == HtmlTagKind.SelfClosing) continue;

            if (tag.Kind == HtmlTagKind.Open)
            {
                open.Add(tag.Name);
                continue;
            }

            // Closing an inline tag also closes anything left open inside it, the way a browser does:
            // "<b><i>x</b>" ends both at the </b> instead of leaking italics down the rest of the note.
            var match = open.LastIndexOf(tag.Name);
            if (match >= 0) open.RemoveRange(match, open.Count - match);
        }
    }

    /// <summary>Ranges of text wrapped by a recognized inline formatting tag pair, as the span kind the
    /// live preview already knows how to paint. <paramref name="openAtLineStart"/> carries the tags a
    /// previous line left open (see <see cref="ComputeLineStates"/>), so a pair spanning several lines
    /// styles every line in between and closes on the right one. Emitted outermost-first so a nested pair
    /// applied later wins over the one enclosing it, and a pair still open at the end of the line styles
    /// the rest of that line — it will be continued by the next line's own inherited state.</summary>
    public static List<(int Start, int End, MdSpanKind Kind)> BuildStyleRanges(
        IReadOnlyList<HtmlTagToken> tags, int lineStartOffset, int lineEndOffset, IReadOnlyList<string> openAtLineStart)
    {
        var ranges = new List<(int Start, int End, MdSpanKind Kind)>();
        if (tags.Count == 0 && openAtLineStart.Count == 0) return ranges;

        // Tags with no style of their own (<span>) still ride the stack: they have to swallow their own
        // closing tag, or a "</span>" would be matched against the <b> underneath it.
        var open = new List<(string Name, MdSpanKind? Kind, int ContentStart)>();
        foreach (var name in openAtLineStart) open.Add((name, StyleOf(name), lineStartOffset));

        void Emit(MdSpanKind? kind, int start, int end, int stackDepth)
        {
            if (kind is null || start >= end) return;
            // Bold nested inside italic (or the reverse) must render as both, not as whichever tag closes
            // last: the two live in the same Typeface, so applying them one after another would drop one.
            var combined = kind.Value;
            for (var i = 0; i < stackDepth; i++)
            {
                var outer = open[i].Kind;
                if (combined == MdSpanKind.Bold && outer is MdSpanKind.Italic or MdSpanKind.BoldItalic) combined = MdSpanKind.BoldItalic;
                if (combined == MdSpanKind.Italic && outer is MdSpanKind.Bold or MdSpanKind.BoldItalic) combined = MdSpanKind.BoldItalic;
            }
            ranges.Add((start, end, combined));
        }

        foreach (var tag in tags)
        {
            if (tag.IsBlock || tag.Kind == HtmlTagKind.SelfClosing) continue;

            if (tag.Kind == HtmlTagKind.Open)
            {
                open.Add((tag.Name, StyleOf(tag.Name), tag.End));
                continue;
            }

            var match = LastIndexOf(open, tag.Name);
            if (match < 0) continue;
            // Everything still open inside the tag being closed ends here too, same as a browser.
            for (var i = open.Count - 1; i >= match; i--) Emit(open[i].Kind, open[i].ContentStart, tag.Start, i);
            open.RemoveRange(match, open.Count - match);
        }

        for (var i = 0; i < open.Count; i++)
            Emit(open[i].Kind, open[i].ContentStart, lineEndOffset, i);

        ranges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        return ranges;
    }

    private static int LastIndexOf(List<(string Name, MdSpanKind? Kind, int ContentStart)> open, string name)
    {
        for (var i = open.Count - 1; i >= 0; i--)
        {
            if (open[i].Name == name) return i;
        }
        return -1;
    }

    /// <summary>Whether the line carries a block container — either alone on its line
    /// (<c>&lt;div align="center"&gt;</c>) or wrapped around its own text
    /// (<c>&lt;div align="center"&gt;Hola&lt;/div&gt;</c>). Such a line is a block of its own: the
    /// exporter emits it as-is instead of folding it into a <c>&lt;p&gt;</c>, which would nest a
    /// container inside a paragraph and make the browser tear the two apart.</summary>
    public static bool HasBlockTag(IReadOnlyList<HtmlTagToken> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.IsBlock) return true;
        }
        return false;
    }

    /// <summary>The tag rewritten for export: same element, but carrying only the alignment it declared
    /// (as inline CSS, since HTML5 dropped the <c>align</c> attribute) and none of its other attributes —
    /// a note is user content, and passing arbitrary attributes through into an exported document would
    /// hand it event handlers and styling the app never intended to support.</summary>
    public static string SanitizedTag(HtmlTagToken tag)
    {
        if (tag.Kind == HtmlTagKind.Close) return "</" + tag.Name + ">";
        if (tag.Name == "br") return "<br/>";

        var sb = new StringBuilder("<").Append(tag.Name);
        if (tag.Align != MdAlign.None)
            sb.Append(" style=\"text-align:").Append(CssValue(tag.Align)).Append('"');
        if (tag.Kind == HtmlTagKind.SelfClosing) sb.Append('/');
        return sb.Append('>').ToString();
    }

    public static string CssValue(MdAlign align) => align switch
    {
        MdAlign.Center => "center",
        MdAlign.Right => "right",
        MdAlign.Justify => "justify",
        _ => "left"
    };

    /// <summary>The live-preview style an inline tag stands for, or null when the tag only wraps text
    /// without changing how it looks (<c>&lt;span&gt;</c>).</summary>
    private static MdSpanKind? StyleOf(string name) => name switch
    {
        "b" or "strong" => MdSpanKind.Bold,
        "i" or "em" => MdSpanKind.Italic,
        "u" or "ins" => MdSpanKind.Underline,
        "s" or "strike" or "del" => MdSpanKind.Strikethrough,
        "mark" => MdSpanKind.Highlight,
        "code" => MdSpanKind.InlineCode,
        _ => null
    };

    private static MdAlign AlignOf(string name, string attributes)
    {
        if (name == "center") return MdAlign.Center;
        if (!BlockTags.Contains(name)) return MdAlign.None;

        var attr = AlignAttrRegex.Match(attributes);
        if (attr.Success && ParseAlign(attr.Groups["v"].Value) is { } fromAttribute) return fromAttribute;

        var style = TextAlignStyleRegex.Match(attributes);
        if (style.Success && ParseAlign(style.Groups["v"].Value) is { } fromStyle) return fromStyle;

        return MdAlign.None;
    }

    private static MdAlign? ParseAlign(string value) => value.Trim().ToLowerInvariant() switch
    {
        "center" or "middle" => MdAlign.Center,
        "right" or "end" => MdAlign.Right,
        "justify" => MdAlign.Justify,
        "left" or "start" => MdAlign.Left,
        _ => null
    };

    private static bool HasContent(string text, int from, int to)
    {
        for (var i = Math.Max(0, from); i < Math.Min(to, text.Length); i++)
        {
            if (!char.IsWhiteSpace(text[i])) return true;
        }
        return false;
    }
}
