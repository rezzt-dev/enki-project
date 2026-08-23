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
}
