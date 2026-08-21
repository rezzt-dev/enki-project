using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// Conceals Markdown syntax markers (headings, emphasis, links, blockquote/list markers)
/// wherever the caret/selection isn't touching them, and replaces list bullets and task
/// checkboxes with rendered widgets - the core mechanic behind Obsidian's live preview.
/// </summary>
public sealed class MarkdownElementGenerator : VisualLineElementGenerator
{
    private enum ZoneKind { Hide, Bullet, Checkbox, QuoteMarker, TablePipe, TableSeparator }

    private readonly record struct Zone(int Start, int End, ZoneKind Kind, MdLine Line);

    private readonly LivePreviewState _state;
    private readonly TextArea _textArea;

    private int _cachedLineNumber = -1;
    private List<Zone> _cachedZones = new();

    public MarkdownElementGenerator(LivePreviewState state, TextArea textArea)
    {
        _state = state;
        _textArea = textArea;
    }

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _cachedLineNumber = -1;
    }

    private List<Zone> GetZones(int lineNumber)
    {
        if (_cachedLineNumber == lineNumber) return _cachedZones;

        var line = CurrentContext.Document.GetLineByNumber(lineNumber);
        var text = CurrentContext.Document.GetText(line);
        var fence = _state.GetFence(lineNumber);
        var model = MarkdownLineParser.Parse(text, line.Offset, fence);
        var zones = new List<Zone>();
        var lineActive = _state.Intersects(line.Offset, line.EndOffset);

        switch (model.Kind)
        {
            case MdBlockKind.Heading:
            case MdBlockKind.HorizontalRule:
            case MdBlockKind.CodeFence:
                if (!lineActive && model.MarkerStart < model.MarkerEnd)
                    zones.Add(new Zone(model.MarkerStart, model.MarkerEnd, ZoneKind.Hide, model));
                break;
            case MdBlockKind.Blockquote:
                // Unlike the other Hide zones below, the '>' marker can't just collapse to zero width:
                // MarkdownBackgroundRenderer always paints the quote rail at a fixed x, so blank text
                // here would leave nothing reserving that space and the rail would cut straight through
                // the first letters of the quoted text.
                if (model.MarkerStart < model.MarkerEnd)
                    zones.Add(new Zone(model.MarkerStart, model.MarkerEnd, ZoneKind.QuoteMarker, model));
                break;
            case MdBlockKind.BulletList:
                if (model.MarkerStart < model.MarkerEnd)
                    zones.Add(new Zone(model.MarkerStart, model.MarkerEnd, ZoneKind.Bullet, model));
                break;
            case MdBlockKind.TaskList:
                if (model.MarkerStart < model.MarkerEnd)
                    zones.Add(new Zone(model.MarkerStart, model.MarkerEnd, ZoneKind.Checkbox, model));
                break;
            case MdBlockKind.Table when !lineActive:
                if (model.IsTableSeparator)
                {
                    // Oculta toda la fila separadora; el fondo pintará la línea horizontal.
                    zones.Add(new Zone(line.Offset, line.Offset + line.Length, ZoneKind.TableSeparator, model));
                }
                else
                {
                    for (var i = 0; i < text.Length; i++)
                    {
                        if (text[i] != '|') continue;
                        var pipeOffset = line.Offset + i;
                        // No ocultamos los '|' que forman parte de sintaxis inline (código, wikilinks, etc.)
                        if (model.Spans.Any(s => pipeOffset >= s.Start && pipeOffset < s.End)) continue;
                        zones.Add(new Zone(pipeOffset, pipeOffset + 1, ZoneKind.TablePipe, model));
                    }
                }
                break;
        }

        foreach (var span in model.Spans)
        {
            if (_state.Intersects(span.Start, span.End)) continue;
            if (span.ContentStart > span.Start) zones.Add(new Zone(span.Start, span.ContentStart, ZoneKind.Hide, model));
            if (span.End > span.ContentEnd) zones.Add(new Zone(span.ContentEnd, span.End, ZoneKind.Hide, model));
        }

        zones.Sort((a, b) => a.Start.CompareTo(b.Start));
        _cachedLineNumber = lineNumber;
        _cachedZones = zones;
        return zones;
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        var line = CurrentContext.Document.GetLineByOffset(startOffset);
        foreach (var zone in GetZones(line.LineNumber))
        {
            if (zone.Start >= startOffset) return zone.Start;
        }
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var line = CurrentContext.Document.GetLineByOffset(offset);
        foreach (var zone in GetZones(line.LineNumber))
        {
            if (zone.Start != offset) continue;
            var length = zone.End - zone.Start;
            return zone.Kind switch
            {
                // Trailing spaces after the glyph give the bullet room to breathe instead of
                // butting straight into the list text (the hidden marker span it replaces already
                // covered that width, so this doesn't shift anything else on the line).
                ZoneKind.Bullet => new FormattedTextElement("•  ", length),
                ZoneKind.Checkbox => new InlineObjectElement(length, BuildCheckbox(zone.Line)),
                ZoneKind.QuoteMarker => new FormattedTextElement("   ", length),
                ZoneKind.TablePipe => new FormattedTextElement("\u2502", length),
                ZoneKind.TableSeparator => new FormattedTextElement(string.Empty, length),
                _ => new FormattedTextElement(string.Empty, length)
            };
        }
        return null;
    }

    private CheckBox BuildCheckbox(MdLine model)
    {
        var checkBox = CreateInertCheckbox(model.TaskChecked, _textArea);

        if (model.CheckboxCharOffset >= 0)
        {
            // Anchored rather than a plain captured int: the visual line (and this widget) can outlive a
            // document edit made elsewhere before AvaloniaEdit gets around to rebuilding it, and a raw
            // offset would then point at the wrong character on click, corrupting whatever text had shifted
            // into its place. TextAnchor tracks the "[ ]" char through any such edit automatically.
            var anchor = _textArea.Document.CreateAnchor(model.CheckboxCharOffset);
            anchor.MovementType = AnchorMovementType.BeforeInsertion;

            checkBox.IsHitTestVisible = true;
            checkBox.Click += (_, _) =>
            {
                if (anchor.IsDeleted) return;
                var doc = _textArea.Document;
                var offset = anchor.Offset;
                if (offset < 0 || offset >= doc.TextLength) return;
                var current = doc.GetCharAt(offset);
                var next = current is 'x' or 'X' ? ' ' : 'x';
                doc.Replace(offset, 1, next.ToString());
            };
        }

        return checkBox;
    }

    /// <summary>The 16px box (see CheckBox.markdown-checkbox in Styles.axaml) sits inside an
    /// AvaloniaEdit <see cref="InlineObjectElement"/>, whose TextView.ArrangeOverride places the
    /// control at the top of the line box. To center the box with the caret — which spans the full
    /// line height — we push it down by half the difference between the editor line height and the
    /// box, and pin the inline run's baseline to the text baseline so the surrounding line height
    /// does not grow.</summary>
    private static CheckBox CreateInertCheckbox(bool isChecked, TextArea textArea)
    {
        const double boxSize = 16;
        var textView = textArea.TextView;
        var topMargin = Math.Max(0, (textView.DefaultLineHeight - boxSize) / 2);

        var checkBox = new CheckBox
        {
            IsChecked = isChecked,
            IsHitTestVisible = false,
            Padding = default,
            Width = boxSize,
            Height = boxSize,
            MinWidth = boxSize,
            MinHeight = boxSize,
            Margin = new Avalonia.Thickness(0, topMargin, 6, 0),
            VerticalAlignment = VerticalAlignment.Top,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        checkBox.Classes.Add("markdown-checkbox");
        TextBlock.SetBaselineOffset(checkBox, textView.DefaultBaseline);
        return checkBox;
    }
}
