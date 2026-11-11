using System;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>La nota diaria (<see cref="DailyNoteService"/>) y las plantillas de notas
/// (<see cref="NoteTemplateService"/>). La nota de cada día se busca y se crea con la misma definición que el
/// botón de Documentos —nombre y plantilla configurados incluidos—, y lo que se le añade pasa por el mismo
/// camino de escritura comprobada que <c>append_to_note</c>. Las dos cosas son del vault, no de una sesión: la
/// nota diaria vive en su raíz y las plantillas en <c>.enki/templates</c>.</summary>
public sealed partial class AiVaultToolbox
{
    private void RegisterDailyNoteAndTemplateTools()
    {
        Register("get_daily_note", AiToolScope.Vault, AiToolEffect.Read,
            "Reads the daily note of a day (today by default): the one the app opens with its \"daily note\" button. "
            + "If it does not exist yet, 'exists' is false and nothing is created.",
            Schema(Opt("date", Text("Day as 2026-10-24 (default today)."))),
            call =>
            {
                var date = Day(call, "date") ?? DailyNoteService.Today;
                var template = _dailyNotes.ResolveTemplate();
                if (_dailyNotes.Find(date, template) is not { } note)
                {
                    call.Target(AiToolTargetKind.Note, _dailyNotes.TitleFor(date, template));
                    return new { Date = FormatDay(date), Exists = false, Title = _dailyNotes.TitleFor(date, template), Template = template?.Name };
                }
                return new { Date = FormatDay(date), Exists = true, Template = template?.Name, Note = NoteReading(call, VaultWorkspace, note.FullPath) };
            });

        Register("append_to_daily_note", AiToolScope.Vault, AiToolEffect.Change,
            "Adds Markdown text at the end of the daily note of a day (today by default). If that day's note does not exist yet it is "
            + "created first, with the user's daily note template when one is configured.",
            Schema(Req("text", Text("Markdown to add.")), Opt("date", Text("Day as 2026-10-24 (default today)."))),
            call =>
            {
                var text = call.RequireText("text").Trim('\n');
                var date = Day(call, "date") ?? DailyNoteService.Today;
                var opening = _dailyNotes.OpenOrCreate(date, _vault.VaultPath);
                var path = opening.Note.FullPath;
                call.Target(AiToolTargetKind.Note, opening.Note.Title, path);
                var note = ChangeNote(call, path, content => AppendAtEnd(content, text), requireCompleteRead: false);
                // Una nota que nació en esta misma llamada se deshace entera, no solo lo añadido. Con la revisión de
                // cambios, lo añadido queda pendiente y la forma de deshacerlo la pone la aprobación.
                if (opening.Created && !call.IsQueued) call.Undoable(UndoCreatedNote(path));
                return new { Date = FormatDay(date), Created = opening.Created, Note = NoteSummary(VaultWorkspace, note) };
            });

        Register("list_note_templates", AiToolScope.Vault, AiToolEffect.Read,
            "Lists the note templates of the vault (Markdown files with variables like {{date}} or {{title}}), and which one is "
            + "configured for the daily note.",
            Schema(), call =>
            {
                var daily = _dailyNotes.ResolveTemplate();
                return new
                {
                    Templates = _templates.LoadTemplates().Select(t => new
                    {
                        Template = t.RelativePath,
                        t.Name,
                        Description = t.Description.Length > 0 ? t.Description : null,
                        t.TitlePattern,
                        DailyNote = daily is not null && string.Equals(daily.RelativePath, t.RelativePath, VaultService.PathComparison) ? true : (bool?)null
                    }).ToList()
                };
            });

        Register("create_note_from_template", AiToolScope.Vault, AiToolEffect.Change,
            "Creates a new note from a note template, with its variables filled in. The template may set the note's title; otherwise "
            + "'title' is used, or the template's name. If a note with that name already exists, a numeric suffix is added.",
            Schema(Req("template", Text("Template path or name, from list_note_templates.")),
                Opt("title", Text("Title for the new note (also the value of {{title}} in the template).")),
                WorkspaceParam(), Opt("folder", Text("Destination folder relative to the workspace (omit for the root)."))),
            call =>
            {
                var template = Template(call);
                var workspace = Workspace(call);
                var folder = ExistingFolder(workspace, call.GetText("folder"));
                var title = call.GetText("title")?.Trim() is { Length: > 0 } requested ? requested : template.Name;
                var draft = NoteTemplateService.Prepare(template, title);
                call.Target(AiToolTargetKind.Note, draft.Title);
                if (call.Context.Review is { } review)
                {
                    StageCreation(call, review, workspace, folder, draft.Title, draft.Content);
                    return new { Workspace = WorkspaceKey(workspace), Folder = RelativePath(workspace, folder), draft.Title };
                }

                var note = _vault.CreateNote(draft.Title, folder, draft.Content);
                call.Target(AiToolTargetKind.Note, note.Title, note.FullPath);
                if (NoteDiskStamps.TryRead(note.FullPath) is { } stamp) call.Context.CompleteReads[note.FullPath] = stamp;
                call.Undoable(UndoCreatedNote(note.FullPath));
                return NoteSummary(workspace, note);
            });
    }

    /// <summary>Resuelve el argumento <c>template</c>: su ruta dentro de la carpeta de plantillas, o el nombre
    /// (o el nombre del archivo) de una única plantilla.</summary>
    private NoteTemplate Template(AiToolCall call)
    {
        var requested = call.RequireText("template").Trim().Replace('\\', '/');
        var templates = _templates.LoadTemplates();
        var matches = templates.Where(t => string.Equals(t.RelativePath, requested, VaultService.PathComparison)).ToList();
        if (matches.Count == 0)
            matches = templates.Where(t => string.Equals(t.Name, requested, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Id, requested, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1)
            throw AiToolCall.Invalid($"Several templates are called \"{requested}\". Use the path: "
                + string.Join(", ", matches.Select(t => t.RelativePath)) + ".");
        return matches.FirstOrDefault()
            ?? throw new AiToolException(AiToolErrorKind.NotFound, $"There is no template \"{requested}\". Use list_note_templates.");
    }
}
