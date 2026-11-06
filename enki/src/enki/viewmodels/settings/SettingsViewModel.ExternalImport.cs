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

    [RelayCommand]
    private Task ImportLogseq() => ImportExternal(ExternalImportKind.Logseq);

    [RelayCommand]
    private Task ImportEvernote() => ImportExternal(ExternalImportKind.Evernote);

    [RelayCommand]
    private Task ImportKeep() => ImportExternal(ExternalImportKind.Keep);

    [RelayCommand]
    private Task ImportTickTick() => ImportExternal(ExternalImportKind.TickTick);

    private enum ExternalImportKind { Obsidian, Notion, Todoist, GoogleCalendar, Logseq, Evernote, Keep, TickTick }

    private async Task ImportExternal(ExternalImportKind kind)
    {
        if (IsExternalImporting || _kanbanService is null || _reminderImportService is null) return;
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;
        string? source;
        var loc = LocalizationService.Instance;
        if (kind is ExternalImportKind.Obsidian or ExternalImportKind.Logseq)
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = kind == ExternalImportKind.Logseq ? loc["ExternalImport_Logseq"] : loc["ExternalImport_Obsidian"],
                AllowMultiple = false
            });
            source = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        else
        {
            var extension = kind switch
            {
                ExternalImportKind.Notion or ExternalImportKind.Keep => "zip",
                ExternalImportKind.Todoist or ExternalImportKind.TickTick => "csv",
                ExternalImportKind.Evernote => "enex",
                _ => "ics"
            };
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = kind switch
                {
                    ExternalImportKind.Notion => loc["ExternalImport_Notion"],
                    ExternalImportKind.Todoist => loc["ExternalImport_Todoist"],
                    ExternalImportKind.Evernote => loc["ExternalImport_Evernote"],
                    ExternalImportKind.Keep => loc["ExternalImport_Keep"],
                    ExternalImportKind.TickTick => loc["ExternalImport_TickTick"],
                    _ => loc["ExternalImport_GoogleCalendar"]
                },
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = ["*." + extension] }]
            });
            source = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
        if (string.IsNullOrWhiteSpace(source)) return;
        IsExternalImporting = true;
        ExternalImportService.Plan? plan = null;
        try
        {
            var importer = new ExternalImportService(_vaultService, _kanbanService, _reminderImportService);
            plan = await Task.Run(() => kind switch
            {
                ExternalImportKind.Obsidian => ExternalImportService.ReadObsidian(source),
                ExternalImportKind.Notion => ExternalImportService.ReadNotion(source),
                ExternalImportKind.Todoist => ExternalImportService.ReadTodoist(source),
                ExternalImportKind.Logseq => ExternalImportService.ReadLogseq(source),
                ExternalImportKind.Evernote => ExternalImportService.ReadEvernote(source),
                ExternalImportKind.Keep => ExternalImportService.ReadGoogleKeep(source),
                ExternalImportKind.TickTick => ExternalImportService.ReadTickTick(source),
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
            AnnounceImportReport(importer, plan, result, interrupted: false);
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
            if (plan is not null)
                AnnounceImportReport(new ExternalImportService(_vaultService, _kanbanService, _reminderImportService), plan, ex.Completed, interrupted: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            LogService.Error("No se pudo importar la exportación externa", ex);
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("ExternalImport_ErrorTitle"),
                LocalizedTextFormatter.Format("ExternalImport_ErrorMessage"), NotificationSeverity.Error);
        }
        finally { IsExternalImporting = false; }
    }

    /// <summary>Guarda el informe de la importación como nota en la carpeta importada y avisa de cuántos
    /// elementos se omitieron. Un fallo al escribir el informe no deshace una importación que ya terminó.</summary>
    private void AnnounceImportReport(ExternalImportService importer, ExternalImportService.Plan plan,
        ExternalImportService.Result result, bool interrupted)
    {
        try
        {
            if (importer.WriteReport(plan, result, interrupted) is not { } reportPath) return;
            _onExternalImportCompleted?.Invoke();
            var skipped = plan.Issues.Count + plan.IssuesOmitted;
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("ExternalImport_ReportTitle"),
                LocalizedTextFormatter.Format("ExternalImport_ReportMessage", skipped,
                    Path.GetFileName(Path.GetDirectoryName(reportPath))),
                skipped > 0 || interrupted ? NotificationSeverity.Warning : NotificationSeverity.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogService.Error("No se pudo guardar el informe de la importación", ex);
        }
    }
}
