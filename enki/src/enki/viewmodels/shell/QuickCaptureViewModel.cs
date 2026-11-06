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

/// <summary>Estado duradero de la captura rápida, compartido por sus dos superficies: la mini-ventana
/// global (<c>QuickCaptureWindow</c>, atajo del sistema) y el popup del botón de bandeja de entrada de la
/// barra de título (<c>MainWindow.axaml</c>). Ninguna de las dos es dueña del borrador — esta instancia
/// permanece viva en la composición del shell, así que Esc lo conserva y lo escrito en una puerta sigue
/// ahí al abrir la otra.</summary>
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
    public ObservableCollection<KanbanBoard> KanbanBoards { get; } = [];
    [ObservableProperty] private KanbanBoard? _selectedKanbanBoard;
    public bool ShowKanbanBoards => IsKanbanDestination && KanbanBoards.Count > 1;
    private bool _reloadingDestinations;
    partial void OnSelectedKanbanBoardChanged(KanbanBoard? value)
    {
        if (_reloadingDestinations) return;
        LoadKanbanColumns(null);
    }
    private void LoadKanbanColumns(string? preferredId)
    {
        KanbanColumns.Clear();
        if (SelectedKanbanBoard is not { } board) { SelectedKanbanColumn = null; return; }
        foreach (var column in board.Columns) KanbanColumns.Add(new(column.Id, column.Title));
        SelectedKanbanColumn = KanbanColumns.FirstOrDefault(c => c.Id == preferredId)
            ?? KanbanColumns.FirstOrDefault(c => board.Columns.Any(column => column.Id == c.Id && column.Role == CoreColumnRole.Backlog))
            ?? KanbanColumns.FirstOrDefault();
    }
    public void CloseForVaultChange()
    {
        Draft = string.Empty;
        IsOpen = false;
        SelectedDestination = null;
        SelectedKanbanColumn = null;
        _reloadingDestinations = true;
        SelectedKanbanBoard = null;
        _reloadingDestinations = false;
    }

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

    /// <summary>Estado del popup de la bandeja de entrada de la barra de título (MainWindow.axaml). Vive
    /// aquí, junto al borrador, y no en un viewmodel aparte: el popup y la mini-ventana global son dos
    /// puertas a la misma captura, así que lo que se escriba en una sigue ahí al abrir la otra.</summary>
    [ObservableProperty]
    private bool _isOpen;

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
    /// existiendo. Una preferencia obsoleta presenta el predeterminado antes del siguiente envío.</summary>
    public void ReloadDestinations()
    {
        var preferredId = SelectedKanbanColumn?.Id ?? _vaultSettingsService.Settings.QuickCaptureKanbanColumnId;
        var boardId = SelectedKanbanBoard?.Id ?? _vaultSettingsService.Settings.QuickCaptureKanbanBoardId;
        _reloadingDestinations = true;
        KanbanBoards.Clear();
        foreach (var board in _kanbanService.Boards) KanbanBoards.Add(board);
        SelectedKanbanBoard = KanbanBoards.FirstOrDefault(b => b.Id == boardId)
            ?? KanbanBoards.FirstOrDefault(b => b.Columns.Any(c => c.Id == preferredId)) ?? _kanbanService.DefaultBoard;
        _reloadingDestinations = false;
        LoadKanbanColumns(preferredId);
        OnPropertyChanged(nameof(ShowKanbanBoards));
    }

    partial void OnSelectedDestinationChanged(QuickCaptureDestinationOption? value)
    {
        OnPropertyChanged(nameof(ShowKanbanBoards));
        if (value is null || !_vaultService.IsVaultConfigured) return;
        _vaultSettingsService.Settings.QuickCaptureDestination = value.Value;
        _vaultSettingsService.Save();
    }

    partial void OnSelectedKanbanColumnChanged(QuickCaptureColumnOption? value)
    {
        if (value is null || !_vaultService.IsVaultConfigured) return;
        _vaultSettingsService.Settings.QuickCaptureKanbanColumnId = value.Id;
        _vaultSettingsService.Settings.QuickCaptureKanbanBoardId = _kanbanService.Boards.First(b => b.Columns.Any(c => c.Id == value.Id)).Id;
        _vaultSettingsService.Save();
    }

    /// <summary>Abre/cierra el popup de la barra de título. Al abrir repite la misma preparación que la
    /// mini-ventana (<see cref="PrepareToShow"/>), porque el vault o las columnas del tablero pueden haber
    /// cambiado desde la última vez.</summary>
    [RelayCommand]
    private void Toggle()
    {
        if (!IsOpen) PrepareToShow();
        IsOpen = !IsOpen;
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
            if (IsKanbanDestination && (SelectedKanbanColumn is null || SelectedKanbanBoard is null ||
                !_kanbanService.Boards.Any(b => b.Id == SelectedKanbanBoard.Id && b.Columns.Any(c => c.Id == SelectedKanbanColumn.Id))))
                throw new InvalidOperationException("El destino ha cambiado.");
            var result = _captureService.Capture(Draft,
                SelectedDestination?.Value ?? QuickCaptureDestination.InboxNote,
                SelectedKanbanColumn?.Id,
                SelectedKanbanBoard?.Id);
            Draft = string.Empty;
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Notification_QuickCaptureSaved_Title"),
                LocalizedTextFormatter.Format(
                    result.Destination == QuickCaptureDestination.InboxNote
                        ? "Notification_QuickCaptureSaved_Note"
                        : "Notification_QuickCaptureSaved_Card",
                    result.Title), NotificationSeverity.Success);
            _onCaptured?.Invoke(result);
            IsOpen = false;
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
    private void Cancel()
    {
        IsOpen = false;
        CloseRequested?.Invoke();
    }
}
