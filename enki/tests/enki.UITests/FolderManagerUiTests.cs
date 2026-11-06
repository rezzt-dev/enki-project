using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class FolderManagerUiTests : IDisposable
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "enki-folder-ui-" + Guid.NewGuid().ToString("N"));
    public FolderManagerUiTests() => HeadlessApp.EnsureInitialized();
    public void Dispose() => Directory.Delete(_storage, true);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SharedDialog_CreatesFolderFromBothLayouts(bool files, bool tree)
    {
        var settings = new SettingsService(_storage);
        settings.Settings.FilesViewMode = tree ? "Tree" : "Browser";
        var vault = new VaultService(settings);
        vault.InitializeVault(Path.Combine(_storage, "vault"), seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _storage);
        var notesVm = new NotesViewModel(vault, sessions, settingsService: settings, notificationService: notifications);
        notesVm.IsSimplifiedView = tree;
        var filesVm = new FilesViewModel(vault, sessions, notifications, settings);
        var manager = files ? filesVm.FolderManager : notesVm.FolderManager;
        var view = files ? (Control)new FilesView { DataContext = filesVm } : new NotesView { DataContext = notesVm };
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        window.Show();
        try
        {
            manager.Open(vault.VaultPath, vault.VaultPath, create: true);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var panel = window.GetVisualDescendants().OfType<FolderManagerView>().Single();
            Assert.Same(manager, panel.DataContext);
            Assert.True(manager.IsOpen);
            Assert.False(manager.ApplyCommand.CanExecute(null));
            var nameBox = window.GetVisualDescendants().OfType<TextBox>().Single(box =>
                box.IsEffectivelyVisible && Equals(Avalonia.Automation.AutomationProperties.GetName(box),
                    enki.Localization.LocalizationService.Instance["Folders_Name"]));
            nameBox.Text = "New folder";
            Assert.Equal("New folder", manager.Name);
            var apply = window.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.Command, manager.ApplyCommand));
            Assert.True(apply.IsEffectivelyVisible);
            Assert.True(apply.IsEnabled);
            apply.Command!.Execute(null);
            Assert.False(manager.IsOpen);
            Assert.True(Directory.Exists(Path.Combine(vault.VaultPath, "New folder")));
            Assert.Single(notesVm.RootFolders);
            Assert.Single(filesVm.RootFolders);
        }
        finally { window.Close(); }
    }
}
