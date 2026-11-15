using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Services;
using enki.Services.Holidays;

namespace enki.ViewModels;

/// <summary>Ajustes › Calendario › Festivos (docs/calendar-feature/15-festivos-y-dias-no-laborables.md):
/// qué países aportan festivos al Calendario, si se muestran también los regionales y días señalados, y si
/// se aclaran los días no laborables. Cada cambio se guarda al momento y se empuja al Calendario abierto con
/// el mismo <see cref="_onCalendarSettingsChanged"/> que el resto de ajustes de esta página.</summary>
public sealed partial class SettingsViewModel
{
    private readonly HolidayService _holidayService = HolidayService.Shared;

    public ObservableCollection<HolidayCountryGroupViewModel> HolidayCountryGroups { get; } = [];

    [ObservableProperty]
    private string _holidayCountrySearch = string.Empty;

    [ObservableProperty]
    private bool _calendarShowSecondaryHolidays;

    [ObservableProperty]
    private bool _calendarShadeNonWorkingDays;

    /// <summary>"3 países activos: España, Francia, México" o "Ningún país activo".</summary>
    public string HolidaySelectionSummary
    {
        get
        {
            var selected = SelectedHolidayOptions().ToList();
            if (selected.Count == 0) return LocalizationService.Instance["Settings_Calendar_Holidays_NoneSelected"];
            return string.Format(CultureInfo.CurrentCulture,
                LocalizationService.Instance["Settings_Calendar_Holidays_SelectedSummary"],
                selected.Count, string.Join(", ", selected.Select(c => c.DisplayName)));
        }
    }

    public bool HasSelectedHolidayCountries => _settingsService.Settings.CalendarHolidayCountries.Count > 0;

    public bool HasNoHolidayCountryMatches => HolidayCountryGroups.All(g => !g.HasVisibleCountries);

    /// <summary>País de la región del sistema, si está en el catálogo y aún no está activo — atajo para el
    /// caso habitual de "quiero los festivos de donde vivo".</summary>
    public string? SuggestedHolidayCountryCode
    {
        get
        {
            var code = HolidayService.SuggestedCountryCode();
            return code is not null && !_settingsService.Settings.CalendarHolidayCountries.Contains(code, StringComparer.OrdinalIgnoreCase)
                ? code
                : null;
        }
    }

    public bool CanAddSuggestedHolidayCountry => SuggestedHolidayCountryCode is not null;

    public string SuggestedHolidayCountryLabel => SuggestedHolidayCountryCode is { } code
        ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Settings_Calendar_Holidays_AddSuggested"],
            HolidayService.CountryDisplayName(code))
        : string.Empty;

    private void InitializeHolidays()
    {
#pragma warning disable MVVMTK0034 // Estado inicial: no debe disparar el guardado.
        _calendarShowSecondaryHolidays = _settingsService.Settings.CalendarShowSecondaryHolidays;
        _calendarShadeNonWorkingDays = _settingsService.Settings.CalendarShadeNonWorkingDays;
#pragma warning restore MVVMTK0034
        RebuildHolidayCountryGroups();
    }

    /// <summary>Rehace los grupos (nombres de país en el idioma activo, orden alfabético de ese idioma) y la
    /// selección desde los ajustes. Se llama al iniciar, al cambiar de idioma y al cambiar de vault.</summary>
    private void RebuildHolidayCountryGroups()
    {
        var selected = new HashSet<string>(_settingsService.Settings.CalendarHolidayCountries, StringComparer.OrdinalIgnoreCase);
        var comparer = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);

        HolidayCountryGroups.Clear();
        foreach (var group in _holidayService.Countries.GroupBy(c => c.Region).OrderBy(g => g.Key))
        {
            var options = group
                .Select(c => new HolidayCountryOptionViewModel(c.Code, HolidayService.CountryDisplayName(c.Code), selected.Contains(c.Code), HandleHolidayCountryToggled))
                .OrderBy(o => o.DisplayName, comparer)
                .ToList();
            HolidayCountryGroups.Add(new HolidayCountryGroupViewModel(group.Key, options));
        }

        ApplyHolidayCountryFilter();
        RaiseHolidaySummaryChanged();
    }

    /// <summary>Recarga tras cambiar de vault: los ajustes de festivos pueden ser otros.</summary>
    private void ReloadHolidaysForVault()
    {
#pragma warning disable MVVMTK0034 // Recarga sin disparar el guardado.
        _calendarShowSecondaryHolidays = _settingsService.Settings.CalendarShowSecondaryHolidays;
        _calendarShadeNonWorkingDays = _settingsService.Settings.CalendarShadeNonWorkingDays;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(CalendarShowSecondaryHolidays));
        OnPropertyChanged(nameof(CalendarShadeNonWorkingDays));

        var selected = new HashSet<string>(_settingsService.Settings.CalendarHolidayCountries, StringComparer.OrdinalIgnoreCase);
        foreach (var option in HolidayCountryGroups.SelectMany(g => g.Countries))
            option.SetSelectedSilently(selected.Contains(option.Code));
        RaiseHolidaySummaryChanged();
    }

    private IEnumerable<HolidayCountryOptionViewModel> SelectedHolidayOptions()
    {
        var byCode = HolidayCountryGroups.SelectMany(g => g.Countries).ToDictionary(o => o.Code, StringComparer.OrdinalIgnoreCase);
        foreach (var code in _settingsService.Settings.CalendarHolidayCountries)
        {
            if (byCode.TryGetValue(code, out var option)) yield return option;
        }
    }

    partial void OnHolidayCountrySearchChanged(string value) => ApplyHolidayCountryFilter();

    private void ApplyHolidayCountryFilter()
    {
        foreach (var group in HolidayCountryGroups)
        {
            foreach (var option in group.Countries)
                option.IsVisible = SettingsSearchMatcher.Matches(HolidayCountrySearch, [option.DisplayName, option.Code]);
            group.RefreshVisibility();
        }
        OnPropertyChanged(nameof(HasNoHolidayCountryMatches));
    }

    /// <summary>Conserva el orden en que el usuario activó los países: es el orden en que el Calendario
    /// lista sus capas.</summary>
    private void HandleHolidayCountryToggled(HolidayCountryOptionViewModel option)
    {
        var countries = _settingsService.Settings.CalendarHolidayCountries;
        var index = countries.FindIndex(c => string.Equals(c, option.Code, StringComparison.OrdinalIgnoreCase));
        if (option.IsSelected && index < 0) countries.Add(option.Code);
        else if (!option.IsSelected && index >= 0) countries.RemoveAt(index);
        else return;

        _settingsService.Save();
        RaiseHolidaySummaryChanged();
        _onCalendarSettingsChanged();
    }

    [RelayCommand]
    private void AddSuggestedHolidayCountry()
    {
        if (SuggestedHolidayCountryCode is not { } code) return;
        var option = HolidayCountryGroups.SelectMany(g => g.Countries)
            .FirstOrDefault(o => string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase));
        if (option is not null) option.IsSelected = true;
    }

    [RelayCommand]
    private void ClearHolidayCountries()
    {
        if (_settingsService.Settings.CalendarHolidayCountries.Count == 0) return;
        _settingsService.Settings.CalendarHolidayCountries.Clear();
        foreach (var option in HolidayCountryGroups.SelectMany(g => g.Countries)) option.SetSelectedSilently(false);
        _settingsService.Save();
        RaiseHolidaySummaryChanged();
        _onCalendarSettingsChanged();
    }

    partial void OnCalendarShowSecondaryHolidaysChanged(bool value)
    {
        _settingsService.Settings.CalendarShowSecondaryHolidays = value;
        _settingsService.Save();
        _onCalendarSettingsChanged();
    }

    partial void OnCalendarShadeNonWorkingDaysChanged(bool value)
    {
        _settingsService.Settings.CalendarShadeNonWorkingDays = value;
        _settingsService.Save();
        _onCalendarSettingsChanged();
    }

    private void RaiseHolidaySummaryChanged()
    {
        OnPropertyChanged(nameof(HolidaySelectionSummary));
        OnPropertyChanged(nameof(HasSelectedHolidayCountries));
        OnPropertyChanged(nameof(SuggestedHolidayCountryCode));
        OnPropertyChanged(nameof(CanAddSuggestedHolidayCountry));
        OnPropertyChanged(nameof(SuggestedHolidayCountryLabel));
    }
}
