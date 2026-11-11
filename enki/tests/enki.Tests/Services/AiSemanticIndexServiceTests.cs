using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Búsqueda semántica local (AIA-16): índice incremental de embeddings con un Ollama simulado que entiende
/// sinónimos, guardado fuera del vault y sin el texto de las notas, y la herramienta <c>semantic_search</c> dentro del
/// ámbito del chat.</summary>
public sealed class AiSemanticIndexServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly VaultService _vault;
    private readonly SessionService _sessions;
    private readonly FakeOllama _ollama = new();
    private readonly AiSemanticIndexService _index;

    public AiSemanticIndexServiceTests()
    {
        _settings = new SettingsService(_directory);
        _settings.Settings.Ai.SemanticSearchEnabled = true;
        _vault = new VaultService(_settings);
        _vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        _sessions = new SessionService(_vault);
        _index = new AiSemanticIndexService(_settings, _vault, _sessions, Path.Combine(_directory, "install"), new HttpClient(_ollama));
    }

    public void Dispose()
    {
        _index.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private string IndexFile => Directory.GetFiles(Path.Combine(_directory, "install", "ai", "semantic"), "*.bin").Single();

    [Fact]
    public async Task Search_FindsNotesBySynonyms()
    {
        _vault.CreateNote("Taller", initialContent: "# Taller\n\nLlevar el coche a revisar los frenos el lunes.\n");
        _vault.CreateNote("Veterinario", initialContent: "# Veterinario\n\nVacunas del perro y comida para la mascota.\n");
        _vault.CreateNote("Cuentas", initialContent: "# Cuentas\n\nPresupuesto de gastos del trimestre.\n");

        await _index.RefreshAsync(CancellationToken.None);
        var hits = await _index.SearchAsync("automóvil", 3, _ => true, CancellationToken.None);

        Assert.Equal(AiSemanticIndexState.Ready, _index.Status.State);
        Assert.Equal(3, _index.Status.Notes);
        Assert.Equal("Taller", hits[0].Title);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public async Task Refresh_IsIncremental_AndIgnoresTouchesWithoutChanges()
    {
        var one = _vault.CreateNote("Uno", initialContent: "# Uno\n\nNotas sobre el coche nuevo y su seguro.\n");
        _vault.CreateNote("Dos", initialContent: "# Dos\n\nPaseos con el perro por el parque cada mañana.\n");
        await _index.RefreshAsync(CancellationToken.None);
        var firstInputs = _ollama.EmbeddedInputs;

        // Solo cambia la fecha: no se recalcula nada.
        File.SetLastWriteTimeUtc(one.FullPath, DateTime.UtcNow.AddMinutes(5));
        await _index.RefreshAsync(CancellationToken.None);
        Assert.Equal(firstInputs, _ollama.EmbeddedInputs);

        // Cambia el contenido de una nota: solo se recalcula esa.
        File.WriteAllText(one.FullPath, "# Uno\n\nAhora habla del presupuesto y los gastos del año.\n");
        await _index.RefreshAsync(CancellationToken.None);
        Assert.Equal(firstInputs + 1, _ollama.EmbeddedInputs);
        var hits = await _index.SearchAsync("dinero", 1, _ => true, CancellationToken.None);
        Assert.Equal("Uno", hits[0].Title);

        // Una nota borrada sale del índice.
        File.Delete(one.FullPath);
        await _index.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, _index.Status.Notes);
    }

    [Fact]
    public async Task Index_LivesOutsideTheVault_WithoutNoteText_AndSkipsProtectedSessions()
    {
        _vault.CreateNote("Pública", initialContent: "# Pública\n\nPalabraUnicaXyz sobre el coche.\n");
        var (secret, _) = _sessions.CreateEncrypted("Privado", "super-secreta-123");
        _vault.CreateNote("Oculta", secret.RootPath, "# Oculta\n\nEl perro secreto.\n");

        await _index.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, _index.Status.Notes);
        Assert.False(VaultService.IsWithinFolder(IndexFile, _vault.VaultPath));
        Assert.DoesNotContain("PalabraUnicaXyz", Encoding.UTF8.GetString(File.ReadAllBytes(IndexFile)));
        Assert.DoesNotContain(_ollama.Inputs, input => input.Contains("secreto", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Index_SurvivesARestart_AndDeleteLeavesNotesAlone()
    {
        var note = _vault.CreateNote("Taller", initialContent: "# Taller\n\nEl coche en el taller.\n");
        await _index.RefreshAsync(CancellationToken.None);

        using (var reopened = new AiSemanticIndexService(_settings, _vault, _sessions, Path.Combine(_directory, "install"), new HttpClient(_ollama)))
        {
            Assert.True(reopened.IsReady);
            Assert.Equal(1, reopened.Status.Notes);
        }

        var file = IndexFile;
        _index.DeleteIndex();
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(note.FullPath));
        Assert.Equal(AiSemanticIndexState.Empty, _index.Status.State);
    }

    [Fact]
    public async Task MissingOrRemoteModel_IsReported_WithoutIndexing()
    {
        _vault.CreateNote("Taller", initialContent: "# Taller\n\nEl coche.\n");
        _ollama.Show = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        await _index.RefreshAsync(CancellationToken.None);
        Assert.Equal(AiSemanticProblem.ModelMissing, _index.Status.Problem);

        _ollama.Show = _ => FakeOllama.Json(new JsonObject { ["remote_host"] = "https://ollama.com", ["capabilities"] = new JsonArray("embedding") });
        await _index.RefreshAsync(CancellationToken.None);
        Assert.Equal(AiSemanticProblem.RemoteModel, _index.Status.Problem);

        _ollama.Show = _ => FakeOllama.Json(new JsonObject { ["capabilities"] = new JsonArray("completion") });
        await _index.RefreshAsync(CancellationToken.None);
        Assert.Equal(AiSemanticProblem.NotEmbeddingModel, _index.Status.Problem);
        Assert.Equal(0, _ollama.EmbeddedInputs);
        Assert.False(_index.IsReady);
    }

    [Fact]
    public async Task ChangingTheModel_RebuildsTheIndex()
    {
        _vault.CreateNote("Taller", initialContent: "# Taller\n\nEl coche.\n");
        await _index.RefreshAsync(CancellationToken.None);
        var before = _ollama.EmbeddedInputs;

        _settings.Settings.Ai.EmbeddingModel = "mxbai-embed-large";
        Assert.False(_index.IsReady);
        await _index.RefreshAsync(CancellationToken.None);

        Assert.True(_index.IsReady);
        Assert.Equal(before * 2, _ollama.EmbeddedInputs);
    }

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        _settings.Settings.Ai.SemanticSearchEnabled = false;
        _vault.CreateNote("Taller", initialContent: "# Taller\n\nEl coche.\n");
        await _index.RefreshAsync(CancellationToken.None);
        Assert.Equal(0, _ollama.Requests);
        Assert.False(_index.IsReady);
    }

    // ===================== Herramienta =====================

    [Fact]
    public async Task SemanticSearchTool_OnlyWhenEnabled_AndWithinTheChatScope()
    {
        _vault.CreateNote("Taller", initialContent: "# Taller\n\n## Revisión\n\nLlevar el coche a revisar los frenos.\n");
        var session = _sessions.Create("Privado");
        _vault.CreateNote("Furgoneta", session.RootPath, "# Furgoneta\n\nOtro vehículo, el de la sesión.\n");
        var tools = new AiVaultToolbox(_vault, _sessions, new KanbanService(_directory), new PolarTaskService(_directory),
            new PolarAuthService(_settings, baseUrl: "https://test.example", anonKey: "test"), new ReminderService(_directory), _settings,
            semanticIndex: _index);
        var context = new AiToolContext { Scope = AiAccessScope.Vault };

        var outcome = await tools.ExecuteAsync("semantic_search", JsonSerializer.SerializeToElement(new { query = "automóvil" }), context);
        Assert.False(outcome.IsError, outcome.Text);
        var results = JsonSerializer.Deserialize<JsonElement>(outcome.Text).GetProperty("results").EnumerateArray().ToList();
        Assert.Equal("Taller", results[0].GetProperty("title").GetString());
        Assert.Equal("Revisión", results[0].GetProperty("section").GetString());
        Assert.Contains("frenos", results[0].GetProperty("snippet").GetString());
        // La sesión está indexada (no tiene contraseña), pero no en el ámbito de este chat.
        Assert.DoesNotContain(results, r => r.GetProperty("title").GetString() == "Furgoneta");

        _settings.Settings.Ai.SemanticSearchEnabled = false;
        Assert.DoesNotContain(tools.AvailableTools(context), t => t.Name == "semantic_search");
    }

    [Fact]
    public async Task SemanticSearchTool_WithOllamaDown_FailsWithAClearMessage()
    {
        _vault.CreateNote("Taller", initialContent: "# Taller\n\nEl coche.\n");
        _ollama.Show = _ => throw new HttpRequestException("down");
        var tools = new AiVaultToolbox(_vault, _sessions, new KanbanService(_directory), new PolarTaskService(_directory),
            new PolarAuthService(_settings, baseUrl: "https://test.example", anonKey: "test"), new ReminderService(_directory), _settings,
            semanticIndex: _index);

        var outcome = await tools.ExecuteAsync("semantic_search", JsonSerializer.SerializeToElement(new { query = "coche" }),
            new AiToolContext { Scope = AiAccessScope.Vault });

        Assert.True(outcome.IsError);
        Assert.Contains("search_notes", outcome.Text);
    }

    // ===================== Troceado =====================

    [Fact]
    public void Chunker_SkipsFrontMatter_KeepsHeadings_AndSplitsLongText()
    {
        var content = "---\ntags: [a]\n---\n# Título\n\nIntroducción de la nota con bastante texto.\n\n## Sección\n\n"
            + new string('x', 3_500) + "\n";

        var chunks = AiSemanticChunker.Split(content, maxChars: 1_500);

        Assert.DoesNotContain(chunks, c => content.Substring(c.Start, c.Length).Contains("tags:", StringComparison.Ordinal));
        Assert.Equal("Título", chunks[0].Heading);
        Assert.All(chunks, c => Assert.True(c.Length <= 1_500));
        Assert.Equal(3, chunks.Count(c => c.Heading == "Sección"));
    }

    [Fact]
    public void Chunker_IndexesAVeryShortNote()
    {
        var chunks = AiSemanticChunker.Split("# Hola\n");
        Assert.Single(chunks);
    }

    /// <summary>Ollama de mentira: <c>/api/show</c> configurable y <c>/api/embed</c> con vectores de "conceptos" en los
    /// que los sinónimos comparten dimensión.</summary>
    private sealed class FakeOllama : HttpMessageHandler
    {
        private static readonly string[][] Concepts =
        [
            ["coche", "automóvil", "vehículo", "frenos", "taller", "furgoneta"],
            ["perro", "mascota", "veterinario", "vacunas", "paseos"],
            ["presupuesto", "gastos", "dinero", "cuentas"]
        ];

        public Func<HttpRequestMessage, HttpResponseMessage> Show { get; set; } =
            _ => Json(new JsonObject { ["capabilities"] = new JsonArray("embedding") });

        public int Requests { get; private set; }
        public int EmbeddedInputs { get; private set; }
        public List<string> Inputs { get; } = [];

        public static HttpResponseMessage Json(JsonNode body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("localhost", request.RequestUri!.Host);
            if (request.RequestUri.AbsolutePath == "/api/show") return Show(request);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var inputs = body["input"]!.AsArray().Select(i => i!.GetValue<string>()).ToList();
            EmbeddedInputs += inputs.Count;
            Inputs.AddRange(inputs);
            return Json(new JsonObject { ["embeddings"] = new JsonArray(inputs.Select(i => (JsonNode)Vector(i)).ToArray()) });
        }

        private static JsonArray Vector(string text)
        {
            var lower = text.ToLowerInvariant();
            var values = Concepts.Select(words => (double)words.Count(w => lower.Contains(w, StringComparison.Ordinal))).ToList();
            values.Add(0.05);
            return new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());
        }
    }
}
