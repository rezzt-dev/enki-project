using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using enki.Models;

namespace enki.Services;

/// <summary>Traduce en los dos sentidos entre una fecha y el nombre de la nota diaria de ese día: hacia
/// delante para saber cómo debe llamarse la nota de ayer, de mañana o de cualquiera de los días del
/// calendario de constancia, y hacia atrás para reconocer que la nota abierta en el editor <b>es</b> la
/// nota diaria de un día concreto y poder ofrecer la navegación día anterior / día siguiente.
///
/// <para>Sin plantilla configurada el nombre es el <c>yyyy-MM-dd</c> invariante de siempre. Con una
/// plantilla que declare <c>title</c> manda su patrón, y entonces la vuelta —de nombre a fecha— se resuelve
/// convirtiendo ese mismo patrón en un formato de fecha de .NET y haciendo un
/// <see cref="DateTime.TryParseExact(string, string, IFormatProvider, DateTimeStyles, out DateTime)"/>:
/// así <c>title: "Diario {{date}}"</c> reconoce "Diario 2026-09-18" sin que nadie tenga que mantener una
/// segunda gramática en paralelo a la de <see cref="NoteTemplateRenderer"/>.</para>
///
/// <para><b>Toda lectura inversa se valida con una ida y vuelta</b> (<see cref="TryParseDate"/>): la fecha
/// leída solo se acepta si volver a generar el título con ella devuelve exactamente el nombre de partida.
/// Es la red que cubre todo lo que un patrón puede hacer y este análisis no: un carácter que
/// <see cref="VaultService"/> sanea al crear el archivo, una cultura distinta a la que escribió el nombre,
/// un patrón sin día pero con mes y año. Ante la duda, la nota simplemente no se considera nota diaria —
/// que es exactamente lo que era antes de existir esta funcionalidad— en vez de navegarse a un día
/// equivocado.</para></summary>
public static class DailyNoteNaming
{
    /// <summary>El nombre de la nota diaria sin plantilla: ISO 8601 corto e invariante, el mismo que
    /// Documentos ya usaba antes de existir las plantillas (ver
    /// <see cref="NoteTemplateRenderer.DefaultDateFormat"/>) — ordena alfabéticamente igual que
    /// cronológicamente y no cambia al cambiar de idioma.</summary>
    public const string FallbackTitleFormat = NoteTemplateRenderer.DefaultDateFormat;

    /// <summary>Misma gramática de variable que <see cref="NoteTemplateRenderer"/>
    /// (<c>{{nombre[±N unidad][:formato]}}</c>): lo que se reconoce aquí para la lectura inversa es
    /// justamente lo que aquel expande al generar el nombre.</summary>
    private static readonly Regex PlaceholderPattern = new(
        @"\{\{\s*(?<name>\p{L}+)\s*(?:(?<sign>[+-])\s*(?<amount>\d+)\s*(?<unit>\p{L}+))?\s*(?::(?<format>[^}]*))?\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Nombre de la nota diaria de <paramref name="date"/>: el que dicte el <c>title</c> de
    /// <paramref name="template"/>, o <see cref="FallbackTitleFor"/> si no hay plantilla o no declara
    /// ninguno.</summary>
    public static string TitleFor(DateOnly date, NoteTemplate? template)
    {
        var fallback = FallbackTitleFor(date);
        if (template?.TitlePattern is not { } pattern) return fallback;

        return NoteTemplateService.ResolveTitle(StripOffsets(pattern), fallback, ToReference(date));
    }

    public static string FallbackTitleFor(DateOnly date) =>
        date.ToString(FallbackTitleFormat, CultureInfo.InvariantCulture);

    /// <summary>Lee la fecha que codifica <paramref name="title"/>, si es el nombre de una nota diaria.
    ///
    /// <para>Se prueba primero el patrón de la plantilla y después el <c>yyyy-MM-dd</c> de siempre, no solo
    /// uno u otro: un vault que lleva meses con notas <c>2026-09-18.md</c> y al que hoy se le configura una
    /// plantilla con otro <c>title</c> debe seguir reconociendo las notas diarias que ya tenía, en vez de
    /// que el calendario de constancia se vacíe de golpe.</para></summary>
    public static bool TryParseDate(string? title, NoteTemplate? template, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(title)) return false;

        var trimmed = title.Trim();
        if (template?.TitlePattern is { } pattern && TryParseWithPattern(trimmed, pattern, template, out date)) return true;

        if (!DateOnly.TryParseExact(trimmed, FallbackTitleFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var fallbackDate))
            return false;

        date = fallbackDate;
        return true;
    }

    /// <summary>Si <paramref name="title"/> encaja con el patrón de título de la plantilla. Se intenta con
    /// la cultura activa y con la invariante porque el propio renderizador mezcla las dos —los nombres de
    /// día y mes siguen al idioma de la interfaz, las fechas numéricas sin formato explícito no—, y un
    /// único <c>ParseExact</c> no puede reproducir esa mezcla; la validación de ida y vuelta decide cuál de
    /// las dos lecturas (si alguna) es la buena.</summary>
    private static bool TryParseWithPattern(string title, string pattern, NoteTemplate template, out DateOnly date)
    {
        date = default;
        if (!TryBuildParseFormat(pattern, out var format)) return false;

        foreach (var culture in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture })
        {
            if (!DateTime.TryParseExact(title, format, culture, DateTimeStyles.None, out var parsed)) continue;

            var candidate = DateOnly.FromDateTime(parsed);
            // La ida y vuelta es la validación real: el formato de arriba es una aproximación al patrón, y
            // solo generar de nuevo el nombre con la fecha leída demuestra que la lectura era correcta.
            if (!string.Equals(TitleFor(candidate, template), title, StringComparison.Ordinal)) continue;

            date = candidate;
            return true;
        }

        return false;
    }

    /// <summary>Convierte el patrón de título de una plantilla en un formato de fecha personalizado de
    /// .NET: el texto literal se entrecomilla y cada variable aporta su formato
    /// (<c>{{date}}</c> → <c>yyyy-MM-dd</c>, <c>{{date:dd/MM/yyyy}}</c> → <c>dd/MM/yyyy</c>, …).
    ///
    /// <para>Devuelve <see langword="false"/> —y la nota no se considera nota diaria— cuando el patrón
    /// contiene algo que no identifica un día: una variable que no es de fecha (<c>{{title}}</c>,
    /// <c>{{uuid}}</c>) o un conjunto de variables que no llega a fijar día, mes y año
    /// (<c>{{month}} {{year}}</c>, que dos días distintos compartirían).</para></summary>
    private static bool TryBuildParseFormat(string rawPattern, out string format)
    {
        // El mismo patrón que se usó para generar el nombre (ver StripOffsets), no el original.
        var pattern = StripOffsets(rawPattern);
        format = string.Empty;
        var builder = new StringBuilder();
        var tokens = new StringBuilder();
        var lastIndex = 0;

        foreach (Match match in PlaceholderPattern.Matches(pattern))
        {
            AppendLiteral(builder, pattern[lastIndex..match.Index]);
            lastIndex = match.Index + match.Length;

            var explicitFormat = match.Groups["format"].Success ? match.Groups["format"].Value.Trim() : null;
            var token = TokenFor(Canonicalize(match.Groups["name"].Value), explicitFormat);
            if (token is null) return false;

            builder.Append(token);
            tokens.Append(token);
        }

        AppendLiteral(builder, pattern[lastIndex..]);

        var tokenText = tokens.ToString();
        if (!tokenText.Contains('y') || !tokenText.Contains('M') || !tokenText.Contains('d')) return false;

        format = builder.ToString();
        return format.Length > 0;
    }

    /// <summary>El patrón de título sin sus desplazamientos: <c>{{date+1d}}</c> pasa a ser <c>{{date}}</c>.
    ///
    /// <para>Un desplazamiento tiene todo el sentido en el <b>cuerpo</b> de una plantilla —un enlace a la
    /// nota de ayer, una revisión programada para dentro de una semana— pero ninguno en el <b>nombre</b> de
    /// la nota diaria: el nombre es lo que dice de qué día es la nota, así que llamarla con la fecha de
    /// mañana rompería lo único que hace posible encontrarla, navegar de un día a otro y pintar el
    /// calendario de constancia. En vez de rechazar esas plantillas (dejando al usuario con una nota diaria
    /// que no se reconoce a sí misma y sin ninguna pista de por qué), se ignora el desplazamiento solo
    /// aquí, en el título.</para></summary>
    private static string StripOffsets(string pattern) => PlaceholderPattern.Replace(pattern, match =>
    {
        if (!match.Groups["amount"].Success) return match.Value;

        var format = match.Groups["format"];
        return format.Success
            ? $"{{{{{match.Groups["name"].Value}:{format.Value}}}}}"
            : $"{{{{{match.Groups["name"].Value}}}}}";
    });

    /// <summary>Formato que aporta una variable al patrón de lectura, o null si esa variable no describe
    /// una fecha. Los valores por defecto son los mismos que aplica <see cref="NoteTemplateRenderer"/> al
    /// generar el nombre.</summary>
    private static string? TokenFor(string name, string? explicitFormat) => name switch
    {
        "date" => string.IsNullOrEmpty(explicitFormat) ? NoteTemplateRenderer.DefaultDateFormat : explicitFormat,
        "datetime" => string.IsNullOrEmpty(explicitFormat) ? NoteTemplateRenderer.DefaultDateTimeFormat : explicitFormat,
        "time" => string.IsNullOrEmpty(explicitFormat) ? NoteTemplateRenderer.DefaultTimeFormat : explicitFormat,
        "weekday" => string.IsNullOrEmpty(explicitFormat) ? "dddd" : explicitFormat,
        "month" => string.IsNullOrEmpty(explicitFormat) ? "MMMM" : explicitFormat,
        "year" => string.IsNullOrEmpty(explicitFormat) ? "yyyy" : explicitFormat,
        _ => null
    };

    /// <summary>Añade texto literal al formato entrecomillado, para que unas iniciales cualesquiera del
    /// nombre de la plantilla (la "D" de "Diario") no se interpreten como especificadores de fecha.</summary>
    private static void AppendLiteral(StringBuilder builder, string literal)
    {
        if (literal.Length == 0) return;
        builder.Append('\'')
            .Append(literal.Replace("\\", "\\\\").Replace("'", "\\'"))
            .Append('\'');
    }

    /// <summary>Misma normalización de nombres que <see cref="NoteTemplateRenderer"/> —minúsculas, sin
    /// acentos y con los sinónimos en castellano reducidos a su forma canónica—, para que
    /// <c>title: "{{fecha}}"</c> se lea igual que <c>title: "{{date}}"</c>.</summary>
    private static string Canonicalize(string raw)
    {
        var normalized = raw.Trim().ToLowerInvariant()
            .Replace('á', 'a').Replace('é', 'e').Replace('í', 'i').Replace('ó', 'o').Replace('ú', 'u')
            .Replace('ñ', 'n');

        return normalized switch
        {
            "fecha" => "date",
            "hora" => "time",
            "fechahora" or "fecha_hora" => "datetime",
            "diasemana" => "weekday",
            "mes" => "month",
            "ano" => "year",
            _ => normalized
        };
    }

    /// <summary>La fecha como instante para el renderizador. Mediodía y no medianoche a propósito: una
    /// plantilla cuyo título use <c>{{time}}</c> no puede reproducirse de todas formas (la hora de creación
    /// no se recupera del nombre), y el mediodía deja el día estable frente a cualquier ajuste horario.</summary>
    private static DateTime ToReference(DateOnly date) => date.ToDateTime(new TimeOnly(12, 0));
}
