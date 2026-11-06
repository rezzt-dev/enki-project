using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using enki.Localization;
using enki.Models;

namespace enki.Services;

/// <summary>La Papelera: lo mismo que lista <c>TrashViewModel</c> —notas, archivos, carpetas, sesiones,
/// tarjetas de Kanban, listas y tareas de Polar y recordatorios—, restaurar cualquiera de ellos y borrarlos
/// para siempre. Lo que venga de una sesión protegida con contraseña no se enseña ni se toca (ver
/// <see cref="AiAccessScope"/>). Borrar para siempre no tiene vuelta atrás, así que pide confirmación en el
/// chat cada vez, con la lista exacta de lo que se va a borrar.</summary>
public sealed partial class AiVaultToolbox
{
    private static readonly string[] TrashTypes =
        ["note", "file", "folder", "session", "kanban_card", "polar_list", "polar_task", "reminder"];

    /// <summary>Un elemento de la papelera: su tipo, el id con el que el modelo lo nombra, lo que se le
    /// enseña al usuario y cómo restaurarlo o borrarlo de verdad.</summary>
    private sealed record TrashEntry(string Type, string Id, string Label, DateTime? DeletedUtc, Action Restore, Action Purge);

    private void RegisterTrashTools()
    {
        Register("list_trash", AiToolScope.Vault, AiToolEffect.Read,
            "Lists what is in the trash: notes, files, folders, sessions, Kanban cards, Polar lists and tasks, and reminders. "
            + "Items stay for 30 days. Use the returned type and id to restore or permanently delete them.",
            Schema(Opt("type", Choice("Only items of this type.", TrashTypes))),
            call =>
            {
                var type = call.GetText("type");
                return new
                {
                    Items = TrashEntries(call.Context.Scope)
                        .Where(e => type is null || e.Type == type)
                        .Select(e => new { e.Type, e.Id, Name = e.Label, Deleted = e.DeletedUtc is { } deleted ? FormatInstant(deleted) : null })
                        .ToList()
                };
            });

        Register("restore_from_trash", AiToolScope.Vault, AiToolEffect.Change,
            "Restores an item from the trash to where it was (or to the vault root if that place no longer exists).",
            Schema(Req("type", Choice("Item type.", TrashTypes)), Req("id", Text("Item id from list_trash."))),
            call =>
            {
                var entry = FindTrashEntry(call);
                entry.Restore();
                if (entry.Type == "session") _host.SessionsChanged();
                return new { Restored = true, entry.Type, Name = entry.Label };
            });

        RegisterAsync("delete_from_trash", AiToolScope.Vault, AiToolEffect.Irreversible,
            "Permanently deletes items from the trash. This cannot be undone: the user is asked to confirm in the app and may refuse.",
            Schema(Req("items", ObjectList("Items to delete.", Schema(Req("type", Choice("Item type.", TrashTypes)), Req("id", Text("Item id from list_trash.")))))),
            async call =>
            {
                var requested = call.GetObjects("items") is { Count: > 0 } items ? items : throw AiToolCall.Invalid("'items' must not be empty.");
                var all = TrashEntries(call.Context.Scope);
                var entries = requested.Select(element =>
                {
                    var item = call.Nested(element);
                    var (type, id) = (item.RequireText("type"), item.RequireText("id"));
                    return all.FirstOrDefault(e => e.Type == type && e.Id == id)
                        ?? throw new AiToolException(AiToolErrorKind.NotFound, $"There is no {type} \"{id}\" in the trash. Use list_trash.");
                }).Distinct().ToList();
                return await PurgeAsync(call, entries);
            });

        RegisterAsync("empty_trash", AiToolScope.Vault, AiToolEffect.Irreversible,
            "Permanently deletes everything in the trash, or only the given types. Cannot be undone: the user is asked to confirm.",
            Schema(Opt("types", new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "array",
                ["items"] = Choice("Item type.", TrashTypes),
                ["description"] = "Only these types (omit for everything)."
            })),
            async call =>
            {
                var types = call.GetTextList("types");
                var entries = TrashEntries(call.Context.Scope).Where(e => types is null || types.Contains(e.Type)).ToList();
                if (entries.Count == 0) return new { Deleted = 0 };
                return await PurgeAsync(call, entries);
            });
    }

    private async Task<object?> PurgeAsync(AiToolCall call, IReadOnlyList<TrashEntry> entries)
    {
        call.Target(AiToolTargetKind.Trash, entries.Count == 1 ? entries[0].Label
            : LocalizedTextFormatter.Resolve(LocalizedTextFormatter.PluralFormat("Assistant_Approval_ItemCount", entries.Count, entries.Count)));
        await RequireApprovalAsync(call, LocalizedTextFormatter.PluralFormat("Assistant_Approval_DeleteForever", entries.Count, entries.Count),
            entries.Select(e => e.Label).ToList());

        var deleted = 0;
        foreach (var entry in entries)
        {
            entry.Purge();
            deleted++;
        }
        if (entries.Any(e => e.Type == "session")) _host.SessionsChanged();
        return new { Deleted = deleted };
    }

    private TrashEntry FindTrashEntry(AiToolCall call)
    {
        var (type, id) = (call.RequireText("type"), call.RequireText("id"));
        var entry = TrashEntries(call.Context.Scope).FirstOrDefault(e => e.Type == type && e.Id == id)
            ?? throw new AiToolException(AiToolErrorKind.NotFound, $"There is no {type} \"{id}\" in the trash. Use list_trash.");
        call.Target(AiToolTargetKind.Trash, entry.Label);
        return entry;
    }

    /// <summary>Todo lo que hay en la papelera y el ámbito deja ver, leído de los mismos servicios que la
    /// Papelera de la app. Las notas, archivos y carpetas se nombran por su nombre dentro de la papelera,
    /// que es único; el resto, por su id.</summary>
    private List<TrashEntry> TrashEntries(AiAccessScope scope)
    {
        var entries = new List<TrashEntry>();
        bool Hidden(string trashPath) => _vault.IsTrashedItemFromEncryptedSession(trashPath);

        foreach (var note in _vault.GetTrashedNotes().Where(n => !Hidden(n.FullPath)))
            entries.Add(new TrashEntry("note", note.FileName, note.Title, File.GetLastWriteTimeUtc(note.FullPath),
                () => _vault.RestoreNote(note), () => _vault.PermanentlyDelete(note)));
        foreach (var file in _vault.GetTrashedFiles().Where(f => !Hidden(f.FullPath)))
            entries.Add(new TrashEntry("file", file.FileName, file.FileName, file.ModifiedUtc,
                () => _vault.RestoreFile(file), () => _vault.PermanentlyDelete(file)));
        foreach (var folder in _vault.GetTrashedFolders().Where(f => !Hidden(f.FullPath)))
            entries.Add(new TrashEntry("folder", folder.Name, folder.Name, Directory.GetLastWriteTimeUtc(folder.FullPath),
                () => _vault.RestoreFolder(folder), () => _vault.PermanentlyDeleteFolder(folder)));
        foreach (var session in _sessions.ListTrashedSessions().Where(s => !s.IsEncrypted))
            entries.Add(new TrashEntry("session", session.Id, session.Name, session.ModifiedUtc,
                () =>
                {
                    if (!_sessions.RestoreTrashedSession(session)) throw new AiToolException(AiToolErrorKind.NotFound, "The session is no longer in the trash.");
                },
                () => SessionService.PermanentlyDeleteTrashedSession(session)));

        foreach (var trashed in _kanban.AllTrashedCards.ToList())
            entries.Add(new TrashEntry("kanban_card", trashed.Card.Id, trashed.Card.Title, trashed.DeletedUtc,
                () => _kanban.RestoreTrashedCard(trashed), () => _kanban.PermanentlyDeleteTrashedCard(trashed)));

        if (_polarAuth.IsLinked)
        {
            foreach (var list in _polar.Store.Lists.Where(l => l.IsDeleted).ToList())
                entries.Add(new TrashEntry("polar_list", list.Id, list.Title, list.DeletedAtUtc,
                    () => _polar.RestoreList(list), () => _polar.PurgeList(list)));
            foreach (var task in _polar.Store.Tasks.Where(t => t.IsDeleted).ToList())
                entries.Add(new TrashEntry("polar_task", task.Id, task.Title, task.DeletedAtUtc,
                    () => _polar.RestoreTask(task), () => _polar.PurgeTask(task)));
        }

        foreach (var reminder in _reminders.DeletedReminders.ToList())
            entries.Add(new TrashEntry("reminder", reminder.Id, reminder.Title, reminder.DeletedAtUtc,
                () => _reminders.Restore(reminder.Id),
                () => _reminders.PurgeDeleted(reminder.Id)));
        return entries;
    }
}
