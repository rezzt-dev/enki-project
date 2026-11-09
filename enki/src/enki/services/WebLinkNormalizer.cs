using System;

namespace enki.Services;

/// <summary>Por qué un texto no se acepta como enlace web de una tarjeta. <see cref="None"/> es el único
/// valor que significa "adelante".</summary>
public enum WebLinkError
{
    None,

    /// <summary>El campo está vacío o solo tiene espacios.</summary>
    Empty,

    /// <summary>No es una dirección web absoluta: lleva espacios, no tiene servidor, o usa un esquema que
    /// no es <c>http</c>/<c>https</c> (<c>javascript:</c>, <c>file:</c>, <c>mailto:</c>…).</summary>
    Invalid
}

/// <summary>
/// Convierte lo que el usuario escribe en el campo «enlaces» de una tarjeta Kanban en una URL web absoluta,
/// o lo rechaza con un motivo concreto.
///
/// <para>Los enlaces de una tarjeta están pensados para páginas web, así que solo se aceptan <c>http</c> y
/// <c>https</c> — un subconjunto de <see cref="ExternalLinkPolicy.SafeSchemes"/>, de modo que todo lo que
/// pasa por aquí lo vuelve a aceptar <see cref="UrlOpener"/> al abrirlo. Un texto sin esquema que tiene pinta
/// de dominio (<c>github.com/enki</c>, <c>www.ejemplo.es</c>) se completa con <c>https://</c>, que es lo que
/// haría la barra de direcciones de un navegador.</para>
///
/// <para>Es estática y pura, así que se prueba directamente — ver <c>WebLinkNormalizerTests</c>.</para>
/// </summary>
public static class WebLinkNormalizer
{
    /// <summary>Límite de longitud de una URL guardada. Holgado para cualquier enlace real y suficiente para
    /// que un pegado accidental de un texto enorme no acabe en el JSON del tablero.</summary>
    public const int MaxLength = 2048;

    /// <summary>Normaliza <paramref name="input"/>; <paramref name="url"/> solo tiene valor cuando el
    /// resultado es <see cref="WebLinkError.None"/>.</summary>
    public static WebLinkError TryNormalize(string? input, out string url)
    {
        url = string.Empty;
        var trimmed = input?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return WebLinkError.Empty;
        if (trimmed.Length > MaxLength || ContainsWhitespaceOrControl(trimmed)) return WebLinkError.Invalid;

        var candidate = HasScheme(trimmed) ? trimmed : "https://" + trimmed.TrimStart('/');
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return WebLinkError.Invalid;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return WebLinkError.Invalid;
        if (string.IsNullOrEmpty(uri.Host)) return WebLinkError.Invalid;

        // Sin esquema escrito, "localhost" o un nombre suelto como "notas" casi siempre es un error de
        // tecleo, no una página: se exige un punto en el servidor salvo que el usuario haya puesto el esquema
        // a mano (http://localhost:8080 sí es intencionado).
        if (!HasScheme(trimmed) && !uri.Host.Contains('.', StringComparison.Ordinal)) return WebLinkError.Invalid;

        url = uri.AbsoluteUri;
        return WebLinkError.None;
    }

    /// <summary>Texto corto para enseñar un enlace en la interfaz: servidor y ruta sin esquema, sin
    /// <c>www.</c> y sin la barra final — lo que identifica la página sin repetir lo que todos comparten.
    /// Si <paramref name="url"/> no se puede analizar (un JSON editado a mano), se enseña tal cual.</summary>
    public static string Describe(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return url;

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        if (!uri.IsDefaultPort) host += ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var rest = Uri.UnescapeDataString(uri.PathAndQuery + uri.Fragment).TrimEnd('/');
        return host + rest;
    }

    private static bool HasScheme(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0) return false;
        // "ejemplo.com:8080/ruta" lleva dos puntos pero no esquema: lo que va detrás es un puerto.
        if (colon + 1 < value.Length && char.IsAsciiDigit(value[colon + 1])) return false;
        for (var i = 0; i < colon; i++)
        {
            var c = value[i];
            var valid = char.IsAsciiLetter(c) || (i > 0 && (char.IsAsciiDigit(c) || c is '+' or '-' or '.'));
            if (!valid) return false;
        }
        return true;
    }

    private static bool ContainsWhitespaceOrControl(string value)
    {
        foreach (var c in value)
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return true;
        return false;
    }
}
