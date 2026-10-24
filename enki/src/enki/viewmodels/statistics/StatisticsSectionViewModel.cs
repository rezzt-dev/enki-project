using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;

namespace enki.ViewModels;

/// <summary>Una de las seis sub-secciones plegables de Estadísticas (Pulso, Ritmo, Tareas, Conocimiento,
/// Hábitos, Almacén). La página es larga a propósito — el objetivo es "tenerlo todo bajo control" — y sin
/// poder plegar bloques enteros llegar a Almacén cuesta seis pantallas de scroll.
///
/// El resumen de la derecha (<see cref="Summary"/>) es lo que hace que plegar no sea perder información:
/// con la sección cerrada, la cabecera sigue diciendo el dato de cabecera de ese bloque (el tamaño del
/// vault, el % de constancia, cuántas tarjetas hay), así que se puede dejar plegado sin quedarse a ciegas.
/// Mismo patrón de encabezado plegable que Ajustes › Apariencia › Temas predefinidos, generalizado a una
/// lista de claves en vez de un booleano por bloque.</summary>
public sealed partial class StatisticsSectionViewModel : ObservableObject
{
    private readonly Action<StatisticsSectionViewModel>? _onExpandedChanged;

    /// <summary>Clave estable persistida en <see cref="Models.AppSettings.StatisticsCollapsedSections"/> —
    /// nunca el título, que cambia con el idioma.</summary>
    public string Key { get; }

    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>Dato de cabecera que sigue visible con la sección plegada.</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isExpanded = true;

    public StatisticsSectionViewModel(string key, string title, bool isExpanded,
        Action<StatisticsSectionViewModel>? onExpandedChanged = null)
    {
        Key = key;
        _title = title;
        _isExpanded = isExpanded;
        _onExpandedChanged = onExpandedChanged;
    }

    /// <summary>Texto del tooltip y del lector de pantalla, que dice la acción (plegar/desplegar), no el
    /// estado — es lo que espera quien lo lee antes de pulsar.</summary>
    public string ToggleTooltip =>
        LocalizationService.Instance[IsExpanded ? "Statistics_Section_Collapse" : "Statistics_Section_Expand"];

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    /// <summary>Cambio de idioma en caliente: no se llama <c>RefreshLocalizedLabels</c> a propósito, para
    /// no entrar en el recuento declaración/llamada de <c>RefreshLocalizedLabelsCoverageTests</c> — esta
    /// no es una raíz de la cadena de refresco, la invoca <see cref="StatisticsViewModel"/> con el título
    /// ya resuelto.</summary>
    public void ApplyLocalizedTitle(string title)
    {
        Title = title;
        OnPropertyChanged(nameof(ToggleTooltip));
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleTooltip));
        _onExpandedChanged?.Invoke(this);
    }
}
