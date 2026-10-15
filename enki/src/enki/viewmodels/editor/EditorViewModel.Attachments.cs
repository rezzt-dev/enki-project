using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;
using enki.Views.Editor.Markdown;

namespace enki.ViewModels;

/// <summary>Gestión y anotación de los adjuntos referenciados en el documento, con los visores de Archivos.</summary>
public sealed partial class EditorViewModel
{
    public event Action? AttachmentPickerRequested;

    [RelayCommand]
    private void RequestAttachmentPicker() => AttachmentPickerRequested?.Invoke();

    [RelayCommand]
    private void RemoveAttachmentReference(NoteAttachmentItemViewModel item)
    {
        RefreshAttachments();
        if (!Attachments.Contains(item)) return;
        Document.Remove(item.Offset, item.Length);
        OnTextChanged(Document.Text);
        RefreshAttachments();
        if (!Attachments.Any(a => a.Target == _previewTarget)) CloseAttachmentPreview();
    }

    public ObservableCollection<NoteAttachmentItemViewModel> Attachments { get; } = [];
    [ObservableProperty] private bool _isAttachmentsOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachmentPreview))]
    private FileCardViewModel? _attachmentPreview;
    public bool HasAttachmentPreview => AttachmentPreview is not null;
    [ObservableProperty] private string _attachmentAnnotation = string.Empty;
    private string? _previewTarget;
    private readonly Dictionary<string, string> _annotationDrafts = new(StringComparer.Ordinal);

    private bool CanAddAttachmentAnnotation => _previewTarget is not null && !string.IsNullOrWhiteSpace(AttachmentAnnotation);
    partial void OnAttachmentAnnotationChanged(string value) => AddAttachmentAnnotationCommand.NotifyCanExecuteChanged();

    public string? AttachFile(string sourcePath)
    {
        try
        {
            if (_vaultService.IsPathInLockedSession(Note.FullPath)) throw new SessionLockedException(Note.FullPath);
            var reference = NoteImageAttachments.IsImageFile(sourcePath)
                ? ImageService.SaveFromFile(Note, sourcePath)
                : new NoteAttachmentService(_vaultService).SaveFromFile(Note, sourcePath);
            if (reference is null) throw new FileNotFoundException("El archivo no está disponible.", sourcePath);
            return NoteImageAttachments.IsImageFile(sourcePath)
                ? $"![]({reference})"
                : (NoteAttachmentService.IsAttachmentTarget(reference) ? "!" : "")
                    + $"[{NoteAttachmentService.EscapeLabel(Path.GetFileName(sourcePath))}]({reference})";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException
                                      or VaultUnavailableException or NoteImageAttachmentException)
        {
            ReportAttachmentFailure(ex);
            return null;
        }
    }

    private void ReportAttachmentFailure(Exception ex)
    {
        LogService.Warning($"No se pudo procesar un adjunto de '{Note.FullPath}'", ex);
        _notificationService?.Push(LocalizationService.Instance["Editor_Attachments_Error"],
            ex is SessionLockedException ? VaultFailureReasons.Describe(ex)
                : LocalizationService.Instance["Editor_Attachments_ErrorHint"], NotificationSeverity.Warning);
    }

    [RelayCommand]
    private void ShowAttachments()
    {
        RefreshAttachments();
        IsAttachmentsOpen = true;
    }

    public void RefreshAttachments()
    {
        Attachments.Clear();
        var content = Document.Text;
        var fences = MarkdownLineParser.ComputeFences(content);
        var offset = 0;
        var lineNumber = 1;
        foreach (var rawLine in content.Split('\n'))
        {
            fences.TryGetValue(lineNumber, out var fence);
            var parsed = MarkdownLineParser.Parse(rawLine.TrimEnd('\r'), offset, fence);
            foreach (var span in parsed.Spans.Where(s => s.Kind is MdSpanKind.Image or MdSpanKind.Link))
            {
                if (span.LinkTarget is not { } target || !ExternalLinkPolicy.LooksLikeLocalPath(target)
                    || target.StartsWith('#') || target.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                Attachments.Add(new(target, content.Substring(span.ContentStart, span.ContentEnd - span.ContentStart),
                    span.Start, span.End - span.Start) { IsSelected = target == _previewTarget });
            }
            offset += rawLine.Length + 1;
            lineNumber++;
        }
    }

    public void OpenAttachmentTarget(string target)
    {
        var resolution = ExternalLinkPolicy.Resolve(target, NoteDirectory, VaultRootPath);
        if (resolution.Target is { } remote && ExternalLinkPolicy.IsSafeUrl(remote))
        {
            try { Process.Start(new ProcessStartInfo(remote) { UseShellExecute = true }); }
            catch (Exception ex) { ReportAttachmentFailure(ex); }
            return;
        }
        if (resolution.Target is not { } fullPath || !File.Exists(fullPath))
        {
            ReportBlockedLink(target, resolution.Reason);
            return;
        }
        try
        {
            if (_vaultService.IsPathInLockedSession(fullPath)) throw new SessionLockedException(fullPath);
            CloseAttachmentPreview();
            var info = new FileInfo(fullPath);
            var card = new FileCardViewModel(_vaultService, new VaultFile
            {
                FullPath = fullPath, SizeBytes = info.Length, ModifiedUtc = info.LastWriteTimeUtc,
                Kind = VaultFile.KindFromPath(fullPath)
            });
            _previewTarget = target;
            AttachmentAnnotation = _annotationDrafts.GetValueOrDefault(target, string.Empty);
            AttachmentPreview = card;
            AddAttachmentAnnotationCommand.NotifyCanExecuteChanged();
            card.EnsurePreviewImageLoaded();
            card.EnsurePdfDocumentLoaded();
            card.EnsureCodeContentLoaded();
            card.EnsureMediaPreviewLoaded(message => _notificationService?.Push(
                LocalizationService.Instance["Files_MediaErrorTitle"], message, NotificationSeverity.Error));
            ShowAttachments();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            ReportAttachmentFailure(ex);
        }
    }

    [RelayCommand]
    private void PreviewAttachment(NoteAttachmentItemViewModel item) => OpenAttachmentTarget(item.Target);

    [RelayCommand]
    private void OpenAttachmentExternally()
    {
        if (_previewTarget is not { } target) return;
        var resolution = ExternalLinkPolicy.Resolve(target, NoteDirectory, VaultRootPath);
        if (resolution.Target is { } path) OpenImageExternally(path);
        else ReportBlockedLink(target, resolution.Reason);
    }

    [RelayCommand(CanExecute = nameof(CanAddAttachmentAnnotation))]
    private void AddAttachmentAnnotation()
    {
        if (_previewTarget is not { } target || string.IsNullOrWhiteSpace(AttachmentAnnotation)) return;
        RefreshAttachments();
        var item = Attachments.FirstOrDefault(a => a.Target == target);
        if (item is null) return;
        var label = NoteAttachmentService.EscapeLabel(AttachmentPreview?.FileName ?? item.FileName);
        int? page = AttachmentPreview?.PdfDocument is { IsLoading: false, IsFaulted: false, PageCount: > 0 } pdf
            ? pdf.CurrentPageNumber : null;
        // Referencia y texto viven en la nota: edición, historial, sync y conflictos usan el flujo habitual.
        var text = "\n\n> [!note] [" + label + "](" + target + ")"
            + (page is null ? string.Empty : " · " + string.Format(System.Globalization.CultureInfo.CurrentCulture,
                LocalizationService.Instance["Editor_Attachments_Page"], page))
            + "\n> " + AttachmentAnnotation.Trim().Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n').Replace("\n", "\n> ", StringComparison.Ordinal) + "\n";
        // Una referencia en una tabla o en mitad de un párrafo no se puede partir con un callout.
        var line = Document.GetLineByOffset(item.Offset);
        var standalone = Document.GetText(line).Trim() == Document.GetText(item.Offset, item.Length);
        Document.Insert(standalone ? line.EndOffset : Document.TextLength, text);
        OnTextChanged(Document.Text);
        AttachmentAnnotation = string.Empty;
        RefreshAttachments();
    }

    [RelayCommand]
    private void CloseAttachments()
    {
        IsAttachmentsOpen = false;
        CloseAttachmentPreview();
    }

    private void CloseAttachmentPreview()
    {
        if (_previewTarget is { } target) _annotationDrafts[target] = AttachmentAnnotation;
        AttachmentPreview?.MediaPreview?.Close();
        AttachmentPreview?.PdfDocument?.Close();
        AttachmentPreview = null;
        _previewTarget = null;
        AddAttachmentAnnotationCommand.NotifyCanExecuteChanged();
    }
}
