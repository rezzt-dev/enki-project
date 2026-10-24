using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Zona de peligro (Ajustes › General, docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md):
/// ruta y tamaño del vault, backup automático/manual, exportar/importar <c>settings.json</c> y restablecer
/// ajustes por defecto — split out of <c>SettingsViewModel.cs</c> to keep every "irreversible or
/// disk-affecting" action together. Still the very same <see cref="SettingsViewModel"/> class — partial
/// types share every field/property regardless of which file declares them — so no behavior change.</summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    private string _vaultPath;

    /// <summary>Tamaño total en disco del vault activo, ya formateado ("128 MB") — reutiliza
    /// <see cref="VaultService.GetVaultSizeBytes"/> (ya usado por el Dashboard), solo que aquí se muestra
    /// junto a la ruta del vault en la "zona de peligro" (docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md
    /// §2). Se recalcula al construir y cada vez que cambia de carpeta.</summary>
    [ObservableProperty]
    private string _vaultSizeLabel = string.Empty;

    /// <summary>La guía de sincronización se despliega junto a la ruta del almacén para poder
    /// consultarla sin salir de Ajustes ni depender de una página externa.</summary>
    [ObservableProperty]
    private bool _privateSyncGuideOpen;

    [RelayCommand]
    private void TogglePrivateSyncGuide() => PrivateSyncGuideOpen = !PrivateSyncGuideOpen;

    /// <summary>True mientras el diálogo de confirmación de "Restablecer ajustes a valores por defecto"
    /// está abierto — mismo patrón request/cancel/confirm que <c>TrashViewModel.PendingEmptyTarget</c>.</summary>
    [ObservableProperty]
    private bool _pendingResetSettings;

    [ObservableProperty]
    private bool _pendingRestoreBackup;

    [ObservableProperty]
    private string _pendingRestoreDestination = string.Empty;

    private string? _pendingRestoreSource;

    /// <summary>"Backup automático" (Ajustes › General, zona de peligro, Fase 1 de
    /// docs/analisis-proyecto/roadmap.md) — complemento opcional al backup manual: crea uno nuevo cada
    /// <see cref="AppSettings.AutoBackupIntervalDays"/> días y conserva los últimos
    /// <see cref="AppSettings.AutoBackupRetentionCount"/> en <c>%AppData%/Enki/backups/</c>. Apagado por
    /// defecto.</summary>
    [ObservableProperty]
    private bool _autoBackupEnabled;

    /// <summary>Cada cuántos días se crea un backup automático (Fase 10, docs/analisis-proyecto/roadmap.md
    /// — antes solo el interruptor de <see cref="AutoBackupEnabled"/> tenía control en la UI, el intervalo
    /// y la retención quedaban fijos en su valor por defecto para todo el mundo). El servicio
    /// (<c>VaultBackupService.RunScheduledBackupIfDue</c>) ya acota por debajo a 1 día
    /// independientemente de este campo; el máximo de 30 del selector de la UI es solo para mantener el
    /// control usable, no una validación adicional.</summary>
    [ObservableProperty]
    private int _autoBackupIntervalDays;

    /// <summary>Cuántos backups automáticos se conservan antes de rotar los más antiguos (Fase 10, ver
    /// <see cref="AutoBackupIntervalDays"/>). Mismo criterio de acotado por la UI, no por el servicio.</summary>
    [ObservableProperty]
    private int _autoBackupRetentionCount;

    partial void OnAutoBackupEnabledChanged(bool value)
    {
        _settingsService.Settings.AutoBackupEnabled = value;
        _settingsService.Save();
    }

    partial void OnAutoBackupIntervalDaysChanged(int value)
    {
        _settingsService.Settings.AutoBackupIntervalDays = value;
        _settingsService.Save();
    }

    partial void OnAutoBackupRetentionCountChanged(int value)
    {
        _settingsService.Settings.AutoBackupRetentionCount = value;
        _settingsService.Save();
    }

    [RelayCommand]
    private async Task ChangeVaultFolder()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Instance["Settings_General_ChangeVaultFolder_DialogTitle"],
            AllowMultiple = false
        });

        var pickedFolder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(pickedFolder)) return;

        // Same "picked folder is a container, not the vault" resolution as VaultSetupViewModel — see
        // VaultService.ResolveVaultFolder.
        var folder = VaultService.ResolveVaultFolder(pickedFolder);

        // Desde T4 el cambio de vault (validar la ruta, volcar lo pendiente y cancelar si algo quedó sin
        // guardar, bloquear sesiones, pausar la sincronización y recargar) lo hace el shell por un único
        // camino, compartido con el selector del sidebar y la paleta de comandos. Aquí solo queda
        // refrescar lo que esta pantalla muestra del vault, y solo si el cambio se llegó a producir.
        if (!await _switchVault(folder, null)) return;

        VaultPath = folder;
        RefreshVaultSizeLabel();
    }

    // ===================== Zona de peligro (Ajustes › General) =====================
    // docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md — tamaño del vault, abrir su carpeta,
    // copia de seguridad y restablecer ajustes por defecto.

    private void RefreshVaultSizeLabel() => VaultSizeLabel = FileSizeFormatter.Format(_vaultService.GetVaultSizeBytes());

    [RelayCommand]
    private void OpenVaultFolder() => FolderOpener.Open(_vaultService.VaultPath);

    /// <summary>Empaqueta el vault activo + los ficheros de estado de <c>%AppData%/Enki</c> en un único
    /// .zip elegido por el usuario (mismo patrón <c>StorageProvider</c> que <see cref="ChangeVaultFolder"/>
    /// para llegar a un <see cref="TopLevel"/> desde este viewmodel). Notifica éxito o fallo por
    /// <see cref="NotificationService"/> en vez de fallar en silencio.</summary>
    [RelayCommand]
    private async Task CreateBackup()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var suggestedName = $"Enki-Backup-{DateTime.Now:yyyy-MM-dd-HHmm}.zip";
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LocalizationService.Instance["Settings_General_DangerZone_BackupDialogTitle"],
            SuggestedFileName = suggestedName,
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType(LocalizationService.Instance["Settings_General_DangerZone_ZipFileType"]) { Patterns = ["*.zip"] }]
        });

        var destination = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(destination)) return;

        try
        {
            var result = _vaultBackupService.CreateBackup(destination);
            if (result.IsComplete)
            {
                _notificationService.PushLocalized(
                    LocalizedTextFormatter.Format("Settings_General_DangerZone_BackupCreatedTitle"),
                    LocalizedTextFormatter.Format("Settings_General_DangerZone_BackupCreatedMessage", destination),
                    Models.NotificationSeverity.Success);
            }
            else
            {
                // Fase 1 (docs/analisis-proyecto/roadmap.md): antes un fichero bloqueado/ilegible se omitía
                // del .zip en silencio — el backup "tenía éxito" mostrando el mismo mensaje que uno completo.
                _notificationService.PushLocalized(
                    LocalizedTextFormatter.Format("Settings_General_DangerZone_BackupIncompleteTitle"),
                    LocalizedTextFormatter.PluralFormat("Settings_General_DangerZone_BackupIncompleteMessage", result.FailedEntries.Count, result.FailedEntries.Count, destination),
                    Models.NotificationSeverity.Warning);
            }
        }
        // Specific, expected failure types get a fully-translated message (docs/agent-docs/
        // internacionalizacion-i18n.md §5.3) instead of leaking Exception.Message, which can come through
        // in the OS's own language rather than Enki's active one.
        catch (UnauthorizedAccessException)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Settings_General_DangerZone_BackupFailedTitle"),
                LocalizedTextFormatter.Format("Common_Error_AccessDenied"),
                Models.NotificationSeverity.Error);
        }
        catch (IOException)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Settings_General_DangerZone_BackupFailedTitle"),
                LocalizedTextFormatter.Format("Common_Error_IOFailure"),
                Models.NotificationSeverity.Error);
        }
        catch (Exception ex)
        {
            // El detalle técnico se registra; el aviso usa una razón traducible.
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Settings_General_DangerZone_BackupFailedTitle"),
                VaultFailureReasons.DescribeLocalized(ex),
                Models.NotificationSeverity.Error);
        }
    }

    /// <summary>Elige un ZIP y una carpeta contenedora, pero no toca el disco hasta que el usuario
    /// confirma el aviso. La restauración siempre crea un vault hermano nuevo: nunca extrae sobre el
    /// activo ni sobre una carpeta existente.</summary>
    [RelayCommand]
    private async Task RequestRestoreBackup()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance["Settings_General_DangerZone_RestoreDialogTitle"],
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(LocalizationService.Instance["Settings_General_DangerZone_ZipFileType"]) { Patterns = ["*.zip"] }]
        });
        var source = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(source)) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Instance["Settings_General_DangerZone_RestoreFolderDialogTitle"],
            AllowMultiple = false
        });
        var parent = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(parent)) return;

        _pendingRestoreSource = source;
        PendingRestoreDestination = UniqueRestorePath(parent);
        PendingRestoreBackup = true;
    }

    [RelayCommand]
    private void CancelRestoreBackup()
    {
        PendingRestoreBackup = false;
        _pendingRestoreSource = null;
        PendingRestoreDestination = string.Empty;
    }

    [RelayCommand]
    private async Task ConfirmRestoreBackup()
    {
        var source = _pendingRestoreSource;
        var destination = PendingRestoreDestination;
        CancelRestoreBackup();
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination)) return;

        try
        {
            // La extracción no toca el vault activo (escribe en una carpeta nueva, ver
            // VaultBackupService.RestoreBackup), así que se hace antes de pedir el cambio: si el .zip
            // resulta no ser válido, el vault abierto no se ha movido de sitio.
            var restored = _vaultBackupService.RestoreBackup(source, destination);
            // Las preferencias que venían en la copia se importan en el hueco entre adoptar la ruta nueva
            // y recargar las pantallas, que es donde tienen que aplicarse: ya sobre el vault restaurado,
            // pero todavía antes de que nadie las lea.
            var imported = await _switchVault(restored.VaultPath, () =>
            {
                if (!string.IsNullOrWhiteSpace(restored.SettingsJson))
                    _settingsService.ImportPreferences(restored.SettingsJson);
            });
            if (!imported) return;

            VaultPath = restored.VaultPath;
            RefreshVaultSizeLabel();

            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Settings_General_DangerZone_RestoreCompletedTitle"),
                LocalizedTextFormatter.Format("Settings_General_DangerZone_RestoreCompletedMessage", destination),
                Models.NotificationSeverity.Success);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            LogService.Warning($"No se pudo validar la copia de seguridad '{source}'", ex);
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Settings_General_DangerZone_RestoreFailedTitle"),
                LocalizedTextFormatter.Format("Settings_General_DangerZone_RestoreInvalidMessage"),
                Models.NotificationSeverity.Error);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogService.Warning($"No se pudo restaurar la copia de seguridad '{source}'", ex);
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Settings_General_DangerZone_RestoreFailedTitle"),
                LocalizedTextFormatter.Format("Common_Error_AccessDenied"),
                Models.NotificationSeverity.Error);
        }
        catch (IOException ex)
        {
            LogService.Warning($"No se pudo restaurar la copia de seguridad '{source}'", ex);
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Settings_General_DangerZone_RestoreFailedTitle"),
                LocalizedTextFormatter.Format("Common_Error_IOFailure"),
                Models.NotificationSeverity.Error);
        }
    }

    private static string UniqueRestorePath(string parent)
    {
        var baseName = $"Enki-Restored-{DateTime.Now:yyyy-MM-dd-HHmmss}";
        var candidate = Path.Combine(parent, baseName);
        var suffix = 2;
        while (Directory.Exists(candidate) || File.Exists(candidate))
            candidate = Path.Combine(parent, $"{baseName}-{suffix++}");
        return candidate;
    }

    /// <summary>Exporta <c>settings.json</c> a una ubicación elegida por el usuario — sin lógica de merge,
    /// útil para llevarse la configuración de un equipo a otro
    /// (docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md §1). A diferencia de una copia literal del
    /// fichero, el vínculo de sesión con Polar (<c>PolarAccessToken</c>/<c>PolarRefreshToken</c>) se redacta
    /// antes de escribir el fichero exportado — ver <see cref="SettingsService.BuildRedactedExportJson"/> —
    /// para que "Exportar ajustes" no se convierta en un vector de robo de sesión
    /// (docs/analisis-proyecto/roadmap.md, Fase 4). Reimportar el fichero exportado no restaura el vínculo
    /// con Polar; el usuario tiene que volver a iniciar sesión desde Ajustes › Cuenta.</summary>
    [RelayCommand]
    private async Task ExportSettings()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LocalizationService.Instance["Settings_General_DangerZone_ExportDialogTitle"],
            SuggestedFileName = "enki-settings.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType(LocalizationService.Instance["Settings_General_DangerZone_JsonFileType"]) { Patterns = ["*.json"] }]
        });

        var destination = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(destination)) return;

        try
        {
            _settingsService.Save();
            System.IO.File.WriteAllText(destination, _settingsService.BuildRedactedExportJson());
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Settings_General_DangerZone_ExportedTitle"),
                LocalizedTextFormatter.Format("Settings_General_DangerZone_ExportedMessage", destination),
                Models.NotificationSeverity.Success);
        }
        catch (UnauthorizedAccessException)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Settings_General_DangerZone_ExportFailedTitle"),
                LocalizedTextFormatter.Format("Common_Error_AccessDenied"),
                Models.NotificationSeverity.Error);
        }
        catch (IOException)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Settings_General_DangerZone_ExportFailedTitle"),
                LocalizedTextFormatter.Format("Common_Error_IOFailure"),
                Models.NotificationSeverity.Error);
        }
        catch (Exception ex)
        {
            // Mantener el diagnóstico técnico fuera del mensaje visible.
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Settings_General_DangerZone_ExportFailedTitle"),
                VaultFailureReasons.DescribeLocalized(ex),
                Models.NotificationSeverity.Error);
        }
    }

    /// <summary>Reemplaza <c>settings.json</c> por el fichero elegido, sin fusionar — mismo criterio simple
    /// que <see cref="ExportSettings"/>. Requiere reiniciar Enki para que se apliquen, igual que
    /// <see cref="ConfirmResetSettings"/>.</summary>
    [RelayCommand]
    private async Task ImportSettings()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance["Settings_General_DangerZone_ImportDialogTitle"],
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(LocalizationService.Instance["Settings_General_DangerZone_JsonFileType"]) { Patterns = ["*.json"] }]
        });

        var source = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(source)) return;

        try
        {
            _settingsService.ImportPreferences(System.IO.File.ReadAllText(source));
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Settings_General_DangerZone_ImportedTitle"),
                LocalizedTextFormatter.Format("Settings_General_DangerZone_ImportedMessage"),
                Models.NotificationSeverity.Success);
        }
        catch (UnauthorizedAccessException)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Settings_General_DangerZone_ImportFailedTitle"),
                LocalizedTextFormatter.Format("Common_Error_AccessDenied"),
                Models.NotificationSeverity.Error);
        }
        catch (IOException)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Settings_General_DangerZone_ImportFailedTitle"),
                LocalizedTextFormatter.Format("Common_Error_IOFailure"),
                Models.NotificationSeverity.Error);
        }
        catch (Exception ex)
        {
            // Mantener el diagnóstico técnico fuera del mensaje visible.
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Settings_General_DangerZone_ImportFailedTitle"),
                VaultFailureReasons.DescribeLocalized(ex),
                Models.NotificationSeverity.Error);
        }
    }

    [RelayCommand]
    private void RequestResetSettings() => PendingResetSettings = true;

    [RelayCommand]
    private void CancelResetSettings() => PendingResetSettings = false;

    /// <summary>Escribe <see cref="AppSettings"/> por defecto a disco (conservando la ruta del vault) y
    /// avisa que hace falta reiniciar Enki — no hay hoy ningún mecanismo de recarga en caliente para las
    /// ~50 propiedades ya cargadas en este viewmodel, así que refrescarlas en memoria una a una sería
    /// complejidad innecesaria para una acción rara.</summary>
    [RelayCommand]
    private void ConfirmResetSettings()
    {
        _settingsService.ResetToDefaults();
        PendingResetSettings = false;
        _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Settings_General_DangerZone_ResetDoneTitle"),
            LocalizedTextFormatter.Format("Settings_General_DangerZone_ResetDoneMessage"),
            Models.NotificationSeverity.Success);
    }
}
