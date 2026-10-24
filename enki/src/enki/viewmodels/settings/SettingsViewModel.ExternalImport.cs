using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    private bool _isExternalImporting;

    private readonly ReminderService? _reminderImportService;
    private readonly Action? _onExternalImportCompleted;

    [RelayCommand]
    private Task ImportObsidian() => ImportExternal(ExternalImportKind.Obsidian);

    [RelayCommand]
    private Task ImportNotion() => ImportExternal(ExternalImportKind.Notion);

    [RelayCommand]
    private Task ImportTodoist() => ImportExternal(ExternalImportKind.Todoist);

    [RelayCommand]
    private Task ImportGoogleCalendar() => ImportExternal(ExternalImportKind.GoogleCalendar);

    private enum ExternalImportKind { Obsidian, Notion, Todoist, GoogleCalendar }

    private async Task ImportExternal(ExternalImportKind kind)
    {
        if (IsExternalImporting || _kanbanService is null || _reminderImportService is null) return;
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;
        string? source;
        var loc = LocalizationService.Instance;
        if (kind == ExternalImportKind.Obsidian)
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = loc["ExternalImport_Obsidian"], AllowMultiple = false
            });
            source = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        else
        {
            var extension = kind switch { ExternalImportKind.Notion => "zip", ExternalImportKind.Todoist => "csv", _ => "ics" };
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = kind switch
                {
                    ExternalImportKind.Notion => loc["ExternalImport_Notion"],
                    ExternalImportKind.Todoist => loc["ExternalImport_Todoist"],
                    _ => loc["ExternalImport_GoogleCalendar"]
                },
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = ["*." + extension] }]
            });
            source = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
        if (string.IsNullOrWhiteSpace(source)) return;
        IsExternalImporting = true;
        try
        {
            var importer = new ExternalImportService(_vaultService, _kanbanService, _reminderImportService);
            var plan = await Task.Run(() => kind switch
            {
                ExternalImportKind.Obsidian => ExternalImportService.ReadObsidian(source),
                ExternalImportKind.Notion => ExternalImportService.ReadNotion(source),
                ExternalImportKind.Todoist => ExternalImportService.ReadTodoist(source),
                _ => ExternalImportService.ReadGoogleCalendar(source)
            });
            if (plan.Notes.Count + plan.Tasks.Count + plan.Events.Count + plan.Assets.Count == 0)
            {
                _notificationService.PushLocalized(LocalizedTextFormatter.Format("ExternalImport_EmptyTitle"),
                    LocalizedTextFormatter.Format("ExternalImport_EmptyMessage"), NotificationSeverity.Warning);
                return;
            }
            var result = importer.Apply(plan);
            _onExternalImportCompleted?.Invoke();
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("ExternalImport_SuccessTitle"),
                LocalizedTextFormatter.Format("ExternalImport_SuccessMessage", result.Notes, result.Tasks, result.Events, result.Assets),
                NotificationSeverity.Success);
            if (plan.RecurringSeries > 0)
                _notificationService.PushLocalized(LocalizedTextFormatter.Format("ExternalImport_RecurringTitle"),
                    LocalizedTextFormatter.Format("ExternalImport_RecurringMessage", plan.RecurringSeries),
                    NotificationSeverity.Warning);
        }
        catch (ExternalImportService.ImportInterruptedException ex)
        {
            LogService.Error("La importación externa se interrumpió después de escribir parte de los datos", ex);
            _onExternalImportCompleted?.Invoke();
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("ExternalImport_ErrorTitle"),
                LocalizedTextFormatter.Format("ExternalImport_PartialMessage", ex.Completed.Notes,
                    ex.Completed.Tasks, ex.Completed.Events, ex.Completed.Assets), NotificationSeverity.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            LogService.Error("No se pudo importar la exportación externa", ex);
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("ExternalImport_ErrorTitle"),
                LocalizedTextFormatter.Format("ExternalImport_ErrorMessage"), NotificationSeverity.Error);
        }
        finally { IsExternalImporting = false; }
    }
}
