using System;
using System.IO;
using System.Text;
using enki.Models;

namespace enki.Services;

public sealed partial class VaultService
{
    /// <summary>Copia un archivo sin sobrescribir y convierte el cifrado al de la carpeta de destino.</summary>
    public string CopyFileToFolder(string sourcePath, string destinationFolder)
    {
        EnsureVaultAvailable();
        if (!Directory.Exists(destinationFolder)) throw new DirectoryNotFoundException(destinationFolder);
        var destination = ResolveUniqueDestination(sourcePath, destinationFolder);
        CopyFileExclusive(sourcePath, destination);
        InvalidateVaultSizeCache();
        return destination;
    }

    private void CopyFileExclusive(string source, string destination, byte[]? content = null)
    {
        var plaintext = content ?? ReadFileBytes(source);
        var root = ResolveEncryptedSessionRoot(destination);
        var payload = root is null ? plaintext : _encryptionService.Encrypt(root, plaintext);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, payload);
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Transfiere una nota con sus imágenes. Publica la nota solo cuando sus adjuntos están
    /// completos; los fallos de copia conservan el original y eliminan los adjuntos recién creados.</summary>
    public NoteFile TransferNote(NoteFile note, string destinationFolder, bool cut)
    {
        EnsureVaultAvailable();
        if (!Directory.Exists(destinationFolder)) throw new DirectoryNotFoundException(destinationFolder);
        if (IsPathInLockedSession(note.FullPath)) throw new SessionLockedException(note.FullPath);
        if (IsPathInLockedSession(destinationFolder)) throw new SessionLockedException(destinationFolder);
        if (!File.Exists(note.FullPath)) throw new FileNotFoundException(null, note.FullPath);
        if (cut && PathsEqual(Path.GetDirectoryName(note.FullPath)!, destinationFolder)) return note;
        var source = note.FullPath;
        var fresh = ReadFromDisk(source) ?? throw new FileNotFoundException(null, source);
        var destination = ResolveUniqueDestination(source, destinationFolder);
        var suffix = 2;
        while (File.Exists(destination) || Directory.Exists(destination) || Directory.Exists(NoteImageAttachments.FolderFor(destination)) || File.Exists(NoteImageAttachments.FolderFor(destination)))
            destination = Path.Combine(destinationFolder, $"{Path.GetFileNameWithoutExtension(source)} {suffix++}.md");
        var sourceAttachments = NoteImageAttachments.FolderFor(source)!;
        var destinationAttachments = NoteImageAttachments.FolderFor(destination)!;
        var copiedAttachments = false;
        try
        {
            var content = fresh.Content;
            if (Directory.Exists(sourceAttachments))
            {
                if ((File.GetAttributes(sourceAttachments) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("La carpeta de adjuntos es un enlace simbólico.");
                Directory.CreateDirectory(destinationAttachments);
                copiedAttachments = true;
                CopyAttachmentDirectory(sourceAttachments, destinationAttachments);
                var oldReference = NoteImageAttachments.MarkdownReference(source, sourceAttachments) + "/";
                var newReference = NoteImageAttachments.MarkdownReference(destination, destinationAttachments) + "/";
                content = content.Replace(oldReference, newReference, StringComparison.Ordinal);
            }
            if (cut && NoteDiskStamps.Check(source, fresh.Stamp, out _) != NoteDiskState.Unchanged)
                throw new IOException("La nota ha cambiado durante la transferencia.");
            CopyFileExclusive(source, destination, Encoding.UTF8.GetBytes(content));
        }
        catch
        {
            if (copiedAttachments) Directory.Delete(destinationAttachments, recursive: true);
            throw;
        }
        if (cut)
        {
            try
            {
                if (NoteDiskStamps.Check(source, fresh.Stamp, out _) != NoteDiskState.Unchanged)
                    throw new IOException("La nota ha cambiado durante la transferencia.");
                File.Delete(source);
            }
            catch
            {
                File.Delete(destination);
                if (copiedAttachments) Directory.Delete(destinationAttachments, recursive: true);
                throw;
            }
            MoveVersionHistory(source, destination);
            // Los adjuntos originales se conservan: otras notas pueden referenciarlos también.
            _noteCache.Invalidate(source);
        }
        _noteCache.Invalidate(destination);
        InvalidateVaultSizeCache();
        return GetNote(destination)!;
    }

    private void CopyAttachmentDirectory(string source, string destination)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("No se pueden copiar adjuntos que sean enlaces simbólicos.");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                CopyAttachmentDirectory(entry, target);
            }
            else CopyFileExclusive(entry, target);
        }
    }
}
