using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using SkiaSharp;
using Xunit;

namespace enki.UITests;

/// <summary>Ajustes › Apariencia › Logo, montado contra la vista real: que la vista previa se pinte con la
/// imagen importada, que elegir "Circular" la recorte de verdad (el único recorte redondo del sistema de
/// diseño) y que quitar la imagen devuelva la tarjeta a su estado vacío. La lógica de optimización se
/// prueba aparte, sin plataforma gráfica, en <c>enki.Tests</c>.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class BrandLogoSettingsUiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");

    public BrandLogoSettingsUiTests() => HeadlessApp.EnsureInitialized();

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private (SettingsView View, SettingsViewModel Vm, Window Window) CreateView()
    {
        var settings = new SettingsService(_directory);
        var vault = new VaultService(settings);
        vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        var auth = new PolarAuthService(settings);
        var sync = new PolarSyncService(auth, new SupabaseRestClient(auth), new PolarTaskService(_directory),
            new ReminderService(_directory), settings);
        var vm = new SettingsViewModel(settings, new CustomThemeService(settings), new BrandLogoService(_directory),
            new VaultSettingsService(vault), vault, new VaultBackupService(vault, _directory), auth, sync,
            new NotificationService(settings, _directory),
            () => { }, _ => { }, _ => { }, _ => { }, _ => { }, (_, _) => { }, () => { }, () => { },
            _ => { }, _ => { }, _ => { }, _ => { }, () => { });
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 900 };
        window.Show();
        return (view, vm, window);
    }

    /// <summary>El Border de 44×44 con el que la tarjeta enseña la vista previa — se localiza por su tamaño
    /// y por llevar dentro la imagen enlazada a <see cref="SettingsViewModel.BrandLogoPreview"/>, sin
    /// nombrarlo en el XAML solo para el test.</summary>
    private static Border FindPreview(SettingsView view) => view.GetVisualDescendants().OfType<Border>()
        .Single(b => b is { Width: 44, Height: 44 } && b.Child is Image);

    private string WriteSampleImage()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "origen.png");
        using var bitmap = new SKBitmap(400, 400, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.OrangeRed);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    [Fact]
    public void Card_ShowsTheImportedImage_AndRoundsItOnlyWhenCircularIsChosen()
    {
        var (view, vm, window) = CreateView();
        try
        {
            var preview = FindPreview(view);
            var picture = (Image)preview.Child!;
            Assert.False(vm.HasCustomBrandLogo);
            Assert.False(preview.IsVisible);

            // ImportBrandLogo es el camino real del comando una vez elegido el archivo; el diálogo del
            // sistema que lo precede no existe en un host headless.
            vm.ImportBrandLogo(WriteSampleImage());
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.True(vm.HasCustomBrandLogo);
            Assert.True(preview.IsVisible);
            Assert.NotNull(picture.Source);
            Assert.Equal(new Avalonia.CornerRadius(0), preview.CornerRadius);
            Assert.Equal(Stretch.Uniform, picture.Stretch);

            vm.IsBrandLogoCircular = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            // La mitad del lado: un círculo exacto, y ClipToBounds es lo que lo recorta de verdad.
            Assert.Equal(new Avalonia.CornerRadius(22), preview.CornerRadius);
            Assert.Equal(Stretch.UniformToFill, picture.Stretch);
            Assert.True(preview.ClipToBounds);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Card_TurnsCustomModeOffAndHidesThePreviewWhenTheImageIsRemoved()
    {
        var (view, vm, window) = CreateView();
        try
        {
            vm.ImportBrandLogo(WriteSampleImage());
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(new SettingsService(_directory).Settings.UseCustomBrandLogo);

            vm.RemoveBrandLogoCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.False(vm.HasCustomBrandLogo);
            Assert.False(vm.UseCustomBrandLogo);
            Assert.Null(vm.BrandLogoPreview);
            Assert.False(FindPreview(view).IsVisible);
            Assert.False(new SettingsService(_directory).Settings.UseCustomBrandLogo);
        }
        finally { window.Close(); }
    }

    /// <summary>Sin imagen no se puede quedar en modo personalizado: el ajuste guardado a mano, o la imagen
    /// borrada desde fuera de Enki, tienen que caer al icono por defecto en vez de dejar el hueco vacío.</summary>
    [Fact]
    public void CustomMode_CannotBeTurnedOnWithoutAnImage()
    {
        var (_, vm, window) = CreateView();
        try
        {
            vm.UseCustomBrandLogo = true;

            Assert.False(vm.UseCustomBrandLogo);
            Assert.False(new SettingsService(_directory).Settings.UseCustomBrandLogo);
        }
        finally { window.Close(); }
    }
}
