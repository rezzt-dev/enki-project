using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Qué acepta cada destino de arrastre de la sección Archivos, en sus dos disposiciones y sobre el
/// XAML compilado de verdad. Es la única capa donde se puede comprobar: el reparto de "esto se mueve, esto
/// se importa, esto no se acepta" vive entero en el code-behind de las vistas
/// (<see cref="FilesBrowserView"/>/<see cref="FilesTreeView"/>), que es lo que traduce el gesto a la llamada
/// al viewmodel, así que un test de solo viewmodel no lo ve.
///
/// <para>El caso que dio origen a estos tests es la asimetría entre las dos disposiciones: las filas de
/// carpeta del modo explorador aceptaban el arrastre interno pero no lo que llegaba del explorador del
/// sistema, mientras que las del modo árbol sí lo importaban a la carpeta soltada. La misma carpeta,
/// arrastrada lo mismo desde el mismo sitio, aceptaba o rechazaba según la disposición desde la que se
/// estuviera mirando.</para></summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class FilesDropTargetUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _vaultPath;

    public FilesDropTargetUiTests()
    {
        HeadlessApp.EnsureInitialized();
        _vaultPath = Path.Combine(_tempDir, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (FilesViewModel ViewModel, SessionService Sessions, Window Window) CreateView(bool treeMode)
    {
        var settings = new SettingsService(_tempDir);
        settings.Settings.FilesViewMode = treeMode ? "Tree" : "Browser";
        settings.Save();

        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _tempDir);
        var viewModel = new FilesViewModel(vault, sessions, notifications, settings);

        var window = new Window { Content = new FilesView { DataContext = viewModel }, Width = 1400, Height = 900 };
        window.Show();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        return (viewModel, sessions, window);
    }

    /// <summary>Una carpeta "diseno" vacía dentro del vault, que es la que recibe lo que se suelte.</summary>
    private string SeedFolder()
    {
        var folder = Path.Combine(_vaultPath, "diseno");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Un fichero fuera del vault, como el que se arrastraría desde el explorador del sistema.</summary>
    private string SeedExternalFile(string name = "externo.png")
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    // ===================== Arrastres de mentira =====================

    /// <summary>Un arrastre entrante del explorador del sistema: el mismo <see cref="DataFormat.File"/> que
    /// pone la plataforma, con ficheros reales resueltos por el <see cref="IStorageProvider"/> del propio
    /// TopLevel — así lo que llega al code-behind es lo mismo que llegaría de verdad, incluido que
    /// <c>TryGetLocalPath</c> devuelva una ruta.</summary>
    private static async Task<DataTransfer> OsDragAsync(Window window, params string[] paths)
    {
        var transfer = new DataTransfer();
        foreach (var path in paths)
        {
            var file = await window.StorageProvider.TryGetFileFromPathAsync(path);
            Assert.NotNull(file);
            transfer.Add(DataTransferItem.CreateFile(file));
        }
        return transfer;
    }

    /// <summary>Un arrastre interno de una tarjeta/fila de la propia sección.</summary>
    private static DataTransfer InternalDrag(string vaultFilePath)
    {
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(FilesBrowserView.FileDragFormat, vaultFilePath));
        return transfer;
    }

    private static DragDropEffects RaiseDragOver(Interactive target, IDataTransfer transfer)
    {
        var args = new DragEventArgs(DragDrop.DragOverEvent, transfer, target, default, KeyModifiers.None);
        target.RaiseEvent(args);
        return args.DragEffects;
    }

    private static void RaiseDrop(Interactive target, IDataTransfer transfer) =>
        target.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, transfer, target, default, KeyModifiers.None));

    /// <summary>La fila de la carpeta <paramref name="name"/> en el panel lateral, sea del modo explorador
    /// (<see cref="FileFolderViewModel"/>) o del modo árbol (<see cref="FileTreeItemViewModel"/>).</summary>
    private static Border FolderRow(Window window, string name) =>
        window.GetVisualDescendants().OfType<Border>().First(b => b.DataContext switch
        {
            FileFolderViewModel folder => folder.Name == name,
            FileTreeItemViewModel node => node.IsFolder && node.Name == name,
            _ => false,
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InternalFolderDrag_ShowsFloatingName_AndMovesTheWholeFolder(bool treeMode)
    {
        SeedFolder();
        var source = Path.Combine(_vaultPath, "origen");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "archivo.txt"), "contenido");
        var (_, _, window) = CreateView(treeMode);
        var destination = FolderRow(window, "diseno");
        using var transfer = InternalDrag(source);

        Assert.Equal(DragDropEffects.Move, RaiseDragOver(destination, transfer));
        Assert.Contains(window.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("dropdown-popover") && border.IsVisible &&
            border.Child is StackPanel panel && panel.Children.OfType<TextBlock>().Any(text => text.Text == "origen"));

        RaiseDrop(destination, transfer);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("dropdown-popover") && border.IsVisible &&
            border.Child is StackPanel panel && panel.Children.OfType<TextBlock>().Any(text => text.Text == "origen"));
        Assert.True(File.Exists(Path.Combine(_vaultPath, "diseno", "origen", "archivo.txt")));
        Assert.False(Directory.Exists(source));
        window.Close();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InternalFolderDrag_RejectsItsOwnFolder(bool treeMode)
    {
        var source = SeedFolder();
        var (_, _, window) = CreateView(treeMode);
        var row = FolderRow(window, "diseno");
        using var transfer = InternalDrag(source);

        Assert.Equal(DragDropEffects.None, RaiseDragOver(row, transfer));
        window.Close();
    }

    // ===================== Modo explorador =====================

    [Fact]
    public async Task BrowserMode_DroppingAnOsFileOnAFolderRow_ImportsItIntoThatFolder()
    {
        var folder = SeedFolder();
        var external = SeedExternalFile();
        var (viewModel, _, window) = CreateView(treeMode: false);
        viewModel.ReloadFolders();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var row = FolderRow(window, "diseno");
        Assert.Equal(DragDropEffects.Copy, RaiseDragOver(row, await OsDragAsync(window, external)));
        RaiseDrop(row, await OsDragAsync(window, external));

        // Dentro de la carpeta soltada, y no en la raíz del vault ni en la carpeta que se estuviera viendo.
        Assert.True(File.Exists(Path.Combine(folder, "externo.png")));
        Assert.False(File.Exists(Path.Combine(_vaultPath, "externo.png")));

        window.Close();
    }

    /// <summary>Mientras el arrastre pasa por encima, la fila se enciende — sin eso el gesto se aceptaría a
    /// ciegas y no habría forma de saber en qué carpeta va a caer el archivo.</summary>
    [Fact]
    public async Task BrowserMode_AnOsFileOverAFolderRow_HighlightsIt()
    {
        SeedFolder();
        var external = SeedExternalFile();
        var (viewModel, _, window) = CreateView(treeMode: false);
        viewModel.ReloadFolders();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var row = FolderRow(window, "diseno");
        var folder = (FileFolderViewModel)row.DataContext!;

        row.RaiseEvent(new DragEventArgs(
            DragDrop.DragEnterEvent, await OsDragAsync(window, external), row, default, KeyModifiers.None));
        Assert.True(folder.IsDropTarget);

        row.RaiseEvent(new DragEventArgs(
            DragDrop.DragLeaveEvent, await OsDragAsync(window, external), row, default, KeyModifiers.None));
        Assert.False(folder.IsDropTarget);

        window.Close();
    }

    /// <summary>La fila "TODOS" es el destino de la raíz efectiva (el vault, o la raíz de la sesión
    /// elegida), y acepta lo mismo que las demás filas.</summary>
    [Fact]
    public async Task BrowserMode_DroppingAnOsFileOnTheRootRow_ImportsItIntoTheEffectiveRoot()
    {
        SeedFolder();
        var external = SeedExternalFile();
        var (viewModel, _, window) = CreateView(treeMode: false);
        viewModel.ReloadFolders();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        // La fila "TODOS" es la única fila de carpeta sin FileFolderViewModel detrás: su DataContext es el
        // viewmodel de la sección entera.
        var rootRow = window.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("folder-row") && b.DataContext is FilesViewModel);

        Assert.Equal(DragDropEffects.Copy, RaiseDragOver(rootRow, await OsDragAsync(window, external)));
        RaiseDrop(rootRow, await OsDragAsync(window, external));

        Assert.True(File.Exists(Path.Combine(_vaultPath, "externo.png")));

        window.Close();
    }

    /// <summary>El arrastre interno sigue significando "mover", no "importar": el mismo destino distingue
    /// las dos clases de arrastre por su formato.</summary>
    [Fact]
    public void BrowserMode_DroppingAFileOfTheSectionOnAFolderRow_StillMovesIt()
    {
        var folder = SeedFolder();
        var (viewModel, _, window) = CreateView(treeMode: false);
        viewModel.ImportPaths([SeedExternalFile("suelto.pdf")]);
        viewModel.ReloadFolders();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var source = Path.Combine(_vaultPath, "suelto.pdf");
        Assert.True(File.Exists(source));

        var row = FolderRow(window, "diseno");
        Assert.Equal(DragDropEffects.Move, RaiseDragOver(row, InternalDrag(source)));
        RaiseDrop(row, InternalDrag(source));

        Assert.True(File.Exists(Path.Combine(folder, "suelto.pdf")));
        Assert.False(File.Exists(source));

        window.Close();
    }

    /// <summary>Con una sesión protegida y bloqueada elegida no se acepta nada: cifrar lo que se importe
    /// necesita una clave que no está en memoria, así que es mejor no ofrecer el gesto que aceptarlo y
    /// contestar con un aviso de error.</summary>
    [Fact]
    public async Task BrowserMode_WithALockedSessionSelected_TheRootRowRefusesTheDrop()
    {
        var external = SeedExternalFile();
        var (viewModel, sessions, window) = CreateView(treeMode: false);
        var (session, _) = sessions.CreateEncrypted("Privada", "super-secreta-123");
        viewModel.ReloadSessions();
        viewModel.SelectSession(session.Id);
        sessions.Lock(session);
        viewModel.ReloadFolders();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.True(viewModel.IsSelectedSessionLocked);

        var rootRow = window.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("folder-row") && b.DataContext is FilesViewModel);

        Assert.Equal(DragDropEffects.None, RaiseDragOver(rootRow, await OsDragAsync(window, external)));
        Assert.False(viewModel.IsRootDropTarget);

        window.Close();
    }

    // ===================== Modo árbol =====================

    /// <summary>El mismo gesto sobre la misma carpeta en la otra disposición. Ya funcionaba antes de estos
    /// cambios: está aquí para que la simetría entre las dos quede fijada por un test y no vuelva a
    /// separarse en silencio.</summary>
    [Fact]
    public async Task TreeMode_DroppingAnOsFileOnAFolderRow_ImportsItIntoThatFolder()
    {
        var folder = SeedFolder();
        var external = SeedExternalFile();
        var (viewModel, _, window) = CreateView(treeMode: true);
        viewModel.ReloadFolders();
        viewModel.Reload();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var row = FolderRow(window, "diseno");
        Assert.Equal(DragDropEffects.Copy, RaiseDragOver(row, await OsDragAsync(window, external)));
        RaiseDrop(row, await OsDragAsync(window, external));

        Assert.True(File.Exists(Path.Combine(folder, "externo.png")));
        Assert.False(File.Exists(Path.Combine(_vaultPath, "externo.png")));

        window.Close();
    }
}
