using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;
using enki.Services.Export;

namespace enki.ViewModels;

/// <summary>Calendario › "Exportar vista" a PNG y a PDF (T8 de
/// .docs/funcionalidades-pendientes-priorizadas.md §4.4) — en su propio archivo parcial, igual que
/// <see cref="KanbanViewModel"/> hace con lo mismo.
///
/// <para>Lo exportado es exactamente lo que está en pantalla: el rango (mes/semana/día/agenda), las capas
/// activas y los filtros aplicados. El documento de la funcionalidad proponía además un diálogo con rango
/// y capas configurables aparte; se descartó porque duplicaría en un formulario los controles que la
/// propia vista ya tiene, y "exporta lo que veo" es más predecible que "exporta lo que dije en otro
/// sitio".</para></summary>
public sealed partial class CalendarViewModel
{
    private static readonly string[] ExportRootNames = ["CalendarExportRoot"];

    [RelayCommand]
    private Task ExportViewAsPng() => ExportViewAsync(ExportFormat.Png);

    [RelayCommand]
    private Task ExportViewAsPdf() => ExportViewAsync(ExportFormat.Pdf);

    public Task ExportViewAsync(ExportFormat format)
    {
        var request = new ViewExportRequest(
            ExportRootNames,
            LocalizationService.Instance["Nav_Calendar"],
            PeriodLabel,
            ViewExportService.TimestampedFileName(LocalizationService.Instance["Nav_Calendar"]));

        return ViewExportService.ExportViewAsync(request, format, VisualSnapshotRenderer.ScaleHighResolution,
            App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null,
            (title, message, severity) => _notificationService.PushLocalized(title, message, severity));
    }

    /// <summary>Calendario › «Exportar a ICS» (MED-04): escribe en un archivo <c>.ics</c> lo pendiente de la
    /// agenda —tarjetas, tareas y recordatorios con fecha, con su repetición— para abrirlo en otro
    /// calendario. A diferencia de PNG y PDF no depende de la vista: exporta toda la agenda, no solo el
    /// rango que se ve. Es manual y local; no hay servidor ni suscripción.</summary>
    [RelayCommand]
    private async Task ExportIcs() => await ExportIcsAsync();

    /// <summary>Mismo selector de destino para la interfaz y el asistente; nunca acepta rutas del modelo.</summary>
    public async Task<bool> ExportIcsAsync(System.Threading.CancellationToken cancellation = default)
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return false;
        var loc = LocalizationService.Instance;

        var exporter = new CalendarIcsExportService(_aggregationService, _reminderService, _polarTaskService);
        var entries = exporter.CollectEntries();
        if (entries.Count == 0)
        {
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Export_IcsEmptyTitle"),
                LocalizedTextFormatter.Format("Export_IcsEmptyMessage"), NotificationSeverity.Info);
            return false;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = loc["Export_IcsDialogTitle"],
            SuggestedFileName = ViewExportService.TimestampedFileName(loc["Nav_Calendar"]) + ".ics",
            DefaultExtension = "ics",
            FileTypeChoices = [new FilePickerFileType(loc["Export_IcsFileType"]) { Patterns = ["*.ics"] }]
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return false;

        cancellation.ThrowIfCancellationRequested();
        try
        {
            File.WriteAllText(path, CalendarIcsExportService.Write(entries, TimeZoneInfo.Local, DateTime.UtcNow), new UTF8Encoding(false));
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Export_IcsSuccessTitle"),
                LocalizedTextFormatter.Format("Export_IcsSuccessMessage", entries.Count, Path.GetFileName(path)),
                NotificationSeverity.Success);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error("No se pudo exportar el calendario a ICS", ex);
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Export_IcsFailedTitle"),
                LocalizedTextFormatter.Format(ex is UnauthorizedAccessException ? "Common_Error_AccessDenied" : "Common_Error_IOFailure"),
                NotificationSeverity.Error);
            return false;
        }
    }
}
