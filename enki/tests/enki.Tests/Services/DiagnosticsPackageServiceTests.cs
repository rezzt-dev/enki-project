using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Paquete de diagnóstico (MED-08): lo que entra, lo que nunca debe entrar y cómo se escribe.
/// Todo en carpetas temporales; los «secretos» son valores inventados.</summary>
public sealed class DiagnosticsPackageServiceTests : IDisposable
{
    private const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enki-diag-" + Guid.NewGuid().ToString("N"));
    private readonly string _vault;
    private readonly string _logs;

    public DiagnosticsPackageServiceTests()
    {
        _vault = Path.Combine(_root, "Mis Notas Privadas");
        _logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(_vault);
        Directory.CreateDirectory(_logs);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private AppSettings Settings() => new()
    {
        VaultPath = _vault,
        Language = "es-ES",
        Theme = "Dark",
        EditorFontSize = 15,
        DebugLoggingEnabled = true,
        PolarLinked = true,
        PolarEmail = "persona@ejemplo.org",
        PolarUserId = "11111111-2222-3333-4444-555555555555",
        PolarAccessToken = Jwt,
        PolarRefreshToken = "refresh-secreto-abcdefghijklmnopqrstuvwxyz0123456789",
        RecentVaults = [new RecentVault { Path = _vault }],
        OpenNotePaths = [Path.Combine(_vault, "Diario secreto.md")],
        Ai = new AiSettings { CodexCliPath = "/usr/local/bin/codex", OllamaEndpoint = "http://usuario:clave@localhost:11434/api?key=abc" }
    };

    private static Dictionary<string, string> Read(DiagnosticsPackage package) =>
        package.Files.ToDictionary(f => f.Name, f => f.Content);

    private static string All(DiagnosticsPackage package) => string.Join("\n", package.Files.Select(f => f.Name + "\n" + f.Content));

    // ── contenido ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_ContainsVersionSystemSettingsAndLogs()
    {
        File.WriteAllText(Path.Combine(_logs, "enki-20261106.log"), "2026-11-06 10:00:00.000 +01:00 [ERROR] enki 1.0.0.0 | Algo falló\n");

        var package = new DiagnosticsPackageService(Settings(), _logs).Build();

        Assert.Equal(["LEEME.txt", "resumen.txt", "ajustes.json", "registros.txt"], package.Files.Select(f => f.Name));
        var files = Read(package);
        Assert.Contains("Enki:", files["resumen.txt"]);
        Assert.Contains("Sistema:", files["resumen.txt"]);
        Assert.Contains(".NET:", files["resumen.txt"]);
        Assert.Contains("Vault configurado: si", files["resumen.txt"]);
        Assert.Contains("\"Theme\": \"Dark\"", files["ajustes.json"]);
        Assert.Contains("\"EditorFontSize\": 15", files["ajustes.json"]);
        Assert.Contains("Algo falló", files["registros.txt"]);
        Assert.Contains("No contiene notas", files["LEEME.txt"]);
    }

    [Fact]
    public void Build_NeverContainsNotesVaultPathsKeysOrPersonalData()
    {
        var log = string.Join('\n',
            $"2026-11-06 10:00:00 [ERROR] No se pudo escribir '{Path.Combine(_vault, "Diario secreto.md")}'",
            $"2026-11-06 10:00:01 [WARNING] Vault en {_vault}/Proyectos/Plan de despido.md falló",
            $"2026-11-06 10:00:02 [WARNING] Token {Jwt} caducado",
            "2026-11-06 10:00:03 [WARNING] Authorization: Bearer abcdefghijklmnop1234567890",
            "2026-11-06 10:00:04 [WARNING] apikey=sk-abcdefghijklmnopqrstuvwxyz0123456789 rechazada",
            "2026-11-06 10:00:05 [WARNING] Correo persona@ejemplo.org no válido",
            "2026-11-06 10:00:06 [WARNING] GET https://api.ejemplo.org/rest/v1/tasks?select=*&apikey=zzz&token=yyy 401",
            "2026-11-06 10:00:07 [WARNING] Archivo C:\\Users\\Persona\\Documents\\Mis Notas\\Secreto.md y /home/persona/Documentos/otro.md",
            "2026-11-06 10:00:08 [WARNING] id 0a1b2c3d-1111-2222-3333-444455556666 y cadena aGVsbG8gd29ybGQgdGhpcyBpcyBhIGxvbmcgc2VjcmV0IHN0cmluZw==");
        File.WriteAllText(Path.Combine(_logs, "enki-20261106.log"), log);

        var package = new DiagnosticsPackageService(Settings(), _logs).Build();
        var text = All(package);

        foreach (var forbidden in new[]
                 {
                     _vault, "Mis Notas Privadas", "Diario secreto", "Plan de despido", "persona@ejemplo.org", Jwt,
                     "refresh-secreto", "abcdefghijklmnop1234567890", "sk-abcdefghijklmnop", "apikey=zzz", "token=yyy",
                     "C:\\Users\\Persona", "Secreto.md", "/home/persona", "otro.md", "usuario:clave", "11111111-2222-3333-4444-555555555555",
                     "0a1b2c3d-1111", "aGVsbG8gd29ybGQ", Environment.UserName.Length >= 3 ? Environment.UserName : "\u0000"
                 })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("<vault>", text, StringComparison.Ordinal);
        Assert.Contains("<ruta>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_AnyStringOrListWithPersonalShape_IsDroppedWholesale()
    {
        // Todas las propiedades de texto, de lista y de diccionario con un valor «personal»: nada de eso puede
        // salir, se llame la propiedad como se llame (también las que se añadan en el futuro).
        var settings = new AppSettings();
        FillWithCanary(settings, depth: 0);

        var json = Read(new DiagnosticsPackageService(settings, null).Build())["ajustes.json"];

        Assert.DoesNotContain("CANARY", json, StringComparison.OrdinalIgnoreCase);
    }

    private static void FillWithCanary(object target, int depth)
    {
        const string canary = "CANARY@ejemplo.org/CANARY";
        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            var type = property.PropertyType;
            try
            {
                if (type == typeof(string)) property.SetValue(target, canary);
                else if (type == typeof(List<string>)) property.SetValue(target, new List<string> { canary });
                else if (type == typeof(Dictionary<string, string>)) property.SetValue(target, new Dictionary<string, string> { [canary] = canary });
                else if (type.IsClass && type.GetConstructor(Type.EmptyTypes) is not null && !type.IsGenericType && depth < 2
                         && property.GetValue(target) is { } child)
                    FillWithCanary(child, depth + 1);
            }
            catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or InvalidCastException) { /* propiedad con validación: se omite */ }
        }
    }

    [Fact]
    public void Settings_KeepOnlyHarmlessScalars()
    {
        var json = Read(new DiagnosticsPackageService(Settings(), null).Build())["ajustes.json"];

        Assert.DoesNotContain("PolarEmail", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PolarAccessToken", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PolarRefreshToken", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PolarUserId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("VaultPath", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RecentVaults", json, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenNotePaths", json, StringComparison.Ordinal);
        Assert.DoesNotContain("CodexCliPath", json, StringComparison.Ordinal);
        Assert.DoesNotContain("OllamaEndpoint", json, StringComparison.Ordinal);
        Assert.Contains("\"Language\": \"es-ES\"", json);
    }

    // ── registros ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Logs_AreTrimmedToTheLatestLines_AcrossFilesNewestLast()
    {
        File.WriteAllLines(Path.Combine(_logs, "enki-20261104.log"), Enumerable.Range(0, 50).Select(i => $"viejo {i}"));
        File.WriteAllLines(Path.Combine(_logs, "enki-20261106.log"), Enumerable.Range(0, SpellOutMax()).Select(i => $"reciente {i}"));

        var logs = Read(new DiagnosticsPackageService(Settings(), _logs).Build())["registros.txt"];
        var lines = logs.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.True(lines.Length <= DiagnosticsPackageService.MaxLogLines);
        Assert.Contains($"reciente {SpellOutMax() - 1}", logs);
        Assert.DoesNotContain("viejo 0", logs);
        Assert.True(Encoding.UTF8.GetByteCount(logs) <= DiagnosticsPackageService.MaxLogBytes + 4);
    }

    private static int SpellOutMax() => DiagnosticsPackageService.MaxLogLines + 500;

    [Fact]
    public void Logs_AreCappedInBytes_KeepingTheNewest()
    {
        var big = Enumerable.Range(0, 3000).Select(i => $"línea {i} " + new string('x', 200));
        File.WriteAllLines(Path.Combine(_logs, "enki-20261106.log"), big);

        var logs = Read(new DiagnosticsPackageService(Settings(), _logs).Build())["registros.txt"];

        Assert.True(Encoding.UTF8.GetByteCount(logs) <= DiagnosticsPackageService.MaxLogBytes + 4);
        Assert.Contains("línea 2999", logs);
        Assert.DoesNotContain("línea 0 ", logs);
    }

    [Fact]
    public void StackTraces_KeepSourceFileNamesAndLineNumbers_ButNotFolders()
    {
        File.WriteAllText(Path.Combine(_logs, "enki-20261106.log"),
            "[ERROR] Fallo\nSystem.IOException: x\n   at enki.Services.VaultService.WriteContent() in /home/persona/proyectos/enki/src/enki/services/VaultService.cs:line 123\n");

        var logs = Read(new DiagnosticsPackageService(Settings(), _logs).Build())["registros.txt"];

        Assert.Contains("in VaultService.cs:line 123", logs);
        Assert.DoesNotContain("/home/persona", logs);
        Assert.DoesNotContain("proyectos", logs);
    }

    [Fact]
    public void NoLogs_ProducesAPlaceholderInsteadOfFailing()
    {
        var withoutFolder = new DiagnosticsPackageService(Settings(), null).Build();
        var emptyFolder = new DiagnosticsPackageService(Settings(), _logs).Build();

        Assert.Contains("sin registros", Read(withoutFolder)["registros.txt"]);
        Assert.Contains("sin registros", Read(emptyFolder)["registros.txt"]);
    }

    [Fact]
    public void ExtraSensitiveValues_AreHiddenWhereverTheyAppear()
    {
        File.WriteAllText(Path.Combine(_logs, "enki-20261106.log"), "[WARNING] contraseña de sesión: tortuga-azul-77 en uso\n");

        var package = new DiagnosticsPackageService(Settings(), _logs, ["tortuga-azul-77"]).Build();

        Assert.DoesNotContain("tortuga-azul-77", All(package));
    }

    // ── zip ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WriteZip_ContainsExactlyTheReviewedFiles_AndLeavesNoTemporaryFile()
    {
        File.WriteAllText(Path.Combine(_logs, "enki-20261106.log"), "[ERROR] ñandú y diseño\n");
        var package = new DiagnosticsPackageService(Settings(), _logs).Build();
        var destination = Path.Combine(_root, "diagnostico.zip");

        package.WriteZip(destination);

        using var archive = ZipFile.OpenRead(destination);
        Assert.Equal(package.Files.Select(f => f.Name).Order(), archive.Entries.Select(e => e.FullName).Order());
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            Assert.Equal(package.Files.Single(f => f.Name == entry.FullName).Content, reader.ReadToEnd());
        }
        Assert.Contains("ñandú y diseño", package.Files.Single(f => f.Name == "registros.txt").Content);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public void WriteZip_ToAnUnwritableLocation_FailsWithoutLeavingFiles()
    {
        var package = new DiagnosticsPackageService(Settings(), null).Build();

        Assert.ThrowsAny<IOException>(() => package.WriteZip(Path.Combine(_root, "no-existe", "x.zip")));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Scrub_IsIdempotentAndLeavesPlainTextAlone()
    {
        var service = new DiagnosticsPackageService(Settings(), null);
        const string plain = "Se guardó la nota correctamente en 12 ms (3 etiquetas)";

        Assert.Equal(plain, service.Scrub(plain));
        var once = service.Scrub($"x {_vault}/a.md y {Jwt}");
        Assert.Equal(once, service.Scrub(once));
    }
}

/// <summary>Vista previa del paquete (MED-08): lista los mismos archivos que se escribirán.</summary>
public sealed class DiagnosticsPreviewViewModelTests
{
    [Fact]
    public void Preview_ListsEveryFile_SelectsTheFirst_AndShowsItsContent()
    {
        var package = new DiagnosticsPackage([new DiagnosticsFile("a.txt", "uno"), new DiagnosticsFile("b.txt", "dos dos")]);

        var preview = new enki.ViewModels.DiagnosticsPreviewViewModel(package);

        Assert.Equal(["a.txt", "b.txt"], preview.Files.Select(f => f.Name));
        Assert.Equal("a.txt", preview.SelectedFile!.Name);
        Assert.Equal("uno", preview.SelectedContent);
        preview.SelectedFile = preview.Files[1];
        Assert.Equal("dos dos", preview.SelectedContent);
        Assert.Contains("2", preview.SummaryLabel, StringComparison.Ordinal);
        Assert.Same(package, preview.Package);
    }
}
