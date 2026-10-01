using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using enki.Localization;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using ImageMagick;
using Xunit;

namespace enki.UITests;

[Collection(AvaloniaHeadlessCollection.Name)]
public class FileCompatibilityUiTests
{
    [Fact]
    public void MediaPreview_ShowsAccessibleControlsWithoutLoadingNativeVlc()
    {
        HeadlessApp.EnsureInitialized();
        var directory = Path.Combine(Path.GetTempPath(), "enki-media-ui-" + Guid.NewGuid().ToString("N"));
        Window? window = null;
        try
        {
            var settings = new SettingsService(directory);
            var vault = new VaultService(settings);
            vault.InitializeVault(Path.Combine(directory, "vault"), seedWelcomeNote: false);
            File.WriteAllBytes(Path.Combine(directory, "vault", "sound.wav"), [1, 2, 3]);
            var vm = new FilesViewModel(vault, new SessionService(vault), new NotificationService(settings, directory), settings);
            window = new Window { Width = 1000, Height = 700, Content = new FilesView { DataContext = vm } };
            window.Show();
            vm.Refresh();
            vm.OpenCardCommand.Execute(vm.Files.Single());
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var media = Assert.IsType<MediaPreviewViewModel>(vm.ActivePreview!.MediaPreview);
            Assert.Null(media.Player);
            Assert.False(vm.ActivePreview.ShowsInfoPanel);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b =>
                Equals(Avalonia.Automation.AutomationProperties.GetName(b), LocalizationService.Instance["Files_MediaPlayPause"])
                && b.Command is not null);
            vm.ClosePreviewCommand.Execute(null);
            Assert.Null(media.Player);
        }
        finally
        {
            window?.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
