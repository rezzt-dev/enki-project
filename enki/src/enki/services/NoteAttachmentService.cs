using System;
using System.IO;
using enki.Models;

namespace enki.Services;

/// <summary>Importa adjuntos al almacén real del vault, junto a las imágenes de la nota, sin índices paralelos.</summary>
public sealed class NoteAttachmentService(VaultService vault)
{
    public const long MaxSourceBytes = 128L * 1024 * 1024;

    /// <summary>Los documentos incrustados se muestran como tarjetas; las imágenes conservan su render habitual.</summary>
    public static bool IsAttachmentTarget(string target)
    {
        var path = target.Split('#', '?')[0];
        try { path = Uri.UnescapeDataString(path); }
        catch (UriFormatException) { return false; }
        return !string.IsNullOrEmpty(Path.GetExtension(path)) && !NoteImageAttachments.IsImageFile(path)
            && !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reutiliza archivos del vault; importa los externos con escritura atómica y cifrado de destino.</summary>
    public string SaveFromFile(NoteFile note, string sourcePath)
    {
        var noteResolution = ExternalLinkPolicy.Resolve(note.FullPath, null, vault.VaultPath);
        if (noteResolution.Target is null) throw new IOException("La nota no pertenece al vault disponible.");
        if (vault.IsPathInLockedSession(note.FullPath)) throw new SessionLockedException(note.FullPath);
        var source = Path.GetFullPath(sourcePath);
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException("El adjunto ya no existe.", source);
        if (info.Length == 0) throw new InvalidDataException("El adjunto está vacío.");
        if (info.Length > MaxSourceBytes) throw new InvalidDataException("El adjunto supera el límite de 128 MB.");

        var resolution = ExternalLinkPolicy.Resolve(source, null, vault.VaultPath);
        if (resolution.Reason is ExternalLinkBlockReason.ExecutableFile)
            throw new InvalidDataException("Este tipo de archivo no se puede adjuntar.");
        // La misma política se aplica a los externos antes de copiarlos.
        var folder = NoteImageAttachments.FolderFor(note.FullPath)!;
        var name = NoteImageAttachments.SanitizeFileName(info.Name);
        if (string.IsNullOrEmpty(Path.GetExtension(info.Name))) name = name[..^4];
        var destination = Path.Combine(folder, name);
        var candidatePolicy = ExternalLinkPolicy.Resolve(destination, null, vault.VaultPath);
        if (candidatePolicy.Reason is not (ExternalLinkBlockReason.Missing or ExternalLinkBlockReason.None))
            throw new InvalidDataException("Este tipo de archivo no se puede adjuntar.");

        if (resolution.Target is not null)
        {
            if (vault.IsPathInLockedSession(source)) throw new SessionLockedException(source);
            return NoteImageAttachments.MarkdownReference(note.FullPath, source);
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var suffix = 2; File.Exists(destination); suffix++)
            destination = Path.Combine(folder, $"{stem}-{suffix}{extension}");
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        vault.CopyBinaryFile(destination, stream, MaxSourceBytes);
        return NoteImageAttachments.MarkdownReference(note.FullPath, destination);
    }

    /// <summary>Devuelve el rótulo visible de una referencia con corchetes escapados.</summary>
    public static string UnescapeLabel(string label) => label.Replace("\\[", "[", StringComparison.Ordinal)
        .Replace("\\]", "]", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    /// <summary>Escapa el rótulo para que un nombre de archivo no altere el Markdown generado.</summary>
    public static string EscapeLabel(string label) => label.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
