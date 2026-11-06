using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Localization;

namespace enki.Controls;

/// <summary>Adapta los textos internos del selector de Avalonia al idioma de Enki, conservando
/// su edición de colores. Las muestras usan el código hexadecimal como nombre universal.</summary>
public sealed class LocalizedColorPicker : ColorPicker
{
    private Flyout? _flyout;
    private Control? _content;

    protected override Type StyleKeyOverride => typeof(ColorPicker);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_flyout is not null) _flyout.Opened -= OnOpened;
        if (_content is not null) _content.RemoveHandler(SelectingItemsControl.SelectionChangedEvent, OnSelectionChanged);
        base.OnApplyTemplate(e);
        _flyout = this.GetVisualDescendants().OfType<DropDownButton>().FirstOrDefault()?.Flyout as Flyout;
        _content = _flyout?.Content as Control;
        if (_flyout is not null) _flyout.Opened += OnOpened;
        _content?.AddHandler(SelectingItemsControl.SelectionChangedEvent, OnSelectionChanged, RoutingStrategies.Bubble);
    }

    private void OnOpened(object? sender, EventArgs e) => UpdateLabels();

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(UpdateLabels);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColorModelProperty) Dispatcher.UIThread.Post(UpdateLabels);
    }

    private void UpdateLabels()
    {
        if (_content is null) return;
        var rgb = ColorModel == ColorModel.Rgba;
        foreach (var control in _content.GetVisualDescendants().OfType<Control>())
        {
            if (control is Panel && control.DataContext is Color color)
            {
                var hex = color.ToString();
                ToolTip.SetTip(control, hex);
                AutomationProperties.SetName(control, hex);
            }
            var key = control.Name switch
            {
                "PART_HexTextBox" => "ColorPicker_Hex",
                "ColorSpectrum" => "ColorPicker_Spectrum",
                "ColorSpectrumThirdComponentSlider" => "ColorPicker_Component",
                "ColorSpectrumAlphaSlider" or "AlphaComponentSlider" or "AlphaComponentNumericUpDown" => "ColorPicker_Alpha",
                "Component1Slider" or "Component1NumericUpDown" => rgb ? "ColorPicker_Red" : "ColorPicker_Hue",
                "Component2Slider" or "Component2NumericUpDown" => rgb ? "ColorPicker_Green" : "ColorPicker_Saturation",
                "Component3Slider" or "Component3NumericUpDown" => rgb ? "ColorPicker_Blue" : "ColorPicker_Value",
                _ => null
            };
            if (key is not null)
                control.Bind(AutomationProperties.NameProperty,
                    new Binding($"[{key}]") { Source = LocalizationService.Instance, Mode = BindingMode.OneWay });
        }
    }
}
