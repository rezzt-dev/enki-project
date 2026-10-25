using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using enki.Models;
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

    private (RemindersView View, RemindersViewModel ViewModel, Window Window) CreateView(double width = 1100)
    {
        var settings = new SettingsService(_tempDir);
        var authService = new PolarAuthService(settings);
        var restClient = new SupabaseRestClient(authService);
        var taskService = new PolarTaskService(_tempDir);
        var reminderService = new ReminderService(_tempDir);
        var syncService = new PolarSyncService(authService, restClient, taskService, reminderService, settings);
        var viewModel = new RemindersViewModel(reminderService, authService, syncService);

        var view = new RemindersView { DataContext = viewModel };
        var window = new Window { Content = view, Width = width, Height = 800 };
        window.Show();

        return (view, viewModel, window);
    }

    private static string RenderedText(Window window) => string.Join(
        " | ",
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)));

    /// <summary>La fecha y las acciones siguen dentro de la fila con textos largos y poco ancho.</summary>
    [Theory]
    [InlineData(480)]
    [InlineData(1100)]
    public void ReminderRows_KeepContentAndActionsInside_AndCommandsWork(double width)
    {
        var service = new ReminderService(_tempDir);
        service.Create(new string('W', 100), "Descripción extensa de un recordatorio para comprobar el ajuste del contenido en una ventana estrecha.",
            DateTime.UtcNow.AddDays(-2), "BellRingOutline", recurrence: PolarRecurrence.Daily);
        var (view, vm, window) = CreateView(width);
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var row = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("reminder-row"));
            var rowBounds = new Rect(row.TranslatePoint(default, window)!.Value, row.Bounds.Size);
            var buttons = row.GetVisualDescendants().OfType<Button>().ToArray();
            Assert.Equal(2, buttons.Length);
            foreach (var button in buttons)
            {
                var bounds = new Rect(button.TranslatePoint(default, window)!.Value, button.Bounds.Size);
                Assert.True(rowBounds.Contains(bounds));
                Assert.InRange(bounds.Right, 0, width);
            }
            var metadata = row.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("reminder-meta")).ToArray();
            Assert.Equal(3, metadata.Length);
            foreach (var text in metadata)
            {
                var bounds = new Rect(text.TranslatePoint(default, window)!.Value, text.Bounds.Size);
                Assert.True(rowBounds.Contains(bounds));
                Assert.True(bounds.Width > 0);
            }
            var complete = buttons.Single(b => b.Command == vm.ToggleCompletedCommand);
            complete.Command!.Execute(complete.CommandParameter);
            Assert.True(Assert.Single(vm.Items).IsCompleted);
            Assert.Null(vm.Detail);
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var open = view.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.OpenDetailCommand);
            open.Command!.Execute(open.CommandParameter);
            Assert.NotNull(vm.Detail);
        }
        finally { window.Close(); }
    }

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

    /// <summary>La fila "Sincronizado con Polar · {fecha}" (escudo + etiqueta + botón de sync) tiene que
    /// quedar centrada verticalmente. Sin <c>VerticalAlignment="Center"</c> el <see cref="TextBlock"/> se
    /// estiraba a la altura del botón (20 px) mientras el icono, de 12 px fijos, sí se centraba — y como
    /// Avalonia dibuja el texto desde el borde superior de la caja, la etiqueta salía pegada arriba.</summary>
    [Fact]
    public void PolarSyncStatusRow_IsVerticallyCentered()
    {
        var settings = new SettingsService(_tempDir);
        var authService = new PolarAuthService(settings, baseUrl: "https://test.example", anonKey: "test-key");
        settings.Settings.PolarLinked = true;
        settings.Settings.PolarUserId = "user-1";
        settings.Settings.PolarAccessToken = "test-token";
        var syncService = new PolarSyncService(authService, new SupabaseRestClient(authService),
            new PolarTaskService(_tempDir), new ReminderService(_tempDir), settings);
        var viewModel = new RemindersViewModel(new ReminderService(_tempDir), authService, syncService)
        {
            LastSyncedLabel = "13 de oct, 12:10"
        };

        var view = new RemindersView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1100, Height = 800 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();

            var label = view.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Text == viewModel.PolarSyncStatusLabel);
            var panel = (Control)label.Parent!;
            var icon = panel.GetVisualChildren().OfType<Control>().First(c => !ReferenceEquals(c, label));
            var row = (Control)panel.Parent!;

            static Rect BoundsIn(Control control, Window window) =>
                new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

            var rowBounds = BoundsIn(row, window);
            var labelBounds = BoundsIn(label, window);
            var iconBounds = BoundsIn(icon, window);

            // El label conserva la altura de su propia línea de texto: estirado a la altura de la fila,
            // el texto se dibuja desde arriba y se ve desalineado del icono aunque su caja esté centrada.
            Assert.True(labelBounds.Height < rowBounds.Height,
                $"La etiqueta de sync no debe estirarse a la altura de la fila ({labelBounds.Height} de {rowBounds.Height}).");
            Assert.InRange(labelBounds.Center.Y, rowBounds.Center.Y - 1, rowBounds.Center.Y + 1);
            Assert.InRange(iconBounds.Center.Y, rowBounds.Center.Y - 1, rowBounds.Center.Y + 1);
            Assert.InRange(Math.Abs(labelBounds.Center.Y - iconBounds.Center.Y), 0, 1);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
