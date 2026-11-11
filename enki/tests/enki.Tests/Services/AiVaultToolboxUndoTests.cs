using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>"Revertir esta respuesta": lo que el asistente cambió en un turno vuelve a como estaba, y lo que el
/// usuario tocó después no se pisa. Sobre servicios reales y un vault desechable.</summary>
public sealed class AiVaultToolboxUndoTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly VaultService _vault;
    private readonly SessionService _sessions;
    private readonly KanbanService _kanban;
    private readonly ReminderService _reminders;
    private readonly HabitService _habits;
    private readonly AiVaultToolbox _tools;

    /// <summary>Las acciones del "turno" en curso, en orden, como las guarda el mensaje del asistente.</summary>
    private readonly List<AiToolActivity> _turn = [];
    private readonly AiToolContext _context;
    private bool _noteBusy;

    public AiVaultToolboxUndoTests()
    {
        _settings = new SettingsService(_directory);
        _vault = new VaultService(_settings);
        _vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        _sessions = new SessionService(_vault);
        _kanban = new KanbanService(_directory);
        _reminders = new ReminderService(_directory);
        _habits = new HabitService(_vault);
        _tools = new AiVaultToolbox(_vault, _sessions, _kanban, new PolarTaskService(_directory),
            new PolarAuthService(_settings, baseUrl: "https://test.example", anonKey: "test"), _reminders, _settings,
            host: new AiVaultHost { PrepareNoteChange = _ => !_noteBusy }, habitService: _habits);
        _context = new AiToolContext
        {
            Scope = AiAccessScope.Vault,
            ActivityReported = activity =>
            {
                if (!_turn.Contains(activity)) _turn.Add(activity);
            }
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private async Task<JsonElement> Call(string name, object args)
    {
        var result = await _tools.ExecuteAsync(name, JsonSerializer.SerializeToElement(args), _context);
        Assert.False(result.IsError, result.Text);
        return JsonSerializer.Deserialize<JsonElement>(result.Text);
    }

    /// <summary>Lo que hay en el vault que el usuario ve: ruta relativa y contenido de cada nota.</summary>
    private Dictionary<string, string> Snapshot() => Directory
        .EnumerateFiles(_vault.VaultPath, "*.md", SearchOption.AllDirectories)
        .Where(path => !Path.GetRelativePath(_vault.VaultPath, path).StartsWith('.'))
        .ToDictionary(path => Path.GetRelativePath(_vault.VaultPath, path).Replace('\\', '/'), File.ReadAllText);

    private AiUndoState[] States => _turn.Where(a => a.Undo is not null).Select(a => a.Undo!.State).ToArray();

    // ===================== Notas =====================

    [Fact]
    public async Task Revert_CreateEditAndMoveNote_LeavesTheVaultIdentical()
    {
        Directory.CreateDirectory(Path.Combine(_vault.VaultPath, "Archivo"));
        var existing = _vault.CreateNote("Existente", initialContent: "# Existente\n\nOriginal\n");
        var before = Snapshot();

        await Call("create_note", new { title = "Nueva", content = "Borrador" });
        await Call("append_to_note", new { path = "Nueva", text = "Más" });
        await Call("edit_note", new { path = "Existente", edits = new[] { new { find = "Original", replace = "Cambiado" } } });
        await Call("rename_note", new { path = "Existente", new_title = "Renombrada" });
        await Call("move_note", new { path = "Renombrada", destination_folder = "Archivo" });
        Assert.NotEqual(before, Snapshot());
        Assert.True(File.Exists(Path.Combine(_vault.VaultPath, "Archivo", "Renombrada.md")));

        var report = _tools.RevertActivities(_turn);

        Assert.Equal(new AiRevertReport(5, 0, 0), report);
        Assert.All(States, state => Assert.Equal(AiUndoState.Reverted, state));
        Assert.Equal(before, Snapshot());
        Assert.Equal(existing.FullPath, _vault.GetNote(existing.FullPath)!.FullPath);
        // Lo creado no se destruye: queda en la papelera.
        Assert.Contains(_vault.GetTrashedNotes(), n => n.Title == "Nueva");
        // Y revertir tampoco pierde lo que había: queda en el historial de la nota.
        Assert.Contains(_vault.GetVersionHistory(existing), v => _vault.ReadVersionContent(existing, v.Id)!.Contains("Cambiado"));
    }

    [Fact]
    public async Task Revert_DoesNotOverwriteWhatTheUserEditedAfterwards()
    {
        var touched = _vault.CreateNote("Tocada", initialContent: "# Tocada\n\nUno\n");
        var untouched = _vault.CreateNote("Intacta", initialContent: "# Intacta\n\nUno\n");
        await Call("append_to_note", new { path = "Tocada", text = "Del asistente" });
        await Call("append_to_note", new { path = "Intacta", text = "Del asistente" });
        await Call("create_note", new { title = "Creada", content = "Del asistente" });
        var created = _vault.GetAllNotesRecursive(_vault.VaultPath).Single(n => n.Title == "Creada");

        // El usuario sigue trabajando en dos de ellas.
        File.AppendAllText(touched.FullPath, "\nDel usuario\n");
        File.AppendAllText(created.FullPath, "\nDel usuario\n");

        var report = _tools.RevertActivities(_turn);

        Assert.Equal(new AiRevertReport(1, 2, 0), report);
        Assert.Contains("Del usuario", File.ReadAllText(touched.FullPath));
        Assert.Contains("Del asistente", File.ReadAllText(touched.FullPath));
        Assert.Contains("Del usuario", File.ReadAllText(created.FullPath));
        Assert.DoesNotContain("Del asistente", File.ReadAllText(untouched.FullPath));
        Assert.Equal(AiUndoSkipReason.ChangedSince, _turn[0].Undo!.Reason);
        Assert.Equal(AiUndoState.Reverted, _turn[1].Undo!.State);
        Assert.Equal(AiUndoSkipReason.ChangedSince, _turn[2].Undo!.Reason);
        // Lo que cambió después no se va a poder revertir por repetirlo: no se ofrece otra vez.
        Assert.DoesNotContain(_turn, a => a.Undo!.CanAttempt);
        Assert.Equal(new AiRevertReport(0, 0, 0), _tools.RevertActivities(_turn));
    }

    [Fact]
    public async Task Revert_SeveralEditsOfTheSameNote_UnwindInReverseOrder()
    {
        var note = _vault.CreateNote("Lista", initialContent: "# Lista\n\n- uno\n");
        var original = File.ReadAllText(note.FullPath);
        await Call("append_to_note", new { path = "Lista", text = "- dos" });
        await Call("update_note_metadata", new { path = "Lista", add_tags = new[] { "pendiente" } });
        await Call("read_note", new { path = "Lista" });
        await Call("write_note", new { path = "Lista", content = "# Lista\n\nReescrita\n" });

        Assert.Equal(new AiRevertReport(3, 0, 0), _tools.RevertActivities(_turn));
        Assert.Equal(original, File.ReadAllText(note.FullPath));
    }

    [Fact]
    public async Task Revert_DeletedNote_ReturnsToItsFolderAndName()
    {
        var folder = Path.Combine(_vault.VaultPath, "Proyectos");
        Directory.CreateDirectory(folder);
        var note = _vault.CreateNote("Plan", folder, "# Plan\n\nTexto\n");
        var before = Snapshot();

        await Call("delete_note", new { path = "Proyectos/Plan.md" });
        Assert.False(File.Exists(note.FullPath));

        Assert.Equal(new AiRevertReport(1, 0, 0), _tools.RevertActivities(_turn));
        Assert.Equal(before, Snapshot());
        Assert.Empty(_vault.GetTrashedNotes());
    }

    [Fact]
    public async Task Revert_MovedNote_IsSkippedWhenItsOldPlaceIsTaken_AndCopyGoesToTrash()
    {
        Directory.CreateDirectory(Path.Combine(_vault.VaultPath, "Destino"));
        _vault.CreateNote("Plan", initialContent: "# Plan\n\nA\n");
        _vault.CreateNote("Otra", initialContent: "# Otra\n\nB\n");
        await Call("move_note", new { path = "Plan", destination_folder = "Destino" });
        await Call("move_note", new { path = "Otra", destination_folder = "Destino", copy = true });
        // El usuario crea otra nota donde estaba la que se movió.
        _vault.CreateNote("Plan", initialContent: "# Plan\n\nNueva del usuario\n");

        var report = _tools.RevertActivities(_turn);

        Assert.Equal(new AiRevertReport(1, 1, 0), report);
        Assert.Equal(AiUndoSkipReason.Occupied, _turn[0].Undo!.Reason);
        Assert.True(File.Exists(Path.Combine(_vault.VaultPath, "Destino", "Plan.md")));
        Assert.Contains("Nueva del usuario", File.ReadAllText(Path.Combine(_vault.VaultPath, "Plan.md")));
        Assert.False(File.Exists(Path.Combine(_vault.VaultPath, "Destino", "Otra.md")));
        Assert.True(File.Exists(Path.Combine(_vault.VaultPath, "Otra.md")));
    }

    [Fact]
    public async Task Revert_NoteBusyInTheEditor_IsLeftAlone_AndCanBeRetried()
    {
        var note = _vault.CreateNote("Abierta", initialContent: "# Abierta\n\nUno\n");
        var original = File.ReadAllText(note.FullPath);
        await Call("append_to_note", new { path = "Abierta", text = "Dos" });

        _noteBusy = true;
        Assert.Equal(new AiRevertReport(0, 1, 0), _tools.RevertActivities(_turn));
        Assert.Equal(AiUndoSkipReason.Busy, _turn[0].Undo!.Reason);
        Assert.Contains("Dos", File.ReadAllText(note.FullPath));

        _noteBusy = false;
        Assert.Equal(new AiRevertReport(1, 0, 0), _tools.RevertActivities(_turn));
        Assert.Equal(original, File.ReadAllText(note.FullPath));
    }

    [Fact]
    public async Task Revert_DailyNoteCreatedByTheTurn_IsRemovedWhole()
    {
        await Call("append_to_daily_note", new { text = "- Hecho" });
        Assert.Single(Snapshot());

        Assert.Equal(new AiRevertReport(1, 0, 0), _tools.RevertActivities(_turn));
        Assert.Empty(Snapshot());
    }

    // ===================== Kanban, recordatorios y hábitos =====================

    [Fact]
    public async Task Revert_KanbanCards_RestoreStateAndPosition()
    {
        var first = (await Call("create_kanban_card", new { title = "Primera", subtasks = new[] { "a", "b" } })).GetProperty("id").GetString()!;
        var second = (await Call("create_kanban_card", new { title = "Segunda", tags = new[] { "casa" } })).GetProperty("id").GetString()!;
        var doomed = (await Call("create_kanban_card", new { title = "Sobra" })).GetProperty("id").GetString()!;
        _turn.Clear();
        var before = JsonSerializer.Serialize(_kanban.Boards);

        await Call("update_kanban_card", new { card_id = first, title = "Primera (editada)", priority = "high", due = "2026-12-01" });
        await Call("move_kanban_card", new { card_id = first, column = "done" });
        await Call("archive_kanban_card", new { card_id = first });
        await Call("move_kanban_card", new { card_id = second, column = "backlog", before_card_id = doomed });
        await Call("delete_kanban_card", new { card_id = doomed });
        await Call("create_kanban_card", new { title = "Del turno" });
        Assert.NotEqual(before, JsonSerializer.Serialize(_kanban.Boards));

        var report = _tools.RevertActivities(_turn);

        Assert.Equal(new AiRevertReport(6, 0, 0), report);
        var card = _kanban.FindCardLocation(first)!;
        Assert.Equal("Primera", card.Card.Title);
        Assert.Equal(CoreColumnRole.Backlog, card.Column.Role);
        Assert.False(card.Card.IsCompleted);
        Assert.All(card.Card.Subtasks, s => Assert.False(s.IsDone));
        // Delante quedan las tarjetas de ejemplo del tablero; las tres de la prueba vuelven a su orden.
        Assert.Equal([first, second, doomed], card.Column.Cards.Select(c => c.Id).TakeLast(3));
        // La tarjeta creada por el turno va a la papelera de Kanban, no desaparece.
        Assert.Contains(_kanban.AllTrashedCards, t => t.Card.Title == "Del turno");
        Assert.DoesNotContain(_kanban.AllTrashedCards, t => t.Card.Id == doomed);
    }

    [Fact]
    public async Task Revert_KanbanCardEditedByTheUserAfterwards_IsNotTouched()
    {
        var id = (await Call("create_kanban_card", new { title = "Tarea" })).GetProperty("id").GetString()!;
        _turn.Clear();
        await Call("update_kanban_card", new { card_id = id, title = "Del asistente" });
        var location = _kanban.FindCardLocation(id)!;
        location.Card.Description = "Notas del usuario";
        _kanban.Save();

        Assert.Equal(new AiRevertReport(0, 1, 0), _tools.RevertActivities(_turn));
        Assert.Equal("Del asistente", location.Card.Title);
        Assert.Equal("Notas del usuario", location.Card.Description);
        Assert.Equal(AiUndoSkipReason.ChangedSince, _turn[0].Undo!.Reason);
    }

    [Fact]
    public async Task Revert_Reminders_RestoreTimeWithoutRearmingAndRecoverDeleted()
    {
        var past = DateTime.UtcNow.AddDays(-1);
        var fired = _reminders.Create("Ya sonó", string.Empty, past, "BellRingOutline");
        fired.IsTriggered = true;
        var removed = _reminders.Create("Borrado", string.Empty, DateTime.UtcNow.AddDays(3), "BellRingOutline");
        _reminders.Save();

        await Call("update_reminder", new { reminder_id = fired.Id, notify_at = "2030-01-01T10:00", completed = true });
        await Call("delete_reminder", new { reminder_id = removed.Id });
        var createdId = (await Call("create_reminder", new { title = "Del turno", notify_at = "2030-02-02T09:00" })).GetProperty("id").GetString()!;

        Assert.Equal(new AiRevertReport(3, 0, 0), _tools.RevertActivities(_turn));
        Assert.Equal(past, fired.NotifyAtUtc);
        Assert.False(fired.IsCompleted);
        // Volver a su hora no lo hace sonar otra vez.
        Assert.True(fired.IsTriggered);
        Assert.Contains(_reminders.ActiveReminders, r => r.Id == removed.Id);
        Assert.Contains(_reminders.DeletedReminders, r => r.Id == createdId);
    }

    [Fact]
    public async Task Revert_HabitMark_UnlessTheUserChangedItBack()
    {
        var read = _habits.AddHabit("Leer", HabitFrequency.Daily, []);
        var run = _habits.AddHabit("Correr", HabitFrequency.Daily, []);
        await Call("set_habit_done", new { habit = "Leer" });
        await Call("set_habit_done", new { habit = "Correr" });
        _habits.ToggleCompletion(run, DailyNoteService.Today);
        _habits.ToggleCompletion(run, DailyNoteService.Today.AddDays(-1));

        Assert.Equal(new AiRevertReport(1, 1, 0), _tools.RevertActivities(_turn));
        Assert.False(HabitService.IsCompleted(read, DailyNoteService.Today));
        Assert.Equal([DailyNoteService.Today.AddDays(-1)], run.CompletedDates);
    }

    // ===================== Lo que no se revierte =====================

    [Fact]
    public async Task Revert_ReportsActionsWithoutUndo_AndIgnoresReadsAndFailures()
    {
        _vault.CreateNote("Plan");
        await Call("create_folder", new { name = "Carpeta" });
        await Call("list_notes", new { });
        Assert.True((await _tools.ExecuteAsync("delete_note", JsonSerializer.SerializeToElement(new { path = "No existe" }), _context)).IsError);
        await Call("append_to_note", new { path = "Plan", text = "Texto" });

        var report = _tools.RevertActivities(_turn);

        Assert.Equal(new AiRevertReport(1, 0, 1), report);
        Assert.True(Directory.Exists(Path.Combine(_vault.VaultPath, "Carpeta")));
        Assert.Null(_turn.Single(a => a.Tool == "list_notes").Undo);
    }

    [Fact]
    public async Task Undo_SurvivesTheChatHistoryRoundTrip_WithoutStoringNoteContent()
    {
        _vault.CreateNote("Secreta", initialContent: "# Secreta\n\nTexto confidencial\n");
        await Call("append_to_note", new { path = "Secreta", text = "Añadido reservado" });

        var json = JsonSerializer.Serialize(_turn);
        Assert.DoesNotContain("confidencial", json);
        Assert.DoesNotContain("reservado", json);

        var restored = JsonSerializer.Deserialize<List<AiToolActivity>>(json)!;
        Assert.Equal(new AiRevertReport(1, 0, 0), _tools.RevertActivities(restored));
        Assert.DoesNotContain("reservado", File.ReadAllText(Path.Combine(_vault.VaultPath, "Secreta.md")));
        Assert.Equal(AiUndoState.Reverted, restored[0].Undo!.State);
    }
}
