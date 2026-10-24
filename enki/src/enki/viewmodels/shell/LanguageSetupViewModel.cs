using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

/// <summary>ViewModel de la pantalla de selección de idioma en el primer arranque — se muestra antes que
/// cualquier otra pantalla cuando <see cref="Models.AppSettings.Language"/> es null/vacío. Ver
/// docs/agent-docs/internacionalizacion-i18n.md §3.1.</summary>
public sealed partial class LanguageSetupViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly Action _onLanguageSelected;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Lista estática de los 6 idiomas soportados con su nombre nativo. Se muestra tal cual
    /// (no traducido por el idioma activo) para que cada hablante reconozca su idioma. CA1822 sugiere
    /// marcar el getter <c>static</c> — mismo caso que <c>SettingsViewModel.AvailableLanguages</c>: se
    /// suprime en vez de "arreglarse" porque <c>{Binding Languages}</c> en LanguageSetupView.axaml
    /// necesita resolverlo contra la instancia de <c>DataContext</c> (Fase 10, docs/analisis-proyecto/roadmap.md).</summary>
#pragma warning disable CA1822
    public IReadOnlyList<SupportedLanguageInfo> Languages => SupportedLanguages.All;
#pragma warning restore CA1822

    public LanguageSetupViewModel(SettingsService settingsService, Action onLanguageSelected)
    {
        _settingsService = settingsService;
        _onLanguageSelected = onLanguageSelected;
    }

    [RelayCommand]
    private void SelectLanguage(string? cultureCode)
    {
        var language = SupportedLanguages.Find(cultureCode);
        if (language is null) return;

        var previousLanguage = _settingsService.Settings.Language;
        ErrorMessage = null;
        _settingsService.Settings.Language = language.CultureCode;
        try
        {
            _settingsService.Save();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _settingsService.Settings.Language = previousLanguage;
            // En el idioma recién pulsado, no en el de origen: un guardado fallido no cambia el idioma de
            // la aplicación (esa es la parte que se conserva), pero el aviso lo lee alguien que acaba de
            // decir en qué idioma quiere leer.
            ErrorMessage = LocalizedTextFormatter.Resolve(
                VaultFailureReasons.DescribeLocalized(ex),
                culture: CultureInfo.GetCultureInfo(language.CultureCode));
            return;
        }

        LocalizationService.Instance.SetLanguage(language.CultureCode);
        _onLanguageSelected();
    }
}
