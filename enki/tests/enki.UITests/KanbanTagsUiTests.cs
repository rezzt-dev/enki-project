using System;
using System.Linq;
using System.IO;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using enki.Services;
using enki.Views;
using Avalonia.Controls;
using Avalonia.Interactivity;
using enki.Controls;
using enki.Models;
using enki.ViewModels;
using Xunit;

namespace enki.UITests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class KanbanTagsUiTests
{
    public KanbanTagsUiTests() => HeadlessApp.EnsureInitialized();

    [Theory]
    [InlineData(KanbanBoardOrientation.Vertical)]
    [InlineData(KanbanBoardOrientation.Horizontal)]
    public void Board_AppliesVisibilityLive_AndExpansionStaysInsideTheCard(KanbanBoardOrientation orientation)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
        Window? window = null;
        try
        {
            var settings = new SettingsService(directory);
            var auth = new PolarAuthService(settings);
            var tasks = new PolarTaskService(directory);
            var sync = new PolarSyncService(auth, new SupabaseRestClient(auth), tasks, new ReminderService(directory), settings);
            var kanban = new KanbanService(directory);
            var model = new KanbanCard { Id = "tags", Title = "Etiquetas largas", Tags = Enumerable.Range(1, 12).Select(i => $"etiqueta-{i:00}").ToList() };
            kanban.Board.Columns[0].Cards.Add(model);
            var vm = new KanbanViewModel(kanban, auth, new PolarTaskManagementViewModel(tasks, sync), settingsService: settings)
            { BoardOrientation = orientation };
            var view = new KanbanView { DataContext = vm };
            window = new Window { Content = view, Width = 1400, Height = 1000 };
            window.Show();
            window.UpdateLayout();
            var tags = view.GetVisualDescendants().OfType<KanbanTags>().Single(t => t.Tags?.Contains("etiqueta-12") == true);
            Assert.True(tags.IsVisible);
            settings.Settings.KanbanShowCardTags = false;
            settings.Save();
            Assert.False(tags.IsVisible);
            Assert.Equal(12, model.Tags.Count);
            settings.Settings.KanbanShowCardTags = true;
            settings.Save();
            window.UpdateLayout();
            var expand = tags.FindControl<Button>("ExpandButton")!;
            var point = expand.TranslatePoint(new Point(expand.Bounds.Width / 2, expand.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            window.UpdateLayout();
            Assert.Contains("etiqueta-12", tags.FindControl<TextBlock>("TagsText")!.Text);
            Assert.Null(vm.CardDetail);
            var card = tags.GetVisualAncestors().OfType<Button>().First(b => b.Classes.Contains("kanban-card"));
            var bottom = tags.TranslatePoint(new Point(0, tags.Bounds.Height), card)!.Value;
            Assert.True(bottom.Y <= card.Bounds.Height);
        }
        finally
        {
            window?.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Overflow_ShowsFiveTags_ExpandsAndCollapses_WithoutOpeningTheCard()
    {
        var control = new KanbanTags { Tags = ["uno", "dos", "tres", "cuatro", "cinco", "seis", "siete"] };
        var parentClicks = 0;
        var card = new Button { Content = control, Width = 150 };
        card.Click += (_, _) => parentClicks++;
        var window = new Window { Content = card, Width = 300, Height = 600 };
        window.Show();
        try
        {
            window.UpdateLayout();
            var button = control.FindControl<Button>("ExpandButton")!;
            var label = control.FindControl<TextBlock>("TagsText")!;
            Assert.True(button.IsVisible);
            Assert.Equal("uno, dos, tres, cuatro, cinco", label.Text);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.Equal("uno, dos, tres, cuatro, cinco, seis, siete", label.Text);
            Assert.Equal(0, label.MaxLines);
            Assert.Equal(0, parentClicks);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("uno, dos, tres, cuatro, cinco", label.Text);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Resize_RecalculatesOverflow_AndEmptyTagsTakeNoSpace()
    {
        var control = new KanbanTags { Tags = [new string('a', 50)], Width = 100 };
        var window = new Window { Content = control, Width = 800, Height = 400 };
        window.Show();
        try
        {
            window.UpdateLayout();
            var button = control.FindControl<Button>("ExpandButton")!;
            Assert.True(button.IsVisible);
            control.Width = 750;
            window.UpdateLayout();
            Assert.False(button.IsVisible);
            control.Tags = Array.Empty<string>();
            window.UpdateLayout();
            Assert.False(control.FindControl<StackPanel>("TagsPanel")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void RefreshCard_AfterEditingTags_UpdatesTheBoundControl()
    {
        var card = new KanbanCard { Id = "card", Title = "Tarjeta", Tags = ["original"] };
        var vm = new KanbanCardViewModel(card);
        var control = new KanbanTags { DataContext = vm };
        control.Bind(KanbanTags.TagsProperty, new Avalonia.Data.Binding(nameof(KanbanCardViewModel.Tags)));
        var window = new Window { Content = control, Width = 300, Height = 300 };
        window.Show();
        try
        {
            card.Tags.Clear();
            card.Tags.Add("actualizada");
            vm.RefreshFromModel();
            Assert.Equal("actualizada", control.FindControl<TextBlock>("TagsText")!.Text);
            Assert.Single(card.Tags);
        }
        finally { window.Close(); }
    }
}
