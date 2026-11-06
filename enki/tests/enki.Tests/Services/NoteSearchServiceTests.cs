using System;
using System.IO;
using System.Linq;
using System.Threading;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre la búsqueda de texto completo sobre el vault. El caso que da sentido a todo el servicio
/// es <see cref="Search_FindsTextBeyondThePreviewWindow"/>: hasta ahora las cuatro superficies de búsqueda
/// de la app comparaban contra <c>NoteFile.Preview</c> —los primeros 160 caracteres del cuerpo— así que
/// cualquier cosa escrita más adelante en una nota era imposible de encontrar desde la aplicación.
///
/// <para>Mismo patrón de directorio temporal por test que el resto de tests de <c>Services</c>.</para></summary>
public class NoteSearchServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public NoteSearchServiceTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (VaultService Vault, SessionService Sessions, NoteSearchService Search) CreateServices()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        return (vault, new SessionService(vault), new NoteSearchService(vault));
    }

    private static NoteFile WriteNote(VaultService vault, string title, string body, string? folder = null)
    {
        var note = vault.CreateNote(title, folder);
        vault.WriteContent(note, $"# {title}\n\n{body}");
        return note;
    }

    // ===================== El agujero que cierra este servicio =====================

    [Fact]
    public void Search_FindsTextBeyondThePreviewWindow()
    {
        var (vault, _, search) = CreateServices();
        // NoteFile.Preview corta a 160 caracteres: este relleno deja la frase buscada muy por detrás.
        var relleno = string.Join(" ", Enumerable.Repeat("relleno", 60));
        WriteNote(vault, "Notas de reunion", $"{relleno}\n\nacordamos migrar el backend en octubre");

        var hits = search.Search("migrar el backend");

        var hit = Assert.Single(hits);
        Assert.Equal("Notas de reunion", hit.Note.Title);
        Assert.Equal(NoteSearchField.Content, hit.Field);
        Assert.DoesNotContain("migrar el backend", hit.Note.Preview, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Search_ReportsWhereTheMatchIs_SoTheEditorCanJumpToIt()
    {
        var (vault, _, search) = CreateServices();
        var note = WriteNote(vault, "Diario", "primera linea\nsegunda linea\ntercera con la palabra clave aqui");

        var hit = Assert.Single(search.Search("palabra clave"));

        var content = vault.ReadContent(note);
        Assert.Equal(NoteSearchField.Content, hit.Field);
        Assert.Equal(content.IndexOf("palabra clave", StringComparison.Ordinal), hit.Offset);
        Assert.Equal(5, hit.LineNumber); // "# Diario", "", "primera", "segunda", "tercera"
        Assert.Contains("palabra clave", hit.Snippet);
    }

    [Fact]
    public void Search_SnippetStaysWithinTheMatchingLine_AndIsTrimmedAroundTheMatch()
    {
        var (vault, _, search) = CreateServices();
        var largo = string.Join(" ", Enumerable.Repeat("palabreja", 40));
        WriteNote(vault, "Larga", $"{largo} aguja {largo}\notra linea distinta");

        var hit = Assert.Single(search.Search("aguja"));

        Assert.Contains("aguja", hit.Snippet);
        Assert.DoesNotContain("otra linea distinta", hit.Snippet);
        Assert.StartsWith("…", hit.Snippet, StringComparison.Ordinal);
        Assert.EndsWith("…", hit.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_CountsEveryOccurrence_ButReturnsOneEntryPerNote()
    {
        var (vault, _, search) = CreateServices();
        WriteNote(vault, "Repetida", "eco\nmas texto\neco\ny otra vez eco");

        var hit = Assert.Single(search.Search("eco"));

        Assert.Equal(3, hit.MatchCount);
    }

    // ===================== Alcance: sesiones =====================

    [Fact]
    public void Search_IncludesNotesLivingInsideSessions()
    {
        var (vault, sessions, search) = CreateServices();
        var session = sessions.Create("Proyecto");
        WriteNote(vault, "Dentro de la sesion", "el texto que busco", session.RootPath);

        var hit = Assert.Single(search.Search("el texto que busco"));

        Assert.Equal("Dentro de la sesion", hit.Note.Title);
    }

    [Fact]
    public void Search_ExcludesNotesOfALockedSession()
    {
        var (vault, sessions, search) = CreateServices();
        var (privada, _) = sessions.CreateEncrypted("Privada", "contrasena-larga-1");
        WriteNote(vault, "Secreta", "confidencial y unico", privada.RootPath);
        WriteNote(vault, "Publica", "confidencial y unico");

        Assert.Equal(2, search.Search("confidencial y unico").Count);

        sessions.Lock(privada);

        var hit = Assert.Single(search.Search("confidencial y unico"));
        Assert.Equal("Publica", hit.Note.Title);
    }

    // ===================== Orden y campos =====================

    [Fact]
    public void Search_RanksTitleMatchesAboveTagMatchesAboveContentMatches()
    {
        var (vault, _, search) = CreateServices();
        WriteNote(vault, "Cuerpo", "aqui aparece avalonia en el texto");
        WriteNote(vault, "Avalonia en el titulo", "sin coincidencia en el cuerpo");
        // Las etiquetas viven en el frontmatter YAML (NoteTagHelper), no como "#tag" suelto en el cuerpo.
        var etiquetada = vault.CreateNote("Etiquetada", null);
        vault.WriteContent(etiquetada, "---\ntags: [avalonia]\n---\n# Etiquetada\n\nsin coincidencia en el cuerpo");

        var hits = search.Search("avalonia");

        Assert.Equal(3, hits.Count);
        Assert.Equal(NoteSearchField.Title, hits[0].Field);
        Assert.Equal(NoteSearchField.Tag, hits[1].Field);
        Assert.Equal(NoteSearchField.Content, hits[2].Field);
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        var (vault, _, search) = CreateServices();
        WriteNote(vault, "Mayusculas", "CONTENIDO EN MAYUSCULAS");

        Assert.Single(search.Search("contenido en mayusculas"));
    }

    [Fact]
    public void Search_WithAnEmptyQuery_ReturnsNothing()
    {
        var (vault, _, search) = CreateServices();
        WriteNote(vault, "Cualquiera", "algo");

        Assert.Empty(search.Search("   "));
    }

    [Fact]
    public void Search_RespectsTheResultCap()
    {
        var (vault, _, search) = CreateServices();
        for (var i = 0; i < 10; i++) WriteNote(vault, $"Nota {i}", "coincidencia comun");

        Assert.Equal(4, search.Search("coincidencia comun", maxResults: 4).Count);
    }

    [Fact]
    public void Search_HonoursCancellation()
    {
        var (vault, _, search) = CreateServices();
        WriteNote(vault, "Cualquiera", "algo");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => search.Search("algo", token: cts.Token));
    }

    // ===================== Alcances acotados =====================

    [Fact]
    public void SearchFolder_LooksOnlyInsideThatFolder_NotItsSubfolders()
    {
        var (vault, _, search) = CreateServices();
        var subfolder = VaultService.CreateFolder(_vaultPath, "Subcarpeta");
        WriteNote(vault, "Arriba", "termino compartido");
        WriteNote(vault, "Abajo", "termino compartido", subfolder.FullPath);

        var hit = Assert.Single(search.SearchFolder(_vaultPath, "termino compartido"));

        Assert.Equal("Arriba", hit.Note.Title);
    }

    [Fact]
    public void SearchTree_LooksInsideSubfolders_ButNotOutsideTheGivenRoot()
    {
        var (vault, sessions, search) = CreateServices();
        var session = sessions.Create("Proyecto");
        var subfolder = VaultService.CreateFolder(session.RootPath, "Subcarpeta");
        WriteNote(vault, "Fuera", "termino compartido");
        WriteNote(vault, "Dentro", "termino compartido", subfolder.FullPath);

        var hit = Assert.Single(search.SearchTree(session.RootPath, "termino compartido"));

        Assert.Equal("Dentro", hit.Note.Title);
    }
}
