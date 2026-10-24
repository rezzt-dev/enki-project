using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Fase 4 §"Editor de notas" gap 18 ("Undo/redo por tab a veces confuso") — audits that each open
/// tab keeps its own independent undo/redo history. EditorView shares a single AvaloniaEdit TextEditor
/// across every tab and repoints its <c>Document</c> property on every tab switch instead of overwriting
/// Text with a snapshot (see EditorViewModel.Document's doc comment); since AvaloniaEdit's TextDocument
/// owns its own UndoStack instance, that swap is what keeps each note's undo/redo intact rather than
/// wiping or sharing it across tabs. These tests exercise the view-model layer both share (Document,
/// UndoStack) without needing the actual UI control.</summary>
[Collection("LocalizationMutatingTests")]
public class EditorViewModelUndoStackTests : IDisposable
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

    private static NoteFile FindNote(VaultService vault, string fileName) =>
        vault.GetNotes().Single(n => n.FullPath.EndsWith(fileName, StringComparison.Ordinal));

    [Fact]
    public void TwoOpenTabs_HaveDistinctUndoStackInstances()
    {
        var vault = CreateVault(out var vaultDir);
        File.WriteAllText(Path.Combine(vaultDir, "Uno.md"), "Uno");
        File.WriteAllText(Path.Combine(vaultDir, "Dos.md"), "Dos");

        var tabOne = new EditorViewModel(vault, FindNote(vault, "Uno.md"), onBack: () => { }, onChanged: () => { });
        var tabTwo = new EditorViewModel(vault, FindNote(vault, "Dos.md"), onBack: () => { }, onChanged: () => { });

        Assert.NotSame(tabOne.Document, tabTwo.Document);
        Assert.NotSame(tabOne.Document.UndoStack, tabTwo.Document.UndoStack);
    }

    [Fact]
    public void UndoingOneTab_DoesNotAffectAnotherTabsContentOrUndoState()
    {
        var vault = CreateVault(out var vaultDir);
        File.WriteAllText(Path.Combine(vaultDir, "Uno.md"), "Uno");
        File.WriteAllText(Path.Combine(vaultDir, "Dos.md"), "Dos");

        var tabOne = new EditorViewModel(vault, FindNote(vault, "Uno.md"), onBack: () => { }, onChanged: () => { });
        var tabTwo = new EditorViewModel(vault, FindNote(vault, "Dos.md"), onBack: () => { }, onChanged: () => { });

        tabOne.Document.Insert(tabOne.Document.TextLength, " editado");
        tabTwo.Document.Insert(tabTwo.Document.TextLength, " modificado");

        Assert.True(tabOne.Document.UndoStack.CanUndo);
        Assert.True(tabTwo.Document.UndoStack.CanUndo);

        tabOne.Document.UndoStack.Undo();

        Assert.Equal("Uno", tabOne.Document.Text);
        Assert.Equal("Dos modificado", tabTwo.Document.Text);
        Assert.False(tabOne.Document.UndoStack.CanUndo);
        Assert.True(tabTwo.Document.UndoStack.CanUndo);
    }

    [Fact]
    public void SameTab_RedoAfterUndo_RestoresTheEdit()
    {
        var vault = CreateVault(out var vaultDir);
        File.WriteAllText(Path.Combine(vaultDir, "Nota.md"), "Base");
        var note = vault.GetNotes().Single();
        var editor = new EditorViewModel(vault, note, onBack: () => { }, onChanged: () => { });

        editor.Document.Insert(editor.Document.TextLength, " extra");
        Assert.Equal("Base extra", editor.Document.Text);

        editor.Document.UndoStack.Undo();
        Assert.Equal("Base", editor.Document.Text);
        Assert.True(editor.Document.UndoStack.CanRedo);

        editor.Document.UndoStack.Redo();
        Assert.Equal("Base extra", editor.Document.Text);
    }
}
