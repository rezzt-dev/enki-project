using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using enki.Localization;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Smoke-renders the real <see cref="FilesView"/> XAML bound to a real
/// <see cref="FilesViewModel"/> and opens its delete-confirmation dialog (docs/version-alfa-3/
/// 02-roadmap-implementacion.md Fase 5) — the request/cancel/confirm flow added in Fase 2 of that same
/// roadmap (see <c>FilesViewModelDeleteConfirmationTests</c> for the ViewModel-only coverage).</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class FilesViewUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _vaultPath;

    public FilesViewUiTests()
    {
        HeadlessApp.EnsureInitialized();
        _vaultPath = Path.Combine(_tempDir, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (FilesView View, FilesViewModel ViewModel, VaultService Vault, Window Window) CreateView()
    {
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _tempDir);
        var viewModel = new FilesViewModel(vault, sessions, notifications, settings);

        var view = new FilesView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();

        return (view, viewModel, vault, window);
    }

    private static string ImportFile(VaultService vault, string tempDir, string name)
    {
        var sourcePath = Path.Combine(tempDir, name);
        File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);
        return vault.ImportFile(sourcePath, vault.VaultPath).FullPath;
    }

    private static string RenderedText(Window window) => string.Join(
        " | ",
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)));

    [Fact]
    public void FilesView_WithAnImportedFile_RendersItsName()
    {
        var (view, viewModel, vault, window) = CreateView();
        ImportFile(vault, _tempDir, "adjunto.png");
        viewModel.Reload();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var renderedTexts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("adjunto.png", renderedTexts);

        window.Close();
    }

    [Fact]
    public void RequestDeleteFile_WithConfirmBeforeDeleteEnabled_OpensDeleteDialog()
    {
        var (view, viewModel, vault, window) = CreateView();
        viewModel.Reload();
        ImportFile(vault, _tempDir, "adjunto.png");
        viewModel.Reload();
        var card = viewModel.Files.Single();

        viewModel.RequestDeleteFileCommand.Execute(card);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.NotNull(viewModel.FilePendingDelete);
        Assert.Contains(
            LocalizationService.Instance["Files_DeleteFile_Title"], RenderedText(window),
            StringComparison.OrdinalIgnoreCase);

        window.Close();
    }

    [Fact]
    public void CancelDeleteFile_ClosesDialogWithoutRemovingTheFile()
    {
        var (view, viewModel, vault, window) = CreateView();
        ImportFile(vault, _tempDir, "adjunto.png");
        viewModel.Reload();
        var card = viewModel.Files.Single();
        viewModel.RequestDeleteFileCommand.Execute(card);

        viewModel.CancelDeleteFileCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.Null(viewModel.FilePendingDelete);
        Assert.Single(viewModel.Files);

        window.Close();
    }
}
