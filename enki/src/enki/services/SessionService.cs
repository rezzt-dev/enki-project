using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using enki.Localization;
using enki.Models;

namespace enki.Services;

/// <summary>Manages "sessions" — isolated, named sub-workspaces living inside the same vault
/// (<c>&lt;vault&gt;/.sessions/&lt;folder&gt;/</c>), each with its own note tree and a "fichero de
/// localización" (<see cref="SessionManifest"/>, stored as <c>.session.json</c> at the session root)
/// that lays its notes out as a graph and remembers manually-drawn relations between them.</summary>
public sealed class SessionService
{
    private const string ManifestFileName = ".session.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly VaultService _vaultService;

    /// <summary>The same instance <see cref="_vaultService"/> was constructed with — reused rather than
    /// injected separately, so there's only ever one shared unlocked-DEK cache between content read/write
    /// (in <see cref="VaultService"/>) and the unlock/lock lifecycle (here).</summary>
    private readonly SessionEncryptionService _encryptionService;

    /// <summary>Raised when a <c>.session.json</c> read or write fails with an I/O error (locked, moved,
    /// deleted out-of-band, disco lleno) — <see cref="ViewModels.MainWindowViewModel"/> lo reenvía a
    /// <c>NotificationService</c> en vez de dejar el fallo en silencio (03-problemas-y-deuda-tecnica.md
    /// §1). No se dispara por corrupción de JSON leíble pero inválido, ya cubierta por el manifiesto
    /// sintético de <see cref="ReadManifest"/>.</summary>
    public event Action<string>? ManifestError;

    public SessionService(VaultService vaultService)
    {
        _vaultService = vaultService;
        _encryptionService = vaultService.EncryptionService;
    }

    private string SessionsRoot => Path.Combine(_vaultService.VaultPath, ".sessions");

    /// <summary>Carpeta donde <see cref="Delete"/> aparca las sesiones borradas — la categoría "Sesiones"
    /// de la Papelera (<c>TrashViewModel</c>) lista/restaura/purga sobre ella (Fase 3 de
    /// docs/analisis-proyecto/roadmap.md).</summary>
    private string TrashedSessionsRoot => Path.Combine(_vaultService.VaultPath, ".trash", "sessions");

    public List<Session> List()
    {
        var sessions = EnumerateSessionManifests().Select(t => ToSession(t.Path, t.Manifest)).ToList();
        return sessions.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Async wrapper around <see cref="List"/> — every call underneath is synchronous disk I/O
    /// (directory enumeration, manifest reads), pushed onto a background thread so callers on the UI
    /// thread (e.g. <c>SessionsViewModel.Reload</c>) don't block the whole app while it runs.</summary>
    public Task<List<Session>> ListAsync() => Task.Run(() => List());

    private IEnumerable<(string Path, SessionManifest Manifest)> EnumerateSessionManifests()
    {
        foreach (var dir in ListSessionDirectories())
        {
            var manifest = ReadManifest(dir);
            if (manifest is null) continue;

            yield return (dir, manifest);
        }
    }

    /// <summary>Aísla la enumeración del directorio de sesiones en un método normal (no iterador) porque
    /// <c>yield return</c> no puede vivir dentro de un <c>try</c> con <c>catch</c> — Fase 0 del roadmap de
    /// deficiencias (docs/analisis-proyecto/analisis-deficiencias-proyecto.md §2, "Concurrencia/async"):
    /// esto es lo único entre un permiso denegado/unidad desconectada al listar <c>.sessions</c> y una
    /// excepción no capturable propagándose por los <c>async void Reload</c>/<c>LoadGraph</c> de
    /// <c>SessionsViewModel</c> (vía <see cref="List"/>/<see cref="LoadGlobalGraph"/>). La sobrecarga con
    /// <paramref name="root"/> reutiliza la misma guarda para la papelera de sesiones
    /// (<see cref="ListTrashedSessions"/>).</summary>
    private List<string> ListSessionDirectories() => ListSessionDirectories(SessionsRoot);

    private List<string> ListSessionDirectories(string root)
    {
        if (!Directory.Exists(root)) return [];

        try
        {
            return Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning($"No se pudieron enumerar las sesiones en '{root}'", ex);
            ManifestError?.Invoke(string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Sessions_ListError"], ex.Message));
            return [];
        }
    }

    public Session Create(string desiredName, bool isDefault = false)
    {
        var (path, manifest) = CreateFolderAndManifest(desiredName, isDefault, encryption: null);
        return ToSession(path, manifest);
    }

    /// <summary>Creates a password-protected session: same folder/manifest creation as <see cref="Create"/>,
    /// plus a freshly-generated Data Encryption Key wrapped under <paramref name="password"/> and 5
    /// independent recovery codes (see <see cref="SessionEncryptionService.CreateEncryptionInfo"/>). The
    /// returned codes exist only in this call's return value — nothing here persists them anywhere; the
    /// caller (the create-session UI) is responsible for showing them to the user exactly once. The new
    /// session is left unlocked (the user just supplied the password themselves, no need to ask again
    /// immediately).</summary>
    public (Session Session, IReadOnlyList<string> RecoveryCodes) CreateEncrypted(string desiredName, string password, bool isDefault = false)
    {
        var encryption = SessionEncryptionService.CreateEncryptionInfo(password, out var recoveryCodes);
        var (path, manifest) = CreateFolderAndManifest(desiredName, isDefault, encryption);
        _encryptionService.TryUnlockWithPassword(path, encryption, password);
        return (ToSession(path, manifest), recoveryCodes);
    }

    private (string Path, SessionManifest Manifest) CreateFolderAndManifest(string desiredName, bool isDefault, SessionEncryptionInfo? encryption)
    {
        Directory.CreateDirectory(SessionsRoot);

        var name = string.IsNullOrWhiteSpace(desiredName) ? LocalizationService.Instance["Sessions_DefaultName"] : desiredName.Trim();
        var safeFolder = VaultService.SanitizeFileName(name);
        var path = Path.Combine(SessionsRoot, safeFolder);
        var suffix = 2;
        while (Directory.Exists(path))
        {
            path = Path.Combine(SessionsRoot, $"{safeFolder} {suffix}");
            suffix++;
        }

        Directory.CreateDirectory(path);

        var manifest = new SessionManifest
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            CreatedUtc = DateTime.UtcNow,
            IsDefault = isDefault,
            Encryption = encryption
        };
        WriteManifest(path, manifest);

        return (path, manifest);
    }

    /// <summary>Unwraps the session's DEK with <paramref name="password"/> and, on success, caches it as
    /// unlocked so <see cref="VaultService"/>'s content read/write transparently decrypts/encrypts this
    /// session from here on. Returns false on a wrong password or a non-encrypted session — never throws
    /// for either.</summary>
    public bool Unlock(Session session, string password)
    {
        var manifest = ReadManifest(session.RootPath);
        if (manifest?.Encryption is not { } encryption) return false;

        return _encryptionService.TryUnlockWithPassword(session.RootPath, encryption, password);
    }

    /// <summary>Drops the session's unlocked DEK and any plaintext of its notes cached in
    /// <see cref="VaultService"/>'s in-memory note cache — after this, reading any of its content throws
    /// <see cref="SessionLockedException"/> again until it's unlocked (or its manifest isn't encrypted at
    /// all, in which case this is a no-op).</summary>
    public void Lock(Session session)
    {
        _encryptionService.Lock(session.RootPath);
        _vaultService.InvalidateCacheUnderPath(session.RootPath);
    }

    public bool IsUnlocked(Session session) => !session.IsEncrypted || _encryptionService.IsUnlocked(session.RootPath);

    /// <summary>"Forgot my password": proves <paramref name="recoveryCode"/> is one of the 5 unused codes,
    /// then rewraps the DEK under <paramref name="newPassword"/> and persists the updated manifest. Leaves
    /// the session unlocked on success (the DEK was just recovered, no reason to ask the user to unlock it
    /// again with the password they just chose). Returns false for a non-encrypted session, a wrong/already
    /// -used code, without throwing.</summary>
    public bool ResetPasswordWithRecoveryCode(Session session, string recoveryCode, string newPassword)
    {
        var manifest = ReadManifest(session.RootPath);
        if (manifest?.Encryption is not { } encryption) return false;

        if (!_encryptionService.TryUnlockWithRecoveryCode(session.RootPath, encryption, recoveryCode, out var usedSlotIndex))
            return false;

        manifest.Encryption = _encryptionService.ResetPassword(session.RootPath, encryption, usedSlotIndex!.Value, newPassword);
        WriteManifest(session.RootPath, manifest);
        return true;
    }

    /// <summary>Called once, right after a brand-new vault is created — guarantees it always starts with
    /// one "default" session that can be renamed but never deleted. Idempotent: if a default session
    /// already exists (e.g. called twice), returns it instead of creating another.</summary>
    public Session EnsureDefaultSession()
    {
        var existing = List().FirstOrDefault(s => s.IsDefault);
        // "default" used to be a literal, untranslated English identifier here — but Session.Name is what
        // SessionListItemViewModel.Name binds to in SessionsView.axaml, so it's shown to the user verbatim
        // in the session list (renameable, like any other default name — see Vault_DefaultNoteTitle/
        // Vault_DefaultFolderName), not a purely internal id. Resolved via LocalizationService instead (see
        // docs/agent-docs/internacionalizacion-i18n.md §5.1).
        return existing ?? Create(LocalizationService.Instance["Sessions_DefaultSessionName"], isDefault: true);
    }

    public Session Rename(Session session, string newName)
    {
        var name = string.IsNullOrWhiteSpace(newName) ? session.Name : newName.Trim();
        var manifest = ReadManifest(session.RootPath) ?? EmptyManifestFor(session);
        manifest.Name = name;
        WriteManifest(session.RootPath, manifest);

        session.Name = name;
        return session;
    }

    /// <summary>Soft-deletes a session by moving its folder to the vault's trash. No-ops on the default
    /// session — the UI is expected to hide the delete action for it in the first place, but this guard
    /// keeps that invariant even if some other call path is added later. Returns whether it actually deleted.</summary>
    public bool Delete(Session session)
    {
        if (session.IsDefault) return false;
        if (!Directory.Exists(session.RootPath)) return false;

        Directory.CreateDirectory(TrashedSessionsRoot);

        var folderName = Path.GetFileName(session.RootPath);
        var destination = Path.Combine(TrashedSessionsRoot, folderName);
        var suffix = 2;
        while (Directory.Exists(destination))
        {
            destination = Path.Combine(TrashedSessionsRoot, $"{folderName} {suffix}");
            suffix++;
        }

        Directory.Move(session.RootPath, destination);
        return true;
    }

    /// <summary>Lista las sesiones aparcadas en <c>&lt;vault&gt;/.trash/sessions/</c> — la fuente de la
    /// categoría "Sesiones" de la Papelera (Fase 3 de docs/analisis-proyecto/roadmap.md, top 10 #10 del
    /// análisis). Mismo criterio de lectura que <see cref="List"/> (manifiesto sintético para carpetas
    /// huérfanas, errores de E/S anunciados vía <see cref="ManifestError"/> en vez de propagarse).</summary>
    public List<Session> ListTrashedSessions()
    {
        var sessions = new List<Session>();
        foreach (var dir in ListSessionDirectories(TrashedSessionsRoot))
        {
            var manifest = ReadManifest(dir);
            if (manifest is null) continue;
            sessions.Add(ToSession(dir, manifest));
        }
        return sessions.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Devuelve una sesión borrada de la papelera a <c>.sessions/</c> — la acción "Restaurar" de
    /// la Papelera. La carpeta destino se desduplica con sufijo numérico si ya existe una sesión con el
    /// mismo nombre de carpeta (creada mientras tanto), igual que <see cref="Create"/>; el nombre visible
    /// de la sesión vive en el manifiesto y no se toca. Devuelve si realmente se restauró — <c>false</c>
    /// cuando la carpeta ya no estaba en la papelera (borrada a mano entre el listado y el clic).</summary>
    public bool RestoreTrashedSession(Session session)
    {
        if (!Directory.Exists(session.RootPath)) return false;

        Directory.CreateDirectory(SessionsRoot);

        var folderName = Path.GetFileName(session.RootPath);
        var destination = Path.Combine(SessionsRoot, folderName);
        var suffix = 2;
        while (Directory.Exists(destination))
        {
            destination = Path.Combine(SessionsRoot, $"{folderName} {suffix}");
            suffix++;
        }

        Directory.Move(session.RootPath, destination);
        return true;
    }

    /// <summary>Borra físicamente una sesión de la papelera (la acción "eliminar definitivamente" / el
    /// vaciado de la categoría) — a diferencia de <see cref="Delete"/>, esto no tiene vuelta atrás: la
    /// carpeta y todas sus notas desaparecen del disco.</summary>
    public static void PermanentlyDeleteTrashedSession(Session session)
    {
        if (Directory.Exists(session.RootPath)) Directory.Delete(session.RootPath, recursive: true);
    }

    /// <summary>Resolves the true global graph of the vault: every session's own notes (each still its own
    /// cluster for node positions/manual links, exactly as before) plus every note living directly at the
    /// vault root outside any session — all handed back as one <see cref="SessionGraph"/>, with wikilinks
    /// resolved across the <em>whole</em> combined note set, not just within each note's own session. That
    /// last part is what makes this a genuinely global graph rather than "every session's graph glued
    /// together": a <c>[[wikilink]]</c> from a session note to a vault-root note, or to a note in a
    /// different session, now actually produces an edge (see <see cref="ComputeGlobalWikiLinkEdges"/>),
    /// where it previously couldn't resolve at all because each session's wikilinks were only ever matched
    /// against its own note set. Same-scope edges (within one session, or between two vault-root notes)
    /// are still contributed by each scope's own resolution pass — the global pass only adds edges that
    /// cross a scope boundary, so nothing is computed twice.</summary>
    public SessionGraph LoadGlobalGraph()
    {
        var combined = new SessionGraph();

        // Every note currently known anywhere in the vault, keyed by its GlobalId — the universe the
        // cross-scope wikilink pass resolves against below. Seeded with vault-root notes first since they
        // have no session loop of their own to fold them in from.
        var byGlobalId = new Dictionary<string, NoteFile>(StringComparer.OrdinalIgnoreCase);
        var contentByGlobalId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var (rootNodes, rootEdges) = LoadRootGroup(byGlobalId, contentByGlobalId);
        combined.Nodes.AddRange(rootNodes);
        combined.Edges.AddRange(rootEdges);

        // Enumerates sessions directly instead of going through List() — List()'s Session.NoteCount is
        // for the sidebar's session list and costs its own recursive directory walk per session; nothing
        // below reads NoteCount, so paying for that walk here (once per session, on every graph load) was
        // pure waste on top of the walk LoadGraph itself already does to build the node set.
        foreach (var (path, manifest) in EnumerateSessionManifests())
        {
            var session = ToSessionLight(path, manifest);
            var graph = LoadGraph(session);
            combined.Nodes.AddRange(graph.Nodes);
            combined.Edges.AddRange(graph.Edges);

            // A locked encrypted session already contributed nothing above (LoadGraph short-circuits to an
            // empty graph for it) — its content can't be decrypted to scan for wikilinks either, so it's
            // left out of the cross-scope universe too, same as it's left out of everything else.
            if (session.IsEncrypted && !_encryptionService.IsUnlocked(session.RootPath)) continue;

            foreach (var (note, content) in _vaultService.GetAllNotesRecursiveWithContent(session.RootPath))
            {
                var relativePath = RelativePath(session.RootPath, note.FullPath);
                var globalId = $"{session.Id}::{relativePath}";
                byGlobalId[globalId] = note;
                contentByGlobalId[globalId] = content;
            }
        }

        var crossEdgeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fromGlobalId, toGlobalId) in ComputeGlobalWikiLinkEdges(byGlobalId, contentByGlobalId))
        {
            var (fromGroupId, fromRelativePath) = SplitGlobalId(fromGlobalId);
            var (toGroupId, toRelativePath) = SplitGlobalId(toGlobalId);
            if (fromGroupId == toGroupId) continue; // already covered by that scope's own resolution pass above

            if (!crossEdgeKeys.Add(EdgeKey(fromGlobalId, toGlobalId))) continue;
            combined.Edges.Add(new SessionGraphEdge
            {
                FromSessionId = fromGroupId,
                ToSessionId = toGroupId,
                FromRelativePath = fromRelativePath,
                ToRelativePath = toRelativePath
            });
        }

        return combined;
    }

    /// <summary>Async wrapper around <see cref="LoadGlobalGraph"/> — see <see cref="ListAsync"/> for why
    /// this exists (the same synchronous-I/O-off-the-UI-thread rationale, only worse here: this walks and
    /// reads every note in every session, plus every note at the vault root).</summary>
    public Task<SessionGraph> LoadGlobalGraphAsync() => Task.Run(() => LoadGlobalGraph());

    /// <summary>Builds the vault-root pseudo-group's own nodes and within-group wikilink edges — the notes
    /// living directly under the vault path, outside every session folder (<see cref="VaultService.GetAllNotesRecursiveWithContent"/>
    /// already excludes any path under a dot-prefixed directory, so this naturally excludes <c>.sessions</c>
    /// and <c>.trash</c>). Mirrors <see cref="LoadGraph"/>'s self-healing/position-persistence behavior for
    /// a real session, but against a vault-level <c>.graph.json</c> (see <see cref="ReadRootManifest"/>)
    /// since root notes have no session folder to keep a manifest in. Also feeds every root note's GlobalId
    /// and content into <paramref name="byGlobalId"/>/<paramref name="contentByGlobalId"/> so the caller's
    /// cross-scope wikilink pass can resolve links into/out of this group too.</summary>
    private (List<SessionGraphNode> Nodes, List<SessionGraphEdge> Edges) LoadRootGroup(
        Dictionary<string, NoteFile> byGlobalId, Dictionary<string, string> contentByGlobalId)
    {
        var manifest = ReadRootManifest();
        var notesWithContent = _vaultService.GetAllNotesRecursiveWithContent(_vaultService.VaultPath);

        var byPath = new Dictionary<string, NoteFile>(StringComparer.OrdinalIgnoreCase);
        var contentByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (note, content) in notesWithContent)
        {
            var relativePath = RelativePath(_vaultService.VaultPath, note.FullPath);
            byPath[relativePath] = note;
            contentByPath[relativePath] = content;

            var globalId = $"{SessionGraph.RootGroupId}::{relativePath}";
            byGlobalId[globalId] = note;
            contentByGlobalId[globalId] = content;
        }

        var storedPositions = manifest.Nodes.ToDictionary(n => n.RelativePath, StringComparer.OrdinalIgnoreCase);
        var changed = manifest.Nodes.RemoveAll(n => !byPath.ContainsKey(n.RelativePath)) > 0;

        var rootGroupName = LocalizationService.Instance["Sessions_Graph_RootGroupName"];
        var nodes = new List<SessionGraphNode>();
        foreach (var (relativePath, note) in byPath)
        {
            if (storedPositions.TryGetValue(relativePath, out var stored))
            {
                nodes.Add(new SessionGraphNode
                {
                    SessionId = SessionGraph.RootGroupId,
                    SessionName = rootGroupName,
                    RelativePath = relativePath,
                    Title = note.Title,
                    X = stored.X,
                    Y = stored.Y,
                    Z = stored.Z,
                    HasStoredPosition = true
                });
            }
            else
            {
                var (x, y, z) = InitialPosition(relativePath);
                nodes.Add(new SessionGraphNode
                {
                    SessionId = SessionGraph.RootGroupId,
                    SessionName = rootGroupName,
                    RelativePath = relativePath,
                    Title = note.Title,
                    X = x,
                    Y = y,
                    Z = z
                });
                changed = true;
            }
        }

        var edges = new List<SessionGraphEdge>();
        var edgeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in ComputeWikiLinkEdges(byPath, contentByPath))
        {
            if (edgeKeys.Add(EdgeKey(from, to)))
            {
                edges.Add(new SessionGraphEdge
                {
                    FromSessionId = SessionGraph.RootGroupId,
                    ToSessionId = SessionGraph.RootGroupId,
                    FromRelativePath = from,
                    ToRelativePath = to
                });
            }
        }

        if (changed)
        {
            manifest.Nodes = nodes.Select(n => new SessionManifestNode { RelativePath = n.RelativePath, X = n.X, Y = n.Y, Z = n.Z }).ToList();
            WriteRootManifest(manifest);
        }

        return (nodes, edges);
    }

    /// <summary>Batch-persists final node coordinates for vault-root notes — the vault-level counterpart of
    /// <see cref="SavePositions"/>, since root notes have no session manifest of their own to save into.</summary>
    public void SaveRootPositions(IEnumerable<(string RelativePath, double X, double Y, double Z)> positions)
    {
        var manifest = ReadRootManifest();
        var byPath = manifest.Nodes.ToDictionary(n => n.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var (relativePath, x, y, z) in positions)
        {
            if (byPath.TryGetValue(relativePath, out var node))
            {
                node.X = x;
                node.Y = y;
                node.Z = z;
            }
        }

        WriteRootManifest(manifest);
    }

    /// <summary>Resolves a single session's graph: every note file on disk under the session root as a
    /// node (self-healing against the manifest — new files get a node, deleted files lose theirs), plus
    /// edges derived from <c>[[wikilinks]]</c> in note content and manually-drawn edges from the manifest.</summary>
    public SessionGraph LoadGraph(Session session)
    {
        // A locked encrypted session contributes nothing to the graph — not even opaque placeholder nodes,
        // since a node's label is a note's decrypted title. Short-circuits before any disk read (no
        // GetAllNotesRecursiveWithContent call at all), so nothing here ever touches SessionLockedException.
        if (session.IsEncrypted && !_encryptionService.IsUnlocked(session.RootPath)) return new SessionGraph();

        var manifest = ReadManifest(session.RootPath) ?? EmptyManifestFor(session);
        // Reads every note's content exactly once — the metadata (title) and the wikilink edges below both
        // need it, and re-reading each file a second time to extract [[wikilinks]] (the old SafeRead call)
        // doubled every session's disk I/O for no reason.
        var notesWithContent = _vaultService.GetAllNotesRecursiveWithContent(session.RootPath);
        var changed = false;

        var byPath = new Dictionary<string, NoteFile>(StringComparer.OrdinalIgnoreCase);
        var contentByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (note, content) in notesWithContent)
        {
            var relativePath = RelativePath(session.RootPath, note.FullPath);
            byPath[relativePath] = note;
            contentByPath[relativePath] = content;
        }

        var storedPositions = manifest.Nodes.ToDictionary(n => n.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (manifest.Nodes.RemoveAll(n => !byPath.ContainsKey(n.RelativePath)) > 0) changed = true;

        var nodes = new List<SessionGraphNode>();
        foreach (var (relativePath, note) in byPath)
        {
            if (storedPositions.TryGetValue(relativePath, out var stored))
            {
                nodes.Add(new SessionGraphNode
                {
                    SessionId = session.Id,
                    SessionName = session.Name,
                    RelativePath = relativePath,
                    Title = note.Title,
                    X = stored.X,
                    Y = stored.Y,
                    Z = stored.Z,
                    HasStoredPosition = true
                });
            }
            else
            {
                var (x, y, z) = InitialPosition(relativePath);
                nodes.Add(new SessionGraphNode
                {
                    SessionId = session.Id,
                    SessionName = session.Name,
                    RelativePath = relativePath,
                    Title = note.Title,
                    X = x,
                    Y = y,
                    Z = z
                });
                changed = true;
            }
        }

        var edges = new List<SessionGraphEdge>();
        var edgeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var derived in ComputeWikiLinkEdges(byPath, contentByPath))
        {
            if (edgeKeys.Add(EdgeKey(derived.From, derived.To)))
                edges.Add(new SessionGraphEdge { FromSessionId = session.Id, ToSessionId = session.Id, FromRelativePath = derived.From, ToRelativePath = derived.To });
        }

        if (manifest.ManualLinks.RemoveAll(e => !byPath.ContainsKey(e.FromRelativePath) || !byPath.ContainsKey(e.ToRelativePath)) > 0)
            changed = true;

        foreach (var manual in manifest.ManualLinks)
        {
            var key = EdgeKey(manual.FromRelativePath, manual.ToRelativePath);
            if (edgeKeys.Add(key))
            {
                edges.Add(new SessionGraphEdge { FromSessionId = session.Id, ToSessionId = session.Id, FromRelativePath = manual.FromRelativePath, ToRelativePath = manual.ToRelativePath, IsManual = true });
            }
            else
            {
                var existing = edges.First(e => EdgeKey(e.FromRelativePath, e.ToRelativePath) == key);
                existing.IsManual = true;
            }
        }

        if (changed)
        {
            manifest.Nodes = nodes.Select(n => new SessionManifestNode { RelativePath = n.RelativePath, X = n.X, Y = n.Y, Z = n.Z }).ToList();
            WriteManifest(session.RootPath, manifest);
        }

        return new SessionGraph { Nodes = nodes, Edges = edges };
    }

    /// <summary>Batch-persists final node coordinates once the force simulation settles, so the layout
    /// resumes roughly where it left off next time the session is opened.</summary>
    public void SavePositions(Session session, IEnumerable<(string RelativePath, double X, double Y, double Z)> positions)
    {
        var manifest = ReadManifest(session.RootPath);
        if (manifest is null) return;

        var byPath = manifest.Nodes.ToDictionary(n => n.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var (relativePath, x, y, z) in positions)
        {
            if (byPath.TryGetValue(relativePath, out var node))
            {
                node.X = x;
                node.Y = y;
                node.Z = z;
            }
        }

        WriteManifest(session.RootPath, manifest);
    }

    /// <summary>Toggles a manual edge between two notes: creates it if absent, removes it if present.
    /// Returns whether the edge exists after the call.</summary>
    public bool ToggleManualLink(Session session, string fromRelativePath, string toRelativePath)
    {
        var manifest = ReadManifest(session.RootPath) ?? EmptyManifestFor(session);
        var key = EdgeKey(fromRelativePath, toRelativePath);
        var existing = manifest.ManualLinks.FirstOrDefault(e => EdgeKey(e.FromRelativePath, e.ToRelativePath) == key);

        bool nowLinked;
        if (existing is not null)
        {
            manifest.ManualLinks.Remove(existing);
            nowLinked = false;
        }
        else
        {
            manifest.ManualLinks.Add(new SessionManifestEdge { FromRelativePath = fromRelativePath, ToRelativePath = toRelativePath });
            nowLinked = true;
        }

        WriteManifest(session.RootPath, manifest);
        return nowLinked;
    }

    private static IEnumerable<(string From, string To)> ComputeWikiLinkEdges(
        Dictionary<string, NoteFile> byPath, Dictionary<string, string> contentByPath)
    {
        // File names are unique on disk (VaultService.CreateNote de-duplicates them), but every fresh note
        // starts life with the exact same "# Nueva nota" heading, so two or more untitled notes routinely
        // share the same Title. byTitle used to be built with TryAdd — first note wins, every later note
        // with that same title silently has no title-based resolution at all — so [[Nueva nota]] either
        // linked the wrong note or (if that first note happened to be the one containing the link) resolved
        // to itself and got dropped by the self-link check below, which reads as "wikilinks don't connect
        // anything" with no error and no indication why. Ambiguous titles are now excluded from byTitle
        // entirely — filename-based resolution (always unique) still works for them, and there's no
        // plausible-but-wrong guess.
        var byFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguousTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relativePath, note) in byPath)
        {
            byFileName.TryAdd(Path.GetFileNameWithoutExtension(note.FullPath), relativePath);

            if (ambiguousTitles.Contains(note.Title)) continue;
            if (!byTitle.TryAdd(note.Title, relativePath))
            {
                byTitle.Remove(note.Title);
                ambiguousTitles.Add(note.Title);
            }
        }

        foreach (var (relativePath, note) in byPath)
        {
            var content = contentByPath[relativePath];
            foreach (var target in WikiLinkParser.ExtractTargets(content))
            {
                if (!byFileName.TryGetValue(target, out var resolved) && !byTitle.TryGetValue(target, out resolved))
                    continue;
                if (string.Equals(resolved, relativePath, StringComparison.OrdinalIgnoreCase)) continue;

                yield return (relativePath, resolved);
            }
        }
    }

    /// <summary>Same resolution strategy as <see cref="ComputeWikiLinkEdges"/> (exact filename first, else
    /// an unambiguous title match, self-links dropped), run over the whole vault-wide note universe instead
    /// of one session's — keyed by GlobalId so a target can resolve to a note in any group. A vault with
    /// several sessions routinely has more than one note sharing the same starting title (every fresh note
    /// begins as "# Nueva nota"), so both the filename and the title maps are ambiguity-aware here: a
    /// filename or title held by more than one note vault-wide resolves to nothing rather than an arbitrary
    /// guess, exactly like <see cref="ComputeWikiLinkEdges"/> already does for titles within one session.</summary>
    private static IEnumerable<(string FromGlobalId, string ToGlobalId)> ComputeGlobalWikiLinkEdges(
        Dictionary<string, NoteFile> byGlobalId, Dictionary<string, string> contentByGlobalId)
    {
        var byFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguousFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguousTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (globalId, note) in byGlobalId)
        {
            var fileName = Path.GetFileNameWithoutExtension(note.FullPath);
            if (!ambiguousFileNames.Contains(fileName))
            {
                if (!byFileName.TryAdd(fileName, globalId))
                {
                    byFileName.Remove(fileName);
                    ambiguousFileNames.Add(fileName);
                }
            }

            if (ambiguousTitles.Contains(note.Title)) continue;
            if (!byTitle.TryAdd(note.Title, globalId))
            {
                byTitle.Remove(note.Title);
                ambiguousTitles.Add(note.Title);
            }
        }

        foreach (var (globalId, note) in byGlobalId)
        {
            var content = contentByGlobalId[globalId];
            foreach (var target in WikiLinkParser.ExtractTargets(content))
            {
                if (!byFileName.TryGetValue(target, out var resolved) && !byTitle.TryGetValue(target, out resolved))
                    continue;
                if (string.Equals(resolved, globalId, StringComparison.OrdinalIgnoreCase)) continue;

                yield return (globalId, resolved);
            }
        }
    }

    /// <summary>Splits a <see cref="SessionGraphNode.GlobalId"/>-shaped string ("{groupId}::{relativePath}")
    /// back into its parts — the inverse of how every GlobalId in this file is built. Safe against a
    /// relative path itself containing "::" (vanishingly unlikely, but not impossible on some filesystems)
    /// since only the *first* occurrence is treated as the separator, matching how every GlobalId here is
    /// always constructed as exactly one groupId followed by one relative path.</summary>
    private static (string GroupId, string RelativePath) SplitGlobalId(string globalId)
    {
        var separatorIndex = globalId.IndexOf("::", StringComparison.Ordinal);
        return separatorIndex < 0 ? (globalId, string.Empty) : (globalId[..separatorIndex], globalId[(separatorIndex + 2)..]);
    }

    private static string EdgeKey(string a, string b)
    {
        var ordered = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
        return $"{ordered.Item1}␟{ordered.Item2}".ToLowerInvariant();
    }

    private static string RelativePath(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Deterministic point on a sphere, seeded from the note's own path so a freshly-discovered
    /// node (no cached position yet) lands somewhere stable and non-overlapping across reloads, instead
    /// of at the origin — d3-force-3d can't compute a repulsion direction between two coincident nodes.</summary>
    private static (double X, double Y, double Z) InitialPosition(string relativePath)
    {
        var rnd = new Random(StableHash(relativePath));
        var theta = rnd.NextDouble() * Math.PI * 2;
        var phi = Math.Acos(2 * rnd.NextDouble() - 1);
        var r = 60 + rnd.NextDouble() * 40;
        return (
            r * Math.Sin(phi) * Math.Cos(theta),
            r * Math.Sin(phi) * Math.Sin(theta),
            r * Math.Cos(phi));
    }

    /// <summary>FNV-1a — <see cref="string.GetHashCode()"/> is randomized per process on .NET Core and
    /// can't be used as a stable seed across app restarts.</summary>
    private static int StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(value))
            {
                hash ^= b;
                hash *= 16777619;
            }
            return (int)hash;
        }
    }

    private static Session ToSession(string path, SessionManifest manifest) => new()
    {
        Id = manifest.Id,
        Name = manifest.Name,
        RootPath = path,
        CreatedUtc = manifest.CreatedUtc,
        ModifiedUtc = File.Exists(Path.Combine(path, ManifestFileName))
            ? File.GetLastWriteTimeUtc(Path.Combine(path, ManifestFileName))
            : manifest.CreatedUtc,
        NoteCount = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.md", SearchOption.AllDirectories).Count()
            : 0,
        IsDefault = manifest.IsDefault,
        IsEncrypted = manifest.Encryption is not null
    };

    /// <summary>Same as <see cref="ToSession"/> but skips the recursive directory walk behind
    /// <see cref="Session.NoteCount"/> — for callers (<see cref="LoadGlobalGraph"/>) that only need
    /// <see cref="Session.Id"/>/<see cref="Session.Name"/>/<see cref="Session.RootPath"/> to load a graph
    /// and never read NoteCount at all.</summary>
    private static Session ToSessionLight(string path, SessionManifest manifest) => new()
    {
        Id = manifest.Id,
        Name = manifest.Name,
        RootPath = path,
        CreatedUtc = manifest.CreatedUtc,
        ModifiedUtc = manifest.CreatedUtc,
        NoteCount = 0,
        IsDefault = manifest.IsDefault,
        IsEncrypted = manifest.Encryption is not null
    };

    /// <summary>Reconstructs a manifest shell from a <see cref="Session"/> already in hand, for the rare
    /// case its <c>.session.json</c> can't be read back (deleted/corrupted out-of-band) — preserves
    /// <see cref="Session.IsDefault"/> so a damaged default session doesn't lose its protection.</summary>
    private static SessionManifest EmptyManifestFor(Session session) => new()
    {
        Id = session.Id,
        Name = session.Name,
        CreatedUtc = session.CreatedUtc,
        IsDefault = session.IsDefault
    };

    private SessionManifest? ReadManifest(string sessionPath)
    {
        var manifestPath = Path.Combine(sessionPath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            if (!Directory.Exists(sessionPath)) return null;
            // Orphaned session folder (manifest missing/deleted out-of-band): synthesize one from the
            // folder name so the session stays usable instead of silently disappearing from the list.
            return new SessionManifest
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = Path.GetFileName(sessionPath),
                CreatedUtc = Directory.GetCreationTimeUtc(sessionPath)
            };
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<SessionManifest>(json, JsonOptions);
        }
        catch (IOException ex)
        {
            ManifestError?.Invoke(string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Sessions_ManifestReadError"], Path.GetFileName(sessionPath), ex.Message));
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            ManifestError?.Invoke(string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Sessions_ManifestReadError"], Path.GetFileName(sessionPath), ex.Message));
            return null;
        }
        catch (JsonException)
        {
            // Manifiesto legible pero con JSON inválido: tolerado en silencio, igual que antes — los
            // llamadores ya reconstruyen un manifiesto vacío con EmptyManifestFor cuando esto pasa.
            return null;
        }
    }

    private const string RootGraphManifestFileName = ".graph.json";

    /// <summary>On-disk shape of the vault-root graph's own "fichero de localización" (<c>&lt;vault&gt;/.graph.json</c>)
    /// — just the cached layout of vault-root notes, the vault-level counterpart of <see cref="SessionManifest.Nodes"/>.
    /// Vault-root notes have no manual links or encryption of their own (both are session concepts), so
    /// this carries none of the rest of <see cref="SessionManifest"/>'s shape.</summary>
    private sealed class RootGraphManifest
    {
        public List<SessionManifestNode> Nodes { get; set; } = [];
    }

    private RootGraphManifest ReadRootManifest()
    {
        var manifestPath = Path.Combine(_vaultService.VaultPath, RootGraphManifestFileName);
        if (!File.Exists(manifestPath)) return new RootGraphManifest();

        try
        {
            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<RootGraphManifest>(json, JsonOptions) ?? new RootGraphManifest();
        }
        catch (IOException ex)
        {
            ManifestError?.Invoke(string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Sessions_ManifestReadError"], RootGraphManifestFileName, ex.Message));
            return new RootGraphManifest();
        }
        catch (UnauthorizedAccessException ex)
        {
            ManifestError?.Invoke(string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Sessions_ManifestReadError"], RootGraphManifestFileName, ex.Message));
            return new RootGraphManifest();
        }
        catch (JsonException)
        {
            // Readable but invalid JSON: tolerated in silence, same as a corrupt session manifest — the
            // caller already reconstructs an empty manifest whenever this happens.
            return new RootGraphManifest();
        }
    }

    private void WriteRootManifest(RootGraphManifest manifest)
    {
        var manifestPath = Path.Combine(_vaultService.VaultPath, RootGraphManifestFileName);
        try
        {
            var tempPath = $"{manifestPath}.tmp-{Guid.NewGuid():N}";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(manifest, JsonOptions));
            File.Move(tempPath, manifestPath, overwrite: true);
        }
        catch (IOException ex)
        {
            ManifestError?.Invoke(string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Sessions_ManifestWriteError"], RootGraphManifestFileName, ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            ManifestError?.Invoke(string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Sessions_ManifestWriteError"], RootGraphManifestFileName, ex.Message));
        }
    }

    private void WriteManifest(string sessionPath, SessionManifest manifest)
    {
        var manifestPath = Path.Combine(sessionPath, ManifestFileName);
        // Best-effort: this runs on every drag-settle/link-toggle (see SavePositions/ToggleManualLink),
        // including from inside a Dispatcher.UIThread.Post callback (SessionsView.OnWebMessageReceived)
        // that nothing above it catches. A session folder briefly locked, moved, or deleted out-of-band
        // (e.g. from Explorer/Finder or another process) must not take the whole app down over a graph
        // position update — ReadManifest already tolerates a missing/corrupt manifest the same way. Written
        // atomically (temp file + File.Move) since Fase 1 (docs/analisis-proyecto/roadmap.md) — this already
        // reported I/O failures instead of swallowing them, it just wasn't atomic yet.
        try
        {
            var tempPath = $"{manifestPath}.tmp-{Guid.NewGuid():N}";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(manifest, JsonOptions));
            File.Move(tempPath, manifestPath, overwrite: true);
        }
        catch (IOException ex)
        {
            ManifestError?.Invoke(string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Sessions_ManifestWriteError"], Path.GetFileName(sessionPath), ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            ManifestError?.Invoke(string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Sessions_ManifestWriteError"], Path.GetFileName(sessionPath), ex.Message));
        }
    }
}
