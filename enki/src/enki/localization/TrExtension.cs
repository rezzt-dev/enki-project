using System;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace enki.Localization;

/// <summary>XAML shorthand for binding to <see cref="LocalizationService"/>'s indexer —
/// <c>{loc:Tr Nav_Dashboard}</c> instead of the equivalent but longer
/// <c>{Binding [Nav_Dashboard], Source={x:Static loc:LocalizationService.Instance}}</c>. See
/// docs/agent-docs/internacionalizacion-i18n.md §1.3. <see cref="Converter"/> lets fixed UI chrome opt
/// into e.g. <c>{loc:Tr Nav_Dashboard, Converter={StaticResource UpperCaseConverter}}</c> for the
/// "todo en mayúsculas" nav/button/badge/label rule (docs/sistema-diseno-web.md §0/§2.2/§7.2/§7.6) —
/// never apply it to a key whose value can be user-authored content.</summary>
public sealed class TrExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public IValueConverter? Converter { get; set; }

    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
            Converter = Converter,
        };
}
