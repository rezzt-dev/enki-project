using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using enki.Models;

namespace enki.Services;

/// <summary>Local version history — a per-note trail of Markdown snapshots kept inside the vault itself, so
/// a fast typo, a bad find-and-replace, or an accidental overwrite is never a permanent loss the way it
/// used to be with only the current file on disk and no undo history surviving past the app closing.
///
/// <para><b>Where snapshots live.</b> Each note gets its own hidden folder, keyed by its path relative to
/// whichever root governs its encryption — <c>&lt;vault&gt;/.enki/history/&lt;key&gt;/</c> for a root note,
/// <c>&lt;vault&gt;/.sessions/&lt;session&gt;/.history/&lt;key&gt;/</c> for one inside a session. Nesting the
/// session variant *inside* the session's own folder means a snapshot of a note in an encrypted session is
/// itself encrypted by the exact same mechanism as the note (<see cref="ResolveEncryptedSessionRoot"/> keys
/// off path prefix, so anything under the session root qualifies) — version history must never become the
/// one place a protected note's content sits in the clear. Both roots start with a dot, so every existing
/// enumeration that already skips hidden directories (<see cref="IsUnderHiddenDirectory"/>,
/// <see cref="BuildFolderChildren"/>, <see cref="GetFiles"/>) skips this too, without needing to know it
/// exists.</para>
///
/// <para><b>Why autosave doesn't just snapshot every write.</b> The editor's debounced autosave
/// (<see cref="EditorViewModel"/>) already writes to disk roughly every 600ms of typing pause — capturing a
/// full snapshot on every one of those would produce thousands of near-identical versions per session and
/// bury the ones that actually matter. <see cref="CaptureSnapshot"/> throttles autosave snapshots to at most
/// one per <see cref="MinimumAutosaveInterval"/> of elapsed time since the note's last snapshot (any trigger),
/// and always skips capturing content byte-identical to the most recent version. Restore- and
/// bulk-operation-triggered snapshots bypass the throttle: they exist specifically to protect one exact
/// moment, so they always get recorded.</para>
///
/// <para><b>Retention.</b> <see cref="PruneRetainedVersions"/> keeps at most <see cref="MaxVersionsPerNote"/>
/// snapshots per note, dropping the oldest first — but never the single oldest version if it's the only one
/// left, so a note always has at least one point to fall back to as long as it has any history at all.</para>
/// </summary>
public sealed partial class VaultService
{
    /// <summary>How often an autosave tick is allowed to add a new snapshot, per note. Chosen to capture
    /// "the shape of an editing session" (a version every few minutes of active work) rather than "the shape
    /// of every debounce tick" — short enough that a session cut short by a crash still leaves a recent
    /// snapshot, long enough that a normal editing session doesn't accumulate hundreds of near-duplicates.</summary>
    public static readonly TimeSpan MinimumAutosaveInterval = TimeSpan.FromMinutes(5);

    /// <summary>How many snapshots <see cref="PruneRetainedVersions"/> keeps per note before deleting the
    /// oldest. Generous enough to cover weeks of periodic autosave snapshots plus every restore/bulk-operation
    /// safety net, without letting a note's history grow unbounded.</summary>
    public const int MaxVersionsPerNote = 50;

    private const string RootHistoryDirectoryName = ".enki";
    private const string HistorySubdirectoryName = "history";
    private const string SessionHistorySubdirectoryName = ".history";
    private const string ManifestFileName = "manifest.json";

    /// <summary>Every version recorded for <paramref name="note"/>, newest first — empty if the note has
    /// never had a snapshot captured (a brand-new note, or one created before this feature existed and never
    /// yet autosaved past the throttle window).</summary>
    public List<NoteVersion> GetVersionHistory(NoteFile note) =>
        LoadManifest(VersionsFolderFor(note.FullPath)).OrderByDescending(v => v.CreatedUtc).ToList();

    /// <summary>Reads one snapshot's Markdown content, decrypting it first if <paramref name="note"/> falls
    /// under an encrypted session — same transparent handling as <see cref="ReadContent"/>. Returns
    /// <see langword="null"/> if the snapshot no longer exists (deleted, or the manifest entry is stale).</summary>
    public string? ReadVersionContent(NoteFile note, string versionId)
    {
        var path = SnapshotFilePath(VersionsFolderFor(note.FullPath), versionId);
        if (!File.Exists(path)) return null;
        return SafeReadContent(path);
    }

    /// <summary>Captures a snapshot of <paramref name="content"/> for <paramref name="note"/>, applying the
    /// autosave throttle and duplicate-content skip described in this class' doc comment for
    /// <see cref="NoteVersionTrigger.Autosave"/> — every other trigger always records. Returns the version
    /// that was recorded, or <see langword="null"/> if this call was skipped (nothing changed, or too soon
    /// since the last autosave snapshot). Never throws: a failure to snapshot must not block the write it's
    /// protecting, so I/O errors here are logged and swallowed.</summary>
    public NoteVersion? CaptureSnapshot(NoteFile note, string content, NoteVersionTrigger trigger)
    {
        try
        {
            EnsureVaultAvailable();
            var folder = VersionsFolderFor(note.FullPath);
            var manifest = LoadManifest(folder);
            var latest = manifest.OrderByDescending(v => v.CreatedUtc).FirstOrDefault();

            if (latest is not null)
            {
                var latestPath = SnapshotFilePath(folder, latest.Id);
                if (File.Exists(latestPath) && ReadFromDisk(latestPath)?.Content == content) return null;

                if (trigger == NoteVersionTrigger.Autosave &&
                    DateTime.UtcNow - latest.CreatedUtc < MinimumAutosaveInterval)
                {
                    return null;
                }
            }

            Directory.CreateDirectory(folder);
            var version = new NoteVersion
            {
                Id = NewVersionId(),
                CreatedUtc = DateTime.UtcNow,
                Trigger = trigger,
                WordCount = CountWords(content),
                SizeBytes = Encoding.UTF8.GetByteCount(content)
            };

            WriteFileAtomic(SnapshotFilePath(folder, version.Id), content);
            manifest.Add(version);
            PruneRetainedVersions(folder, manifest);
            SaveManifest(folder, manifest);
            return version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            LogService.Warning($"No se pudo guardar una versión del historial de '{note.FullPath}'", ex);
            return null;
        }
    }

    /// <summary>Restores <paramref name="note"/>'s content to what version <paramref name="versionId"/> held,
    /// after first capturing the note's *current* content as a <see cref="NoteVersionTrigger.PreRestore"/>
    /// snapshot — so restoring the wrong version by mistake is itself just another entry in the list to
    /// restore back from, never a dead end. Throws if the target version no longer exists, or if the note's
    /// session is locked (same failure modes as <see cref="WriteContent"/>).</summary>
    public void RestoreVersion(NoteFile note, string versionId, NoteDiskStamp? baseline = null, string? unsavedContent = null)
    {
        var content = ReadFromDisk(SnapshotFilePath(VersionsFolderFor(note.FullPath), versionId))?.Content
            ?? throw new FileNotFoundException($"La versión '{versionId}' ya no existe.", versionId);

        var disk = ReadFromDisk(note.FullPath)
            ?? throw new FileNotFoundException("La nota que se quiere restaurar ya no existe.", note.FullPath);
        CaptureSnapshotChecked(note, disk.Content, NoteVersionTrigger.PreRestore);
        if (unsavedContent is not null && unsavedContent != disk.Content)
            CaptureSnapshotChecked(note, unsavedContent, NoteVersionTrigger.PreRestore);
        WriteContentChecked(note, content, baseline ?? disk.Stamp);
    }

    /// <summary>Una restauración destructiva solo continúa cuando su contenido anterior es recuperable.
    /// CaptureSnapshot devuelve null tanto al fallar como si ya existía una copia idéntica.</summary>
    internal void CaptureSnapshotChecked(NoteFile note, string content, NoteVersionTrigger trigger)
    {
        if (CaptureSnapshot(note, content, trigger) is not null) return;
        if (GetVersionHistory(note).Any(version =>
                ReadFromDisk(SnapshotFilePath(VersionsFolderFor(note.FullPath), version.Id))?.Content == content)) return;
        throw new IOException("No se pudo conservar el contenido anterior en el historial de versiones.");
    }

    /// <summary>Deletes a single snapshot from <paramref name="note"/>'s history — lets the user prune an
    /// unwanted intermediate version without touching the note's current content or its other versions.</summary>
    public void DeleteVersion(NoteFile note, string versionId)
    {
        var folder = VersionsFolderFor(note.FullPath);
        var manifest = LoadManifest(folder);
        if (manifest.RemoveAll(v => v.Id == versionId) == 0) return;

        var path = SnapshotFilePath(folder, versionId);
        if (File.Exists(path)) File.Delete(path);
        SaveManifest(folder, manifest);
    }

    /// <summary>Folder holding <paramref name="notePath"/>'s snapshots and manifest — nested inside its
    /// encrypted session's own folder when it has one (see this class' doc comment), or under the vault's
    /// shared <c>.enki/history/</c> otherwise. The folder name itself is a short hash of the note's path
    /// relative to that root plus a readable prefix, so two notes with the same title in different folders
    /// never collide, while a vault owner poking around on disk can still tell them apart at a glance.</summary>
    private string VersionsFolderFor(string notePath)
    {
        var sessionRoot = ResolveEncryptedSessionRootForHistory(notePath);
        var (scopeRoot, historyRoot) = sessionRoot is not null
            ? (sessionRoot, Path.Combine(sessionRoot, SessionHistorySubdirectoryName))
            : (VaultPath, Path.Combine(VaultPath, RootHistoryDirectoryName, HistorySubdirectoryName));

        return Path.Combine(historyRoot, HistoryKey(scopeRoot, notePath));
    }

    /// <summary>Resolves which session (if any) owns <paramref name="notePath"/> for the purpose of deciding
    /// where its history lives — deliberately not restricted to only encrypted sessions the way
    /// <see cref="ResolveEncryptedSessionRoot"/> is, because a note's plain-text session still deserves its
    /// history nested with it rather than scattered into the shared vault-root folder, and moving a session
    /// between plain-text and encrypted must never orphan history that was filed under the wrong root.
    /// Falls back to null (vault-root history) for anything outside <c>.sessions/</c>.</summary>
    private string? ResolveEncryptedSessionRootForHistory(string notePath)
    {
        if (string.IsNullOrEmpty(VaultPath)) return null;

        var sessionsRoot = Path.Combine(VaultPath, ".sessions");
        var relative = Path.GetRelativePath(sessionsRoot, notePath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return null;

        var sessionFolder = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return string.IsNullOrEmpty(sessionFolder) ? null : Path.Combine(sessionsRoot, sessionFolder);
    }

    private static string HistoryKey(string scopeRoot, string notePath)
    {
        var relative = Path.GetRelativePath(scopeRoot, notePath).Replace('\\', '/');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative)))[..12];
        var readablePart = SanitizeFileName(Path.GetFileNameWithoutExtension(notePath));
        return $"{readablePart}-{hash}";
    }

    private static string SnapshotFilePath(string folder, string versionId) => Path.Combine(folder, $"{versionId}.md");

    private static string NewVersionId() =>
        $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..21];

    private static List<NoteVersion> LoadManifest(string folder) =>
        JsonFileStore.TryLoad<List<NoteVersion>>(Path.Combine(folder, ManifestFileName), out var manifest) && manifest is not null
            ? manifest
            : [];

    private static void SaveManifest(string folder, List<NoteVersion> manifest) =>
        JsonFileStore.Save(Path.Combine(folder, ManifestFileName), manifest);

    /// <summary>Keeps at most <see cref="MaxVersionsPerNote"/> entries in <paramref name="manifest"/>,
    /// deleting the oldest snapshot files on disk as their entries are dropped — mutates the list in place so
    /// the caller's subsequent <see cref="SaveManifest"/> persists the pruned set.</summary>
    private static void PruneRetainedVersions(string folder, List<NoteVersion> manifest)
    {
        if (manifest.Count <= MaxVersionsPerNote) return;

        var toRemove = manifest.OrderBy(v => v.CreatedUtc).Take(manifest.Count - MaxVersionsPerNote).ToList();
        foreach (var version in toRemove)
        {
            var path = SnapshotFilePath(folder, version.Id);
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Warning($"No se pudo eliminar la versión antigua '{path}'", ex);
                continue;
            }
            manifest.Remove(version);
        }
    }

    /// <summary>Moves a note's whole history folder to sit alongside it at <paramref name="newNotePath"/> —
    /// called by <see cref="RenameNote"/> and <see cref="MoveNote"/>/<see cref="MovePreservingEncryption"/>
    /// right after the note itself lands at its new path, mirroring <see cref="MoveAttachmentsFolder"/>'s
    /// reasoning exactly: history that stayed keyed to the old path would silently stop being found the
    /// moment the note moves, showing an empty history for a note that actually has one. Crosses an
    /// encryption boundary the same way attachments do — each snapshot is decrypted from its old root and
    /// re-encrypted under the new one via <see cref="WriteFileAtomic(string, string)"/>/<see cref="SafeReadContent"/>,
    /// never a raw byte-for-byte file move. A failure here is logged and swallowed: the note itself already
    /// moved successfully, so aborting would just leave orphaned history rather than undoing anything.</summary>
    private void MoveVersionHistory(string oldNotePath, string newNotePath)
    {
        var source = VersionsFolderFor(oldNotePath);
        var destination = VersionsFolderFor(newNotePath);
        if (PathsEqual(source, destination) || !Directory.Exists(source)) return;

        try
        {
            var manifest = LoadManifest(source);
            Directory.CreateDirectory(destination);

            foreach (var version in manifest)
            {
                var sourceFile = SnapshotFilePath(source, version.Id);
                if (!File.Exists(sourceFile)) continue;
                WriteFileAtomic(SnapshotFilePath(destination, version.Id), SafeReadContent(sourceFile));
            }

            SaveManifest(destination, manifest);
            Directory.Delete(source, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            LogService.Warning($"No se pudo mover el historial de versiones de '{oldNotePath}' a '{newNotePath}'", ex);
        }
    }

    /// <summary>Sidecar mapping a trashed note's flat path under <see cref="TrashPath"/> back to the full
    /// path it was trashed from — this feature's own copy of what <c>.trash-origins.json</c> already does for
    /// encryption, kept separate rather than reusing that file because that one is only ever written for
    /// notes that came from an encrypted session, while every trashed note's history needs to stay findable.
    ///
    /// <para>A trashed note's history folder is deliberately <em>not</em> physically moved into the trash: it
    /// stays exactly where <see cref="VersionsFolderFor"/> already puts it (nested inside the note's session
    /// when it has one), so its encryption never has to be reasoned about a second time for a location — the
    /// flat <see cref="TrashPath"/> folder — that <see cref="ResolveEncryptedSessionRoot"/> can't itself
    /// attribute to any session. This mapping is what lets <see cref="RestoreNoteHistory"/> and
    /// <see cref="PurgeNoteHistory"/> find that untouched folder again once the note itself is gone from its
    /// original location.</para></summary>
    private string HistoryOriginsPath => Path.Combine(TrashPath, ".history-origins.json");

    private Dictionary<string, string> ReadHistoryOrigins()
    {
        var comparer = PathComparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        return JsonFileStore.TryLoad<Dictionary<string, string>>(HistoryOriginsPath, out var loaded) && loaded is not null
            ? new Dictionary<string, string>(loaded, comparer)
            : new Dictionary<string, string>(comparer);
    }

    private void WriteHistoryOrigins(Dictionary<string, string> origins)
    {
        Directory.CreateDirectory(TrashPath);
        JsonFileStore.Save(HistoryOriginsPath, origins);
    }

    /// <summary>Records that <paramref name="trashedNotePath"/> was trashed from
    /// <paramref name="originalNotePath"/>, for <see cref="RestoreNoteHistory"/>/<see cref="PurgeNoteHistory"/>
    /// to resolve later — called from <see cref="DeleteNote"/> for every trashed note, not only encrypted
    /// ones, since a plain-text note's history is just as worth finding again on restore.</summary>
    private void TrashVersionHistory(string originalNotePath, string trashedNotePath)
    {
        if (!Directory.Exists(VersionsFolderFor(originalNotePath))) return;

        var origins = ReadHistoryOrigins();
        origins[trashedNotePath] = originalNotePath;
        WriteHistoryOrigins(origins);
    }

    /// <summary>Moves a trashed note's history folder back alongside it at its restored path — the
    /// counterpart to <see cref="TrashVersionHistory"/>, called from <see cref="RestoreNote"/> once the note
    /// itself has already landed at <paramref name="restoredNotePath"/>.</summary>
    private void RestoreNoteHistory(string trashedNotePath, string restoredNotePath)
    {
        var origins = ReadHistoryOrigins();
        if (!origins.TryGetValue(trashedNotePath, out var originalNotePath)) return;

        MoveVersionHistory(originalNotePath, restoredNotePath);
        if (origins.Remove(trashedNotePath)) WriteHistoryOrigins(origins);
    }

    /// <summary>Permanently deletes a trashed note's history folder — called from
    /// <see cref="PermanentlyDelete(NoteFile)"/> so purging a note from the trash doesn't leave its old
    /// snapshots behind forever with nothing left that could ever reference them again.</summary>
    private void PurgeNoteHistory(string trashedNotePath)
    {
        var origins = ReadHistoryOrigins();
        var notePathToPurge = origins.TryGetValue(trashedNotePath, out var originalNotePath) ? originalNotePath : trashedNotePath;

        var folder = VersionsFolderFor(notePathToPurge);
        if (Directory.Exists(folder))
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Warning($"No se pudo purgar el historial de versiones de '{notePathToPurge}'", ex);
            }
        }

        if (origins.Remove(trashedNotePath)) WriteHistoryOrigins(origins);
    }
}
