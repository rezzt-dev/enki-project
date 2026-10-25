using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using enki.Localization;
using enki.Models;

namespace enki.Services;

/// <summary>Visión general del ámbito y notas ("documentos"): listar, leer, buscar, crear, editar, organizar
/// y enviar a la papelera, con los mismos efectos colaterales que la app (enlaces de tarjetas Kanban,
/// wikilinks al renombrar, pestañas abiertas del editor).</summary>
public sealed partial class AiVaultToolbox
{
    /// <summary>Lo más que se devuelve de una nota en una lectura. Una nota recortada no se puede reescribir
    /// entera (el modelo no ha visto el resto), pero sí editar por fragmentos.</summary>
    private const int MaxNoteChars = 60_000;

    private void RegisterOverviewTools()
    {
        Register("get_workspace_overview", AiToolScope.Content, AiToolEffect.Read,
            "Call this first. Describes what this chat can access: its workspaces (the vault root and/or sessions) with "
            + "note and file counts, which modules are available (Kanban, Polar, reminders, agenda, trash, session management), "
            + "whether you may change content, and the current local date, time and time zone.",
            Schema(), call =>
            {
                var scope = call.Context.Scope;
                var vaultModules = scope.IncludesVault;
                return new
                {
                    Chat = scope.IsSessionChat ? "session" : "vault",
                    FullAccess = scope.FullVaultAccess,
                    ChangesAllowed = ChangesAllowed,
                    Workspaces = Workspaces(scope).Select(w =>
                    {
                        var locked = w.Session is { } session && !_sessions.IsUnlocked(session);
                        return new
                        {
                            Workspace = WorkspaceKey(w),
                            w.Id,
                            Kind = w.IsVault ? "vault" : "session",
                            Home = w.IsVault ? !scope.IsSessionChat : w.Id == scope.SessionId,
                            Locked = locked,
                            PasswordProtected = w.Session?.IsEncrypted ?? false,
                            Notes = locked ? (int?)null : VaultService.CountNotesRecursive(w.RootPath),
                            Files = locked ? (int?)null : VaultService.CountFilesRecursive(w.RootPath)
                        };
                    }),
                    Modules = new
                    {
                        Kanban = vaultModules,
                        Polar = vaultModules && _polarAuth.IsLinked,
                        Reminders = vaultModules,
                        Agenda = vaultModules && _calendar is not null,
                        Trash = vaultModules,
                        SessionManagement = vaultModules && scope.FullVaultAccess
                    },
                    Now = FormatInstant(DateTime.UtcNow),
                    TimeZone = TimeZoneInfo.Local.Id
                };
            });
    }

    private void RegisterNoteTools()
    {
        Register("list_notes", AiToolScope.Content, AiToolEffect.Read,
            "Lists notes (Markdown documents) of a workspace, most recently modified first, with title, tags and word count. "
            + "Without 'folder' it lists the whole workspace; with recursive=false only that folder, plus its subfolders.",
            Schema(WorkspaceParam(), Opt("folder", Text("Folder path relative to the workspace (omit for the root).")),
                Opt("recursive", Flag("Include subfolders (default true).")),
                Opt("tag", Text("Only notes with this tag.")), LimitParam()),
            call =>
            {
                var workspace = Workspace(call);
                var folder = ExistingFolder(workspace, call.GetText("folder"));
                var recursive = call.GetFlag("recursive") ?? true;
                var tag = call.GetText("tag")?.Trim().TrimStart('#');
                var notes = (recursive ? _vault.GetAllNotesRecursive(folder) : _vault.GetNotes(folder))
                    .Where(n => string.IsNullOrEmpty(tag) || n.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                var limit = Limit(call);
                return new
                {
                    Workspace = WorkspaceKey(workspace),
                    Total = notes.Count,
                    Truncated = notes.Count > limit,
                    Notes = notes.Take(limit).Select(n => NoteSummary(workspace, n)),
                    Subfolders = recursive ? null : _vault.GetFolderTree(folder).Select(f => RelativePath(workspace, f.FullPath))
                };
            });

        Register("read_note", AiToolScope.Content, AiToolEffect.Read,
            "Reads a note: its Markdown content (including YAML front matter), title, tags and properties. "
            + $"Content longer than {MaxNoteChars} characters is truncated. 'path' may also be the exact note title.",
            Schema(WorkspaceParam(), Req("path", Text("Note path relative to the workspace, e.g. \"Projects/Plan.md\", or its exact title."))),
            call =>
            {
                var (workspace, path) = FindNote(call);
                var disk = _vault.ReadFromDisk(path) ?? throw NoteNotFound(call.GetText("path"));
                call.Target(AiToolTargetKind.Note, disk.Note.Title, path);
                var truncated = disk.Content.Length > MaxNoteChars;
                if (!truncated) call.Context.CompleteReads[path] = disk.Stamp;
                return new
                {
                    Workspace = WorkspaceKey(workspace),
                    Path = RelativePath(workspace, path),
                    disk.Note.Title,
                    disk.Note.Tags,
                    Properties = NotePropertyHelper.Extract(disk.Content),
                    Modified = FormatInstant(disk.Note.ModifiedUtc),
                    disk.Note.WordCount,
                    Truncated = truncated,
                    Content = truncated ? Truncate(disk.Content, MaxNoteChars) : disk.Content
                };
            });

        Register("search_notes", AiToolScope.Content, AiToolEffect.Read,
            "Full-text search over note titles, tags and content. Searches every workspace of this chat unless 'workspace' is given. "
            + "Returns matching notes with a snippet and line number.",
            Schema(Req("query", Text("Words to look for.")), WorkspaceParam("Limit the search to this workspace (omit to search all of them)."),
                LimitParam()),
            call =>
            {
                var query = call.RequireText("query");
                call.Target(AiToolTargetKind.None, query);
                var limit = Limit(call);
                List<AiWorkspace> workspaces = call.Has("workspace")
                    ? [Workspace(call)]
                    : Workspaces(call.Context.Scope).Where(w => w.Session is null || _sessions.IsUnlocked(w.Session)).ToList();
                var hits = workspaces
                    .SelectMany(w => _search.SearchTree(w.RootPath, query, limit).Select(hit => (Workspace: w, Hit: hit)))
                    .Take(limit)
                    .ToList();
                return new
                {
                    Total = hits.Count,
                    Results = hits.Select(h => new
                    {
                        Workspace = WorkspaceKey(h.Workspace),
                        Path = RelativePath(h.Workspace, h.Hit.Note.FullPath),
                        h.Hit.Note.Title,
                        MatchedIn = h.Hit.Field.ToString().ToLowerInvariant(),
                        Line = h.Hit.LineNumber > 0 ? h.Hit.LineNumber : (int?)null,
                        h.Hit.Snippet
                    })
                };
            });

        Register("create_note", AiToolScope.Content, AiToolEffect.Change,
            "Creates a new note. 'content' is the Markdown body; if it does not start with a '# ' heading, one with the title is added. "
            + "If a note with that name already exists, a numeric suffix is added. The folder must exist.",
            Schema(Req("title", Text("Note title, also used as the file name.")), WorkspaceParam(),
                Opt("folder", Text("Destination folder relative to the workspace (omit for the root).")),
                Opt("content", Text("Markdown content.")), Opt("tags", TextList("Tags to set in the front matter."))),
            call =>
            {
                var title = call.RequireText("title").Trim();
                call.Target(AiToolTargetKind.Note, title);
                var workspace = Workspace(call);
                var folder = ExistingFolder(workspace, call.GetText("folder"));
                var body = call.GetText("content") ?? string.Empty;
                var content = body.TrimStart().StartsWith("# ", StringComparison.Ordinal) || body.StartsWith("---", StringComparison.Ordinal)
                    ? body
                    : $"# {title}\n\n{body}";
                if (call.GetTextList("tags") is { Count: > 0 } tags) content = NoteTagHelper.SetTags(content, tags);
                if (!content.EndsWith('\n')) content += "\n";

                var note = _vault.CreateNote(title, folder, content);
                call.Target(AiToolTargetKind.Note, note.Title, note.FullPath);
                // El modelo acaba de escribir la nota entera: la conoce igual que si la hubiera leído.
                if (NoteDiskStamps.TryRead(note.FullPath) is { } stamp) call.Context.CompleteReads[note.FullPath] = stamp;
                return NoteSummary(workspace, note);
            });

        Register("write_note", AiToolScope.Content, AiToolEffect.Change,
            "Replaces the whole content of a note. You must have read the complete note with read_note in this same turn; "
            + "prefer edit_note or append_to_note for partial changes. The previous content is kept in the note's version history.",
            Schema(WorkspaceParam(), Req("path", Text("Note path or exact title.")), Req("content", Text("New full Markdown content."))),
            call =>
            {
                var (workspace, path) = FindNote(call);
                var content = call.GetText("content") ?? throw AiToolCall.Invalid("'content' is required.");
                return NoteSummary(workspace, ChangeNote(call, path, _ => content, requireCompleteRead: true));
            });

        Register("edit_note", AiToolScope.Content, AiToolEffect.Change,
            "Edits a note by exact text replacement, applied in order. Each 'find' must appear exactly once in the current content "
            + "(include surrounding words to make it unique) unless 'all' is true. Nothing is changed if any edit does not apply.",
            Schema(WorkspaceParam(), Req("path", Text("Note path or exact title.")),
                Req("edits", ObjectList("Replacements to apply.", Schema(
                    Req("find", Text("Exact text to find.")), Req("replace", Text("Replacement text (may be empty).")),
                    Opt("all", Flag("Replace every occurrence (default false).")))))),
            call =>
            {
                var (workspace, path) = FindNote(call);
                var edits = call.GetObjects("edits") is { Count: > 0 } list ? list : throw AiToolCall.Invalid("'edits' must contain at least one edit.");
                return NoteSummary(workspace, ChangeNote(call, path, content => ApplyEdits(call, content, edits), requireCompleteRead: false));
            });

        Register("append_to_note", AiToolScope.Content, AiToolEffect.Change,
            "Adds Markdown text to a note, at the end (default) or at the start of its body, after the front matter and title.",
            Schema(WorkspaceParam(), Req("path", Text("Note path or exact title.")), Req("text", Text("Markdown to add.")),
                Opt("position", Choice("Where to add it.", "end", "start"))),
            call =>
            {
                var (workspace, path) = FindNote(call);
                var text = call.RequireText("text").Trim('\n');
                var atStart = call.GetText("position") == "start";
                return NoteSummary(workspace, ChangeNote(call, path, content => atStart ? InsertAtStart(content, text) : AppendAtEnd(content, text),
                    requireCompleteRead: false));
            });

        Register("update_note_metadata", AiToolScope.Content, AiToolEffect.Change,
            "Changes a note's tags and/or front matter properties without touching the rest of its content. "
            + "'tags' replaces all tags; 'add_tags'/'remove_tags' adjust them. A property set to null is removed.",
            Schema(WorkspaceParam(), Req("path", Text("Note path or exact title.")),
                Opt("tags", TextList("Full replacement list of tags.")), Opt("add_tags", TextList("Tags to add.")),
                Opt("remove_tags", TextList("Tags to remove.")),
                Opt("properties", new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Front matter properties to set (string values) or remove (null).",
                    ["additionalProperties"] = new System.Text.Json.Nodes.JsonObject { ["type"] = new System.Text.Json.Nodes.JsonArray("string", "null") }
                })),
            call =>
            {
                var (workspace, path) = FindNote(call);
                return NoteSummary(workspace, ChangeNote(call, path, content => ApplyMetadata(call, content), requireCompleteRead: false));
            });

        Register("rename_note", AiToolScope.Content, AiToolEffect.Change,
            "Renames a note's file. Every [[wikilink]] pointing to it across the vault is updated, and so are Kanban cards linked to it.",
            Schema(WorkspaceParam(), Req("path", Text("Note path or exact title.")), Req("new_title", Text("New name, without extension."))),
            call =>
            {
                var (workspace, path) = FindNote(call);
                var newTitle = call.RequireText("new_title").Trim();
                var note = _vault.GetNote(path) ?? throw NoteNotFound(call.GetText("path"));
                call.Target(AiToolTargetKind.Note, note.Title, path);
                PrepareNoteChange(path);
                var renamed = _vault.RenameNote(note, newTitle);
                _kanban.RelinkNotePath(path, renamed.FullPath);
                _host.NoteChanged(path, renamed);
                call.Target(AiToolTargetKind.Note, Path.GetFileNameWithoutExtension(renamed.FullPath), renamed.FullPath);
                return NoteSummary(workspace, renamed);
            });

        RegisterAsync("move_note", AiToolScope.Content, AiToolEffect.Change,
            "Moves (or copies, with copy=true) a note and its attachments to another folder, possibly in another workspace of this chat. "
            + "Kanban cards linked to a moved note follow it.",
            Schema(WorkspaceParam(), Req("path", Text("Note path or exact title.")),
                Opt("destination_folder", Text("Destination folder relative to the destination workspace (omit for its root).")),
                Opt("destination_workspace", Text("Destination workspace (omit to stay in the same one).")),
                Opt("copy", Flag("Copy instead of moving (default false)."))),
            async call =>
            {
                var (workspace, path) = FindNote(call);
                var note = _vault.GetNote(path) ?? throw NoteNotFound(call.GetText("path"));
                call.Target(AiToolTargetKind.Note, note.Title, path);
                var destinationWorkspace = call.Has("destination_workspace") ? Workspace(call, "destination_workspace") : workspace;
                var destination = ExistingFolder(destinationWorkspace, call.GetText("destination_folder"));
                var copy = call.GetFlag("copy") ?? false;
                await RequireApprovalToLeaveProtectedSessionAsync(call, path, destination, note.Title);

                PrepareNoteChange(path);
                var result = _vault.TransferNote(note, destination, cut: !copy);
                if (!copy)
                {
                    _kanban.RelinkNotePath(path, result.FullPath);
                    _host.NoteChanged(path, result);
                }
                call.Target(AiToolTargetKind.Note, result.Title, result.FullPath);
                return NoteSummary(destinationWorkspace, result);
            });

        Register("delete_note", AiToolScope.Content, AiToolEffect.Change,
            "Moves a note to the trash, where it can be restored for 30 days.",
            Schema(WorkspaceParam(), Req("path", Text("Note path or exact title."))),
            call =>
            {
                var (_, path) = FindNote(call);
                var note = _vault.GetNote(path) ?? throw NoteNotFound(call.GetText("path"));
                call.Target(AiToolTargetKind.Note, note.Title);
                PrepareNoteChange(path);
                _vault.DeleteNote(note);
                _host.NoteChanged(path, null);
                return new { Trashed = true, note.Title };
            });

        Register("get_note_links", AiToolScope.Content, AiToolEffect.Read,
            "Lists a note's outgoing [[wikilinks]] (resolved to notes when possible) and the notes that link to it (backlinks), "
            + "within this chat's workspaces.",
            Schema(WorkspaceParam(), Req("path", Text("Note path or exact title."))),
            call =>
            {
                var (_, path) = FindNote(call);
                var notes = AccessibleNotesWithContent(call.Context.Scope);
                var target = notes.FirstOrDefault(n => VaultService.PathsEqual(n.Note.FullPath, path));
                if (target.Note is null) throw NoteNotFound(call.GetText("path"));
                call.Target(AiToolTargetKind.Note, target.Note.Title, path);
                var all = notes.Select(n => n.Note).ToList();
                return new
                {
                    Outgoing = WikiLinkParser.FindOccurrences(target.Content)
                        .Select(o => (Occurrence: o, Resolved: WikiLinkParser.ResolveTarget(all, o.Target)))
                        .Select(t => new { Link = t.Occurrence.Target, Note = t.Resolved is { } note ? NoteLocation(call.Context.Scope, note) : null })
                        .DistinctBy(l => l.Link),
                    Backlinks = notes
                        .Where(n => !VaultService.PathsEqual(n.Note.FullPath, path)
                            && WikiLinkParser.ExtractTargets(n.Content).Any(t => WikiLinkParser.ResolveTarget(all, t) is { } r
                                && VaultService.PathsEqual(r.FullPath, path)))
                        .Select(n => NoteLocation(call.Context.Scope, n.Note))
                };
            });
    }

    // ===================== Escritura =====================

    /// <summary>El único camino por el que el asistente cambia el contenido de una nota: vuelca y comprueba la
    /// pestaña abierta, relee el disco, guarda lo que había en el historial y escribe contra esa huella. Un
    /// cambio hecho por otro mientras tanto (el usuario, un sincronizador) aborta la escritura en vez de pisarlo.</summary>
    private NoteFile ChangeNote(AiToolCall call, string path, Func<string, string> transform, bool requireCompleteRead)
    {
        var reads = call.Context.CompleteReads;
        if (requireCompleteRead && !reads.ContainsKey(path))
            throw AiToolCall.Invalid("Read the complete note with read_note in this turn before replacing its whole content, or use edit_note.");

        PrepareNoteChange(path);
        var fresh = _vault.ReadFromDisk(path) ?? throw NoteNotFound(Path.GetFileNameWithoutExtension(path));
        call.Target(AiToolTargetKind.Note, fresh.Note.Title, path);
        // Se compara el hash y no la huella entera: un sincronizador que reescribe los mismos bytes no es un cambio.
        if (requireCompleteRead && reads[path].ContentHash != fresh.Stamp.ContentHash)
            throw new AiToolException(AiToolErrorKind.Conflict, "The note changed after you read it. Read it again before replacing it.");

        var updated = transform(fresh.Content);
        // Las ediciones parciales conservan el formato de línea del documento original.
        if (!requireCompleteRead) updated = KeepLineEndings(fresh.Content, updated);
        if (updated == fresh.Content) return fresh.Note;

        var (note, stamp) = CommitNoteChange(path, fresh.Note, fresh.Content, fresh.Stamp, updated);
        // Lo que acaba de quedar escrito lo conoce el modelo entero si lo escribió entero o si ya lo había leído.
        if (stamp is { } written && (requireCompleteRead || reads.ContainsKey(path))) reads[path] = written;
        else reads.Remove(path);
        call.Target(AiToolTargetKind.Note, note.Title, path);
        return note;
    }

    /// <summary>Guarda lo que había en el historial de la nota, escribe contra la huella leída y avisa a las
    /// pestañas abiertas. Común a las herramientas y a "Añadir a la nota activa".</summary>
    private (NoteFile Note, NoteDiskStamp? Stamp) CommitNoteChange(string path, NoteFile current, string previousContent,
        NoteDiskStamp baseline, string updated)
    {
        _vault.CaptureSnapshotChecked(current, previousContent, NoteVersionTrigger.Assistant);
        var stamp = _vault.WriteContentChecked(current, updated, baseline);
        var note = _vault.GetNote(path) ?? current;
        _host.NoteChanged(path, note);
        return (note, stamp);
    }

    private static string KeepLineEndings(string original, string updated) =>
        original.Contains("\r\n", StringComparison.Ordinal)
            ? updated.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)
            : updated;

    /// <summary>"Añadir a la nota activa" del chat: lo pide el usuario sobre una respuesta, no el modelo, pero
    /// pasa por el mismo camino de escritura que las herramientas. <paramref name="baseline"/> es lo que el
    /// usuario vio en la vista previa: si la nota cambió desde entonces (también por un guardado pendiente de su
    /// pestaña), no se escribe nada y se lanza <see cref="ExternalModificationException"/>.</summary>
    public NoteFile AppendToNote(string path, string text, NoteDiskStamp baseline)
    {
        if (!_host.PrepareNoteChange(path)) throw new ExternalModificationException(path, VaultExternalChangeKind.Modified);
        var fresh = _vault.ReadFromDisk(path) ?? throw new FileNotFoundException(null, path);
        if (!string.Equals(fresh.Stamp.ContentHash, baseline.ContentHash, StringComparison.Ordinal))
            throw new ExternalModificationException(path, VaultExternalChangeKind.Modified);
        var (note, _) = CommitNoteChange(path, fresh.Note, fresh.Content, fresh.Stamp,
            KeepLineEndings(fresh.Content, AppendAtEnd(fresh.Content, text.Trim('\n'))));
        _host.VaultChanged();
        return note;
    }

    private void PrepareNoteChange(string path)
    {
        if (!_host.PrepareNoteChange(path))
            throw new AiToolException(AiToolErrorKind.Conflict,
                "The note is open in the editor with a pending conflict or unsaved changes. Ask the user to resolve it first.");
    }

    private static string ApplyEdits(AiToolCall call, string content, List<JsonElement> edits)
    {
        var result = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        for (var i = 0; i < edits.Count; i++)
        {
            var edit = call.Nested(edits[i]);
            var find = edit.GetText("find");
            if (string.IsNullOrEmpty(find)) throw AiToolCall.Invalid($"Edit {i + 1}: 'find' is required.");
            find = find.Replace("\r\n", "\n", StringComparison.Ordinal);
            var replace = (edit.GetText("replace") ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
            var occurrences = CountOccurrences(result, find);
            if (occurrences == 0)
                throw AiToolCall.Invalid($"Edit {i + 1}: the text to find was not found. Read the note again and copy the text exactly.");
            if (occurrences > 1 && edit.GetFlag("all") != true)
                throw AiToolCall.Invalid($"Edit {i + 1}: the text to find appears {occurrences} times. Include more context or set all=true.");
            result = result.Replace(find, replace, StringComparison.Ordinal);
        }
        return result;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    internal static string AppendAtEnd(string content, string text) =>
        content.TrimEnd() + (content.Trim().Length == 0 ? string.Empty : "\n\n") + text + "\n";

    /// <summary>Al principio del cuerpo: tras el frontmatter y tras el título si es lo primero que hay.</summary>
    private static string InsertAtStart(string content, string text)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var body = NoteTagHelper.StripFrontMatter(normalized);
        var bodyStart = normalized.Length - body.Length;
        var insertAt = bodyStart;
        if (body.StartsWith("# ", StringComparison.Ordinal))
        {
            var lineEnd = body.IndexOf('\n');
            insertAt = lineEnd < 0 ? normalized.Length : bodyStart + lineEnd + 1;
        }
        var before = normalized[..insertAt].TrimEnd('\n');
        var after = normalized[insertAt..].TrimStart('\n');
        return (before.Length > 0 ? before + "\n\n" : string.Empty) + text + "\n" + (after.Length > 0 ? "\n" + after : string.Empty);
    }

    private static string ApplyMetadata(AiToolCall call, string content)
    {
        var result = content;
        var tags = call.GetTextList("tags") ?? NoteTagHelper.ExtractTags(content);
        if (call.GetTextList("add_tags") is { } add) tags = tags.Concat(add).ToList();
        if (call.GetTextList("remove_tags") is { } remove)
            tags = tags.Where(t => !remove.Contains(t.TrimStart('#'), StringComparer.OrdinalIgnoreCase)).ToList();
        if (call.Has("tags") || call.Has("add_tags") || call.Has("remove_tags"))
            result = NoteTagHelper.SetTags(result, tags.Select(t => t.TrimStart('#')).ToList());

        if (call.Get("properties") is { } properties)
        {
            if (properties.ValueKind != JsonValueKind.Object) throw AiToolCall.Invalid("'properties' must be an object.");
            foreach (var property in properties.EnumerateObject())
            {
                if (string.Equals(property.Name, "tags", StringComparison.OrdinalIgnoreCase))
                    throw AiToolCall.Invalid("Use 'tags', 'add_tags' or 'remove_tags' to change tags.");
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => property.Value.GetString(),
                    _ => property.Value.GetRawText()
                };
                result = NotePropertyHelper.Set(result, property.Name, value);
            }
        }
        return result;
    }

    // ===================== Localizar notas =====================

    /// <summary>Resuelve el argumento <c>path</c> a una nota existente del espacio: la ruta tal cual, con
    /// <c>.md</c> añadido, o —si no hay barras— el título o nombre exacto de una única nota del espacio.</summary>
    private (AiWorkspace Workspace, string Path) FindNote(AiToolCall call)
    {
        var workspace = Workspace(call);
        var requested = call.RequireText("path").Trim();
        var full = ResolvePath(workspace, requested);
        if (!full.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && File.Exists(full + ".md")) full += ".md";
        if (File.Exists(full) && full.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return (workspace, full);

        if (!requested.Contains('/', StringComparison.Ordinal))
        {
            var name = requested.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? requested[..^3] : requested;
            var matches = _vault.GetAllNotesRecursive(workspace.RootPath)
                .Where(n => string.Equals(n.Title, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(n.FileNameWithoutExtension, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 1) return (workspace, matches[0].FullPath);
            if (matches.Count > 1)
                throw AiToolCall.Invalid($"Several notes are called \"{name}\": "
                    + string.Join(", ", matches.Take(10).Select(m => RelativePath(workspace, m.FullPath))) + ". Use the path.");
        }
        throw NoteNotFound(requested);
    }

    private static AiToolException NoteNotFound(string? requested) => new(AiToolErrorKind.NotFound,
        $"There is no note \"{requested}\" in that workspace. Use list_notes or search_notes to find it.");

    private List<(NoteFile Note, string Content)> AccessibleNotesWithContent(AiAccessScope scope) =>
        Workspaces(scope)
            .Where(w => w.Session is null || _sessions.IsUnlocked(w.Session))
            .SelectMany(w => _vault.GetAllNotesRecursiveWithContent(w.RootPath))
            .ToList();

    /// <summary>Si sale del ámbito un contenido de una sesión protegida (moverlo o copiarlo fuera de ella), su
    /// versión descifrada queda en otro sitio: lo decide el usuario, no el modelo.</summary>
    private async Task RequireApprovalToLeaveProtectedSessionAsync(AiToolCall call, string source, string destinationFolder, string label)
    {
        if (!_vault.IsPathInEncryptedSession(source)) return;
        var sourceSession = _sessions.List().FirstOrDefault(s => VaultService.IsWithinFolder(source, s.RootPath));
        if (sourceSession is not null && VaultService.IsWithinFolder(destinationFolder, sourceSession.RootPath)) return;
        await RequireApprovalAsync(call, LocalizedTextFormatter.Format("Assistant_Approval_LeaveProtectedSession", sourceSession?.Name ?? string.Empty),
            [label]);
    }

    // ===================== Representación =====================

    private static string WorkspaceKey(AiWorkspace workspace) => workspace.Id;

    private static object NoteSummary(AiWorkspace workspace, NoteFile note) => new
    {
        Workspace = WorkspaceKey(workspace),
        Path = RelativePath(workspace, note.FullPath),
        note.Title,
        Tags = note.Tags.Count > 0 ? note.Tags : null,
        Modified = FormatInstant(note.ModifiedUtc),
        Words = note.WordCount
    };

    private object? NoteLocation(AiAccessScope scope, NoteFile note) =>
        WorkspaceOf(scope, note.FullPath) is { } workspace
            ? new { Workspace = WorkspaceKey(workspace), Path = RelativePath(workspace, note.FullPath), note.Title }
            : null;
}
