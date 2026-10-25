using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Localization;
using enki.Controls;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using enki.Views.Sessions;
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
            var host = view.GetLogicalDescendants().OfType<ModalOverlayHost>()
                .Single(h => h.Content is Border border && border.Width == 740);
            var panel = Assert.IsType<Border>(host.Content);
            host.Content = null;
            panel.DataContext = vm;
            window = new Window { Content = panel, Width = 800, Height = 650 };
            window.Show();
            window.GetLayoutManager()!.ExecuteLayoutPass();

            // Las plantillas propias mantienen teclado y arrastre, además del binding al grafo.
            var labels = panel.GetVisualDescendants().OfType<CheckBox>().First();
            labels.Focus();
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.False(vm.ShowGraphLabels);
            var slider = panel.GetVisualDescendants().OfType<Slider>().First();
            slider.Focus();
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.Equal(1.1, vm.GraphNodeSize, 5);
            slider.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
            var point = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseMove(point + new Vector(35, 0), RawInputModifiers.LeftMouseButton);
            window.MouseUp(point + new Vector(35, 0), MouseButton.Left);
            Assert.True(vm.GraphNodeSize > 1.1);
            var beforeTrackClick = vm.GraphNodeSize;
            var decrease = slider.GetVisualDescendants().OfType<RepeatButton>().First();
            var trackPoint = decrease.TranslatePoint(new Point(decrease.Bounds.Width / 2, decrease.Bounds.Height / 2), window)!.Value;
            window.MouseDown(trackPoint, MouseButton.Left);
            window.MouseUp(trackPoint, MouseButton.Left);
            Assert.True(vm.GraphNodeSize < beforeTrackClick);
            panel.GetVisualDescendants().OfType<Slider>().Last().BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            window.MouseMove(new Point(10, 10));
            if (Environment.GetEnvironmentVariable("ENKI_GRAPH_PREVIEW") is { Length: > 0 } preview)
            {
                Directory.CreateDirectory(preview);
                using var bitmap = new RenderTargetBitmap(new PixelSize(800, 650));
                bitmap.Render(panel);
                bitmap.Save(Path.Combine(preview, light ? "filters-light.png" : "filters-dark.png"), PngBitmapEncoderOptions.Default);
            }

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
            Assert.InRange(panel.Bounds.Width, 1, 800);
            Assert.InRange(panel.Bounds.Height, 1, 650);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableOrUnresponsiveWebRuntime_ShowsInteractiveGraph_AndReattaches(bool unresponsive)
    {
        HeadlessApp.EnsureInitialized();
        var directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
        Window? window = null;
        try
        {
            var settings = new SettingsService(directory);
            var vault = new VaultService(settings);
            vault.InitializeVault(Path.Combine(directory, "vault"), false);
            File.WriteAllText(Path.Combine(vault.VaultPath, "A.md"), "---\ntags: [ideas]\n---\n# A");
            File.WriteAllText(Path.Combine(vault.VaultPath, "B.md"), "# B");
            string? openedPath = null;
            var vm = new SessionsViewModel(vault, new SessionService(vault),
                new NotificationService(settings, directory), (_, note) => openedPath = note?.FullPath, () => { });
            AwaitLoad(vm);
            var view = new SessionsView(() => unresponsive) { DataContext = vm };
            if (unresponsive)
            {
                // Sin adjuntar al árbol visual no se inicia ningún adaptador de sistema: simulamos
                // un documento que nunca confirma «ready», y comprobamos el temporizador real.
                vm.GraphTagFilters.Single(f => f.GroupId == "ideas").IsActive = true;
                AwaitNativeGraph(view);
                Assert.Equal(1, ((NativeSessionGraph)view.FindControl<ContentControl>("GraphContentHost")!.Content!).NodeCount);
                vm.ResetGraphFiltersCommand.Execute(null);
            }
            window = new Window { Content = view, Width = 1200, Height = 850 };
            window.Show();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var graph = view.GetVisualDescendants().OfType<NativeSessionGraph>().Single();
            Assert.DoesNotContain(view.GetVisualDescendants(), control => control is NativeWebView);
            Assert.Equal(2, graph.NodeCount);
            // El mismo filtro que utiliza el popover debe actualizar el renderizador de compatibilidad.
            vm.GraphTagFilters.Single(f => f.GroupId == "ideas").IsActive = true;
            Assert.Equal(1, graph.NodeCount);
            vm.ResetGraphFiltersCommand.Execute(null);
            Assert.Equal(2, graph.NodeCount);
            using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(1200, 850)))
                bitmap.Render(view);
            var point = graph.TranslatePoint(graph.NodeScreenPosition(enki.Models.SessionGraph.RootGroupId + "::A.md"), window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.Equal(Path.Combine(vault.VaultPath, "A.md"), openedPath);

            window.Content = null;
            // Esta instancia vuelve a comprobar el motor al reentrar; ahora forzamos que falte.
            unresponsive = false;
            window.Content = view;
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var recreated = view.GetVisualDescendants().OfType<NativeSessionGraph>().Single();
            Assert.NotSame(graph, recreated);
            Assert.Equal(2, recreated.NodeCount);
            vm.GraphExplorationIndex = 1;
            Assert.Equal(vm.NodeCount, recreated.NodeCount);
        }
        finally
        {
            window?.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void AwaitNativeGraph(SessionsView view)
    {
        var host = view.FindControl<ContentControl>("GraphContentHost")!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        void OnContentChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == ContentControl.ContentProperty && host.Content is NativeSessionGraph)
                timeout.Cancel();
        }
        host.PropertyChanged += OnContentChanged;
        try { Dispatcher.UIThread.MainLoop(timeout.Token); }
        finally { host.PropertyChanged -= OnContentChanged; }
        Assert.IsType<NativeSessionGraph>(host.Content);
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
