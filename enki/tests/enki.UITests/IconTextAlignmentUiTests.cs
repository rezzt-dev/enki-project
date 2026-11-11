using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>La tinta del texto comparte centro con el icono aunque cambien las métricas de la fuente.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class IconTextAlignmentUiTests
{
    [Theory]
    [InlineData("Space Grotesk", 11.5, "APARIENCIA")]
    [InlineData("JetBrainsMono Nerd Font Mono", 11.5, "APARIENCIA")]
    [InlineData("sans-serif", 14, "Configuración y tamaño")]
    [InlineData("serif", 18, "AÑO, DISEÑO Y CONTRASEÑA")]
    [InlineData("Space Grotesk", 20, "Configuración y tamaño")]
    [InlineData("JetBrainsMono Nerd Font Mono", 20, "ACTUALIZACIONES Y\nDIAGNÓSTICO")]
    public void HorizontalRow_CentersInk_AndReactsToFontAndTextChanges(string family, double size, string caption)
    {
        HeadlessApp.EnsureInitialized();
        var icon = new MaterialIcon { Kind = MaterialIconKind.CogOutline, Width = 16, Height = 16 };
        var label = new TextBlock { Text = caption, FontFamily = Font(family), FontSize = size };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { icon, label } };
        var window = new Window { Width = 440, Height = 200, Content = row };
        window.Show();
        try
        {
            Layout(window);
            AssertCentered(icon, label);
            label.FontFamily = Font(family == "Space Grotesk" ? "JetBrainsMono Nerd Font Mono" : "Space Grotesk");
            label.FontSize = size * 1.5;
            label.FontWeight = FontWeight.Bold;
            label.Text = "VAULT Y ARCHIVOS\ncontraseña, año y diseño";
            Layout(window);
            AssertCentered(icon, label);
            var offset = icon.RenderTransform?.Value.M32 ?? 0;
            for (var i = 0; i < 4; i++) Layout(window);
            Assert.Equal(offset, icon.RenderTransform?.Value.M32 ?? 0, 3);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void GridRow_CentersWrappedLabel_AndIconInsideBadgeContainer()
    {
        HeadlessApp.EnsureInitialized();
        var icon = new MaterialIcon { Kind = MaterialIconKind.Update, Width = 16, Height = 16 };
        var wrapper = new Panel { Width = 20, Height = 20, Children = { icon } };
        var label = new TextBlock
        {
            Text = "ACTUALIZACIONES Y DIAGNÓSTICO", FontFamily = Font("JetBrainsMono Nerd Font Mono"),
            FontSize = 17, TextWrapping = TextWrapping.Wrap
        };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 4 };
        heading.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 4, Height = 4, Fill = Brushes.White });
        Grid.SetColumn(label, 1);
        heading.Children.Add(label);
        var textGroup = new StackPanel { Children = { heading, new Button { Content = "Acción auxiliar" } } };
        Grid.SetColumn(textGroup, 1);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*"), ColumnSpacing = 12,
            Width = 210, Children = { wrapper, textGroup } };
        row.Classes.Add("icon-text-row");
        var window = new Window { Width = 380, Height = 320,
            Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(1.5, 1.5), Child = row } };
        window.Show();
        try
        {
            Layout(window);
            Assert.True(label.TextLayout.TextLines.Count > 1);
            AssertCentered(icon, label, textGroup);
            row.Width = 150;
            Layout(window);
            AssertCentered(icon, label, textGroup);
            IconTextAlignment.SetIsEnabled(icon, false);
            Assert.True(icon.RenderTransform is null || icon.RenderTransform.Value.IsIdentity);
            IconTextAlignment.SetIsEnabled(icon, true);
            Layout(window);
            AssertCentered(icon, label, textGroup);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void IconOnlyButton_AndExplicitInlineTransform_ArePreserved()
    {
        HeadlessApp.EnsureInitialized();
        var iconOnly = new MaterialIcon { Kind = MaterialIconKind.Close, Width = 16, Height = 16 };
        var explicitTransform = new TranslateTransform(0, 4);
        var inline = new MaterialIcon { Kind = MaterialIconKind.InformationOutline, Width = 16, Height = 16,
            RenderTransform = explicitTransform };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children =
        {
            new Button { Content = iconOnly }, inline, new TextBlock { Text = "Información" }
        } };
        var window = new Window { Width = 300, Height = 100, Content = row };
        window.Show();
        try
        {
            Layout(window);
            Assert.True(iconOnly.RenderTransform is null || iconOnly.RenderTransform.Value.IsIdentity);
            Assert.Same(explicitTransform, inline.RenderTransform);
            Assert.Equal(4, explicitTransform.Y);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void ContextMenuIcon_UsesTheRenderedHeaderMetrics()
    {
        HeadlessApp.EnsureInitialized();
        var icon = new MaterialIcon { Kind = MaterialIconKind.FolderOutline, Width = 14, Height = 14 };
        var item = new MenuItem { Header = "ABRIR CARPETA", Icon = icon, FontSize = 18,
            FontFamily = Font("JetBrainsMono Nerd Font Mono") };
        var menu = new ContextMenu { Items = { item } };
        var anchor = new Border { ContextMenu = menu };
        var window = new Window { Width = 400, Height = 200, Content = anchor };
        window.Show();
        try
        {
            menu.Open(anchor);
            Layout(window);
            var label = item.GetVisualDescendants().OfType<TextBlock>()
                .First(text => text.Text == "ABRIR CARPETA");
            AssertCentered(icon, label);
        }
        finally { menu.Close(); window.Close(); }
    }

    private static FontFamily Font(string family) => family is "sans-serif" or "serif"
        ? new FontFamily(family) : new FontFamily("avares://enki/assets/fonts#" + family);

    private static void Layout(Window window) => window.GetLayoutManager()!.ExecuteLayoutPass();

    private static void AssertCentered(MaterialIcon icon, TextBlock label, Control? fallbackGroup = null)
    {
        var text = label.TextLayout;
        // El dibujante simulado de headless no proporciona tinta; comprobar entonces el layout.
        if (text.Extent <= 0)
        {
            var group = fallbackGroup ?? label;
            var center = icon.TranslatePoint(new Point(0, icon.Bounds.Height / 2), group)!.Value.Y;
            Assert.InRange(center - group.Bounds.Height / 2, -1, 1);
            return;
        }
        var inkCenter = label.Padding.Top + text.Height + text.OverhangAfter - text.Extent / 2;
        var iconCenter = icon.TranslatePoint(new Point(0, icon.Bounds.Height / 2), label)!.Value.Y;
        Assert.InRange(iconCenter - inkCenter, -0.1, 0.1);
    }
}
