using System;
using System.Globalization;
using System.IO;
using enki.Models;
using SkiaSharp;
using Svg.Skia;
using System.Xml;

namespace enki.Services;

/// <summary>
/// Guarda como adjunto de una nota la imagen que el usuario acaba de pegar, arrastrar o elegir, y
/// devuelve la ruta con la que referirla desde su Markdown. Las reglas de nombres y rutas viven aparte,
/// en <see cref="NoteImageAttachments"/>; aquí solo está lo que toca disco.
///
/// <para>Toda escritura pasa por <see cref="VaultService.WriteBinaryFile"/>, de modo que un adjunto de
/// una nota que vive en una sesión protegida queda cifrado con la misma clave que la nota — nunca en
/// claro dentro de una carpeta que el usuario creó precisamente para que no lo estuviera.</para>
/// </summary>
public sealed class NoteImageService
{
    public const long MaxSourceBytes = 64L * 1024 * 1024;

    private readonly VaultService _vaultService;

    public NoteImageService(VaultService vaultService) => _vaultService = vaultService;

    /// <summary>Guarda bytes de imagen ya codificados (lo que sale del portapapeles, siempre PNG) como
    /// adjunto de <paramref name="note"/>. Devuelve la ruta relativa lista para insertar en el Markdown,
    /// o null si la nota no tiene una carpeta contenedora donde crear los adjuntos.</summary>
    public string? SaveBytes(NoteFile note, byte[] data, string extension)
    {
        var folder = NoteImageAttachments.FolderFor(note.FullPath);
        if (folder is null) return null;

        var destination = UniquePath(folder, NoteImageAttachments.TimestampedFileName(extension, DateTime.Now));
        _vaultService.WriteBinaryFile(destination, data);
        return NoteImageAttachments.MarkdownReference(note.FullPath, destination);
    }

    /// <summary>Adjunta a <paramref name="note"/> un archivo de imagen que ya existe en disco. Un archivo
    /// que ya vive dentro del vault se referencia donde está en vez de duplicarse: arrastrar a una nota
    /// una imagen que ya está en el módulo Archivos debe enlazarla, no dejar dos copias que luego hay que
    /// mantener a la vez. Cualquier otro archivo se copia a la carpeta de adjuntos de la nota, para que la
    /// nota no dependa de una ruta de fuera del vault que mañana puede no existir.</summary>
    public string? SaveFromFile(NoteFile note, string sourcePath)
    {
        if (!File.Exists(sourcePath)) return null;

        var fullSource = Path.GetFullPath(sourcePath);
        var info = new FileInfo(fullSource);
        if (info.Length > MaxSourceBytes)
            throw new NoteImageAttachmentException(NoteImageAttachmentFailure.TooLarge);
        ValidateImage(fullSource, Path.GetExtension(fullSource));

        if (IsInsideVault(fullSource))
            return NoteImageAttachments.MarkdownReference(note.FullPath, fullSource);

        var folder = NoteImageAttachments.FolderFor(note.FullPath);
        if (folder is null) return null;

        var destination = UniquePath(folder, NoteImageAttachments.SanitizeFileName(Path.GetFileName(fullSource)));
        using var source = new FileStream(fullSource, FileMode.Open, FileAccess.Read, FileShare.Read);
        _vaultService.CopyBinaryFile(destination, source, MaxSourceBytes);
        return NoteImageAttachments.MarkdownReference(note.FullPath, destination);
    }

    private static void ValidateImage(string path, string extension)
    {
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (string.Equals(extension, ".svg", StringComparison.OrdinalIgnoreCase))
            {
                using var svg = new SKSvg();
                svg.Load(source);
                if (svg.Picture is null) throw new InvalidDataException("SVG sin contenido gráfico.");
                return;
            }

            using var codec = SKCodec.Create(source);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
                throw new InvalidDataException("Cabecera de imagen no reconocida.");
        }
        catch (NoteImageAttachmentException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException
                                       or InvalidOperationException or NotSupportedException or XmlException
                                       or OutOfMemoryException)
        {
            throw new NoteImageAttachmentException(NoteImageAttachmentFailure.UnsupportedFormat, ex);
        }
    }

    private bool IsInsideVault(string fullPath)
    {
        var vault = _vaultService.VaultPath;
        if (string.IsNullOrEmpty(vault)) return false;

        var root = Path.GetFullPath(vault);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, VaultService.PathComparison);
    }

    /// <summary>Primera variante libre de <paramref name="fileName"/> dentro de
    /// <paramref name="folder"/>: <c>captura.png</c>, <c>captura-2.png</c>, <c>captura-3.png</c>… Dos
    /// pegados en el mismo segundo, o dos archivos arrastrados con el mismo nombre, no pueden pisarse.</summary>
    private static string UniquePath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate)) return candidate;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; suffix < 1000; suffix++)
        {
            candidate = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{name}-{suffix}{extension}"));
            if (!File.Exists(candidate)) return candidate;
        }

        // Mil colisiones seguidas solo pasan si algo va muy mal; un sufijo aleatorio cierra el caso sin
        // dejar que el bucle devuelva una ruta ya ocupada.
        return Path.Combine(folder, $"{name}-{Guid.NewGuid():N}{extension}");
    }
}

public enum NoteImageAttachmentFailure { TooLarge, UnsupportedFormat }

/// <summary>Fallo de validación de un adjunto, separado de un error de disco para que el editor pueda
/// explicar si el archivo excede el límite o no contiene una imagen reconocible.</summary>
public sealed class NoteImageAttachmentException : IOException
{
    public NoteImageAttachmentFailure Failure { get; }

    public NoteImageAttachmentException(NoteImageAttachmentFailure failure, Exception? innerException = null)
        : base(failure == NoteImageAttachmentFailure.TooLarge
            ? "La imagen supera el tamaño máximo permitido."
            : "El archivo no contiene una imagen compatible.", innerException) => Failure = failure;
}
