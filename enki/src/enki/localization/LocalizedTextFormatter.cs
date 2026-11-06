using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using enki.Models;

namespace enki.Localization;

/// <summary>Resuelve textos persistidos al mostrarlos, incluidos plurales y argumentos que
/// también son textos de sistema. Los argumentos literales nunca se buscan en los recursos.</summary>
public static class LocalizedTextFormatter
{
    public static LocalizedText Format(string key, params object?[] arguments) => new()
    {
        Key = key,
        Arguments = arguments.Select(a => JsonSerializer.SerializeToElement(a)).ToArray()
    };

    public static LocalizedText PluralFormat(string key, int count, params object?[] arguments)
    {
        var text = Format(key, arguments);
        text.Count = count;
        return text;
    }

    public static LocalizedText Literal(string? value) => new() { Literal = value ?? string.Empty };

    public static LocalizedText Join(string separator, params LocalizedText[] values) => new()
    {
        Separator = separator,
        Arguments = values.Select(v => JsonSerializer.SerializeToElement(v)).ToArray()
    };

    /// <summary>Resuelve el texto en la cultura activa, o en <paramref name="culture"/> cuando se indica.
    /// El override existe para la pantalla de primer arranque: si guardar el idioma elegido falla, el aviso
    /// debe leerse en el idioma que el usuario acaba de pulsar, y ahí la cultura activa todavía es la de
    /// origen a propósito — un guardado fallido no cambia el idioma de la aplicación.</summary>
    public static string Resolve(LocalizedText? text, string fallback = "", CultureInfo? culture = null)
    {
        if (text is null) return fallback;
        if (text.Literal is not null) return text.Literal;
        culture ??= LocalizationService.Instance.CurrentCulture;
        var arguments = text.Arguments.Select(a => ResolveArgument(a, culture)).ToArray();
        if (text.Separator is not null) return string.Join(text.Separator, arguments);
        if (text.Key is null) return fallback;
        var key = text.Key;
        if (text.Count is { } count)
        {
            var singular = culture.TwoLetterISOLanguageName == "fr" ? count is 0 or 1 : count == 1;
            key += singular ? "_one" : "_other";
        }
        var template = Strings.ResourceManager.GetString(key, culture);
        if (template is null) return fallback;
        if (arguments.Length == 0) return template;
        try
        {
            return string.Format(culture, template, arguments);
        }
        catch (FormatException)
        {
            // Una notificación de otra versión puede tener una firma de argumentos distinta.
            return fallback;
        }
    }

    private static object? ResolveArgument(JsonElement value, CultureInfo culture) => value.ValueKind switch
    {
        JsonValueKind.Object => Resolve(value.Deserialize<LocalizedText>(), culture: culture),
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null => null,
        _ => value.ToString()
    };
}
