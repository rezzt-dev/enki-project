using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using enki.Localization;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Etiquetas globales y estadísticas (AIA-08), resumen del día y de la semana (AIA-12) y enlaces sugeridos
/// (AIA-13): siempre dentro del ámbito del chat. Sobre servicios reales y un vault desechable.</summary>
[Collection("LocalizationMutatingTests")]
public sealed class AiVaultToolboxInsightTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly VaultService _vault;
    private readonly SessionService _sessions;
    private readonly KanbanService _kanban;
    private readonly AiVaultToolbox _tools;
    private readonly List<AiApprovalRequest> _approvals = [];
    private bool _approve = true;
    private int _bulkChanges;

    public AiVaultToolboxInsightTests()
    {
        _settings = new SettingsService(_directory);
        _vault = new VaultService(_settings);
        _vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        _sessions = new SessionService(_vault);
        _kanban = new KanbanService(_directory);
        var polar = new PolarTaskService(_directory);
        var polarAuth = new PolarAuthService(_settings, baseUrl: "https://test.example", anonKey: "test");
        var reminders = new ReminderService(_directory);
        _tools = new AiVaultToolbox(_vault, _sessions, _kanban, polar, polarAuth, reminders, _settings,
            new CalendarAggregationService(_kanban, polar, reminders, polarAuth),
            host: new AiVaultHost { BulkNotesChanged = () => _bulkChanges++ });
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private AiToolContext Context(AiAccessScope? scope = null) => new()
    {
        Scope = scope ?? AiAccessScope.Vault,
        ApprovalRequested = (request, _) =>
        {
            _approvals.Add(request);
            return Task.FromResult(_approve);
        }
    };

    private async Task<AiToolOutcome> Run(string name, object args, AiToolContext? context = null) =>
        await _tools.ExecuteAsync(name, JsonSerializer.SerializeToElement(args), context ?? Context());

    private async Task<JsonElement> Call(string name, object args, AiToolContext? context = null)
    {
        var result = await Run(name, args, context);
        Assert.False(result.IsError, result.Text);
        return JsonSerializer.Deserialize<JsonElement>(result.Text);
    }

    private static string Tagged(string title, params string[] tags) =>
        $"---\ntags: [{string.Join(", ", tags)}]\n---\n# {title}\n";

    // ===================== Etiquetas =====================

    [Fact]
    public async Task ListTags_CountsNotesAndCards_InScopeOnly()
    {
        _vault.CreateNote("Uno", initialContent: Tagged("Uno", "reunion"));
        _vault.CreateNote("Dos", initialContent: Tagged("Dos", "reunión", "viaje"));
        var session = _sessions.Create("Privado");
        _vault.CreateNote("Secreto", session.RootPath, Tagged("Secreto", "oculta"));
        await Call("create_kanban_card", new { title = "Tarjeta", tags = new[] { "viaje" } });

        var tags = (await Call("list_tags", new { })).GetProperty("tags").EnumerateArray().ToList();

        // La sesión no está en el ámbito de un chat del vault sin acceso ampliado: su etiqueta no aparece.
        Assert.DoesNotContain(tags, t => t.GetProperty("name").GetString() == "oculta");
        var viaje = tags.Single(t => t.GetProperty("name").GetString() == "viaje");
        Assert.Equal(1, viaje.GetProperty("notes").GetInt32());
        Assert.Equal(1, viaje.GetProperty("kanban_cards").GetInt32());

        var sessionChat = (await Call("list_tags", new { }, Context(new AiAccessScope(session.Id, false)))).GetProperty("tags");
        var only = Assert.Single(sessionChat.EnumerateArray());
        Assert.Equal("oculta", only.GetProperty("name").GetString());
        Assert.False(only.TryGetProperty("kanban_cards", out _));
    }

    [Fact]
    public async Task RenameTag_AsksWithTheCount_AndOnlyTouchesTheScope()
    {
        var uno = _vault.CreateNote("Uno", initialContent: Tagged("Uno", "reunion"));
        var session = _sessions.Create("Privado");
        var secreto = _vault.CreateNote("Secreto", session.RootPath, Tagged("Secreto", "reunion"));
        var sessionBefore = File.ReadAllText(secreto.FullPath);
        await Call("create_kanban_card", new { title = "Tarjeta", tags = new[] { "reunion" } });

        var result = await Call("rename_tag", new { tag = "reunion", new_name = "Reunión" });

        Assert.Equal(1, result.GetProperty("notes_updated").GetInt32());
        Assert.Equal(1, result.GetProperty("kanban_cards_updated").GetInt32());
        var request = Assert.Single(_approvals);
        Assert.Equal(2, request.Items.Count);
        Assert.Contains("reunión", NoteTagHelper.ExtractTags(File.ReadAllText(uno.FullPath)));
        Assert.Equal(sessionBefore, File.ReadAllText(secreto.FullPath));
        Assert.Contains("reunión", _kanban.AllColumns.SelectMany(c => c.Cards).Single(c => c.Title == "Tarjeta").Tags);
        Assert.Equal(1, _bulkChanges);
        // La nota guarda su versión anterior en el historial.
        Assert.NotEmpty(_vault.GetVersionHistory(_vault.GetNote(uno.FullPath)!));
    }

    [Fact]
    public async Task RenameTag_Denied_ChangesNothing()
    {
        var uno = _vault.CreateNote("Uno", initialContent: Tagged("Uno", "reunion"));
        var before = File.ReadAllText(uno.FullPath);
        _approve = false;

        var result = await Run("rename_tag", new { tag = "reunion", new_name = "reunión" });

        Assert.True(result.IsError);
        Assert.Equal(before, File.ReadAllText(uno.FullPath));
        Assert.Equal(0, _bulkChanges);
    }

    [Fact]
    public async Task MergeTags_JoinsVariants_AndRejectsUnknownOrInvalidNames()
    {
        var uno = _vault.CreateNote("Uno", initialContent: Tagged("Uno", "reunion"));
        var dos = _vault.CreateNote("Dos", initialContent: Tagged("Dos", "reuniones"));

        var result = await Call("merge_tags", new { tags = new[] { "reunion", "reuniones" }, into = "reunión" });

        Assert.Equal(2, result.GetProperty("notes_updated").GetInt32());
        Assert.Equal(["reunión"], NoteTagHelper.ExtractTags(File.ReadAllText(uno.FullPath)));
        Assert.Equal(["reunión"], NoteTagHelper.ExtractTags(File.ReadAllText(dos.FullPath)));
        Assert.True((await Run("merge_tags", new { tags = new[] { "inexistente" }, into = "algo" })).IsError);
        Assert.True((await Run("rename_tag", new { tag = "reunión", new_name = "a,b" })).IsError);
    }

    // ===================== Estadísticas =====================

    [Fact]
    public async Task GetStatistics_ReportsThePeriodAndThePreviousOne()
    {
        _vault.CreateNote("Hoy", initialContent: "# Hoy\n\nUna nota escrita hoy.\n");

        var result = await Call("get_statistics", new { period = "last_7_days" });

        Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), result.GetProperty("to").GetString());
        Assert.True(result.GetProperty("current").GetProperty("notes_created").GetInt32() >= 1);
        Assert.Equal(7, result.GetProperty("daily").GetArrayLength());
        Assert.True(result.TryGetProperty("previous_period", out _));
        Assert.True((await Run("get_statistics", new { period = "decade" })).IsError);
    }

    [Fact]
    public async Task GetStatistics_IsNotOfferedInASessionChat()
    {
        var session = _sessions.Create("Privado");
        Assert.DoesNotContain(_tools.AvailableTools(Context(new AiAccessScope(session.Id, false))), t => t.Name == "get_statistics");
        Assert.Contains(_tools.AvailableTools(Context()), t => t.Name == "get_statistics");
    }

    // ===================== Resumen =====================

    [Fact]
    public async Task GetBriefing_GathersOverdueAgendaAndRecentNotes()
    {
        _vault.CreateNote("Reciente", initialContent: "# Reciente\n");
        await Call("create_kanban_card", new { title = "Atrasada", due = DateTime.Today.AddDays(-2).ToString("yyyy-MM-dd") });
        await Call("create_kanban_card", new { title = "Para hoy", due = DateTime.Today.ToString("yyyy-MM-dd") + "T23:59" });

        var result = await Call("get_briefing", new { period = "day" });

        var overdue = result.GetProperty("overdue").GetProperty("kanban_cards").EnumerateArray().Select(c => c.GetProperty("title").GetString());
        Assert.Contains("Atrasada", overdue);
        Assert.DoesNotContain("Para hoy", overdue);
        Assert.Contains(result.GetProperty("agenda").GetProperty("items").EnumerateArray(),
            i => i.GetProperty("title").GetString() == "Para hoy");
        Assert.Contains(result.GetProperty("recent_notes").GetProperty("notes").EnumerateArray(),
            n => n.GetProperty("title").GetString() == "Reciente");
    }

    [Fact]
    public async Task GetBriefing_InASessionChat_LeavesVaultModulesOut()
    {
        var session = _sessions.Create("Privado");
        _vault.CreateNote("De la sesión", session.RootPath, "# De la sesión\n");
        _vault.CreateNote("Del vault", initialContent: "# Del vault\n");

        var result = await Call("get_briefing", new { period = "week" }, Context(new AiAccessScope(session.Id, false)));

        Assert.False(result.TryGetProperty("agenda", out _));
        Assert.False(result.TryGetProperty("overdue", out _));
        var notes = result.GetProperty("recent_notes").GetProperty("notes").EnumerateArray().Select(n => n.GetProperty("title").GetString());
        Assert.Equal(["De la sesión"], notes);
    }

    // ===================== Enlaces =====================

    [Fact]
    public async Task SuggestLinks_FindsMentionsInBothDirections_AndSkipsLinkedNotes()
    {
        _vault.CreateNote("Viaje a Lisboa", initialContent: "# Viaje a Lisboa\n\nHotel y vuelos. Ver presupuesto anual.\n");
        _vault.CreateNote("Presupuesto anual", initialContent: "# Presupuesto anual\n\nGastos del año.\n");
        _vault.CreateNote("Diario", initialContent: "# Diario\n\nHoy he planeado el Viaje a Lisboa.\n");
        _vault.CreateNote("Enlazada", initialContent: "# Enlazada\n\nYa enlaza [[Viaje a Lisboa]].\n");

        var result = await Call("suggest_links", new { path = "Viaje a Lisboa" });
        var suggestions = result.GetProperty("suggestions").EnumerateArray().ToList();

        var presupuesto = suggestions.Single(s => s.GetProperty("location").GetProperty("title").GetString() == "Presupuesto anual");
        Assert.Contains("mentioned_in_this_note", presupuesto.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("[[Presupuesto anual]]", presupuesto.GetProperty("link").GetString());
        Assert.Equal("this_note", presupuesto.GetProperty("where").GetString());

        var diario = suggestions.Single(s => s.GetProperty("location").GetProperty("title").GetString() == "Diario");
        Assert.Contains("mentions_this_note", diario.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("[[Viaje a Lisboa]]", diario.GetProperty("link_to_this_note").GetString());

        Assert.DoesNotContain(suggestions, s => s.GetProperty("location").GetProperty("title").GetString() == "Enlazada");
        Assert.Equal("off", result.GetProperty("semantic_search").GetString());
    }

    [Fact]
    public async Task SuggestLinks_ChangesNothing_AndStaysInScope()
    {
        var session = _sessions.Create("Privado");
        _vault.CreateNote("Nota", initialContent: "# Nota\n\nHabla de Secreto.\n");
        _vault.CreateNote("Secreto", session.RootPath, "# Secreto\n");
        var before = Directory.GetFiles(_vault.VaultPath, "*.md", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllText);

        var result = await Call("suggest_links", new { path = "Nota" });

        Assert.Empty(result.GetProperty("suggestions").EnumerateArray());
        Assert.Equal(before, Directory.GetFiles(_vault.VaultPath, "*.md", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllText));
    }

    [Fact]
    public void NewTools_HaveALabelInEveryLanguage()
    {
        foreach (var tool in new[] { "list_tags", "rename_tag", "merge_tags", "get_statistics", "get_briefing", "suggest_links", "semantic_search" })
            Assert.False(string.IsNullOrEmpty(Strings.ResourceManager.GetString("Assistant_Tool_" + tool)), tool);
    }
}
