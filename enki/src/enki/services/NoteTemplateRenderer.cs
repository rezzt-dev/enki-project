using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace enki.Services;

/// <summary>Resultado de expandir una plantilla: el Markdown ya sin variables y, si la plantilla
/// declaraba <c>{{cursor}}</c>, la posición donde debe quedar el cursor al abrir la nota
/// (<see cref="ViewModels.NotesViewModel.OpenNote"/> ya acepta ese desplazamiento).</summary>
/// <param name="Content">Markdown resultante, sin ninguna marca de plantilla.</param>
/// <param name="CaretOffset">Desplazamiento del cursor, o null si la plantilla no lo indicaba.</param>
public readonly record struct NoteTemplateRenderResult(string Content, int? CaretOffset);

/// <summary>Sustituye las variables <c>{{...}}</c> de una plantilla de notas
/// (<see cref="NoteTemplateService"/>) por su valor en el momento de crear la nota. Deliberadamente
/// estático y sin dependencias —ni vault, ni ajustes, ni reloj propio— para que sea comprobable de un
/// vistazo y desde los tests sin montar nada: todo lo variable (el título, la fecha) entra por parámetro.
///
/// <para>Gramática de una variable: <c>{{nombre[±N unidad][:formato]}}</c>, con espacios opcionales en
/// cualquier punto. El nombre admite su forma en inglés (canónica, la que documenta
/// <c>.docs/project-info/plantillas-de-notas.md</c>) y su equivalente en castellano, porque las plantillas
/// las escribe el usuario a mano y el resto de identificadores que teclea en la app —los comandos del menú
/// <c>/</c> del editor— también están en castellano.</para>
///
/// <para><b>Una variable que no se reconoce se deja tal cual.</b> Ni se borra ni se convierte en texto
/// vacío: un <c>{{pendiente}}</c> que el usuario todavía no ha rellenado, o una errata en el nombre, se ve
/// en la nota y se puede corregir, en vez de desaparecer sin dejar rastro de que allí había algo. Mismo
/// criterio para un formato inválido (<c>{{date:ZZZ}}</c>), que además evita que una plantilla mal escrita
/// tumbe la creación de la nota con una <see cref="FormatException"/>.</para></summary>
public static class NoteTemplateRenderer
{
    /// <summary>Formato de <c>{{date}}</c> sin formato explícito: ISO 8601 corto, el mismo nombre que
    /// <c>NotesViewModel</c> ya usaba para la nota diaria antes de existir las plantillas, y por el mismo
    /// motivo — ordena alfabéticamente igual que cronológicamente y no depende del idioma activo.</summary>
    public const string DefaultDateFormat = "yyyy-MM-dd";

    public const string DefaultTimeFormat = "HH:mm";

    public const string DefaultDateTimeFormat = "yyyy-MM-dd HH:mm";

    /// <summary>Un grupo <c>format</c> no codicioso y limitado a caracteres que no sean <c>}</c> permite
    /// que el propio formato lleve dos puntos (<c>{{time:HH:mm:ss}}</c>) sin ambigüedad: el primer <c>:</c>
    /// separa nombre y formato, el resto pertenece al formato.</summary>
    private static readonly Regex PlaceholderPattern = new(
        @"\{\{\s*(?<name>\p{L}+)\s*(?:(?<sign>[+-])\s*(?<amount>\d+)\s*(?<unit>\p{L}+))?\s*(?::(?<format>[^}]*))?\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Expande <paramref name="template"/> con <paramref name="title"/> como valor de
    /// <c>{{title}}</c> y <paramref name="now"/> (por defecto, la hora local actual) como referencia
    /// temporal de todas las variables de fecha — un único instante para toda la plantilla, así que una
    /// plantilla con <c>{{date}}</c> y <c>{{time}}</c> nunca queda a caballo entre dos días si se expande
    /// justo a medianoche.</summary>
    public static NoteTemplateRenderResult Render(string? template, string title, DateTime? now = null)
    {
        if (string.IsNullOrEmpty(template)) return new NoteTemplateRenderResult(string.Empty, null);

        var reference = now ?? DateTime.Now;
        var builder = new StringBuilder(template.Length);
        int? caretOffset = null;
        var lastIndex = 0;

        foreach (Match match in PlaceholderPattern.Matches(template))
        {
            builder.Append(template, lastIndex, match.Index - lastIndex);
            lastIndex = match.Index + match.Length;

            var name = Canonicalize(match.Groups["name"].Value);
            if (name == "cursor")
            {
                // Solo la primera marca fija el cursor; las demás se retiran igualmente para que no queden
                // impresas en la nota, porque un cursor solo puede estar en un sitio.
                caretOffset ??= builder.Length;
                continue;
            }

            builder.Append(Resolve(name, match, title, reference));
        }

        builder.Append(template, lastIndex, template.Length - lastIndex);
        return new NoteTemplateRenderResult(builder.ToString(), caretOffset);
    }

    /// <summary>Valor de una variable ya reconocida, o el texto original de la variable
    /// (<c>match.Value</c>) si el nombre no existe o el formato no es válido.</summary>
    private static string Resolve(string name, Match match, string title, DateTime reference)
    {
        if (name == "title") return title;
        if (name is "uuid") return Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);

        var format = match.Groups["format"];
        var explicitFormat = format.Success ? format.Value.Trim() : null;

        var moment = ApplyOffset(reference, match, out var validOffset);
        if (!validOffset) return match.Value;

        var (defaultFormat, useInvariant) = name switch
        {
            "date" => (DefaultDateFormat, true),
            "time" => (DefaultTimeFormat, true),
            "datetime" => (DefaultDateTimeFormat, true),
            // Nombres de día y mes: texto que el usuario lee, no un identificador técnico, así que siguen
            // al idioma activo igual que el resto de la interfaz.
            "weekday" => ("dddd", false),
            "month" => ("MMMM", false),
            "year" => ("yyyy", true),
            _ => (null, true)
        };
        if (defaultFormat is null) return match.Value;

        var effectiveFormat = string.IsNullOrEmpty(explicitFormat) ? defaultFormat : explicitFormat;
        // Un formato de un solo carácter lo interpreta .NET como especificador estándar ("d" = fecha corta,
        // "y" = año y mes), no como el patrón literal que el usuario tenía en mente al escribir
        // {{date:d}}. El prefijo "%" lo fuerza a personalizado, que es la lectura obvia dentro de una
        // plantilla donde todos los demás formatos también son patrones.
        if (effectiveFormat.Length == 1) effectiveFormat = "%" + effectiveFormat;

        // La cultura solo manda en el texto (nombres de día/mes y separadores): con formato explícito se
        // usa la del idioma activo, porque quien escribe {{date:dddd d 'de' MMMM}} quiere su idioma.
        var culture = useInvariant && string.IsNullOrEmpty(explicitFormat)
            ? CultureInfo.InvariantCulture
            : CultureInfo.CurrentCulture;

        try
        {
            return moment.ToString(effectiveFormat, culture);
        }
        catch (FormatException)
        {
            return match.Value;
        }
    }

    /// <summary>Aplica el desplazamiento <c>±N unidad</c> de la variable, si lo trae. Una unidad
    /// desconocida (<c>{{date+2x}}</c>) no se ignora en silencio: marca <paramref name="valid"/> como falso
    /// para que la variable entera se deje sin expandir, en vez de producir la fecha de hoy y hacer creer
    /// al usuario que su desplazamiento se aplicó.</summary>
    private static DateTime ApplyOffset(DateTime reference, Match match, out bool valid)
    {
        valid = true;
        if (!match.Groups["amount"].Success) return reference;

        if (!int.TryParse(match.Groups["amount"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
        {
            valid = false;
            return reference;
        }

        if (match.Groups["sign"].Value == "-") amount = -amount;

        return Normalize(match.Groups["unit"].Value) switch
        {
            "d" or "dia" or "dias" or "day" or "days" => reference.AddDays(amount),
            "w" or "s" or "semana" or "semanas" or "week" or "weeks" => reference.AddDays(amount * 7),
            "m" or "mes" or "meses" or "month" or "months" => reference.AddMonths(amount),
            "y" or "a" or "ano" or "anos" or "year" or "years" => reference.AddYears(amount),
            "h" or "hora" or "horas" or "hour" or "hours" => reference.AddHours(amount),
            "min" or "minuto" or "minutos" or "minute" or "minutes" => reference.AddMinutes(amount),
            _ => Invalid(out valid, reference)
        };
    }

    private static DateTime Invalid(out bool valid, DateTime reference)
    {
        valid = false;
        return reference;
    }

    /// <summary>Nombre de variable normalizado: <see cref="Normalize"/> más los sinónimos en castellano
    /// reducidos a su forma canónica en inglés, de modo que <c>{{título}}</c>, <c>{{titulo}}</c> y
    /// <c>{{TITLE}}</c> sean la misma variable. Las unidades de desplazamiento no pasan por aquí —usan
    /// <see cref="Normalize"/> directamente— porque comparten letras con los nombres de variable
    /// (<c>m</c> de mes, <c>a</c> de año) y no deben arrastrar sus sinónimos.</summary>
    private static string Canonicalize(string raw) => Normalize(raw) switch
    {
        "titulo" or "nombre" => "title",
        "fecha" => "date",
        "hora" => "time",
        "fechahora" or "fecha_hora" => "datetime",
        "diasemana" => "weekday",
        "mes" => "month",
        "ano" => "year",
        "guid" => "uuid",
        var other => other
    };

    /// <summary>Minúsculas y sin acentos, para que ni el bloqueo de mayúsculas ni la tilde de
    /// <c>{{título}}</c>/<c>{{año}}</c> decidan si una variable se reconoce.</summary>
    private static string Normalize(string raw) => raw.Trim().ToLowerInvariant()
        .Replace('á', 'a').Replace('é', 'e').Replace('í', 'i').Replace('ó', 'o').Replace('ú', 'u')
        .Replace('ñ', 'n');
}
