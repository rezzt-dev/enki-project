using System;
using System.Globalization;
using System.IO;
using enki.Services;

namespace enki.Views.Editor.Markdown;

/// <summary>De dónde puede salir la imagen de un <c>![…](…)</c>: un archivo del vault, un <c>data:</c>
/// incrustado en la propia nota, o una URL remota.</summary>
internal enum MarkdownImageKind
{
    /// <summary>No se pudo decidir nada útil con el destino escrito (vacío, esquema desconocido…).</summary>
    Unknown,
    File,
    DataUri,
    Remote
}

/// <summary>Lo que el editor necesita saber de la nota abierta para resolver las rutas de sus imágenes y
/// leerlas del disco. Lo publica <c>EditorView</c> en <see cref="LivePreviewState.Images"/> cada vez que
/// cambia de pestaña, en vez de que el generador de elementos hable con <c>VaultService</c> por su cuenta:
/// la lectura pasa por <see cref="ReadBytes"/>, que es la del viewmodel y por tanto la que descifra sola
/// una sesión protegida (<c>VaultService.ReadFileBytes</c>).</summary>
internal sealed class MarkdownImageContext
{
    public static readonly MarkdownImageContext Empty = new();

    /// <summary>Carpeta de la nota abierta — el ancla de toda ruta relativa escrita en ella.</summary>
    public string? NoteDirectory { get; init; }

    /// <summary>Raíz del vault, usada solo como último recurso para localizar por nombre el destino de un
    /// <c>![[archivo.png]]</c>, que por convención de Obsidian no lleva ruta.</summary>
    public string? VaultPath { get; init; }

    /// <summary>Lee los bytes en claro de un archivo del vault. Null deshabilita la carga de archivos
    /// (no hay nota enlazada todavía).</summary>
    public Func<string, byte[]>? ReadBytes { get; init; }
}

/// <summary>Destino ya resuelto de una imagen: qué es y, si es un archivo, dónde está exactamente.</summary>
internal readonly record struct MarkdownImageTarget(MarkdownImageKind Kind, string Value)
{
    public static readonly MarkdownImageTarget None = new(MarkdownImageKind.Unknown, string.Empty);
}

/// <summary>
/// Traduce el destino escrito en la nota (<c>foto.png</c>, <c>../assets/foto.png</c>,
/// <c>C:\fotos\foto.png</c>, <c>https://…</c>, <c>data:image/png;base64,…</c>) a un
/// <see cref="MarkdownImageTarget"/> concreto.
///
/// El orden de búsqueda de un archivo imita al de Obsidian, de lo más explícito a lo más adivinado:
/// ruta absoluta tal cual → relativa a la carpeta de la nota → carpeta de adjuntos de la nota →
/// búsqueda por nombre en todo el vault (solo para un destino sin carpeta, que es la forma en que se
/// escribe un <c>![[archivo.png]]</c>). Resolver puede tocar disco, así que siempre se llama desde el
/// hilo de carga de <see cref="MarkdownImageCache"/>, nunca durante el layout del editor.
/// </summary>
internal static class MarkdownImageResolver
{
    public static MarkdownImageTarget Resolve(MarkdownImageContext context, string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return MarkdownImageTarget.None;
        var trimmed = src.Trim();

        if (trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return new MarkdownImageTarget(MarkdownImageKind.DataUri, trimmed);

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new MarkdownImageTarget(MarkdownImageKind.Remote, trimmed);

        // Un esquema que no sea ninguno de los anteriores (mailto:, javascript:…) no es una imagen que
        // este editor deba abrir. Una letra de unidad de Windows ("C:\…") parece un esquema pero no lo es.
        if (HasForeignScheme(trimmed)) return MarkdownImageTarget.None;

        var path = DecodePath(trimmed);
        var file = ResolveFile(context, path);
        return file is null ? MarkdownImageTarget.None : new MarkdownImageTarget(MarkdownImageKind.File, file);
    }

    /// <summary>Ruta absoluta del archivo al que apunta <paramref name="path"/>, exista o no: si ningún
    /// candidato existe se devuelve el primero (el más probable), para que quien la reciba pueda decir
    /// "falta este archivo" con una ruta concreta en vez de con un null.</summary>
    private static string? ResolveFile(MarkdownImageContext context, string path)
    {
        if (path.Length == 0) return null;

        if (Path.IsPathRooted(path))
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        var directory = context.NoteDirectory;
        if (string.IsNullOrEmpty(directory)) return null;

        string? first = null;
        foreach (var candidate in Candidates(context, directory, path))
        {
            first ??= candidate;
            if (File.Exists(candidate)) return candidate;
        }
        return first;
    }

    private static System.Collections.Generic.IEnumerable<string> Candidates(
        MarkdownImageContext context, string noteDirectory, string path)
    {
        var combined = TryCombine(noteDirectory, path);
        if (combined is not null) yield return combined;

        // Un nombre suelto ("captura.png") es lo que escribe un `![[…]]`, y también lo que queda si el
        // usuario mueve la nota sin mover el adjunto: mirar en la carpeta de adjuntos y luego en todo el
        // vault recupera esos dos casos sin tocar el texto de la nota.
        if (path.IndexOfAny(['/', '\\']) >= 0) yield break;

        foreach (var folder in Directory.Exists(noteDirectory)
                     ? Directory.EnumerateDirectories(noteDirectory, "*" + NoteImageAttachments.FolderSuffix)
                     : [])
        {
            var inAttachments = TryCombine(folder, path);
            if (inAttachments is not null) yield return inAttachments;
        }

        var found = SearchVault(context.VaultPath, path);
        if (found is not null) yield return found;
    }

    private static string? SearchVault(string? vaultPath, string fileName)
    {
        if (string.IsNullOrEmpty(vaultPath) || !Directory.Exists(vaultPath)) return null;

        try
        {
            foreach (var match in Directory.EnumerateFiles(vaultPath, fileName, new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         MatchCasing = MatchCasing.CaseInsensitive
                     }))
            {
                return match;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        return null;
    }

    private static string? TryCombine(string directory, string path)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(directory, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Deshace el porcentaje-escapado que <see cref="Services.NoteImageAttachments"/> aplica al
    /// insertar la ruta (un espacio en Markdown cortaría la ruta en el primer hueco), y normaliza las
    /// barras al separador del sistema.</summary>
    private static string DecodePath(string value)
    {
        var decoded = value;
        if (decoded.Contains('%', StringComparison.Ordinal))
        {
            try
            {
                decoded = Uri.UnescapeDataString(decoded);
            }
            catch (UriFormatException)
            {
                // Un '%' suelto que no forma un escape válido es parte del nombre del archivo.
            }
        }
        return decoded.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }

    private static bool HasForeignScheme(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 1) return false; // Sin esquema, o una letra de unidad de Windows.

        for (var i = 0; i < colon; i++)
        {
            var c = value[i];
            if (!char.IsLetterOrDigit(c) && c != '+' && c != '-' && c != '.') return false;
        }
        return char.IsLetter(value[0]);
    }

    /// <summary>Descodifica el cuerpo base64 de un <c>data:image/…;base64,…</c>. Devuelve null si el URI
    /// no trae base64 (la forma con texto plano, que solo tiene sentido para SVG y es rarísima) o si el
    /// base64 está corrupto.</summary>
    public static byte[]? DecodeDataUri(string dataUri)
    {
        var comma = dataUri.IndexOf(',');
        if (comma < 0) return null;

        var header = dataUri[..comma];
        if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            return Convert.FromBase64String(dataUri[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Nombre corto con el que enseñar un destino en un hueco de imagen rota, sin llenar la línea
    /// con una ruta larga o un base64 entero.</summary>
    public static string ShortLabel(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return string.Empty;
        var value = src.Trim();

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return "data:image";

        var slash = value.LastIndexOfAny(['/', '\\']);
        var name = slash >= 0 && slash + 1 < value.Length ? value[(slash + 1)..] : value;
        return name.Length <= 48 ? name : string.Concat(name.AsSpan(0, 45), "…");
    }

    /// <summary>Redondea un ancho de decodificación a saltos de <paramref name="step"/> px. Sin esto, cada
    /// píxel de redimensionado de la ventana estrenaría una entrada de caché y volvería a decodificar la
    /// misma imagen.</summary>
    public static int QuantizeWidth(double width, int step = 200)
    {
        if (double.IsNaN(width) || width <= 0) return step;
        var quantized = (int)Math.Ceiling(width / step) * step;
        return Math.Clamp(quantized, step, 4000);
    }

    public static string Key(MarkdownImageContext context, string src, int decodeWidth) =>
        string.Create(CultureInfo.InvariantCulture, $"{context.NoteDirectory}\u0000{src}\u0000{decodeWidth}");
}
