using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Multi-page state for a single open PDF preview — page count, the current page's rendered
/// image, zoom and the page-thumbnail rail. One instance per <see cref="FileCardViewModel"/> (see
/// <see cref="FileCardViewModel.EnsurePdfDocumentLoaded"/>), created lazily the first time that card's
/// preview is opened and kept around for the rest of the session so reopening the same PDF doesn't
/// re-render it from scratch.</summary>
public sealed partial class PdfDocumentViewModel : ObservableObject
{
    // A middling screen DPI: legible without rendering a huge bitmap for every page turn. Zoom just
    // scales this before each render — see RenderCurrentPageAsync — rather than resampling an
    // already-rendered bitmap, so zooming in actually asks PDFium for more detail instead of blurring.
    private const int BaseDpi = 130;
    private const double MinZoom = 0.5;
    private const double MaxZoom = 3.0;
    private const double ZoomStep = 0.25;
    private const int ThumbnailWidth = 120;

    private readonly VaultService _vaultService;
    private readonly string _rawFullPath;

    /// <summary>The path every render call below actually reads from — <paramref name="fullPath"/>
    /// unchanged for a plain file, or a decrypted temp copy (see <see cref="_isTempCopy"/>) resolved once
    /// in <see cref="LoadAsync"/> for a file in a protected session. Empty until that first resolve
    /// completes.</summary>
    private string _fullPath = string.Empty;

    /// <summary>Whether <see cref="_fullPath"/> is a decrypted temp copy that <see cref="Close"/> needs to
    /// delete — false for a plain (non-encrypted-session) PDF, where <see cref="_fullPath"/> just is
    /// <see cref="_rawFullPath"/> and there's nothing to clean up.</summary>
    private bool _isTempCopy;

    // Bumped on every page/zoom change and compared after the matching background render completes —
    // a stale render (superseded by a newer page turn or zoom click before it finished) is dropped
    // instead of momentarily flashing the wrong page/resolution into CurrentPageImage.
    private int _renderToken;

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

    public string PageIndicator => PageCount == 0 ? string.Empty : $"{CurrentPageNumber} / {PageCount}";
    public string ZoomLabel => $"{ZoomFactor * 100:0}%";
    public bool CanGoPrevious => CurrentPageNumber > 1;
    public bool CanGoNext => CurrentPageNumber < PageCount;
    public bool CanZoomIn => ZoomFactor < MaxZoom;
    public bool CanZoomOut => ZoomFactor > MinZoom;

    /// <summary>Neither still loading nor failed — the page rail and the current page image are safe to
    /// show. Computed rather than a fourth independent flag so it can never drift out of sync with
    /// <see cref="IsLoading"/>/<see cref="IsFaulted"/>.</summary>
    public bool IsReady => !IsLoading && !IsFaulted;

    public int CurrentPageImageWidth => CurrentPageImage?.PixelSize.Width ?? 0;
    public int CurrentPageImageHeight => CurrentPageImage?.PixelSize.Height ?? 0;

    public PdfDocumentViewModel(VaultService vaultService, string fullPath)
    {
        _vaultService = vaultService;
        _rawFullPath = fullPath;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        IsFaulted = false;
        try
        {
            // Resolved once, up front — a protected session's PDF needs a real decrypted temp file for
            // PdfRenderHelper's native (path-based) reader; every render below reuses this same path
            // instead of re-decrypting per page.
            _fullPath = await Task.Run(() => _vaultService.ResolveReadablePath(_rawFullPath));
            _isTempCopy = _fullPath != _rawFullPath;

            var count = await Task.Run(() => PdfRenderHelper.GetPageCount(_fullPath));
            if (count <= 0) throw new InvalidOperationException("El PDF no tiene páginas.");

            PageCount = count;
            for (var i = 0; i < count; i++) Pages.Add(new PdfPageThumbnailViewModel(i));
            Pages[0].IsCurrent = true;

            await RenderCurrentPageAsync();
        }
        catch
        {
            IsFaulted = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsReady));

    partial void OnIsFaultedChanged(bool value) => OnPropertyChanged(nameof(IsReady));

    partial void OnCurrentPageNumberChanged(int value)
    {
        OnPropertyChanged(nameof(PageIndicator));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        foreach (var page in Pages) page.IsCurrent = page.PageIndex == value - 1;
        _ = RenderCurrentPageAsync();
    }

    partial void OnZoomFactorChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomLabel));
        OnPropertyChanged(nameof(CanZoomIn));
        OnPropertyChanged(nameof(CanZoomOut));
        _ = RenderCurrentPageAsync();
    }

    partial void OnCurrentPageImageChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(CurrentPageImageWidth));
        OnPropertyChanged(nameof(CurrentPageImageHeight));
    }

    private async Task RenderCurrentPageAsync()
    {
        if (PageCount == 0) return;

        var token = ++_renderToken;
        var pageIndex = CurrentPageNumber - 1;
        var dpi = (int)Math.Round(BaseDpi * ZoomFactor);

        Bitmap? rendered;
        try
        {
            rendered = await Task.Run(() => PdfRenderHelper.RenderPageByDpi(_fullPath, pageIndex, dpi));
        }
        catch
        {
            // Only fail the whole preview if this was the very first render — a transient failure on a
            // later page turn/zoom step just leaves the previous page image on screen.
            if (CurrentPageImage is null) IsFaulted = true;
            return;
        }

        if (token != _renderToken) return; // superseded by a newer page/zoom change
        CurrentPageImage = rendered;
    }

    /// <summary>Renders a single rail thumbnail the first time its container is actually realized on
    /// screen (see <c>FilesView.axaml.cs</c>'s <c>PdfPageThumb_Initialized</c>) — the rail's
    /// <c>VirtualizingStackPanel</c> only realizes what's near the viewport, so a document with hundreds
    /// of pages never renders more than a handful of thumbnails at once.</summary>
    public void EnsurePageThumbnailLoaded(PdfPageThumbnailViewModel page)
    {
        if (page.LoadAttempted) return;
        page.LoadAttempted = true;

        Task.Run(() =>
        {
            try
            {
                var bitmap = PdfRenderHelper.RenderPageByWidth(_fullPath, page.PageIndex, ThumbnailWidth);
                Dispatcher.UIThread.Post(() => page.Thumbnail = bitmap);
            }
            catch
            {
                // Best-effort — a single missing rail thumbnail just stays blank, not worth surfacing.
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
    private void GoToPage(PdfPageThumbnailViewModel page) => CurrentPageNumber = page.PageNumber;

    [RelayCommand]
    private void ZoomIn()
    {
        if (CanZoomIn) ZoomFactor = Math.Min(MaxZoom, Math.Round(ZoomFactor + ZoomStep, 2));
    }

    [RelayCommand]
    private void ZoomOut()
    {
        if (CanZoomOut) ZoomFactor = Math.Max(MinZoom, Math.Round(ZoomFactor - ZoomStep, 2));
    }

    [RelayCommand]
    private void ResetZoom() => ZoomFactor = 1.0;

    /// <summary>Best-effort cleanup of the decrypted temp copy <see cref="LoadAsync"/> may have made — a
    /// no-op for a PDF outside a protected session. Called by <see cref="FileCardViewModel.UpdateFile"/>
    /// before this instance is discarded (rename/refresh); not called on every preview close, since this
    /// document is deliberately kept around so reopening the same card's preview doesn't re-render it.</summary>
    public void Close()
    {
        if (_isTempCopy) VaultService.TryCleanupDecryptedTempFile(_fullPath);
    }
}
