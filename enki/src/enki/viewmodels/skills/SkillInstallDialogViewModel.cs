using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Diálogo "Instalar en el sistema" de una skill: una fila por asistente con su estado y la
/// acción que corresponde. Las acciones las ejecuta <see cref="SkillsViewModel"/>, que es quien tiene el
/// servicio y las notificaciones; este modelo solo describe lo que se pinta.</summary>
public sealed partial class SkillInstallDialogViewModel : ObservableObject
{
    public SkillViewModel Skill { get; }

    /// <summary>Nombre de carpeta y <c>name</c> con que la verán los asistentes.</summary>
    public string InstallName { get; }

    public ObservableCollection<SkillInstallTargetRowViewModel> Targets { get; } = [];

    /// <summary>Avisos de compatibilidad ya localizados.</summary>
    public IReadOnlyList<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public SkillInstallDialogViewModel(SkillViewModel skill, string installName, IEnumerable<SkillInstallStatus> statuses,
        IEnumerable<SkillInstallWarning> warnings)
    {
        Skill = skill;
        InstallName = installName;
        Warnings = warnings.Select(warning => string.Format(CultureInfo.CurrentCulture,
            LocalizationService.Instance[warning.ResourceKey], warning.Arguments.ToArray())).ToList();
        Update(statuses);
    }

    /// <summary>Sustituye las filas por el estado actual del disco.</summary>
    public void Update(IEnumerable<SkillInstallStatus> statuses)
    {
        Targets.Clear();
        foreach (var status in statuses) Targets.Add(new SkillInstallTargetRowViewModel(status));
        OnPropertyChanged(nameof(CanInstallAll));
    }

    /// <summary>Hay al menos un asistente detectado donde instalar o actualizar sin pisar nada.</summary>
    public bool CanInstallAll => Targets.Any(row => row.Status.Target.IsAssistantDetected
        && row.Status.State is SkillInstallState.NotInstalled or SkillInstallState.Outdated);
}

/// <summary>Fila de un asistente en <see cref="SkillInstallDialogViewModel"/>.</summary>
public sealed class SkillInstallTargetRowViewModel
{
    public SkillInstallStatus Status { get; }

    public SkillInstallTargetRowViewModel(SkillInstallStatus status) => Status = status;

    public string DisplayName => Status.Target.DisplayName;
    public string InstallDirectory => Status.InstallDirectory;
    public bool IsAssistantDetected => Status.Target.IsAssistantDetected;
    public bool IsInstalled => Status.State is SkillInstallState.Installed or SkillInstallState.Outdated or SkillInstallState.ModifiedExternally;

    /// <summary>Estados que piden atención: no se resuelven solos al guardar.</summary>
    public bool IsAttention => Status.State is SkillInstallState.ModifiedExternally or SkillInstallState.NameTaken;

    public string StateLabel => LocalizationService.Instance[Status.State switch
    {
        SkillInstallState.Installed => "SkillInstall_State_Installed",
        SkillInstallState.Outdated => "SkillInstall_State_Outdated",
        SkillInstallState.ModifiedExternally => "SkillInstall_State_ModifiedExternally",
        SkillInstallState.NameTaken => "SkillInstall_State_NameTaken",
        _ => "SkillInstall_State_NotInstalled"
    }];

    /// <summary>La acción principal según el estado; un nombre ocupado no tiene ninguna.</summary>
    public bool CanInstall => Status.State != SkillInstallState.NameTaken && Status.State != SkillInstallState.Installed;

    public string InstallLabel => LocalizationService.Instance[Status.State switch
    {
        SkillInstallState.Outdated => "SkillInstall_Action_Update",
        SkillInstallState.ModifiedExternally => "SkillInstall_Action_Overwrite",
        _ => "SkillInstall_Action_Install"
    }];

    public bool CanUninstall => IsInstalled;
}

/// <summary>Diálogo "Skills del sistema": todo lo que hay en los directorios de los asistentes, haya
/// llegado desde Enki o no, para importarlo a la biblioteca o desinstalar lo que instaló Enki.</summary>
public sealed partial class SystemSkillsDialogViewModel : ObservableObject
{
    public ObservableCollection<SystemSkillRowViewModel> Skills { get; } = [];

    /// <summary>Directorios de los tres asistentes, para abrirlos aunque estén vacíos.</summary>
    public IReadOnlyList<SkillInstallTarget> Targets { get; }

    [ObservableProperty]
    private bool _isEmpty;

    public SystemSkillsDialogViewModel(IReadOnlyList<SkillInstallTarget> targets, IEnumerable<SystemSkill> skills)
    {
        Targets = targets;
        Update(skills);
    }

    public void Update(IEnumerable<SystemSkill> skills)
    {
        Skills.Clear();
        foreach (var skill in skills) Skills.Add(new SystemSkillRowViewModel(skill));
        IsEmpty = Skills.Count == 0;
    }
}

/// <summary>Fila de <see cref="SystemSkillsDialogViewModel"/>. Desinstalar pide una segunda pulsación:
/// si el origen ya no está en la biblioteca, la copia instalada es la única que queda.</summary>
public sealed partial class SystemSkillRowViewModel : ObservableObject
{
    public SystemSkill Skill { get; }

    public SystemSkillRowViewModel(SystemSkill skill) => Skill = skill;

    public string TargetName => Skill.Target.DisplayName;
    public string Name => Skill.Name;
    public string InstallName => Skill.InstallName;
    public string Description => Skill.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Skill.Description);
    public string DirectoryPath => Skill.DirectoryPath;
    public bool IsManagedByEnki => Skill.IsManagedByEnki;
    public bool IsSourceMissing => Skill.IsSourceMissing;

    public string OriginLabel => LocalizationService.Instance[Skill switch
    {
        { IsManagedByEnki: true, IsSourceMissing: true } => "SkillInstall_Origin_EnkiMissingSource",
        { IsManagedByEnki: true } => "SkillInstall_Origin_Enki",
        _ => "SkillInstall_Origin_External"
    }];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UninstallLabel))]
    private bool _isConfirmingUninstall;

    public string UninstallLabel => LocalizationService.Instance[IsConfirmingUninstall
        ? "SkillInstall_Action_ConfirmUninstall"
        : "SkillInstall_Action_Uninstall"];
}
