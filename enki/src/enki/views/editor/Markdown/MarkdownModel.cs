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

    /// <summary>Underline — has no Markdown marker of its own; produced only by the HTML
    /// <c>&lt;u&gt;</c>/<c>&lt;ins&gt;</c> tags (see <see cref="MarkdownHtmlParser"/>).</summary>
    Underline,

    /// <summary>A recognized HTML tag (<c>&lt;div align="center"&gt;</c>, <c>&lt;b&gt;</c>, <c>&lt;/div&gt;</c>…),
    /// concealed exactly like a Markdown marker: the span covers the tag itself and has no content, so the
    /// live preview hides it whenever the caret is elsewhere and the exporter emits it as real HTML.</summary>
    HtmlTag,

    /// <summary>Obsidian-style <c>[[Note]]</c> / <c>[[Note|Alias]]</c> reference — same concealment/click
    /// mechanics as <see cref="Link"/>, but resolved against the vault's notes instead of opened as a
    /// URL.</summary>
    WikiLink,

    /// <summary>Imagen: <c>![alt](ruta)</c> o la forma incrustada de Obsidian <c>![[archivo.png]]</c>. A
    /// diferencia del resto de spans, el live preview no oculta solo sus marcadores sino el span entero,
    /// y dibuja la imagen real en su lugar (ver <see cref="MarkdownElementGenerator"/>); el rango cubre
    /// desde el <c>!</c> hasta el paréntesis/corchete de cierre para que ocultarlo no deje residuos.</summary>
    Image
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
    /// see <see cref="Services.WikiLinkParser.ResolveTarget"/>. Para <see cref="MdSpanKind.Image"/>: la
    /// ruta (relativa a la nota, absoluta, <c>data:</c> o URL) de la imagen.</summary>
    public string? LinkTarget { get; set; }

    /// <summary>Para <see cref="MdSpanKind.Image"/>: tamaño pedido explícitamente con la sintaxis de
    /// Obsidian <c>![alt|300](ruta)</c> / <c>![alt|300x200](ruta)</c> / <c>![[archivo.png|300]]</c>, en
    /// píxeles; 0 = sin especificar, y la imagen se dibuja a su tamaño natural limitada al ancho de la
    /// columna. Con tamaño, el sufijo <c>|300</c> queda fuera de
    /// <see cref="ContentStart"/>/<see cref="ContentEnd"/>, así que el texto alternativo es el mismo aquí
    /// y en el HTML exportado.</summary>
    public int ImageWidth { get; set; }

    /// <summary>Alto explícito en píxeles de la imagen, o 0 — ver <see cref="ImageWidth"/>. Solo se
    /// rellena con la forma <c>|anchoxalto</c>; <c>|ancho</c> deja el alto proporcional.</summary>
    public int ImageHeight { get; set; }
}

/// <summary>Parsed structure of a single document line, in absolute document offsets.</summary>
public sealed class MdLine
{
    public MdBlockKind Kind { get; set; } = MdBlockKind.Paragraph;
    public int HeadingLevel { get; set; }

    /// <summary>Número de marcadores de cita consecutivos al inicio de la línea.</summary>
    public int QuoteDepth { get; set; }

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

    /// <summary>Recognized HTML tags found on the line, in document order — the same tags that also appear
    /// in <see cref="Spans"/> as <see cref="MdSpanKind.HtmlTag"/>, kept apart so consumers can reason about
    /// them as tags (pairing, alignment, export) instead of as opaque concealed ranges.</summary>
    public List<HtmlTagToken> HtmlTags { get; } = new();
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
