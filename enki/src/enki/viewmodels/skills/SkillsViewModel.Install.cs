using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Paquetes de skills e instalación en el sistema
/// (.docs/project-info/ia-skills/instalacion-en-el-sistema.md). La instalación es un addon: solo aparece
/// si la composición entrega un <see cref="SkillInstallService"/> y el usuario lo activa en Ajustes ›
/// Habilidades. Sin servicio —el caso de los tests que no lo piden— nada de esta parte escribe fuera de la
/// biblioteca.</summary>
public sealed partial class SkillsViewModel
{
    private SkillInstallService? _installService;

    /// <summary>Addon activo: hay servicio y el ajuste está encendido.</summary>
    [ObservableProperty]
    private bool _isSystemInstallAvailable;

    [ObservableProperty]
    private SkillInstallDialogViewModel? _installDialog;

    [ObservableProperty]
    private SystemSkillsDialogViewModel? _systemSkillsDialog;

    /// <summary>"Claude Code · Codex" para el detalle de la skill seleccionada; vacío si no está instalada
    /// en ningún asistente.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSkillInstallSummary))]
    private string _selectedSkillInstallSummary = string.Empty;

    public bool HasSelectedSkillInstallSummary => SelectedSkillInstallSummary.Length > 0;

    /// <summary>Archivos auxiliares del paquete seleccionado (scripts, referencias, plantillas).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPackageFiles))]
    private IReadOnlyList<string> _selectedPackageFiles = [];

    public bool HasSelectedPackageFiles => SelectedPackageFiles.Count > 0;

    private void InitializeSystemInstall(SkillInstallService? installService)
    {
        _installService = installService;
        if (_settingsService is not null) _settingsService.SettingsChanged += RefreshSystemInstallAvailability;
        RefreshSystemInstallAvailability();
    }

    /// <summary>Se llama en cada guardado de ajustes, así que solo actúa cuando el addon cambia de
    /// estado: recalcular el resumen lee y resume en hash las copias instaladas.</summary>
    private void RefreshSystemInstallAvailability()
    {
        var available = _installService is not null &&
                        (_settingsService?.Settings.SkillSystemIntegrationEnabled ?? false);
        if (available == IsSystemInstallAvailable) return;

        IsSystemInstallAvailable = available;
        if (!available)
        {
            InstallDialog = null;
            SystemSkillsDialog = null;
        }
        RefreshSelectedSkillExtras();
    }

    /// <summary>Resumen de instalación y archivos del paquete de <see cref="SelectedSkill"/>. Lee disco,
    /// así que solo se llama al cambiar la selección, al recargar y tras instalar.</summary>
    private void RefreshSelectedSkillExtras()
    {
        var skill = SelectedSkill?.Model;
        SelectedPackageFiles = skill is null ? [] : SafeGetPackageFiles(skill);

        if (skill is null || !IsSystemInstallAvailable || _installService is null)
        {
            SelectedSkillInstallSummary = string.Empty;
            return;
        }

        try
        {
            SelectedSkillInstallSummary = string.Join(" · ", _installService.GetStatuses(skill)
                .Where(status => status.State is SkillInstallState.Installed or SkillInstallState.Outdated or SkillInstallState.ModifiedExternally)
                .Select(status => status.Target.DisplayName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Debug($"No se pudo leer el estado de instalación de '{skill.FilePath}': {ex}");
            SelectedSkillInstallSummary = string.Empty;
        }
    }

    private static List<string> SafeGetPackageFiles(SkillFile skill)
    {
        try { return SkillService.GetPackageFiles(skill); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Debug($"No se pudieron listar los archivos del paquete '{skill.PackageDirectory}': {ex}");
            return [];
        }
    }

    /// <summary>Tras guardar, mover o convertir una skill, actualiza sus copias del sistema que sigan
    /// sin cambios externos. Silencioso cuando va bien —el autoguardado lo llama cada pocos cientos de
    /// milisegundos— y avisa solo de los fallos.</summary>
    private void SyncInstalledCopies(SkillFile skill)
    {
        if (_installService is null || !IsSystemInstallAvailable) return;
        if (!(_settingsService?.Settings.SkillSystemAutoSync ?? true)) return;

        var (_, failed) = _installService.SyncInstalled(skill);
        foreach (var (target, error) in failed)
        {
            LogService.Warning($"No se pudo sincronizar la skill '{skill.FilePath}' con {target.DisplayName}", error);
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("SkillInstall_Notify_SyncFailed_Title"),
                LocalizedTextFormatter.Format("SkillInstall_Notify_SyncFailed_Message", skill.Name, target.DisplayName),
                NotificationSeverity.Warning);
        }
        RefreshSelectedSkillExtras();
    }

    /// <summary>Las filas de los diálogos resuelven sus textos al crearse; al cambiar de idioma se
    /// recrean con el estado actual del disco.</summary>
    private void RecreateOpenInstallDialogs()
    {
        if (InstallDialog is { } dialog) OpenInstallDialog(FindSkillByPath(dialog.Skill.FilePath) ?? dialog.Skill);
        if (SystemSkillsDialog is not null) RefreshSystemSkills();
    }

    // ===================== Paquetes =====================

    /// <summary>Convierte una skill de un archivo en paquete <c>&lt;id&gt;/SKILL.md</c>, para poder
    /// añadirle scripts y referencias.</summary>
    [RelayCommand]
    private void ConvertToPackage(SkillViewModel? skill)
    {
        if (skill is null || skill.IsPackage || skill.IsDefault) return;
        if (!PrepareLibraryMutation()) return;
        try
        {
            var converted = _skillService.ConvertToPackage(skill.Model);
            Reload();
            SelectedSkill = FindSkillByPath(converted.FilePath);
            SyncInstalledCopies(converted);
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Skills_Notify_ConvertedToPackage_Title"),
                LocalizedTextFormatter.Format("Skills_Notify_ConvertedToPackage_Message", converted.Name),
                NotificationSeverity.Success);
        }
        catch (InvalidOperationException ex)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Skills_Notify_ConvertFailed_Title"),
                LocalizedValidationErrors.Describe(ex),
                NotificationSeverity.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NotifyFolderFailure("Skills_Notify_ConvertFailed_Title", ex);
        }
    }

    /// <summary>Abre la carpeta del paquete en el gestor de archivos, que es donde se añaden scripts y
    /// referencias; para una skill de un archivo, revela el archivo.</summary>
    [RelayCommand]
    private static void OpenSkillLocation(SkillViewModel? skill)
    {
        if (skill is null) return;
        if (skill.Model.IsPackage) FolderOpener.Open(skill.Model.PackageDirectory!);
        else FolderOpener.RevealFile(skill.FilePath);
    }

    // ===================== Instalar en el sistema =====================

    [RelayCommand]
    private void OpenInstallDialog(SkillViewModel? skill)
    {
        if (skill is null || _installService is null || !IsSystemInstallAvailable) return;
        // Se instala lo que hay en disco: un borrador sin guardar no llegaría al asistente.
        ActiveEditor?.FlushPendingSave();
        if (ActiveEditor?.IsDirty == true) return;

        try
        {
            InstallDialog = new SkillInstallDialogViewModel(skill, SkillInstallService.GetInstallName(skill.Model),
                _installService.GetStatuses(skill.Model), SkillInstallService.Validate(skill.Model));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NotifyFolderFailure("SkillInstall_Notify_Failed_Title", ex);
        }
    }

    [RelayCommand]
    private void CloseInstallDialog()
    {
        InstallDialog = null;
        RefreshSelectedSkillExtras();
    }

    [RelayCommand]
    private void InstallToTarget(SkillInstallTargetRowViewModel? row)
    {
        if (row is null || InstallDialog is not { } dialog || _installService is null) return;
        RunInstallAction(dialog, () =>
        {
            _installService.Install(dialog.Skill.Model, row.Status.Target.Kind,
                overwriteExternalChanges: row.Status.State == SkillInstallState.ModifiedExternally);
            NotifyInstalled(dialog.Skill.Name, [row.Status.Target.DisplayName]);
        });
    }

    /// <summary>Instala o actualiza en todos los asistentes detectados en este equipo, sin pisar copias
    /// editadas fuera de Enki ni nombres ocupados.</summary>
    [RelayCommand]
    private void InstallToAllDetected()
    {
        if (InstallDialog is not { } dialog || _installService is null) return;
        RunInstallAction(dialog, () =>
        {
            var installed = new List<string>();
            foreach (var row in dialog.Targets.Where(r => r.IsAssistantDetected
                         && r.Status.State is SkillInstallState.NotInstalled or SkillInstallState.Outdated).ToList())
            {
                _installService.Install(dialog.Skill.Model, row.Status.Target.Kind);
                installed.Add(row.DisplayName);
            }
            if (installed.Count > 0) NotifyInstalled(dialog.Skill.Name, installed);
        });
    }

    [RelayCommand]
    private void UninstallFromTarget(SkillInstallTargetRowViewModel? row)
    {
        if (row is null || InstallDialog is not { } dialog || _installService is null) return;
        RunInstallAction(dialog, () =>
        {
            _installService.Uninstall(dialog.Skill.Model, row.Status.Target.Kind);
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("SkillInstall_Notify_Uninstalled_Title"),
                LocalizedTextFormatter.Format("SkillInstall_Notify_Uninstalled_Message", dialog.Skill.Name, row.DisplayName),
                NotificationSeverity.Success);
        });
    }

    [RelayCommand]
    private static void OpenInstallLocation(SkillInstallTargetRowViewModel? row)
    {
        if (row is null) return;
        var path = Directory.Exists(row.InstallDirectory) ? row.InstallDirectory : row.Status.Target.SkillsDirectory;
        if (!Directory.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
        FolderOpener.Open(path);
    }

    private void RunInstallAction(SkillInstallDialogViewModel dialog, Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException ex)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("SkillInstall_Notify_Failed_Title"),
                LocalizedValidationErrors.Describe(ex),
                NotificationSeverity.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NotifyFolderFailure("SkillInstall_Notify_Failed_Title", ex);
        }

        try { dialog.Update(_installService!.GetStatuses(dialog.Skill.Model)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { LogService.Debug($"No se pudo refrescar el estado de instalación: {ex}"); }
        RefreshSelectedSkillExtras();
    }

    private void NotifyInstalled(string skillName, IReadOnlyList<string> targets) =>
        _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("SkillInstall_Notify_Installed_Title"),
            LocalizedTextFormatter.Format("SkillInstall_Notify_Installed_Message", skillName, string.Join(", ", targets)),
            NotificationSeverity.Success);

    // ===================== Skills del sistema =====================

    [RelayCommand]
    private void OpenSystemSkills()
    {
        if (_installService is null || !IsSystemInstallAvailable) return;
        try
        {
            SystemSkillsDialog = new SystemSkillsDialogViewModel(_installService.GetTargets(), _installService.ListSystemSkills());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NotifyFolderFailure("SkillInstall_Notify_Failed_Title", ex);
        }
    }

    [RelayCommand]
    private void CloseSystemSkills()
    {
        SystemSkillsDialog = null;
        RefreshSelectedSkillExtras();
    }

    [RelayCommand]
    private void RefreshSystemSkills()
    {
        if (_installService is null || SystemSkillsDialog is not { } dialog) return;
        try { dialog.Update(_installService.ListSystemSkills()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NotifyFolderFailure("SkillInstall_Notify_Failed_Title", ex);
        }
    }

    /// <summary>Trae una skill del sistema a la carpeta activa de la biblioteca, con sus scripts y
    /// referencias. Reutiliza la importación normal, conflictos incluidos.</summary>
    [RelayCommand]
    private void ImportSystemSkill(SystemSkillRowViewModel? row)
    {
        if (row is null) return;
        BeginImport([row.DirectoryPath]);
    }

    [RelayCommand]
    private void UninstallSystemSkill(SystemSkillRowViewModel? row)
    {
        if (row is null || !row.IsManagedByEnki || _installService is null) return;
        if (!row.IsConfirmingUninstall)
        {
            row.IsConfirmingUninstall = true;
            return;
        }

        try
        {
            _installService.Uninstall(row.Skill);
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("SkillInstall_Notify_Uninstalled_Title"),
                LocalizedTextFormatter.Format("SkillInstall_Notify_Uninstalled_Message", row.Name, row.TargetName),
                NotificationSeverity.Success);
        }
        catch (InvalidOperationException ex)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("SkillInstall_Notify_Failed_Title"),
                LocalizedValidationErrors.Describe(ex),
                NotificationSeverity.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NotifyFolderFailure("SkillInstall_Notify_Failed_Title", ex);
        }
        RefreshSystemSkills();
    }

    [RelayCommand]
    private static void OpenSystemSkillLocation(SystemSkillRowViewModel? row)
    {
        if (row is not null) FolderOpener.Open(row.DirectoryPath);
    }

    [RelayCommand]
    private static void OpenTargetDirectory(SkillInstallTarget? target)
    {
        if (target is null) return;
        var path = Directory.Exists(target.SkillsDirectory) ? target.SkillsDirectory : Path.GetDirectoryName(target.SkillsDirectory);
        if (!string.IsNullOrEmpty(path)) FolderOpener.Open(path);
    }
}
