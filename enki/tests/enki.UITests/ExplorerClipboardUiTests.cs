using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class ExplorerClipboardUiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-clipboard-ui-" + Guid.NewGuid().ToString("N"));

    public ExplorerClipboardUiTests() => HeadlessApp.EnsureInitialized();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Files_ContextMenuCopiesFile_AndKeyboardPastes(bool tree)
    {
        var settings = new SettingsService(_directory);
        settings.Settings.FilesViewMode = tree ? "Tree" : "Browser";
        var vault = new VaultService(settings);
        vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        File.WriteAllText(Path.Combine(vault.VaultPath, "file.txt"), "contenido");
        var vm = new FilesViewModel(vault, new SessionService(vault), new NotificationService(settings, _directory), settings);
        var view = new FilesView { DataContext = vm };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        window.Show();
        window.GetLayoutManager()!.ExecuteLayoutPass();
        try
        {
            var row = view.GetVisualDescendants().OfType<Control>().First(c =>
                c.ContextMenu is not null && (tree ? c.DataContext is FileTreeItemViewModel { IsFile: true } : c.DataContext is FileCardViewModel));
            row.ContextMenu!.Open(row);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var copy = row.ContextMenu.Items.OfType<MenuItem>().First(m => ReferenceEquals(m.Command, vm.CopyEntryCommand));
            Assert.True(copy.Command!.CanExecute(copy.CommandParameter));
            copy.Command.Execute(copy.CommandParameter);
            row.ContextMenu.Close();
            Press(row, Key.V);
            Assert.Equal(2, vault.GetFiles().Count);
            Press(view, Key.Escape, KeyModifiers.None);
            Assert.False(vm.PasteEntryCommand.CanExecute(null));
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Notes_ContextMenuCopies_AndSearchTextBoxDoesNotPasteFiles(bool simplified)
    {
        var settings = new SettingsService(_directory);
        settings.Settings.NotesViewMode = simplified ? "Simple" : "Full";
        var vault = new VaultService(settings);
        vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        vault.CreateNote("Nota");
        var vm = new NotesViewModel(vault, new SessionService(vault), settingsService: settings);
        var view = new NotesView { DataContext = vm };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        window.Show();
        window.GetLayoutManager()!.ExecuteLayoutPass();
        try
        {
            var row = view.GetVisualDescendants().OfType<Control>().First(c =>
                c.ContextMenu is not null && c.DataContext is NoteCardViewModel or NoteTreeItemViewModel { IsNote: true });
            row.ContextMenu!.Open(row);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var copy = row.ContextMenu.Items.OfType<MenuItem>().First(m => ReferenceEquals(m.Command, vm.CopyEntryCommand));
            copy.Command!.Execute(copy.CommandParameter);
            row.ContextMenu.Close();
            var search = view.GetVisualDescendants().OfType<TextBox>().First();
            Press(search, Key.V);
            Assert.Single(vault.GetNotes());
            Press(row, Key.V);
            Assert.Equal(2, vault.GetNotes().Count);
        }
        finally { window.Close(); }
    }

    private static void Press(Control control, Key key, KeyModifiers modifiers = KeyModifiers.Control) =>
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
