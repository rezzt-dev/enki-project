namespace enki.Models;

/// <summary>One customizable color slot of Enki's design system — the fixed surface a custom theme's CSS
/// file is validated against (<c>Services.ThemeCssParser</c>). <see cref="CssVariable"/> is the
/// <c>--kebab-case</c> custom property name the shipped template exposes; <see cref="ResourceKey"/> is the
/// matching Avalonia <c>Color</c> resource key already declared per theme variant in
/// <c>styles/Colors.axaml</c> (<c>ThemeDictionaries</c>). <see cref="DisplayNameKey"/> points to a localized
/// label shown in the in-app theme editor; <see cref="CategoryKey"/> groups related slots together.
/// A custom theme may now override every token, including accent and semantic feedback colors.</summary>
public sealed record ThemeColorDefinition(
    string CssVariable,
    string ResourceKey,
    string DefaultHex,
    string DisplayNameKey,
    string CategoryKey);
