using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using enki.Localization;
using enki.Services;
using enki.ViewModels;

namespace enki.Controls;

/// <summary>Filtra un bloque declarativo sin duplicar sus controles ni alterar los bindings internos.
/// Las claves pertenecen al contenido; la consulta y la sección seleccionada se heredan del formulario.</summary>
public sealed class SettingsSearchOption : Decorator
{
    public static readonly AttachedProperty<string> QueryProperty =
        AvaloniaProperty.RegisterAttached<SettingsSearchOption, Control, string>("Query", string.Empty, inherits: true);
    public static readonly AttachedProperty<SettingsSection> SelectedSectionProperty =
        AvaloniaProperty.RegisterAttached<SettingsSearchOption, Control, SettingsSection>("SelectedSection", inherits: true);
    public static readonly StyledProperty<string> ResourceKeysProperty =
        AvaloniaProperty.Register<SettingsSearchOption, string>(nameof(ResourceKeys), string.Empty);
    public static readonly StyledProperty<bool> MatchDescendantsProperty =
        AvaloniaProperty.Register<SettingsSearchOption, bool>(nameof(MatchDescendants));
    public static readonly StyledProperty<string> AdditionalTextProperty =
        AvaloniaProperty.Register<SettingsSearchOption, string>(nameof(AdditionalText), string.Empty);
    public static readonly StyledProperty<SettingsSection?> SectionProperty =
        AvaloniaProperty.Register<SettingsSearchOption, SettingsSection?>(nameof(Section));

    public static string GetQuery(Control control) => control.GetValue(QueryProperty);
    public static void SetQuery(Control control, string value) => control.SetValue(QueryProperty, value);
    public static SettingsSection GetSelectedSection(Control control) => control.GetValue(SelectedSectionProperty);
    public static void SetSelectedSection(Control control, SettingsSection value) => control.SetValue(SelectedSectionProperty, value);

    /// <summary>Los grupos se muestran si alguna opción coincide; evita combinar palabras de opciones distintas.</summary>
    public bool MatchDescendants
    {
        get => GetValue(MatchDescendantsProperty);
        set => SetValue(MatchDescendantsProperty, value);
    }

    public string ResourceKeys
    {
        get => GetValue(ResourceKeysProperty);
        set => SetValue(ResourceKeysProperty, value);
    }

    /// <summary>Textos procedentes de listas dinámicas, ya traducidos por su viewmodel.</summary>
    public string AdditionalText
    {
        get => GetValue(AdditionalTextProperty);
        set => SetValue(AdditionalTextProperty, value);
    }

    public SettingsSection? Section
    {
        get => GetValue(SectionProperty);
        set => SetValue(SectionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChildProperty)
        {
            if (change.OldValue is Control previous) previous.PropertyChanged -= ChildVisibilityChanged;
            if (change.NewValue is Control current) current.PropertyChanged += ChildVisibilityChanged;
            Refresh();
        }
        if (change.Property == QueryProperty || change.Property == SelectedSectionProperty ||
            change.Property == MatchDescendantsProperty || change.Property == AdditionalTextProperty || change.Property == ResourceKeysProperty || change.Property == SectionProperty)
            Refresh();
        if (change.Property == IsVisibleProperty)
        {
            foreach (var ancestor in this.GetLogicalAncestors().OfType<SettingsSearchOption>())
                if (ancestor.MatchDescendants) ancestor.Refresh();
        }
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

    private void ChildVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) Refresh();
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var query = GetQuery(this);
        if (string.IsNullOrWhiteSpace(query))
        {
            IsVisible = Section is null || Section == GetSelectedSection(this);
            return;
        }

        if (Child is { IsVisible: false })
        {
            IsVisible = false;
            return;
        }

        var context = ResourceKeys.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(key => LocalizationService.Instance[key]).ToArray();
        // Cada línea representa una opción dinámica; no mezclamos, por ejemplo, dos atajos distintos.
        var dynamicMatches = AdditionalText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(text => SettingsSearchMatcher.Matches(query, context.Append(text)));
        IsVisible = MatchDescendants
            ? dynamicMatches || this.GetLogicalDescendants().OfType<SettingsSearchOption>().Any(option =>
                option.IsVisible && option.GetLogicalAncestors().TakeWhile(ancestor => ancestor != this)
                    .OfType<Control>().All(ancestor => ancestor is SettingsSearchOption || ancestor.IsVisible))
            : dynamicMatches || SettingsSearchMatcher.Matches(query, context);
    }
}
