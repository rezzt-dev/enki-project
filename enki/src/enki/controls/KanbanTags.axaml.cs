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

    /// <summary>Tope de etiquetas de la vista plegada; el ancho real puede recortarlo aún más.</summary>
    private const int MaxCollapsedTags = 5;

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

    /// <summary>Plegadas, las etiquetas ocupan exactamente una línea: se muestran las que quepan
    /// (nunca más de <see cref="MaxCollapsedTags"/>) y el resto se resume en un "+N" que siempre
    /// queda visible, en vez de perderse tras los puntos suspensivos.</summary>
    private void Refresh()
    {
        if (TagsText is null || ExpandButton is null) return;
        var tags = Tags ?? Array.Empty<string>();
        TagsPanel.IsVisible = tags.Count > 0;
        if (tags.Count == 0) return;

        var fullText = string.Join(", ", tags);
        var width = Bounds.Width;
        // Antes de la primera pasada de layout no hay ancho contra el que medir: se pinta la lista
        // entera en una línea y el SizeChanged que viene a continuación recalcula el plegado.
        var overflows = width > 0 && MeasureWidth(fullText) > width;

        ExpandButton.IsVisible = overflows;
        var expanded = _expanded && overflows;
        TagsText.Text = !overflows || expanded ? fullText : CollapseToOneLine(tags, width);
        TagsText.MaxLines = expanded ? 0 : 1;
        ExpandButton.Content = LocalizationService.Instance[expanded ? "Kanban_Tags_ShowLess" : "Kanban_Tags_ShowAll"];
        ToolTip.SetTip(TagsText, fullText);
    }

    /// <summary>Mayor prefijo de etiquetas —tope de cinco— que cabe en una línea junto a su contador
    /// de restantes. Siempre devuelve al menos una etiqueta, aunque por sí sola no quepa: en ese caso
    /// el recorte del <c>TextBlock</c> se encarga del sobrante.</summary>
    private string CollapseToOneLine(IReadOnlyList<string> tags, double width)
    {
        for (var shown = Math.Min(MaxCollapsedTags, tags.Count); shown > 1; shown--)
        {
            var candidate = Compose(tags, shown);
            if (MeasureWidth(candidate) <= width) return candidate;
        }

        return Compose(tags, 1);
    }

    private static string Compose(IReadOnlyList<string> tags, int shown)
    {
        var visible = string.Join(", ", tags.Take(shown));
        var hidden = tags.Count - shown;
        return hidden > 0 ? $"{visible}  +{hidden}" : visible;
    }

    private double MeasureWidth(string text) =>
        new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection,
            new Typeface(TagsText.FontFamily, TagsText.FontStyle, TagsText.FontWeight),
            TagsText.FontSize, Brushes.Black).WidthIncludingTrailingWhitespace;
}
