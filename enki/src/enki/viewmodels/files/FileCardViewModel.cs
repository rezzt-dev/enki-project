using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
    public bool IsOther => File.Kind == VaultFileKind.Other;

    /// <summary>Fixed icon for kinds without a decoded <see cref="Thumbnail"/> yet, or that never get one
    /// (see <see cref="EnsureThumbnailLoaded"/>).</summary>
    public MaterialIconKind IconKind => File.Kind switch
    {
        VaultFileKind.Pdf => MaterialIconKind.FilePdfBox,
        _ => MaterialIconKind.FileOutline
    };

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
        File = file;
        _thumbnailLoadAttempted = false;
        Thumbnail = null;
        PdfDocument = null;
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(ModifiedLabel));
        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(IsPdf));
        OnPropertyChanged(nameof(IsOther));
        OnPropertyChanged(nameof(IconKind));
        OnPropertyChanged(nameof(PdfDocument));
    }

    /// <summary>Decodes the grid-card thumbnail the first time this card is actually realized on screen —
    /// not eagerly for every file in a folder, which could hold hundreds of images or PDFs. Images decode
    /// directly; PDFs render their first page via <see cref="PdfRenderHelper"/> at a small fixed width, so
    /// the "Archivos" grid shows a real page preview instead of a generic icon for PDFs too. Runs off the
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
        var vaultService = _vaultService;
        Task.Run(() =>
        {
            try
            {
                Bitmap bitmap;
                if (isImage)
                {
                    // Decrypts straight into memory if this file lives in a protected session — no temp
                    // file needed since Bitmap can decode from any stream, unlike the PDF path below.
                    var bytes = vaultService.ReadFileBytes(fullPath);
                    using var stream = new MemoryStream(bytes);
                    bitmap = new Bitmap(stream);
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
