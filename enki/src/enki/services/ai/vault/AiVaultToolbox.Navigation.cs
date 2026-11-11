using System;
using System.Linq;
using enki.Localization;
using enki.Models;

namespace enki.Services;

/// <summary>Lo que el asistente puede enseñar en la interfaz y lo que sabe explicar de ella: abrir una nota, un
/// tablero de Kanban o una sección, y consultar el manual integrado (<see cref="AiHelpService"/>). Ninguna de
/// estas herramientas cambia datos, así que no piden confirmación ni dependen de
/// <see cref="AiSettings.AllowVaultChanges"/>. Abrir una nota o un tablero sí respeta el ámbito del chat y su
/// permiso de consulta, porque confirma que el elemento existe; ir a una sección y leer el manual no tocan el
/// vault. La navegación la hace el shell a través de <see cref="AiVaultHost"/>, igual que el enlace de cada
/// fila de actividad del chat.</summary>
public sealed partial class AiVaultToolbox
{
    /// <summary>Secciones a las que se puede ir con <c>open_section</c>: la clave que entiende el shell y el
    /// recurso con su nombre visible. El asistente no figura: la conversación ya está a la vista.</summary>
    private static readonly (string Key, string LabelKey)[] SectionCatalog =
    [
        ("dashboard", "Nav_Dashboard"), ("notes", "Nav_Documents"), ("files", "Nav_Files"), ("sessions", "Nav_Sessions"),
        ("kanban", "Nav_Kanban"), ("calendar", "Nav_Calendar"), ("reminders", "Nav_RemindersLabel"),
        ("notifications", "Nav_NotificationsLabel"), ("habits", "Nav_Habits"), ("statistics", "Nav_Statistics"),
        ("tags", "Nav_TagManager"), ("skills", "Nav_Skills"), ("trash", "Nav_Trash"), ("settings", "Nav_SettingsLabel")
    ];

    public static readonly string[] Sections = SectionCatalog.Select(section => section.Key).ToArray();

    private const string SectionDisabled =
        "That section is turned off in this installation (Settings > Navigation), so it cannot be opened. "
        + "Tell the user where to turn it on if they want it; do not retry.";

    private void RegisterNavigationTools()
    {
        Register("open_note", AiToolScope.Content, AiToolEffect.Read,
            "Shows a note to the user by opening it in the Documents section of the app. Use it only when the user asks to open, "
            + "show or go to a note; to read a note yourself use read_note. It changes nothing.",
            Schema(WorkspaceParam(), Req("path", Text("Note path relative to the workspace, or its exact title."))),
            call =>
            {
                var (workspace, path) = FindNote(call);
                var note = _vault.GetNote(path) ?? throw NoteNotFound(call.GetText("path"));
                call.Target(AiToolTargetKind.Note, note.Title, path);
                if (!_host.OpenNote(note)) throw new AiToolException(AiToolErrorKind.Unavailable, SectionDisabled);
                return new { Opened = true, Workspace = WorkspaceKey(workspace), Path = RelativePath(workspace, path), note.Title };
            });

        Register("open_kanban_board", AiToolScope.Vault, AiToolEffect.Read,
            "Shows a Kanban board to the user by opening it in the Kanban section, optionally with the detail of one of its cards. "
            + "Use it only when the user asks to open, show or go to a board or card. It changes nothing.",
            Schema(Opt("board", Text("Board name or id (omit for the default board, or when card_id is given).")),
                Opt("card_id", Text("Card whose detail to open, from list_kanban."))),
            call =>
            {
                KanbanBoard board;
                string? cardId = null;
                if (call.Has("card_id"))
                {
                    var location = Card(call);
                    (board, cardId) = (location.Board, location.Card.Id);
                }
                else
                {
                    board = Board(call);
                    call.Target(AiToolTargetKind.KanbanBoard, board.Name, board.Id);
                }
                if (!_host.OpenKanbanBoard(board.Id, cardId)) throw new AiToolException(AiToolErrorKind.Unavailable, SectionDisabled);
                return new { Opened = true, Board = board.Name, CardId = cardId };
            });

        Register("open_section", AiToolScope.App, AiToolEffect.Read,
            "Takes the user to a section of the app. Use it only when the user asks to go to or open that section. It changes nothing "
            + "and reads nothing: to answer questions about the content use the other tools.",
            Schema(Req("section", Choice("Section to open.", Sections))),
            call =>
            {
                var requested = call.RequireText("section").Trim().ToLowerInvariant();
                if (SectionCatalog.FirstOrDefault(s => s.Key == requested) is not { Key: { } section } entry)
                    throw AiToolCall.Invalid("Unknown section. Use one of: " + string.Join(", ", Sections) + ".");
                call.Target(AiToolTargetKind.Section, LocalizationService.Instance[entry.LabelKey], section);
                if (!_host.OpenSection(section)) throw new AiToolException(AiToolErrorKind.Unavailable, SectionDisabled);
                return new { Opened = true, Section = section };
            });
    }

    private void RegisterHelpTools()
    {
        Register("search_help", AiToolScope.App, AiToolEffect.Read,
            "Searches Enki's built-in user manual: what each section does, how to do things step by step, settings and the user's "
            + "current keyboard shortcuts (they are remappable, so never quote a shortcut from memory). Use it for any \"how do I…?\" "
            + "or \"where is…?\" question about the app. It does not read the vault. Without 'query' it lists the manual's topics.",
            Schema(Opt("query", Text("What to look for, in the user's words (e.g. \"open the assistant\", \"atajo nota diaria\").")),
                Opt("limit", Number($"Maximum sections to return (default {AiHelpService.DefaultLimit}, max {AiHelpService.MaxLimit})."))),
            call =>
            {
                var query = call.GetText("query")?.Trim() ?? string.Empty;
                call.Target(AiToolTargetKind.Help, query);
                var limit = call.GetInteger("limit", 1, AiHelpService.MaxLimit) ?? AiHelpService.DefaultLimit;
                var result = AiHelpService.Search(query, limit);
                return new
                {
                    result.Language,
                    AppVersion = AiHelpService.AppVersion,
                    Sections = result.Sections.Select(s => new { s.Title, s.Content }),
                    // Sin coincidencias (o sin consulta) se devuelven los temas, para que el modelo afine.
                    Topics = result.Sections.Count == 0 ? result.Topics : null
                };
            });
    }
}
