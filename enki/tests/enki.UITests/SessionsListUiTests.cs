using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Los nombres largos no invaden las acciones y la edición conserva el ancho de la fila.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class SessionsListUiTests
{
    [Theory]
    [InlineData(800)]
    [InlineData(1200)]
    public void LongNames_StayInsideRows_AndSelectionAndRenameWork(double width)
    {
        HeadlessApp.EnsureInitialized();
        var directory = Path.Combine(Path.GetTempPath(), "enki-sessions-ui-" + Guid.NewGuid().ToString("N"));
        Window? window = null;
        try
        {
            var settings = new SettingsService(directory);
            var vault = new VaultService(settings);
            vault.InitializeVault(Path.Combine(directory, "vault"), false);
            var service = new SessionService(vault);
            var session = service.Create(new string('W', 70));
            var vm = new SessionsViewModel(vault, service, new NotificationService(settings, directory), (_, _) => { }, () => { });
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!vm.PendingLoad.IsCompleted && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }
            Assert.True(vm.PendingLoad.IsCompleted);
            Assert.True(vm.PendingLoad.IsCompletedSuccessfully, vm.PendingLoad.Exception?.ToString());
            var view = new SessionsView(() => false) { DataContext = vm };
            window = new Window { Content = view, Width = width, Height = 800 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var item = vm.Sessions.Single(s => s.Session.Id == session.Id);
            var row = view.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("session-row") && b.DataContext == item);
            var controls = row.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToArray();
            foreach (var control in controls)
            {
                var bounds = new Rect(control.TranslatePoint(default, row)!.Value, control.Bounds.Size);
                Assert.True(new Rect(row.Bounds.Size).Contains(bounds));
                Assert.True(bounds.Width > 0);
            }
            var select = controls.Single(b => b.Command == vm.SelectSessionCommand);
            var rename = controls.Single(b => b.Command == vm.BeginRenameSessionCommand);
            var selectBounds = new Rect(select.TranslatePoint(default, row)!.Value, select.Bounds.Size);
            var renameBounds = new Rect(rename.TranslatePoint(default, row)!.Value, rename.Bounds.Size);
            Assert.True(selectBounds.Bottom <= renameBounds.Top);
            select.Command!.Execute(select.CommandParameter);
            Assert.Same(item, vm.SelectedSession);
            rename.Command!.Execute(rename.CommandParameter);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var editor = row.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.True(editor.IsEffectivelyVisible);
            Assert.True(editor.Bounds.Width > select.Bounds.Width);
            Assert.True(new Rect(row.Bounds.Size).Contains(new Rect(editor.TranslatePoint(default, row)!.Value, editor.Bounds.Size)));
        }
        finally
        {
            window?.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
