using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class SettingsViewModel
{
    private readonly ExternalCalendarService? _externalCalendarService;
    public ObservableCollection<ExternalCalendarSubscription> ExternalCalendars { get; } = [];

    [ObservableProperty] private string _externalCalendarName = string.Empty;
    [ObservableProperty] private string _externalCalendarUrl = string.Empty;
    [ObservableProperty] private string _externalCalendarMessage = string.Empty;
    [ObservableProperty] private bool _isExternalCalendarBusy;
    [ObservableProperty] private ExternalCalendarSubscription? _pendingExternalCalendarRemoval;

    private void InitializeExternalCalendars()
    {
        if (_externalCalendarService is null) return;
        ReloadExternalCalendars();
        _externalCalendarService.DataChanged += ReloadExternalCalendars;
    }

    private void ReloadExternalCalendars()
    {
        ExternalCalendars.Clear();
        if (_externalCalendarService is null) return;
        foreach (var feed in _externalCalendarService.Subscriptions) ExternalCalendars.Add(feed);
    }

    [RelayCommand]
    private async Task AddExternalCalendar()
    {
        if (_externalCalendarService is null || IsExternalCalendarBusy) return;
        ExternalCalendarMessage = string.Empty;
        try
        {
            var feed = _externalCalendarService.Add(ExternalCalendarName, ExternalCalendarUrl);
            ExternalCalendarName = string.Empty;
            ExternalCalendarUrl = string.Empty;
            await RefreshExternalCalendar(feed);
        }
        catch (ArgumentException) { ExternalCalendarMessage = LocalizationService.Instance["Calendar_External_Invalid"]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            LogService.Warning("No se pudo guardar una suscripción iCalendar", ex);
            ExternalCalendarMessage = LocalizationService.Instance["Calendar_External_SaveFailed"];
            _notificationService.Push(LocalizationService.Instance["Calendar_External_Source"],
                ExternalCalendarMessage, NotificationSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task RefreshExternalCalendar(ExternalCalendarSubscription feed)
    {
        if (_externalCalendarService is null || IsExternalCalendarBusy) return;
        IsExternalCalendarBusy = true;
        ExternalCalendarMessage = string.Empty;
        try
        {
            await _externalCalendarService.RefreshAsync(feed.Id);
            ExternalCalendarMessage = LocalizationService.Instance["Calendar_External_Updated"];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or ArgumentException or InvalidOperationException or FormatException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            LogService.Warning($"No se pudo actualizar el calendario externo '{feed.Name}' ({ex.GetType().Name})");
            ExternalCalendarMessage = LocalizationService.Instance["Calendar_External_Failed"];
            _notificationService.Push(LocalizationService.Instance["Calendar_External_Source"], ExternalCalendarMessage, NotificationSeverity.Warning);
        }
        finally { IsExternalCalendarBusy = false; }
    }

    [RelayCommand]
    private void RemoveExternalCalendar(ExternalCalendarSubscription feed) => PendingExternalCalendarRemoval = feed;

    [RelayCommand]
    private void CancelExternalCalendarRemoval() => PendingExternalCalendarRemoval = null;

    [RelayCommand]
    private void ConfirmExternalCalendarRemoval()
    {
        if (PendingExternalCalendarRemoval is not { } feed) return;
        try { _externalCalendarService?.Remove(feed.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            LogService.Warning("No se pudo eliminar una suscripción iCalendar", ex);
            ExternalCalendarMessage = LocalizationService.Instance["Calendar_External_SaveFailed"];
            _notificationService.Push(LocalizationService.Instance["Calendar_External_Source"],
                ExternalCalendarMessage, NotificationSeverity.Error);
            return;
        }
        PendingExternalCalendarRemoval = null;
        ExternalCalendarMessage = LocalizationService.Instance["Calendar_External_Removed"];
        _notificationService.Push(LocalizationService.Instance["Calendar_External_Source"],
            ExternalCalendarMessage, NotificationSeverity.Info);
    }
}
