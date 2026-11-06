using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Herramientas sobre servicios reales y un vault desechable: ámbito, conflictos, papelera y MCP.</summary>
public sealed class AiVaultToolboxTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly VaultService _vault;
    private readonly SessionService _sessions;
    private readonly KanbanService _kanban;
    private readonly PolarTaskService _polar;
    private readonly PolarAuthService _auth;
    private readonly ReminderService _reminders;
    private readonly AiVaultToolbox _tools;

    public AiVaultToolboxTests()
    {
        _settings = new SettingsService(_directory);
        _vault = new VaultService(_settings);
        _vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        _sessions = new SessionService(_vault);
        _kanban = new KanbanService(_directory);
        _polar = new PolarTaskService(_directory);
        _auth = new PolarAuthService(_settings, baseUrl: "https://test.example", anonKey: "test");
        _reminders = new ReminderService(_directory);
        _tools = new AiVaultToolbox(_vault, _sessions, _kanban, _polar, _auth, _reminders, _settings);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private static AiToolContext Context(AiAccessScope? scope = null) => new() { Scope = scope ?? AiAccessScope.Vault };
    private Task<AiToolOutcome> Call(string name, object args, AiToolContext? context = null) =>
        _tools.ExecuteAsync(name, JsonSerializer.SerializeToElement(args), context ?? Context());
    private static JsonElement Json(AiToolOutcome result)
    {
        Assert.False(result.IsError, result.Text);
        return JsonSerializer.Deserialize<JsonElement>(result.Text);
    }

    [Fact]
    public async Task Skills_UseLibrary_RequireReadBeforeWrite_AndDetectExternalChanges()
    {
        var context = Context();
        var created = Json(await Call("create_skill", new { name = "Editor", content = "Original" }, context));
        var path = created.GetProperty("path").GetString()!;
        var fullPath = Path.Combine(_vault.VaultPath, ".enki", "skills", path);
        var skills = new SkillService(_vault);
        Assert.Single(skills.LoadSkills());
        Assert.True((await Call("write_skill", new { path, content = "Sin leer" })).IsError);
        Json(await Call("read_skill", new { path }, context));
        Json(await Call("write_skill", new { path, content = "Revisado" }, context));
        Assert.Contains("Revisado", skills.LoadSkills().Single().Content);
        File.WriteAllText(fullPath, "Cambio externo");
        Assert.True((await Call("write_skill", new { path, content = "No sobrescribir" }, context)).IsError);
        Assert.Equal("Cambio externo", File.ReadAllText(fullPath));
        Assert.True((await Call("read_skill", new { path = "../settings.json" })).IsError);
        Assert.True((await Call("read_skill", new { path = fullPath })).IsError);
    }

    [Fact]
    public async Task Skills_RespectScope_ReadOnly_AndPermanentDeletionApproval()
    {
        var path = Json(await Call("create_skill", new { name = "Editor" })).GetProperty("path").GetString()!;
        Assert.True((await Call("delete_skill", new { path })).IsError);
        Assert.Single(new SkillService(_vault).LoadSkills());
        var session = _sessions.Create("Privado");
        Assert.DoesNotContain(_tools.AvailableTools(Context(new AiAccessScope(session.Id, false))), t => t.Name.EndsWith("skill") || t.Name == "list_skills");
        _settings.Settings.Ai.AllowVaultChanges = false;
        Assert.True((await Call("write_skill", new { path, content = "No" })).IsError);
        _settings.Settings.Ai.AllowVaultChanges = true;
        var approved = new AiToolContext { Scope = AiAccessScope.Vault, ApprovalRequested = (_, _) => Task.FromResult(true) };
        Json(await Call("delete_skill", new { path }, approved));
        Assert.Empty(new SkillService(_vault).LoadSkills());
    }

    [Fact]
    public async Task Notes_CreateReadEditTrashRestore_UseRealEntities()
    {
        var context = Context();
        var created = Json(await Call("create_note", new { title = "Plan", content = "# Plan\n\nOriginal" }, context));
        var path = created.GetProperty("path").GetString()!;
        Json(await Call("read_note", new { path }, context));
        Json(await Call("edit_note", new { path, edits = new[] { new { find = "Original", replace = "Revisado" } } }, context));
        Assert.Contains("Revisado", File.ReadAllText(Path.Combine(_vault.VaultPath, path)));
        Json(await Call("delete_note", new { path }, context));
        Assert.False(File.Exists(Path.Combine(_vault.VaultPath, path)));
        var item = Json(await Call("list_trash", new { type = "note" })).GetProperty("items").EnumerateArray().Single();
        Json(await Call("restore_from_trash", new { type = "note", id = item.GetProperty("id").GetString() }));
        Assert.Contains("Revisado", File.ReadAllText(Path.Combine(_vault.VaultPath, path)));
    }

    [Fact]
    public async Task WholeNoteWrite_RequiresCompleteRead_AndDetectsExternalChange()
    {
        var note = _vault.CreateNote("Plan", _vault.VaultPath, "# Plan\nOriginal");
        var context = Context();
        Assert.True((await Call("write_note", new { path = note.FileName, content = "Replacement" }, context)).IsError);
        Json(await Call("read_note", new { path = note.FileName }, context));
        File.WriteAllText(note.FullPath, "# Plan\nCambio externo");
        Assert.True((await Call("write_note", new { path = note.FileName, content = "Replacement" }, context)).IsError);
        Assert.Contains("Cambio externo", File.ReadAllText(note.FullPath));
        Json(await Call("read_note", new { path = note.FileName }, context));
        Json(await Call("write_note", new { path = note.FileName, content = "# Plan\nRevisado" }, context));
        Assert.Contains("Revisado", File.ReadAllText(note.FullPath));
    }

    [Fact]
    public async Task ExactEdits_AreAtomic_WhenAReplacementIsAmbiguous()
    {
        var note = _vault.CreateNote("Plan", _vault.VaultPath, "# Plan\nx x y");
        var outcome = await Call("edit_note", new { path = note.FileName, edits = new[]
        {
            new { find = "y", replace = "z" }, new { find = "x", replace = "w" }
        } });
        Assert.True(outcome.IsError);
        Assert.Equal("# Plan\nx x y", File.ReadAllText(note.FullPath));
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("/etc/passwd")]
    [InlineData(".enki/settings.json")]
    [InlineData(".sessions/hidden/note.md")]
    [InlineData("C:\\private\\note.md")]
    public async Task Paths_RejectOutsideAndInternalLocations(string path) =>
        Assert.True((await Call("read_note", new { path })).IsError);

    [Fact]
    public async Task Symlinks_AreNotFollowed()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(_directory, "outside.md");
        File.WriteAllText(outside, "Secret");
        File.CreateSymbolicLink(Path.Combine(_vault.VaultPath, "link.md"), outside);
        Assert.True((await Call("read_note", new { path = "link.md" })).IsError);
        Assert.DoesNotContain("Secret", Json(await Call("list_notes", new { })).GetRawText());
    }

    [Fact]
    public async Task ReadOnly_IsEnforcedAtExecution_AndInDiscovery()
    {
        _settings.Settings.Ai.AllowVaultChanges = false;
        Assert.All(_tools.AvailableTools(Context()), t => Assert.Equal(AiToolEffect.Read, t.Effect));
        Assert.True((await Call("create_note", new { title = "No" })).IsError);
        Assert.Empty(_vault.GetAllNotesRecursive(_vault.VaultPath));
    }

    [Fact]
    public async Task SessionChat_IsIsolated_AndExpandedAccessExcludesProtectedSessions()
    {
        var session = _sessions.Create("Proyecto");
        var other = _sessions.Create("Otro");
        var (secret, _) = _sessions.CreateEncrypted("Privado", "super-secreta-123");
        var context = Context(new AiAccessScope(session.Id, false));
        Json(await Call("create_note", new { title = "Plan" }, context));
        Assert.Single(_vault.GetAllNotesRecursive(session.RootPath));
        Assert.True((await Call("list_notes", new { workspace = "vault" }, context)).IsError);
        Assert.True((await Call("list_notes", new { workspace = other.Id }, context)).IsError);
        Assert.True((await Call("list_kanban", new { }, context)).IsError);
        var expanded = Context(new AiAccessScope(session.Id, true));
        Assert.True((await Call("list_notes", new { workspace = other.Id }, expanded)).IsError);
        Assert.True((await Call("list_notes", new { workspace = "vault" }, expanded)).IsError);
        var vaultAccess = Context(new AiAccessScope(null, true));
        Json(await Call("list_notes", new { workspace = other.Id }, vaultAccess));
        Assert.True((await Call("list_notes", new { workspace = secret.Id }, expanded)).IsError);
        Assert.True((await Call("list_notes", new { workspace = session.Id })).IsError);
    }

    [Fact]
    public async Task LinkedNote_OutsideScope_DoesNotExposeSessionPath()
    {
        var session = _sessions.Create("Invisible");
        var note = _vault.CreateNote("Secreto", session.RootPath);
        var board = _kanban.DefaultBoard;
        _kanban.CreateCard(board.Id, board.Columns[0].Id, new KanbanCard
        {
            Id = "card", Title = "Tarea", LinkedNotePath = note.FullPath
        });
        var result = Json(await Call("list_kanban", new { })).GetRawText();
        Assert.DoesNotContain("Invisible", result);
        Assert.DoesNotContain("Secreto", result);
        Assert.Contains("outside_scope", result);
    }

    [Fact]
    public async Task Kanban_CreatesMovesAndRestoresTheSameCard()
    {
        var created = Json(await Call("create_kanban_card", new { title = "Revisar", subtasks = new[] { "Leer" } }));
        var id = created.GetProperty("id").GetString()!;
        Json(await Call("move_kanban_card", new { card_id = id, column = "done" }));
        var card = _kanban.FindCardLocation(id)!.Card;
        Assert.NotNull(card.CompletedAtUtc);
        Assert.True(Assert.Single(card.Subtasks).IsDone);
        Json(await Call("archive_kanban_card", new { card_id = id }));
        Assert.True(card.IsArchived);
        Json(await Call("delete_kanban_card", new { card_id = id }));
        Json(await Call("restore_from_trash", new { type = "kanban_card", id }));
        Assert.Same(card, _kanban.FindCardLocation(id)!.Card);
        Assert.False(card.IsArchived);
    }

    [Fact]
    public async Task Polar_RequiresLink_AndUsesTheExistingStore()
    {
        Assert.True((await Call("list_polar", new { })).IsError);
        _settings.Settings.PolarLinked = true;
        _settings.Settings.PolarUserId = "user";
        _settings.Settings.PolarAccessToken = "test";
        var list = Json(await Call("create_polar_list", new { title = "Trabajo" }));
        var task = Json(await Call("create_polar_task", new
        {
            list_id = list.GetProperty("id").GetString(), title = "Implementar", subtasks = new[] { "Revisar" }
        }));
        var id = task.GetProperty("id").GetString();
        Json(await Call("update_polar_task", new { task_id = id, completed = true }));
        Assert.True(Assert.Single(_polar.Store.Tasks).Completed);
        Assert.True(Assert.Single(_polar.Store.Subtasks).Completed);
        Assert.True(Assert.Single(_polar.Store.Tasks).Dirty);
    }

    [Fact]
    public async Task Reminder_CreateRescheduleTrashRestore_UsesExistingService()
    {
        var created = Json(await Call("create_reminder", new { title = "Revisar", notify_at = "2027-01-03T18:30+01:00" }));
        var id = created.GetProperty("id").GetString()!;
        Json(await Call("update_reminder", new { reminder_id = id, notify_at = "2027-01-04T19:30+01:00" }));
        Assert.Equal(new DateTime(2027, 1, 4, 18, 30, 0, DateTimeKind.Utc), Assert.Single(_reminders.ActiveReminders).NotifyAtUtc);
        Json(await Call("delete_reminder", new { reminder_id = id }));
        Assert.Empty(_reminders.ActiveReminders);
        Json(await Call("restore_from_trash", new { type = "reminder", id }));
        Assert.Single(_reminders.ActiveReminders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermanentDeletion_RequiresExplicitApproval(bool approved)
    {
        var note = _vault.CreateNote("Plan", _vault.VaultPath);
        _vault.DeleteNote(note);
        var requests = new List<AiApprovalRequest>();
        var context = new AiToolContext
        {
            Scope = AiAccessScope.Vault,
            ApprovalRequested = (request, _) => { requests.Add(request); return Task.FromResult(approved); }
        };
        var result = await Call("empty_trash", new { }, context);
        Assert.Equal(!approved, result.IsError);
        Assert.Single(requests);
        Assert.Equal(approved ? 0 : 1, _vault.GetTrashedNotes().Count);
    }

    [Fact]
    public async Task CancelledContext_CannotChangeVault()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call("create_note", new { title = "No" },
            new AiToolContext { Scope = AiAccessScope.Vault, Cancellation = cancellation.Token }));
        Assert.Empty(_vault.GetAllNotesRecursive(_vault.VaultPath));
    }

    [Fact]
    public async Task Mcp_AuthenticatesRejectsOriginsExecutesAndRevokesTurn()
    {
        using var server = new EnkiMcpServer(_tools);
        var access = server.Register(Context());
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        async Task<HttpResponseMessage> Send(string body, bool token = true, bool origin = false)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, access.Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            if (token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.BearerToken);
            if (origin) request.Headers.Add("Origin", "https://example.com");
            return await http.SendAsync(request);
        }
        const string list = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
        using (var rejected = await Send(list, token: false)) Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using (var rejected = await Send(list, origin: true)) Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        using (var response = await Send(list))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("create_note", await response.Content.ReadAsStringAsync());
        }
        using (var response = await Send("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"create_note","arguments":{"title":"Desde MCP"}}}"""))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Desde MCP", await response.Content.ReadAsStringAsync());
        }
        Assert.Single(_vault.GetAllNotesRecursive(_vault.VaultPath));
        server.Unregister(access.BearerToken);
        using (var rejected = await Send(list)) Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        server.Stop();
        Assert.False(server.IsRunning);
    }
    [Fact]
    public async Task Approval_DoesNotOverrideReadOnlyEnabledWhileWaiting()
    {
        var note = _vault.CreateNote("Plan", _vault.VaultPath);
        _vault.DeleteNote(note);
        var context = new AiToolContext
        {
            Scope = AiAccessScope.Vault,
            ApprovalRequested = (_, _) =>
            {
                _settings.Settings.Ai.AllowVaultChanges = false;
                return Task.FromResult(true);
            }
        };
        Assert.True((await Call("empty_trash", new { }, context)).IsError);
        Assert.Single(_vault.GetTrashedNotes());
    }

    [Fact]
    public void SessionSymlinks_AreExcludedFromWorkspaceDiscovery()
    {
        if (OperatingSystem.IsWindows()) return;
        var session = _sessions.Create("Proyecto");
        Directory.CreateSymbolicLink(Path.Combine(Path.GetDirectoryName(session.RootPath)!, "link"), session.RootPath);
        Assert.Single(_sessions.List());
    }

    [Fact]
    public async Task OpenEditorConflict_BlocksNoteChanges()
    {
        var note = _vault.CreateNote("Plan", _vault.VaultPath, "# Plan\nOriginal");
        var tools = new AiVaultToolbox(_vault, _sessions, _kanban, _polar, _auth, _reminders, _settings,
            host: new AiVaultHost { PrepareNoteChange = _ => false });
        var result = await tools.ExecuteAsync("append_to_note", JsonSerializer.SerializeToElement(new
        {
            path = note.FileName, text = "Cambio"
        }), Context());
        Assert.True(result.IsError);
        Assert.Equal("# Plan\nOriginal", File.ReadAllText(note.FullPath));
    }

    [Fact]
    public async Task TruncatedRead_CannotAuthorizeWholeNoteReplacement()
    {
        var note = _vault.CreateNote("Larga", _vault.VaultPath, "# Larga\n" + new string('x', 61_000));
        var context = Context();
        Assert.True(Json(await Call("read_note", new { path = note.FileName }, context)).GetProperty("truncated").GetBoolean());
        Assert.True((await Call("write_note", new { path = note.FileName, content = "Corta" }, context)).IsError);
        Assert.True(File.ReadAllText(note.FullPath).Length > 60_000);
    }

    [Fact]
    public async Task PartialEdits_PreserveCrLfLineEndings()
    {
        var note = _vault.CreateNote("Plan", _vault.VaultPath, "# Plan\r\n\r\nOriginal\r\n");
        Json(await Call("edit_note", new { path = note.FileName, edits = new[] { new { find = "Original", replace = "Uno\nDos" } } }));
        Assert.Equal("# Plan\r\n\r\nUno\r\nDos\r\n", File.ReadAllText(note.FullPath));
    }


    // ===================== Permiso de consulta =====================

    [Fact]
    public async Task ReadAccess_NotGranted_AsksOnce_AndDenialHoldsForTheRestOfTheTurn()
    {
        _vault.CreateNote("Plan", _vault.VaultPath);
        var asked = 0;
        var activities = new List<AiToolActivity>();
        var context = new AiToolContext
        {
            Scope = AiAccessScope.Vault,
            ReadAccess = new AiReadAccess(() => false, _ => { asked++; return Task.FromResult(false); }),
            ActivityReported = activities.Add
        };

        var first = await Call("list_notes", new { }, context);
        var second = await Call("search_notes", new { query = "Plan" }, context);

        Assert.True(first.IsError);
        Assert.True(second.IsError);
        Assert.Contains("not allowed this chat to search", first.Text, StringComparison.Ordinal);
        Assert.Equal(1, asked);
        Assert.All(activities, activity => Assert.Equal(AiToolActivityState.Denied, activity.State));
    }

    [Fact]
    public async Task ReadAccess_GrantedWhenAsked_RunsTheRead_AndConcurrentReadsShareOneRequest()
    {
        _vault.CreateNote("Plan", _vault.VaultPath);
        var granted = false;
        var asked = 0;
        var decision = new TaskCompletionSource<bool>();
        var context = new AiToolContext
        {
            Scope = AiAccessScope.Vault,
            ReadAccess = new AiReadAccess(() => granted, async _ =>
            {
                asked++;
                var allowed = await decision.Task;
                granted = allowed;
                return allowed;
            })
        };

        var first = Call("list_notes", new { }, context);
        var second = Call("get_workspace_overview", new { }, context);
        decision.SetResult(true);

        Json(await first);
        Json(await second);
        Assert.Equal(1, asked);
        // Concedido, las lecturas siguientes no vuelven a preguntar.
        Json(await Call("search_notes", new { query = "Plan" }, context));
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task ReadAccess_DoesNotGateChanges_AndUnrestrictedNeverAsks()
    {
        var context = new AiToolContext
        {
            Scope = AiAccessScope.Vault,
            ReadAccess = new AiReadAccess(() => false, _ => throw new InvalidOperationException("No debería preguntar."))
        };
        Json(await Call("create_note", new { title = "Nueva" }, context));
        Assert.Single(_vault.GetAllNotesRecursive(_vault.VaultPath));
        Json(await Call("list_notes", new { }));
    }

    [Fact]
    public async Task ReadAccess_StoppingTheTurnWhileAsking_CancelsWithoutGranting()
    {
        using var cancellation = new CancellationTokenSource();
        var context = new AiToolContext
        {
            Scope = AiAccessScope.Vault,
            Cancellation = cancellation.Token,
            ReadAccess = new AiReadAccess(() => false, token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(true);
            })
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call("list_notes", new { }, context));
    }

    // ===================== Añadir a la nota activa =====================

    [Fact]
    public void AppendToNote_WritesAtTheEnd_KeepsVersion_AndRefusesAChangedNote()
    {
        var note = _vault.CreateNote("Plan", _vault.VaultPath, initialContent: "# Plan\n\nUno\n");
        var preview = _vault.ReadFromDisk(note.FullPath)!.Value;

        var updated = _tools.AppendToNote(note.FullPath, "Dos", preview.Stamp);

        Assert.Equal("# Plan\n\nUno\n\nDos\n", File.ReadAllText(note.FullPath));
        Assert.Equal("Plan", updated.Title);
        Assert.Contains(_vault.GetVersionHistory(note), version => version.Trigger == NoteVersionTrigger.Assistant);

        // Lo que se previsualizó ya no es lo que hay: no se escribe nada.
        File.WriteAllText(note.FullPath, "# Plan\n\nCambiado fuera\n");
        Assert.Throws<ExternalModificationException>(() => _tools.AppendToNote(note.FullPath, "Tres", preview.Stamp));
        Assert.Equal("# Plan\n\nCambiado fuera\n", File.ReadAllText(note.FullPath));
    }
}
