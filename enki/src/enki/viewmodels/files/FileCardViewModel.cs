using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Un archivo de la sección Archivos: lo que enseña su tarjeta de la rejilla o su fila del árbol, y lo
/// que la previsualización carga bajo demanda la primera vez que se abre (imagen a resolución completa,
/// documento PDF, texto del visor de código). Cada carga es perezosa y no-op si ya se hizo, así que una carpeta
/// con cientos de archivos no decodifica nada que no llegue a pintarse.</summary>
public sealed partial class FileCardViewModel : ObservableObject
{
    /// <summary>Ancho en píxeles de la miniatura de la rejilla. La tarjeta la pinta a unos 150 DIP, así que el
    /// doble sigue nítido en una pantalla al 200 % sin retener en memoria la foto entera de cada tarjeta — que es
    /// lo que pasaba al decodificar a tamaño completo: una carpeta de fotos de móvil eran cientos de MB.</summary>
    private const int GridThumbnailWidth = 320;

    /// <summary>Tope de ancho de la imagen de la previsualización. Una foto de 50 MP decodificada entera ocupa
    /// cientos de MB y ningún monitor la enseña a ese tamaño; por encima de esto se reduce al decodificar.</summary>
    private const int PreviewImageMaxWidth = 4096;

    /// <summary>Ancho al que se rasteriza un SVG para la previsualización. Es vectorial, así que lo que importa
    /// es que sobre nitidez al ajustarlo a la ventana: la miniatura de 320 px que se reutilizaba antes se veía
    /// borrosa en cuanto el panel era algo más ancho que la tarjeta.</summary>
    private const int SvgPreviewWidth = 1600;

    /// <summary>Por encima de este tamaño un archivo de texto no se lee para previsualizarlo: habría que cargarlo
    /// entero en memoria —y descifrarlo entero si vive en una sesión protegida— para enseñar solo el principio.</summary>
    private const long MaxCodeFileBytes = 32 * 1024 * 1024;

    /// <summary>Caracteres que carga como mucho el visor de código antes de truncar.</summary>
    private const int MaxCodePreviewChars = 512_000;

    /// <summary>Cuántas miniaturas se decodifican a la vez. Abrir una carpeta con trescientas imágenes realiza
    /// decenas de tarjetas de golpe, y lanzarlas todas en paralelo solo dispara la memoria sin acabar antes:
    /// PDFium serializa sus llamadas por dentro de todas formas.</summary>
    private static readonly SemaphoreSlim ThumbnailGate = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));

    /// <summary>Todo lo que se deriva de <see cref="File"/> y hay que volver a anunciar cuando
    /// <see cref="UpdateFile"/> cambia el archivo de debajo.</summary>
    private static readonly string[] FileDerivedProperties =
    [
        nameof(FileName), nameof(SizeLabel), nameof(ModifiedLabel), nameof(ExtensionLabel), nameof(HasExtension),
        nameof(MetaLabel), nameof(KindLabel), nameof(IsImage), nameof(IsPdf), nameof(IsAudio), nameof(IsVideo),
        nameof(IsCode), nameof(IsOther), nameof(IsPlayable), nameof(IconKind), nameof(PdfDocument),
        nameof(IsContainerPreview), nameof(CodeContent), nameof(CodeDocument), nameof(CodeLanguage), nameof(CodeLanguageLabel),
        nameof(CodeLinesLabel), nameof(IsCodeViewer), nameof(IsCodeTruncated), nameof(ShowsInfoPanel),
        nameof(PreviewHint), nameof(IsImageViewer), nameof(IsImageLoading), nameof(KindMetaLabel)
    ];

    private readonly VaultService _vaultService;

    /// <summary>Se incrementa cada vez que <see cref="UpdateFile"/> cambia el archivo de debajo, para descartar
    /// una decodificación en segundo plano que terminaría pintando el contenido del archivo anterior.</summary>
    private int _generation;

    private bool _thumbnailLoadAttempted;
    private bool _previewImageLoadAttempted;
    private bool _codeLoadAttempted;

    /// <summary>Clave del motivo por el que un archivo de texto no tiene visor (binario, demasiado grande o
    /// ilegible), o null si lo tiene o todavía no se ha intentado cargar.</summary>
    private string? _codeUnavailableKey;

    public VaultFile File { get; private set; }

    public string FileName => File.FileName;
    public string SizeLabel => File.SizeLabel;
    public string ModifiedLabel => File.ModifiedLabel;

    /// <summary>Extensión en mayúsculas y sin punto ("PDF", "PNG"), o vacía para un archivo sin extensión.</summary>
    public string ExtensionLabel => File.Extension;
    public bool HasExtension => ExtensionLabel.Length > 0;

    /// <summary>Tamaño y fecha en una sola línea: lo que cabe debajo del nombre en una tarjeta de la rejilla.</summary>
    public string MetaLabel => $"{SizeLabel} · {ModifiedLabel}";

    /// <summary>Tipo, tamaño y fecha en una línea, para la ficha del panel de información.</summary>
    public string KindMetaLabel => $"{KindLabel} · {MetaLabel}";

    public bool IsImage => File.Kind == VaultFileKind.Image;
    public bool IsPdf => File.Kind == VaultFileKind.Pdf;
    public bool IsAudio => File.Kind == VaultFileKind.Audio;
    public bool IsVideo => File.Kind == VaultFileKind.Video;
    public bool IsCode => File.Kind == VaultFileKind.Code;
    public bool IsOther => File.Kind == VaultFileKind.Other;

    /// <summary>Audio o vídeo: su botón principal dice "reproducir" en vez de "abrir".</summary>
    public bool IsPlayable => IsAudio || IsVideo;

    /// <summary>Nombre del tipo de archivo en el idioma activo, para la cabecera de la previsualización.</summary>
    public string KindLabel => LocalizationService.Instance[File.Kind switch
    {
        VaultFileKind.Image => "Files_Kind_Image",
        VaultFileKind.Pdf => "Files_Kind_Pdf",
        VaultFileKind.Audio => "Files_Kind_Audio",
        VaultFileKind.Video => "Files_Kind_Video",
        VaultFileKind.Code => "Files_Kind_Code",
        _ => "Files_Kind_Other"
    }];

    /// <summary>Icono por tipo, para cuando no hay miniatura decodificada (aún, o nunca) y para las filas del
    /// árbol. Un icono de archivo distinto por tipo, no un icono genérico de "música" o "código": en una lista
    /// densa de filas tiene que seguir leyéndose como archivo.</summary>
    public MaterialIconKind IconKind => File.Kind switch
    {
        VaultFileKind.Image => MaterialIconKind.FileImageOutline,
        VaultFileKind.Pdf => MaterialIconKind.FilePdfBox,
        VaultFileKind.Audio => MaterialIconKind.FileMusicOutline,
        VaultFileKind.Video => MaterialIconKind.FileVideoOutline,
        VaultFileKind.Code => MaterialIconKind.FileCodeOutline,
        _ => MaterialIconKind.FileOutline
    };

    /// <summary>Carpeta del archivo relativa a la carpeta desde la que se busca. Solo la rellena la búsqueda de
    /// la rejilla, que baja a las subcarpetas: navegando no hace falta, porque todos los archivos viven en la
    /// carpeta que ya dice la miga de pan.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocation))]
    private string _locationLabel = string.Empty;

    public bool HasLocation => LocationLabel.Length > 0;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _editingName = string.Empty;

    // ===================== Imagen =====================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayImage), nameof(IsImageLoading))]
    private Bitmap? _thumbnail;

    /// <summary>La imagen a resolución de pantalla que pinta la previsualización. Null hasta que
    /// <see cref="EnsurePreviewImageLoaded"/> termina de decodificarla; mientras tanto se enseña la miniatura.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayImage), nameof(IsImageLoading))]
    private Bitmap? _previewImage;

    public Bitmap? DisplayImage => PreviewImage ?? Thumbnail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsInfoPanel), nameof(PreviewHint), nameof(IsImageViewer), nameof(IsImageLoading))]
    private bool _isPreviewImageFaulted;

    /// <summary>La previsualización pinta la imagen (no el panel de información de una imagen ilegible).</summary>
    public bool IsImageViewer => IsImage && !IsPreviewImageFaulted;

    /// <summary>Todavía no hay nada que pintar: ni la imagen ni su miniatura han terminado de decodificarse.</summary>
    public bool IsImageLoading => IsImage && DisplayImage is null && !IsPreviewImageFaulted;

    /// <summary>"4032 × 3024 px" una vez decodificada la imagen, o vacía (SVG, o todavía cargando).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImageDimensions))]
    private string _imageDimensionsLabel = string.Empty;

    public bool HasImageDimensions => ImageDimensionsLabel.Length > 0;

    /// <summary>La imagen se enseña a tamaño real con barras de desplazamiento en vez de ajustada al panel.</summary>
    [ObservableProperty]
    private bool _isImageActualSize;

    // ===================== Código =====================

    /// <summary>Texto del archivo de código, cargado la primera vez que se abre su previsualización. Null para
    /// cualquier otro tipo, y para un archivo de texto que no se ha podido o no se debía leer.</summary>
    public string? CodeContent { get; private set; }

    /// <summary>Extensión con punto (".css") que el code-behind pasa a <c>RegistryOptions.GetLanguageByExtension</c>
    /// para resolver la gramática TextMate.</summary>
    public string? CodeLanguage { get; private set; }

    public string? CodeLanguageLabel { get; private set; }

    public TextDocument? CodeDocument { get; private set; }

    public bool IsCodeViewer => CodeDocument is not null;
    public bool IsContainerPreview => IsOther && ContainerPreviewService.Supports(File.FullPath);

    public bool IsCodeTruncated { get; private set; }

    public string CodeLinesLabel =>
        CodeDocument is null ? string.Empty : Plural.Get("Files_CodeLines", CodeDocument.LineCount, CodeDocument.LineCount);

    [ObservableProperty]
    private bool _isCodeWordWrap;

    // ===================== Panel de información =====================

    /// <summary>La previsualización no tiene visor para este archivo y enseña en su lugar el panel de
    /// información con el botón de abrirlo con la aplicación del sistema: tipos sin visor, texto ilegible
    /// e imágenes que no se han podido decodificar.</summary>
    public bool ShowsInfoPanel =>
        (IsPlayable && MediaPreview is null) || (IsOther && !IsCodeViewer) || (IsCode && _codeUnavailableKey is not null) || (IsImage && IsPreviewImageFaulted);

    public string PreviewHint => LocalizationService.Instance[File.Kind switch
    {
        VaultFileKind.Audio => "Files_AudioPreviewHint",
        VaultFileKind.Video => "Files_VideoPreviewHint",
        VaultFileKind.Other when _codeUnavailableKey is not null => _codeUnavailableKey,
        VaultFileKind.Code when _codeUnavailableKey is not null => _codeUnavailableKey,
        VaultFileKind.Image when IsPreviewImageFaulted => "Files_ImagePreviewErrorHint",
        _ => "Files_NoPreviewAvailable"
    }];

    /// <summary>Estado del visor de PDF de esta tarjeta — número de páginas, página actual, zoom y raíl de
    /// miniaturas. Null hasta que <see cref="EnsurePdfDocumentLoaded"/> se llama por primera vez, y siempre null
    /// para lo que no es un PDF.</summary>
    public PdfDocumentViewModel? PdfDocument { get; private set; }

    public MediaPreviewViewModel? MediaPreview { get; private set; }

    public void EnsureMediaPreviewLoaded(Action<string> reportError)
    {
        if (!IsPlayable || MediaPreview is not null) return;
        MediaPreview = new MediaPreviewViewModel(_vaultService, File, reportError);
        OnPropertyChanged(nameof(MediaPreview));
        OnPropertyChanged(nameof(ShowsInfoPanel));
    }

    public FileCardViewModel(VaultService vaultService, VaultFile file)
    {
        _vaultService = vaultService;
        File = file;
    }

    /// <summary>Vuelve a anunciar las etiquetas que dependen del idioma. Casi todas las tarjetas se recrean en
    /// la recarga que sigue a un cambio de idioma, pero la de la previsualización abierta se reutiliza a
    /// propósito (ver <c>FilesViewModel.Reload</c>), y sin esto su tipo, su pista y su contador de líneas se
    /// quedaban en el idioma anterior. Lo llama <c>FilesViewModel.RefreshLocalizedLabels</c>.</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(PreviewHint));
        OnPropertyChanged(nameof(CodeLinesLabel));
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(ModifiedLabel));
        OnPropertyChanged(nameof(MetaLabel));
        OnPropertyChanged(nameof(KindMetaLabel));
    }

    /// <summary>Apunta la tarjeta a otro archivo —el mismo renombrado o movido— y descarta todo lo cargado del
    /// anterior: miniatura, imagen, PDF (liberando su copia descifrada) y texto. Quien la esté previsualizando
    /// tiene que volver a pedir la carga (ver <c>FilesViewModel.LoadPreviewContent</c>).</summary>
    public void UpdateFile(VaultFile file)
    {
        MediaPreview?.Close();
        MediaPreview = null;
        OnPropertyChanged(nameof(MediaPreview));
        PdfDocument?.Close();
        PdfDocument = null;
        File = file;
        _generation++;

        _thumbnailLoadAttempted = false;
        _previewImageLoadAttempted = false;
        _codeLoadAttempted = false;
        _codeUnavailableKey = null;

        Thumbnail = null;
        PreviewImage = null;
        IsPreviewImageFaulted = false;
        ImageDimensionsLabel = string.Empty;
        IsImageActualSize = false;

        CodeContent = null;
        CodeLanguage = null;
        CodeLanguageLabel = null;
        CodeDocument = null;
        IsCodeTruncated = false;

        foreach (var property in FileDerivedProperties) OnPropertyChanged(property);
    }

    /// <summary>Decodifica la miniatura de la tarjeta la primera vez que se realiza en pantalla — nunca para
    /// todos los archivos de la carpeta de golpe. Las imágenes rasterizadas se reducen al decodificar
    /// (<see cref="GridThumbnailWidth"/>), los SVG pasan por <see cref="SvgRenderHelper"/> y los PDF enseñan su
    /// primera página vía <see cref="PdfRenderHelper"/>. Corre fuera del hilo de UI y como mucho
    /// <see cref="ThumbnailGate"/> a la vez; cualquier fallo deja el icono por tipo en su sitio.</summary>
    public void EnsureThumbnailLoaded()
    {
        if (_thumbnailLoadAttempted || (!IsImage && !IsPdf)) return;
        _thumbnailLoadAttempted = true;

        var generation = _generation;
        var file = File;
        var vaultService = _vaultService;
        Task.Run(async () =>
        {
            await ThumbnailGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var bitmap = file.Kind == VaultFileKind.Pdf
                    ? RenderPdfThumbnail(vaultService, file.FullPath)
                    : file.IsSvg
                        ? SvgRenderHelper.RenderSvg(vaultService.ReadFileBytes(file.FullPath), GridThumbnailWidth)
                        : DecodeRaster(vaultService.ReadFileBytes(file.FullPath), GridThumbnailWidth, file.Extension, out _);

                Dispatcher.UIThread.Post(() =>
                {
                    if (generation == _generation) Thumbnail = bitmap;
                    else bitmap.Dispose();
                });
            }
            catch (Exception ex)
            {
                // Sin miniatura: la tarjeta sigue enseñando el icono por tipo.
                LogService.Debug($"No se pudo generar la miniatura de '{file.FullPath}': {ex}");
            }
            finally
            {
                ThumbnailGate.Release();
            }
        });
    }

    private static Bitmap RenderPdfThumbnail(VaultService vaultService, string fullPath)
    {
        // PdfRenderHelper abre el archivo por ruta (enlace nativo de PDFium): el PDF de una sesión protegida
        // necesita una copia descifrada real, que se borra en cuanto termina el render.
        var readablePath = vaultService.ResolveReadablePath(fullPath);
        try
        {
            return PdfRenderHelper.RenderPageByWidth(readablePath, pageIndex: 0, GridThumbnailWidth);
        }
        finally
        {
            if (readablePath != fullPath) VaultService.TryCleanupDecryptedTempFile(readablePath);
        }
    }

    /// <summary>Decodifica la imagen que pinta la previsualización, a resolución de pantalla y fuera del hilo de
    /// UI. Hasta que termina, la previsualización enseña la miniatura como marcador de posición.</summary>
    public void EnsurePreviewImageLoaded()
    {
        if (!IsImage || _previewImageLoadAttempted) return;
        _previewImageLoadAttempted = true;

        var generation = _generation;
        var file = File;
        var vaultService = _vaultService;
        Task.Run(() =>
        {
            try
            {
                var bytes = vaultService.ReadFileBytes(file.FullPath);
                var dimensions = string.Empty;
                Bitmap bitmap;
                if (file.IsSvg)
                {
                    bitmap = SvgRenderHelper.RenderSvg(bytes, SvgPreviewWidth);
                }
                else
                {
                    bitmap = DecodeRaster(bytes, PreviewImageMaxWidth, file.Extension, out var originalSize);
                    if (originalSize.Width > 0) dimensions = $"{originalSize.Width} × {originalSize.Height} px";
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _generation) { bitmap.Dispose(); return; }
                    PreviewImage = bitmap;
                    ImageDimensionsLabel = dimensions;
                });
            }
            catch (Exception ex)
            {
                LogService.Warning($"No se pudo decodificar la imagen '{file.FullPath}' para previsualizarla", ex);
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation == _generation) IsPreviewImageFaulted = true;
                });
            }
        });
    }

    /// <summary>Decodifica el primer fotograma, corrige la orientación y limita ambas dimensiones sin ampliar.</summary>
    private static Bitmap DecodeRaster(byte[] bytes, int maxWidth, string extension, out PixelSize originalSize)
    {
        return ImagePreviewService.Decode(bytes, maxWidth, extension, out originalSize);
    }

    [RelayCommand]
    private void ToggleImageActualSize() => IsImageActualSize = !IsImageActualSize;

    /// <summary>Crea el <see cref="PdfDocumentViewModel"/> de esta tarjeta y empieza a cargarlo la primera vez
    /// que se abre la previsualización de un PDF — no-op en las siguientes llamadas y para lo que no es un PDF.</summary>
    public void EnsurePdfDocumentLoaded()
    {
        if (!IsPdf || PdfDocument is not null) return;
        PdfDocument = new PdfDocumentViewModel(_vaultService, File.FullPath);
        OnPropertyChanged(nameof(PdfDocument));
        _ = PdfDocument.LoadAsync();
    }

    /// <summary>Carga el texto de un archivo de código la primera vez que se abre su previsualización. Antes de
    /// dárselo al editor comprueba que de verdad es texto: un <c>.log</c> o un <c>.csv</c> con bytes nulos es un
    /// binario con extensión de texto, y volcarlo en el editor solo enseñaría basura. Respeta la marca de orden de
    /// bytes (UTF-8/UTF-16) y trunca los archivos enormes para que el editor no se atasque. No-op para lo que no
    /// es código.</summary>
    public void EnsureCodeContentLoaded()
    {
        if ((!IsCode && !IsContainerPreview) || _codeLoadAttempted) return;
        _codeLoadAttempted = true;

        try
        {
            if (File.SizeBytes > MaxCodeFileBytes)
            {
                _codeUnavailableKey = "Files_TooLargePreviewHint";
            }
            else
            {
                var bytes = _vaultService.ReadFileBytes(File.FullPath);
                if (IsContainerPreview)
                {
                    CodeContent = ContainerPreviewService.Read(bytes, File.FullPath, out var truncated);
                    IsCodeTruncated = truncated;
                    IsCodeWordWrap = true;
                }
                else if (LooksBinary(bytes))
                {
                    _codeUnavailableKey = "Files_BinaryPreviewHint";
                }
                else
                {
                    var text = DecodeText(bytes);
                    if (text.Length > MaxCodePreviewChars)
                    {
                        text = text[..MaxCodePreviewChars];
                        IsCodeTruncated = true;
                    }
                    CodeContent = text;
                }
            }
        }
        catch (Exception ex)
        {
            // Una sesión bloqueada, un archivo que ya no está o sin permisos: la previsualización nunca debe
            // tumbar la sección, así que cualquier fallo de lectura acaba en el panel de información.
            LogService.Warning($"No se pudo leer '{File.FullPath}' para previsualizarlo", ex);
            _codeUnavailableKey = "Files_CodeReadErrorHint";
        }

        if (CodeContent is not null)
        {
            CodeLanguage = IsContainerPreview ? ".txt" : CodePreviewService.LanguageExtension(File.FileName);
            CodeLanguageLabel = CodeLanguage.TrimStart('.').ToUpperInvariant();
            CodeDocument = new TextDocument(CodeContent) { FileName = File.FileName };
        }

        OnPropertyChanged(nameof(IsContainerPreview));
        OnPropertyChanged(nameof(CodeContent));
        OnPropertyChanged(nameof(CodeLanguage));
        OnPropertyChanged(nameof(CodeLanguageLabel));
        OnPropertyChanged(nameof(CodeDocument));
        OnPropertyChanged(nameof(CodeLinesLabel));
        OnPropertyChanged(nameof(IsCodeViewer));
        OnPropertyChanged(nameof(IsCodeTruncated));
        OnPropertyChanged(nameof(ShowsInfoPanel));
        OnPropertyChanged(nameof(PreviewHint));
    }

    /// <summary>Mismo criterio que git: un byte nulo en los primeros 8000 bytes delata un binario. Un texto
    /// UTF-16 lleva nulos legítimos, así que con marca de orden de bytes se da por texto.</summary>
    internal static bool LooksBinary(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) return false;
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            return false;

        var length = Math.Min(bytes.Length, 8000);
        var controls = 0;
        for (var i = 0; i < length; i++)
        {
            if (bytes[i] == 0) return true;
            if (bytes[i] < 32 && bytes[i] is not (9 or 10 or 12 or 13)) controls++;
        }
        return length > 0 && controls > length / 10;
    }

    private static string DecodeText(byte[] bytes)
    {
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    [RelayCommand]
    private void ToggleCodeWordWrap() => IsCodeWordWrap = !IsCodeWordWrap;

    [RelayCommand]
    private void BeginRename()
    {
        EditingName = FileName;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private void RevealInSystem() => FolderOpener.RevealFile(File.FullPath);
}
