using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using enki.Localization;
using enki.Models;

namespace enki.Services;

public sealed partial class VaultService
{
    /// <summary>Filename of a session's manifest, duplicated from <see cref="SessionService"/>'s own
    /// constant — <see cref="ResolveEncryptedSessionRoot"/> reads it directly instead of going through
    /// <see cref="SessionService"/>, which itself depends on <see cref="VaultService"/> and would create a
    /// circular dependency.</summary>
    private const string SessionManifestFileName = ".session.json";

    private readonly SettingsService _settingsService;
    private readonly SessionEncryptionService _encryptionService;

    /// <summary>Shared with <see cref="SessionService"/> (constructed from the same <see cref="VaultService"/>
    /// instance) so both sides of the choke point — content read/write here, unlock/lock lifecycle there —
    /// agree on which sessions are currently unlocked.</summary>
    public SessionEncryptionService EncryptionService => _encryptionService;

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

    public VaultService(SettingsService settingsService) : this(settingsService, new SessionEncryptionService())
    {
    }

    public VaultService(SettingsService settingsService, SessionEncryptionService encryptionService)
    {
        _settingsService = settingsService;
        _encryptionService = encryptionService;
        VaultPath = settingsService.Settings.VaultPath ?? string.Empty;
    }

    /// <summary>Cached answer to "is the session rooted at this path encrypted?", keyed by session root and
    /// validated against the manifest's <see cref="FileInfo.LastWriteTimeUtc"/>. Before this existed,
    /// <see cref="ResolveEncryptedSessionRoot"/> deserialized <c>.session.json</c> from disk on *every*
    /// content read and write — including the editor's 600 ms autosave and every note of a full-vault
    /// scan — which is the cost its own doc comment flagged as "worth caching later if profiling ever
    /// shows it matters". A full-text search over a session-heavy vault is what made it matter.</summary>
    private readonly Dictionary<string, (bool IsEncrypted, DateTime ManifestWriteUtc)> _sessionEncryptionCache =
        new(PathComparerFor());

    private static StringComparer PathComparerFor() =>
        PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Drops the cached encryption status for <paramref name="sessionRootPath"/> (or all of them
    /// when null) — called by <see cref="SessionService"/> whenever it writes a manifest, so a session that
    /// just became encrypted (or was restored from the trash under a new folder name) is never served from
    /// a stale entry. The mtime check in <see cref="ResolveEncryptedSessionRoot"/> already covers edits made
    /// outside the app; this covers the same-tick case where a write and a read share a filesystem timestamp
    /// granularity too coarse to tell them apart.</summary>
    internal void InvalidateSessionEncryptionCache(string? sessionRootPath = null)
    {
        lock (_sessionEncryptionCache)
        {
            if (sessionRootPath is null) _sessionEncryptionCache.Clear();
            else _sessionEncryptionCache.Remove(sessionRootPath);
        }
    }

    /// <summary>Null if <paramref name="fullPath"/> isn't under any session, or is under a plain-text one.
    /// Otherwise, the root path of the encrypted session it belongs to — the same identifier
    /// <see cref="SessionEncryptionService"/> uses as its unlocked-DEK cache key. Reads
    /// <c>.session.json</c> directly with its own minimal JSON read (see <see cref="SessionManifestFileName"/>'s
    /// doc comment for why this doesn't go through <see cref="SessionService"/>), memoized per session root
    /// in <see cref="_sessionEncryptionCache"/>.
    ///
    /// <para><b>Fails closed.</b> A manifest that exists but can't be read or parsed used to return null —
    /// i.e. "this session is plain text" — which is the most dangerous possible guess: the very next
    /// <see cref="WriteFileAtomic"/> would write the user's note to disk <em>unencrypted</em> inside a
    /// folder they created precisely so its contents would never be, and would do it silently. A corrupt
    /// manifest now raises <see cref="SessionLockedException"/> instead, so the session behaves exactly
    /// like a locked one (readable name/note count in the sidebar, no content access) until the manifest
    /// is repaired.</para></summary>
    private string? ResolveEncryptedSessionRoot(string fullPath)
    {
        if (string.IsNullOrEmpty(VaultPath)) return null;

        // The vault-wide trash sits outside every session's .sessions/<name>/ boundary, so a note/file
        // moved there by DeleteNote/DeleteFile can no longer be recognized as belonging to an encrypted
        // session just from its current (trash) path — resolve against the path it was trashed FROM
        // instead, recorded by RecordTrashOrigin at delete time. Without this, a trashed item from a
        // protected session was silently treated as plain-text: read as garbage while in the trash, and
        // left permanently undecryptable once restored, since AES-GCM ciphertext bytes were never
        // recognized as needing decryption again.
        if (PathsEqual(Path.GetDirectoryName(fullPath) ?? string.Empty, TrashPath) &&
            ReadTrashOrigins().TryGetValue(fullPath, out var originalPath))
        {
            fullPath = originalPath;
        }

        var sessionsRoot = Path.Combine(VaultPath, ".sessions");
        var relative = Path.GetRelativePath(sessionsRoot, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return null;

        var sessionFolder = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (string.IsNullOrEmpty(sessionFolder)) return null;

        var sessionRoot = Path.Combine(sessionsRoot, sessionFolder);
        var manifestPath = Path.Combine(sessionRoot, SessionManifestFileName);

        // No manifest at all: an orphaned folder someone dropped under .sessions/ by hand, which List()
        // also treats as a plain session via a synthetic manifest. Nothing was ever encrypted here, so
        // there's no ciphertext to protect and plain text is the correct answer, not a guess.
        var manifestInfo = new FileInfo(manifestPath);
        if (!manifestInfo.Exists) return null;

        var manifestWriteUtc = manifestInfo.LastWriteTimeUtc;
        lock (_sessionEncryptionCache)
        {
            if (_sessionEncryptionCache.TryGetValue(sessionRoot, out var cached) && cached.ManifestWriteUtc == manifestWriteUtc)
                return cached.IsEncrypted ? sessionRoot : null;
        }

        bool isEncrypted;
        try
        {
            var manifest = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(manifestPath));
            isEncrypted = manifest?.Encryption is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fail closed — see this method's doc comment. Deliberately NOT cached: the manifest may become
            // readable again (a transient lock, a restored backup) and nothing should keep serving the
            // blocked answer once it does.
            LogService.Error($"No se pudo leer el manifiesto de sesión '{manifestPath}'; se bloquea el acceso a su contenido en vez de tratarlo como texto plano", ex);
            throw new SessionLockedException(sessionRoot);
        }

        lock (_sessionEncryptionCache)
        {
            _sessionEncryptionCache[sessionRoot] = (isEncrypted, manifestWriteUtc);
        }
        return isEncrypted ? sessionRoot : null;
    }

    /// <summary>Fixed (non-localized) name of the subfolder Enki keeps its vault content in, nested one
    /// level below whatever folder the user picks in the folder dialog (docs/ajustes-docs — "el usuario
    /// elige 'Documentos', el vault real vive en 'Documentos/enki-documents'"). Keeping it constant across
    /// languages/OSes means a vault created under one locale is still recognized as one after a language
    /// change.</summary>
    private const string VaultSubfolderName = "enki-documents";

    /// <summary>Resolves the folder a user just picked in an OS folder dialog to the actual vault root to
    /// use — never the picked folder itself unless it already looks like a vault. Without this, picking a
    /// general-purpose folder like "Documentos" turned that whole folder into the vault, scattering
    /// <c>.trash</c>/<c>.sessions</c>/notes directly alongside the user's unrelated files. Instead:
    /// <list type="bullet">
    /// <item>a folder that already looks like a vault (its name is already
    /// <see cref="VaultSubfolderName"/>, or it already holds vault content — notes, sessions, or a trash
    /// folder) is used as-is, so an existing vault (old-style flat layout, or one already nested under
    /// <see cref="VaultSubfolderName"/> from a prior pick) is never silently orphaned;</item>
    /// <item>otherwise, <see cref="VaultSubfolderName"/> nested inside the picked folder is used/created —
    /// covering both a brand-new pick and re-selecting the same parent folder a second time.</item>
    /// </list></summary>
    public static string ResolveVaultFolder(string pickedFolder)
    {
        var nested = Path.Combine(pickedFolder, VaultSubfolderName);
        if (Directory.Exists(nested)) return nested;
        if (LooksLikeExistingVaultRoot(pickedFolder)) return pickedFolder;
        return nested;
    }

    private static bool LooksLikeExistingVaultRoot(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(name, VaultSubfolderName, StringComparison.OrdinalIgnoreCase)) return true;
        if (Directory.Exists(Path.Combine(folder, ".tasks"))) return true;

        if (!Directory.Exists(folder)) return false;
        if (Directory.Exists(Path.Combine(folder, ".sessions")) || Directory.Exists(Path.Combine(folder, ".trash")))
            return true;

        try
        {
            // A real vault can have every single one of its notes filed away in subfolders (e.g. "Trabajo/",
            // "Personal/") without ever having used sessions or the trash — TopDirectoryOnly missed that
            // case entirely, so re-selecting exactly that folder as "open existing vault" (after syncing it
            // to a new machine, or reinstalling) got it classified as a general-purpose folder and nested a
            // brand-new, empty enki-documents/ inside it instead of recognizing it
            // (docs/version-alfa-3/01-analisis-detallado.md §4.5, hallazgo crítico).
            return EnumerateMarkdownFiles(folder, SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An inaccessible subfolder somewhere under a general-purpose folder shouldn't block vault
            // setup — fall through to "doesn't look like a vault yet" and nest enki-documents/ as usual.
            return false;
        }
    }

    /// <summary>Configures the vault at <paramref name="path"/>, creating the directory if needed.
    /// Pass <paramref name="seedWelcomeNote"/> = true only when creating a brand-new vault; false when
    /// pointing at an existing folder, so its notes aren't disturbed.</summary>
    public void InitializeVault(string path, bool seedWelcomeNote)
    {
        Directory.CreateDirectory(path);
        AdoptVaultPath(path);

        if (seedWelcomeNote && !Directory.EnumerateFiles(VaultPath, "*.md").Any())
        {
            var welcomePath = Path.Combine(VaultPath, LocalizationService.Instance["WelcomeNote_FileName"]);
            File.WriteAllText(welcomePath, LocalizationService.Instance["WelcomeNote_Content"]);
        }
    }

    public void SetVaultPath(string path)
    {
        Directory.CreateDirectory(path);
        AdoptVaultPath(path);
    }

    /// <summary>Lo que significa "a partir de ahora el vault es este", compartido por
    /// <see cref="InitializeVault"/> y <see cref="SetVaultPath"/> para que cambiar de vault en caliente
    /// (T4, .docs/project-info/multi-vault-en-caliente.md) no dependa de por cuál de los dos se entró.
    ///
    /// <para>Lo que antes no hacía ninguno de los dos, y es lo que convierte esto en un cambio de vault de
    /// verdad y no solo en un cambio de ruta: <b>vaciar las cachés en memoria</b>. Tanto
    /// <see cref="_noteCache"/> como <see cref="_sessionEncryptionCache"/> están indexadas por ruta
    /// absoluta, así que con vaults distintos nunca se servirían entradas cruzadas — pero sí se quedaban
    /// vivas las del vault anterior, ocupando el presupuesto de memoria de la caché y, sobre todo,
    /// manteniendo en RAM el contenido de notas de un vault que el usuario acaba de cerrar (incluidas las
    /// de una sesión que se bloquea justo después). Volver a A después de B tiene que releer el disco,
    /// no servir lo que se leyó antes de irse.</para></summary>
    private void AdoptVaultPath(string path)
    {
        _noteCache.Clear();
        InvalidateSessionEncryptionCache();
        InvalidateVaultSizeCache();
        VaultPath = path;
        _settingsService.Settings.VaultPath = path;
        // Antes de RegisterRecentVault: SwitchVault reemplaza la instancia de AppSettings, y la lista de
        // recientes tiene que acabar en la nueva (viaja con las claves de instalación), no en la que se
        // acaba de sustituir.
        _settingsService.SwitchVault(path);
        _settingsService.Save();
        _settingsService.RegisterRecentVault(path);
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

    /// <summary>Por qué no se puede abrir <paramref name="path"/> como vault, o <see langword="null"/> si
    /// sí se puede (T4). Comprueba lo que de verdad falla en la práctica —la carpeta ya no está (movida,
    /// borrada, unidad externa desconectada, recurso de red caído) o está pero no se deja leer (permisos)—
    /// haciendo una lectura real del directorio en vez de fiarse solo de <see cref="Directory.Exists"/>,
    /// que devuelve <see langword="false"/> tanto para "no existe" como para "no tengo permiso ni para
    /// mirar", y deja al usuario sin saber cuál de las dos es.
    ///
    /// <para>Se usa para <b>pintar</b> una entrada reciente como no disponible con su motivo, no para
    /// borrarla: la lista nunca se poda sola (ver <see cref="Models.RecentVault"/>). El texto se compone
    /// con las mismas claves que <see cref="VaultFailureReasons"/> usa para los fallos de escritura, para
    /// que "acceso denegado" se lea igual venga de donde venga.</para></summary>
    public static LocalizedText? DescribeVaultUnavailability(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return LocalizedTextFormatter.Format("Notification_VaultUnavailable_Reason");

        try
        {
            if (!Directory.Exists(path))
                return LocalizedTextFormatter.Format("Notification_SaveFailed_Reason_PathNotFound");
            // Enumerar (y no solo existir) es lo que distingue "no está" de "no puedo leerla": basta con
            // pedir la primera entrada, así que ni siquiera en un vault enorme cuesta nada.
            using var probe = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            probe.MoveNext();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return LocalizedTextFormatter.Format("Common_Error_AccessDenied");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            LogService.Warning($"No se pudo comprobar la disponibilidad del vault '{path}'", ex);
            return LocalizedTextFormatter.Format("Common_Error_IOFailure");
        }
    }

    /// <summary>Drops the cached vault size so the next <see cref="GetVaultSizeBytes"/> call triggers a
    /// fresh background walk — called by every write path below that changes how many bytes live under the
    /// vault root.</summary>
    private void InvalidateVaultSizeCache()
    {
        lock (_vaultSizeLock) { _vaultSizeCacheValid = false; }
    }

    /// <summary>Drops every note cached under <paramref name="rootPath"/> from <see cref="_noteCache"/> —
    /// called by <see cref="SessionService.Lock"/> so no plaintext content read while a session was
    /// unlocked stays resident in memory after it's locked again.</summary>
    public void InvalidateCacheUnderPath(string rootPath) => _noteCache.InvalidatePrefix(rootPath);

    /// <summary>Reads a file's plaintext bytes into memory, decrypting first if it falls under an
    /// encrypted session — for callers (image thumbnail decoding) that can consume a byte buffer directly
    /// and don't need a real path on disk. Throws <see cref="SessionLockedException"/> if the session is
    /// encrypted but locked.</summary>
    public byte[] ReadFileBytes(string fullPath)
    {
        var encryptedRoot = ResolveEncryptedSessionRoot(fullPath);
        var raw = File.ReadAllBytes(fullPath);
        return encryptedRoot is null ? raw : _encryptionService.Decrypt(encryptedRoot, raw);
    }

    /// <summary>For a file under a currently-unlocked encrypted session, decrypts it to a fresh temp file
    /// under <see cref="PrivateTempFile"/>'s per-user directory and returns that path — for callers (PDF
    /// rendering via PDFium, "open with the system app") that need a real path on disk rather than a byte
    /// buffer. For a file outside any encrypted session, returns <paramref name="fullPath"/> unchanged —
    /// the overwhelmingly common case pays zero extra I/O. Throws <see cref="SessionLockedException"/> if
    /// the session is encrypted but locked. The caller owns cleanup of a temp copy via
    /// <see cref="TryCleanupDecryptedTempFile"/> — compare the returned path against
    /// <paramref name="fullPath"/> to know whether one was made.</summary>
    public string ResolveReadablePath(string fullPath)
    {
        var encryptedRoot = ResolveEncryptedSessionRoot(fullPath);
        if (encryptedRoot is null) return fullPath;

        var plaintext = _encryptionService.Decrypt(encryptedRoot, File.ReadAllBytes(fullPath));
        // El archivo nace ya con modo 0600 dentro del directorio temporal privado, antes del primer byte de
        // contenido. Antes se escribía con File.WriteAllBytes y se hacía SetUnixFileMode *después*: entre
        // las dos operaciones el archivo existía con la máscara heredada (normalmente 0644), o sea legible
        // por cualquier otra cuenta local, y justo con el contenido que el usuario cifró a propósito
        // (.docs/auditoria-seguridad-y-fallos-criticos.md, SEC-04). PrivateTempFile también borra el
        // archivo si la escritura falla a medias, para no dejar texto en claro incompleto en disco.
        return PrivateTempFile.Write(DecryptedTempFilePrefix, Path.GetExtension(fullPath), plaintext);
    }

    /// <summary>Best-effort delete of a decrypted temp file produced by <see cref="ResolveReadablePath"/>
    /// — swallows failures. A temp copy still in use by an external viewer (see
    /// <c>FilesViewModel.OpenWithSystem</c>) or already gone isn't worth surfacing an error over; the OS
    /// temp directory gets cleaned up eventually regardless.</summary>
    public static void TryCleanupDecryptedTempFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning($"No se pudo borrar el temporal descifrado '{path}'", ex);
        }
    }

    /// <summary>Prefix every decrypted temp file from <see cref="ResolveReadablePath"/> is named with —
    /// shared with <see cref="CleanupStaleDecryptedTempFiles"/> below so the sweep only ever touches files
    /// this app itself created.</summary>
    private const string DecryptedTempFilePrefix = "enki-dec-";

    /// <summary>Best-effort sweep of every leftover decrypted temp file in the OS temp directory — called
    /// both at startup and at shutdown (see <c>Program.Main</c>/<c>MainWindowViewModel.Shutdown</c>).
    /// <see cref="TryCleanupDecryptedTempFile"/> alone only ever cleans up the one file its own caller made;
    /// nothing previously swept the whole directory, so a crash (or, before this fix, simply closing the
    /// app — <see cref="SessionEncryptionService.LockAll"/> was never actually called either) could leave
    /// plaintext copies of protected-session content in the world-shared temp directory indefinitely
    /// (docs/version-alfa-3/01-analisis-detallado.md §4.1, Alta). Runs at startup too in case the previous
    /// shutdown was itself a crash that skipped the shutdown sweep. Sweeps both where these files live now
    /// (<see cref="PrivateTempFile"/>'s per-user directory) and the root of the OS temp directory, where
    /// every version before SEC-04 left them — otherwise upgrading would strand a user's old plaintext
    /// copies there forever.</summary>
    public static void CleanupStaleDecryptedTempFiles() =>
        PrivateTempFile.CleanupStale(DecryptedTempFilePrefix, TryCleanupDecryptedTempFile);

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

        return ReadableMarkdownFiles(folderPath, SearchOption.TopDirectoryOnly)
            .Select(TryReadCached)
            .Where(entry => entry is not null)
            .Select(entry => entry!.Value.Note)
            .OrderByDescending(n => n.ModifiedUtc)
            .ToList();
    }

    /// <summary>Reads one note through <see cref="_noteCache"/>, yielding null instead of throwing when the
    /// note belongs to a session that's currently locked (or whose manifest can't be read — see
    /// <see cref="ResolveEncryptedSessionRoot"/>).
    ///
    /// <para>Every enumeration below used to call <c>_noteCache.GetOrRead</c> straight inside a LINQ
    /// <c>Select</c>, so a single locked note aborted the whole scan with a
    /// <see cref="SessionLockedException"/> escaping from inside an <see cref="IEnumerable{T}"/> — only
    /// <see cref="GetAllNotesAcrossVaultWithContent"/> guarded against it, and only at whole-session
    /// granularity. That made <see cref="GetAllNotesRecursive"/> over a locked session throw rather than
    /// return, which is a latent crash in every caller that enumerates notes without first checking the
    /// lock state (the note grid checks; the trash view, the session sidebar and search do not). Skipping
    /// is the behaviour the rest of the app already assumes: "a locked session contributes nothing",
    /// exactly like <c>SessionService.LoadGraph</c>.</para></summary>
    private (NoteFile Note, string Content)? TryReadCached(FileInfo info)
    {
        try
        {
            return _noteCache.GetOrRead(info, ReadNoteAndContent);
        }
        catch (SessionLockedException)
        {
            return null;
        }
    }

    /// <summary>One folder's notes with their content — the same relationship to <see cref="GetNotes"/> that
    /// <see cref="GetAllNotesRecursiveWithContent"/> has to <see cref="GetAllNotesRecursive"/>. Needed by the
    /// note grid's search filter, which searches the folder the user is currently looking at and now does so
    /// over each note's full body rather than <see cref="NoteFile.Preview"/>'s first 160 characters. Shares
    /// <see cref="_noteCache"/> with <see cref="GetNotes"/>, so asking for both inside one
    /// <c>NotesViewModel.Reload</c> doesn't read the disk twice.</summary>
    public List<(NoteFile Note, string Content)> GetNotesWithContent(string? folderPath = null)
    {
        folderPath ??= VaultPath;
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return [];

        return ReadableMarkdownFiles(folderPath, SearchOption.TopDirectoryOnly)
            .Select(TryReadCached)
            .Where(entry => entry is not null)
            .Select(entry => entry!.Value)
            .OrderByDescending(t => t.Note.ModifiedUtc)
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

        return ReadableMarkdownFiles(root, SearchOption.AllDirectories)
            .Where(info => !IsUnderHiddenDirectory(root, info.FullName))
            .Select(TryReadCached)
            .Where(entry => entry is not null)
            .Select(entry => entry!.Value.Note)
            .OrderByDescending(n => n.ModifiedUtc)
            .ToList();
    }

    /// <summary>Cuenta las notas visibles bajo una carpeta sin leer su contenido. Comparte las reglas de
    /// exclusión de <see cref="GetAllNotesRecursive"/>; al aplicarse a la raíz del vault no incluye las
    /// sesiones ni la papelera, que viven en carpetas ocultas.</summary>
    public static int CountNotesRecursive(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return 0;

        return ReadableMarkdownFiles(root, SearchOption.AllDirectories)
            .Count(info => !IsUnderHiddenDirectory(root, info.FullName));
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

        return ReadableMarkdownFiles(root, SearchOption.AllDirectories)
            .Where(info => !IsUnderHiddenDirectory(root, info.FullName))
            .Select(TryReadCached)
            .Where(entry => entry is not null)
            .Select(entry => entry!.Value)
            .OrderByDescending(t => t.Note.ModifiedUtc)
            .ToList();
    }

    /// <summary>Every note in the vault, with content — the vault root (<see cref="GetAllNotesRecursiveWithContent"/>
    /// excludes <c>.sessions/</c> as a hidden directory on its own) plus every session folder directly under
    /// it, walked here rather than through <see cref="SessionService"/> (which depends on this class, so a
    /// reference back would be circular) — <c>.sessions/&lt;folder&gt;</c> is the session-root convention
    /// <see cref="SessionService"/> itself uses, so no wiring from composition is needed for this to see
    /// every session by construction. A session that's currently locked throws
    /// <see cref="SessionLockedException"/> on its very first note (its content can't be decrypted to even
    /// read a title) — caught per session so one locked session excludes only itself, exactly like every
    /// other place a locked session is silently skipped (<c>SessionService.LoadGraph</c>/<c>LoadGlobalGraph</c>).
    /// The single enumeration every wikilink-aware feature resolves against — Ctrl+click
    /// (<c>EditorViewModel.OpenWikiLink</c>), live-preview coloring, the backlinks panel
    /// (<see cref="EditorBacklinksService"/>) and the rename cascade (<see cref="RenameNote"/>) — so a
    /// <c>[[wikilink]]</c> that crosses a session boundary resolves the same way everywhere instead of only
    /// in the session graph's own view of the vault (<c>SessionService.LoadGlobalGraph</c>).</summary>
    public List<(NoteFile Note, string Content)> GetAllNotesAcrossVaultWithContent()
    {
        var result = GetAllNotesRecursiveWithContent(VaultPath);

        var sessionsRoot = Path.Combine(VaultPath, ".sessions");
        if (!Directory.Exists(sessionsRoot)) return result;

        foreach (var sessionDir in Directory.EnumerateDirectories(sessionsRoot))
        {
            try
            {
                result.AddRange(GetAllNotesRecursiveWithContent(sessionDir));
            }
            catch (SessionLockedException)
            {
            }
        }

        return result;
    }

    /// <summary>Same note set as <see cref="GetAllNotesAcrossVaultWithContent"/>, without the content —
    /// what Ctrl+click and live-preview coloring need (just <see cref="NoteFile"/> to resolve a target
    /// against), without paying to read every file's body for callers that never look at it.</summary>
    public List<NoteFile> GetAllNotesAcrossVault() =>
        GetAllNotesAcrossVaultWithContent().Select(t => t.Note).ToList();

    /// <summary>Enumerates <c>.md</c> files as <see cref="FileInfo"/> rather than plain path strings — the
    /// OS enumeration API already has to stat each entry to report it, so this carries
    /// <see cref="FileInfo.LastWriteTimeUtc"/> for free, which <see cref="_noteCache"/> needs to validate a
    /// cached entry without an extra per-file stat call.</summary>
    private static IEnumerable<FileInfo> EnumerateMarkdownFiles(string root, SearchOption option) =>
        new DirectoryInfo(root).EnumerateFiles("*.md", option)
            .Where(info => !info.Name.StartsWith('.'));

    /// <summary>Los ficheros que <see cref="EnumerateMarkdownFiles"/> llega a leer, ya materializados y sin
    /// dejar escapar un fallo de disco. El <c>Directory.Exists</c> que cada recorrido hace justo antes no
    /// basta: entre esa comprobación y el primer paso real del enumerador la raíz puede haber desaparecido,
    /// y entonces esto lanza <see cref="DirectoryNotFoundException"/>. La ventana no es teórica — borrar una
    /// sesión mueve su carpeta entera a la papelera mientras <c>SessionService.LoadGlobalGraph</c> puede
    /// estar recorriendo en segundo plano la lista de sesiones que tomó un momento antes—, y el recorrido
    /// vive dentro de los <c>async void</c> de <c>SessionsViewModel</c>, donde una excepción ya no tiene
    /// quién la recoja: se lleva el proceso por delante. Devolver lo que se pudo leer es inocuo, porque la
    /// carpeta que falta es justo la que ha dejado de existir y el siguiente recorrido ya ve el árbol como
    /// quedó.
    ///
    /// <para>Una subcarpeta que se borre a media enumeración no hace falta tratarla aparte: el enumerador
    /// del framework ya se la salta. Es la raíz la que no perdona.</para></summary>
    private static List<FileInfo> ReadableMarkdownFiles(string root, SearchOption option)
    {
        try
        {
            return EnumerateMarkdownFiles(root, option).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private (NoteFile Note, string Content) ReadNoteAndContent(FileInfo info)
    {
        var content = SafeReadContent(info.FullName);
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
        note.FullPath = MovePreservingEncryption(note.FullPath, destinationFolderPath);
        MoveAttachmentsFolder(oldPath, note.FullPath);
        MoveVersionHistory(oldPath, note.FullPath);
        _noteCache.Invalidate(oldPath);
        _noteCache.Invalidate(note.FullPath);
        return note;
    }

    /// <summary>Lleva la carpeta de adjuntos de una nota (<c>&lt;nota&gt;.attachments/</c>, ver
    /// <see cref="NoteImageAttachments"/>) allá donde acabe de irse la nota, sea por un renombrado o por
    /// un movimiento de carpeta. Sin esto, renombrar una nota dejaría cada <c>![](Nota.attachments/…)</c>
    /// que contiene apuntando a una carpeta que ya no se llama así: las imágenes desaparecerían del
    /// editor sin que nada indicase por qué, y arreglarlo exigiría editar el texto de la nota a mano.
    ///
    /// <para>Cada archivo se mueve por <see cref="MovePreservingEncryption"/>, no por
    /// <see cref="File.Move(string, string)"/>, porque mover una nota entre una sesión protegida y el
    /// vault cruza una frontera de cifrado y sus imágenes tienen que cruzarla con ella — quedarían
    /// ilegibles exactamente igual que quedaría la nota.</para>
    ///
    /// <para>Un fallo aquí se registra y se traga: la nota ya se ha movido y no hay vuelta atrás, así que
    /// abortar dejaría un estado peor que el de unas imágenes que hay que reubicar a mano.</para></summary>
    private void MoveAttachmentsFolder(string oldNotePath, string newNotePath)
    {
        var source = NoteImageAttachments.FolderFor(oldNotePath);
        var destination = NoteImageAttachments.FolderFor(newNotePath);
        if (source is null || destination is null || PathsEqual(source, destination)) return;
        if (!Directory.Exists(source)) return;

        try
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source)) MovePreservingEncryption(file, destination);
            foreach (var subfolder in Directory.GetDirectories(source))
                Directory.Move(subfolder, Path.Combine(destination, Path.GetFileName(subfolder)));

            if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
            InvalidateVaultSizeCache();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            LogService.Warning($"No se pudo mover la carpeta de adjuntos '{source}' junto a su nota", ex);
        }
    }

    /// <summary>Moves a note or attachment into <paramref name="destinationFolder"/>, converting its
    /// on-disk representation when the move crosses an encryption boundary — out of a protected session
    /// into the vault (decrypt), into a protected session from outside it (encrypt), or straight from one
    /// protected session into another (re-encrypt under the destination's own DEK).
    ///
    /// <para>A plain <see cref="File.Move(string, string)"/> — what this used to be — moves the file's
    /// <em>bytes</em> while <see cref="ResolveEncryptedSessionRoot"/> silently changes its mind about what
    /// those bytes mean, and both directions destroy the content as far as the user can tell. Dragging a
    /// vault note into a protected session left plain text sitting in a folder every read now tries to
    /// AES-GCM-decrypt: the tag check fails, <see cref="SafeReadContent"/> logs and returns "", and the
    /// note opens blank. Dragging one out left raw ciphertext in the vault root, which nothing decrypts
    /// any more, so the note opens as binary garbage. Neither case reported an error and neither was
    /// recoverable from the UI.</para>
    ///
    /// <para>Content is written to the destination before the source is removed, so an interrupted move
    /// leaves a duplicate rather than a hole. A locked source or destination session raises
    /// <see cref="SessionLockedException"/> from the encryption service and aborts the move with the file
    /// untouched — moving content into or out of a session you can't open is exactly the operation that
    /// must not proceed on a guess.</para></summary>
    private string MovePreservingEncryption(string sourcePath, string destinationFolder)
    {
        var destination = ResolveUniqueDestination(sourcePath, destinationFolder);

        var sourceRoot = ResolveEncryptedSessionRoot(sourcePath);
        var destinationRoot = ResolveEncryptedSessionRoot(destination);

        if (sourceRoot is not null && IsPathInLockedSession(sourcePath)) throw new SessionLockedException(sourcePath);
        if (destinationRoot is not null && IsPathInLockedSession(destination)) throw new SessionLockedException(destination);
        var sameScope = sourceRoot is null
            ? destinationRoot is null
            : destinationRoot is not null && PathsEqual(sourceRoot, destinationRoot);
        if (sameScope) return MoveWithUniqueSuffix(sourcePath, destinationFolder);

        var stamp = NoteDiskStamps.Read(new FileInfo(sourcePath));
        var raw = File.ReadAllBytes(sourcePath);
        var plaintext = sourceRoot is null ? raw : _encryptionService.Decrypt(sourceRoot, raw);
        var payload = destinationRoot is null ? plaintext : _encryptionService.Encrypt(destinationRoot, plaintext);

        var tempPath = $"{destination}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllBytes(tempPath, payload);
            File.Move(tempPath, destination, overwrite: false);
            try
            {
                if (NoteDiskStamps.Check(sourcePath, stamp, out _) != NoteDiskState.Unchanged)
                    throw new IOException("El archivo ha cambiado durante la transferencia.");
                File.Delete(sourcePath);
            }
            catch
            {
                File.Delete(destination);
                throw;
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        return destination;
    }

    /// <summary>The name <paramref name="sourcePath"/> would take inside <paramref name="destinationFolder"/>
    /// once the same " 2"/" 3" collision suffix <see cref="MoveWithUniqueSuffix"/> applies is accounted for —
    /// split out of it so <see cref="MovePreservingEncryption"/> can know the final destination path (and
    /// therefore which session, if any, will own the file) <em>before</em> anything is written there.</summary>
    private static string ResolveUniqueDestination(string sourcePath, string destinationFolder)
    {
        var destination = Path.Combine(destinationFolder, Path.GetFileName(sourcePath));
        var suffix = 2;
        while (File.Exists(destination) || Directory.Exists(destination))
        {
            destination = Path.Combine(destinationFolder,
                $"{Path.GetFileNameWithoutExtension(sourcePath)} {suffix}{Path.GetExtension(sourcePath)}");
            suffix++;
        }
        return destination;
    }

    /// <summary>Moves <paramref name="sourcePath"/> into <paramref name="destinationFolder"/>, appending
    /// a numeric suffix (" 2", " 3", ...) before the extension if a file with the same name already
    /// exists there — shared collision-avoidance for every move/trash/restore operation below, notes and
    /// files alike, instead of duplicating the same loop for each one. Logs and rethrows on I/O failure
    /// (docs/analisis-proyecto/roadmap.md, Fase 1) instead of letting a bare exception surface with no
    /// trace of which move it was.</summary>
    private static string MoveWithUniqueSuffix(string sourcePath, string destinationFolder)
    {
        var destination = ResolveUniqueDestination(sourcePath, destinationFolder);

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

    private NoteFile ReadNoteMetadata(string fullPath) => ReadNoteMetadata(new FileInfo(fullPath), SafeReadContent(fullPath));

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

    /// <summary>True when <paramref name="fullPath"/> falls under an encrypted session that isn't
    /// currently unlocked — the check-before-read counterpart to letting <see cref="SafeReadContent"/>/
    /// <see cref="WriteFileAtomic"/> throw <see cref="SessionLockedException"/> after the fact. Every
    /// caller able to react proactively (skip a locked note while restoring tabs, refuse an autosave
    /// instead of crashing) should prefer this over a try/catch, mirroring
    /// <c>FilesViewModel.IsSelectedSessionLocked</c> — the one place that already got this right before
    /// Notas/Editor did (docs/version-alfa-3/01-analisis-detallado.md §4.1).
    ///
    /// <para>Also true when the session's manifest itself can't be read, since
    /// <see cref="ResolveEncryptedSessionRoot"/> now fails closed on that: a guard that threw the very
    /// exception it exists to pre-empt would be no guard at all — the tab-restore loop in
    /// <c>NotesViewModel.RestoreOpenTabs</c> calls this precisely so shell initialization can't crash on an
    /// inaccessible session.</para></summary>
    public bool IsPathInLockedSession(string fullPath)
    {
        try
        {
            var encryptedRoot = ResolveEncryptedSessionRoot(fullPath);
            return encryptedRoot is not null && !_encryptionService.IsUnlocked(encryptedRoot);
        }
        catch (SessionLockedException)
        {
            return true;
        }
    }

    /// <summary>Reads a note's text content — transparently decrypting it first if <paramref name="path"/>
    /// falls under an encrypted session (see <see cref="ResolveEncryptedSessionRoot"/>). Unlike the old
    /// plain-text-only version of this method, a locked session is NOT collapsed to <c>""</c>: that would
    /// be indistinguishable from "this note has no content", so <see cref="SessionLockedException"/>
    /// propagates instead and callers must handle it explicitly. A missing file, or any other read failure
    /// on a plain-text note, still returns <c>""</c> exactly as before.</summary>
    private string SafeReadContent(string path)
    {
        var encryptedRoot = ResolveEncryptedSessionRoot(path);
        if (encryptedRoot is null)
        {
            try { return File.ReadAllText(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Warning($"No se pudo leer la nota '{path}'", ex);
                return string.Empty;
            }
        }

        byte[] raw;
        try { raw = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning($"No se pudo leer la nota cifrada '{path}'", ex);
            return string.Empty;
        }

        try
        {
            return Encoding.UTF8.GetString(_encryptionService.Decrypt(encryptedRoot, raw));
        }
        catch (CryptographicException ex)
        {
            LogService.Error($"No se pudo descifrar '{path}' (contenido dañado o corrupto)", ex);
            return string.Empty;
        }
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
        body = Regex.Replace(body, @"[#*`_>\-\[\]!]", " ", RegexOptions.None, RegexSafety.MatchTimeout);
        body = Regex.Replace(body, @"\s+", " ", RegexOptions.None, RegexSafety.MatchTimeout).Trim();

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
        CaptureAutosaveSnapshotBeforeWrite(note);
        WriteFileAtomic(note.FullPath, content);
        CacheAfterWrite(note.FullPath, content);
    }

    /// <summary>Snapshots whatever <paramref name="note"/> holds on disk right now — the content this write
    /// is about to replace — before it's gone for good (historial local de versiones, see
    /// <see cref="CaptureSnapshot"/>/<see cref="MinimumAutosaveInterval"/> for the throttling that keeps this
    /// from firing on every 600ms autosave tick). Deliberately tolerant of any failure reading the previous
    /// content (locked session race, transient I/O) — losing one history checkpoint is recoverable; turning a
    /// working save into a failed one because its safety net stumbled is not.</summary>
    private void CaptureAutosaveSnapshotBeforeWrite(NoteFile note)
    {
        if (!File.Exists(note.FullPath)) return;
        try
        {
            var previous = SafeReadContent(note.FullPath);
            CaptureSnapshot(note, previous, NoteVersionTrigger.Autosave);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            LogService.Warning($"No se pudo leer el contenido previo de '{note.FullPath}' para el historial de versiones", ex);
        }
    }

    /// <param name="initialContent">Contenido con el que nace la nota. Null —lo habitual— produce el
    /// encabezado de siempre con el título; lo pasa con valor el flujo de plantillas
    /// (<see cref="NoteTemplateService"/>), que ya trae el Markdown expandido y no quiere que se le
    /// anteponga nada. Cadena vacía es una elección legítima (una plantilla deliberadamente en blanco) y
    /// se respeta tal cual.</param>
    public NoteFile CreateNote(string? desiredTitle = null, string? folderPath = null, string? initialContent = null)
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

        var content = initialContent ?? $"# {desiredTitle}\n\n";
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
    private void WriteFileAtomic(string path, string content) =>
        WriteFileAtomic(path, Encoding.UTF8.GetBytes(content));

    /// <summary>Binary counterpart of <see cref="WriteFileAtomic(string, string)"/> — same temp-file +
    /// atomic move and the same "encrypt first if this path falls under a protected session" rule, for
    /// content that isn't text (hoy, las imágenes que <see cref="NoteImageService"/> guarda como adjunto
    /// de una nota).</summary>
    private void WriteFileAtomic(string path, byte[] plaintext)
    {
        var encryptedRoot = ResolveEncryptedSessionRoot(path);
        var bytes = encryptedRoot is null
            ? plaintext
            : _encryptionService.Encrypt(encryptedRoot, plaintext); // throws SessionLockedException if locked

        try
        {
            DurableFile.WriteAllBytesAtomic(path, bytes);
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
        while (File.Exists(destination) || Directory.Exists(destination))
        {
            destination = Path.Combine(folderPath,
                $"{Path.GetFileNameWithoutExtension(fileName)} {suffix}{Path.GetExtension(fileName)}");
            suffix++;
        }

        var encryptedRoot = ResolveEncryptedSessionRoot(destination);
        if (encryptedRoot is null)
        {
            File.Copy(sourcePath, destination);
        }
        else
        {
            var ciphertext = _encryptionService.Encrypt(encryptedRoot, File.ReadAllBytes(sourcePath));
            File.WriteAllBytes(destination, ciphertext);
        }

        InvalidateVaultSizeCache();
        return ReadNoteMetadata(destination);
    }

    /// <summary>Escribe un archivo binario dentro del vault (creando su carpeta si hace falta) con la
    /// misma escritura atómica y el mismo cifrado automático que una nota — ver
    /// <see cref="WriteFileAtomic(string, byte[])"/>. Es la vía por la que <see cref="NoteImageService"/>
    /// guarda una imagen pegada o arrastrada: una imagen adjunta a una nota de una sesión protegida tiene
    /// que quedar cifrada igual que la nota que la enseña, y centralizarlo aquí es lo que impide que
    /// alguna ruta de escritura futura la deje en claro por descuido.</summary>
    public void WriteBinaryFile(string fullPath, byte[] bytes)
    {
        EnsureVaultAvailable();

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        WriteFileAtomic(fullPath, bytes);
        InvalidateVaultSizeCache();
    }

    /// <summary>Copia un adjunto sin cargarlo entero en memoria y sin publicar un archivo parcial. En una
    /// sesión protegida el formato AES-GCM actual necesita autenticar el mensaje completo, por lo que se
    /// materializa únicamente después de aplicar el límite; fuera de ella la copia es streaming.</summary>
    public void CopyBinaryFile(string fullPath, Stream source, long maxBytes)
    {
        EnsureVaultAvailable();
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var encryptedRoot = ResolveEncryptedSessionRoot(fullPath);
        if (encryptedRoot is null)
        {
            DurableFile.CopyAtomic(fullPath, source, maxBytes);
        }
        else
        {
            using var bounded = new MemoryStream();
            var buffer = new byte[81920];
            long copied = 0;
            while (true)
            {
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                copied += read;
                if (copied > maxBytes)
                    throw new InvalidDataException($"El archivo supera el límite de {maxBytes} bytes.");
                bounded.Write(buffer, 0, read);
            }
            WriteFileAtomic(fullPath, bounded.ToArray());
        }
        InvalidateVaultSizeCache();
    }

    private string TrashPath => Path.Combine(VaultPath, ".trash");

    /// <summary>Sidecar JSON mapping a trashed item's current (flat, top-level) path under
    /// <see cref="TrashPath"/> back to the full path it was trashed from — the only place that
    /// association survives once <see cref="DeleteNote"/>/<see cref="DeleteFile"/> move a file out from
    /// under its session folder. Only ever written for an item that came from an encrypted session (see
    /// <see cref="RecordTrashOrigin"/>); a vault with no encrypted sessions never creates this file.</summary>
    private string TrashOriginsPath => Path.Combine(TrashPath, ".trash-origins.json");

    private Dictionary<string, string> ReadTrashOrigins()
    {
        var comparer = PathComparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        if (!File.Exists(TrashOriginsPath)) return new Dictionary<string, string>(comparer);

        try
        {
            var json = File.ReadAllText(TrashOriginsPath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return loaded is null ? new Dictionary<string, string>(comparer) : new Dictionary<string, string>(loaded, comparer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, string>(comparer);
        }
    }

    private void WriteTrashOrigins(Dictionary<string, string> origins)
    {
        Directory.CreateDirectory(TrashPath);
        var tempPath = $"{TrashOriginsPath}.tmp-{Guid.NewGuid():N}";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(origins));
        File.Move(tempPath, TrashOriginsPath, overwrite: true);
    }

    /// <summary>Records that <paramref name="trashPath"/> (an item's new location, flat under
    /// <see cref="TrashPath"/>) was trashed from <paramref name="originalPath"/> — called only when
    /// <paramref name="originalPath"/> resolved to an encrypted session, since a plain-text item never
    /// needs this to read/restore correctly.</summary>
    private void RecordTrashOrigin(string trashPath, string originalPath)
    {
        var origins = ReadTrashOrigins();
        origins[trashPath] = originalPath;
        WriteTrashOrigins(origins);
    }

    /// <summary>Drops the origin recorded for <paramref name="trashPath"/> once it's no longer in the
    /// trash (restored or purged) — a no-op if none was ever recorded (the common, plain-text case).</summary>
    private void RemoveTrashOrigin(string trashPath)
    {
        var origins = ReadTrashOrigins();
        if (origins.Remove(trashPath)) WriteTrashOrigins(origins);
    }

    /// <summary>True for a trashed note/file whose recorded origin (see <see cref="RecordTrashOrigin"/>)
    /// is an encrypted session that isn't currently unlocked — <see cref="GetTrashedNotes"/> and
    /// <see cref="GetTrashedFiles"/> exclude these from their lists entirely instead of letting
    /// <see cref="SafeReadContent"/> throw <see cref="SessionLockedException"/> while building the trash
    /// view, or exposing a locked session's attachment names, mirroring the same "a locked session
    /// contributes nothing" stance <see cref="SessionService.LoadGraph"/> already takes.</summary>
    private bool IsTrashedItemFromLockedSession(string trashPath)
    {
        if (!ReadTrashOrigins().TryGetValue(trashPath, out var originalPath)) return false;
        return IsPathInLockedSession(originalPath);
    }

    public void DeleteNote(NoteFile note)
    {
        if (!File.Exists(note.FullPath)) return;

        var originalPath = note.FullPath;
        var wasEncrypted = ResolveEncryptedSessionRoot(originalPath) is not null;

        Directory.CreateDirectory(TrashPath);
        var trashPath = MoveWithUniqueSuffix(originalPath, TrashPath);
        if (wasEncrypted) RecordTrashOrigin(trashPath, originalPath);
        TrashVersionHistory(originalPath, trashPath);

        _noteCache.Invalidate(originalPath);
        InvalidateVaultSizeCache();
    }

    public List<NoteFile> GetTrashedNotes()
    {
        if (!Directory.Exists(TrashPath)) return [];

        return ReadableMarkdownFiles(TrashPath, SearchOption.TopDirectoryOnly)
            .Where(info => !IsTrashedItemFromLockedSession(info.FullName))
            .Select(info => _noteCache.GetOrRead(info, ReadNoteAndContent).Note)
            .OrderByDescending(n => n.ModifiedUtc)
            .ToList();
    }

    /// <summary>Restores <paramref name="note"/> from the trash — back into the session folder it was
    /// trashed from when that's recorded and still exists (see <see cref="RecordTrashOrigin"/>), so an
    /// encrypted note lands back under a path <see cref="ResolveEncryptedSessionRoot"/> recognizes and can
    /// decrypt again; to the vault root otherwise, same as before this distinction existed.</summary>
    public void RestoreNote(NoteFile note)
    {
        if (!File.Exists(note.FullPath)) return;

        var trashPath = note.FullPath;
        var origins = ReadTrashOrigins();
        var originalFolder = origins.TryGetValue(trashPath, out var originalPath)
            ? Path.GetDirectoryName(originalPath)
            : null;
        var destinationFolder = originalFolder is not null && Directory.Exists(originalFolder) ? originalFolder : VaultPath;

        note.FullPath = MoveWithUniqueSuffix(trashPath, destinationFolder);
        if (origins.Remove(trashPath)) WriteTrashOrigins(origins);
        RestoreNoteHistory(trashPath, note.FullPath);

        _noteCache.Invalidate(trashPath);
        InvalidateVaultSizeCache();
    }

    public void PermanentlyDelete(NoteFile note)
    {
        if (!File.Exists(note.FullPath)) return;
        File.Delete(note.FullPath);
        _noteCache.Invalidate(note.FullPath);
        InvalidateVaultSizeCache();
        RemoveTrashOrigin(note.FullPath);
        PurgeNoteHistory(note.FullPath);
    }

    /// <summary>Renames <paramref name="note"/>'s file and, unlike before Fase 5, keeps the rest of the
    /// vault's wikilink graph pointing at it: every other note in the vault (<see cref="GetAllNotesAcrossVaultWithContent"/>
    /// — every session too, not just the renamed note's own) whose <c>[[...]]</c> references resolved to the
    /// old file — by filename or by unambiguous title, same rule <see cref="WikiLinkParser.ResolveTarget"/>
    /// uses everywhere else — gets rewritten to reference the new filename instead
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
        var notesBeforeRename = GetAllNotesAcrossVaultWithContent();

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
        MoveAttachmentsFolder(oldPath, newPath);
        MoveVersionHistory(oldPath, newPath);
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

    /// <summary>Rewrites, in every note captured by <paramref name="notesBeforeRename"/>, each wikilink
    /// occurrence that resolved (against that pre-rename snapshot) to <paramref name="oldPath"/> — replacing
    /// its target text with <paramref name="newLinkName"/> while preserving any <c>|alias</c>/<c>#anchor</c>
    /// suffix. Writes go through the same atomic path as everything else in this class; a note that fails
    /// to rewrite (locked, permissions) is logged and skipped rather than aborting the rename that already
    /// succeeded — a stale link on one note is recoverable, an interrupted rename is not.</summary>
    private void RewriteWikilinksToRenamedNote(
        IReadOnlyList<(NoteFile Note, string Content)> notesBeforeRename, string oldPath, string newLinkName)
    {
        var notesSnapshot = notesBeforeRename.Select(t => t.Note).ToList();
        var newDir = Path.GetDirectoryName(oldPath)!;
        var renamedNotePath = Path.Combine(newDir, newLinkName + ".md");

        foreach (var (noteFile, _) in notesBeforeRename)
        {
            // The renamed note itself now lives at its new path on disk; every other note keeps its own.
            var writePath = PathsEqual(noteFile.FullPath, oldPath) ? renamedNotePath : noteFile.FullPath;
            try
            {
                var disk = ReadFromDisk(writePath);
                if (disk is not { } fresh) continue;
                var rewritten = RewriteLinksTo(fresh.Content, notesSnapshot, oldPath, newLinkName);
                if (rewritten == fresh.Content) continue;
                CaptureSnapshot(fresh.Note, fresh.Content, NoteVersionTrigger.PreBulkOperation);
                WriteContentChecked(fresh.Note, rewritten, fresh.Stamp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
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
            .Where(path => !Path.GetFileName(path).StartsWith('.'))
            .Select(ReadFileMetadata)
            .OrderByDescending(f => f.ModifiedUtc)
            .ToList();
    }

    /// <summary>Todos los archivos bajo <paramref name="root"/>, a cualquier profundidad — lo que recorre la
    /// búsqueda de la rejilla de Archivos para encontrar un archivo aunque viva en una subcarpeta. Se compone de
    /// <see cref="GetFiles"/> carpeta a carpeta sobre el mismo recorrido de <see cref="GetFolderTree"/> en vez de
    /// un <c>SearchOption.AllDirectories</c> propio, así que excluye exactamente lo mismo que la navegación:
    /// notas, archivos ocultos y carpetas que empiezan por punto (<c>.trash</c>, <c>.sessions</c>).</summary>
    public List<VaultFile> GetFilesRecursive(string root)
    {
        var result = GetFiles(root);
        foreach (var folder in GetFolderTree(root)) CollectFilesRecursive(folder, result);
        return result;
    }

    private void CollectFilesRecursive(NoteFolder folder, List<VaultFile> result)
    {
        result.AddRange(GetFiles(folder.FullPath));
        foreach (var child in folder.Children) CollectFilesRecursive(child, result);
    }

    /// <summary>Cuántos elementos enseña Archivos al entrar en <paramref name="folderPath"/>: sus subcarpetas
    /// visibles más sus archivos que no son notas. Cuenta entradas del directorio sin leer los metadatos de
    /// ninguna, que es todo lo que necesita el contador de una tarjeta de carpeta; una carpeta que desaparece o
    /// no se puede leer cuenta cero en vez de tumbar la rejilla entera.</summary>
    public static int CountFolderEntries(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath)) return 0;

            var folders = Directory.EnumerateDirectories(folderPath)
                .Count(path => !Path.GetFileName(path).StartsWith('.'));
            var files = Directory.EnumerateFiles(folderPath)
                .Count(path => !Path.GetFileName(path).StartsWith('.') &&
                               !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
            return folders + files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
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

    /// <summary>Attachment counterpart of <see cref="MoveNote"/> — went through the same raw
    /// <see cref="File.Move(string, string)"/> and so had the same encryption-boundary bug, which is why
    /// this is no longer static: re-encrypting needs the instance's <see cref="_encryptionService"/>.</summary>
    public VaultFile MoveFile(VaultFile file, string destinationFolderPath)
    {
        file.FullPath = MovePreservingEncryption(file.FullPath, destinationFolderPath);
        // Un movimiento normal no cambia el tamaño del vault, pero uno que cruza la frontera de cifrado sí:
        // el sobre AES-GCM añade (o quita) el nonce y el tag de cada archivo.
        InvalidateVaultSizeCache();
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

        var originalPath = file.FullPath;
        var wasEncrypted = ResolveEncryptedSessionRoot(originalPath) is not null;

        Directory.CreateDirectory(TrashPath);
        var trashPath = MoveWithUniqueSuffix(originalPath, TrashPath);
        if (wasEncrypted) RecordTrashOrigin(trashPath, originalPath);
    }

    public List<VaultFile> GetTrashedFiles()
    {
        if (!Directory.Exists(TrashPath)) return [];

        return Directory.EnumerateFiles(TrashPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Where(path => !PathsEqual(path, TrashOriginsPath))
            .Where(path => !IsTrashedItemFromLockedSession(path))
            .Select(ReadFileMetadata)
            .OrderByDescending(f => f.ModifiedUtc)
            .ToList();
    }

    /// <summary>Restores <paramref name="file"/> from the trash — back into the session folder it was
    /// trashed from when that's recorded and still exists (see <see cref="RecordTrashOrigin"/>), so an
    /// encrypted attachment lands back under a path <see cref="ResolveEncryptedSessionRoot"/> recognizes
    /// and can decrypt again; to the vault root otherwise, same as before this distinction existed.</summary>
    public void RestoreFile(VaultFile file)
    {
        if (!File.Exists(file.FullPath)) return;

        var trashPath = file.FullPath;
        var origins = ReadTrashOrigins();
        var originalFolder = origins.TryGetValue(trashPath, out var originalPath)
            ? Path.GetDirectoryName(originalPath)
            : null;
        var destinationFolder = originalFolder is not null && Directory.Exists(originalFolder) ? originalFolder : VaultPath;

        var newPath = MoveWithUniqueSuffix(trashPath, destinationFolder);
        file.FullPath = newPath;
        if (origins.Remove(trashPath)) WriteTrashOrigins(origins);
    }

    public void PermanentlyDelete(VaultFile file)
    {
        if (!File.Exists(file.FullPath)) return;
        var path = file.FullPath;
        File.Delete(path);
        InvalidateVaultSizeCache();
        RemoveTrashOrigin(path);
    }

    internal static string SanitizeFileName(string name, string? fallback = null)
    {
        const int maxUtf8Bytes = 200;
        const string portableInvalid = "<>:\"/\\|?*";
        var cleaned = new string(name.Where(c => c >= ' ' && portableInvalid.IndexOf(c) < 0
                                                   && !Path.GetInvalidFileNameChars().Contains(c)).ToArray())
            .Trim().TrimStart('.').TrimEnd(' ', '.');

        if (cleaned is "." or ".." || string.IsNullOrWhiteSpace(cleaned))
            cleaned = fallback ?? LocalizationService.Instance["Vault_DefaultNoteTitle"];

        var stem = cleaned.Split('.')[0];
        if (WindowsReservedFileNames.Contains(stem)) cleaned = "_" + cleaned;

        var truncated = new StringBuilder();
        var byteCount = 0;
        foreach (var rune in cleaned.EnumerateRunes())
        {
            if (byteCount + rune.Utf8SequenceLength > maxUtf8Bytes) break;
            truncated.Append(rune.ToString());
            byteCount += rune.Utf8SequenceLength;
        }

        var result = truncated.ToString().TrimEnd(' ', '.');
        return string.IsNullOrWhiteSpace(result)
            ? LocalizationService.Instance["Vault_DefaultNoteTitle"]
            : result;
    }

    private static readonly HashSet<string> WindowsReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
}
