using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers the local version-history engine in <c>VaultService.NoteHistory.cs</c> — snapshot
/// capture and its autosave throttle/dedupe, restore-with-safety-net, and the rename/move/trash lifecycle
/// that keeps a note's history following it around the vault instead of orphaning it.</summary>
public class VaultServiceNoteHistoryTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public VaultServiceNoteHistoryTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private VaultService CreateVault()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        return vault;
    }

    /// <summary>Backdates every recorded version of <paramref name="note"/> so the next
    /// <see cref="VaultService.CaptureSnapshot"/> call falls outside <see cref="VaultService.MinimumAutosaveInterval"/>
    /// — the same trick <c>VaultBackupServiceTests</c>-style tests use for interval logic that reads
    /// <see cref="DateTime.UtcNow"/> directly instead of an injectable clock: rewrite what's already on disk
    /// rather than waiting for real time to pass.</summary>
    private static void BackdateHistory(VaultService vault, NoteFile note, TimeSpan by)
    {
        var manifestPath = FindManifestPath(vault, note);
        var json = File.ReadAllText(manifestPath);
        var versions = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<NoteVersion>>(json)!;
        foreach (var version in versions) version.CreatedUtc -= by;
        File.WriteAllText(manifestPath, System.Text.Json.JsonSerializer.Serialize(versions));
    }

    private static string FindManifestPath(VaultService vault, NoteFile note)
    {
        var historyRoot = Path.Combine(vault.VaultPath, ".enki", "history");
        var manifest = Directory.EnumerateFiles(historyRoot, "manifest.json", SearchOption.AllDirectories).Single();
        return manifest;
    }

    [Fact]
    public void CaptureSnapshot_FirstCallForANote_AlwaysCaptures()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota", initialContent: "Contenido inicial");

        var version = vault.CaptureSnapshot(note, "Contenido inicial", NoteVersionTrigger.Autosave);

        Assert.NotNull(version);
        Assert.Single(vault.GetVersionHistory(note));
    }

    [Fact]
    public void CaptureSnapshot_IdenticalContentToLatestVersion_IsSkipped()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota", initialContent: "Contenido");
        vault.CaptureSnapshot(note, "Contenido", NoteVersionTrigger.Autosave);

        var second = vault.CaptureSnapshot(note, "Contenido", NoteVersionTrigger.Autosave);

        Assert.Null(second);
        Assert.Single(vault.GetVersionHistory(note));
    }

    [Fact]
    public void CaptureSnapshot_Autosave_ThrottlesRapidSuccessiveCaptures()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota", initialContent: "v1");
        vault.CaptureSnapshot(note, "v1", NoteVersionTrigger.Autosave);

        // Too soon after the first snapshot — even though the content genuinely changed, autosave must not
        // flood the history with one entry per debounce tick (historial local de versiones).
        var second = vault.CaptureSnapshot(note, "v2", NoteVersionTrigger.Autosave);

        Assert.Null(second);
        Assert.Single(vault.GetVersionHistory(note));
    }

    [Fact]
    public void CaptureSnapshot_Autosave_CapturesAgainOnceTheThrottleWindowHasPassed()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota", initialContent: "v1");
        vault.CaptureSnapshot(note, "v1", NoteVersionTrigger.Autosave);
        BackdateHistory(vault, note, VaultService.MinimumAutosaveInterval + TimeSpan.FromSeconds(1));

        var second = vault.CaptureSnapshot(note, "v2", NoteVersionTrigger.Autosave);

        Assert.NotNull(second);
        Assert.Equal(2, vault.GetVersionHistory(note).Count);
    }

    [Fact]
    public void CaptureSnapshot_PreRestoreTrigger_BypassesTheAutosaveThrottle()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota", initialContent: "v1");
        vault.CaptureSnapshot(note, "v1", NoteVersionTrigger.Autosave);

        var preRestore = vault.CaptureSnapshot(note, "v2", NoteVersionTrigger.PreRestore);

        Assert.NotNull(preRestore);
        Assert.Equal(2, vault.GetVersionHistory(note).Count);
        Assert.Equal(NoteVersionTrigger.PreRestore, vault.GetVersionHistory(note)[0].Trigger);
    }

    [Fact]
    public void RestoreVersion_WritesBackOldContentAndSnapshotsWhatWasThereBeforeRestoring()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota", initialContent: "Versión 1");
        var v1 = vault.CaptureSnapshot(note, "Versión 1", NoteVersionTrigger.Autosave)!;
        vault.WriteContent(note, "Versión 2 (actual)");

        vault.RestoreVersion(note, v1.Id);

        Assert.Equal("Versión 1", vault.ReadContent(note));
        var history = vault.GetVersionHistory(note);
        Assert.Contains(history, v => v.Trigger == NoteVersionTrigger.PreRestore);
        // The pre-restore snapshot protects "Versión 2 (actual)" — restoring the wrong version is itself
        // recoverable, never a dead end.
        var preRestoreVersion = history.First(v => v.Trigger == NoteVersionTrigger.PreRestore);
        Assert.Equal("Versión 2 (actual)", vault.ReadVersionContent(note, preRestoreVersion.Id));
    }

    [Fact]
    public void DeleteVersion_RemovesItFromHistoryAndFromDisk()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota", initialContent: "v1");
        var version = vault.CaptureSnapshot(note, "v1", NoteVersionTrigger.Autosave)!;

        vault.DeleteVersion(note, version.Id);

        Assert.Empty(vault.GetVersionHistory(note));
        Assert.Null(vault.ReadVersionContent(note, version.Id));
    }

    [Fact]
    public void RenameNote_MovesVersionHistoryAlongsideTheRenamedFile()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Original", initialContent: "v1");
        var version = vault.CaptureSnapshot(note, "v1", NoteVersionTrigger.Autosave)!;

        var renamed = vault.RenameNote(note, "Renombrada");

        var history = vault.GetVersionHistory(renamed);
        Assert.Single(history);
        Assert.Equal(version.Id, history[0].Id);
        Assert.Equal("v1", vault.ReadVersionContent(renamed, version.Id));
    }

    [Fact]
    public void MoveNote_MovesVersionHistoryAlongsideTheMovedFile()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota", initialContent: "v1");
        vault.CaptureSnapshot(note, "v1", NoteVersionTrigger.Autosave);
        var destination = VaultService.CreateFolder(_vaultPath, "Carpeta");

        var moved = vault.MoveNote(note, destination.FullPath);

        Assert.Single(vault.GetVersionHistory(moved));
    }

    [Fact]
    public void DeleteNoteThenRestore_RoundTripsVersionHistory()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota", initialContent: "v1");
        var version = vault.CaptureSnapshot(note, "v1", NoteVersionTrigger.Autosave)!;

        vault.DeleteNote(note);
        var trashed = vault.GetTrashedNotes().Single();
        vault.RestoreNote(trashed);
        var restored = vault.GetNotes().Single();

        var history = vault.GetVersionHistory(restored);
        Assert.Single(history);
        Assert.Equal(version.Id, history[0].Id);
    }

    [Fact]
    public void PermanentlyDelete_PurgesVersionHistoryToo()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota", initialContent: "v1");
        vault.CaptureSnapshot(note, "v1", NoteVersionTrigger.Autosave);
        vault.DeleteNote(note);
        var trashed = vault.GetTrashedNotes().Single();

        vault.PermanentlyDelete(trashed);

        var historyRoot = Path.Combine(vault.VaultPath, ".enki", "history");
        var remainingManifests = Directory.Exists(historyRoot)
            ? Directory.EnumerateFiles(historyRoot, "manifest.json", SearchOption.AllDirectories).ToList()
            : [];
        Assert.Empty(remainingManifests);
    }

    [Fact]
    public void WriteContent_CapturesThePreviousContentBeforeOverwritingIt()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota", initialContent: "Primera versión");

        vault.WriteContent(note, "Segunda versión");

        var history = vault.GetVersionHistory(note);
        Assert.Single(history);
        Assert.Equal("Primera versión", vault.ReadVersionContent(note, history[0].Id));
        Assert.Equal("Segunda versión", vault.ReadContent(note));
    }
}
