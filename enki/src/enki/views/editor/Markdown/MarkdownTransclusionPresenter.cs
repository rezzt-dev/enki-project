using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;
using enki.Localization;
using enki.Services;

namespace enki.Views.Editor.Markdown;

/// <summary>Construye el bloque de solo lectura que sustituye a una transclusión en el live preview.
/// Reutiliza <see cref="MarkdownLineParser"/> para que encabezados, listas y formato en línea se vean
/// igual que en la nota principal, sin crear un segundo parser o un segundo documento editable.</summary>
internal static class MarkdownTransclusionPresenter
{
    private const int MaxRenderedLines = 300;

    public static Control Build(NoteTransclusionResult result, MarkdownTransclusionContext context,
        MarkdownStyle style, double maxWidth)
    {
        Control body = result.Status == NoteTransclusionStatus.Resolved
            ? BuildBody(result, context, style)
            : BuildProblem(result, style);

        var title = result.Note?.Title ?? WikiLinkParser.ParseTarget(result.Reference).Note;
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Children =
            {
                new MaterialIcon
                {
                    Kind = result.Status == NoteTransclusionStatus.Resolved
                        ? MaterialIconKind.FileDocumentOutline
                        : MaterialIconKind.AlertCircleOutline,
                    Width = 14,
                    Height = 14,
                    Foreground = result.Status == NoteTransclusionStatus.Resolved ? style.TextSecondary : style.Danger,
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = title,
                    FontFamily = style.BodyFontFamily,
                    FontSize = Math.Max(11, style.BaseFontSize - 2),
                    FontWeight = FontWeight.SemiBold,
                    Foreground = result.Status == NoteTransclusionStatus.Resolved ? style.TextSecondary : style.Danger,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(header);
        panel.Children.Add(body);

        var border = new Border
        {
            Child = panel,
            Background = style.CodeBlockBackground,
            BorderBrush = result.Status == NoteTransclusionStatus.Resolved ? style.CodeBlockBorder : style.Danger,
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(12, 10),
            Margin = new Thickness(0, 5),
            Width = Math.Max(180, Math.Min(maxWidth, 720)),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(border, string.Format(CultureInfo.CurrentCulture,
            LocalizationService.Instance["Editor_Transclusion_AccessibleName"], title));

        if (result.Note is not null && context.Open is not null)
        {
            border.Cursor = new Cursor(StandardCursorType.Hand);
            border.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left) return;
                context.Open(result.Reference);
                e.Handled = true;
            };
        }

        SetBaseline(border);
        return border;
    }

    private static StackPanel BuildBody(NoteTransclusionResult result, MarkdownTransclusionContext context,
        MarkdownStyle style)
    {
        var stack = new StackPanel { Spacing = 4 };
        var normalized = result.Markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var fences = MarkdownLineParser.ComputeFences(normalized);
        var count = Math.Min(lines.Length, MaxRenderedLines);
        var offset = 0;

        for (var i = 0; i < count; i++)
        {
            fences.TryGetValue(i + 1, out var fence);
            var model = MarkdownLineParser.Parse(lines[i], offset, fence);
            var standalone = StandaloneTransclusion(model, lines[i], offset);
            if (standalone?.LinkTarget is { } nestedReference && context.Resolve is not null)
            {
                var nested = context.Resolve(nestedReference, result.Ancestry);
                stack.Children.Add(Build(nested, context, style, 660));
            }
            else if (!fence.IsDelimiter && (lines[i].Length > 0 || model.Kind == MdBlockKind.HorizontalRule))
            {
                stack.Children.Add(BuildLine(lines[i], offset, model, style, fence.IsInside));
            }
            else if (lines[i].Length == 0)
            {
                stack.Children.Add(new Border { Height = 5 });
            }
            offset += lines[i].Length + 1;
        }

        if (lines.Length > MaxRenderedLines)
        {
            stack.Children.Add(new TextBlock
            {
                Text = LocalizationService.Instance["Editor_Transclusion_Truncated"],
                FontFamily = style.BodyFontFamily,
                FontSize = Math.Max(10, style.BaseFontSize - 3),
                FontStyle = FontStyle.Italic,
                Foreground = style.TextMuted
            });
        }
        return stack;
    }

    private static MdSpan? StandaloneTransclusion(MdLine model, string line, int offset)
    {
        foreach (var span in model.Spans)
        {
            if (span.Kind != MdSpanKind.Transclusion) continue;
            if (string.IsNullOrWhiteSpace(line[..(span.Start - offset)])
                && string.IsNullOrWhiteSpace(line[(span.End - offset)..])) return span;
        }
        return null;
    }

    private static Control BuildLine(string line, int offset, MdLine model, MarkdownStyle style, bool inCode)
    {
        if (model.Kind == MdBlockKind.HorizontalRule)
            return new Border { Height = 1, Background = style.RuleLine, Margin = new Thickness(0, 5) };

        var prefix = model.Kind switch
        {
            MdBlockKind.BulletList => "• ",
            MdBlockKind.OrderedList => "1. ",
            MdBlockKind.TaskList => model.TaskChecked ? "☑ " : "☐ ",
            MdBlockKind.Blockquote => "│ ",
            _ => string.Empty
        };
        var start = Math.Clamp(model.ContentStart - offset, 0, line.Length);
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = inCode ? style.MonoFontFamily : style.BodyFontFamily,
            FontSize = model.Kind == MdBlockKind.Heading
                ? style.BaseFontSize * MarkdownStyle.HeadingScale(model.HeadingLevel)
                : style.BaseFontSize,
            FontWeight = model.Kind == MdBlockKind.Heading ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = inCode ? style.TextSecondary : style.TextPrimary
        };
        if (prefix.Length > 0) text.Inlines!.Add(new Avalonia.Controls.Documents.Run(prefix));
        AddInlineRuns(text, line, offset, start, model, style);

        if (!inCode) return text;
        return new Border
        {
            Child = text,
            Background = style.CodeBlockBackground,
            Padding = new Thickness(8, 2),
            CornerRadius = new CornerRadius(0)
        };
    }

    private static void AddInlineRuns(TextBlock target, string line, int offset, int start, MdLine model,
        MarkdownStyle style)
    {
        var position = start;
        foreach (var span in model.Spans)
        {
            var spanStart = span.Start - offset;
            var spanEnd = span.End - offset;
            if (spanStart < position || spanStart < start || spanEnd > line.Length) continue;
            if (spanStart > position) target.Inlines!.Add(new Avalonia.Controls.Documents.Run(line[position..spanStart]));

            var contentStart = Math.Clamp(span.ContentStart - offset, 0, line.Length);
            var contentEnd = Math.Clamp(span.ContentEnd - offset, contentStart, line.Length);
            var content = line[contentStart..contentEnd];
            var run = new Avalonia.Controls.Documents.Run(content);
            switch (span.Kind)
            {
                case MdSpanKind.Bold:
                    run.FontWeight = FontWeight.Bold;
                    break;
                case MdSpanKind.Italic:
                    run.FontStyle = FontStyle.Italic;
                    break;
                case MdSpanKind.BoldItalic:
                    run.FontWeight = FontWeight.Bold;
                    run.FontStyle = FontStyle.Italic;
                    break;
                case MdSpanKind.Strikethrough:
                    run.TextDecorations = TextDecorations.Strikethrough;
                    break;
                case MdSpanKind.InlineCode:
                    run.FontFamily = style.MonoFontFamily;
                    run.Background = style.CodeInlineBackground;
                    break;
                case MdSpanKind.Link:
                case MdSpanKind.WikiLink:
                    run.Foreground = style.Accent;
                    run.TextDecorations = TextDecorations.Underline;
                    break;
                case MdSpanKind.Transclusion:
                    run.Text = span.LinkTarget ?? content;
                    run.Foreground = style.Accent;
                    break;
            }
            target.Inlines!.Add(run);
            position = spanEnd;
        }
        if (position < line.Length) target.Inlines!.Add(new Avalonia.Controls.Documents.Run(line[position..]));
    }

    private static TextBlock BuildProblem(NoteTransclusionResult result, MarkdownStyle style) => new()
    {
        Text = LocalizationService.Instance[StatusKey(result.Status)],
        FontFamily = style.BodyFontFamily,
        FontSize = Math.Max(11, style.BaseFontSize - 1),
        Foreground = style.TextSecondary,
        TextWrapping = TextWrapping.Wrap
    };

    private static string StatusKey(NoteTransclusionStatus status) => status switch
    {
        NoteTransclusionStatus.Ambiguous => "Editor_Transclusion_Ambiguous",
        NoteTransclusionStatus.SectionMissing => "Editor_Transclusion_SectionMissing",
        NoteTransclusionStatus.BlockMissing => "Editor_Transclusion_BlockMissing",
        NoteTransclusionStatus.Locked => "Editor_Transclusion_Locked",
        NoteTransclusionStatus.Cycle => "Editor_Transclusion_Cycle",
        NoteTransclusionStatus.DepthLimit => "Editor_Transclusion_DepthLimit",
        NoteTransclusionStatus.ReadFailed => "Editor_Transclusion_ReadFailed",
        _ => "Editor_Transclusion_Broken"
    };

    private static void SetBaseline(Control control)
    {
        control.Measure(Size.Infinity);
        TextBlock.SetBaselineOffset(control, Math.Max(0, control.DesiredSize.Height));
    }
}
