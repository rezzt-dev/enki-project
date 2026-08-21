using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers restaurar pestañas abiertas al reiniciar
/// (docs/ajustes-docs/02-productividad-como-app-de-escritorio.md §2): que abrir/cerrar/reordenar pestañas
/// persiste <see cref="Models.AppSettings.OpenNotePaths"/>/<see cref="Models.AppSettings.ActiveNotePath"/>,
/// y que <see cref="NotesViewModel.RestoreOpenTabs"/> las reabre en el mismo orden ignorando rutas que ya
/// no existen.</summary>
public class NotesViewModelOpenTabsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _vaultDir;

    public NotesViewModelOpenTabsTests()
    {
        _vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(_vaultDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (NotesViewModel Notes, SettingsService Settings, VaultService Vault, SessionService Session) CreateSetup()
    {
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.SetVaultPath(_vaultDir);
        var session = new SessionService(vault);
        var notes = new NotesViewModel(vault, session, settingsService: settings);
        return (notes, settings, vault, session);
    }

    [Fact]
    public void OpenNote_PersistsOpenPathsAndActivePath()
    {
        var (notes, settings, vault, _) = CreateSetup();
        File.WriteAllText(Path.Combine(_vaultDir, "A.md"), "# A");
        File.WriteAllText(Path.Combine(_vaultDir, "B.md"), "# B");
        var noteA = vault.GetNotes().Single(n => n.Title == "A");
        var noteB = vault.GetNotes().Single(n => n.Title == "B");

        notes.OpenNote(noteA);
        notes.OpenNote(noteB);

        Assert.Equal(new[] { noteA.FullPath, noteB.FullPath }, settings.Settings.OpenNotePaths);
        Assert.Equal(noteB.FullPath, settings.Settings.ActiveNotePath);
    }

    [Fact]
    public void CloseEditorTab_OnBackgroundTab_RemovesItFromPersistedList()
    {
        var (notes, settings, vault, _) = CreateSetup();
        File.WriteAllText(Path.Combine(_vaultDir, "A.md"), "# A");
        File.WriteAllText(Path.Combine(_vaultDir, "B.md"), "# B");
        var noteA = vault.GetNotes().Single(n => n.Title == "A");
        var noteB = vault.GetNotes().Single(n => n.Title == "B");
        notes.OpenNote(noteA);
        notes.OpenNote(noteB); // B is active, A is the background tab

        var editorA = notes.OpenEditors.Single(e => e.Note.FullPath == noteA.FullPath);
        notes.CloseEditorTabCommand.Execute(editorA);

        Assert.Equal(new[] { noteB.FullPath }, settings.Settings.OpenNotePaths);
        Assert.Equal(noteB.FullPath, settings.Settings.ActiveNotePath); // unchanged, closing a background tab
    }

    [Fact]
    public void CloseAllEditors_ClearsPersistedState()
    {
        var (notes, settings, vault, _) = CreateSetup();
        File.WriteAllText(Path.Combine(_vaultDir, "A.md"), "# A");
        var noteA = vault.GetNotes().Single();
        notes.OpenNote(noteA);

        notes.CloseAllEditorsCommand.Execute(null);

        Assert.Empty(settings.Settings.OpenNotePaths);
        Assert.Null(settings.Settings.ActiveNotePath);
    }

    [Fact]
    public void RestoreOpenTabs_ReopensSavedPathsAndActivatesTheSavedOne()
    {
        var (notes, settings, vault, _) = CreateSetup();
        File.WriteAllText(Path.Combine(_vaultDir, "A.md"), "# A");
        File.WriteAllText(Path.Combine(_vaultDir, "B.md"), "# B");
        var noteA = vault.GetNotes().Single(n => n.Title == "A");
        var noteB = vault.GetNotes().Single(n => n.Title == "B");
        settings.Settings.OpenNotePaths = [noteA.FullPath, noteB.FullPath];
        settings.Settings.ActiveNotePath = noteA.FullPath;

        notes.RestoreOpenTabs();

        Assert.Equal(2, notes.OpenEditors.Count);
        Assert.Equal(noteA.FullPath, notes.ActiveEditor?.Note.FullPath);
    }

    [Fact]
    public void RestoreOpenTabs_IgnoresPathsThatNoLongerExist()
    {
        var (notes, settings, vault, _) = CreateSetup();
        File.WriteAllText(Path.Combine(_vaultDir, "A.md"), "# A");
        var noteA = vault.GetNotes().Single();
        settings.Settings.OpenNotePaths = [noteA.FullPath, Path.Combine(_vaultDir, "YaNoExiste.md")];

        notes.RestoreOpenTabs();

        Assert.Single(notes.OpenEditors);
        Assert.Equal(noteA.FullPath, notes.OpenEditors[0].Note.FullPath);
    }

    [Fact]
    public void WithoutSettingsService_OpenAndCloseTabs_DoNotThrow()
    {
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.SetVaultPath(_vaultDir);
        var session = new SessionService(vault);
        var notes = new NotesViewModel(vault, session); // no settingsService, same as existing tests

        File.WriteAllText(Path.Combine(_vaultDir, "A.md"), "# A");
        var noteA = vault.GetNotes().Single();

        notes.OpenNote(noteA);
        notes.CloseAllEditorsCommand.Execute(null);
        notes.RestoreOpenTabs(); // no-op, must not throw
    }
}
