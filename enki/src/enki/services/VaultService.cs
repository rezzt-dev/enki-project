using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using enki.Localization;
using enki.Models;

namespace enki.Services;

public sealed class VaultService
{
    private readonly SettingsService _settingsService;

    /// <summary>Shared by every note-reading method below so a full vault scan reads each <c>.md</c> file
    /// at most once per change instead of every consumer (backlinks, search, quick switcher, the note
    /// grid) independently re-reading and re-parsing the same files (docs/analisis-proyecto/roadmap.md,
    /// Fase 6). See <see cref="VaultNoteCache"/> for the invalidation contract.</summary>
    private readonly VaultNoteCache _noteCache = new();

    public string VaultPath { get; private set; }

    public bool IsVaultConfigured => !string.IsNullOrWhiteSpace(VaultPath) && Directory.Exists(VaultPath);

    /// <summary>The comparison to use when deciding whether two paths identify the same file or folder on
    /// disk. Windows and macOS' default filesystems are case-insensitive, but Linux (ext4, btrfs, ...) is
    /// case-sensitive — comparing paths with <see cref="StringComparison.OrdinalIgnoreCase"/> unconditionally
    /// (as every path-identity check in this codebase used to) would treat <c>Todo.md</c> and <c>todo.md</c>
    /// as the same note on Linux even though the filesystem itself keeps them apart
    /// (docs/analisis-proyecto/roadmap.md, Fase 5). Not a general-purpose path-normalization utility — just
    /// the comparer for "are these two paths, already produced by this app, the same entry."</summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>Shorthand for comparing two vault paths with <see cref="PathComparison"/> — used everywhere
    /// this file (and <see cref="EditorBacklinksService"/>) needs to know whether two <see cref="NoteFile"/>
    /// references point at the same file on disk.</summary>
    public static bool PathsEqual(string a, string b) => string.Equals(a, b, PathComparison);

    public VaultService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        VaultPath = settingsService.Settings.VaultPath ?? string.Empty;
    }

    /// <summary>Configures the vault at <paramref name="path"/>, creating the directory if needed.
    /// Pass <paramref name="seedWelcomeNote"/> = true only when creating a brand-new vault; false when
    /// pointing at an existing folder, so its notes aren't disturbed.</summary>
    public void InitializeVault(string path, bool seedWelcomeNote)
    {
        Directory.CreateDirectory(path);
        VaultPath = path;
        _settingsService.Settings.VaultPath = path;
        _settingsService.Save();

        if (seedWelcomeNote && !Directory.EnumerateFiles(VaultPath, "*.md").Any())
        {
            var welcomePath = Path.Combine(VaultPath, LocalizationService.Instance["WelcomeNote_FileName"]);
            File.WriteAllText(welcomePath, LocalizationService.Instance["WelcomeNote_Content"]);
        }
    }

    public void SetVaultPath(string path)
    {
        Directory.CreateDirectory(path);
        VaultPath = path;
        _settingsService.Settings.VaultPath = path;
        _settingsService.Save();
    }

    /// <summary>True cuando la última llamada a <see cref="GetVaultSizeBytes"/> se topó con al menos un
    /// archivo ilegible (bloqueado, permisos) y por tanto el total devuelto es una subcuenta parcial —
    /// consultado por <see cref="ViewModels.DashboardViewModel"/> para avisar al usuario en vez de mostrar
    /// un tamaño incompleto como si fuera exacto (03-problemas-y-deuda-tecnica.md §1).</summary>
    public bool LastVaultSizeCalculationIncomplete { get; private set; }

    /// <summary>Fires once a background <see cref="GetVaultSizeBytes"/> recomputation (triggered by a
    /// stale/never-computed cache) finishes, so a subscriber (Dashboard, Ajustes) can re-read the now-fresh
    /// value and update its label — always raised from a background thread pool thread, never the UI
    /// thread, so subscribers must marshal back via <c>Dispatcher.UIThread.Post</c> themselves before
    /// touching bound properties, same convention already used elsewhere in this codebase for
    /// background-computed values (e.g. <c>FileCardViewModel</c>'s thumbnail).</summary>
    public event Action? VaultSizeChanged;

    private readonly object _vaultSizeLock = new();
    private long _cachedVaultSizeBytes;
    private bool _cachedVaultSizeIncomplete;
    private bool _vaultSizeCacheValid;
    private Task? _vaultSizeRefreshTask;

    /// <summary>Total size on disk of every file under the vault root, at any depth (notes, sessions,
    /// trash, kanban data, etc.) — used by the dashboard's storage stat, not just note content.
    ///
    /// <para>Returns the last cached value synchronously instead of walking the whole vault on every call
    /// (a vault with thousands of files made this block the UI thread on every Dashboard/Ajustes reload —
    /// docs/analisis-proyecto/roadmap.md, Fase 6, análisis §3, "el Dashboard no necesita el dato al
    /// segundo"). The real disk walk always happens on a background thread via
    /// <see cref="RefreshVaultSizeBytesAsync"/>, kicked off automatically the first time this is called (or
    /// after <see cref="InvalidateVaultSizeCache"/>) and reported back through <see cref="VaultSizeChanged"/>
    /// once it lands — so the very first read of a session (or right after a write) can briefly return 0
    /// before the background walk catches up, but every read after that is instant and correct as of the
    /// last known change.</para></summary>
    public long GetVaultSizeBytes()
    {
        lock (_vaultSizeLock)
        {
            if (_vaultSizeCacheValid)
            {
                LastVaultSizeCalculationIncomplete = _cachedVaultSizeIncomplete;
                return _cachedVaultSizeBytes;
            }
        }

        if (!IsVaultConfigured)
        {
            LastVaultSizeCalculationIncomplete = false;
            return 0;
        }

        _ = RefreshVaultSizeBytesAsync();
        LastVaultSizeCalculationIncomplete = false;
        return 0;
    }

    /// <summary>Kicks off (or, if one is already running, returns) a background recomputation of the vault
    /// size, updating the cache <see cref="GetVaultSizeBytes"/> reads from and raising
    /// <see cref="VaultSizeChanged"/> when it lands.</summary>
    public Task RefreshVaultSizeBytesAsync()
    {
        lock (_vaultSizeLock)
        {
            if (_vaultSizeRefreshTask is { IsCompleted: false } running) return running;

            var task = Task.Run(ComputeVaultSizeBytes).ContinueWith(t =>
            {
                var (size, incomplete) = t.Result;
                lock (_vaultSizeLock)
                {
                    _cachedVaultSizeBytes = size;
                    _cachedVaultSizeIncomplete = incomplete;
                    _vaultSizeCacheValid = true;
                }
                VaultSizeChanged?.Invoke();
            }, TaskScheduler.Default);
            _vaultSizeRefreshTask = task;
            return task;
        }
    }

    /// <summary>Drops the cached vault size so the next <see cref="GetVaultSizeBytes"/> call triggers a
    /// fresh background walk — called by every write path below that changes how many bytes live under the
    /// vault root.</summary>
    private void InvalidateVaultSizeCache()
    {
        lock (_vaultSizeLock) { _vaultSizeCacheValid = false; }
    }

    private (long Size, bool Incomplete) ComputeVaultSizeBytes()
    {
        if (string.IsNullOrWhiteSpace(VaultPath) || !Directory.Exists(VaultPath)) return (0, false);

        long total = 0;
        var incomplete = false;
        foreach (var file in Directory.EnumerateFiles(VaultPath, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { incomplete = true; }
            catch (UnauthorizedAccessException) { incomplete = true; }
        }
        return (total, incomplete);
    }

    public List<NoteFile> GetNotes(string? folderPath = null)
    {
        folderPath ??= VaultPath;
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return [];

        return EnumerateMarkdownFiles(folderPath, SearchOption.TopDirectoryOnly)
            .Select(info => _noteCache.GetOrRead(info, ReadNoteAndContent).Note)
            .OrderByDescending(n => n.ModifiedUtc)
            .ToList();
    }

    public List<NoteFolder> GetFolderTree(string? root = null)
    {
        root ??= VaultPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return [];
        return BuildFolderChildren(root);
    }

    /// <summary>All notes under <paramref name="root"/>, at any depth — unlike <see cref="GetNotes"/>
    /// (a single folder's notes for the browsing grid), this powers session graphs, which relate every
    /// file in the session regardless of which subfolder it lives in.</summary>
    public List<NoteFile> GetAllNotesRecursive(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return [];

        return EnumerateMarkdownFiles(root, SearchOption.AllDirectories)
            .Where(info => !IsUnderHiddenDirectory(root, info.FullName))
            .Select(info => _noteCache.GetOrRead(info, ReadNoteAndContent).Note)
            .OrderByDescending(n => n.ModifiedUtc)
            .ToList();
    }

    /// <summary>Same file set as <see cref="GetAllNotesRecursive"/>, but also hands back each note's raw
    /// content alongside its metadata — callers that need both (session graphs need title for the node
    /// label and content to extract <c>[[wikilinks]]</c>, the backlinks panel needs content to find
    /// occurrences and metadata to resolve what they point to) used to call this (or
    /// <see cref="GetAllNotesRecursive"/>) and then re-read every file's content themselves, hitting disk
    /// twice per note per refresh. Both now share <see cref="_noteCache"/>, so the second read is a
    /// dictionary lookup instead of a second pass over the vault (docs/analisis-proyecto/roadmap.md, Fase
    /// 6, análisis §3).</summary>
    public List<(NoteFile Note, string Content)> GetAllNotesRecursiveWithContent(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return [];

        return EnumerateMarkdownFiles(root, SearchOption.AllDirectories)
            .Where(info => !IsUnderHiddenDirectory(root, info.FullName))
            .Select(info => _noteCache.GetOrRead(info, ReadNoteAndContent))
            .OrderByDescending(t => t.Note.ModifiedUtc)
            .ToList();
    }

    /// <summary>Enumerates <c>.md</c> files as <see cref="FileInfo"/> rather than plain path strings — the
    /// OS enumeration API already has to stat each entry to report it, so this carries
    /// <see cref="FileInfo.LastWriteTimeUtc"/> for free, which <see cref="_noteCache"/> needs to validate a
    /// cached entry without an extra per-file stat call.</summary>
    private static IEnumerable<FileInfo> EnumerateMarkdownFiles(string root, SearchOption option) =>
        new DirectoryInfo(root).EnumerateFiles("*.md", option);

    private static (NoteFile Note, string Content) ReadNoteAndContent(FileInfo info)
    {
        var content = SafeReadAllText(info.FullName);
        return (ReadNoteMetadata(info, content), content);
    }

    private static bool IsUnderHiddenDirectory(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Take(segments.Length - 1).Any(s => s.StartsWith('.'));
    }

    private static List<NoteFolder> BuildFolderChildren(string parentPath)
    {
        var result = new List<NoteFolder>();
        foreach (var dir in Directory.EnumerateDirectories(parentPath)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;
            result.Add(new NoteFolder { FullPath = dir, Children = BuildFolderChildren(dir) });
        }
        return result;
    }

    public static NoteFolder CreateFolder(string parentPath, string? desiredName = null)
    {
        var safeName = SanitizeFileName(desiredName ?? LocalizationService.Instance["Vault_DefaultFolderName"]);
        var path = Path.Combine(parentPath, safeName);
        var suffix = 2;
        while (Directory.Exists(path))
        {
            path = Path.Combine(parentPath, $"{safeName} {suffix}");
            suffix++;
        }

        Directory.CreateDirectory(path);
        return new NoteFolder { FullPath = path };
    }

    /// <summary>Renames <paramref name="folder"/>, adding a numeric suffix (" 2", " 3", ...) instead of
    /// silently no-op'ing when the destination name already exists — same collision policy as
    /// <see cref="CreateFolder"/>/<see cref="RenameNote"/>/<see cref="RenameFile"/>, so there's never a
    /// "ghost rename" where the caller's view of the name changed but the folder on disk didn't
    /// (docs/analisis-proyecto/roadmap.md, Fase 5). Compare the returned folder's <c>FullPath</c> against
    /// what was requested to know whether a suffix was actually applied.</summary>
    public static NoteFolder RenameFolder(NoteFolder folder, string newName)
    {
        var safeName = SanitizeFileName(newName);
        var parent = Path.GetDirectoryName(folder.FullPath);
        if (parent is null) return folder;

        var newPath = Path.Combine(parent, safeName);
        if (PathsEqual(newPath, folder.FullPath)) return folder;

        if (Directory.Exists(newPath)) newPath = ResolveCollision(parent, safeName, extension: null);

        Directory.Move(folder.FullPath, newPath);
        folder.FullPath = newPath;
        return folder;
    }

    public void DeleteFolder(NoteFolder folder)
    {
        if (!Directory.Exists(folder.FullPath)) return;

        Directory.CreateDirectory(TrashPath);
        var destination = Path.Combine(TrashPath, Path.GetFileName(folder.FullPath));
        var suffix = 2;
        while (Directory.Exists(destination))
        {
            destination = Path.Combine(TrashPath, $"{Path.GetFileName(folder.FullPath)} {suffix}");
            suffix++;
        }

        Directory.Move(folder.FullPath, destination);
    }

    public NoteFile MoveNote(NoteFile note, string destinationFolderPath)
    {
        EnsureVaultAvailable();
        var oldPath = note.FullPath;
        note.FullPath = MoveWithUniqueSuffix(note.FullPath, destinationFolderPath);
        _noteCache.Invalidate(oldPath);
        return note;
    }

    /// <summary>Moves <paramref name="sourcePath"/> into <paramref name="destinationFolder"/>, appending
    /// a numeric suffix (" 2", " 3", ...) before the extension if a file with the same name already
    /// exists there — shared collision-avoidance for every move/trash/restore operation below, notes and
    /// files alike, instead of duplicating the same loop for each one. Logs and rethrows on I/O failure
    /// (docs/analisis-proyecto/roadmap.md, Fase 1) instead of letting a bare exception surface with no
    /// trace of which move it was.</summary>
    private static string MoveWithUniqueSuffix(string sourcePath, string destinationFolder)
    {
        var fileName = Path.GetFileName(sourcePath);
        var destination = Path.Combine(destinationFolder, fileName);
        var suffix = 2;
        while (File.Exists(destination))
        {
            destination = Path.Combine(destinationFolder,
                $"{Path.GetFileNameWithoutExtension(sourcePath)} {suffix}{Path.GetExtension(sourcePath)}");
            suffix++;
        }

        try
        {
            File.Move(sourcePath, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error($"No se pudo mover '{sourcePath}' a '{destination}'", ex);
            throw;
        }

        return destination;
    }

    /// <summary>Appends a numeric suffix (" 2", " 3", ...) to <paramref name="safeName"/> until it no
    /// longer collides with an existing entry in <paramref name="dir"/> — the rename counterpart of
    /// <see cref="MoveWithUniqueSuffix"/>'s collision handling, used by
    /// <see cref="RenameNote"/>/<see cref="RenameFile"/>/<see cref="RenameFolder"/> so a rename to an
    /// already-taken name gets a distinct final name instead of silently doing nothing
    /// (docs/analisis-proyecto/roadmap.md, Fase 5). <paramref name="extension"/> includes the leading dot
    /// (e.g. <c>".md"</c>), or is null for a folder.</summary>
    private static string ResolveCollision(string dir, string safeName, string? extension)
    {
        var suffix = 2;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{safeName} {suffix}{extension}");
            suffix++;
        } while (extension is null ? Directory.Exists(candidate) : File.Exists(candidate));

        return candidate;
    }

    private static NoteFile ReadNoteMetadata(string fullPath) => ReadNoteMetadata(new FileInfo(fullPath), SafeReadAllText(fullPath));

    private static NoteFile ReadNoteMetadata(FileInfo info, string content)
    {
        var tags = NoteTagHelper.ExtractTags(content);
        var stripped = NoteTagHelper.StripFrontMatter(content);
        var (title, preview) = ExtractTitleAndPreview(stripped, Path.GetFileNameWithoutExtension(info.Name));

        return new NoteFile
        {
            FullPath = info.FullName,
            Title = title,
            Preview = preview,
            Tags = tags,
            ModifiedUtc = info.LastWriteTimeUtc,
            WordCount = CountWords(content)
        };
    }

    private static string SafeReadAllText(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return string.Empty; }
    }

    private static (string Title, string Preview) ExtractTitleAndPreview(string content, string fallbackTitle)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        string? title = null;
        var bodyLines = new List<string>();

        foreach (var line in lines)
        {
            if (title is null && line.TrimStart().StartsWith('#'))
            {
                title = line.TrimStart('#', ' ').Trim();
                continue;
            }
            if (!string.IsNullOrWhiteSpace(line)) bodyLines.Add(line);
        }

        title ??= fallbackTitle;
        if (string.IsNullOrWhiteSpace(title)) title = LocalizationService.Instance["Vault_UntitledNoteTitle"];

        var body = string.Join(" ", bodyLines.Where(l => l != title));
        body = Regex.Replace(body, @"[#*`_>\-\[\]!]", " ");
        body = Regex.Replace(body, @"\s+", " ").Trim();

        var preview = body.Length > 160 ? body[..160].TrimEnd() + "…" : body;
        return (title, preview);
    }

    private static int CountWords(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return 0;
        return content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>Reads a single note's metadata from an arbitrary path — e.g. a session graph node, whose
    /// only durable identity is the path itself. Returns null if the file no longer exists.</summary>
    public NoteFile? GetNote(string fullPath) =>
        File.Exists(fullPath) ? _noteCache.GetOrRead(new FileInfo(fullPath), ReadNoteAndContent).Note : null;

    public string ReadContent(NoteFile note) =>
        File.Exists(note.FullPath) ? _noteCache.GetOrRead(new FileInfo(note.FullPath), ReadNoteAndContent).Content : string.Empty;

    /// <summary>Writes a note's Markdown content atomically: a temp file in the same directory, then
    /// <see cref="File.Move(string, string, bool)"/> onto <see cref="NoteFile.FullPath"/> — same pattern as
    /// <see cref="JsonFileStore.Save{T}"/>, applied here to the highest-value content in the app (the notes
    /// themselves), which used to go through a direct, non-atomic <see cref="File.WriteAllText(string, string)"/>
    /// (docs/analisis-proyecto/roadmap.md, Fase 1). A reader (backlinks scan, session graph, another Enki
    /// window) never observes a half-written file, and a failure partway through leaves the original note
    /// untouched instead of truncated. Checks vault availability first and logs+rethrows on failure instead
    /// of swallowing it, so the caller (<c>EditorViewModel.Save</c>) can react instead of losing the edit
    /// in silence.</summary>
    public void WriteContent(NoteFile note, string content)
    {
        EnsureVaultAvailable();
        WriteFileAtomic(note.FullPath, content);
        CacheAfterWrite(note.FullPath, content);
    }

    public NoteFile CreateNote(string? desiredTitle = null, string? folderPath = null)
    {
        EnsureVaultAvailable();

        desiredTitle ??= LocalizationService.Instance["Vault_DefaultNoteTitle"];
        folderPath ??= VaultPath;
        var safeName = SanitizeFileName(desiredTitle);
        var path = Path.Combine(folderPath, $"{safeName}.md");
        var suffix = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(folderPath, $"{safeName} {suffix}.md");
            suffix++;
        }

        var content = $"# {desiredTitle}\n\n";
        WriteFileAtomic(path, content);
        return CacheAfterWrite(path, content);
    }

    /// <summary>Seeds <see cref="_noteCache"/> straight from the content this call already just wrote to
    /// disk, so the next read of this note (autosave's own <c>EditorViewModel</c> tab, the note grid after
    /// <c>Reload()</c>) is a cache hit instead of a redundant re-read of the file this process itself just
    /// finished writing — and marks the vault-size stat stale, since a write may have changed how many
    /// bytes live on disk (docs/analisis-proyecto/roadmap.md, Fase 6).</summary>
    private NoteFile CacheAfterWrite(string path, string content)
    {
        var info = new FileInfo(path);
        var note = ReadNoteMetadata(info, content);
        _noteCache.Put(path, note, content, info.LastWriteTimeUtc);
        InvalidateVaultSizeCache();
        // Cloned, not the same instance just stored above — the caller (EditorViewModel.Save,
        // NotesViewModel.NewNote, ...) routinely mutates its own copy of a returned NoteFile in place, which
        // must never reach back into the cache entry (see NoteFile.Clone's doc comment).
        return note.Clone();
    }

    /// <summary>Throws <see cref="VaultUnavailableException"/> when <see cref="VaultPath"/> doesn't
    /// currently resolve to an existing directory (unplugged drive, folder moved/deleted out-of-band) —
    /// called proactively at the top of every write operation that touches the vault, instead of letting
    /// the first symptom be a raw <see cref="DirectoryNotFoundException"/> surfacing from deep inside
    /// autosave. (docs/analisis-proyecto/roadmap.md, Fase 1).</summary>
    private void EnsureVaultAvailable()
    {
        if (!IsVaultConfigured) throw new VaultUnavailableException(VaultPath);
    }

    /// <summary>Shared atomic-write primitive for plain (non-JSON) vault content — a temp file next to
    /// <paramref name="path"/>, then an atomic <see cref="File.Move(string, string, bool)"/> onto it.
    /// Logs and rethrows on <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> (which also
    /// covers <see cref="DirectoryNotFoundException"/>) instead of swallowing it, so a disk-full, permission,
    /// or missing-path failure leaves a trace in <see cref="LogService"/> and still reaches the caller.</summary>
    private static void WriteFileAtomic(string path, string content)
    {
        try
        {
            var tempPath = $"{path}.tmp-{Guid.NewGuid():N}";
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error($"No se pudo escribir el archivo '{path}'", ex);
            throw;
        }
    }

    public NoteFile ImportFile(string sourcePath, string? folderPath = null)
    {
        folderPath ??= VaultPath;
        var fileName = Path.GetFileName(sourcePath);
        var destination = Path.Combine(folderPath, fileName);
        var suffix = 2;
        while (File.Exists(destination))
        {
            destination = Path.Combine(folderPath,
                $"{Path.GetFileNameWithoutExtension(fileName)} {suffix}{Path.GetExtension(fileName)}");
            suffix++;
        }

        File.Copy(sourcePath, destination);
        InvalidateVaultSizeCache();
        return ReadNoteMetadata(destination);
    }

    private string TrashPath => Path.Combine(VaultPath, ".trash");

    public void DeleteNote(NoteFile note)
    {
        if (!File.Exists(note.FullPath)) return;

        Directory.CreateDirectory(TrashPath);
        MoveWithUniqueSuffix(note.FullPath, TrashPath);
        _noteCache.Invalidate(note.FullPath);
        InvalidateVaultSizeCache();
    }

    public List<NoteFile> GetTrashedNotes()
    {
        if (!Directory.Exists(TrashPath)) return [];

        return EnumerateMarkdownFiles(TrashPath, SearchOption.TopDirectoryOnly)
            .Select(info => _noteCache.GetOrRead(info, ReadNoteAndContent).Note)
            .OrderByDescending(n => n.ModifiedUtc)
            .ToList();
    }

    public void RestoreNote(NoteFile note)
    {
        if (!File.Exists(note.FullPath)) return;
        MoveWithUniqueSuffix(note.FullPath, VaultPath);
        _noteCache.Invalidate(note.FullPath);
        InvalidateVaultSizeCache();
    }

    public void PermanentlyDelete(NoteFile note)
    {
        if (!File.Exists(note.FullPath)) return;
        File.Delete(note.FullPath);
        _noteCache.Invalidate(note.FullPath);
        InvalidateVaultSizeCache();
    }

    /// <summary>Renames <paramref name="note"/>'s file and, unlike before Fase 5, keeps the rest of the
    /// vault's wikilink graph pointing at it: every other note in its scope
    /// (<see cref="ResolveWikiLinkScopeRoot"/>) whose <c>[[...]]</c> references resolved to the old file —
    /// by filename or by unambiguous title, same rule <see cref="WikiLinkParser.ResolveTarget"/> uses
    /// everywhere else — gets rewritten to reference the new filename instead
    /// (docs/analisis-proyecto/roadmap.md, Fase 5, análisis §3 "Integridad de la red de enlaces"). A
    /// destination name that already exists gets a numeric suffix (same policy as
    /// <see cref="CreateNote"/>/<see cref="RenameFolder"/>/<see cref="RenameFile"/>) instead of silently
    /// doing nothing — compare the returned note's file name against <paramref name="newTitle"/> to know
    /// whether a suffix was applied.</summary>
    public NoteFile RenameNote(NoteFile note, string newTitle)
    {
        EnsureVaultAvailable();

        var safeName = SanitizeFileName(newTitle);
        var currentDir = Path.GetDirectoryName(note.FullPath) ?? VaultPath;
        var newPath = Path.Combine(currentDir, $"{safeName}.md");
        if (PathsEqual(newPath, note.FullPath)) return note;

        if (File.Exists(newPath)) newPath = ResolveCollision(currentDir, safeName, ".md");

        var oldPath = note.FullPath;
        var scopeRoot = ResolveWikiLinkScopeRoot(oldPath);
        var notesBeforeRename = GetAllNotesRecursiveWithContent(scopeRoot);

        try
        {
            File.Move(oldPath, newPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error($"No se pudo renombrar '{oldPath}' a '{newPath}'", ex);
            throw;
        }

        note.FullPath = newPath;
        RewriteWikilinksToRenamedNote(notesBeforeRename, oldPath, Path.GetFileNameWithoutExtension(newPath));

        // The rewrite pass above may have touched any note in the pre-rename snapshot (plus the renamed
        // note itself moved off oldPath entirely) — invalidate the whole snapshot rather than tracking
        // exactly which ones changed, since a rename is a rare, user-initiated action where a few extra
        // cache misses on the next scan cost nothing compared to the risk of a stale entry surviving it.
        _noteCache.Invalidate(oldPath);
        foreach (var (noteFile, _) in notesBeforeRename) _noteCache.Invalidate(noteFile.FullPath);
        InvalidateVaultSizeCache();

        return note;
    }

    /// <summary>The narrowest note tree containing <paramref name="notePath"/>: the session it lives under
    /// (<c>&lt;vault&gt;/.sessions/&lt;name&gt;/...</c>), or the vault root for a note outside every
    /// session. Single source of truth for wikilink scope — <see cref="RenameNote"/>'s cascading rewrite,
    /// the editor's Ctrl+click (<c>ViewModels.EditorViewModel.OpenWikiLink</c>) and the delete-time backlink
    /// warning (<see cref="EditorBacklinksService.WarnIfDeletingLeavesBrokenLinks"/>) all resolve against
    /// this same root, so none of them can reach (or miss) a note the others wouldn't
    /// (docs/analisis-proyecto/roadmap.md, Fase 5).</summary>
    public string ResolveWikiLinkScopeRoot(string notePath)
    {
        var sessionsRoot = Path.Combine(VaultPath, ".sessions") + Path.DirectorySeparatorChar;
        if (!notePath.StartsWith(sessionsRoot, StringComparison.OrdinalIgnoreCase)) return VaultPath;

        var relative = Path.GetRelativePath(sessionsRoot, notePath);
        var sessionFolder = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return Path.Combine(sessionsRoot, sessionFolder);
    }

    /// <summary>Rewrites, in every note captured by <paramref name="notesBeforeRename"/>, each wikilink
    /// occurrence that resolved (against that pre-rename snapshot) to <paramref name="oldPath"/> — replacing
    /// its target text with <paramref name="newLinkName"/> while preserving any <c>|alias</c>/<c>#anchor</c>
    /// suffix. Writes go through the same atomic path as everything else in this class; a note that fails
    /// to rewrite (locked, permissions) is logged and skipped rather than aborting the rename that already
    /// succeeded — a stale link on one note is recoverable, an interrupted rename is not.</summary>
    private static void RewriteWikilinksToRenamedNote(
        IReadOnlyList<(NoteFile Note, string Content)> notesBeforeRename, string oldPath, string newLinkName)
    {
        var notesSnapshot = notesBeforeRename.Select(t => t.Note).ToList();
        var newDir = Path.GetDirectoryName(oldPath)!;
        var renamedNotePath = Path.Combine(newDir, newLinkName + ".md");

        foreach (var (noteFile, content) in notesBeforeRename)
        {
            var rewritten = RewriteLinksTo(content, notesSnapshot, oldPath, newLinkName);
            if (rewritten == content) continue;

            // The renamed note itself now lives at its new path on disk; every other note keeps its own.
            var writePath = PathsEqual(noteFile.FullPath, oldPath) ? renamedNotePath : noteFile.FullPath;

            try
            {
                WriteFileAtomic(writePath, rewritten);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Error($"No se pudo actualizar wikilinks en '{writePath}' tras renombrar '{oldPath}'", ex);
            }
        }
    }

    private static string RewriteLinksTo(string content, IReadOnlyList<NoteFile> notes, string oldPath, string newLinkName)
    {
        var occurrences = WikiLinkParser.FindOccurrences(content).ToList();
        if (occurrences.Count == 0) return content;

        var changed = false;
        var sb = new StringBuilder(content.Length);
        var cursor = 0;

        foreach (var occurrence in occurrences)
        {
            var resolved = WikiLinkParser.ResolveTarget(notes, occurrence.Target);
            if (resolved is null || !PathsEqual(resolved.FullPath, oldPath)) continue;

            sb.Append(content, cursor, occurrence.Start - cursor);
            sb.Append("[[").Append(newLinkName).Append(ExtractLinkSuffix(occurrence.RawText)).Append("]]");
            cursor = occurrence.End;
            changed = true;
        }

        if (!changed) return content;

        sb.Append(content, cursor, content.Length - cursor);
        return sb.ToString();
    }

    /// <summary>Given a raw <c>[[Target|alias]]</c>/<c>[[Target#anchor]]</c> match, returns everything from
    /// the first <c>|</c>/<c>#</c> up to (but excluding) the closing <c>]]</c> — empty for a plain
    /// <c>[[Target]]</c> link. Preserved verbatim by <see cref="RewriteLinksTo"/> so a rename never drops a
    /// display alias or a heading anchor.</summary>
    private static string ExtractLinkSuffix(string rawText)
    {
        var inner = rawText[2..^2];
        var separatorIndex = inner.IndexOfAny(['|', '#']);
        return separatorIndex < 0 ? string.Empty : inner[separatorIndex..];
    }

    // ===================== Archivos (binarios no-.md) =====================
    // Rama nueva de métodos sobre el mismo VaultService en vez de un FilesService paralelo — el almacén
    // real es el propio sistema de archivos del vault, y VaultPath/TrashPath/SanitizeFileName/
    // GetFolderTree/ImportFile ya son genéricos pese a vivir junto a los métodos de notas. Solo la
    // detección de metadatos difiere: aquí es FileInfo puro, sin parsear contenido como ReadNoteMetadata.

    public List<VaultFile> GetFiles(string? folderPath = null)
    {
        folderPath ??= VaultPath;
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return [];

        return Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Select(ReadFileMetadata)
            .OrderByDescending(f => f.ModifiedUtc)
            .ToList();
    }

    private static VaultFile ReadFileMetadata(string fullPath)
    {
        var info = new FileInfo(fullPath);
        return new VaultFile
        {
            FullPath = fullPath,
            ModifiedUtc = info.LastWriteTimeUtc,
            SizeBytes = info.Length,
            Kind = VaultFile.KindFromExtension(info.Extension)
        };
    }

    public static VaultFile MoveFile(VaultFile file, string destinationFolderPath)
    {
        file.FullPath = MoveWithUniqueSuffix(file.FullPath, destinationFolderPath);
        return file;
    }

    /// <summary>Renames <paramref name="file"/>, adding a numeric suffix before the extension instead of
    /// silently no-op'ing when the destination name already exists — same collision policy as
    /// <see cref="RenameNote"/>/<see cref="RenameFolder"/> (docs/analisis-proyecto/roadmap.md, Fase 5).</summary>
    public VaultFile RenameFile(VaultFile file, string newFileName)
    {
        var safeName = SanitizeFileName(newFileName, fallback: file.FileName);
        var currentDir = Path.GetDirectoryName(file.FullPath) ?? VaultPath;
        var newPath = Path.Combine(currentDir, safeName);
        if (PathsEqual(newPath, file.FullPath)) return file;

        if (File.Exists(newPath))
            newPath = ResolveCollision(currentDir, Path.GetFileNameWithoutExtension(safeName), Path.GetExtension(safeName));

        File.Move(file.FullPath, newPath);
        file.FullPath = newPath;
        return file;
    }

    public void DeleteFile(VaultFile file)
    {
        if (!File.Exists(file.FullPath)) return;

        Directory.CreateDirectory(TrashPath);
        MoveWithUniqueSuffix(file.FullPath, TrashPath);
    }

    public List<VaultFile> GetTrashedFiles()
    {
        if (!Directory.Exists(TrashPath)) return [];

        return Directory.EnumerateFiles(TrashPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Select(ReadFileMetadata)
            .OrderByDescending(f => f.ModifiedUtc)
            .ToList();
    }

    public void RestoreFile(VaultFile file)
    {
        if (!File.Exists(file.FullPath)) return;
        MoveWithUniqueSuffix(file.FullPath, VaultPath);
    }

    public void PermanentlyDelete(VaultFile file)
    {
        if (!File.Exists(file.FullPath)) return;
        File.Delete(file.FullPath);
        InvalidateVaultSizeCache();
    }

    internal static string SanitizeFileName(string name, string? fallback = null)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? (fallback ?? LocalizationService.Instance["Vault_DefaultNoteTitle"]) : cleaned;
    }
}
