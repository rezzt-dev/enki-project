using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
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

    /// <summary>La barra se localiza por el segmento que solo existe en modo indeterminado, no por un nombre
    /// añadido al XAML para el test.</summary>
    private static Panel FindTrack(StartupScreen screen) => screen.GetVisualDescendants()
        .OfType<Panel>()
        .First(p => p.Children.OfType<Border>().Any(b => b.Classes.Contains("startup-glide")));

    private static Border FindGlide(Panel track) =>
        track.Children.OfType<Border>().Single(b => b.Classes.Contains("startup-glide"));

    private static Border FindFill(Panel track) =>
        track.Children.OfType<Border>().Single(b => !b.Classes.Contains("startup-glide"));

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
    public void AtRest_ItCoversNothing()
    {
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
    public void TheBar_GlidesWhileNothingCanBeCounted_AndFillsWhileItCan()
    {
        var (screen, vm, window) = Mount();
        try
        {
            Show(screen, window);

            var track = FindTrack(screen);
            var glide = FindGlide(track);
            var fill = FindFill(track);

            // Fase sin pasos que contar (la indexación del vault): segmento a la vista, relleno a cero.
            Assert.True(vm.IsWarmUpIndeterminate);
            Assert.True(glide.IsVisible);
            Assert.Equal(0, fill.Width);

            // El ancho del relleno se anima con una transición, y en un host headless el reloj de animación
            // no avanza: se anula para leer el valor de destino y no uno intermedio.
            fill.Transitions = null;
            vm.IsWarmUpIndeterminate = false;
            vm.WarmUpBarFillWidth = 130;
            Dispatcher.UIThread.RunJobs();

            Assert.False(glide.IsVisible);
            Assert.Equal(130, fill.Width);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void TheStatusLine_ShowsWhatTheWarmUpIsDoing()
    {
        var (screen, vm, window) = Mount();
        try
        {
            Show(screen, window);

            vm.WarmUpStatus = "Preparando Documentos";
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(screen.GetVisualDescendants().OfType<TextBlock>(),
                block => block.Text == vm.WarmUpStatus);
        }
        finally { window.Close(); }
    }

    /// <summary>Sin vault configurado no hay nada que precargar: la ventana enseña la pantalla de
    /// configuración y la de arranque se queda quieta y sin tapar nada (es el único caso en el que la shell
    /// no arranca la precarga, ver <c>MainWindowViewModel.ShowVaultSetupOrShell</c>).</summary>
    [Fact]
    public void TheMainWindow_WithNoVault_KeepsTheLaunchScreenOutOfTheWay()
    {
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
