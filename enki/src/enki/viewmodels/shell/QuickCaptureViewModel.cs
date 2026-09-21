using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public sealed record QuickCaptureDestinationOption(QuickCaptureDestination Value, string Label);
public sealed record QuickCaptureColumnOption(string Id, string Title);

/// <summary>Estado duradero de la mini-ventana de captura. La ventana puede cerrarse y recrearse; esta
/// instancia permanece viva en la composición del shell, por lo que Esc conserva el borrador.</summary>
public sealed partial class QuickCaptureViewModel : ViewModelBase
{
    private readonly QuickCaptureService _captureService;
    private readonly VaultService _vaultService;
    private readonly KanbanService _kanbanService;
    private readonly VaultSettingsService _vaultSettingsService;
    private readonly NotificationService _notificationService;
    private readonly Action<QuickCaptureResult>? _onCaptured;

    public event Action? CloseRequested;

    public ObservableCollection<QuickCaptureDestinationOption> Destinations { get; } = [];
    public ObservableCollection<QuickCaptureColumnOption> KanbanColumns { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _draft = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsKanbanDestination))]
    private QuickCaptureDestinationOption? _selectedDestination;

    [ObservableProperty]
    private QuickCaptureColumnOption? _selectedKanbanColumn;

    [ObservableProperty]
    private string? _errorMessage;

    public bool IsKanbanDestination => SelectedDestination?.Value == QuickCaptureDestination.KanbanCard;
    public double UiZoomScale => _vaultSettingsService.Settings.UiZoomPercent / 100.0;

    public QuickCaptureViewModel(QuickCaptureService captureService, VaultService vaultService,
        KanbanService kanbanService, VaultSettingsService vaultSettingsService,
        NotificationService notificationService, Action<QuickCaptureResult>? onCaptured = null)
    {
        _captureService = captureService;
        _vaultService = vaultService;
        _kanbanService = kanbanService;
        _vaultSettingsService = vaultSettingsService;
        _notificationService = notificationService;
        _onCaptured = onCaptured;
        RefreshLocalizedLabels();
        ReloadDestinations();
    }

    public void PrepareToShow()
    {
        ErrorMessage = null;
        ReloadDestinations();
        OnPropertyChanged(nameof(UiZoomScale));
    }

    public void RefreshLocalizedLabels()
    {
        var selected = SelectedDestination?.Value ?? _vaultSettingsService.Settings.QuickCaptureDestination;
        Destinations.Clear();
        Destinations.Add(new(QuickCaptureDestination.InboxNote, LocalizationService.Instance["QuickCapture_Destination_Inbox"]));
        Destinations.Add(new(QuickCaptureDestination.KanbanCard, LocalizationService.Instance["QuickCapture_Destination_Kanban"]));
        SelectedDestination = Destinations.First(option => option.Value == selected);
    }

    /// <summary>Reconstruye columnas después de abrir/cambiar de vault y conserva la elegida cuando siga
    /// existiendo. Una preferencia obsoleta cae en la primera columna, igual que el servicio al escribir.</summary>
    public void ReloadDestinations()
    {
        var preferredId = SelectedKanbanColumn?.Id ?? _vaultSettingsService.Settings.QuickCaptureKanbanColumnId;
        KanbanColumns.Clear();
        foreach (var column in _kanbanService.Board.Columns)
            KanbanColumns.Add(new QuickCaptureColumnOption(column.Id, column.Title));
        SelectedKanbanColumn = KanbanColumns.FirstOrDefault(column => column.Id == preferredId)
                               ?? KanbanColumns.FirstOrDefault();
    }

    partial void OnSelectedDestinationChanged(QuickCaptureDestinationOption? value)
    {
        if (value is null || !_vaultService.IsVaultConfigured) return;
        _vaultSettingsService.Settings.QuickCaptureDestination = value.Value;
        _vaultSettingsService.Save();
    }

    partial void OnSelectedKanbanColumnChanged(QuickCaptureColumnOption? value)
    {
        if (value is null || !_vaultService.IsVaultConfigured) return;
        _vaultSettingsService.Settings.QuickCaptureKanbanColumnId = value.Id;
        _vaultSettingsService.Save();
    }

    private bool CanSubmit() => !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private void Submit()
    {
        ErrorMessage = null;
        if (!_vaultService.IsVaultConfigured)
        {
            ErrorMessage = LocalizationService.Instance["QuickCapture_Error_NoVault"];
            return;
        }

        try
        {
            var result = _captureService.Capture(Draft,
                SelectedDestination?.Value ?? QuickCaptureDestination.InboxNote,
                SelectedKanbanColumn?.Id);
            Draft = string.Empty;
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Notification_QuickCaptureSaved_Title"),
                LocalizedTextFormatter.Format(
                    result.Destination == QuickCaptureDestination.InboxNote
                        ? "Notification_QuickCaptureSaved_Note"
                        : "Notification_QuickCaptureSaved_Card",
                    result.Title), NotificationSeverity.Success);
            _onCaptured?.Invoke(result);
            CloseRequested?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultUnavailableException
                                       or SessionLockedException or InvalidOperationException)
        {
            if (ex is InvalidOperationException)
            {
                LogService.Warning("No se pudo guardar la captura rápida", ex);
                ErrorMessage = LocalizationService.Instance["QuickCapture_Error_NoKanbanColumn"];
            }
            else
            {
                var reason = LocalizedTextFormatter.Resolve(VaultFailureReasons.DescribeLocalized(ex));
                ErrorMessage = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    LocalizationService.Instance["QuickCapture_Error_SaveFailed"], reason);
            }
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}
