using System;
using System.IO;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the Fase 6 change to <see cref="EditorViewModel.OnTextChanged"/>
/// (docs/analisis-proyecto/roadmap.md, análisis §3, top 10 #6): <see cref="EditorViewModel.Backlinks"/> used
/// to rescan the whole vault on every keystroke; it now rides the same 600ms debounce as autosave instead,
/// via the same <c>_saveTimer</c> — a dirty edit alone must not refresh it, and
/// <see cref="EditorViewModel.FlushPendingSave"/> must still flush a refresh that's pending so a tab switch
/// right after typing doesn't leave the panel stale.</summary>
public class EditorViewModelBacklinksDebounceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _vaultDir;

    public EditorViewModelBacklinksDebounceTests()
    {
        _vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(_vaultDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private VaultService CreateVault()
    {
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.SetVaultPath(_vaultDir);
        return vault;
    }

    [Fact]
    public void OnTextChanged_WithADirtyEdit_DoesNotImmediatelyRefreshBacklinks()
    {
        var vault = CreateVault();
        var target = vault.CreateNote("Objetivo");
        File.WriteAllText(Path.Combine(_vaultDir, "Enlazadora.md"), "Sin enlace todavía.");
        var editor = new EditorViewModel(vault, target, onBack: () => { }, onChanged: () => { });
        Assert.True(editor.Backlinks.IsEmpty);

        // A link to the target appears on disk (simulating another tab/process), and this tab's own text
        // changes — under the old per-keystroke refresh this alone would have picked it up immediately.
        File.WriteAllText(Path.Combine(_vaultDir, "Enlazadora.md"), "Ahora sí: [[Objetivo]].");
        editor.OnTextChanged("# Objetivo\n\nEditando...");

        Assert.True(editor.Backlinks.IsEmpty);
    }

    [Fact]
    public void FlushPendingSave_AfterADirtyEdit_RefreshesTheBacklinksThatWerePending()
    {
        var vault = CreateVault();
        var target = vault.CreateNote("Objetivo");
        File.WriteAllText(Path.Combine(_vaultDir, "Enlazadora.md"), "Sin enlace todavía.");
        var editor = new EditorViewModel(vault, target, onBack: () => { }, onChanged: () => { });
        Assert.True(editor.Backlinks.IsEmpty);

        File.WriteAllText(Path.Combine(_vaultDir, "Enlazadora.md"), "Ahora sí: [[Objetivo]].");
        editor.OnTextChanged("# Objetivo\n\nEditando...");
        Assert.True(editor.Backlinks.IsEmpty);

        editor.FlushPendingSave();

        Assert.False(editor.Backlinks.IsEmpty);
        Assert.Single(editor.Backlinks.Items);
    }

    [Fact]
    public void OnTextChanged_WithNoActualChange_DoesNotMarkDirty_AndLeavesBacklinksAlone()
    {
        var vault = CreateVault();
        var target = vault.CreateNote("Objetivo");
        var editor = new EditorViewModel(vault, target, onBack: () => { }, onChanged: () => { });

        editor.OnTextChanged(editor.Document.Text);

        Assert.False(editor.IsDirty);
    }
}
