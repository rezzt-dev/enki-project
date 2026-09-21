using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Vigilancia de cambios externos al vault
/// (.docs/project-info/cambios-externos-en-el-vault.md §3). Todas las pruebas usan un vault temporal y
/// tiempos de rebote/sondeo de milisegundos (constructor interno) en vez de los de la app, para que sean
/// rápidas sin dejar de ejercitar el <c>FileSystemWatcher</c> real: lo que se quiere comprobar es
/// justamente el comportamiento frente al sistema de archivos de verdad, no frente a un doble.</summary>
public sealed class VaultChangeWatcherTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    /// <summary>Margen para que el evento llegue. Generoso a propósito: el retardo de <c>inotify</c> y
    /// compañía no es determinista, y una prueba que falla de vez en cuando es peor que una lenta.</summary>
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Espera antes de afirmar que <em>no</em> llegó nada. Varias veces el rebote, que es el
    /// único retardo que este servicio introduce por su cuenta.</summary>
    private static readonly TimeSpan SilenceWindow = TimeSpan.FromMilliseconds(600);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly BlockingCollection<VaultExternalChange> _changes = new();
    private VaultChangeWatcher? _watcher;

    public void Dispose()
    {
        _watcher?.Dispose();
        _changes.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    private VaultChangeWatcher CreateWatcher(out string vaultDir, bool enabled = true)
    {
        vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(vaultDir);

        var settings = new SettingsService(_tempDir);
        settings.Settings.VaultWatchExternalChanges = enabled;
        var vault = new VaultService(settings);
        vault.SetVaultPath(vaultDir);

        _watcher = new VaultChangeWatcher(vault, settings, Debounce, Poll);
        _watcher.ExternalChangeDetected += change => _changes.Add(change);
        return _watcher;
    }

    private string TrackNote(VaultChangeWatcher watcher, string vaultDir, string content = "# Nota\n\nOriginal.")
    {
        var path = Path.Combine(vaultDir, "Nota.md");
        File.WriteAllText(path, content);
        watcher.Track(path, NoteDiskStamps.TryRead(path)!.Value);
        return path;
    }

    private VaultExternalChange WaitForChange()
    {
        Assert.True(_changes.TryTake(out var change, EventTimeout),
            "No llegó ningún aviso de cambio externo dentro del tiempo de espera.");
        return change!;
    }

    private void AssertNoFurtherChanges()
    {
        Assert.False(_changes.TryTake(out var unexpected, SilenceWindow),
            $"Llegó un aviso que no debía: {unexpected?.Kind} sobre '{unexpected?.FullPath}'.");
    }

    [Fact]
    public void ExternalWrite_OnATrackedNote_IsReported()
    {
        var watcher = CreateWatcher(out var vaultDir);
        watcher.Start(vaultDir);
        var path = TrackNote(watcher, vaultDir);

        File.WriteAllText(path, "# Nota\n\nEscrito desde otro editor.");

        var change = WaitForChange();
        Assert.Equal(VaultExternalChangeKind.Modified, change.Kind);
        Assert.Equal(path, change.FullPath);
    }

    /// <summary>Un sincronizador no escribe un archivo de una vez: lo toca varias veces seguidas. El
    /// usuario tiene que ver un aviso, no cinco.</summary>
    [Fact]
    public void BurstOfExternalWrites_IsReportedOnce()
    {
        var watcher = CreateWatcher(out var vaultDir);
        watcher.Start(vaultDir);
        var path = TrackNote(watcher, vaultDir);

        for (var i = 0; i < 5; i++) File.WriteAllText(path, $"# Nota\n\nRáfaga {i}.");

        Assert.Equal(VaultExternalChangeKind.Modified, WaitForChange().Kind);
        AssertNoFurtherChanges();
    }

    /// <summary>El caso que, sin resolver, convertiría cada autoguardado en un falso conflicto: Enki
    /// escribe y, acto seguido, adopta la huella de lo que acaba de dejar en disco.</summary>
    [Fact]
    public void OwnWrite_FollowedByUpdateStamp_IsNotReported()
    {
        var watcher = CreateWatcher(out var vaultDir);
        watcher.Start(vaultDir);
        var path = TrackNote(watcher, vaultDir);

        File.WriteAllText(path, "# Nota\n\nGuardado por Enki.");
        watcher.UpdateStamp(path, NoteDiskStamps.TryRead(path)!.Value);

        AssertNoFurtherChanges();
    }

    /// <summary>Reescribir bytes idénticos (Syncthing, Dropbox) cambia la fecha del archivo pero no su
    /// contenido: no es un cambio que merezca interrumpir a nadie.</summary>
    [Fact]
    public void RewriteWithIdenticalContent_IsNotReported()
    {
        var watcher = CreateWatcher(out var vaultDir);
        watcher.Start(vaultDir);
        var path = TrackNote(watcher, vaultDir);

        File.WriteAllText(path, "# Nota\n\nOriginal.");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        AssertNoFurtherChanges();
    }

    [Fact]
    public void ExternalDelete_IsReported_AndStopsTrackingTheNote()
    {
        var watcher = CreateWatcher(out var vaultDir);
        watcher.Start(vaultDir);
        var path = TrackNote(watcher, vaultDir);

        File.Delete(path);

        Assert.Equal(VaultExternalChangeKind.Deleted, WaitForChange().Kind);
        Assert.DoesNotContain(path, watcher.TrackedPaths);
    }

    [Fact]
    public void ExternalRename_IsReported_WithTheNewPath()
    {
        var watcher = CreateWatcher(out var vaultDir);
        watcher.Start(vaultDir);
        var path = TrackNote(watcher, vaultDir);
        var renamed = Path.Combine(vaultDir, "Nota renombrada.md");

        File.Move(path, renamed);

        var change = WaitForChange();
        Assert.Equal(VaultExternalChangeKind.Renamed, change.Kind);
        Assert.Equal(path, change.FullPath);
        Assert.Equal(renamed, change.NewFullPath);
    }

    /// <summary>Una nota que nadie tiene abierta no necesita vigilancia: el siguiente escaneo del vault ya
    /// la relee sola.</summary>
    [Fact]
    public void ChangesToUntrackedNotes_AreNotReported()
    {
        var watcher = CreateWatcher(out var vaultDir);
        watcher.Start(vaultDir);
        TrackNote(watcher, vaultDir);

        File.WriteAllText(Path.Combine(vaultDir, "Otra.md"), "# Otra\n\nCambio ajeno.");

        AssertNoFurtherChanges();
    }

    [Fact]
    public void AfterUntrack_ChangesAreNotReported()
    {
        var watcher = CreateWatcher(out var vaultDir);
        watcher.Start(vaultDir);
        var path = TrackNote(watcher, vaultDir);

        watcher.Untrack(path);
        File.WriteAllText(path, "# Nota\n\nYa no interesa.");

        AssertNoFurtherChanges();
    }

    /// <summary>Con el ajuste apagado no se mira nada; la red de seguridad que queda es la comprobación
    /// previa a guardar, que no pasa por aquí.</summary>
    [Fact]
    public void WhenDisabled_NothingIsReported()
    {
        var watcher = CreateWatcher(out var vaultDir, enabled: false);
        watcher.Start(vaultDir);
        var path = TrackNote(watcher, vaultDir);

        File.WriteAllText(path, "# Nota\n\nEscrito desde otro editor.");

        Assert.False(watcher.IsEnabled);
        AssertNoFurtherChanges();
    }

    /// <summary>Cambiar de vault libera el vigilante anterior; las notas que vigilaba eran pestañas del
    /// vault que se acaba de dejar.</summary>
    [Fact]
    public void StartingOnAnotherVault_ForgetsThePreviousTracking()
    {
        var watcher = CreateWatcher(out var vaultDir);
        watcher.Start(vaultDir);
        var path = TrackNote(watcher, vaultDir);

        var otherVault = Path.Combine(_tempDir, "otro-vault");
        Directory.CreateDirectory(otherVault);
        watcher.Start(otherVault);

        Assert.Empty(watcher.TrackedPaths);
        File.WriteAllText(path, "# Nota\n\nDel vault anterior.");
        AssertNoFurtherChanges();
    }

    [Fact]
    public void AfterDispose_NothingIsReported()
    {
        var watcher = CreateWatcher(out var vaultDir);
        watcher.Start(vaultDir);
        var path = TrackNote(watcher, vaultDir);

        watcher.Dispose();
        File.WriteAllText(path, "# Nota\n\nDespués de cerrar.");

        AssertNoFurtherChanges();
    }

    /// <summary>Cuando no se puede crear el <c>FileSystemWatcher</c> (unidad de red, límite de
    /// <c>inotify</c> agotado), la vigilancia degrada a sondeo de las notas abiertas en vez de quedarse
    /// ciega en silencio.</summary>
    [Fact]
    public void WhenTheFileSystemWatcherCannotStart_DegradesToPolling()
    {
        var watcher = CreateWatcher(out var vaultDir);
        var path = TrackNote(watcher, vaultDir);

        watcher.Start(Path.Combine(_tempDir, "carpeta-que-no-existe"));
        Assert.True(watcher.IsPolling);

        // El sondeo mira las notas registradas, estén donde estén: se vuelve a registrar porque cambiar de
        // raíz olvida lo anterior, igual que un cambio de vault.
        watcher.Track(path, NoteDiskStamps.TryRead(path)!.Value);
        File.WriteAllText(path, "# Nota\n\nDetectado por sondeo.");

        Assert.Equal(VaultExternalChangeKind.Modified, WaitForChange().Kind);
    }
}
