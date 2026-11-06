using System;
using System.IO;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="ExternalLinkPolicy"/>, el filtro que decide qué destino de un enlace de una
/// nota puede llegar a <c>Process.Start(UseShellExecute = true)</c> — es decir, a ejecutarse como si el
/// usuario hubiera hecho doble clic. Hasta que existió, el Ctrl+clic del editor entregaba el texto crudo
/// del enlace tal cual, y el texto de una nota puede haberlo escrito cualquiera: un vault sincronizado,
/// una plantilla descargada, un repositorio clonado (.docs/auditoria-seguridad-y-fallos-criticos.md,
/// SEC-01). Cada test trabaja sobre un vault de verdad en una carpeta temporal, porque la política sí
/// comprueba existencia en disco.</summary>
public class ExternalLinkPolicyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;
    private readonly string _noteDirectory;

    public ExternalLinkPolicyTests()
    {
        _vaultPath = Path.Combine(_root, "vault");
        _noteDirectory = Path.Combine(_vaultPath, "notas");
        Directory.CreateDirectory(_noteDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ExternalLinkResolution Resolve(string target) =>
        ExternalLinkPolicy.Resolve(target, _noteDirectory, _vaultPath);

    private string CreateFile(string relativePath)
    {
        var full = Path.Combine(_noteDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "contenido");
        return full;
    }

    [Theory]
    [InlineData("https://enki.app/docs")]
    [InlineData("http://ejemplo.test/pagina")]
    [InlineData("HTTPS://MAYUSCULAS.TEST")]
    [InlineData("mailto:alguien@ejemplo.test")]
    public void Resolve_AllowsTheThreeSafeSchemes(string url)
    {
        var resolution = Resolve(url);

        Assert.Equal(ExternalLinkBlockReason.None, resolution.Reason);
        Assert.Equal(url, resolution.Target);
    }

    /// <summary>El corazón de SEC-01: todo esto abría algo antes de que existiera la política.</summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("search-ms:query=algo")]
    [InlineData(@"\\192.0.2.10\share\factura.pdf.exe")]  // UNC: ejecutable servido desde la red
    [InlineData("//192.0.2.10/share/algo")]              // sin esquema, pero igual de remoto
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlocksEverySchemeOutsideTheAllowlist(string target)
    {
        var resolution = Resolve(target);

        Assert.Equal(ExternalLinkBlockReason.UnsupportedScheme, resolution.Reason);
        Assert.Null(resolution.Target);
    }

    [Fact]
    public void Resolve_AllowsAnExistingFileInsideTheVault()
    {
        var expected = CreateFile("adjunto.pdf");

        var resolution = Resolve("adjunto.pdf");

        Assert.Equal(ExternalLinkBlockReason.None, resolution.Reason);
        Assert.Equal(expected, resolution.Target);
    }

    /// <summary>Una ruta relativa con espacios se escribe porcentaje-escapada en Markdown; sin
    /// descodificarla, el archivo "no existiría" y el enlace quedaría roto para el usuario.</summary>
    [Fact]
    public void Resolve_DecodesPercentEscapesInALocalPath()
    {
        var expected = CreateFile("un adjunto.pdf");

        var resolution = Resolve("un%20adjunto.pdf");

        Assert.Equal(ExternalLinkBlockReason.None, resolution.Reason);
        Assert.Equal(expected, resolution.Target);
    }

    [Fact]
    public void Resolve_AllowsAFileInASubfolderOfTheVault()
    {
        var expected = CreateFile(Path.Combine("sub", "hoja.csv"));

        var resolution = Resolve("sub/hoja.csv");

        Assert.Equal(ExternalLinkBlockReason.None, resolution.Reason);
        Assert.Equal(expected, resolution.Target);
    }

    [Fact]
    public void Resolve_BlocksARelativePathThatEscapesTheVault()
    {
        File.WriteAllText(Path.Combine(_root, "fuera.txt"), "secreto");

        var resolution = Resolve("../../fuera.txt");

        Assert.Equal(ExternalLinkBlockReason.OutsideVault, resolution.Reason);
        Assert.Null(resolution.Target);
    }

    [Fact]
    public void Resolve_BlocksAnAbsolutePathOutsideTheVault()
    {
        var outside = Path.Combine(_root, "fuera.txt");
        File.WriteAllText(outside, "secreto");

        var resolution = Resolve(outside);

        Assert.Equal(ExternalLinkBlockReason.OutsideVault, resolution.Reason);
    }

    /// <summary>Un vault hermano cuyo nombre empieza igual no está "dentro": la comparación se hace con el
    /// separador final añadido justo para esto.</summary>
    [Fact]
    public void Resolve_BlocksASiblingFolderWithAPrefixMatchingName()
    {
        var sibling = _vaultPath + "-privado";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "nota.md"), "contenido");

        var resolution = ExternalLinkPolicy.Resolve(
            Path.Combine(sibling, "nota.md"), _noteDirectory, _vaultPath);

        Assert.Equal(ExternalLinkBlockReason.OutsideVault, resolution.Reason);
    }

    /// <summary>Estar dentro del vault no basta: el vault es una carpeta que se sincroniza entre equipos,
    /// así que un ejecutable puede llegar a él sin que su dueño lo haya puesto ahí. La lista cubre las
    /// tres plataformas a la vez a propósito.</summary>
    [Theory]
    [InlineData("payload.exe")]
    [InlineData("payload.bat")]
    [InlineData("payload.cmd")]
    [InlineData("payload.ps1")]
    [InlineData("payload.sh")]
    [InlineData("payload.desktop")]
    [InlineData("payload.jar")]
    [InlineData("payload.PDF.exe")]
    public void Resolve_BlocksAnExecutableEvenInsideTheVault(string fileName)
    {
        CreateFile(fileName);

        var resolution = Resolve(fileName);

        Assert.Equal(ExternalLinkBlockReason.ExecutableFile, resolution.Reason);
        Assert.Null(resolution.Target);
    }

    [Fact]
    public void Resolve_ReportsABrokenLinkApartFromABlockedOne()
    {
        var resolution = Resolve("no-existe.pdf");

        Assert.Equal(ExternalLinkBlockReason.Missing, resolution.Reason);
    }

    /// <summary>Un ancla del propio documento no es un enlace rechazado sino algo que Enki todavía no
    /// navega; el editor la distingue para no avisar de nada.</summary>
    [Fact]
    public void Resolve_TreatsAnInPageAnchorAsItsOwnCase()
    {
        var resolution = Resolve("#una-seccion");

        Assert.Equal(ExternalLinkBlockReason.InPageAnchor, resolution.Reason);
    }

    /// <summary>Sin vault configurado no hay frontera contra la que validar, así que una ruta local no se
    /// abre "por si acaso".</summary>
    [Fact]
    public void Resolve_WithoutAVault_BlocksEveryLocalPath()
    {
        var resolution = ExternalLinkPolicy.Resolve("adjunto.pdf", _noteDirectory, vaultPath: null);

        Assert.Equal(ExternalLinkBlockReason.OutsideVault, resolution.Reason);
    }

    [Fact]
    public void Describe_TruncatesALongTarget()
    {
        var described = ExternalLinkPolicy.Describe("https://ejemplo.test/" + new string('a', 500));

        Assert.Equal(80, described.Length);
        Assert.EndsWith("…", described, StringComparison.Ordinal);
    }
}
