using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using enki.Localization;
using enki.Models;

namespace enki.Services;

/// <summary>Exportación de notas a HTML autónomo (propuesta 6 de
/// docs/propuesta-nuevas-funcionalidades.md — solo la mitad de HTML de esa propuesta está implementada
/// hoy, no hay vista de impresión ni exportación a PDF propias): elige destino con el diálogo de guardado
/// del sistema, renderiza con <see cref="NoteExportRenderer"/> y escribe el archivo. Sin estado propio —
/// el render es puro y el picker es efímero — así que toda la clase es estática; los avisos de éxito/fallo
/// llegan al centro de notificaciones a través del callback <c>notify</c> que le pasa el llamador
/// (<see cref="ViewModels.NotesViewModel"/>, típicamente con <see cref="NotificationService.Push"/>).</summary>
public static class NoteExportService
{
    /// <summary>Flujo completo de "Exportar como HTML…". No-op silencioso si no hay
    /// <paramref name="topLevel"/> (tests, donde <c>App.MainWindowRef</c> es null — mismo criterio que
    /// los comandos de importar/exportar ajustes) o si el usuario cancela el diálogo.</summary>
    public static async Task ExportHtmlAsync(NoteFile note, string content, bool includeTags,
        TopLevel? topLevel, Action<LocalizedText, LocalizedText, NotificationSeverity>? notify)
    {
        if (topLevel?.StorageProvider is null) return;

        var loc = LocalizationService.Instance;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = loc["Export_HtmlDialog_Title"],
            SuggestedFileName = note.Title + ".html",
            DefaultExtension = "html",
            FileTypeChoices = [new FilePickerFileType(loc["Export_HtmlFileType"]) { Patterns = ["*.html"] }]
        });

        var destination = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(destination)) return;

        try
        {
            var html = NoteExportRenderer.RenderDocument(note, content, includeTags);
            await File.WriteAllTextAsync(destination, html);
            notify?.Invoke(
                LocalizedTextFormatter.Format("Notification_NoteExportSuccess_Title"),
                LocalizedTextFormatter.Format("Notification_NoteExportSuccess_Message", destination),
                NotificationSeverity.Success);
        }
        // Los errores visibles usan recursos; el diagnóstico técnico se conserva en el log.
        catch (UnauthorizedAccessException)
        {
            notify?.Invoke(LocalizedTextFormatter.Format("Notification_NoteExportFailed_Title"), LocalizedTextFormatter.Format("Common_Error_AccessDenied"), NotificationSeverity.Error);
        }
        catch (IOException)
        {
            notify?.Invoke(LocalizedTextFormatter.Format("Notification_NoteExportFailed_Title"), LocalizedTextFormatter.Format("Common_Error_IOFailure"), NotificationSeverity.Error);
        }
        catch (Exception ex)
        {
            LogService.Error($"No se pudo exportar la nota '{note.FullPath}' a HTML", ex);
            notify?.Invoke(LocalizedTextFormatter.Format("Notification_NoteExportFailed_Title"), VaultFailureReasons.DescribeLocalized(ex), NotificationSeverity.Error);
        }
    }
}
