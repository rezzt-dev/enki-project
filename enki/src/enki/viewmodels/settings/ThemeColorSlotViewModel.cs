using System;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>One editable color slot inside <see cref="ThemeEditorViewModel"/> — binds the Avalonia
/// <see cref="Color"/> to a <c>#RRGGBB</c> text so the user can edit either the picker or the hex field.
/// The <see cref="ResourceKey"/> matches <see cref="Services.ThemeCatalog"/>, which is what
/// <see cref="Services.ThemeRegistry"/> writes into application resources.</summary>
public sealed partial class ThemeColorSlotViewModel : ObservableObject
{
    public string ResourceKey { get; }

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public string Category
    {
        get => _category;
        private set => SetProperty(ref _category, value);
    }

    private string _displayName = "";
    private string _category = "";

    [ObservableProperty]
    private Color _color;

    [ObservableProperty]
    private string _hex = "#000000";

    public ThemeColorSlotViewModel(ThemeColorDefinition definition, string initialHex)
    {
        ResourceKey = definition.ResourceKey;
        _definition = definition;
        DisplayName = LocalizationService.Instance[definition.DisplayNameKey];
        Category = LocalizationService.Instance[$"Settings_Appearance_Theme_Category_{definition.CategoryKey}"];
        SetHex(initialHex);
    }

    private readonly ThemeColorDefinition _definition;

    /// <summary>Recarga las etiquetas localizadas tras un cambio de idioma de la UI.</summary>
    public void RefreshLabel()
    {
        DisplayName = LocalizationService.Instance[_definition.DisplayNameKey];
        Category = LocalizationService.Instance[$"Settings_Appearance_Theme_Category_{_definition.CategoryKey}"];
    }

    /// <summary>Updates both <see cref="Color"/> and <see cref="Hex"/> from a raw hex string, normalizing
    /// it to mayúsculas de 6 dígitos. Si el valor no es válido se mantiene el color previo para que la UI
    /// no salte mientras el usuario escribe.</summary>
    public void SetHex(string value)
    {
        var normalized = HexColorUtility.NormalizeHex(value) ?? Hex;
        if (normalized == Hex) return;

        Hex = normalized;
        Color = Color.Parse(normalized);
    }

    partial void OnColorChanged(Color value)
    {
        var newHex = HexColorUtility.ColorToHex(value);
        if (newHex != Hex) Hex = newHex;
    }

    partial void OnHexChanged(string value)
    {
        var normalized = HexColorUtility.NormalizeHex(value);
        if (normalized is null)
        {
            // Mientras el usuario escribe un HEX incompleto no se cambia Color ni se fuerza negro.
            return;
        }

        if (normalized != value)
        {
            Hex = normalized;
            return;
        }

        var parsed = Color.Parse(normalized);
        if (parsed != Color) Color = parsed;
    }
}
