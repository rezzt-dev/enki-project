using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Estado de un PDF abierto en la previsualización — número de páginas, imagen de la página actual,
/// zoom y raíl de miniaturas. Una instancia por <see cref="FileCardViewModel"/> (ver
/// <see cref="FileCardViewModel.EnsurePdfDocumentLoaded"/>), creada la primera vez que se abre esa tarjeta.
///
/// <para>El zoom se mide contra el tamaño físico de la página: 100 % es la página a su tamaño en papel sobre una
/// pantalla de 96 DIP por pulgada. Por defecto el documento se ajusta al ancho del panel
/// (<see cref="IsFitWidth"/>), que es lo que se quiere al abrir un PDF; en cuanto el usuario elige un zoom
/// concreto deja de ajustarse. Cada cambio de zoom vuelve a pedirle a PDFium la página al DPI que corresponde
/// —multiplicado por la escala de la pantalla, para que en HiDPI no se vea borrosa—, así que ampliar nunca es
/// reescalar un bitmap ya hecho. Mientras llega el render nuevo, la página ya dibujada se estira al tamaño
/// nuevo: el zoom responde al instante y la nitidez llega un momento después.</para></summary>
public sealed partial class PdfDocumentViewModel : ObservableObject
{
    private const double DipsPerInch = 96;
    private const double PointsPerInch = 72;

    /// <summary>Margen horizontal total alrededor de la página en el panel (24 DIP a cada lado), descontado al
    /// ajustar al ancho para que la página no quede pegada a los bordes ni asome la barra horizontal.</summary>
    private const double FitWidthMargin = 64;

    /// <summary>Ancho en píxeles de las miniaturas del raíl: se pintan a 110 DIP, así que el doble para HiDPI.</summary>
    private const int ThumbnailWidth = 220;

    /// <summary>Lado largo máximo en píxeles de un render de página. A 400 % en una pantalla al 200 % un A4 pediría
    /// un bitmap de 9000 px de alto (más de 300 MB); por encima de este tope se renderiza a menos resolución y la
    /// página se estira, que a esos aumentos no se nota.</summary>
    private const double MaxRenderPixels = 6000;

    private static readonly double[] ZoomSteps = [0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4];
    private static double MinZoom => ZoomSteps[0];
    private static double MaxZoom => ZoomSteps[^1];

    private readonly VaultService _vaultService;
    private readonly string _rawFullPath;

    /// <summary>La ruta de la que leen todos los renders: <see cref="_rawFullPath"/> tal cual para un PDF normal,
    /// o una copia temporal descifrada (ver <see cref="_isTempCopy"/>) resuelta una sola vez en
    /// <see cref="LoadAsync"/> para uno de una sesión protegida. Vacía hasta entonces.</summary>
    private string _fullPath = string.Empty;

    private bool _isTempCopy;

    /// <summary>Se incrementa en cada render pedido y se compara al terminar: un render adelantado por otro cambio
    /// de página o de zoom antes de acabar se descarta, en vez de pintar un instante la página equivocada.</summary>
    private int _renderToken;

    /// <summary>Tamaño de cada página en puntos PDF (1/72 de pulgada). Vacío si PDFium no lo pudo leer, en cuyo
    /// caso se deduce del último render (ver <see cref="CurrentPagePoints"/>).</summary>
    private IReadOnlyList<(double Width, double Height)> _pageSizes = [];

    private int _lastRenderDpi;
    private double _viewportWidth;
    private double _renderScale = 1;
    private bool _renderScaleChanged;
    private bool _applyingFitWidth;

    /// <summary>Redimensionar la ventana notifica el ancho del panel en cada píxel: se espera a que pare antes de
    /// volver a ajustar y renderizar.</summary>
    private readonly DispatcherTimer _viewportDebounce = new() { Interval = TimeSpan.FromMilliseconds(120) };

    public ObservableCollection<PdfPageThumbnailViewModel> Pages { get; } = [];

    [ObservableProperty]
    private int _pageCount;

    [ObservableProperty]
    private int _currentPageNumber = 1;

    [ObservableProperty]
    private Bitmap? _currentPageImage;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _isFaulted;

    [ObservableProperty]
    private double _zoomFactor = 1.0;

    /// <summary>El zoom sigue al ancho del panel. Es el modo con el que se abre un PDF; elegir un zoom concreto lo
    /// apaga, y el botón de ajustar al ancho lo vuelve a encender.</summary>
    [ObservableProperty]
    private bool _isFitWidth = true;

    /// <summary>Se dispara al cambiar de página, para que la vista vuelva arriba del todo y lleve la miniatura
    /// actual del raíl a la vista — cosas de scroll que el viewmodel no puede hacer.</summary>
    public event Action? CurrentPageChanged;

    public string PageIndicator => PageCount == 0 ? string.Empty : $"{CurrentPageNumber} / {PageCount}";
    public string ZoomLabel => $"{ZoomFactor * 100:0}%";
    public bool CanGoPrevious => CurrentPageNumber > 1;
    public bool CanGoNext => CurrentPageNumber < PageCount;
    public bool CanZoomIn => ZoomFactor < MaxZoom - 0.001;
    public bool CanZoomOut => ZoomFactor > MinZoom + 0.001;

    /// <summary>Ni cargando ni con error: el raíl y la página se pueden enseñar. Calculado en vez de un tercer
    /// indicador independiente para que nunca se desincronice de <see cref="IsLoading"/>/<see cref="IsFaulted"/>.</summary>
    public bool IsReady => !IsLoading && !IsFaulted;

    /// <summary>Tamaño en DIP con el que se dibuja la página actual al zoom actual — independiente de los píxeles
    /// del bitmap, que dependen de la escala de la pantalla y del tope de <see cref="MaxRenderPixels"/>.</summary>
    public double CurrentPageDisplayWidth => CurrentPagePoints().Width / PointsPerInch * DipsPerInch * ZoomFactor;
    public double CurrentPageDisplayHeight => CurrentPagePoints().Height / PointsPerInch * DipsPerInch * ZoomFactor;

    public PdfDocumentViewModel(VaultService vaultService, string fullPath)
    {
        _vaultService = vaultService;
        _rawFullPath = fullPath;
        _viewportDebounce.Tick += (_, _) => OnViewportSettled();
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        IsFaulted = false;
        try
        {
            // Resuelta una vez al principio: el PDF de una sesión protegida necesita una copia descifrada real
            // para el lector nativo (por ruta) de PdfRenderHelper, y todos los renders reutilizan esa misma copia
            // en vez de descifrar el archivo por cada página.
            _fullPath = await Task.Run(() => _vaultService.ResolveReadablePath(_rawFullPath));
            _isTempCopy = _fullPath != _rawFullPath;

            var count = await Task.Run(() => PdfRenderHelper.GetPageCount(_fullPath));
            if (count <= 0) throw new InvalidOperationException("El PDF no tiene páginas.");

            _pageSizes = await Task.Run(() => TryReadPageSizes(_fullPath));

            PageCount = count;
            for (var i = 0; i < count; i++) Pages.Add(new PdfPageThumbnailViewModel(i));
            Pages[0].IsCurrent = true;

            if (IsFitWidth) ApplyFitWidth();
            await RenderCurrentPageAsync();
        }
        catch (Exception ex)
        {
            IsFaulted = true;
            LogService.Warning($"No se pudo abrir o renderizar el PDF '{_rawFullPath}'", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static IReadOnlyList<(double Width, double Height)> TryReadPageSizes(string path)
    {
        try
        {
            return PdfRenderHelper.GetPageSizes(path);
        }
        catch (Exception ex)
        {
            // Sin tamaños se sigue pudiendo ver el PDF: se deducen del primer render.
            LogService.Debug($"No se pudieron leer los tamaños de página de '{path}': {ex}");
            return [];
        }
    }

    /// <summary>La vista informa del ancho disponible para la página y de la escala de la pantalla cada vez que
    /// cambian. El ajuste y el render se aplazan hasta que el tamaño deja de cambiar.</summary>
    public void SetViewport(double width, double renderScale)
    {
        if (width <= 0) return;
        if (renderScale <= 0) renderScale = 1;

        var widthChanged = Math.Abs(width - _viewportWidth) > 1;
        var scaleChanged = Math.Abs(renderScale - _renderScale) > 0.01;
        if (!widthChanged && !scaleChanged) return;

        _viewportWidth = width;
        _renderScale = renderScale;
        _renderScaleChanged |= scaleChanged;

        _viewportDebounce.Stop();
        _viewportDebounce.Start();
    }

    private void OnViewportSettled()
    {
        _viewportDebounce.Stop();
        if (PageCount == 0 || IsLoading) return;

        var zoomBefore = ZoomFactor;
        if (IsFitWidth) ApplyFitWidth();

        // Si el ajuste cambió el zoom, OnZoomFactorChanged ya ha pedido el render; si no, solo hace falta uno
        // nuevo cuando lo que cambió es la escala de la pantalla (la ventana pasó a otro monitor).
        if (_renderScaleChanged && Math.Abs(zoomBefore - ZoomFactor) < 0.0001) _ = RenderCurrentPageAsync();
        _renderScaleChanged = false;
    }

    private void ApplyFitWidth()
    {
        if (_viewportWidth <= 0 || PageCount == 0) return;

        var pageWidthDips = CurrentPagePoints().Width / PointsPerInch * DipsPerInch;
        if (pageWidthDips <= 0) return;

        var zoom = Math.Clamp((_viewportWidth - FitWidthMargin) / pageWidthDips, MinZoom, MaxZoom);
        _applyingFitWidth = true;
        try
        {
            ZoomFactor = Math.Round(zoom, 3);
        }
        finally
        {
            _applyingFitWidth = false;
        }
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsReady));

    partial void OnIsFaultedChanged(bool value) => OnPropertyChanged(nameof(IsReady));

    partial void OnIsFitWidthChanged(bool value)
    {
        if (value) ApplyFitWidth();
    }

    partial void OnPageCountChanged(int value)
    {
        OnPropertyChanged(nameof(PageIndicator));
        OnPropertyChanged(nameof(CanGoNext));
    }

    partial void OnCurrentPageNumberChanged(int value)
    {
        OnPropertyChanged(nameof(PageIndicator));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        foreach (var page in Pages) page.IsCurrent = page.PageIndex == value - 1;

        OnPropertyChanged(nameof(CurrentPageDisplayWidth));
        OnPropertyChanged(nameof(CurrentPageDisplayHeight));

        // Un PDF puede mezclar tamaños de página (un anexo apaisado): ajustado al ancho, cada una se ajusta a
        // su propio ancho. Si el ajuste no mueve el zoom, el render lo pide esta misma llamada.
        var zoomBefore = ZoomFactor;
        if (IsFitWidth) ApplyFitWidth();
        if (Math.Abs(zoomBefore - ZoomFactor) < 0.0001 && !IsLoading) _ = RenderCurrentPageAsync();

        CurrentPageChanged?.Invoke();
    }

    partial void OnZoomFactorChanged(double value)
    {
        if (!_applyingFitWidth) IsFitWidth = false;

        OnPropertyChanged(nameof(ZoomLabel));
        OnPropertyChanged(nameof(CanZoomIn));
        OnPropertyChanged(nameof(CanZoomOut));
        OnPropertyChanged(nameof(CurrentPageDisplayWidth));
        OnPropertyChanged(nameof(CurrentPageDisplayHeight));
        if (!IsLoading) _ = RenderCurrentPageAsync();
    }

    /// <summary>Tamaño en puntos de la página actual: el que declara el PDF, o si no se pudo leer, el deducido
    /// del último render; en el peor caso, carta US, para que el primer render tenga un DPI razonable.</summary>
    private (double Width, double Height) CurrentPagePoints()
    {
        var index = CurrentPageNumber - 1;
        if (index >= 0 && index < _pageSizes.Count && _pageSizes[index].Width > 0) return _pageSizes[index];

        if (CurrentPageImage is { } image && _lastRenderDpi > 0)
            return (image.PixelSize.Width / (double)_lastRenderDpi * PointsPerInch,
                    image.PixelSize.Height / (double)_lastRenderDpi * PointsPerInch);

        return (612, 792);
    }

    private async Task RenderCurrentPageAsync()
    {
        if (PageCount == 0) return;

        var token = ++_renderToken;
        var pageIndex = CurrentPageNumber - 1;
        var points = CurrentPagePoints();

        var dpi = DipsPerInch * ZoomFactor * _renderScale;
        var longestSideInches = Math.Max(points.Width, points.Height) / PointsPerInch;
        if (longestSideInches > 0) dpi = Math.Min(dpi, MaxRenderPixels / longestSideInches);
        var renderDpi = Math.Max(24, (int)Math.Round(dpi));

        Bitmap? rendered;
        try
        {
            rendered = await Task.Run(() => PdfRenderHelper.RenderPageByDpi(_fullPath, pageIndex, renderDpi));
        }
        catch (Exception ex)
        {
            // Solo el primer render tumba la previsualización entera: un fallo puntual en un cambio de página o
            // de zoom posterior deja en pantalla la página que ya había.
            if (CurrentPageImage is null) IsFaulted = true;
            LogService.Warning($"No se pudo renderizar la página {pageIndex + 1} de '{_rawFullPath}'", ex);
            return;
        }

        if (token != _renderToken) return;
        _lastRenderDpi = renderDpi;
        CurrentPageImage = rendered;
        OnPropertyChanged(nameof(CurrentPageDisplayWidth));
        OnPropertyChanged(nameof(CurrentPageDisplayHeight));
    }

    /// <summary>Renderiza una miniatura del raíl la primera vez que su contenedor se realiza en pantalla (ver
    /// <c>FilePreviewView.PdfPageThumb_Initialized</c>). El raíl es un <c>VirtualizingStackPanel</c>, así que un
    /// documento de cientos de páginas nunca renderiza más de un puñado de miniaturas a la vez.</summary>
    public void EnsurePageThumbnailLoaded(PdfPageThumbnailViewModel page)
    {
        if (page.LoadAttempted || _fullPath.Length == 0) return;
        page.LoadAttempted = true;

        Task.Run(() =>
        {
            try
            {
                var bitmap = PdfRenderHelper.RenderPageByWidth(_fullPath, page.PageIndex, ThumbnailWidth);
                Dispatcher.UIThread.Post(() => page.Thumbnail = bitmap);
            }
            catch (Exception ex)
            {
                // Una miniatura suelta que no se pudo renderizar se queda en blanco; no merece un aviso.
                LogService.Debug($"No se pudo renderizar la miniatura {page.PageIndex + 1} de '{_rawFullPath}': {ex}");
            }
        });
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (CanGoPrevious) CurrentPageNumber--;
    }

    [RelayCommand]
    private void NextPage()
    {
        if (CanGoNext) CurrentPageNumber++;
    }

    [RelayCommand]
    private void FirstPage()
    {
        if (PageCount > 0) CurrentPageNumber = 1;
    }

    [RelayCommand]
    private void LastPage()
    {
        if (PageCount > 0) CurrentPageNumber = PageCount;
    }

    [RelayCommand]
    private void GoToPage(PdfPageThumbnailViewModel page) => CurrentPageNumber = page.PageNumber;

    /// <summary>Al siguiente escalón de zoom por encima del actual. Escalones fijos y no un paso constante: desde
    /// un zoom ajustado al ancho (p. ej. 83 %) se llega a un valor redondo en vez de a 108 %.</summary>
    [RelayCommand]
    private void ZoomIn()
    {
        foreach (var step in ZoomSteps)
        {
            if (step <= ZoomFactor + 0.001) continue;
            ZoomFactor = step;
            return;
        }
    }

    [RelayCommand]
    private void ZoomOut()
    {
        for (var i = ZoomSteps.Length - 1; i >= 0; i--)
        {
            if (ZoomSteps[i] >= ZoomFactor - 0.001) continue;
            ZoomFactor = ZoomSteps[i];
            return;
        }
    }

    /// <summary>Ctrl + rueda del ratón sobre la página.</summary>
    public void ZoomByWheel(double delta)
    {
        if (delta > 0) ZoomIn();
        else if (delta < 0) ZoomOut();
    }

    [RelayCommand]
    private void FitWidth()
    {
        if (IsFitWidth) ApplyFitWidth();
        else IsFitWidth = true;
    }

    [RelayCommand]
    private void ActualSize() => ZoomFactor = 1.0;

    /// <summary>Borra, si la hubo, la copia temporal descifrada que hizo <see cref="LoadAsync"/> — no-op para un
    /// PDF fuera de una sesión protegida. Lo llaman <see cref="FileCardViewModel.UpdateFile"/> y las recargas de
    /// la sección antes de descartar la tarjeta.</summary>
    public void Close()
    {
        _viewportDebounce.Stop();
        if (_isTempCopy) VaultService.TryCleanupDecryptedTempFile(_fullPath);
    }
}
