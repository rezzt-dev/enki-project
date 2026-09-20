using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using enki.ViewModels;
using enki.Services;

namespace enki.Views;

/// <summary>Code-behind de la previsualización: instalar TextMate sobre el visor de código (con el tema claro u
/// oscuro de la app), el renderizado perezoso de las miniaturas del raíl del PDF, y lo que el visor de PDF
/// necesita de la vista —el ancho disponible para ajustar la página, volver arriba al cambiar de página y el zoom
/// con Ctrl + rueda—. Vive aquí y no en las disposiciones porque las dos montan este mismo control, y porque es
/// justo lo que no debe existir por duplicado: una instalación de TextMate y un PDFium por cada vista viva.</summary>
public partial class FilePreviewView : UserControl
{
    private TextMate.Installation? _codeTextMateInstallation;
    private RegistryOptions? _codeRegistryOptions;
    private FileCardViewModel? _currentCodeCard;
    private string? _appliedCodeLanguage;

    private PdfDocumentViewModel? _pdfDocument;
    private FilesViewModel? _subscribedViewModel;

    public FilePreviewView()
    {
        InitializeComponent();

        CodeEditor.Initialized += CodeEditor_Initialized;
        CodeEditor.DataContextChanged += CodeEditor_DataContextChanged;

        PdfPageScroller.DataContextChanged += (_, _) => AttachPdfDocument(PdfPageScroller.DataContext as PdfDocumentViewModel);
        PdfPageHost.SizeChanged += (_, _) => ReportPdfViewport();
        PdfPageScroller.AddHandler(PointerWheelChangedEvent, PdfPageScroller_PointerWheelChanged, RoutingStrategies.Tunnel);

        // Un clic en una zona sin controles (el fondo de la imagen, el margen del PDF) deja el foco aquí, para que
        // los atajos de la previsualización respondan. Los controles que se quedan el clic —botones, el editor de
        // código— lo marcan como manejado y no llegan a este manejador.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.Handled) Focus();
        }, RoutingStrategies.Bubble);

        DataContextChanged += (_, _) => SubscribeToViewModel();
        AttachedToVisualTree += (_, _) =>
        {
            SubscribeToViewModel();
            if (Application.Current is { } app) app.ActualThemeVariantChanged += OnAppThemeChanged;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_subscribedViewModel is not null) _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
            if (Application.Current is { } app) app.ActualThemeVariantChanged -= OnAppThemeChanged;
            AttachPdfDocument(null);
            AttachCodeCard(null);
        };
    }

    private void SubscribeToViewModel()
    {
        var vm = DataContext as FilesViewModel;
        if (ReferenceEquals(vm, _subscribedViewModel)) return;

        if (_subscribedViewModel is not null) _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedViewModel = vm;
        if (vm is not null) vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>En el modo explorador la previsualización sustituye a la rejilla, así que se queda el foco al
    /// abrirse: Esc, las flechas del PDF y Alt+flechas funcionan sin tener que pulsar antes dentro. En el modo
    /// árbol no: ahí el foco es del árbol, que se recorre con las mismas flechas.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FilesViewModel.ActivePreview)) return;
        if (sender is not FilesViewModel { ActivePreview: not null, IsTreeView: false }) return;

        Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Input);
    }

    // ===================== Visor de código =====================

    /// <summary>Instala TextMate sobre el editor de código la primera vez que se crea su control nativo. Las
    /// gramáticas se cargan una sola vez (RegistryOptions es costoso de construir) y la instalación se reutiliza
    /// mientras esta vista viva. El tema sigue a la app: DarkPlus con un tema oscuro y LightPlus con uno claro —
    /// antes era DarkPlus siempre, un bloque negro en mitad de una app clara.</summary>
    private void CodeEditor_Initialized(object? sender, EventArgs e)
    {
        if (_codeRegistryOptions is not null) return;

        var theme = CurrentCodeTheme();
        _codeRegistryOptions = new RegistryOptions(theme);
        _codeTextMateInstallation = CodeEditor.InstallTextMate(_codeRegistryOptions);

        // Aplicar explícitamente el tema sobre el editor — sin esto, el foreground y background del tema no se
        // aplican y el editor se queda con los defaults de Avalonia.
        _codeTextMateInstallation.SetTheme(_codeRegistryOptions.LoadTheme(theme));

        // Aire entre el margen de números de línea y el texto: sin esto el código empieza pegado a la costura.
        CodeEditor.TextArea.TextView.Margin = new Thickness(12, 0, 0, 0);
        ApplyCodeGrammar();
    }

    private static ThemeName CurrentCodeTheme() =>
        Application.Current?.ActualThemeVariant == ThemeVariant.Light ? ThemeName.LightPlus : ThemeName.DarkPlus;

    private void OnAppThemeChanged(object? sender, EventArgs e)
    {
        if (_codeTextMateInstallation is null || _codeRegistryOptions is null) return;
        _codeTextMateInstallation.SetTheme(_codeRegistryOptions.LoadTheme(CurrentCodeTheme()));
    }

    private void CodeEditor_DataContextChanged(object? sender, EventArgs e) =>
        AttachCodeCard(CodeEditor.DataContext as FileCardViewModel);

    /// <summary>Sigue a la tarjeta que enseña el editor también después de montarla: renombrar un archivo puede
    /// cambiarle la extensión (y con ella el lenguaje) sin que cambie la tarjeta, y antes la gramática se
    /// quedaba en la del nombre viejo.</summary>
    private void AttachCodeCard(FileCardViewModel? card)
    {
        if (ReferenceEquals(card, _currentCodeCard)) return;

        if (_currentCodeCard is not null) _currentCodeCard.PropertyChanged -= OnCodeCardPropertyChanged;
        _currentCodeCard = card;
        if (card is null) return;

        card.PropertyChanged += OnCodeCardPropertyChanged;
        ApplyCodeGrammar();
    }

    private void OnCodeCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileCardViewModel.CodeLanguage)) ApplyCodeGrammar();
    }

    /// <summary>Resuelve la gramática por extensión (<c>GetLanguageByExtension</c> → <c>GetScopeByLanguageId</c>,
    /// la API documentada de AvaloniaEdit.TextMate). Una extensión sin gramática (<c>.log</c>, <c>.txt</c>) deja
    /// el editor sin resaltado en vez de con los colores del último lenguaje abierto.</summary>
    private void ApplyCodeGrammar()
    {
        if (_codeTextMateInstallation is null || _codeRegistryOptions is null) return;
        if (_currentCodeCard?.CodeLanguage is not { } language) return;
        if (string.Equals(language, _appliedCodeLanguage, StringComparison.OrdinalIgnoreCase)) return;

        _appliedCodeLanguage = language;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var scopeName = _codeRegistryOptions.GetLanguageByExtension(language) is { } found
                    ? _codeRegistryOptions.GetScopeByLanguageId(found.Id)
                    : null;
                _codeTextMateInstallation.SetGrammar(scopeName);
            }
            catch (Exception ex)
            {
                // Gramática que TextMate no sabe cargar: el texto se sigue viendo, solo que sin colores.
                LogService.Debug($"No se pudo cargar la gramática TextMate para '{language}': {ex}");
            }
        }, DispatcherPriority.Loaded);
    }

    // ===================== Visor de PDF =====================

    private void AttachPdfDocument(PdfDocumentViewModel? document)
    {
        if (ReferenceEquals(document, _pdfDocument)) return;

        if (_pdfDocument is not null) _pdfDocument.CurrentPageChanged -= OnPdfCurrentPageChanged;
        _pdfDocument = document;
        if (document is null) return;

        document.CurrentPageChanged += OnPdfCurrentPageChanged;
        ReportPdfViewport();
    }

    /// <summary>El visor ajusta la página al ancho disponible y renderiza a la escala de la pantalla: los dos
    /// datos solo los tiene la vista. Con el visor todavía oculto (cargando) el ancho es 0 y no se informa; el
    /// cambio de tamaño al mostrarse lo vuelve a llamar.</summary>
    private void ReportPdfViewport()
    {
        if (_pdfDocument is null) return;
        _pdfDocument.SetViewport(PdfPageHost.Bounds.Width, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
    }

    /// <summary>Cambiar de página vuelve arriba del todo —antes la página nueva aparecía con el desplazamiento
    /// de la anterior, a media altura— y lleva su miniatura del raíl a la vista.</summary>
    private void OnPdfCurrentPageChanged()
    {
        if (_pdfDocument is not { } document) return;

        PdfPageScroller.Offset = new Vector(PdfPageScroller.Offset.X, 0);
        var index = document.CurrentPageNumber - 1;
        if (index >= 0 && index < document.Pages.Count) PdfRail.ScrollIntoView(index);
    }

    private void PdfPageScroller_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_pdfDocument is null || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        _pdfDocument.ZoomByWheel(e.Delta.Y);
        e.Handled = true;
    }

    private void PdfPageThumb_Initialized(object? sender, EventArgs e)
    {
        if (sender is not Control { DataContext: PdfPageThumbnailViewModel page } control) return;
        if (control.FindAncestorOfType<ItemsControl>()?.DataContext is PdfDocumentViewModel document)
            document.EnsurePageThumbnailLoaded(page);
    }
}
