using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace enki.ViewModels;

/// <summary>One entry in a PDF's page-navigation rail (see <see cref="PdfDocumentViewModel.Pages"/>) —
/// just enough state to render a small thumbnail lazily and highlight the currently-viewed page.</summary>
public sealed partial class PdfPageThumbnailViewModel(int pageIndex) : ObservableObject
{
    public int PageIndex { get; } = pageIndex;
    public int PageNumber => PageIndex + 1;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>Guards against re-rendering the same thumbnail twice — set the first time
    /// <see cref="PdfDocumentViewModel.EnsurePageThumbnailLoaded"/> is called for this page, mirroring
    /// <c>FileCardViewModel</c>'s own lazy-thumbnail guard.</summary>
    internal bool LoadAttempted;
}
