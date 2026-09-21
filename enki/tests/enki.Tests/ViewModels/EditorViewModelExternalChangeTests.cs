using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>La tabla de política de una pestaña frente a un cambio hecho fuera de Enki
/// (.docs/project-info/cambios-externos-en-el-vault.md §5): sin cambios locales se recarga sola, con
/// cambios locales se pausa el autoguardado y se pregunta, y ninguna de las dos salidas del diálogo
/// destruye nada. <c>HandleExternalChange</c> se llama directamente en vez de levantar un
/// <see cref="VaultChangeWatcher"/> real: lo que se prueba aquí es la decisión, no la detección (que tiene
/// sus propias pruebas), y hacerlo así mantiene estas pruebas deterministas.</summary>
[Collection("LocalizationMutatingTests")]
public sealed class EditorViewModelExternalChangeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    private sealed record Fixture(
        VaultService Vault,
        SettingsService Settings,
        NotificationService Notifications,
        EditorViewModel Editor,
        NoteFile Note);

    private Fixture CreateEditor(string content = "# Nota\n\nOriginal.", bool autoReload = true)
    {
        var vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(vaultDir);

        var settings = new SettingsService(_tempDir);
        settings.Settings.VaultAutoReloadExternalChanges = autoReload;
        var vault = new VaultService(settings);
        vault.SetVaultPath(vaultDir);

        File.WriteAllText(Path.Combine(vaultDir, "Nota.md"), content);
        var note = vault.GetNotes().Single();

        var notificationsDir = Path.Combine(_tempDir, "notifications");
        var notifications = new NotificationService(new SettingsService(notificationsDir), notificationsDir);

        var editor = new EditorViewModel(vault, note, onBack: () => { }, onChanged: () => { },
            settingsService: settings, notificationService: notifications);
        return new Fixture(vault, settings, notifications, editor, note);
    }

    private static VaultExternalChange Modified(NoteFile note) =>
        new(note.FullPath, VaultExternalChangeKind.Modified);

    // ===================== Sin cambios locales =====================

    [Fact]
    public void ExternalChange_WithoutLocalEdits_ReloadsSilently()
    {
        var f = CreateEditor();
        File.WriteAllText(f.Note.FullPath, "# Nota\n\nDesde otro editor.");

        f.Editor.HandleExternalChange(Modified(f.Note));

        Assert.Equal("# Nota\n\nDesde otro editor.", f.Editor.Document.Text);
        Assert.False(f.Editor.IsDirty);
        Assert.False(f.Editor.IsExternalConflictOpen);
        Assert.False(f.Editor.IsAutosavePaused);
        Assert.Contains(f.Notifications.Notifications, n => n.Severity == NotificationSeverity.Info);
    }

    [Fact]
    public void ExternalChange_WithoutLocalEdits_KeepsTheCaretPosition()
    {
        var f = CreateEditor("# Nota\n\nOriginal con bastante texto.");
        f.Editor.RequestCaretAt(12);
        File.WriteAllText(f.Note.FullPath, "# Nota\n\nDesde otro editor, también largo.");

        f.Editor.HandleExternalChange(Modified(f.Note));

        Assert.Equal(12, f.Editor.LastCaretOffset);
    }

    /// <summary>Un texto más corto no puede dejar el cursor fuera del documento.</summary>
    [Fact]
    public void ExternalChange_WithAShorterFile_ClampsTheCaret()
    {
        var f = CreateEditor("# Nota\n\nUn contenido bastante largo para mover el cursor lejos.");
        f.Editor.RequestCaretAt(50);
        File.WriteAllText(f.Note.FullPath, "# Nota");

        f.Editor.HandleExternalChange(Modified(f.Note));

        Assert.Equal(f.Editor.Document.TextLength, f.Editor.LastCaretOffset);
    }

    /// <summary>Con la recarga automática apagada, el caso sin cambios locales también pregunta: el
    /// ajuste existe precisamente para quien no quiere que su texto cambie solo.</summary>
    [Fact]
    public void ExternalChange_WithoutLocalEdits_AndAutoReloadOff_AsksInstead()
    {
        var f = CreateEditor(autoReload: false);
        File.WriteAllText(f.Note.FullPath, "# Nota\n\nDesde otro editor.");

        f.Editor.HandleExternalChange(Modified(f.Note));

        Assert.True(f.Editor.IsExternalConflictOpen);
        Assert.True(f.Editor.IsAutosavePaused);
        Assert.Equal("# Nota\n\nOriginal.", f.Editor.Document.Text);
    }

    /// <summary>Si lo que llegó de fuera es exactamente lo que ya había en pantalla no hay nada que
    /// resolver ni que avisar.</summary>
    [Fact]
    public void ExternalChange_WithIdenticalContent_DoesNothing()
    {
        var f = CreateEditor();
        File.SetLastWriteTimeUtc(f.Note.FullPath, DateTime.UtcNow.AddMinutes(1));

        f.Editor.HandleExternalChange(Modified(f.Note));

        Assert.False(f.Editor.IsExternalConflictOpen);
        Assert.False(f.Editor.IsAutosavePaused);
        Assert.Empty(f.Notifications.Notifications);
    }

    // ===================== Con cambios locales =====================

    [Fact]
    public void ExternalChange_WithLocalEdits_OpensTheConflictDialog_AndPausesAutosave()
    {
        var f = CreateEditor();
        f.Editor.Document.Text = "# Nota\n\nEditado en Enki.";
        f.Editor.OnTextChanged("# Nota\n\nEditado en Enki.");
        File.WriteAllText(f.Note.FullPath, "# Nota\n\nDesde otro editor.");

        f.Editor.HandleExternalChange(Modified(f.Note));

        Assert.True(f.Editor.IsExternalConflictOpen);
        Assert.True(f.Editor.IsAutosavePaused);
        Assert.True(f.Editor.IsDirty);
        // El diálogo no toca el texto: decidir es del usuario, y hasta que decida su buffer sigue intacto.
        Assert.Equal("# Nota\n\nEditado en Enki.", f.Editor.Document.Text);
        Assert.Contains(f.Notifications.Notifications, n => n.Severity == NotificationSeverity.Warning);
    }

    /// <summary>La pausa tiene que ser real: mientras haya conflicto, guardar no toca el disco.</summary>
    [Fact]
    public void WhileTheConflictIsOpen_SaveDoesNotTouchTheFile()
    {
        var f = CreateEditor();
        f.Editor.OnTextChanged("# Nota\n\nEditado en Enki.");
        File.WriteAllText(f.Note.FullPath, "# Nota\n\nDesde otro editor.");
        f.Editor.HandleExternalChange(Modified(f.Note));

        f.Editor.Save("# Nota\n\nEditado en Enki.");
        f.Editor.FlushPendingSave();

        Assert.Equal("# Nota\n\nDesde otro editor.", File.ReadAllText(f.Note.FullPath));
    }

    [Fact]
    public void KeepMine_WritesTheLocalVersion_AndSnapshotsTheDiscardedOne()
    {
        var f = CreateEditor();
        f.Editor.Document.Text = "# Nota\n\nEditado en Enki.";
        f.Editor.OnTextChanged("# Nota\n\nEditado en Enki.");
        File.WriteAllText(f.Note.FullPath, "# Nota\n\nDesde otro editor.");
        f.Editor.HandleExternalChange(Modified(f.Note));

        f.Editor.KeepMineCommand.Execute(null);

        Assert.Equal("# Nota\n\nEditado en Enki.", File.ReadAllText(f.Note.FullPath));
        Assert.False(f.Editor.IsAutosavePaused);
        Assert.False(f.Editor.IsExternalConflictOpen);
        Assert.False(f.Editor.IsDirty);

        var discarded = f.Vault.GetVersionHistory(f.Note)
            .Where(v => v.Trigger == NoteVersionTrigger.ExternalConflict)
            .Select(v => f.Vault.ReadVersionContent(f.Note, v.Id));
        Assert.Contains("# Nota\n\nDesde otro editor.", discarded);
    }

    [Fact]
    public void LoadFromDisk_AdoptsTheExternalVersion_AndSnapshotsTheDiscardedOne()
    {
        var f = CreateEditor();
        f.Editor.Document.Text = "# Nota\n\nEditado en Enki.";
        f.Editor.OnTextChanged("# Nota\n\nEditado en Enki.");
        File.WriteAllText(f.Note.FullPath, "# Nota\n\nDesde otro editor.");
        f.Editor.HandleExternalChange(Modified(f.Note));

        f.Editor.LoadFromDiskCommand.Execute(null);

        Assert.Equal("# Nota\n\nDesde otro editor.", f.Editor.Document.Text);
        Assert.Equal("# Nota\n\nDesde otro editor.", File.ReadAllText(f.Note.FullPath));
        Assert.False(f.Editor.IsAutosavePaused);
        Assert.False(f.Editor.IsDirty);

        var discarded = f.Vault.GetVersionHistory(f.Note)
            .Where(v => v.Trigger == NoteVersionTrigger.ExternalConflict)
            .Select(v => f.Vault.ReadVersionContent(f.Note, v.Id));
        Assert.Contains("# Nota\n\nEditado en Enki.", discarded);
    }

    [Fact]
    public void ToggleExternalDiff_ComparesTheDiskVersionWithTheLocalOne()
    {
        var f = CreateEditor();
        f.Editor.Document.Text = "# Nota\n\nEditado en Enki.";
        f.Editor.OnTextChanged("# Nota\n\nEditado en Enki.");
        File.WriteAllText(f.Note.FullPath, "# Nota\n\nDesde otro editor.");
        f.Editor.HandleExternalChange(Modified(f.Note));

        f.Editor.ToggleExternalDiffCommand.Execute(null);

        Assert.True(f.Editor.IsExternalDiffVisible);
        Assert.Contains(f.Editor.ExternalDiffLines, l => l.Kind == DiffLineKind.Added && l.Text == "Editado en Enki.");
        Assert.Contains(f.Editor.ExternalDiffLines, l => l.Kind == DiffLineKind.Removed && l.Text == "Desde otro editor.");
    }

    /// <summary>"Decidir luego" cierra el diálogo pero no reanuda el autoguardado: la decisión sigue
    /// pendiente, y el aviso de la cabecera es lo que impide que esa pausa sea invisible.</summary>
    [Fact]
    public void PostponeExternalDecision_KeepsAutosavePaused_AndShowsTheBadge()
    {
        var f = CreateEditor();
        f.Editor.OnTextChanged("# Nota\n\nEditado en Enki.");
        File.WriteAllText(f.Note.FullPath, "# Nota\n\nDesde otro editor.");
        f.Editor.HandleExternalChange(Modified(f.Note));

        f.Editor.PostponeExternalDecisionCommand.Execute(null);

        Assert.False(f.Editor.IsExternalConflictOpen);
        Assert.True(f.Editor.IsAutosavePaused);
        Assert.True(f.Editor.ShowExternalConflictBadge);

        f.Editor.ReopenExternalConflictCommand.Execute(null);
        Assert.True(f.Editor.IsExternalConflictOpen);
        Assert.False(f.Editor.ShowExternalConflictBadge);
    }

    // ===================== Red de seguridad previa a guardar =====================

    /// <summary>Sin que el vigilante haya avisado de nada (búfer desbordado, unidad de red): el propio
    /// guardado se da cuenta, no escribe y abre el conflicto.</summary>
    [Fact]
    public void Save_WhenTheFileChangedBehindItsBack_DoesNotOverwrite_AndOpensTheConflict()
    {
        var f = CreateEditor();
        f.Editor.OnTextChanged("# Nota\n\nEditado en Enki.");
        File.WriteAllText(f.Note.FullPath, "# Nota\n\nDesde otro editor.");

        f.Editor.Save("# Nota\n\nEditado en Enki.");

        Assert.Equal("# Nota\n\nDesde otro editor.", File.ReadAllText(f.Note.FullPath));
        Assert.True(f.Editor.IsExternalConflictOpen);
        Assert.True(f.Editor.IsAutosavePaused);
        Assert.True(f.Editor.IsDirty);
    }

    [Fact]
    public void Save_WhenNothingChangedOutside_WritesNormally()
    {
        var f = CreateEditor();

        f.Editor.Save("# Nota\n\nEditado en Enki.");

        Assert.Equal("# Nota\n\nEditado en Enki.", File.ReadAllText(f.Note.FullPath));
        Assert.False(f.Editor.IsDirty);
        Assert.False(f.Editor.IsAutosavePaused);
    }

    /// <summary>Guardar varias veces seguidas es el caso normal del autoguardado: cada escritura adopta
    /// la huella nueva, así que la siguiente no se confunde con un cambio externo.</summary>
    [Fact]
    public void ConsecutiveSaves_DoNotTriggerAFalseConflict()
    {
        var f = CreateEditor();

        for (var i = 0; i < 5; i++) f.Editor.Save($"# Nota\n\nVersión {i}.");

        Assert.Equal("# Nota\n\nVersión 4.", File.ReadAllText(f.Note.FullPath));
        Assert.False(f.Editor.IsExternalConflictOpen);
        Assert.False(f.Editor.IsAutosavePaused);
    }

    // ===================== Borrado y renombrado desde fuera =====================

    [Fact]
    public void ExternalDelete_KeepsTheBufferOnScreen_AndPausesAutosave()
    {
        var f = CreateEditor();
        f.Editor.Document.Text = "# Nota\n\nEditado en Enki.";
        f.Editor.OnTextChanged("# Nota\n\nEditado en Enki.");
        File.Delete(f.Note.FullPath);

        f.Editor.HandleExternalChange(new VaultExternalChange(f.Note.FullPath, VaultExternalChangeKind.Deleted));

        Assert.True(f.Editor.IsExternallyDeleted);
        Assert.True(f.Editor.IsAutosavePaused);
        Assert.Equal("# Nota\n\nEditado en Enki.", f.Editor.Document.Text);
        Assert.Contains(f.Notifications.Notifications, n => n.Severity == NotificationSeverity.Warning);
    }

    [Fact]
    public void SaveAfterExternalDelete_RecreatesTheNoteWithWhatWasOnScreen()
    {
        var f = CreateEditor();
        f.Editor.Document.Text = "# Nota\n\nEditado en Enki.";
        f.Editor.OnTextChanged("# Nota\n\nEditado en Enki.");
        File.Delete(f.Note.FullPath);
        f.Editor.HandleExternalChange(new VaultExternalChange(f.Note.FullPath, VaultExternalChangeKind.Deleted));

        f.Editor.SaveAfterExternalDeleteCommand.Execute(null);

        Assert.True(File.Exists(f.Editor.Note.FullPath));
        Assert.Equal("# Nota\n\nEditado en Enki.", File.ReadAllText(f.Editor.Note.FullPath));
        Assert.False(f.Editor.IsExternallyDeleted);
        Assert.False(f.Editor.IsAutosavePaused);
        Assert.False(f.Editor.IsDirty);
    }

    [Fact]
    public void ExternalRename_FollowsTheNoteToItsNewPath()
    {
        var f = CreateEditor();
        var renamed = Path.Combine(Path.GetDirectoryName(f.Note.FullPath)!, "Nota renombrada.md");
        File.Move(f.Note.FullPath, renamed);

        f.Editor.HandleExternalChange(
            new VaultExternalChange(f.Note.FullPath, VaultExternalChangeKind.Renamed, renamed));

        Assert.Equal(renamed, f.Editor.Note.FullPath);
        Assert.Equal("Nota renombrada", f.Editor.Title);
        Assert.False(f.Editor.IsExternallyDeleted);
    }

    /// <summary>Cerrar la pestaña con una decisión pendiente no puede escribir el texto sobre la nota
    /// —para eso está la pausa— pero tampoco puede tirarlo: acaba en el historial de versiones.</summary>
    [Fact]
    public void Close_WithAnUnresolvedConflict_KeepsTheUnsavedTextInTheHistory()
    {
        var f = CreateEditor();
        f.Editor.Document.Text = "# Nota\n\nEditado en Enki.";
        f.Editor.OnTextChanged("# Nota\n\nEditado en Enki.");
        File.WriteAllText(f.Note.FullPath, "# Nota\n\nDesde otro editor.");
        f.Editor.HandleExternalChange(Modified(f.Note));

        f.Editor.Close();

        Assert.Equal("# Nota\n\nDesde otro editor.", File.ReadAllText(f.Note.FullPath));
        var kept = f.Vault.GetVersionHistory(f.Note)
            .Where(v => v.Trigger == NoteVersionTrigger.ExternalConflict)
            .Select(v => f.Vault.ReadVersionContent(f.Note, v.Id));
        Assert.Contains("# Nota\n\nEditado en Enki.", kept);
    }

    /// <summary>Un movimiento a una carpeta que el vigilante no cubre llega como renombrado pero con un
    /// destino que ya no es una nota del vault: se trata como un borrado, que es lo que el usuario ve.</summary>
    [Fact]
    public void ExternalRename_ToSomewhereUnreachable_IsTreatedAsADelete()
    {
        var f = CreateEditor();
        var outside = Path.Combine(_tempDir, "fuera-del-vault.md");
        File.Move(f.Note.FullPath, outside);

        f.Editor.HandleExternalChange(
            new VaultExternalChange(f.Note.FullPath, VaultExternalChangeKind.Renamed, Path.Combine(_tempDir, "no-existe.md")));

        Assert.True(f.Editor.IsExternallyDeleted);
        Assert.True(f.Editor.IsAutosavePaused);
    }
}
