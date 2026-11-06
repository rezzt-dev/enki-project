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
public sealed class NotesFolderDropUiTests : IDisposable
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "enki-note-drop-" + Guid.NewGuid().ToString("N"));

    public NotesFolderDropUiTests() => HeadlessApp.EnsureInitialized();

    [Fact]
    public void FolderCard_AcceptsAnotherFolderAndMovesItsContents()
    {
        var settings = new SettingsService(_storage);
        var vault = new VaultService(settings);
        vault.InitializeVault(Path.Combine(_storage, "vault"), seedWelcomeNote: false);
        var source = vault.CreateManagedFolder(vault.VaultPath, "Origen");
        vault.CreateNote("Nota", source.FullPath);
        var destination = vault.CreateManagedFolder(vault.VaultPath, "Destino");
        var vm = new NotesViewModel(vault, new SessionService(vault), settingsService: settings);
        var window = new Window { Content = new NotesView { DataContext = vm }, Width = 1400, Height = 900 };
        window.Show();
        window.GetLayoutManager()!.ExecuteLayoutPass();
        try
        {
            var card = window.GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext is NoteFolderCardViewModel { Name: "Destino" });
            using var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(FullNotesView.NoteDragFormat, source.FullPath));
            var over = new DragEventArgs(DragDrop.DragOverEvent, transfer, card, default, KeyModifiers.None);
            card.RaiseEvent(over);
            Assert.Equal(DragDropEffects.Move, over.DragEffects);
            card.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, transfer, card, default, KeyModifiers.None));

            Assert.True(File.Exists(Path.Combine(destination.FullPath, "Origen", "Nota.md")));
            Assert.False(Directory.Exists(source.FullPath));
        }
        finally { window.Close(); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
    }
}
