using System;
using Avalonia.Media;

namespace enki.Services;

/// <summary>Normaliza códigos de color HEX para el sistema de temas: acepta los formatos #RGB y #RRGGBB
/// (con o sin '#', mayúsculas/minúsculas) y los convierte a mayúsculas de 6 dígitos. No acepta formatos
/// con canal alpha (#ARGB/#AARRGGBB) — ver <see cref="NormalizeHex"/>. Si el formato no es válido
/// devuelve <c>null</c> en vez de forzar un color por defecto, para que el llamador decida cómo
/// presentar el error.</summary>
public static class HexColorUtility
{
    /// <summary>Intenta normalizar <paramref name="hex"/> a un HEX de 6 dígitos mayúsculas con '#'.
    /// Acepta espacios en blanco alrededor, con o sin '#', y formatos de 3 o 6 caracteres hex.
    /// No se aceptan formatos con alpha porque el catálogo de colores de Enki trabaja con
    /// colores opacos para superficies, texto y estado.</summary>
    public static string? NormalizeHex(string? hex)
    {
        var trimmed = hex?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed)) return null;

        if (trimmed.Length > 0 && trimmed[0] == '#') trimmed = trimmed.AsSpan(1).ToString();
        if (trimmed.Length == 0) return null;

        // Rechazar cualquier carácter no hexadecimal.
        foreach (var c in trimmed)
        {
            if (!char.IsAsciiHexDigit(c)) return null;
        }

        ReadOnlySpan<char> rgb;
        switch (trimmed.Length)
        {
            case 3:
                // #RGB → #RRGGBB
                rgb = ExpandShorthand(trimmed);
                break;
            case 6:
                rgb = trimmed.AsSpan();
                break;
            default:
                return null;
        }

        return $"#{rgb.ToString().ToUpperInvariant()}";
    }

    /// <summary>Convierte un <see cref="Color"/> de Avalonia a HEX de 6 dígitos mayúsculas.
    /// El canal alpha se descarta porque los temas de Enki no lo soportan.</summary>
    public static string ColorToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string ExpandShorthand(ReadOnlySpan<char> shorthand)
    {
        Span<char> expanded = stackalloc char[6];
        for (var i = 0; i < 3; i++)
        {
            expanded[i * 2] = shorthand[i];
            expanded[i * 2 + 1] = shorthand[i];
        }
        return new string(expanded);
    }

    /// <summary>WCAG 2.x contrast ratio (1:1 to 21:1) between two HEX colors, for the editor de temas
    /// personalizados' low-contrast warning (análisis alfa-3 §4.5, Alta — un tema sin ninguna validación de
    /// contraste puede dejar la interfaz completamente ilegible). Returns <c>false</c> without setting
    /// <paramref name="ratio"/> if either input fails <see cref="NormalizeHex"/>.</summary>
    public static bool TryGetContrastRatio(string? hexA, string? hexB, out double ratio)
    {
        ratio = 0;
        var normalizedA = NormalizeHex(hexA);
        var normalizedB = NormalizeHex(hexB);
        if (normalizedA is null || normalizedB is null) return false;

        var luminanceA = RelativeLuminance(Color.Parse(normalizedA));
        var luminanceB = RelativeLuminance(Color.Parse(normalizedB));
        var lighter = Math.Max(luminanceA, luminanceB);
        var darker = Math.Min(luminanceA, luminanceB);
        ratio = (lighter + 0.05) / (darker + 0.05);
        return true;
    }

    private static double RelativeLuminance(Color color)
    {
        return 0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);

        static double Linearize(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }
}
