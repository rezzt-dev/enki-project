using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;

namespace enki.Views;

/// <summary>Etiqueta flotante del elemento arrastrado dentro de las vistas del explorador.</summary>
internal sealed class ExplorerDragGhost
{
    private readonly Border _preview;
    private readonly MaterialIcon _icon;
    private readonly TextBlock _name;
    private readonly Control _host;
    private readonly DataFormat<string> _format;

    public ExplorerDragGhost(Control host, Grid surface, DataFormat<string> format)
    {
        _host = host;
        _format = format;
        _icon = new MaterialIcon { Width = 16, Height = 16 };
        _name = new TextBlock { MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        var content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(_icon);
        content.Children.Add(_name);
        _preview = new Border
        {
            IsVisible = false,
            IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Padding = new Thickness(10, 6),
            Opacity = 0.92,
            Child = content
        };
        _preview.Classes.Add("dropdown-popover");
        var layer = new Canvas { IsHitTestVisible = false };
        layer.Children.Add(_preview);
        layer.SetValue(Panel.ZIndexProperty, 1000);
        Grid.SetColumnSpan(layer, Math.Max(1, surface.ColumnDefinitions.Count));
        Grid.SetRowSpan(layer, Math.Max(1, surface.RowDefinitions.Count));
        surface.Children.Add(layer);
        host.AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        host.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        host.AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(_format) is not { } path) { Hide(); return; }
        _icon.Kind = Directory.Exists(path) ? MaterialIconKind.FolderOutline : MaterialIconKind.FileOutline;
        _name.Text = Path.GetFileName(path);
        var point = e.GetPosition(_host);
        Canvas.SetLeft(_preview, point.X + 18);
        Canvas.SetTop(_preview, point.Y + 18);
        _preview.IsVisible = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        var point = e.GetPosition(_host);
        if (!_host.Bounds.Contains(point)) Hide();
    }

    private void OnDrop(object? sender, DragEventArgs e) => Hide();

    public void Hide() => _preview.IsVisible = false;
}
