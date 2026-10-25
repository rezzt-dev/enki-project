using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Localization;

namespace enki.Services;

/// <summary>Cambio de ubicación de una carpeta real del vault y de todos sus descendientes.</summary>
public sealed class FolderMutation(string sourcePath, string? destinationPath)
{
    public string SourcePath { get; } = sourcePath;
    public string? DestinationPath { get; } = destinationPath;
    public bool Cancel { get; set; }
}

public sealed partial class VaultService
{
    public event Action<FolderMutation>? FolderChanging;
    public event Action<FolderMutation>? FolderChanged;

    /// <summary>Compara rutas completas respetando los límites de cada segmento.</summary>
    public static bool IsWithinFolder(string path, string folder) => PathsEqual(path, folder) ||
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Rechaza rutas externas, internas y enlaces simbólicos antes de modificar el árbol.</summary>
    public void ValidateFolderPath(string path, bool allowRoot = false)
    {
        EnsureVaultAvailable();
        path = Path.GetFullPath(path);
        if (!IsWithinFolder(path, VaultPath)) throw new IOException(LocalizationService.Instance["Folders_Error_OutsideVault"]);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        var relative = Path.GetRelativePath(VaultPath, path);
        var parts = relative.Split(Path.DirectorySeparatorChar);
        // .sessions es el contenedor real de sesiones; el resto de carpetas internas no es editable.
        if (parts.Where((p, index) => !(index == 0 && p == ".sessions")).Any(p => p != "." && IsHiddenDirectoryName(p)))
            throw new IOException(LocalizationService.Instance["Folders_Error_Internal"]);
        if (PathsEqual(path, Path.Combine(VaultPath, ".sessions")) || (!allowRoot && (PathsEqual(path, VaultPath) ||
            PathsEqual(Path.GetDirectoryName(path)!, Path.Combine(VaultPath, ".sessions")))))
            throw new IOException(LocalizationService.Instance["Folders_Error_RootProtected"]);
        for (var cursor = path; IsWithinFolder(cursor, VaultPath); cursor = Path.GetDirectoryName(cursor)!)
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(LocalizationService.Instance["Folders_Error_SymbolicLink"]);
            if (PathsEqual(cursor, VaultPath)) break;
        }
        if (IsPathInLockedSession(path)) throw new SessionLockedException(path);
    }

    /// <summary>Nombre portable, visible y de un único segmento, sin correcciones silenciosas.</summary>
    public static void ValidateFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith('.') ||
            name.Any(c => "<>:\"/\\|?*".Contains(c)) || name.Any(char.IsControl) ||
            IsHiddenDirectoryName(name) || name.Length > 200 ||
            System.Text.RegularExpressions.Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new IOException(LocalizationService.Instance["Folders_Error_InvalidName"]);
    }

    private static string UniqueFolderPath(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        for (var suffix = 2; Directory.Exists(path) || File.Exists(path); suffix++)
            path = Path.Combine(parent, $"{name} {suffix}");
        return path;
    }

    public NoteFolder CreateManagedFolder(string parent, string name)
    {
        ValidateFolderPath(parent, allowRoot: true);
        ValidateFolderName(name);
        var path = UniqueFolderPath(parent, name);
        Directory.CreateDirectory(path);
        FolderChanged?.Invoke(new FolderMutation(path, path));
        return new NoteFolder { FullPath = path };
    }

    public NoteFolder RenameManagedFolder(string source, string name)
    {
        ValidateFolderPath(source);
        ValidateFolderName(name);
        if (Path.GetFileName(source) == name) return new NoteFolder { FullPath = source };
        var destination = UniqueFolderPath(Path.GetDirectoryName(source)!, name);
        return RelocateFolder(source, destination);
    }

    /// <summary>Copia la jerarquía completa (incluidos adjuntos), sin fusionar ni sobrescribir destinos.
    /// La copia se prepara en una carpeta oculta y solo se publica al completarse.</summary>
    public NoteFolder TransferFolder(string source, string parent, bool cut)
    {
        ValidateFolderPath(source);
        ValidateFolderPath(parent, allowRoot: true);
        if (IsWithinFolder(parent, source)) throw new IOException(LocalizationService.Instance["Folders_Error_Descendant"]);
        if (cut && PathsEqual(Path.GetDirectoryName(source)!, parent)) return new NoteFolder { FullPath = source };
        var destination = UniqueFolderPath(parent, Path.GetFileName(source));
        if (cut) return RelocateFolder(source, destination);
        var preparation = new FolderMutation(source, source);
        FolderChanging?.Invoke(preparation);
        if (preparation.Cancel) throw new IOException(LocalizationService.Instance["Folders_Error_PendingChanges"]);
        var temporary = Path.Combine(parent, ".folder-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            CopyAttachmentDirectory(source, temporary);
            Directory.Move(temporary, destination);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
        InvalidateVaultSizeCache();
        FolderChanged?.Invoke(new FolderMutation(destination, destination));
        return new NoteFolder { FullPath = destination };
    }


    /// <summary>Devuelve las carpetas recuperables sin exponer sesiones bloqueadas.</summary>
    public System.Collections.Generic.List<NoteFolder> GetTrashedFolders() => !Directory.Exists(TrashPath) ? [] :
        Directory.GetDirectories(TrashPath).Where(p => !IsHiddenDirectoryName(Path.GetFileName(p)) && !string.Equals(Path.GetFileName(p), "sessions", StringComparison.OrdinalIgnoreCase) &&
            !IsTrashedItemFromLockedSession(p)).Select(p => new NoteFolder { FullPath = p }).ToList();

    public NoteFolder RestoreFolder(NoteFolder folder)
    {
        EnsureVaultAvailable();
        if (string.Equals(folder.Name, "sessions", StringComparison.OrdinalIgnoreCase) || !PathsEqual(Path.GetDirectoryName(folder.FullPath)!, TrashPath)) throw new IOException(LocalizationService.Instance["Folders_Error_InvalidTrash"]);
        var origins = ReadTrashOrigins();
        if (!origins.TryGetValue(folder.FullPath, out var original))
            original = Path.Combine(VaultPath, folder.Name);
        var parent = Path.GetDirectoryName(original)!;
        ValidateFolderPath(parent, allowRoot: true);
        var destination = UniqueFolderPath(parent, Path.GetFileName(original));
        Directory.Move(folder.FullPath, destination);
        RemoveTrashOrigin(folder.FullPath);
        InvalidateVaultSizeCache();
        FolderChanged?.Invoke(new FolderMutation(destination, destination));
        return new NoteFolder { FullPath = destination };
    }

    public void PermanentlyDeleteFolder(NoteFolder folder)
    {
        EnsureVaultAvailable();
        if (string.Equals(folder.Name, "sessions", StringComparison.OrdinalIgnoreCase) || !PathsEqual(Path.GetDirectoryName(folder.FullPath)!, TrashPath)) throw new IOException(LocalizationService.Instance["Folders_Error_InvalidTrash"]);
        if (IsTrashedItemFromLockedSession(folder.FullPath)) throw new SessionLockedException(folder.FullPath);
        Directory.Delete(folder.FullPath, true);
        RemoveTrashOrigin(folder.FullPath);
        InvalidateVaultSizeCache();
    }

    /// <summary>Inventario completo, sin seguir enlaces, para detectar cambios durante una conversión.</summary>
    private static System.Collections.Generic.Dictionary<string, string> SnapshotFolder(string root, bool hashContents = true)
    {
        var result = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        void Visit(string folder)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException(LocalizationService.Instance["Folders_Error_SymbolicLink"]);
                var relative = Path.GetRelativePath(root, entry);
                if ((attributes & FileAttributes.Directory) != 0) { result[relative] = "directory"; Visit(entry); }
                else
                {
                    if (hashContents)
                    {
                        using var stream = File.OpenRead(entry);
                        result[relative] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                    }
                    else result[relative] = "file";
                }
            }
        }
        Visit(root);
        return result;
    }

    private void MoveFolderAcrossEncryption(string source, string destination,
        System.Collections.Generic.Dictionary<string, string> snapshot)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".folder-copy-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(Path.GetDirectoryName(source)!, ".folder-move-" + Guid.NewGuid().ToString("N"));
        var published = false;
        try
        {
            Directory.CreateDirectory(temporary);
            CopyAttachmentDirectory(source, temporary);
            Directory.Move(source, backup);
            var current = SnapshotFolder(backup);
            if (snapshot.Count != current.Count || snapshot.Any(pair => !current.TryGetValue(pair.Key, out var hash) || hash != pair.Value))
                throw new IOException(LocalizationService.Instance["Folders_Error_Changed"]);
            Directory.Move(temporary, destination);
            published = true;
        }
        finally
        {
            if (!published && Directory.Exists(backup)) Directory.Move(backup, source);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
        try { Directory.Delete(backup, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // El destino ya está completo; conservar el respaldo es preferible a perder datos.
            LogService.Warning($"El traslado terminó, pero no se pudo retirar el respaldo '{backup}'.", ex);
        }
    }

    private NoteFolder RelocateFolder(string source, string destination)
    {
        // Dentro del mismo ámbito el traslado es atómico. Entre ámbitos se convierte el contenido
        // en un directorio temporal y se verifica de nuevo el origen antes de publicarlo.
        var sourceScope = ResolveEncryptedSessionRoot(source);
        var destinationScope = ResolveEncryptedSessionRoot(destination);
        var change = new FolderMutation(source, destination);
        FolderChanging?.Invoke(change);
        if (change.Cancel) throw new IOException(LocalizationService.Instance["Folders_Error_PendingChanges"]);
        var snapshot = SnapshotFolder(source, hashContents: sourceScope != destinationScope);
        var notes = snapshot.Keys.Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).Select(p => Path.Combine(source, p)).ToArray();
        if (sourceScope == destinationScope) Directory.Move(source, destination);
        else MoveFolderAcrossEncryption(source, destination, snapshot);
        foreach (var path in notes)
        {
            MoveVersionHistory(path, Path.Combine(destination, Path.GetRelativePath(source, path)));
            _noteCache.Invalidate(path);
        }
        InvalidateVaultSizeCache();
        FolderChanged?.Invoke(change);
        return new NoteFolder { FullPath = destination };
    }
}
