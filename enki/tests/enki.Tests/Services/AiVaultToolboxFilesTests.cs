using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary><c>read_file</c> sobre servicios reales: formatos, imágenes en el resultado y por MCP, y el permiso de
/// consulta frente a los archivos adjuntos.</summary>
public sealed class AiVaultToolboxFilesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly VaultService _vault;
    private readonly AiVaultToolbox _tools;

    public AiVaultToolboxFilesTests()
    {
        var settings = new SettingsService(_directory);
        _vault = new VaultService(settings);
        _vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        var sessions = new SessionService(_vault);
        _tools = new AiVaultToolbox(_vault, sessions, new KanbanService(_directory), new PolarTaskService(_directory),
            new PolarAuthService(settings, baseUrl: "https://test.example", anonKey: "test"), new ReminderService(_directory), settings);
        AiFileReader.ClearCache();
    }

    public void Dispose()
    {
        AiFileReader.ClearCache();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_vault.VaultPath, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private Task<AiToolOutcome> Read(object args, AiToolContext? context = null) =>
        _tools.ExecuteAsync("read_file", JsonSerializer.SerializeToElement(args), context ?? new AiToolContext { Scope = AiAccessScope.Vault });

    private static JsonElement Json(AiToolOutcome outcome)
    {
        Assert.False(outcome.IsError, outcome.Text);
        return JsonSerializer.Deserialize<JsonElement>(outcome.Text);
    }

    [Fact]
    public async Task Pdf_IsReadAsText_InChunks()
    {
        Write("informe.pdf", AiFileReaderTests.Pdf("Primera pagina del informe trimestral", "Segunda pagina con las conclusiones"));
        var result = Json(await Read(new { path = "informe.pdf", pages = "2" }));
        Assert.Equal("pdf", result.GetProperty("format").GetString());
        Assert.Equal(2, result.GetProperty("page_count").GetInt32());
        Assert.Contains("conclusiones", result.GetProperty("content").GetString());
        Assert.DoesNotContain("trimestral", result.GetProperty("content").GetString());
        Assert.False(result.TryGetProperty("next_offset", out _));

        var chunk = Json(await Read(new { path = "informe.pdf", max_chars = 500 }));
        Assert.True(chunk.GetProperty("total_chars").GetInt32() > 0);
    }

    [Fact]
    public async Task Image_TravelsAsAnImage_NotAsText()
    {
        Write("foto.png", AiFileReaderTests.Png(2000, 1000));
        var outcome = await Read(new { path = "foto.png", detail = "low" });
        var result = Json(outcome);
        Assert.Equal("image", result.GetProperty("format").GetString());
        Assert.Equal(2000, result.GetProperty("image").GetProperty("width").GetInt32());
        Assert.Equal(AiFileReader.LowImageSide, result.GetProperty("image").GetProperty("sent_width").GetInt32());
        Assert.False(result.TryGetProperty("content", out _));
        Assert.Equal("image/jpeg", Assert.Single(outcome.Images!).MimeType);
    }

    [Fact]
    public async Task Spreadsheet_IsCompact()
    {
        Write("cuentas.xlsx", AiFileReaderTests.Zip(
            ("xl/sharedStrings.xml", "<sst><si><t>Total</t></si></sst>"),
            ("xl/worksheets/sheet1.xml", "<worksheet><sheetData><row r='1'><c r='A1' t='s'><v>0</v></c><c r='B1'><v>42</v></c></row></sheetData></worksheet>")));
        var content = Json(await Read(new { path = "cuentas.xlsx" })).GetProperty("content").GetString();
        Assert.Contains("1\tTotal\t42", content);
    }

    [Fact]
    public async Task Unreadable_OrInvalidRequests_ComeBackAsErrors()
    {
        Write("cancion.mp3", [0x49, 0x44, 0x33]);
        Write("notas.txt", Encoding.UTF8.GetBytes("hola"));
        Assert.Contains("cannot be read", (await Read(new { path = "cancion.mp3" })).Text);
        Assert.True((await Read(new { path = "notas.txt", pages = "1" })).IsError);
        Assert.True((await Read(new { path = "nada.pdf" })).IsError);
    }

    [Fact]
    public async Task AttachedFiles_CanBeReadWithoutTheChatReadPermission_OthersCannot()
    {
        var attached = Write("adjunta.png", AiFileReaderTests.Png(20, 20));
        Write("otra.png", AiFileReaderTests.Png(20, 20));
        var context = new AiToolContext
        {
            Scope = AiAccessScope.Vault,
            ReadAccess = new AiReadAccess(() => false, null),
            AttachedFiles = new HashSet<string> { attached }
        };
        Assert.Single((await Read(new { path = "adjunta.png" }, context)).Images!);
        Assert.Single((await Read(new { workspace = "vault", path = "adjunta.png" }, context)).Images!);
        var denied = await Read(new { path = "otra.png" }, context);
        Assert.True(denied.IsError);
        Assert.Null(denied.Images);
    }

    [Fact]
    public async Task Mcp_SendsImagesAsImageContent_AndTheClientReturnsThem()
    {
        Write("foto.png", AiFileReaderTests.Png(64, 32));
        using var server = new EnkiMcpServer(_tools);
        var access = server.Register(new AiToolContext { Scope = AiAccessScope.Vault });
        var result = await EnkiMcpClient.CallToolAsync(EnkiMcpClient.LoopbackHttp, access, "read_file",
            new JsonObject { ["path"] = "foto.png" }, CancellationToken.None);
        Assert.False(result.IsError, result.Text);
        Assert.Contains("\"format\":\"image\"", result.Text);
        var image = Assert.Single(result.Images);
        Assert.Equal("image/jpeg", image.MimeType);
        Assert.Equal(0xFF, Convert.FromBase64String(image.Base64)[0]);
        server.Stop();
    }
}
