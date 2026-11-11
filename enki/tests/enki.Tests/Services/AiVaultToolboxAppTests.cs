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

/// <summary>Herramientas que no son del contenido de siempre: navegación por la interfaz, manual integrado,
/// hábitos, nota diaria y plantillas. Sobre servicios reales y un vault desechable.</summary>
[Collection("LocalizationMutatingTests")]
public sealed class AiVaultToolboxAppTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly VaultService _vault;
    private readonly SessionService _sessions;
    private readonly KanbanService _kanban;
    private readonly HabitService _habits;
    private readonly NoteTemplateService _templates;
    private readonly AiVaultToolbox _tools;

    private readonly List<string> _openedNotes = [];
    private readonly List<(string Board, string? Card)> _openedBoards = [];
    private readonly List<string> _openedSections = [];
    private readonly HashSet<string> _disabledSections = [];

    public AiVaultToolboxAppTests()
    {
        _settings = new SettingsService(_directory);
        _vault = new VaultService(_settings);
        _vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        _sessions = new SessionService(_vault);
        _kanban = new KanbanService(_directory);
        _habits = new HabitService(_vault);
        _templates = new NoteTemplateService(_vault);
        _tools = new AiVaultToolbox(_vault, _sessions, _kanban, new PolarTaskService(_directory),
            new PolarAuthService(_settings, baseUrl: "https://test.example", anonKey: "test"), new ReminderService(_directory), _settings,
            host: new AiVaultHost
            {
                OpenNote = note =>
                {
                    if (_disabledSections.Contains("notes")) return false;
                    _openedNotes.Add(note.FullPath);
                    return true;
                },
                OpenKanbanBoard = (board, card) =>
                {
                    if (_disabledSections.Contains("kanban")) return false;
                    _openedBoards.Add((board, card));
                    return true;
                },
                OpenSection = section =>
                {
                    if (_disabledSections.Contains(section)) return false;
                    _openedSections.Add(section);
                    return true;
                }
            },
            habitService: _habits, noteTemplateService: _templates);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private static AiToolContext Context(AiAccessScope? scope = null, AiReadAccess? readAccess = null) =>
        new() { Scope = scope ?? AiAccessScope.Vault, ReadAccess = readAccess ?? AiReadAccess.Unrestricted };

    private Task<AiToolOutcome> Call(string name, object args, AiToolContext? context = null) =>
        _tools.ExecuteAsync(name, JsonSerializer.SerializeToElement(args), context ?? Context());

    private static JsonElement Json(AiToolOutcome result)
    {
        Assert.False(result.IsError, result.Text);
        return JsonSerializer.Deserialize<JsonElement>(result.Text);
    }

    private static string Today => DailyNoteService.Today.ToString("yyyy-MM-dd");

    // ===================== Navegación =====================

    [Fact]
    public async Task OpenNote_OpensAnExistingNote_WithoutChangingAnything()
    {
        var note = _vault.CreateNote("Plan", initialContent: "# Plan\n\nTexto\n");
        var before = File.ReadAllText(note.FullPath);
        var activities = new List<AiToolActivity>();
        var context = new AiToolContext { Scope = AiAccessScope.Vault, ActivityReported = activities.Add };

        var result = Json(await Call("open_note", new { path = "Plan" }, context));

        Assert.True(result.GetProperty("opened").GetBoolean());
        Assert.Equal([note.FullPath], _openedNotes);
        Assert.Equal(before, File.ReadAllText(note.FullPath));
        // La fila de actividad enlaza a la misma nota, como cualquier otra acción sobre ella, y no hay nada que deshacer.
        Assert.Equal(AiToolTargetKind.Note, activities[0].TargetKind);
        Assert.Equal(note.FullPath, activities[0].TargetId);
        Assert.Null(activities[0].Undo);
    }

    [Fact]
    public async Task OpenNote_MissingOrOutsideScope_FailsWithoutRevealingPaths()
    {
        _vault.CreateNote("Del vault");
        var session = _sessions.Create("Privado");
        var sessionChat = Context(new AiAccessScope(session.Id, false));

        var missing = await Call("open_note", new { path = "No existe" });
        var outside = await Call("open_note", new { path = "Del vault" }, sessionChat);
        var otherWorkspace = await Call("open_note", new { path = "Del vault", workspace = "vault" }, sessionChat);

        Assert.True(missing.IsError);
        Assert.True(outside.IsError);
        Assert.True(otherWorkspace.IsError);
        Assert.Empty(_openedNotes);
        Assert.All(new[] { missing, outside, otherWorkspace }, r => Assert.DoesNotContain(_vault.VaultPath, r.Text));
    }

    [Fact]
    public async Task OpenNote_IsOfferedWithChangesOff_ButNeedsTheReadPermission()
    {
        _vault.CreateNote("Plan");
        _settings.Settings.Ai.AllowVaultChanges = false;
        Assert.Contains(_tools.AvailableTools(Context()), t => t.Name == "open_note");
        Json(await Call("open_note", new { path = "Plan" }));

        var denied = Context(readAccess: new AiReadAccess(() => false, null));
        Assert.True((await Call("open_note", new { path = "Plan" }, denied)).IsError);
        Assert.Single(_openedNotes);
    }

    [Fact]
    public async Task OpenKanbanBoard_OpensBoardOrCard_AndReportsADisabledSection()
    {
        var board = _kanban.CreateBoard("Trabajo");
        var card = Json(await Call("create_kanban_card", new { title = "Tarea", board = "Trabajo" }));
        var cardId = card.GetProperty("id").GetString()!;

        Json(await Call("open_kanban_board", new { board = "Trabajo" }));
        Json(await Call("open_kanban_board", new { card_id = cardId }));
        Assert.Equal([(board.Id, null), (board.Id, cardId)], _openedBoards);
        Assert.True((await Call("open_kanban_board", new { board = "No existe" })).IsError);

        _disabledSections.Add("kanban");
        var disabled = await Call("open_kanban_board", new { board = "Trabajo" });
        Assert.True(disabled.IsError);
        Assert.Contains("turned off", disabled.Text);
        Assert.Equal(2, _openedBoards.Count);
    }

    [Fact]
    public async Task OpenSection_WorksInAnyScopeWithoutReadPermission_AndRejectsUnknownOrDisabled()
    {
        var session = _sessions.Create("Estudio");
        var restricted = new AiToolContext { Scope = new AiAccessScope(session.Id, false), ReadAccess = new AiReadAccess(() => false, null) };
        Assert.Contains(_tools.AvailableTools(restricted), t => t.Name == "open_section");
        // Kanban cuelga del vault: un chat de sesión no puede abrir tableros, pero sí ir a la sección.
        Assert.DoesNotContain(_tools.AvailableTools(restricted), t => t.Name == "open_kanban_board");

        Json(await Call("open_section", new { section = "calendar" }, restricted));
        Assert.Equal(["calendar"], _openedSections);

        Assert.True((await Call("open_section", new { section = "assistant" })).IsError);
        _disabledSections.Add("habits");
        Assert.True((await Call("open_section", new { section = "habits" })).IsError);
        Assert.Equal(["calendar"], _openedSections);
    }

    // ===================== Manual =====================

    [Fact]
    public async Task SearchHelp_ReturnsTheUsersCurrentShortcut_NotTheDefault()
    {
        LocalizationService.Instance.SetLanguage("es-ES");
        try
        {
            ShortcutRegistry.Initialize(new Dictionary<string, string> { [ShortcutIds.ToggleAssistantPanel] = "Ctrl+Alt+J" });
            var remapped = Json(await Call("search_help", new { query = "atajo para abrir el panel del asistente", limit = 8 }));
            var text = string.Join("\n", remapped.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("content").GetString()));
            Assert.Equal("es", remapped.GetProperty("language").GetString());
            Assert.Contains(ShortcutRegistry.GetGestureDisplay(ShortcutIds.ToggleAssistantPanel), text);
            Assert.Contains("Ctrl+Alt+J", text);
        }
        finally
        {
            ShortcutRegistry.Initialize(new Dictionary<string, string>());
            LocalizationService.Instance.SetLanguage(SupportedLanguages.SourceCultureCode);
        }
    }

    [Fact]
    public async Task SearchHelp_NeedsNeitherVaultScopeNorReadPermission_AndListsTopicsWhenNothingMatches()
    {
        var session = _sessions.Create("Estudio");
        var restricted = new AiToolContext { Scope = new AiAccessScope(session.Id, false), ReadAccess = new AiReadAccess(() => false, null) };
        _settings.Settings.Ai.AllowVaultChanges = false;

        var found = Json(await Call("search_help", new { query = "daily note template" }, restricted));
        Assert.NotEmpty(found.GetProperty("sections").EnumerateArray());
        Assert.False(found.TryGetProperty("topics", out _));

        var nothing = Json(await Call("search_help", new { query = "zzzqqq" }, restricted));
        Assert.Empty(nothing.GetProperty("sections").EnumerateArray());
        Assert.NotEmpty(nothing.GetProperty("topics").EnumerateArray());
    }

    [Theory]
    [InlineData("es-ES", "es", "Nota diaria")]
    [InlineData("en-US", "en", "Daily note")]
    [InlineData("de-DE", "en", "Daily note")]
    public void HelpService_UsesTheManualOfTheInterfaceLanguage(string culture, string language, string expectedTitle)
    {
        LocalizationService.Instance.SetLanguage(culture);
        try
        {
            var result = AiHelpService.Search(language == "es" ? "nota diaria de hoy" : "today's daily note", 2);
            Assert.Equal(language, result.Language);
            Assert.Equal(expectedTitle, result.Sections[0].Title);
            // Los dos manuales tienen los mismos apartados: ninguno se queda sin traducir.
            Assert.True(result.Topics.Count > 20);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(SupportedLanguages.SourceCultureCode);
        }
    }

    [Fact]
    public void HelpService_Parse_SplitsByHeading_AndTermsIgnoreAccentsButKeepEnye()
    {
        var sections = AiHelpService.Parse("# Manual\n\nintro\n\n## Uno\n\ntexto uno\n\n## Dos\n\n- a\n- b\n\n## Vacío\n");
        Assert.Equal(["Uno", "Dos"], sections.Select(s => s.Title));
        Assert.Equal("texto uno", sections[0].Content);
        Assert.Equal(["anadir"], AiHelpService.Terms("¿Cómo anadir?"));
        Assert.Equal(["añadir", "pestaña"], AiHelpService.Terms("añadir una pestaña"));
        Assert.Equal(["atajo", "habito"], AiHelpService.Terms("los ATAJOS de hábitos"));
    }

    // ===================== Hábitos =====================

    [Fact]
    public async Task Habits_ListAndMark_UseTheSharedService()
    {
        var habit = _habits.AddHabit("Leer", HabitFrequency.Daily, []);
        _habits.AddHabit("Antiguo", HabitFrequency.Daily, []).Archived = true;

        var listed = Json(await Call("list_habits", new { }));
        var only = Assert.Single(listed.GetProperty("habits").EnumerateArray());
        Assert.Equal("Leer", only.GetProperty("name").GetString());
        Assert.False(only.GetProperty("done_today").GetBoolean());

        var marked = Json(await Call("set_habit_done", new { habit = "leer" }));
        Assert.True(marked.GetProperty("done").GetBoolean());
        Assert.True(HabitService.IsCompleted(habit, DailyNoteService.Today));
        // Lo que ve la app es lo mismo que guardó la herramienta: no hay otra copia.
        Assert.True(HabitService.IsCompleted(new HabitService(_vault).Store.Habits.Single(h => h.Id == habit.Id), DailyNoteService.Today));

        Json(await Call("set_habit_done", new { habit = habit.Id, done = false }));
        Assert.False(HabitService.IsCompleted(habit, DailyNoteService.Today));

        Assert.True((await Call("set_habit_done", new { habit = "Leer", date = "2999-01-01" })).IsError);
        Assert.True((await Call("set_habit_done", new { habit = "No existe" })).IsError);
    }

    [Fact]
    public async Task Habits_AreVaultModule_NotOfferedToSessionChats_NorWritableWhenReadOnly()
    {
        _habits.AddHabit("Leer", HabitFrequency.Daily, []);
        var session = _sessions.Create("Estudio");
        Assert.DoesNotContain(_tools.AvailableTools(Context(new AiAccessScope(session.Id, false))), t => t.Name.Contains("habit"));

        _settings.Settings.Ai.AllowVaultChanges = false;
        Assert.Contains(_tools.AvailableTools(Context()), t => t.Name == "list_habits");
        Assert.True((await Call("set_habit_done", new { habit = "Leer" })).IsError);
        Assert.Empty(_habits.Store.Habits[0].CompletedDates);
    }

    // ===================== Nota diaria y plantillas =====================

    [Fact]
    public async Task DailyNote_ReadDoesNotCreate_AppendCreatesOnceAndKeepsHistory()
    {
        var missing = Json(await Call("get_daily_note", new { }));
        Assert.False(missing.GetProperty("exists").GetBoolean());
        Assert.Equal(Today, missing.GetProperty("title").GetString());
        Assert.Empty(_vault.GetAllNotesRecursive(_vault.VaultPath));

        var created = Json(await Call("append_to_daily_note", new { text = "- Primera" }));
        Assert.True(created.GetProperty("created").GetBoolean());
        var note = Assert.Single(_vault.GetAllNotesRecursive(_vault.VaultPath));
        Assert.Equal(Today, note.FileNameWithoutExtension);

        var appended = Json(await Call("append_to_daily_note", new { text = "- Segunda" }));
        Assert.False(appended.GetProperty("created").GetBoolean());
        Assert.Single(_vault.GetAllNotesRecursive(_vault.VaultPath));
        var content = _vault.ReadContent(note);
        Assert.Contains("- Primera", content);
        Assert.EndsWith("- Segunda\n", content);
        // Añadir pasa por el camino de escritura de siempre: lo que había queda en el historial de la nota.
        Assert.Contains(_vault.GetVersionHistory(note), v => v.Trigger == NoteVersionTrigger.Assistant);

        var read = Json(await Call("get_daily_note", new { date = Today }));
        Assert.True(read.GetProperty("exists").GetBoolean());
        Assert.Contains("- Segunda", read.GetProperty("note").GetProperty("content").GetString());
    }

    [Fact]
    public async Task DailyNote_RefusesToOverwriteAnExternalChange()
    {
        Json(await Call("append_to_daily_note", new { text = "- Uno" }));
        var path = _vault.GetAllNotesRecursive(_vault.VaultPath).Single().FullPath;
        var blocking = new AiVaultToolbox(_vault, _sessions, _kanban, new PolarTaskService(_directory),
            new PolarAuthService(_settings, baseUrl: "https://test.example", anonKey: "test"), new ReminderService(_directory), _settings,
            host: new AiVaultHost { PrepareNoteChange = _ => false });
        var before = File.ReadAllText(path);

        var result = await blocking.ExecuteAsync("append_to_daily_note", JsonSerializer.SerializeToElement(new { text = "- Dos" }), Context());

        Assert.True(result.IsError);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public async Task Templates_ListAndCreateNote_ExpandVariablesAndRespectFolder()
    {
        Directory.CreateDirectory(_templates.TemplatesDirectory);
        File.WriteAllText(Path.Combine(_templates.TemplatesDirectory, "reunion.md"),
            "---\nname: \"Reunión\"\ndescription: \"Acta\"\ntitle: \"{{date}} · {{title}}\"\n---\n\n# {{title}}\n\nAsistentes:\n");
        Directory.CreateDirectory(Path.Combine(_vault.VaultPath, "Trabajo"));

        var listed = Json(await Call("list_note_templates", new { }));
        var template = Assert.Single(listed.GetProperty("templates").EnumerateArray());
        Assert.Equal("reunion.md", template.GetProperty("template").GetString());
        Assert.Equal("Reunión", template.GetProperty("name").GetString());

        var created = Json(await Call("create_note_from_template", new { template = "Reunión", title = "Equipo", folder = "Trabajo" }));
        var expectedTitle = $"{Today} · Equipo";
        Assert.Equal($"Trabajo/{expectedTitle}.md", created.GetProperty("path").GetString());
        var content = File.ReadAllText(Path.Combine(_vault.VaultPath, "Trabajo", expectedTitle + ".md"));
        Assert.StartsWith($"# {expectedTitle}", content);
        Assert.DoesNotContain("{{", content);
        Assert.DoesNotContain("name:", content);

        Assert.True((await Call("create_note_from_template", new { template = "No existe" })).IsError);
        Assert.True((await Call("create_note_from_template", new { template = "reunion.md", folder = "../fuera" })).IsError);
    }

    [Fact]
    public void EveryTool_HasAVisibleNameInEveryLanguage()
    {
        // La fila de actividad pinta "Assistant_Tool_<nombre>": una herramienta sin recurso saldría como una clave.
        foreach (var tool in _tools.AllTools)
            Assert.False(string.IsNullOrEmpty(Strings.ResourceManager.GetString("Assistant_Tool_" + tool.Name,
                System.Globalization.CultureInfo.GetCultureInfo("es-ES"))), tool.Name);
    }
}
