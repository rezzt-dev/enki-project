namespace enki.Models;

/// <summary>Whether a <see cref="BuiltInThemeDefinition"/> reads as a dark or light palette — used to
/// group the built-in theme picker (Ajustes › Apariencia) into "Oscuros"/"Claros" sections so a user
/// scanning a dozen presets can find the polarity they want without reading every name.</summary>
public enum ThemeAppearance
{
    Dark,
    Light,
}
