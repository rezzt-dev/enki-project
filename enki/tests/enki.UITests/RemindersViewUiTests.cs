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

/// <summary>Smoke-renders the real <see cref="RemindersView"/> XAML bound to a real
/// <see cref="RemindersViewModel"/> and opens its "nuevo recordatorio" editor and "vaciar todo"
/// confirmation dialogs (docs/version-alfa-3/02-roadmap-implementacion.md Fase 5).</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class RemindersViewUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");

    public RemindersViewUiTests() => HeadlessApp.EnsureInitialized();

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (RemindersView View, RemindersViewModel ViewModel, Window Window) CreateView()
    {
        var settings = new SettingsService(_tempDir);
        var authService = new PolarAuthService(settings);
        var restClient = new SupabaseRestClient(authService);
        var taskService = new PolarTaskService(_tempDir);
        var reminderService = new ReminderService(_tempDir);
        var syncService = new PolarSyncService(authService, restClient, taskService, reminderService, settings);
        var viewModel = new RemindersViewModel(reminderService, authService, syncService);

        var view = new RemindersView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1100, Height = 800 };
        window.Show();

        return (view, viewModel, window);
    }

    private static string RenderedText(Window window) => string.Join(
        " | ",
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)));

    [Fact]
    public void RemindersView_RendersWithoutThrowing()
    {
        var (_, viewModel, window) = CreateView();

        Assert.NotNull(viewModel);
        window.Close();
    }

    [Fact]
    public void OpenCreateEditor_OpensTheReminderFormDialog()
    {
        var (_, viewModel, window) = CreateView();

        viewModel.OpenCreateEditorCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.NotNull(viewModel.Editor);
        Assert.Contains(
            LocalizationService.Instance["Reminders_Editor_Title"], RenderedText(window),
            StringComparison.OrdinalIgnoreCase);

        window.Close();
    }

    [Fact]
    public void CancelEditor_ClosesTheDialogWithoutCreatingAReminder()
    {
        var (_, viewModel, window) = CreateView();
        viewModel.OpenCreateEditorCommand.Execute(null);

        viewModel.CancelEditorCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.Null(viewModel.Editor);
        Assert.Empty(viewModel.Items);

        window.Close();
    }

    [Fact]
    public void RequestClearAll_OpensConfirmationDialog()
    {
        var (_, viewModel, window) = CreateView();

        viewModel.RequestClearAllCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.True(viewModel.PendingClearAll);

        window.Close();
    }
}
