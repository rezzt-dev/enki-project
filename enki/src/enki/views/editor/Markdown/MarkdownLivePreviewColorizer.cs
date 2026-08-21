using System;
using System.Collections.Generic;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using enki.Models;
using enki.Services;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// Applies live-preview styling (heading sizes, bold/italic/strikethrough, inline code
/// shading, link color, list/quote accents) to whichever markers are currently concealed
/// by <see cref="MarkdownElementGenerator"/>. Runs after the generator on every redraw.
/// </summary>
public sealed class MarkdownLivePreviewColorizer : DocumentColorizingTransformer
{
    private readonly LivePreviewState _state;
    private readonly MarkdownStyle _style;

    /// <summary>Supplies the note set a <see cref="MdSpanKind.WikiLink"/>'s target should be resolved
    /// against, so it can render as broken (target matches no note) or ambiguous (matches more than one by
    /// title) instead of looking identical to a resolved link — "ambos casos se ven igual de 'no hacen
    /// nada'" (see docs/analisis-proyecto/roadmap.md, Fase 5). This one colorizer instance is shared across every open tab (see EditorView's constructor),
    /// so EditorView repoints this to the active tab's own note list on every tab switch
    /// (<c>EditorViewModel.WikiLinkScopeNotes</c>) instead of the colorizer owning a fixed list itself.
    /// Null (e.g. before the first tab finishes binding) makes every wikilink render as resolved rather than
    /// flashing broken for a frame.</summary>
    public Func<IReadOnlyList<NoteFile>>? WikiLinkNotesProvider { get; set; }

    public MarkdownLivePreviewColorizer(LivePreviewState state, MarkdownStyle style)
    {
        _state = state;
        _style = style;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length == 0) return;

        var text = CurrentContext.Document.GetText(line);
        var fence = _state.GetFence(line.LineNumber);
        var model = MarkdownLineParser.Parse(text, line.Offset, fence);

        if (model.Kind == MdBlockKind.CodeFence)
        {
            var start = model.MarkerStart == model.MarkerEnd ? line.Offset : model.MarkerStart;
            Safe(start, line.EndOffset, el => el.TextRunProperties.SetTypeface(new Typeface(_style.MonoFontFamily)));
            SetForeground(start, line.EndOffset, _style.TextSecondary);
            return;
        }

        switch (model.Kind)
        {
            case MdBlockKind.Heading:
            {
                var size = _style.BaseFontSize * MarkdownStyle.HeadingScale(model.HeadingLevel);
                var weight = model.HeadingLevel <= 2 ? FontWeight.Bold : FontWeight.SemiBold;
                Safe(model.MarkerStart, line.EndOffset, el =>
                {
                    el.TextRunProperties.SetFontRenderingEmSize(size);
                    el.TextRunProperties.SetTypeface(new Typeface(_style.BodyFontFamily, FontStyle.Normal, weight));
                });
                SetForeground(model.MarkerStart, line.EndOffset, _style.TextPrimary);
                break;
            }
            case MdBlockKind.Blockquote:
                Safe(model.ContentStart, line.EndOffset, el =>
                    el.TextRunProperties.SetTypeface(new Typeface(_style.BodyFontFamily, FontStyle.Italic)));
                SetForeground(model.ContentStart, line.EndOffset, _style.TextSecondary);
                break;
            case MdBlockKind.BulletList:
            case MdBlockKind.OrderedList:
                SetForeground(model.MarkerStart, model.MarkerEnd, _style.Accent);
                break;
            case MdBlockKind.TaskList:
                SetForeground(model.MarkerStart, model.MarkerEnd, _style.Accent);
                // Completed tasks read as done at a glance (muted + struck through), matching the
                // convention every other live-preview task list (Obsidian, Notion, GitHub) uses.
                if (model.TaskChecked)
                {
                    Safe(model.ContentStart, line.EndOffset, el =>
                        el.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough));
                    SetForeground(model.ContentStart, line.EndOffset, _style.TextMuted);
                }
                break;
        }

        foreach (var span in model.Spans)
        {
            if (_state.Intersects(span.Start, span.End)) continue;

            switch (span.Kind)
            {
                case MdSpanKind.Bold:
                    Safe(span.ContentStart, span.ContentEnd, el =>
                        el.TextRunProperties.SetTypeface(new Typeface(_style.BodyFontFamily, FontStyle.Normal, FontWeight.Bold)));
                    break;
                case MdSpanKind.Italic:
                    Safe(span.ContentStart, span.ContentEnd, el =>
                        el.TextRunProperties.SetTypeface(new Typeface(_style.BodyFontFamily, FontStyle.Italic)));
                    break;
                case MdSpanKind.BoldItalic:
                    Safe(span.ContentStart, span.ContentEnd, el =>
                        el.TextRunProperties.SetTypeface(new Typeface(_style.BodyFontFamily, FontStyle.Italic, FontWeight.Bold)));
                    break;
                case MdSpanKind.Strikethrough:
                    Safe(span.ContentStart, span.ContentEnd, el =>
                        el.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough));
                    SetForeground(span.ContentStart, span.ContentEnd, _style.TextMuted);
                    break;
                case MdSpanKind.InlineCode:
                    Safe(span.ContentStart, span.ContentEnd, el =>
                    {
                        el.TextRunProperties.SetTypeface(new Typeface(_style.MonoFontFamily));
                        el.BackgroundBrush = _style.CodeInlineBackground;
                    });
                    SetForeground(span.ContentStart, span.ContentEnd, _style.Accent);
                    break;
                case MdSpanKind.Highlight:
                    Safe(span.ContentStart, span.ContentEnd, el => el.BackgroundBrush = _style.HighlightBackground);
                    break;
                case MdSpanKind.Link:
                    Safe(span.ContentStart, span.ContentEnd, el =>
                        el.TextRunProperties.SetTextDecorations(TextDecorations.Underline));
                    SetForeground(span.ContentStart, span.ContentEnd, _style.Accent);
                    break;
                case MdSpanKind.WikiLink:
                {
                    var status = ResolveWikiLinkStatus(span.LinkTarget);
                    Safe(span.ContentStart, span.ContentEnd, el =>
                    {
                        el.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                        // Ambiguous (matches more than one note) also uses Danger, same as broken — both
                        // are "this link needs the user's attention" — but italicized so it doesn't read as
                        // a plain dead link: there IS a note there, just not a unique one.
                        if (status == WikiLinkResolution.Ambiguous)
                            el.TextRunProperties.SetTypeface(new Typeface(_style.BodyFontFamily, FontStyle.Italic));
                    });
                    SetForeground(span.ContentStart, span.ContentEnd,
                        status == WikiLinkResolution.Resolved ? _style.Accent : _style.Danger);
                    break;
                }
            }
        }
    }

    /// <summary>Same resolution strategy every other wikilink consumer uses
    /// (<see cref="WikiLinkParser.ResolveTargetWithStatus"/>) — filename first, unambiguous title as
    /// fallback — so this live-preview coloring can never disagree with what Ctrl+click
    /// (<c>ViewModels.EditorViewModel.OpenWikiLink</c>) or the backlinks panel would do with the same link.</summary>
    private WikiLinkResolution ResolveWikiLinkStatus(string? target)
    {
        if (string.IsNullOrEmpty(target) || WikiLinkNotesProvider is null) return WikiLinkResolution.Resolved;
        return WikiLinkParser.ResolveTargetWithStatus(WikiLinkNotesProvider(), target).Status;
    }

    private void Safe(int start, int end, System.Action<VisualLineElement> action)
    {
        if (start < end) ChangeLinePart(start, end, action);
    }

    /// <summary>Applies <paramref name="brush"/> to [start,end), except wherever that range overlaps the
    /// live selection — there it applies <see cref="MarkdownStyle.SelectionForeground"/> instead, so a
    /// styled run (heading, link, quote, list marker…) stays legible once selected rather than keeping
    /// its own color against the selection highlight, which for near-white styled text on this app's
    /// near-white selection background renders as blank space (see MarkdownStyle.SelectionForeground).</summary>
    private void SetForeground(int start, int end, IBrush brush)
    {
        foreach (var (segStart, segEnd, selected) in SplitBySelection(start, end))
        {
            var fg = selected ? _style.SelectionForeground : brush;
            ChangeLinePart(segStart, segEnd, el => el.TextRunProperties.SetForegroundBrush(fg));
        }
    }

    private IEnumerable<(int Start, int End, bool Selected)> SplitBySelection(int start, int end)
    {
        if (start >= end) yield break;

        var cuts = new List<int> { start, end };
        foreach (var (selStart, selEnd) in _state.Selections)
        {
            if (selStart > start && selStart < end) cuts.Add(selStart);
            if (selEnd > start && selEnd < end) cuts.Add(selEnd);
        }
        cuts.Sort();

        for (var i = 0; i < cuts.Count - 1; i++)
        {
            var segStart = cuts[i];
            var segEnd = cuts[i + 1];
            if (segStart >= segEnd) continue;

            var selected = false;
            foreach (var (selStart, selEnd) in _state.Selections)
            {
                if (selStart < segEnd && selEnd > segStart) { selected = true; break; }
            }
            yield return (segStart, segEnd, selected);
        }
    }
}
