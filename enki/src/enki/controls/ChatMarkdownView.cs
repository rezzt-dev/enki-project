using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using enki.Localization;
using enki.Services;
using Material.Icons;
using Material.Icons.Avalonia;

namespace enki.Controls;

/// <summary>Pinta una respuesta del asistente a partir de su Markdown (ver
/// <see cref="ChatMarkdownParser"/>). Durante el streaming el texto cambia decenas de veces por segundo y
/// casi siempre solo crece por el final, así que en cada cambio se conservan los controles de los bloques
/// que no han variado y solo se reconstruyen los siguientes.</summary>
public sealed class ChatMarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<ChatMarkdownView, string?>(nameof(Markdown));

    private const double BodyFontSize = 13.5;

    private List<ChatBlock> _blocks = [];

    public ChatMarkdownView()
    {
        Spacing = 10;
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty) Render(Markdown ?? string.Empty);
    }

    private void Render(string markdown)
    {
        var blocks = ChatMarkdownParser.ParseBlocks(markdown);
        var unchanged = 0;
        while (unchanged < blocks.Count && unchanged < _blocks.Count && unchanged < Children.Count
               && SameBlock(blocks[unchanged], _blocks[unchanged]))
            unchanged++;

        while (Children.Count > unchanged) Children.RemoveAt(Children.Count - 1);
        for (var i = unchanged; i < blocks.Count; i++) Children.Add(BuildBlock(blocks[i]));
        _blocks = blocks;
    }

    private static bool SameBlock(ChatBlock a, ChatBlock b) =>
        a.Kind == b.Kind && a.Text == b.Text && a.Level == b.Level && a.Marker == b.Marker
        && a.IsChecked == b.IsChecked && a.Language == b.Language
        && a.Rows.Count == b.Rows.Count && a.Rows.Zip(b.Rows).All(pair => pair.First.SequenceEqual(pair.Second));

    private Control BuildBlock(ChatBlock block) => block.Kind switch
    {
        ChatBlockKind.Heading => BuildHeading(block),
        ChatBlockKind.ListItem => BuildListItem(block),
        ChatBlockKind.Quote => BuildQuote(block),
        ChatBlockKind.Code => BuildCode(block),
        ChatBlockKind.Table => BuildTable(block),
        ChatBlockKind.Rule => Themed(new Border { Height = 1, Margin = new Thickness(0, 4) }, Border.BackgroundProperty, "Ink10"),
        _ => BuildText(block.Text, BodyFontSize)
    };

    private SelectableTextBlock BuildText(string markdown, double fontSize, FontWeight? weight = null)
    {
        var text = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            LineHeight = fontSize * 1.5,
            Inlines = BuildInlines(markdown)
        };
        if (weight is { } fontWeight) text.FontWeight = fontWeight;
        Themed(text, SelectableTextBlock.SelectionBrushProperty, "SelectionBackgroundBrush");
        return text;
    }

    private InlineCollection BuildInlines(string markdown)
    {
        var inlines = new InlineCollection();
        foreach (var span in ChatMarkdownParser.ParseInlines(markdown))
        {
            var lines = span.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) inlines.Add(new LineBreak());
                if (lines[i].Length == 0) continue;
                var run = new Run(lines[i]);
                switch (span.Style)
                {
                    case ChatInlineStyle.Bold: run.FontWeight = FontWeight.Bold; break;
                    case ChatInlineStyle.Italic: run.FontStyle = FontStyle.Italic; break;
                    case ChatInlineStyle.BoldItalic:
                        run.FontWeight = FontWeight.Bold;
                        run.FontStyle = FontStyle.Italic;
                        break;
                    case ChatInlineStyle.Code:
                        Themed(run, TextElement.FontFamilyProperty, "CodeFontFamily");
                        Themed(run, TextElement.BackgroundProperty, "Ink10");
                        run.FontSize = BodyFontSize - 1;
                        break;
                    case ChatInlineStyle.Link:
                        run.TextDecorations = TextDecorations.Underline;
                        Themed(run, TextElement.ForegroundProperty, "TextSecondaryBrush");
                        break;
                }
                inlines.Add(run);
            }
        }
        return inlines;
    }

    private SelectableTextBlock BuildHeading(ChatBlock block)
    {
        var size = block.Level switch { 1 => 19, 2 => 17, 3 => 15, _ => 14 };
        var heading = BuildText(block.Text, size, FontWeight.Bold);
        heading.Margin = new Thickness(0, 6, 0, 0);
        return heading;
    }

    private Grid BuildListItem(ChatBlock block)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(block.Level * 18, -4, 0, 0)
        };
        Control marker = block.IsChecked is { } isChecked
            ? new MaterialIcon
            {
                Kind = isChecked ? MaterialIconKind.CheckboxMarkedOutline : MaterialIconKind.CheckboxBlankOutline,
                Width = 15,
                Height = 15,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 10, 0)
            }
            : new TextBlock
            {
                Text = block.Marker,
                FontSize = BodyFontSize,
                LineHeight = BodyFontSize * 1.5,
                MinWidth = 14,
                Margin = new Thickness(0, 0, 8, 0)
            };
        Themed(marker, block.IsChecked is null ? TextBlock.ForegroundProperty : MaterialIcon.ForegroundProperty, "Ink50");
        var content = BuildText(block.Text, BodyFontSize);
        Grid.SetColumn(content, 1);
        grid.Children.Add(marker);
        grid.Children.Add(content);
        return grid;
    }

    private Border BuildQuote(ChatBlock block)
    {
        var text = BuildText(block.Text, BodyFontSize);
        Themed(text, SelectableTextBlock.ForegroundProperty, "TextSecondaryBrush");
        return Themed(new Border { BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(12, 2), Child = text },
            Border.BorderBrushProperty, "Ink20");
    }

    private Border BuildCode(ChatBlock block)
    {
        var code = new SelectableTextBlock
        {
            Text = block.Text,
            FontSize = 12.5,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(14, 0, 14, 12)
        };
        Themed(code, SelectableTextBlock.FontFamilyProperty, "CodeFontFamily");
        Themed(code, SelectableTextBlock.SelectionBrushProperty, "SelectionBackgroundBrush");

        var language = new TextBlock
        {
            Text = (block.Language ?? LocalizationService.Instance["Assistant_Code_Label"]).ToUpperInvariant(),
            FontSize = 9.5,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 1.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        Themed(language, TextBlock.ForegroundProperty, "Ink40");
        Themed(language, TextBlock.FontFamilyProperty, "InterfaceLabelFontFamily");

        var copyLabel = LocalizationService.Instance["Assistant_Code_Copy"];
        var copy = new Button
        {
            Classes = { "icon-btn" },
            Width = 28,
            Height = 28,
            Content = new MaterialIcon { Kind = MaterialIconKind.ContentCopy, Width = 13, Height = 13 }
        };
        ToolTip.SetTip(copy, copyLabel);
        Avalonia.Automation.AutomationProperties.SetName(copy, copyLabel);
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(block.Text);
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(14, 6, 6, 4) };
        Grid.SetColumn(copy, 1);
        header.Children.Add(language);
        header.Children.Add(copy);

        var body = new StackPanel
        {
            Children =
            {
                header,
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = code
                }
            }
        };
        var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(0), Child = body };
        Themed(border, Border.BackgroundProperty, "Ink05");
        return Themed(border, Border.BorderBrushProperty, "Ink10");
    }

    private ScrollViewer BuildTable(ChatBlock block)
    {
        var columns = block.Rows.Count == 0 ? 0 : block.Rows[0].Count;
        var grid = new Grid();
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r < block.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < columns; c++)
            {
                var cell = BuildText(block.Rows[r][c], 12.5, r == 0 ? FontWeight.Bold : null);
                cell.MaxWidth = 320;
                var border = new Border
                {
                    BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1),
                    Padding = new Thickness(10, 6),
                    Child = cell
                };
                Themed(border, Border.BorderBrushProperty, "Ink10");
                if (r == 0) Themed(border, Border.BackgroundProperty, "Ink05");
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = grid
        };
    }

    /// <summary>Enlaza <paramref name="property"/> al recurso <paramref name="key"/> del tema de forma
    /// dinámica, como un <c>{DynamicResource}</c> en XAML: cambiar de tema o importar uno personalizado
    /// repinta el mensaje sin reconstruirlo.</summary>
    private T Themed<T>(T target, AvaloniaProperty property, string key) where T : AvaloniaObject
    {
        target.Bind(property, this.GetResourceObservable(key));
        return target;
    }
}
