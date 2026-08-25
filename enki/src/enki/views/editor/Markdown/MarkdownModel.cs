using System.Collections.Generic;

namespace enki.Views.Editor.Markdown;

public enum MdBlockKind
{
    Paragraph,
    Heading,
    Blockquote,
    BulletList,
    OrderedList,
    TaskList,
    HorizontalRule,
    CodeFence
}

public enum MdSpanKind
{
    Bold,
    Italic,
    BoldItalic,
    Strikethrough,
    InlineCode,
    Highlight,
    Link,

    /// <summary>Obsidian-style <c>[[Note]]</c> / <c>[[Note|Alias]]</c> reference — same concealment/click
    /// mechanics as <see cref="Link"/>, but resolved against the vault's notes instead of opened as a
    /// URL.</summary>
    WikiLink
}

/// <summary>An inline formatting span. Start/End include the markers; ContentStart/ContentEnd exclude them.</summary>
public sealed class MdSpan
{
    public MdSpanKind Kind { get; set; }
    public int Start { get; set; }
    public int End { get; set; }
    public int ContentStart { get; set; }
    public int ContentEnd { get; set; }

    /// <summary>For <see cref="MdSpanKind.Link"/>: the URL to open. For <see cref="MdSpanKind.WikiLink"/>:
    /// the raw target text (note title or filename, alias/heading-anchor already stripped) to resolve —
    /// see <see cref="Services.WikiLinkParser.ResolveTarget"/>.</summary>
    public string? LinkTarget { get; set; }
}

/// <summary>Parsed structure of a single document line, in absolute document offsets.</summary>
public sealed class MdLine
{
    public MdBlockKind Kind { get; set; } = MdBlockKind.Paragraph;
    public int HeadingLevel { get; set; }

    /// <summary>Block-level marker range (e.g. "# ", "&gt; ", "- ", "1. ", "- [x] "), hideable/replaceable.</summary>
    public int MarkerStart { get; set; }
    public int MarkerEnd { get; set; }

    /// <summary>Offset where inline content begins (usually == MarkerEnd).</summary>
    public int ContentStart { get; set; }

    public bool TaskChecked { get; set; }

    /// <summary>Absolute offset of the single character inside "[ ]" / "[x]", or -1.</summary>
    public int CheckboxCharOffset { get; set; } = -1;

    public string? FenceLanguage { get; set; }

    public List<MdSpan> Spans { get; } = new();
}

/// <summary>Fence state for one document line, computed once per document over the whole text.</summary>
public readonly struct FenceInfo
{
    public bool IsDelimiter { get; }
    public bool IsInside { get; }
    public string? Language { get; }

    public FenceInfo(bool isDelimiter, bool isInside, string? language)
    {
        IsDelimiter = isDelimiter;
        IsInside = isInside;
        Language = language;
    }

    public bool IsCode => IsDelimiter || IsInside;
}
