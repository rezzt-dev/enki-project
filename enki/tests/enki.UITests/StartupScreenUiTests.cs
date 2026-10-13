using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Pantalla de arranque de <c>MainWindow</c> (ver <c>StartupScreen</c>): que no tape nada en reposo,
/// que aparezca y se retire cuando el arranque lo pide, y que su barra y su rótulo enseñen lo que el
/// ViewModel tiene.
///
/// Se monta contra la vista y el ViewModel reales —no contra un doble— porque las bindings del proyecto son
/// compiladas (Avalonia las activa por defecto), así que un doble no satisfaría ni uno solo de los enlaces
/// de dentro de la vista. El que ata la cortina al arranque, en cambio, vive en <c>MainWindow.axaml</c>: al
/// montar la vista suelta hay que llevar ese estado a mano, que es justo lo que permite probar la transición
/// paso a paso.
///
/// Lo que no se puede medir en un host headless es la fluidez del compositor: su reloj de animación no dibuja
/// fotogramas de verdad. Por eso el test de la retirada no comprueba cuánto dura el fundido, sino lo que sí es
/// una invariante: que la cortina termina fuera y que no depende de que su animación llegue a completarse
/// (ver <c>StartupScreen.Settle</c>).</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class StartupScreenUiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private MainWindowViewModel? _vm;
    public StartupScreenUiTests() => HeadlessApp.EnsureInitialized();

    /// <summary>Solo durante cada test: cambiar el contexto en el constructor también afectaría a las
    /// continuaciones del runner xUnit, que no bombea el dispatcher entre tests.</summary>
    private sealed class UiContext : IDisposable
    {
        private readonly SynchronizationContext? _original = SynchronizationContext.Current;

        public UiContext() => SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_original);
    }

    public void Dispose()
    {
        _vm?.Shutdown();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    /// <summary>Un ViewModel real sin vault configurado: levanta los servicios y los temporizadores, pero no
    /// la shell (ver <c>ShowVaultSetupOrShell</c>), así que el arranque no se dispara solo y cada test puede
    /// llevar el estado que quiera a mano.</summary>
    private (StartupScreen Screen, MainWindowViewModel Vm, Window Window) Mount()
    {
        _vm = new MainWindowViewModel(new SettingsService(_directory));
        var screen = new StartupScreen { DataContext = _vm };
        var window = new Window { Content = screen, Width = 900, Height = 600 };
        window.Show();
        window.UpdateLayout();
        return (screen, _vm, window);
    }

    /// <summary>Avanza el reloj real y deja que el dispatcher procese lo que se encoló, hasta que
    /// <paramref name="condition"/> se cumple o se agota el margen. Se apoya en el reloj de pared a
    /// propósito: la retirada de la cortina tiene un límite de tiempo propio, así que no hace falta que la
    /// animación avance en el host headless.</summary>
    private static void SpinUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            Thread.Sleep(20);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Pone la cortina y deja que el dispatcher y el layout hagan su trabajo, que es la situación
    /// desde la que se mira su contenido en el resto de los tests.</summary>
    private static void Show(StartupScreen screen, Window window)
    {
        screen.IsWarmUpActive = true;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    [Fact]
    public void BeforeTheViewModelExists_SetupScreensAreHidden()
    {
        using var context = new UiContext();
        var window = new MainWindow(new SettingsService(_directory));
        try
        {
            Assert.False(window.FindControl<LanguageSetupView>("LanguageSetupScreen")!.IsVisible);
            Assert.False(window.FindControl<VaultSetupView>("VaultSetupScreen")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void AQuickWarmUp_KeepsTheScreenVisibleForTwoSeconds()
    {
        using var context = new UiContext();
        var (screen, _, window) = Mount();
        try
        {
            var visible = Stopwatch.StartNew();
            Show(screen, window);
            screen.IsWarmUpActive = false;
            SpinUntil(() => !screen.IsVisible);

            Assert.False(screen.IsVisible);
            Assert.True(visible.Elapsed >= TimeSpan.FromSeconds(2));
            Assert.True(visible.Elapsed < TimeSpan.FromSeconds(3));
        }
        finally { window.Close(); }
    }

    [Fact]
    public void ASlowWarmUp_DoesNotDisappearAfterTheMinimumTime()
    {
        using var context = new UiContext();
        var (screen, _, window) = Mount();
        try
        {
            var visible = Stopwatch.StartNew();
            Show(screen, window);
            SpinUntil(() => visible.Elapsed >= TimeSpan.FromMilliseconds(2200));
            Assert.True(screen.IsVisible);
            Assert.Equal(1, screen.Opacity);

            screen.IsWarmUpActive = false;
            SpinUntil(() => !screen.IsVisible);
            Assert.False(screen.IsVisible);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void ClosingDuringTheEntrance_DoesNotBuildTheShellLater()
    {
        using var context = new UiContext();
        var (screen, _, window) = Mount();
        var entered = false;
        screen.Entered += () => entered = true;
        Show(screen, window);
        window.Close();

        var elapsed = Stopwatch.StartNew();
        SpinUntil(() => elapsed.Elapsed >= TimeSpan.FromMilliseconds(600));
        Assert.False(entered);
    }

    [Fact]
    public void ReattachingDuringTheEntrance_RestartsTheAnimation()
    {
        using var context = new UiContext();
        var (screen, _, window) = Mount();
        try
        {
            var entered = false;
            screen.Entered += () => entered = true;
            Show(screen, window);
            window.Content = null;
            window.Content = screen;
            SpinUntil(() => entered);
            Assert.True(entered);
            Assert.Equal(1, screen.Opacity);

            screen.IsWarmUpActive = false;
            SpinUntil(() => !screen.IsVisible);
            Assert.False(screen.IsVisible);
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheMainWindow_WithAnExistingVault_NeverShowsSetupAndReleasesTheOverlay(bool customTitleBar)
    {
        using var context = new UiContext();
        // Evita adquirir el mutex real del usuario; restaura el estado global al terminar.
        var firstInstance = typeof(SingleInstanceGuard).GetProperty(nameof(SingleInstanceGuard.IsFirstInstance))!;
        var previous = SingleInstanceGuard.IsFirstInstance;
        MainWindow? window = null;
        try
        {
            firstInstance.SetValue(null, true);
            var settings = new SettingsService(_directory);
            settings.Settings.Language = "es-ES";
            settings.Settings.UseCustomTitleBar = customTitleBar;
            settings.Settings.VaultPath = Path.Combine(_directory, "vault");
            Directory.CreateDirectory(settings.Settings.VaultPath);
            var customLogoPath = Path.Combine(_directory, "chosen-logo.png");
            using (var source = new SkiaSharp.SKBitmap(32, 32))
            {
                source.Erase(SkiaSharp.SKColors.Magenta);
                using var png = source.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(customLogoPath, png.ToArray());
            }
            Assert.True(new BrandLogoService(settings).Import(customLogoPath).Success);
            settings.Settings.UseCustomBrandLogo = true;
            window = new MainWindow(settings);
            var setup = window.FindControl<VaultSetupView>("VaultSetupScreen")!;
            var language = window.FindControl<LanguageSetupView>("LanguageSetupScreen")!;
            var overlay = window.FindControl<StartupScreen>("StartupOverlay")!;
            Assert.True(overlay.IsVisible);
            Assert.Equal(1, overlay.Opacity);
            Assert.Equal(customTitleBar ? WindowDecorations.None : WindowDecorations.Full, window.WindowDecorations);
            Assert.DoesNotContain(overlay.GetVisualAncestors(), ancestor => ancestor is LayoutTransformControl);
            Assert.Null(window.DataContext);
            using (var savedLogo = new MemoryStream())
            {
                var logo = Assert.IsType<Bitmap>(overlay.FindControl<Image>("StartupLogo")!.Source);
                logo.Save(savedLogo, PngBitmapEncoderOptions.Default);
                using var decoded = SkiaSharp.SKBitmap.Decode(savedLogo.ToArray());
                Assert.Equal(SkiaSharp.SKColors.Magenta, decoded.GetPixel(decoded.Width / 2, decoded.Height / 2));
            }
            var beforeOpening = Stopwatch.StartNew();
            SpinUntil(() => beforeOpening.Elapsed >= TimeSpan.FromMilliseconds(600));
            Assert.Null(window.DataContext);
            Assert.Equal(1, overlay.Opacity);
            var setupWasVisible = setup.IsVisible || language.IsVisible;
            setup.PropertyChanged += (_, _) => setupWasVisible |= setup.IsVisible;
            language.PropertyChanged += (_, _) => setupWasVisible |= language.IsVisible;

            var visible = Stopwatch.StartNew();
            window.Show();
            Assert.True(overlay.IsVisible);
            Assert.Equal(1, overlay.Opacity);
            window.UpdateLayout();
            Assert.Equal(window.ClientSize, overlay.Bounds.Size);
            Assert.NotNull(overlay.FindControl<Image>("StartupLogo")!.Source);
            if (Environment.GetEnvironmentVariable("ENKI_GRAPH_PREVIEW") is { Length: > 0 } preview)
            {
                Directory.CreateDirectory(preview);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var bitmap = window.CaptureRenderedFrame()!;
                bitmap.Save(Path.Combine(preview, $"startup-first-frame-{customTitleBar}.png"), PngBitmapEncoderOptions.Default);
            }
            SpinUntil(() => !overlay.IsVisible);

            Assert.False(overlay.IsVisible);
            Assert.True(visible.Elapsed >= TimeSpan.FromSeconds(2));
            Assert.False(setupWasVisible);
            var vm = Assert.IsType<MainWindowViewModel>(window.DataContext);
            Assert.True(vm.IsVaultReady);
            Assert.NotNull(vm.CurrentPage);
            window.Hide();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.False(overlay.IsVisible);
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            firstInstance.SetValue(null, previous);
        }
    }

    [Fact]
    public void AtRest_ItCoversNothing()
    {
        using var context = new UiContext();
        var (screen, _, window) = Mount();
        try
        {
            Assert.False(screen.IsVisible);
            Assert.Equal(0, screen.Opacity);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void WhileTheWarmUpRuns_ItCoversTheWindow_AndLeavesWhenTheWarmUpEnds()
    {
        using var context = new UiContext();
        var (screen, _, window) = Mount();
        try
        {
            Show(screen, window);

            Assert.True(screen.IsVisible);
            SpinUntil(() => screen.Opacity == 1);
            Assert.Equal(1, screen.Opacity);

            screen.IsWarmUpActive = false;
            Dispatcher.UIThread.RunJobs();
            SpinUntil(() => !screen.IsVisible);

            Assert.False(screen.IsVisible);
            Assert.Equal(0, screen.Opacity);
        }
        finally { window.Close(); }
    }

    /// <summary>Reactivar la cortina mientras se estaba retirando tiene que dejar el estado del último aviso,
    /// no el de la pasada que quedó a medias: es el caso de un cambio de vault durante el arranque.</summary>
    [Fact]
    public void AnEntranceThatInterruptsAnExit_Wins()
    {
        using var context = new UiContext();
        var (screen, _, window) = Mount();
        try
        {
            Show(screen, window);
            SpinUntil(() => screen.Opacity == 1);

            screen.IsWarmUpActive = false;
            Dispatcher.UIThread.RunJobs();
            screen.IsWarmUpActive = true;
            Dispatcher.UIThread.RunJobs();

            SpinUntil(() => screen.Opacity == 1);
            Assert.True(screen.IsVisible);
            Assert.Equal(1, screen.Opacity);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void TheSameBar_CompletesItsTimelineWithoutChangingLoadingModes()
    {
        using var context = new UiContext();
        var (screen, vm, window) = Mount();
        try
        {
            var progress = screen.FindControl<Border>("StartupProgress")!;
            Show(screen, window);
            var visual = ElementComposition.GetElementVisual(progress)!;
            Assert.NotNull(visual);
            Assert.InRange(visual.Scale.X, 0, 0.1);
            if (Environment.GetEnvironmentVariable("ENKI_GRAPH_PREVIEW") is { Length: > 0 } preview)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                // No se bombea la UI durante este intervalo: la barra debe avanzar en el compositor.
                Thread.Sleep(650);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = window.CaptureRenderedFrame()!;
                Directory.CreateDirectory(preview);
                frame.Save(Path.Combine(preview, "startup-progress-midpoint.png"), PngBitmapEncoderOptions.Default);
                using var pixels = new MemoryStream();
                frame.Save(pixels, PngBitmapEncoderOptions.Default);
                using var decoded = SkiaSharp.SKBitmap.Decode(pixels.ToArray());
                var origin = progress.TranslatePoint(default, window)!.Value;
                Assert.NotEqual(decoded.GetPixel((int)origin.X + 15, (int)origin.Y + 1),
                    decoded.GetPixel((int)origin.X + 240, (int)origin.Y + 1));
            }
            screen.DataContext = null;
            screen.DataContext = vm;
            Assert.Same(progress, screen.FindControl<Border>("StartupProgress"));
            SpinUntil(() => visual.Scale.X == 1);
            Assert.Equal(1, visual.Scale.X);
            Assert.True(screen.IsVisible);
            screen.IsWarmUpActive = false;
            SpinUntil(() => !screen.IsVisible);
            Assert.False(screen.IsVisible);
        }
        finally { window.Close(); }
    }

    /// <summary>Sin vault configurado no hay nada que precargar: la ventana enseña la pantalla de
    /// configuración y la de arranque se queda quieta y sin tapar nada (es el único caso en el que la shell
    /// no arranca la precarga, ver <c>MainWindowViewModel.ShowVaultSetupOrShell</c>).</summary>
    [Fact]
    public void TheMainWindow_WithNoVault_KeepsTheLaunchScreenOutOfTheWay()
    {
        using var context = new UiContext();
        var window = new MainWindow(new SettingsService(_directory));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var overlay = window.GetVisualDescendants().OfType<StartupScreen>().Single();
            Assert.False(overlay.IsVisible);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
