using System;
using System.IO;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre el primitivo con el que Enki distingue una escritura propia de una ajena
/// (.docs/project-info/cambios-externos-en-el-vault.md §3). Lo que se prueba aquí no es "sabe leer un
/// archivo" sino los dos casos límite que motivan que la huella tenga tres campos y no solo la fecha: un
/// sincronizador que refresca la marca de tiempo sin cambiar nada (no debe parecer un cambio) y un cambio
/// real de contenido (debe detectarse aunque el tamaño coincida).</summary>
public sealed class NoteDiskStampsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public NoteDiskStampsTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    private string WriteNote(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNull() =>
        Assert.Null(NoteDiskStamps.TryRead(Path.Combine(_tempDir, "no-existe.md")));

    [Fact]
    public void Check_WithoutAnyChange_ReportsUnchanged()
    {
        var path = WriteNote("Nota.md", "# Nota\n\nContenido.");
        var baseline = NoteDiskStamps.TryRead(path)!.Value;

        var state = NoteDiskStamps.Check(path, baseline, out var current);

        Assert.Equal(NoteDiskState.Unchanged, state);
        Assert.Equal(baseline, current);
    }

    [Fact]
    public void Check_WhenContentChanges_ReportsModified()
    {
        var path = WriteNote("Nota.md", "# Nota\n\nContenido.");
        var baseline = NoteDiskStamps.TryRead(path)!.Value;

        File.WriteAllText(path, "# Nota\n\nOtra cosa distinta.");

        Assert.Equal(NoteDiskState.Modified, NoteDiskStamps.Check(path, baseline, out var current));
        Assert.NotEqual(baseline.ContentHash, current.ContentHash);
    }

    /// <summary>El caso que obliga a que la huella lleve hash: si solo se comparasen fecha y tamaño, dos
    /// contenidos distintos del mismo largo pasarían por iguales.</summary>
    [Fact]
    public void Check_WhenContentChangesKeepingTheSameSize_ReportsModified()
    {
        var path = WriteNote("Nota.md", "AAAA");
        var baseline = NoteDiskStamps.TryRead(path)!.Value;

        File.WriteAllText(path, "BBBB");

        Assert.Equal(NoteDiskState.Modified, NoteDiskStamps.Check(path, baseline, out var current));
        Assert.Equal(baseline.SizeBytes, current.SizeBytes);
    }

    /// <summary>El caso que evita el diálogo de conflicto injustificado: Syncthing y Dropbox reescriben
    /// archivos idénticos y les cambian la fecha. Debe salir "sin cambios", con la huella nueva para que
    /// la siguiente comprobación no vuelva a leer el archivo entero.</summary>
    [Fact]
    public void Check_WhenOnlyTheTimestampChanges_ReportsUnchanged_AndRefreshesTheStamp()
    {
        var path = WriteNote("Nota.md", "# Nota\n\nContenido.");
        var baseline = NoteDiskStamps.TryRead(path)!.Value;

        var touched = baseline.LastWriteUtc.AddMinutes(5);
        File.SetLastWriteTimeUtc(path, touched);

        Assert.Equal(NoteDiskState.Unchanged, NoteDiskStamps.Check(path, baseline, out var current));
        Assert.Equal(touched, current.LastWriteUtc);
        Assert.Equal(baseline.ContentHash, current.ContentHash);
    }

    [Fact]
    public void Check_WhenContentChangesKeepingTimestampAndSize_ReportsModified()
    {
        var path = WriteNote("Nota.md", "AAAA");
        var baseline = NoteDiskStamps.TryRead(path)!.Value;
        File.WriteAllText(path, "BBBB");
        File.SetLastWriteTimeUtc(path, baseline.LastWriteUtc);

        Assert.Equal(NoteDiskState.Modified, NoteDiskStamps.Check(path, baseline, out _));
    }

    [Fact]
    public void Check_WhenTheFileIsGone_ReportsMissing()
    {
        var path = WriteNote("Nota.md", "# Nota");
        var baseline = NoteDiskStamps.TryRead(path)!.Value;

        File.Delete(path);

        Assert.Equal(NoteDiskState.Missing, NoteDiskStamps.Check(path, baseline, out _));
    }
}
