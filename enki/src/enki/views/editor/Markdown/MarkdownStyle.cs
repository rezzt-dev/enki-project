using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace enki.Views.Editor.Markdown;

/// <summary>Brushes and fonts used by the live-preview renderer, resolved once from the app's theme resources.</summary>
public sealed class MarkdownStyle
{
    public required IBrush TextPrimary { get; init; }
    public required IBrush TextSecondary { get; init; }
    public required IBrush TextMuted { get; init; }
    public required IBrush Accent { get; init; }
    public required IBrush Danger { get; init; }
    /// <summary>Foreground the colorizer must switch styled text to wherever it overlaps the selection —
    /// otherwise a heading/link/code span keeps its own color while the selection highlight behind it
    /// turns the same near-white shade the app uses for selected-text background, and the run reads as
    /// blank space instead of selected text (see MarkdownLivePreviewColorizer.SetForeground).</summary>
    public required IBrush SelectionForeground { get; init; }
    public required IBrush CodeInlineBackground { get; init; }
    public required IBrush CodeBlockBackground { get; init; }
    public required IBrush CodeBlockBorder { get; init; }
    public required IBrush HighlightBackground { get; init; }
    public required IBrush QuoteBar { get; init; }
    public required IBrush RuleLine { get; init; }
    public required FontFamily BodyFontFamily { get; set; }
    public required FontFamily MonoFontFamily { get; init; }
    public double BaseFontSize { get; set; } = 14.5;

    public static MarkdownStyle FromApp(FontFamily bodyFont, FontFamily monoFont, double baseFontSize)
    {
        IBrush FindBrush(string key, string fallbackHex)
        {
            if (Application.Current is { } app && app.TryFindResource(key, out var value) && value is IBrush brush)
                return brush;
            return new SolidColorBrush(Color.Parse(fallbackHex));
        }

        var textPrimary = FindBrush("TextPrimaryBrush", "#F2F2F3");
        var textSecondary = FindBrush("TextSecondaryBrush", "#9797A1");
        var textMuted = FindBrush("TextMutedBrush", "#5C5C66");
        // No brand color in this design system (see docs/sistema-diseno-web.md §1.3) — the "accent" for
        // markdown emphasis is the monochrome ink color, not a hue.
        var accent = FindBrush("InkBrush", "#F5F5F5");
        var danger = FindBrush("DangerBrush", "#F76B6B");
        var border = FindBrush("BorderSubtleBrush", "#1A1A1E");
        var selectionForeground = FindBrush("SelectionForegroundBrush", "#000000");
        // PanelBackgroundBrush is the same color as the editor card behind it, so filling code blocks
        // with it was invisible — InputBackgroundBrush plus a visible border is what actually reads as a box.
        var codeBackground = FindBrush("InputBackgroundBrush", "#141414");
        var codeBorder = FindBrush("Ink10", "#2A2A2E");

        return new MarkdownStyle
        {
            TextPrimary = textPrimary,
            TextSecondary = textSecondary,
            TextMuted = textMuted,
            Accent = accent,
            Danger = danger,
            SelectionForeground = selectionForeground,
            CodeInlineBackground = codeBackground,
            CodeBlockBackground = codeBackground,
            CodeBlockBorder = codeBorder,
            HighlightBackground = new SolidColorBrush(Color.Parse("#F5C542"), 0.28),
            QuoteBar = accent,
            RuleLine = border,
            BodyFontFamily = bodyFont,
            MonoFontFamily = monoFont,
            BaseFontSize = baseFontSize
        };
    }

    /// <summary>Font size multiplier for heading levels 1-6, matching Obsidian's default theme scale roughly.</summary>
    public static double HeadingScale(int level) => level switch
    {
        1 => 1.80,
        2 => 1.55,
        3 => 1.32,
        4 => 1.15,
        5 => 1.05,
        _ => 0.95
    };
}
