using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the Fase 2 save indicator (docs/analisis-mejoras-funcionalidades.md §4.1: "indicador de
/// guardado prominente + timestamp") — <see cref="EditorViewModel.SaveStatus"/> and
/// <see cref="EditorViewModel.ShowSavedIcon"/> track dirty/saved state with a wall-clock timestamp instead
/// of a bare "Guardado"/"Sin guardar" word.</summary>
[Collection("LocalizationMutatingTests")]
public class EditorViewModelSaveStatusTests : IDisposable
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
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.SetVaultPath(vaultDir);
        return vault;
    }

    [Fact]
    public void InitialSaveStatus_CarriesTimestamp_AndShowsSavedIcon()
    {
        var vault = CreateVault(out var vaultDir);
        File.WriteAllText(Path.Combine(vaultDir, "Nota.md"), "# Nota\n\nContenido.");
        var note = vault.GetNotes().Single();

        var editor = new EditorViewModel(vault, note, onBack: () => { }, onChanged: () => { });

        Assert.StartsWith("Saved at ", editor.SaveStatus);
        Assert.False(editor.IsDirty);
        Assert.True(editor.ShowSavedIcon);
    }

    [Fact]
    public void OnTextChanged_WithDifferentText_MarksDirty_AndHidesSavedIcon()
    {
        var vault = CreateVault(out var vaultDir);
        File.WriteAllText(Path.Combine(vaultDir, "Nota.md"), "# Nota\n\nContenido.");
        var note = vault.GetNotes().Single();
        var editor = new EditorViewModel(vault, note, onBack: () => { }, onChanged: () => { });

        editor.OnTextChanged("# Nota\n\nContenido editado.");

        Assert.True(editor.IsDirty);
        Assert.Equal("Unsaved changes", editor.SaveStatus);
        Assert.False(editor.ShowSavedIcon);
    }

    [Fact]
    public void Save_ClearsDirty_AndRestoresTimestampedStatus()
    {
        var vault = CreateVault(out var vaultDir);
        File.WriteAllText(Path.Combine(vaultDir, "Nota.md"), "# Nota\n\nContenido.");
        var note = vault.GetNotes().Single();
        var editor = new EditorViewModel(vault, note, onBack: () => { }, onChanged: () => { });
        editor.OnTextChanged("# Nota\n\nContenido editado.");

        editor.Save("# Nota\n\nContenido editado.");

        Assert.False(editor.IsDirty);
        Assert.StartsWith("Saved at ", editor.SaveStatus);
        Assert.True(editor.ShowSavedIcon);
    }
}
