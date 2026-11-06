using System;
using System.Collections.Generic;
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
    public void Board_AppliesTagFilterVisibilityLive_AndExpansionStaysInsideTheCard(KanbanBoardOrientation orientation)
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
            kanban.DefaultBoard.Columns[0].Cards.Add(model);
            var vm = new KanbanViewModel(kanban, auth, new PolarTaskManagementViewModel(tasks, sync), settingsService: settings)
            { BoardOrientation = orientation };
            var view = new KanbanView { DataContext = vm };
            window = new Window { Content = view, Width = 1400, Height = 1000 };
            window.Show();
            window.UpdateLayout();
            var tags = view.GetVisualDescendants().OfType<KanbanTags>().Single(t => t.Tags?.Contains("etiqueta-12") == true);
            Assert.True(tags.IsVisible);
            var filter = view.GetVisualDescendants().OfType<TagFilterView>()
                .Single(f => ReferenceEquals(f.DataContext, vm.TagFilter));
            Assert.True(filter.IsVisible);
            // El ajuste de Apariencia esconde la fila de chips de filtro, no las etiquetas de las
            // tarjetas: estas se pintan siempre que la tarjeta tenga alguna.
            settings.Settings.KanbanShowTagFilter = false;
            settings.Save();
            Assert.False(filter.IsVisible);
            Assert.True(tags.IsVisible);
            Assert.Equal(12, model.Tags.Count);
            settings.Settings.KanbanShowTagFilter = true;
            settings.Save();
            Assert.True(filter.IsVisible);
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
    public void Overflow_ShowsWhatFitsWithACounter_ExpandsAndCollapses_WithoutOpeningTheCard()
    {
        string[] tags = ["uno", "dos", "tres", "cuatro", "cinco", "seis", "siete"];
        var control = new KanbanTags { Tags = tags };
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
            // En una tarjeta estrecha no caben las siete: se ve el prefijo que quepa en una línea y el
            // resto se resume en un "+N" que no se pierde tras los puntos suspensivos.
            var shown = AssertCollapsed(label.Text, tags);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.Equal(string.Join(", ", tags), label.Text);
            Assert.Equal(0, label.MaxLines);
            Assert.Equal(0, parentClicks);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(shown, AssertCollapsed(label.Text, tags));
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Overflow_NeverShowsMoreThanFiveTags_EvenWhenMoreWouldFit()
    {
        var tags = Enumerable.Range(1, 12).Select(i => $"t{i:00}").ToArray();
        var control = new KanbanTags { Tags = tags, Width = 300 };
        var window = new Window { Content = control, Width = 600, Height = 400 };
        window.Show();
        try
        {
            window.UpdateLayout();
            var label = control.FindControl<TextBlock>("TagsText")!;
            Assert.True(control.FindControl<Button>("ExpandButton")!.IsVisible);
            Assert.Equal(5, AssertCollapsed(label.Text, tags));
        }
        finally { window.Close(); }
    }

    /// <summary>Comprueba que el texto plegado son las N primeras etiquetas seguidas del contador de
    /// las que quedan fuera, y devuelve N para poder contrastarlo con el tope de cinco.</summary>
    private static int AssertCollapsed(string? text, IReadOnlyList<string> tags)
    {
        var parts = (text ?? string.Empty).Split("  +");
        Assert.Equal(2, parts.Length);
        var shown = parts[0].Split(", ");
        Assert.InRange(shown.Length, 1, 5);
        Assert.Equal(string.Join(", ", tags.Take(shown.Length)), parts[0]);
        Assert.Equal((tags.Count - shown.Length).ToString(), parts[1]);
        return shown.Length;
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
