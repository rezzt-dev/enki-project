using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Services.Holidays;

namespace enki.ViewModels;

/// <summary>Un país del selector de festivos de Ajustes › Calendario. El cambio de
/// <see cref="IsSelected"/> se comunica por callback a <see cref="SettingsViewModel"/>, que es quien guarda
/// y avisa al Calendario abierto — mismo patrón que <see cref="CalendarLayerViewModel"/>.</summary>
public sealed partial class HolidayCountryOptionViewModel : ObservableObject
{
    private readonly Action<HolidayCountryOptionViewModel> _onSelectionChanged;

    public string Code { get; }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Lo decide el filtro de búsqueda del selector.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    public HolidayCountryOptionViewModel(string code, string displayName, bool isSelected, Action<HolidayCountryOptionViewModel> onSelectionChanged)
    {
        Code = code;
        _displayName = displayName;
        _isSelected = isSelected;
        _onSelectionChanged = onSelectionChanged;
    }

    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged(this);

    /// <summary>Restablece la selección desde los ajustes (otro vault) sin volver a guardarla.</summary>
    public void SetSelectedSilently(bool value)
    {
#pragma warning disable MVVMTK0034 // Asignación directa a propósito: no debe disparar el guardado.
        _isSelected = value;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(IsSelected));
    }
}

/// <summary>Grupo del selector de festivos (Unión Europea, Latinoamérica, otros países). Se recrea al cambiar
/// de idioma (<c>SettingsViewModel.RebuildHolidayCountryGroups</c>), así que <see cref="Title"/> nace ya
/// traducido.</summary>
public sealed partial class HolidayCountryGroupViewModel : ObservableObject
{
    public HolidayRegion Region { get; }

    public IReadOnlyList<HolidayCountryOptionViewModel> Countries { get; }

    [ObservableProperty]
    private bool _hasVisibleCountries = true;

    public HolidayCountryGroupViewModel(HolidayRegion region, IReadOnlyList<HolidayCountryOptionViewModel> countries)
    {
        Region = region;
        Countries = countries;
    }

    public string Title => LocalizationService.Instance[Region switch
    {
        HolidayRegion.EuropeanUnion => "Settings_Calendar_Holidays_GroupEu",
        HolidayRegion.LatinAmerica => "Settings_Calendar_Holidays_GroupLatam",
        _ => "Settings_Calendar_Holidays_GroupOther"
    }];

    public void RefreshVisibility() => HasVisibleCountries = Countries.Any(c => c.IsVisible);
}
