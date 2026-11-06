using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Una fila de la lista de archivos del paquete de diagnóstico.</summary>
public sealed record DiagnosticsFileItem(DiagnosticsFile File)
{
    public string Name => File.Name;
    public string SizeLabel => FileSizeFormatter.Format(File.SizeBytes);
}

/// <summary>Vista previa del paquete de diagnóstico (MED-08): lo que se va a guardar, archivo por archivo,
/// antes de que se escriba nada en disco. Los textos localizados se resuelven al crearla —el diálogo es
/// modal, el idioma no cambia mientras está abierto— y no en propiedades calculadas.</summary>
public sealed partial class DiagnosticsPreviewViewModel : ObservableObject
{
    public DiagnosticsPackage Package { get; }
    public IReadOnlyList<DiagnosticsFileItem> Files { get; }

    /// <summary>«4 archivos · 3,2 KB».</summary>
    public string SummaryLabel { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedContent))]
    private DiagnosticsFileItem? _selectedFile;

    public string SelectedContent => SelectedFile?.File.Content ?? string.Empty;

    public DiagnosticsPreviewViewModel(DiagnosticsPackage package)
    {
        Package = package;
        Files = package.Files.Select(f => new DiagnosticsFileItem(f)).ToList();
        SelectedFile = Files.Count > 0 ? Files[0] : null;
        SummaryLabel = string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Settings_Diagnostics_Preview_Summary"],
            Files.Count, FileSizeFormatter.Format(package.TotalBytes));
    }
}

/// <summary>Ajustes › Mantenimiento › Diagnóstico: exportar un paquete para reportar fallos (MED-08). Se
/// genera a mano, se enseña entero y solo se escribe si el usuario lo confirma.</summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDiagnosticsPreviewOpen))]
    private DiagnosticsPreviewViewModel? _diagnosticsPreview;

    [ObservableProperty]
    private bool _isBuildingDiagnostics;

    public bool IsDiagnosticsPreviewOpen => DiagnosticsPreview is not null;

    /// <summary>Genera el paquete en segundo plano (lee y depura los registros) y abre la vista previa.</summary>
    [RelayCommand]
    private async Task PrepareDiagnostics()
    {
        if (IsBuildingDiagnostics) return;
        IsBuildingDiagnostics = true;
        try
        {
            var settings = _settingsService.Settings;
            var package = await Task.Run(() => new DiagnosticsPackageService(settings, LogService.LogDirectory).Build());
            DiagnosticsPreview = new DiagnosticsPreviewViewModel(package);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogService.Error("No se pudo generar el paquete de diagnóstico", ex);
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Settings_Diagnostics_FailedTitle"),
                LocalizedTextFormatter.Format("Settings_Diagnostics_BuildFailedMessage"), NotificationSeverity.Error);
        }
        finally { IsBuildingDiagnostics = false; }
    }

    [RelayCommand]
    private void CancelDiagnostics() => DiagnosticsPreview = null;

    /// <summary>Pide el destino y escribe el <c>.zip</c> con exactamente lo que se ha mostrado.</summary>
    [RelayCommand]
    private async Task SaveDiagnostics()
    {
        if (DiagnosticsPreview is not { } preview) return;
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var loc = LocalizationService.Instance;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = loc["Settings_Diagnostics_SaveDialogTitle"],
            SuggestedFileName = $"Enki-Diagnostico-{DateTime.Now:yyyy-MM-dd-HHmm}.zip",
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType(loc["Settings_General_DangerZone_ZipFileType"]) { Patterns = ["*.zip"] }]
        });
        var destination = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(destination)) return;

        try
        {
            preview.Package.WriteZip(destination);
            DiagnosticsPreview = null;
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Settings_Diagnostics_SavedTitle"),
                LocalizedTextFormatter.Format("Settings_Diagnostics_SavedMessage", Path.GetFileName(destination)),
                NotificationSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error("No se pudo guardar el paquete de diagnóstico", ex);
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Settings_Diagnostics_FailedTitle"),
                LocalizedTextFormatter.Format(ex is UnauthorizedAccessException ? "Common_Error_AccessDenied" : "Common_Error_IOFailure"),
                NotificationSeverity.Error);
        }
    }
}
