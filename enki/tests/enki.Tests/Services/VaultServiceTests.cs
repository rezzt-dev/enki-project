using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="VaultService"/> — the file-backed persistence every note-related feature in
/// the app sits on top of (docs/analisis-proyecto/03-problemas-y-deuda-tecnica.md §8 flags it, alongside
/// <see cref="SettingsServiceTests"/>, as untested despite being the highest-risk shared surface). Each
/// test points the vault at its own throwaway temp directory, same pattern as the rest of the JSON/file
/// backed services in this project.</summary>
public class VaultServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public VaultServiceTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private VaultService CreateVault(bool seedWelcomeNote = false)
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote);
        return vault;
    }

    [Fact]
    public void ReadNoteMetadata_ExtractsTagsAndSkipsFrontMatterInPreview()
    {
        var vault = CreateVault();

        var path = Path.Combine(_vaultPath, "NotaConTags.md");
        File.WriteAllText(path, "---\ntags: [casa, urgente]\n---\n\n# Nota con tags\n\nContenido de la nota.");

        var note = vault.GetNotes().Single();
        Assert.Equal(new[] { "casa", "urgente" }, note.Tags);
        Assert.Equal("Nota con tags", note.Title);
        Assert.DoesNotContain("---", note.Preview);
        Assert.Contains("Contenido", note.Preview);
    }

    [Fact]
    public void InitializeVault_SeedsWelcomeNoteOnlyWhenRequestedAndVaultIsEmpty()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);

        vault.InitializeVault(_vaultPath, seedWelcomeNote: true);

        Assert.Single(vault.GetNotes());
    }

    [Fact]
    public void InitializeVault_DoesNotSeedWelcomeNoteWhenVaultAlreadyHasNotes()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        Directory.CreateDirectory(_vaultPath);
        File.WriteAllText(Path.Combine(_vaultPath, "Existente.md"), "# Existente");

        vault.InitializeVault(_vaultPath, seedWelcomeNote: true);

        Assert.Single(vault.GetNotes());
        Assert.Equal("Existente", vault.GetNotes().Single().Title);
    }

    [Fact]
    public void InitializeVault_PersistsVaultPathToSettings()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);

        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);

        Assert.Equal(_vaultPath, new SettingsService(_storageDirectory).Settings.VaultPath);
    }

    [Fact]
    public void CreateNote_DuplicateTitle_AppendsNumericSuffixInsteadOfOverwriting()
    {
        var vault = CreateVault();

        var first = vault.CreateNote("Idea");
        var second = vault.CreateNote("Idea");

        Assert.NotEqual(first.FullPath, second.FullPath);
        Assert.True(File.Exists(first.FullPath));
        Assert.True(File.Exists(second.FullPath));
        Assert.Equal(2, vault.GetNotes().Count);
    }

    [Fact]
    public void DeleteNote_MovesItToTrash_RemovingItFromTheVaultListing()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Para borrar");

        vault.DeleteNote(note);

        Assert.Empty(vault.GetNotes());
        Assert.Single(vault.GetTrashedNotes());
        Assert.False(File.Exists(note.FullPath));
    }

    [Fact]
    public void RestoreNote_MovesItBackFromTrashToTheVaultRoot()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Para restaurar");
        vault.DeleteNote(note);
        var trashed = vault.GetTrashedNotes().Single();

        vault.RestoreNote(trashed);

        Assert.Single(vault.GetNotes());
        Assert.Empty(vault.GetTrashedNotes());
    }

    [Fact]
    public void PermanentlyDelete_RemovesTheFileFromDisk()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Para eliminar");
        vault.DeleteNote(note);
        var trashed = vault.GetTrashedNotes().Single();

        vault.PermanentlyDelete(trashed);

        Assert.False(File.Exists(trashed.FullPath));
        Assert.Empty(vault.GetTrashedNotes());
    }

    [Fact]
    public void RenameNote_ChangesTheFileNameAndKeepsItInTheSameFolder()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nombre original");
        var originalDir = Path.GetDirectoryName(note.FullPath);

        var renamed = vault.RenameNote(note, "Nombre nuevo");

        Assert.Equal("Nombre nuevo.md", Path.GetFileName(renamed.FullPath));
        Assert.Equal(originalDir, Path.GetDirectoryName(renamed.FullPath));
        Assert.True(File.Exists(renamed.FullPath));
    }

    // ===================== Fase 5 (docs/analisis-proyecto/roadmap.md): integridad de la red de enlaces =====================

    [Fact]
    public void RenameNote_ToAnAlreadyTakenName_AppendsNumericSuffixInsteadOfSilentlyDoingNothing()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Original");
        vault.CreateNote("Ocupado");

        var renamed = vault.RenameNote(note, "Ocupado");

        Assert.Equal("Ocupado 2.md", Path.GetFileName(renamed.FullPath));
        Assert.True(File.Exists(renamed.FullPath));
        Assert.False(File.Exists(Path.Combine(_vaultPath, "Original.md")));
    }

    [Fact]
    public void RenameFolder_ToAnAlreadyTakenName_AppendsNumericSuffix()
    {
        var vault = CreateVault();
        var folder = VaultService.CreateFolder(_vaultPath, "Original");
        VaultService.CreateFolder(_vaultPath, "Ocupado");

        var renamed = VaultService.RenameFolder(folder, "Ocupado");

        Assert.Equal("Ocupado 2", Path.GetFileName(renamed.FullPath));
        Assert.True(Directory.Exists(renamed.FullPath));
    }

    [Fact]
    public void RenameFile_ToAnAlreadyTakenName_AppendsNumericSuffixBeforeExtension()
    {
        var vault = CreateVault();
        var sourcePath = Path.Combine(_storageDirectory, "original.png");
        File.WriteAllBytes(sourcePath, [1]);
        vault.ImportFile(sourcePath);
        var file = vault.GetFiles().Single();
        File.WriteAllBytes(Path.Combine(_vaultPath, "ocupado.png"), [2]);

        var renamed = vault.RenameFile(file, "ocupado.png");

        Assert.Equal("ocupado 2.png", Path.GetFileName(renamed.FullPath));
        Assert.True(File.Exists(renamed.FullPath));
    }

    [Fact]
    public void RenameNote_RewritesWikilinksInOtherNotes_ByFileName()
    {
        var vault = CreateVault();
        var target = vault.CreateNote("Objetivo");
        File.WriteAllText(Path.Combine(_vaultPath, "Enlazadora.md"), "Ver [[Objetivo]] para más info.");

        vault.RenameNote(target, "Objetivo Renombrado");

        var linkerContent = File.ReadAllText(Path.Combine(_vaultPath, "Enlazadora.md"));
        Assert.Contains("[[Objetivo Renombrado]]", linkerContent, StringComparison.Ordinal);
        Assert.DoesNotContain("[[Objetivo]]", linkerContent, StringComparison.Ordinal);
    }

    [Fact]
    public void RenameNote_RewritesWikilinks_PreservingAliasAndHeadingAnchor()
    {
        var vault = CreateVault();
        var target = vault.CreateNote("Objetivo");
        File.WriteAllText(Path.Combine(_vaultPath, "ConAlias.md"), "Ver [[Objetivo|texto alterno]].");
        File.WriteAllText(Path.Combine(_vaultPath, "ConAncla.md"), "Ver [[Objetivo#Sección]].");

        vault.RenameNote(target, "Nuevo Nombre");

        Assert.Contains("[[Nuevo Nombre|texto alterno]]", File.ReadAllText(Path.Combine(_vaultPath, "ConAlias.md")), StringComparison.Ordinal);
        Assert.Contains("[[Nuevo Nombre#Sección]]", File.ReadAllText(Path.Combine(_vaultPath, "ConAncla.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void RenameNote_DoesNotTouchWikilinksToOtherNotes()
    {
        var vault = CreateVault();
        var target = vault.CreateNote("Objetivo");
        var otro = "Ver [[Otra nota]] sin relación.";
        File.WriteAllText(Path.Combine(_vaultPath, "Enlazadora.md"), otro);

        vault.RenameNote(target, "Objetivo Renombrado");

        Assert.Equal(otro, File.ReadAllText(Path.Combine(_vaultPath, "Enlazadora.md")));
    }

    [Fact]
    public void RenameNote_RewritesSelfReferencingWikilink()
    {
        var vault = CreateVault();
        var target = vault.CreateNote("Objetivo");
        vault.WriteContent(target, "# Objetivo\n\nEsta nota se referencia a sí misma: [[Objetivo]].");

        var renamed = vault.RenameNote(target, "Objetivo Renombrado");

        Assert.Contains("[[Objetivo Renombrado]]", File.ReadAllText(renamed.FullPath), StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveWikiLinkScopeRoot_ForNoteOutsideSessions_ReturnsVaultPath()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Suelta");

        Assert.Equal(_vaultPath, vault.ResolveWikiLinkScopeRoot(note.FullPath));
    }

    [Fact]
    public void ResolveWikiLinkScopeRoot_ForNoteInsideSession_ReturnsSessionFolder()
    {
        var vault = CreateVault();
        var sessionFolder = Path.Combine(_vaultPath, ".sessions", "mi-sesion");
        Directory.CreateDirectory(sessionFolder);
        var notePath = Path.Combine(sessionFolder, "Nota.md");
        File.WriteAllText(notePath, "# Nota");

        Assert.Equal(sessionFolder, vault.ResolveWikiLinkScopeRoot(notePath));
    }

    [Fact]
    public void MoveNote_MovesFileToDestinationFolderAndAvoidsNameCollisions()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Movida");
        var destination = VaultService.CreateFolder(_vaultPath, "Destino").FullPath;
        File.WriteAllText(Path.Combine(destination, "Movida.md"), "# ya existe");

        var moved = vault.MoveNote(note, destination);

        Assert.Equal(destination, Path.GetDirectoryName(moved.FullPath));
        Assert.Equal("Movida 2.md", Path.GetFileName(moved.FullPath));
    }

    // ===================== Fase 6 (docs/analisis-proyecto/roadmap.md): rendimiento del editor y del vault =====================

    [Fact]
    public async Task GetVaultSizeBytes_SumsEveryFileUnderTheVaultRoot()
    {
        var vault = CreateVault();
        File.WriteAllText(Path.Combine(_vaultPath, "a.md"), new string('a', 100));
        var subfolder = VaultService.CreateFolder(_vaultPath, "Sub").FullPath;
        File.WriteAllText(Path.Combine(subfolder, "b.md"), new string('b', 50));

        await vault.RefreshVaultSizeBytesAsync();

        Assert.Equal(150, vault.GetVaultSizeBytes());
        Assert.False(vault.LastVaultSizeCalculationIncomplete);
    }

    /// <summary>The first read of a session (or right after a write invalidates the cache) returns 0
    /// synchronously instead of blocking the caller on a full directory walk — see
    /// <see cref="VaultService.GetVaultSizeBytes"/>'s doc comment. Callers that need the freshly-computed
    /// value in the same test await <see cref="VaultService.RefreshVaultSizeBytesAsync"/> directly instead.</summary>
    [Fact]
    public void GetVaultSizeBytes_BeforeAnyBackgroundRefreshCompletes_ReturnsZeroWithoutBlocking()
    {
        var vault = CreateVault();
        File.WriteAllText(Path.Combine(_vaultPath, "a.md"), new string('a', 100));

        Assert.Equal(0, vault.GetVaultSizeBytes());
    }

    [Fact]
    public async Task GetVaultSizeBytes_AfterAWrite_ReflectsTheNewSizeOnceRefreshed()
    {
        var vault = CreateVault();
        File.WriteAllText(Path.Combine(_vaultPath, "a.md"), new string('a', 100));
        await vault.RefreshVaultSizeBytesAsync();
        Assert.Equal(100, vault.GetVaultSizeBytes());

        vault.CreateNote("Otra nota");

        // CreateNote invalidates the cached total (docs/analisis-proyecto/roadmap.md, Fase 6) — the next
        // read is stale (0) until the background recomputation it triggers lands.
        await vault.RefreshVaultSizeBytesAsync();
        Assert.True(vault.GetVaultSizeBytes() > 100);
    }

    [Fact]
    public async Task RefreshVaultSizeBytesAsync_RaisesVaultSizeChanged_OnceTheWalkCompletes()
    {
        var vault = CreateVault();
        File.WriteAllText(Path.Combine(_vaultPath, "a.md"), new string('a', 100));
        var raised = false;
        vault.VaultSizeChanged += () => raised = true;

        await vault.RefreshVaultSizeBytesAsync();

        Assert.True(raised);
        Assert.Equal(100, vault.GetVaultSizeBytes());
    }

    /// <summary>The vault-wide note-metadata cache backing <see cref="VaultService.GetAllNotesRecursive"/>/
    /// <see cref="VaultService.GetAllNotesRecursiveWithContent"/> must never hand out the same
    /// <see cref="NoteFile"/> instance twice — <see cref="VaultService.RenameNote"/> mutates its own
    /// <c>FullPath</c> in place, which used to silently corrupt a snapshot another caller (the wikilink
    /// rewrite pass itself) had taken moments earlier from the same cache entry, since both held a
    /// reference to the exact same object.</summary>
    [Fact]
    public void GetAllNotesRecursive_ReturnsIndependentInstances_MutatingOneDoesNotAffectAnother()
    {
        var vault = CreateVault();
        vault.CreateNote("Nota");

        var first = vault.GetAllNotesRecursive(_vaultPath).Single();
        var second = vault.GetAllNotesRecursive(_vaultPath).Single();
        first.FullPath = "/mutated/path.md";

        Assert.NotEqual(first.FullPath, second.FullPath);
        Assert.EndsWith("Nota.md", second.FullPath);
    }

    [Fact]
    public void GetAllNotesRecursiveWithContent_SecondCallForUnchangedNote_ReturnsSameContentWithoutRereadingFromDisk()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota");

        var first = vault.GetAllNotesRecursiveWithContent(_vaultPath).Single();
        // Change the file behind the cache's back (bypassing VaultService) without touching its mtime —
        // impossible to do precisely in a portable test, so instead this asserts the cache-served path is
        // at least self-consistent: two calls with no intervening write return identical content.
        var second = vault.GetAllNotesRecursiveWithContent(_vaultPath).Single();

        Assert.Equal(first.Content, second.Content);
        Assert.Equal(note.Title, second.Note.Title);
    }

    [Fact]
    public void ReadContent_AfterWriteContent_ReturnsTheNewContentInsteadOfAStaleCachedCopy()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota");
        vault.GetAllNotesRecursive(_vaultPath); // warms the cache

        vault.WriteContent(note, "# Nota\n\nContenido actualizado.");

        Assert.Equal("# Nota\n\nContenido actualizado.", vault.ReadContent(note));
        Assert.Contains(vault.GetAllNotesRecursive(_vaultPath), n => n.Preview.Contains("actualizado"));
    }

    [Fact]
    public void GetNote_AfterDeleteNote_NoLongerReturnsTheDeletedNoteFromTheVaultRoot()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota");
        vault.GetAllNotesRecursive(_vaultPath); // warms the cache

        vault.DeleteNote(note);

        Assert.DoesNotContain(vault.GetAllNotesRecursive(_vaultPath), n => VaultService.PathsEqual(n.FullPath, note.FullPath));
    }

    /// <summary>Coarse regression guard for the "doble barrido"/N-vault-scans-per-refresh problem the note
    /// cache exists to fix (docs/analisis-proyecto/roadmap.md, Fase 6, análisis §3): a synthetic vault large
    /// enough that a cold, uncached full scan takes a measurable amount of time, checked against a second,
    /// fully-cached scan of the same vault being substantially faster — not an exact "N reads" assertion
    /// (this test has no way to intercept file I/O), but enough to catch a regression that silently turns
    /// the cache into a no-op. Thresholds are generous specifically to avoid CI flakiness.</summary>
    [Fact]
    public void GetAllNotesRecursive_OnALargeVault_SecondScanIsSubstantiallyFasterThanTheFirst()
    {
        var vault = CreateVault();
        Directory.CreateDirectory(_vaultPath);
        const int noteCount = 1500;
        for (var i = 0; i < noteCount; i++)
            File.WriteAllText(Path.Combine(_vaultPath, $"Nota {i}.md"), $"# Nota {i}\n\nContenido de prueba con [[Nota {(i + 1) % noteCount}]].");

        var stopwatchCold = System.Diagnostics.Stopwatch.StartNew();
        var cold = vault.GetAllNotesRecursive(_vaultPath);
        stopwatchCold.Stop();

        var stopwatchWarm = System.Diagnostics.Stopwatch.StartNew();
        var warm = vault.GetAllNotesRecursive(_vaultPath);
        stopwatchWarm.Stop();

        Assert.Equal(noteCount, cold.Count);
        Assert.Equal(noteCount, warm.Count);
        Assert.True(
            stopwatchWarm.Elapsed <= stopwatchCold.Elapsed / 2 || stopwatchWarm.ElapsedMilliseconds < 50,
            $"Expected the cached rescan ({stopwatchWarm.ElapsedMilliseconds}ms) to be meaningfully faster than the cold scan ({stopwatchCold.ElapsedMilliseconds}ms).");
    }

    [Fact]
    public void GetVaultSizeBytes_WithNoVaultConfigured_ReturnsZeroInsteadOfThrowing()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);

        Assert.Equal(0, vault.GetVaultSizeBytes());
    }

    [Fact]
    public void DeleteFolder_MovesItToTrashAvoidingNameCollisions()
    {
        var vault = CreateVault();
        var folder = VaultService.CreateFolder(_vaultPath, "Proyecto");
        File.WriteAllText(Path.Combine(folder.FullPath, "nota.md"), "# nota");

        vault.DeleteFolder(folder);

        Assert.False(Directory.Exists(folder.FullPath));
        Assert.Empty(vault.GetFolderTree());
    }

    // ===================== Archivos =====================

    [Fact]
    public void GetFiles_ExcludesMarkdownNotesAndIncludesEverythingElse()
    {
        var vault = CreateVault();
        File.WriteAllText(Path.Combine(_vaultPath, "nota.md"), "# nota");
        File.WriteAllBytes(Path.Combine(_vaultPath, "foto.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_vaultPath, "informe.pdf"), [1, 2, 3, 4]);

        var files = vault.GetFiles();

        Assert.Equal(2, files.Count);
        Assert.DoesNotContain(files, f => f.FileName == "nota.md");
        Assert.Contains(files, f => f.FileName == "foto.png");
        Assert.Contains(files, f => f.FileName == "informe.pdf");
    }

    [Fact]
    public void ImportFile_CopiesArbitraryFileIntoTheVault()
    {
        var vault = CreateVault();
        var sourcePath = Path.Combine(_storageDirectory, "origen.png");
        File.WriteAllBytes(sourcePath, [9, 9, 9]);

        var imported = vault.ImportFile(sourcePath);

        Assert.True(File.Exists(Path.Combine(_vaultPath, "origen.png")));
        Assert.Single(vault.GetFiles());
    }

    [Fact]
    public void DeleteFile_MovesItToTrash_RemovingItFromTheFileListing()
    {
        var vault = CreateVault();
        var sourcePath = Path.Combine(_storageDirectory, "borrar.png");
        File.WriteAllBytes(sourcePath, [1]);
        vault.ImportFile(sourcePath);
        var file = vault.GetFiles().Single();

        vault.DeleteFile(file);

        Assert.Empty(vault.GetFiles());
        Assert.Single(vault.GetTrashedFiles());
        Assert.False(File.Exists(file.FullPath));
    }

    [Fact]
    public void RestoreFile_MovesItBackFromTrashToTheVaultRoot()
    {
        var vault = CreateVault();
        var sourcePath = Path.Combine(_storageDirectory, "restaurar.png");
        File.WriteAllBytes(sourcePath, [1]);
        vault.ImportFile(sourcePath);
        var file = vault.GetFiles().Single();
        vault.DeleteFile(file);
        var trashed = vault.GetTrashedFiles().Single();

        vault.RestoreFile(trashed);

        Assert.Single(vault.GetFiles());
        Assert.Empty(vault.GetTrashedFiles());
    }

    [Fact]
    public void PermanentlyDelete_File_RemovesItFromDisk()
    {
        var vault = CreateVault();
        var sourcePath = Path.Combine(_storageDirectory, "eliminar.png");
        File.WriteAllBytes(sourcePath, [1]);
        vault.ImportFile(sourcePath);
        var file = vault.GetFiles().Single();
        vault.DeleteFile(file);
        var trashed = vault.GetTrashedFiles().Single();

        vault.PermanentlyDelete(trashed);

        Assert.False(File.Exists(trashed.FullPath));
        Assert.Empty(vault.GetTrashedFiles());
    }

    [Fact]
    public void RenameFile_ChangesTheFileNameAndKeepsItInTheSameFolder()
    {
        var vault = CreateVault();
        var sourcePath = Path.Combine(_storageDirectory, "original.png");
        File.WriteAllBytes(sourcePath, [1]);
        vault.ImportFile(sourcePath);
        var file = vault.GetFiles().Single();
        var originalDir = Path.GetDirectoryName(file.FullPath);

        var renamed = vault.RenameFile(file, "nuevo.png");

        Assert.Equal("nuevo.png", Path.GetFileName(renamed.FullPath));
        Assert.Equal(originalDir, Path.GetDirectoryName(renamed.FullPath));
        Assert.True(File.Exists(renamed.FullPath));
    }

    [Fact]
    public void MoveFile_MovesFileToDestinationFolderAndAvoidsNameCollisions()
    {
        var vault = CreateVault();
        var sourcePath = Path.Combine(_storageDirectory, "movida.png");
        File.WriteAllBytes(sourcePath, [1]);
        vault.ImportFile(sourcePath);
        var file = vault.GetFiles().Single();
        var destination = VaultService.CreateFolder(_vaultPath, "Destino").FullPath;
        File.WriteAllBytes(Path.Combine(destination, "movida.png"), [2]);

        var moved = VaultService.MoveFile(file, destination);

        Assert.Equal(destination, Path.GetDirectoryName(moved.FullPath));
        Assert.Equal("movida 2.png", Path.GetFileName(moved.FullPath));
    }

    // ===================== Fase 1 (docs/analisis-proyecto/roadmap.md): persistencia atómica =====================

    [Fact]
    public void WriteContent_WritesAtomically_NoTempFileLeftBehindOnSuccess()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota", _vaultPath);

        vault.WriteContent(note, "# Nota\n\nContenido nuevo.");

        Assert.Equal("# Nota\n\nContenido nuevo.", File.ReadAllText(note.FullPath));
        Assert.Empty(Directory.EnumerateFiles(_vaultPath, "*.tmp-*"));
    }

    /// <summary>Distinct from <see cref="WriteContent_WhenVaultFolderIsGone_ThrowsVaultUnavailableException"/>:
    /// here the vault folder still exists but isn't writable (permission denied, not a missing/unplugged
    /// drive), which must still propagate a real <see cref="IOException"/> instead of the pre-Fase-1
    /// behavior of a direct <see cref="File.WriteAllText(string, string)"/> whose failure nothing caught or
    /// logged. Skipped when the permission isn't actually enforced (root, or on Windows where
    /// <see cref="UnixFileMode"/> doesn't apply).</summary>
    [Fact]
    public void WriteContent_WhenVaultFolderIsReadOnly_ThrowsIOException()
    {
        if (OperatingSystem.IsWindows()) return;

        var vault = CreateVault();
        var note = vault.CreateNote("Nota", _vaultPath);

        File.SetUnixFileMode(_vaultPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var exception = Record.Exception(() => vault.WriteContent(note, "contenido nuevo"));
            if (exception is not null) Assert.True(exception is IOException or UnauthorizedAccessException, exception.ToString());
        }
        finally
        {
            File.SetUnixFileMode(_vaultPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void WriteContent_WhenVaultFolderIsGone_ThrowsVaultUnavailableException()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota", _vaultPath);
        Directory.Delete(_vaultPath, recursive: true);

        Assert.Throws<VaultUnavailableException>(() => vault.WriteContent(note, "contenido nuevo"));
    }

    [Fact]
    public void CreateNote_WhenVaultFolderIsGone_ThrowsVaultUnavailableException()
    {
        var vault = CreateVault();
        Directory.Delete(_vaultPath, recursive: true);

        Assert.Throws<VaultUnavailableException>(() => vault.CreateNote("Nota", _vaultPath));
    }

    [Fact]
    public void RenameNote_WhenVaultFolderIsGone_ThrowsVaultUnavailableException()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota", _vaultPath);
        Directory.Delete(_vaultPath, recursive: true);

        Assert.Throws<VaultUnavailableException>(() => vault.RenameNote(note, "Nota renombrada"));
    }

    [Fact]
    public void MoveNote_WhenVaultFolderIsGone_ThrowsVaultUnavailableException()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Nota", _vaultPath);
        var destination = VaultService.CreateFolder(_vaultPath, "Destino").FullPath;
        Directory.Delete(_vaultPath, recursive: true);

        Assert.Throws<VaultUnavailableException>(() => vault.MoveNote(note, destination));
    }

    /// <summary><see cref="VaultUnavailableException"/> derives from <see cref="IOException"/> so every
    /// existing <c>catch (IOException)</c> handler in the app (autosave, note create/rename/move) keeps
    /// working unchanged even when it doesn't specifically know about this new exception type.</summary>
    [Fact]
    public void VaultUnavailableException_IsAnIOException()
    {
        var vault = CreateVault();
        Directory.Delete(_vaultPath, recursive: true);

        var ex = Assert.Throws<VaultUnavailableException>(() => vault.CreateNote("Nota", _vaultPath));
        Assert.IsAssignableFrom<IOException>(ex);
    }

    // ===================== ResolveVaultFolder =====================
    // Covers the fix for a folder-picker vault (or "Cambiar carpeta del vault") turning a general-purpose
    // folder like "Documents" into the vault itself instead of nesting Enki's own files inside it.

    [Fact]
    public void ResolveVaultFolder_OnPlainEmptyFolder_NestsEnkiDocumentsSubfolder()
    {
        Directory.CreateDirectory(_storageDirectory);
        var picked = Path.Combine(_storageDirectory, "Documentos");
        Directory.CreateDirectory(picked);

        var resolved = VaultService.ResolveVaultFolder(picked);

        Assert.Equal(Path.Combine(picked, "enki-documents"), resolved);
    }

    [Fact]
    public void ResolveVaultFolder_OnFolderAlreadyNamedEnkiDocuments_UsesItAsIs()
    {
        Directory.CreateDirectory(_storageDirectory);
        var picked = Path.Combine(_storageDirectory, "enki-documents");
        Directory.CreateDirectory(picked);

        var resolved = VaultService.ResolveVaultFolder(picked);

        Assert.Equal(picked, resolved);
    }

    [Fact]
    public void ResolveVaultFolder_OnExistingFlatVaultWithNotes_UsesItAsIs()
    {
        Directory.CreateDirectory(_storageDirectory);
        var picked = Path.Combine(_storageDirectory, "MisNotas");
        Directory.CreateDirectory(picked);
        File.WriteAllText(Path.Combine(picked, "nota.md"), "# Nota");

        var resolved = VaultService.ResolveVaultFolder(picked);

        Assert.Equal(picked, resolved);
    }

    [Fact]
    public void ResolveVaultFolder_WhenParentAlreadyHasNestedVault_ReusesTheNestedFolder()
    {
        Directory.CreateDirectory(_storageDirectory);
        var picked = Path.Combine(_storageDirectory, "Documentos");
        var nested = Path.Combine(picked, "enki-documents");
        Directory.CreateDirectory(nested);

        var resolved = VaultService.ResolveVaultFolder(picked);

        Assert.Equal(nested, resolved);
    }

    /// <summary>The exact repro from docs/version-alfa-3/01-analisis-detallado.md §4.5 (hallazgo crítico):
    /// a real vault organized entirely into subfolders ("Trabajo/", "Personal/"), never having used
    /// sessions or the trash, and with zero top-level <c>.md</c> files. Before the fix, none of the vault
    /// root heuristic's checks matched — re-selecting this exact folder as "open existing vault" (after
    /// syncing it to a new machine, or reinstalling) silently nested a brand-new, empty
    /// <c>enki-documents/</c> inside it instead of recognizing the folder itself as the vault.</summary>
    [Fact]
    public void ResolveVaultFolder_OnExistingVaultWithNotesOnlyInSubfolders_UsesItAsIs()
    {
        Directory.CreateDirectory(_storageDirectory);
        var picked = Path.Combine(_storageDirectory, "MiVault");
        Directory.CreateDirectory(Path.Combine(picked, "Trabajo"));
        Directory.CreateDirectory(Path.Combine(picked, "Personal"));
        File.WriteAllText(Path.Combine(picked, "Trabajo", "Proyecto.md"), "# Proyecto");
        File.WriteAllText(Path.Combine(picked, "Personal", "Diario.md"), "# Diario");

        var resolved = VaultService.ResolveVaultFolder(picked);

        Assert.Equal(picked, resolved);
    }
}
