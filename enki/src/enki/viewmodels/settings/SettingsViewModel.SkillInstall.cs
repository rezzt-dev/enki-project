using System.Collections.Generic;
using System.Linq;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Ajustes › Habilidades › Instalación en el sistema
/// (.docs/project-info/ia-skills/instalacion-en-el-sistema.md). Son claves de instalación
/// (<see cref="SettingsService"/>): describen este equipo, no el vault. Se leen del modelo en cada acceso
/// en vez de copiarse en el constructor, así que un cambio de vault no necesita refrescarlas;
/// <c>SkillsViewModel</c> reacciona al guardado mediante <see cref="SettingsService.SettingsChanged"/>.</summary>
public sealed partial class SettingsViewModel
{
    public bool SkillSystemIntegrationEnabled
    {
        get => _settingsService.Settings.SkillSystemIntegrationEnabled;
        set
        {
            if (_settingsService.Settings.SkillSystemIntegrationEnabled == value) return;
            _settingsService.Settings.SkillSystemIntegrationEnabled = value;
            _settingsService.Save();
            OnPropertyChanged();
        }
    }

    public bool SkillSystemAutoSync
    {
        get => _settingsService.Settings.SkillSystemAutoSync;
        set
        {
            if (_settingsService.Settings.SkillSystemAutoSync == value) return;
            _settingsService.Settings.SkillSystemAutoSync = value;
            _settingsService.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Directorios de destino resueltos en este equipo, para que el usuario sepa exactamente
    /// dónde escribirá Enki antes de activar el addon. Solo resuelve rutas; no escribe nada.</summary>
    public IReadOnlyList<string> SkillInstallTargetPaths { get; } =
        new SkillInstallService().GetTargets().Select(target => $"{target.DisplayName} · {target.SkillsDirectory}").ToList();
}
