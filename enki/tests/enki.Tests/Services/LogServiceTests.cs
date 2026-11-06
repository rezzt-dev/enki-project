using System;
using System.IO;
using System.Linq;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="LogService"/> — Fase 0 del roadmap de deficiencias
/// (docs/analisis-proyecto/analisis-deficiencias-proyecto.md §2, "Manejo de errores y logging"). El
/// criterio de aceptación de esa fase es justamente esto: un fallo real debe dejar una entrada en el log
/// con stack trace legible. <see cref="LogService"/> es estático (tiene que estarlo, ver su doc comment),
/// así que cada test se serializa con un <c>lock</c> propio para no pisar el estado global de otro test
/// que corra en paralelo dentro de esta misma clase (xunit no paraleliza tests de la misma clase entre sí
/// por defecto, pero el candado deja la intención explícita).</summary>
public class LogServiceTests : IDisposable
{
    private static readonly object SequentialGuard = new();

    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public LogServiceTests()
    {
        System.Threading.Monitor.Enter(SequentialGuard);
        LogService.Initialize(_storageDirectory);
        LogService.SetDebugEnabled(false);
    }

    public void Dispose()
    {
        LogService.SetDebugEnabled(false);
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
        System.Threading.Monitor.Exit(SequentialGuard);
    }

    private string ReadTodaysLogFile()
    {
        var path = Directory.EnumerateFiles(_storageDirectory, "enki-*.log").Single();
        return File.ReadAllText(path);
    }

    [Fact]
    public void Warning_WritesALine_ToTodaysLogFile()
    {
        LogService.Warning("algo se cayó");

        var content = ReadTodaysLogFile();
        Assert.Contains("[WARNING]", content);
        Assert.Contains("algo se cayó", content);
    }

    [Fact]
    public void Error_WithException_IncludesStackTrace()
    {
        Exception caught;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception ex) { caught = ex; }

        LogService.Error("fallo real", caught);

        var content = ReadTodaysLogFile();
        Assert.Contains("[ERROR]", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("boom", content);
        Assert.Contains("at ", content); // línea de stack trace
    }

    [Fact]
    public void Debug_IsFiltered_ByDefault()
    {
        LogService.Debug("detalle que no debería aparecer");
        // Nada se escribió: Warning() lo confirma escribiendo algo, y comprobamos que ese "algo" es lo
        // único que hay en el fichero.
        LogService.Warning("marcador");

        var content = ReadTodaysLogFile();
        Assert.DoesNotContain("detalle que no debería aparecer", content);
        Assert.Contains("marcador", content);
    }

    [Fact]
    public void Debug_IsWritten_WhenDebugEnabled()
    {
        LogService.SetDebugEnabled(true);

        LogService.Debug("detalle de depuración");

        var content = ReadTodaysLogFile();
        Assert.Contains("[DEBUG]", content);
        Assert.Contains("detalle de depuración", content);
    }

    [Fact]
    public void Initialize_WithUnwritableDirectory_DegradesToNoOp_InsteadOfThrowing()
    {
        // Un fichero (no una carpeta) como "storageDirectory" hace que Directory.CreateDirectory falle —
        // simula el caso "ni siquiera se pudo crear la carpeta de logs" sin depender de permisos de SO.
        var blockingFile = Path.Combine(Path.GetTempPath(), "enki-tests-blocking-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blockingFile, "");
        try
        {
            LogService.Initialize(blockingFile);

            Assert.Null(LogService.LogDirectory);
            // No debe lanzar ni con la carpeta de logs inutilizable.
            LogService.Error("esto no debe tumbar nada", new Exception("x"));
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }
}
