using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Ajustes › Editor › Plantillas: qué plantilla aplica la nota diaria y el acceso a la carpeta
/// donde viven todas. Partial aparte por el mismo criterio que el resto de la sección
/// (<c>SettingsViewModel.Editor.cs</c>, <c>.Calendar.cs</c>): un tema, un archivo.
///
/// <para>El ajuste se guarda en <see cref="Models.VaultSettings.DailyNoteTemplate"/> —no en
/// <see cref="Models.AppSettings"/>— porque apunta a un archivo del vault activo; cambiar de vault recarga
/// la lista y la selección desde el vault nuevo (<see cref="ReloadDailyNoteTemplates"/>, llamada desde
/// <c>ReloadForVault</c>).</para></summary>
public sealed partial class SettingsViewModel
{
    public ObservableCollection<NoteTemplateOptionViewModel> DailyNoteTemplateOptions { get; } = [];

    [ObservableProperty]
    private NoteTemplateOptionViewModel? _selectedDailyNoteTemplate;

    /// <summary>Evita que reconstruir la lista (al abrir Ajustes, al cambiar de vault o al crear una
    /// plantilla desde Documentos) se interprete como una elección del usuario y vuelva a escribir el
    /// mismo ajuste en disco. Mismo problema —y misma solución— que el <c>#pragma</c> de
    /// <c>SettingsViewModel.VaultReload.cs</c> para el resto de controles.</summary>
    private bool _reloadingDailyNoteTemplates;

    /// <summary>Recompone el desplegable a partir de las plantillas que hay ahora mismo en el vault y
    /// vuelve a marcar la que el ajuste tiene guardada. Si esa plantilla ya no existe (se borró o se
    /// renombró fuera de Enki) la selección cae en "sin plantilla" sin tocar el ajuste guardado, para que
    /// recuperar el archivo devuelva la configuración a donde estaba.</summary>
    internal void ReloadDailyNoteTemplates()
    {
        _reloadingDailyNoteTemplates = true;
        try
        {
            var stored = _vaultSettingsService.Settings.DailyNoteTemplate;

            DailyNoteTemplateOptions.Clear();
            DailyNoteTemplateOptions.Add(NoteTemplateOptionViewModel.None());
            foreach (var template in _noteTemplateService.LoadTemplates())
                DailyNoteTemplateOptions.Add(NoteTemplateOptionViewModel.For(template));

            SelectedDailyNoteTemplate = DailyNoteTemplateOptions.FirstOrDefault(option =>
                    option.RelativePath is not null &&
                    string.Equals(option.RelativePath, stored, VaultService.PathComparison))
                ?? DailyNoteTemplateOptions[0];
        }
        finally
        {
            _reloadingDailyNoteTemplates = false;
        }

        OnPropertyChanged(nameof(TemplatesSearchText));
    }

    partial void OnSelectedDailyNoteTemplateChanged(NoteTemplateOptionViewModel? value)
    {
        if (_reloadingDailyNoteTemplates) return;

        _vaultSettingsService.Settings.DailyNoteTemplate = value?.RelativePath;
        _vaultSettingsService.Save();
    }

    /// <summary>Alimenta el buscador de Ajustes con los nombres de las plantillas del usuario, igual que
    /// <see cref="ThemeSearchText"/> hace con los temas: buscar "diario" debe llevar hasta aquí aunque esa
    /// palabra solo exista en una plantilla suya.</summary>
    public string TemplatesSearchText =>
        string.Join("\n", DailyNoteTemplateOptions.Select(option => option.Label));

    /// <summary>Abre <c>&lt;vault&gt;/.enki/templates/</c> en el explorador del sistema, creándola antes si
    /// todavía no existía — pulsar "Abrir carpeta" y que no se abra nada porque la carpeta aún no se había
    /// sembrado sería el peor resultado posible de un botón que promete justo eso.</summary>
    [RelayCommand]
    private void OpenTemplatesFolder()
    {
        try
        {
            _noteTemplateService.EnsureInitialized();
            if (Directory.Exists(_noteTemplateService.TemplatesDirectory))
                FolderOpener.Open(_noteTemplateService.TemplatesDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error("No se pudo abrir la carpeta de plantillas", ex);
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Notification_VaultWriteFailed_Title"),
                VaultFailureReasons.DescribeLocalized(ex),
                Models.NotificationSeverity.Error);
        }
    }
}
