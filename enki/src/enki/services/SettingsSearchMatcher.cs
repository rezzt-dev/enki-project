using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace enki.Services;

/// <summary>Busca todas las palabras de la consulta en los textos traducidos de una opción.
/// Ignora mayúsculas, diacríticos y diferencias entre espacios o signos de puntuación.</summary>
public static class SettingsSearchMatcher
{
    public static bool Matches(string? query, IEnumerable<string> texts)
    {
        var words = Normalize(query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;
        var document = Normalize(string.Join(' ', texts));
        return words.All(word => document.Contains(word, StringComparison.Ordinal));
    }

    private static string Normalize(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            result.Append(char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : ' ');
        }
        return result.ToString();
    }
}
