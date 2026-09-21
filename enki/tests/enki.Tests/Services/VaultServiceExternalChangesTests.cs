using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>La red de seguridad que no depende de la vigilancia del sistema de archivos: comprobar el
/// disco justo antes de escribir (.docs/project-info/cambios-externos-en-el-vault.md §4). Es lo que cubre
/// los casos en los que el vigilante no llegó a enterarse — búfer desbordado, unidad de red, equipo
/// suspendido— y por eso se prueba aparte de él.</summary>
public sealed class VaultServiceExternalChangesTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    private VaultService CreateVault(out string vaultDir)
    {
        vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(vaultDir);
        var vault = new VaultService(new SettingsService(_tempDir));
        vault.SetVaultPath(vaultDir);
        return vault;
    }

    private static NoteFile SeedNote(VaultService vault, string vaultDir, string content)
    {
        File.WriteAllText(Path.Combine(vaultDir, "Nota.md"), content);
        return vault.GetNotes().Single();
    }

    [Fact]
    public void WriteContentChecked_WhenNothingChangedOnDisk_Writes_AndReturnsTheNewStamp()
    {
        var vault = CreateVault(out var vaultDir);
        var note = SeedNote(vault, vaultDir, "# Nota\n\nOriginal.");
        var baseline = NoteDiskStamps.TryRead(note.FullPath);

        var stamp = vault.WriteContentChecked(note, "# Nota\n\nEditado en Enki.", baseline);

        Assert.Equal("# Nota\n\nEditado en Enki.", File.ReadAllText(note.FullPath));
        Assert.NotNull(stamp);
        Assert.Equal(NoteDiskState.Unchanged, NoteDiskStamps.Check(note.FullPath, stamp!.Value, out _));
    }

    [Fact]
    public void WriteContentChecked_WithoutBaseline_WritesLikeWriteContent()
    {
        var vault = CreateVault(out var vaultDir);
        var note = SeedNote(vault, vaultDir, "# Nota\n\nOriginal.");

        vault.WriteContentChecked(note, "# Nota\n\nSin línea base.", baseline: null);

        Assert.Equal("# Nota\n\nSin línea base.", File.ReadAllText(note.FullPath));
    }

    /// <summary>El fallo que esta funcionalidad existe para evitar: el autoguardado pisando en silencio lo
    /// que otro programa acababa de escribir.</summary>
    [Fact]
    public void WriteContentChecked_WhenTheFileChangedOutside_Throws_AndLeavesTheFileUntouched()
    {
        var vault = CreateVault(out var vaultDir);
        var note = SeedNote(vault, vaultDir, "# Nota\n\nOriginal.");
        var baseline = NoteDiskStamps.TryRead(note.FullPath);

        File.WriteAllText(note.FullPath, "# Nota\n\nEscrito desde otro editor.");

        var error = Assert.Throws<ExternalModificationException>(
            () => vault.WriteContentChecked(note, "# Nota\n\nEditado en Enki.", baseline));

        Assert.Equal(VaultExternalChangeKind.Modified, error.Kind);
        Assert.Equal("# Nota\n\nEscrito desde otro editor.", File.ReadAllText(note.FullPath));
    }

    [Fact]
    public void WriteContentChecked_WhenTheFileWasDeletedOutside_Throws_WithoutRecreatingIt()
    {
        var vault = CreateVault(out var vaultDir);
        var note = SeedNote(vault, vaultDir, "# Nota\n\nOriginal.");
        var baseline = NoteDiskStamps.TryRead(note.FullPath);

        File.Delete(note.FullPath);

        var error = Assert.Throws<ExternalModificationException>(
            () => vault.WriteContentChecked(note, "# Nota\n\nEditado en Enki.", baseline));

        Assert.Equal(VaultExternalChangeKind.Deleted, error.Kind);
        Assert.False(File.Exists(note.FullPath));
    }

    /// <summary>Un vault desconectado no es "alguien borró esta nota": debe seguir saliendo como el fallo
    /// de E/S de siempre, que es lo que el editor ya sabe reintentar.</summary>
    [Fact]
    public void WriteContentChecked_WhenTheWholeVaultIsGone_ThrowsVaultUnavailable()
    {
        var vault = CreateVault(out var vaultDir);
        var note = SeedNote(vault, vaultDir, "# Nota\n\nOriginal.");
        var baseline = NoteDiskStamps.TryRead(note.FullPath);

        Directory.Delete(vaultDir, recursive: true);

        Assert.Throws<VaultUnavailableException>(
            () => vault.WriteContentChecked(note, "# Nota\n\nEditado en Enki.", baseline));
    }

    /// <summary>La caché de notas se refresca comparando fechas de modificación, así que una segunda
    /// escritura dentro de la misma marca de tiempo la dejaría sirviendo contenido viejo: la relectura de
    /// un cambio externo tiene que saltársela.</summary>
    [Fact]
    public void ReadFromDisk_BypassesTheNoteCache()
    {
        var vault = CreateVault(out var vaultDir);
        var note = SeedNote(vault, vaultDir, "# Nota\n\nOriginal.");
        var cachedTimestamp = File.GetLastWriteTimeUtc(note.FullPath);
        Assert.Equal("# Nota\n\nOriginal.", vault.ReadContent(note));

        File.WriteAllText(note.FullPath, "# Nota\n\nDesde fuera.");
        File.SetLastWriteTimeUtc(note.FullPath, cachedTimestamp);

        var fresh = vault.ReadFromDisk(note.FullPath);

        Assert.NotNull(fresh);
        Assert.Equal("# Nota\n\nDesde fuera.", fresh!.Value.Content);
        Assert.Equal(NoteDiskState.Unchanged, NoteDiskStamps.Check(note.FullPath, fresh.Value.Stamp, out _));
    }

    [Fact]
    public void ReadFromDisk_WhenTheFileIsGone_ReturnsNull()
    {
        var vault = CreateVault(out var vaultDir);
        var note = SeedNote(vault, vaultDir, "# Nota");
        File.Delete(note.FullPath);

        Assert.Null(vault.ReadFromDisk(note.FullPath));
    }

    [Fact]
    public void ApplyExternalChange_NotifiesSubscribers()
    {
        var vault = CreateVault(out var vaultDir);
        var note = SeedNote(vault, vaultDir, "# Nota");
        string? notifiedPath = null;
        vault.ExternalChangeApplied += path => notifiedPath = path;

        vault.ApplyExternalChange(note.FullPath);

        Assert.Equal(note.FullPath, notifiedPath);
    }
}
