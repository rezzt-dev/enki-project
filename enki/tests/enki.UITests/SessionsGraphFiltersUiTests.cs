using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Localization;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Ejercita el contenido real del popover sin arrancar un motor WebView nativo en headless.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class SessionsGraphFiltersUiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Filters_BindToGraph_KeepModeAcrossLanguageChange_AndReset(bool light)
    {
        HeadlessApp.EnsureInitialized();
        var directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
        var previousTheme = Application.Current!.RequestedThemeVariant;
        var previousLanguage = LocalizationService.Instance.CurrentCulture.Name;
        Window? window = null;
        try
        {
            Application.Current.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
            LocalizationService.Instance.SetLanguage("es-ES");
            var settings = new SettingsService(directory);
            var vault = new VaultService(settings);
            vault.InitializeVault(Path.Combine(directory, "vault"), false);
            File.WriteAllText(Path.Combine(vault.VaultPath, "A.md"), "---\ntags: [ideas]\n---\n# A");
            File.WriteAllText(Path.Combine(vault.VaultPath, "B.md"), "# B");
            var vm = new SessionsViewModel(vault, new SessionService(vault),
                new NotificationService(settings, directory), (_, _) => { }, () => { });
            AwaitLoad(vm);
            var view = new SessionsView();
            var popup = view.GetLogicalDescendants().OfType<Popup>().Single();
            var panel = popup.Child!;
            popup.Child = null;
            panel.DataContext = vm;
            window = new Window { Content = panel, Width = 340, Height = 520 };
            window.Show();
            window.GetLayoutManager()!.ExecuteLayoutPass();

            var tag = panel.GetVisualDescendants().OfType<ToggleSwitch>()
                .Single(t => t.DataContext is GraphGroupFilterItemViewModel { GroupId: "ideas" });
            tag.IsChecked = true;
            Assert.Equal(1, vm.NodeCount);
            Assert.True(vm.IsGraphFiltered);

            var explore = panel.GetVisualDescendants().OfType<ComboBox>().First();
            explore.SelectedIndex = 1;
            Assert.Equal(1, vm.GraphExplorationIndex);
            LocalizationService.Instance.SetLanguage("en-US");
            vm.RefreshLocalizedLabels();
            AwaitLoad(vm);
            Assert.Equal(1, vm.GraphExplorationIndex);
            Assert.Equal(1, explore.SelectedIndex);

            var reset = panel.GetVisualDescendants().OfType<Button>()
                .Single(b => ReferenceEquals(b.Command, vm.ResetGraphFiltersCommand));
            Assert.True(reset.IsEnabled);
            reset.Command!.Execute(null);
            Assert.Equal(2, vm.NodeCount);
            Assert.False(vm.IsGraphFiltered);
            Assert.Equal(0, explore.SelectedIndex);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.InRange(panel.Bounds.Width, 1, 340);
            Assert.InRange(panel.Bounds.Height, 1, 520);
            Assert.True(reset.Bounds.Height > 0);
        }
        finally
        {
            window?.Close();
            Application.Current.RequestedThemeVariant = previousTheme;
            LocalizationService.Instance.SetLanguage(previousLanguage);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void AwaitLoad(SessionsViewModel vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!vm.PendingLoad.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(vm.PendingLoad.IsCompleted);
        vm.PendingLoad.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }
}
