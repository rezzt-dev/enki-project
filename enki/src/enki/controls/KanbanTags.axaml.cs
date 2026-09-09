using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using enki.Localization;

namespace enki.Controls;

/// <summary>Presenta las etiquetas con expansión accesible cuando exceden el ancho real de una línea.
/// El estado de expansión es visual y local a cada tarjeta; nunca modifica sus etiquetas.</summary>
public partial class KanbanTags : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<string>?> TagsProperty =
        AvaloniaProperty.Register<KanbanTags, IReadOnlyList<string>?>(nameof(Tags));

    public IReadOnlyList<string>? Tags
    {
        get => GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    private bool _expanded;

    public KanbanTags()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Refresh();
        TagsText.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.FontFamilyProperty || e.Property == TextBlock.FontSizeProperty)
                Refresh();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LocalizationService.Instance.PropertyChanged += LanguageChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LocalizationService.Instance.PropertyChanged -= LanguageChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TagsProperty)
        {
            _expanded = false;
            Refresh();
        }
    }

    private void ToggleExpanded(object? sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        Refresh();
        e.Handled = true;
    }

    private void Refresh()
    {
        if (TagsText is null || ExpandButton is null) return;
        var tags = Tags ?? Array.Empty<string>();
        TagsPanel.IsVisible = tags.Count > 0;
        var fullText = string.Join(", ", tags);
        var measured = new FormattedText(fullText, CultureInfo.CurrentCulture, FlowDirection,
            new Typeface(TagsText.FontFamily, TagsText.FontStyle, TagsText.FontWeight),
            TagsText.FontSize, Brushes.Black);
        var overflows = Bounds.Width > 0 && measured.WidthIncludingTrailingWhitespace > Bounds.Width;
        ExpandButton.IsVisible = overflows;
        TagsText.Text = _expanded || !overflows ? fullText : string.Join(", ", tags.Take(5));
        TagsText.MaxLines = _expanded || tags.Count > 5 ? 0 : 1;
        ExpandButton.Content = LocalizationService.Instance[_expanded ? "Kanban_Tags_ShowLess" : "Kanban_Tags_ShowAll"];
        ToolTip.SetTip(TagsText, fullText);
    }
}
