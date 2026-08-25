using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;
using enki.Services;
using Material.Icons;

namespace enki.ViewModels;

public sealed partial class FileCardViewModel : ObservableObject
{
    private const int GridThumbnailWidth = 200;

    private readonly VaultService _vaultService;

    public VaultFile File { get; private set; }

    public string FileName => File.FileName;
    public string SizeLabel => File.SizeLabel;
    public string ModifiedLabel => File.ModifiedLabel;
    public bool IsImage => File.Kind == VaultFileKind.Image;
    public bool IsPdf => File.Kind == VaultFileKind.Pdf;
    public bool IsAudio => File.Kind == VaultFileKind.Audio;
    public bool IsVideo => File.Kind == VaultFileKind.Video;
    public bool IsCode => File.Kind == VaultFileKind.Code;
    public bool IsOther => File.Kind == VaultFileKind.Other;

    /// <summary>Icono fijo para tipos sin thumbnail decodificado aún, o que nunca lo tendrán.</summary>
    public MaterialIconKind IconKind => File.Kind switch
    {
        VaultFileKind.Pdf => MaterialIconKind.FilePdfBox,
        VaultFileKind.Audio => MaterialIconKind.MusicNoteOutline,
        VaultFileKind.Video => MaterialIconKind.VideoOutline,
        VaultFileKind.Code => MaterialIconKind.CodeBraces,
        _ => MaterialIconKind.FileOutline
    };

    /// <summary>Contenido de archivo de código/texto — se carga de forma diferida (lazy) la primera
    /// vez que se abre el preview. Null para tipos que no son código.</summary>
    public string? CodeContent { get; private set; }

    /// <summary>Extensión del archivo con punto (ej. ".css", ".cs") que se pasa directamente a
    /// <c>RegistryOptions.GetLanguageByExtension</c> para resolver la gramática TextMate.
    /// También se usa como fallback para la etiqueta del badge si
    /// <see cref="CodeLanguageLabel"/> es null.</summary>
    public string? CodeLanguage { get; private set; }

    /// <summary>Nombre legible del lenguaje para el badge del encabezado (ej. "CSS", "C#").
    /// Si es null, la UI muestra <see cref="CodeLanguage"/> sin el punto.</summary>
    public string? CodeLanguageLabel { get; private set; }

    /// <summary>Documento de AvaloniaEdit vinculado al TextEditor del visor de código.
    /// Se crea bajo demanda en <see cref="EnsureCodeContentLoaded"/> y se reutiliza mientras
    /// este viewmodel viva. Null para archivos que no son código.</summary>
    public TextDocument? CodeDocument { get; private set; }

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _editingName = string.Empty;

    /// <summary>Multi-page render state — page count, current page image, zoom, page-thumbnail rail — for
    /// this card's PDF. Null until <see cref="EnsurePdfDocumentLoaded"/> is called (the first time this
    /// card's preview is opened), and null forever for non-PDF files.</summary>
    public PdfDocumentViewModel? PdfDocument { get; private set; }

    private bool _thumbnailLoadAttempted;

    public FileCardViewModel(VaultService vaultService, VaultFile file)
    {
        _vaultService = vaultService;
        File = file;
    }

    public void UpdateFile(VaultFile file)
    {
        PdfDocument?.Close();
        PdfDocument = null;
        File = file;
        _thumbnailLoadAttempted = false;
        Thumbnail = null;
        CodeContent = null;
        CodeLanguage = null;
        CodeLanguageLabel = null;
        CodeDocument = null;
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(ModifiedLabel));
        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(IsPdf));
        OnPropertyChanged(nameof(IsAudio));
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(IsCode));
        OnPropertyChanged(nameof(IsOther));
        OnPropertyChanged(nameof(IconKind));
        OnPropertyChanged(nameof(PdfDocument));
    }

    /// <summary>Decodes the grid-card thumbnail the first time this card is actually realized on screen —
    /// not eagerly for every file in a folder, which could hold hundreds of images or PDFs. Images decode
    /// directly (SVG via <see cref="SvgRenderHelper"/>, raster bitmaps via <c>new Bitmap(stream)</c>);
    /// PDFs render their first page via <see cref="PdfRenderHelper"/> at a small fixed width, so the
    /// "Archivos" grid shows a real page preview instead of a generic icon for PDFs too. Runs off the
    /// UI thread — a PDF's first page can take longer to rasterize than a plain image decode, and this is
    /// called from a realized-visual event, where blocking would stutter scrolling. No on-disk cache (see
    /// docs/seccion-archivos.md §13): decoding on the fly is cheap enough while a folder's file count stays
    /// reasonable, and adding a cache before that's actually a problem would be unused complexity.</summary>
    public void EnsureThumbnailLoaded()
    {
        if (_thumbnailLoadAttempted || (!IsImage && !IsPdf)) return;
        _thumbnailLoadAttempted = true;

        var fullPath = File.FullPath;
        var isImage = IsImage;
        var isSvg = File.IsSvg;
        var vaultService = _vaultService;
        Task.Run(() =>
        {
            try
            {
                Bitmap bitmap;
                if (isImage)
                {
                    var bytes = vaultService.ReadFileBytes(fullPath);
                    if (isSvg)
                    {
                        // SVG: rasterizar via Svg.Skia → Bitmap
                        bitmap = SvgRenderHelper.RenderSvg(bytes, GridThumbnailWidth);
                    }
                    else
                    {
                        // Raster (PNG, JPG, GIF, BMP, WebP, ICO, TIFF): SkiaSharp decodifica nativamente
                        using var stream = new MemoryStream(bytes);
                        bitmap = new Bitmap(stream);
                    }
                }
                else
                {
                    // PdfRenderHelper opens the file by path (native PDFium binding) — a protected
                    // session's PDF needs a real decrypted temp file, cleaned up right after rendering.
                    var readablePath = vaultService.ResolveReadablePath(fullPath);
                    try
                    {
                        bitmap = PdfRenderHelper.RenderPageByWidth(readablePath, pageIndex: 0, GridThumbnailWidth);
                    }
                    finally
                    {
                        if (readablePath != fullPath) VaultService.TryCleanupDecryptedTempFile(readablePath);
                    }
                }
                Dispatcher.UIThread.Post(() => Thumbnail = bitmap);
            }
            catch
            {
                Dispatcher.UIThread.Post(() => Thumbnail = null);
            }
        });
    }

    /// <summary>Creates this card's <see cref="PdfDocumentViewModel"/> and starts loading it, the first
    /// time the preview is opened for a PDF — a no-op on every call after the first, and for non-PDF
    /// files.</summary>
    public void EnsurePdfDocumentLoaded()
    {
        if (!IsPdf || PdfDocument is not null) return;
        PdfDocument = new PdfDocumentViewModel(_vaultService, File.FullPath);
        OnPropertyChanged(nameof(PdfDocument));
        _ = PdfDocument.LoadAsync();
    }

    /// <summary>Carga el contenido de un archivo de código/texto la primera vez que se abre su
    /// preview. Infiere el lenguaje de la extensión del archivo para el resaltado de sintaxis.
    /// No-op para tipos que no son código.</summary>
    public void EnsureCodeContentLoaded()
    {
        if (!IsCode || CodeContent is not null) return;

        try
        {
            var bytes = _vaultService.ReadFileBytes(File.FullPath);
            CodeContent = System.Text.Encoding.UTF8.GetString(bytes);
            // Limitar a ~500KB para que el editor de código no se bloquee con archivos enormes
            if (CodeContent.Length > 512_000)
                CodeContent = CodeContent[..512_000] + "\n\n… [archivo truncado — demasiado grande para previsualizar]";
        }
        catch
        {
            CodeContent = "// No se pudo leer el contenido del archivo.";
        }

        // Guardamos la extensión con punto (".css") para que el code-behind la pase
        // directamente a RegistryOptions.GetLanguageByExtension — la API oficial de
        // AvaloniaEdit.TextMate resuelve la gramática a partir de la extensión del archivo.
        CodeLanguage = Path.GetExtension(File.FileName);
        CodeLanguageLabel = CodeLanguage?.TrimStart('.').ToUpperInvariant();
        CodeDocument = new TextDocument(CodeContent) { FileName = File.FileName };
        OnPropertyChanged(nameof(CodeContent));
        OnPropertyChanged(nameof(CodeLanguage));
        OnPropertyChanged(nameof(CodeLanguageLabel));
        OnPropertyChanged(nameof(CodeDocument));
    }

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
