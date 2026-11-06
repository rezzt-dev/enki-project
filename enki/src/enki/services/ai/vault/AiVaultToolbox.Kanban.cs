using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using enki.Models;

namespace enki.Services;

/// <summary>Tableros y tarjetas de Kanban, siempre a través de <see cref="KanbanService"/>: crear, editar,
/// mover (con las mismas transiciones de rol que el arrastre: completar al llegar a "Hecho", subtareas),
/// archivar y enviar a la papelera.</summary>
public sealed partial class AiVaultToolbox
{
    private const int MaxListedCards = 300;

    private void RegisterKanbanTools()
    {
        Register("list_kanban", AiToolScope.Vault, AiToolEffect.Read,
            "Lists Kanban boards with their columns (id, title and role: backlog, in_progress, done or none) and the cards in each column, "
            + "with priority, dates, tags, subtasks and linked note. Archived cards are hidden unless include_archived is true.",
            Schema(Opt("board", Text("Board name or id (omit for every board).")),
                Opt("include_archived", Flag("Include archived cards (default false).")),
                Opt("include_descriptions", Flag("Include full card descriptions (default false: only their first line)."))),
            call =>
            {
                List<KanbanBoard> boards = call.Has("board") ? [Board(call)] : _kanban.Boards.ToList();
                var archived = call.GetFlag("include_archived") ?? false;
                var descriptions = call.GetFlag("include_descriptions") ?? false;
                var remaining = MaxListedCards;
                var truncated = false;
                // Se materializa aquí, no al serializar: el tope de tarjetas se reparte por orden.
                var result = boards.Select(board => new
                {
                    board.Id,
                    board.Name,
                    Default = board.Id == _kanban.DefaultBoard.Id,
                    Columns = board.Columns.Select(column =>
                    {
                        var visible = column.Cards.Where(card => archived || !card.IsArchived).ToList();
                        var taken = visible.Take(Math.Max(0, remaining)).ToList();
                        remaining -= taken.Count;
                        truncated |= taken.Count < visible.Count;
                        return new
                        {
                            column.Id,
                            column.Title,
                            Role = RoleName(column.Role),
                            Cards = taken.Select(card => CardSummary(call.Context.Scope, card, descriptions)).ToList()
                        };
                    }).ToList()
                }).ToList();
                return new { Boards = result, Truncated = truncated };
            });

        Register("create_kanban_board", AiToolScope.Vault, AiToolEffect.Change,
            "Creates an empty Kanban board with the three base columns (backlog, in progress, done).",
            Schema(Req("name", Text("Board name, unique."))),
            call =>
            {
                var name = call.RequireText("name").Trim();
                call.Target(AiToolTargetKind.KanbanBoard, name);
                var board = _kanban.CreateBoard(name);
                return new { board.Id, board.Name, Columns = board.Columns.Select(c => new { c.Id, c.Title, Role = RoleName(c.Role) }) };
            });

        Register("rename_kanban_board", AiToolScope.Vault, AiToolEffect.Change,
            "Renames a Kanban board.",
            Schema(Req("board", Text("Board name or id.")), Req("new_name", Text("New name, unique."))),
            call =>
            {
                var board = Board(call);
                call.Target(AiToolTargetKind.KanbanBoard, board.Name, board.Id);
                _kanban.RenameBoard(board.Id, call.RequireText("new_name"));
                call.Target(AiToolTargetKind.KanbanBoard, board.Name, board.Id);
                return new { board.Id, board.Name };
            });

        Register("create_kanban_card", AiToolScope.Vault, AiToolEffect.Change,
            "Creates a Kanban card. 'column' accepts a column id, title or role (backlog, in_progress, done); it defaults to the "
            + "backlog column of the board, which defaults to the default board.",
            Schema(Req("title", Text("Card title."))
                , Opt("board", Text("Board name or id.")), Opt("column", Text("Column id, title or role.")),
                Opt("description", Text("Markdown description.")), Opt("priority", Choice("Priority (default medium).", "low", "medium", "high")),
                Opt("start", Text("Optional start. " + DateDescription)), Opt("due", Text("Optional due date. " + DateDescription)),
                Opt("duration_minutes", Number("Time slot length in the calendar, from the due time.")),
                Opt("tags", TextList("Tags.")), Opt("subtasks", TextList("Subtask titles.")),
                Opt("linked_note", LinkedNoteSchema())),
            call =>
            {
                var title = call.RequireText("title").Trim();
                call.Target(AiToolTargetKind.KanbanCard, title);
                var board = Board(call);
                var column = Column(board, call.GetText("column"));
                var card = new KanbanCard
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Title = title,
                    Description = call.GetText("description") ?? string.Empty,
                    Priority = Priority(call) ?? KanbanPriority.Medium,
                    StartDateUtc = Date(call, "start"),
                    DueDateUtc = Date(call, "due"),
                    DurationMinutes = call.GetInteger("duration_minutes", 0, 24 * 60),
                    Tags = NormalizedTags(call.GetTextList("tags")),
                    Subtasks = (call.GetTextList("subtasks") ?? [])
                        .Select(t => new KanbanSubtask { Id = Guid.NewGuid().ToString("N"), Title = t, IsDone = column.Role == CoreColumnRole.Done })
                        .ToList(),
                    LinkedNotePath = LinkedNote(call),
                    CompletedAtUtc = column.Role == CoreColumnRole.Done ? DateTime.UtcNow : null
                };
                ValidateCardDates(card);
                _kanban.CreateCard(board.Id, column.Id, card);
                call.Target(AiToolTargetKind.KanbanCard, card.Title, card.Id);
                return CardSummary(call.Context.Scope, card, includeDescription: true);
            });

        Register("update_kanban_card", AiToolScope.Vault, AiToolEffect.Change,
            "Edits a Kanban card. Only the fields you pass change; pass null to clear start, due, duration_minutes or linked_note. "
            + "'tags' and 'subtasks' replace the whole list (keep a subtask's id to preserve it). To complete a card, move it to the done column.",
            Schema(Req("card_id", Text("Card id.")), Opt("title", Text("New title.")), Opt("description", Text("New Markdown description.")),
                Opt("priority", Choice("Priority.", "low", "medium", "high")),
                Opt("start", NullableText(DateDescription)), Opt("due", NullableText(DateDescription)),
                Opt("duration_minutes", new System.Text.Json.Nodes.JsonObject { ["type"] = new System.Text.Json.Nodes.JsonArray("integer", "null") }),
                Opt("tags", TextList("Full list of tags.")),
                Opt("subtasks", ObjectList("Full list of subtasks.", Schema(Opt("id", Text("Existing subtask id.")),
                    Req("title", Text("Subtask title.")), Opt("done", Flag("Completed."))))),
                Opt("linked_note", LinkedNoteSchema())),
            call =>
            {
                var location = Card(call);
                var card = location.Card;
                var draft = new KanbanCard
                {
                    Id = card.Id,
                    Title = call.GetText("title")?.Trim() is { Length: > 0 } title ? title : card.Title,
                    Description = call.Has("description") ? call.GetText("description") ?? string.Empty : card.Description,
                    Priority = Priority(call) ?? card.Priority,
                    StartDateUtc = call.Has("start") ? Date(call, "start") : card.StartDateUtc,
                    DueDateUtc = call.Has("due") ? Date(call, "due") : card.DueDateUtc,
                    DurationMinutes = call.Has("duration_minutes") ? call.GetInteger("duration_minutes", 0, 24 * 60) : card.DurationMinutes,
                    Tags = call.Has("tags") ? NormalizedTags(call.GetTextList("tags")) : [.. card.Tags],
                    Subtasks = call.Has("subtasks") ? Subtasks(call, card) : card.Subtasks.Select(CopySubtask).ToList(),
                    LinkedNotePath = call.Has("linked_note") ? LinkedNote(call) : card.LinkedNotePath
                };
                ValidateCardDates(draft);
                _kanban.SaveCardEdits(card.Id, location.Column.Id, draft);
                call.Target(AiToolTargetKind.KanbanCard, card.Title, card.Id);
                return CardSummary(call.Context.Scope, card, includeDescription: true);
            });

        Register("move_kanban_card", AiToolScope.Vault, AiToolEffect.Change,
            "Moves a card to another column (or board). Moving to the done column completes it and checks its subtasks; "
            + "moving it back to backlog unchecks them, as in the app.",
            Schema(Req("card_id", Text("Card id.")), Req("column", Text("Destination column id, title or role.")),
                Opt("board", Text("Destination board name or id (omit to stay on the same board).")),
                Opt("before_card_id", Text("Place it right before this card (omit to put it at the end)."))),
            call =>
            {
                var location = Card(call);
                var board = call.Has("board") ? Board(call) : location.Board;
                var column = Column(board, call.RequireText("column"));
                _kanban.MoveCardToBoard(location.Card.Id, board.Id, column.Id, call.GetText("before_card_id"));
                return CardSummary(call.Context.Scope, location.Card, includeDescription: false);
            });

        Register("archive_kanban_card", AiToolScope.Vault, AiToolEffect.Change,
            "Archives a card of the done column (it disappears from the board but still counts as completed), or unarchives it.",
            Schema(Req("card_id", Text("Card id.")), Opt("archived", Flag("true to archive (default), false to unarchive."))),
            call =>
            {
                var location = Card(call);
                var archive = call.GetFlag("archived") ?? true;
                if (archive && location.Column.Role != CoreColumnRole.Done)
                    throw AiToolCall.Invalid("Only cards in the done column can be archived. Move it there first.");
                _kanban.SetCardArchived(location.Card.Id, archive);
                return CardSummary(call.Context.Scope, location.Card, includeDescription: false);
            });

        Register("delete_kanban_card", AiToolScope.Vault, AiToolEffect.Change,
            "Moves a Kanban card to the trash, where it can be restored for 30 days.",
            Schema(Req("card_id", Text("Card id."))),
            call =>
            {
                var location = Card(call);
                _kanban.TrashCard(location.Card.Id);
                call.Target(AiToolTargetKind.KanbanCard, location.Card.Title);
                return new { Trashed = true, location.Card.Title };
            });
    }

    private KanbanBoard Board(AiToolCall call)
    {
        if (call.GetText("board")?.Trim() is not { Length: > 0 } requested) return _kanban.DefaultBoard;
        return _kanban.Boards.FirstOrDefault(b => b.Id == requested)
            ?? _kanban.Boards.FirstOrDefault(b => string.Equals(b.Name, requested, StringComparison.OrdinalIgnoreCase))
            ?? throw new AiToolException(AiToolErrorKind.NotFound, $"There is no Kanban board \"{requested}\". Use list_kanban.");
    }

    private static KanbanColumn Column(KanbanBoard board, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return board.Columns.FirstOrDefault(c => c.Role == CoreColumnRole.Backlog) ?? board.Columns.FirstOrDefault()
                ?? throw new AiToolException(AiToolErrorKind.NotFound, $"The board \"{board.Name}\" has no columns.");
        requested = requested.Trim();
        CoreColumnRole? role = requested.ToLowerInvariant().Replace(' ', '_') switch
        {
            "backlog" or "todo" or "to_do" => CoreColumnRole.Backlog,
            "in_progress" or "inprogress" or "doing" => CoreColumnRole.InProgress,
            "done" or "completed" => CoreColumnRole.Done,
            _ => null
        };
        return board.Columns.FirstOrDefault(c => c.Id == requested)
            ?? (role is { } wanted ? board.Columns.FirstOrDefault(c => c.Role == wanted) : null)
            ?? board.Columns.FirstOrDefault(c => string.Equals(c.Title, requested, StringComparison.OrdinalIgnoreCase))
            ?? throw new AiToolException(AiToolErrorKind.NotFound,
                $"The board \"{board.Name}\" has no column \"{requested}\". Columns: " + string.Join(", ", board.Columns.Select(c => c.Title)) + ".");
    }

    private KanbanCardLocation Card(AiToolCall call)
    {
        var id = call.RequireText("card_id").Trim();
        var location = _kanban.FindCardLocation(id)
            ?? throw new AiToolException(AiToolErrorKind.NotFound, $"There is no Kanban card with id \"{id}\". Use list_kanban.");
        call.Target(AiToolTargetKind.KanbanCard, location.Card.Title, location.Card.Id);
        return location;
    }

    private static KanbanPriority? Priority(AiToolCall call) => call.GetText("priority")?.Trim().ToLowerInvariant() switch
    {
        null => null,
        "low" => KanbanPriority.Low,
        "medium" or "normal" => KanbanPriority.Medium,
        "high" => KanbanPriority.High,
        var other => throw AiToolCall.Invalid($"Unknown priority \"{other}\": use low, medium or high.")
    };

    private static List<KanbanSubtask> Subtasks(AiToolCall call, KanbanCard card) =>
        (call.GetObjects("subtasks") ?? []).Select(element =>
        {
            var item = call.Nested(element);
            var id = item.GetText("id");
            var existing = card.Subtasks.FirstOrDefault(s => s.Id == id);
            return new KanbanSubtask
            {
                Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
                Title = item.GetText("title")?.Trim() is { Length: > 0 } title ? title : existing?.Title ?? throw AiToolCall.Invalid("Each subtask needs a title."),
                IsDone = item.GetFlag("done") ?? existing?.IsDone ?? false
            };
        }).ToList();

    private static KanbanSubtask CopySubtask(KanbanSubtask subtask) => new() { Id = subtask.Id, Title = subtask.Title, IsDone = subtask.IsDone };

    private static List<string> NormalizedTags(IEnumerable<string>? tags) =>
        (tags ?? []).Select(t => t.Trim().TrimStart('#').ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList();

    private static void ValidateCardDates(KanbanCard card)
    {
        if (card.StartDateUtc is { } start && card.DueDateUtc is { } due && start > due)
            throw AiToolCall.Invalid("'start' must not be after 'due'.");
    }

    private static System.Text.Json.Nodes.JsonObject LinkedNoteSchema()
    {
        var schema = Schema(WorkspaceParam("Workspace of the note (omit for this chat's own)."), Req("path", Text("Note path or exact title.")));
        schema["type"] = new System.Text.Json.Nodes.JsonArray("object", "null");
        schema["description"] = "Note linked to the card, or null to unlink it.";
        return schema;
    }

    /// <summary>La nota a vincular, que tiene que existir y estar en el ámbito. <see langword="null"/> si el
    /// argumento es nulo (desvincular).</summary>
    private string? LinkedNote(AiToolCall call)
    {
        if (call.Get("linked_note") is not { } element) return null;
        if (element.ValueKind != JsonValueKind.Object) throw AiToolCall.Invalid("'linked_note' must be an object with 'path'.");
        return FindNote(call.Nested(element)).Path;
    }

    private static string? RoleName(CoreColumnRole role) => role switch
    {
        CoreColumnRole.Backlog => "backlog",
        CoreColumnRole.InProgress => "in_progress",
        CoreColumnRole.Done => "done",
        _ => null
    };

    private object CardSummary(AiAccessScope scope, KanbanCard card, bool includeDescription)
    {
        var location = _kanban.FindCardLocation(card.Id);
        return new
        {
            card.Id,
            card.Title,
            Board = location?.Board.Name,
            Column = location?.Column.Title,
            Description = card.Description.Length == 0 ? null
                : includeDescription ? Truncate(card.Description, 4_000) : Truncate(card.Description.Split('\n')[0], 200),
            Priority = card.Priority.ToString().ToLowerInvariant(),
            Start = FormatDate(card.StartDateUtc),
            Due = FormatDate(card.DueDateUtc),
            card.DurationMinutes,
            Tags = card.Tags.Count > 0 ? card.Tags : null,
            Subtasks = card.Subtasks.Count > 0 ? card.Subtasks.Select(s => new { s.Id, s.Title, Done = s.IsDone }) : null,
            LinkedNote = card.LinkedNotePath is { } path ? LinkedNoteSummary(scope, path) : null,
            Archived = card.IsArchived ? true : (bool?)null,
            Completed = card.CompletedAtUtc is { } completed ? FormatInstant(completed) : null
        };
    }

    private sealed record LinkedNoteInfo(string? Workspace, string? Path, bool? Missing, bool? OutsideScope);

    /// <summary>Una nota vinculada fuera del ámbito del chat no se describe: ni su ruta ni su título.</summary>
    private LinkedNoteInfo LinkedNoteSummary(AiAccessScope scope, string fullPath) =>
        WorkspaceOf(scope, fullPath) is { } workspace
            ? new LinkedNoteInfo(WorkspaceKey(workspace), RelativePath(workspace, fullPath),
                System.IO.File.Exists(fullPath) ? null : true, null)
            : new LinkedNoteInfo(null, null, null, OutsideScope: true);
}
