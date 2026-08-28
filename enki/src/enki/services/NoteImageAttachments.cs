using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace enki.Services;

/// <summary>
/// Reglas de nombres y rutas de los adjuntos de imagen de una nota. Todo lo que aquí se decide es
/// aritmética de rutas y nada toca el disco, para que las decisiones (dónde vive un adjunto, con qué
/// nombre, cómo se escribe su ruta dentro del Markdown) sean verificables sin un vault de verdad —
/// escribir el archivo es cosa de <see cref="NoteImageService"/>.
///
/// <para>Una imagen pegada o arrastrada en <c>Proyecto.md</c> aterriza en <c>Proyecto.attachments/</c>,
/// hermana de la nota. Es la convención que fija docs/imagenes-en-notas.md: la nota y sus imágenes se
/// mueven, se copian y se borran juntas, y la carpeta se llama como la nota, así que ver a quién
/// pertenece un adjunto no exige abrir nada.</para>
/// </summary>
public static class NoteImageAttachments
{
    /// <summary>Sufijo fijo (no localizado, igual que <c>enki-documents</c>) de la carpeta de adjuntos de
    /// una nota. Constante en todos los idiomas para que un vault creado en un idioma siga reconociendo
    /// sus adjuntos tras cambiarlo.</summary>
    public const string FolderSuffix = ".attachments";

    /// <summary>Extensiones que Enki guarda e intenta dibujar como imagen — el mismo juego que reconoce el
    /// live preview (<c>MarkdownLineParser.LooksLikeImage</c>) y el exportador a HTML.</summary>
    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".avif", ".ico", ".tif", ".tiff"];

    public static bool IsImageFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = Path.GetExtension(path);
        if (extension.Length == 0) return false;

        foreach (var known in ImageExtensions)
        {
            if (string.Equals(extension, known, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Carpeta de adjuntos de <paramref name="notePath"/>: <c>&lt;nota&gt;.attachments/</c> junto a
    /// la nota. Devuelve null si la ruta no tiene carpeta contenedora (no debería pasar con una nota real
    /// del vault, pero un null aquí es preferible a construir una ruta inventada).</summary>
    public static string? FolderFor(string notePath)
    {
        if (string.IsNullOrWhiteSpace(notePath)) return null;
        var directory = Path.GetDirectoryName(notePath);
        if (string.IsNullOrEmpty(directory)) return null;

        return Path.Combine(directory, Path.GetFileNameWithoutExtension(notePath) + FolderSuffix);
    }

    /// <summary>Nombre de archivo para una imagen recién capturada del portapapeles:
    /// <c>imagen-20260828-143012.png</c>. La marca de tiempo (hora local, formato invariante) evita
    /// colisiones sin depender de leer la carpeta, y el prefijo es fijo en todos los idiomas porque forma
    /// parte del contenido del vault, no de la interfaz.</summary>
    public static string TimestampedFileName(string extension, DateTime localNow) =>
        "imagen-" + localNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + NormalizeExtension(extension);

    /// <summary>Deja un nombre de archivo apto para el vault: sin caracteres inválidos, sin espacios (que
    /// obligarían a escapar la ruta en el Markdown) y sin llegar a longitudes absurdas.</summary>
    public static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = NormalizeExtension(Path.GetExtension(fileName));

        var sb = new StringBuilder(name.Length);
        var lastWasDash = false;
        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c) || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0)
            {
                if (!lastWasDash && sb.Length > 0) sb.Append('-');
                lastWasDash = true;
                continue;
            }
            sb.Append(c);
            lastWasDash = false;
        }

        var sanitized = sb.ToString().Trim('-', '.');
        if (sanitized.Length == 0) sanitized = "imagen";
        if (sanitized.Length > 80) sanitized = sanitized[..80].TrimEnd('-', '.');
        return sanitized + extension;
    }

    /// <summary>Ruta con la que referirse a <paramref name="imagePath"/> desde la nota
    /// <paramref name="notePath"/> dentro de un <c>![](…)</c>: relativa a la nota siempre que ambas
    /// compartan raíz (así la nota sigue funcionando si el vault entero se mueve o se sincroniza a otra
    /// máquina), separada por <c>/</c> como manda Markdown, y con los espacios escapados como <c>%20</c>
    /// porque el paréntesis de cierre de <c>![](…)</c> no admite espacios sin ambigüedad.</summary>
    public static string MarkdownReference(string notePath, string imagePath)
    {
        var noteDirectory = Path.GetDirectoryName(notePath);
        var reference = imagePath;

        if (!string.IsNullOrEmpty(noteDirectory))
        {
            try
            {
                var relative = Path.GetRelativePath(noteDirectory, imagePath);
                // Un GetRelativePath que se sale a otra unidad devuelve la ruta absoluta tal cual; en ese
                // caso la absoluta es justo lo que hay que escribir.
                if (!Path.IsPathRooted(relative)) reference = relative;
            }
            catch (ArgumentException)
            {
                reference = imagePath;
            }
        }

        return EscapeForMarkdown(reference.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/'));
    }

    /// <summary>Escapa lo justo para que la ruta sobreviva dentro de <c>![](…)</c>: espacios y los dos
    /// paréntesis. Nada más — un porcentaje-escapado completo convertiría acentos y eñes perfectamente
    /// válidos en ruido ilegible dentro del texto de la nota.</summary>
    private static string EscapeForMarkdown(string path) => path
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace(" ", "%20", StringComparison.Ordinal)
        .Replace("(", "%28", StringComparison.Ordinal)
        .Replace(")", "%29", StringComparison.Ordinal);

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return ".png";
        var normalized = extension.StartsWith('.') ? extension : "." + extension;
        return normalized.ToLowerInvariant();
    }
}
