using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class EditorLinkedCardsUiTests
{
    [Fact]
    public void LinkedCards_CentersTheCount_AndUsesSeparatedBorderlessRows()
    {
        HeadlessApp.EnsureInitialized();
        var directory = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
        Window? window = null;
        var previousZen = EditorPreferences.IsZenMode;
        try
        {
            EditorPreferences.IsZenMode = false;
            var settings = new SettingsService(directory);
            var vault = new VaultService(settings);
            vault.InitializeVault(Path.Combine(directory, "vault"), seedWelcomeNote: false);
            var note = vault.CreateNote("Nota vinculada");
            var kanban = new KanbanService(directory);
            kanban.Board.Columns[0].Cards.Add(new KanbanCard { Id = "one", Title = "Primera tarea", LinkedNotePath = note.FullPath });
            kanban.Board.Columns[0].Cards.Add(new KanbanCard { Id = "two", Title = "Segunda tarea", LinkedNotePath = note.FullPath });
            string? opened = null;
            var vm = new EditorViewModel(vault, note, () => { }, () => { }, kanbanService: kanban, onOpenLinkedCard: id => opened = id);
            var view = new EditorView { DataContext = vm };
            window = new Window { Content = view, Width = 1100, Height = 800 };
            window.Show();
            window.UpdateLayout();
            var trigger = view.FindControl<ToggleButton>("LinkedCardsTrigger")!;
            var count = trigger.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "2");
            var center = count.TranslatePoint(new Point(count.Bounds.Width / 2, count.Bounds.Height / 2), trigger)!.Value;
            Assert.InRange(Math.Abs(center.Y - trigger.Bounds.Height / 2), 0, 1);

            vm.IsLinkedCardsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var popup = view.FindControl<Popup>("LinkedCardsPopup")!;
            Assert.True(popup.IsOpen);
            var rows = popup.Child!.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("linked-card-row")).ToArray();
            Assert.Equal(2, rows.Length);
            Assert.All(rows, row => Assert.Equal(new Thickness(0), row.BorderThickness));
            Assert.Contains(popup.Child!.GetVisualDescendants().OfType<StackPanel>(), p => p.Spacing == 1 && p.Background is not null);
            rows[0].Command!.Execute(rows[0].CommandParameter);
            Assert.Equal("one", opened);
            Assert.False(vm.IsLinkedCardsExpanded);
            vm.FlushPendingSave();
        }
        finally
        {
            window?.Close();
            EditorPreferences.IsZenMode = previousZen;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
