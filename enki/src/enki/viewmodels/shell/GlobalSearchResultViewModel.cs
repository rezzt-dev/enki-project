using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace enki.ViewModels;

/// <summary>
/// Resultado individual de la búsqueda global. Lleva la información mínima necesaria para
/// renderizar la fila y navegar a la pantalla que posee el dato real.
/// </summary>
public sealed partial class GlobalSearchResultViewModel : ObservableObject
{
    /// <summary>
    /// Tipo de origen del resultado (nota, tarjeta Kanban, tarea Polar o recordatorio).
    /// </summary>
    public GlobalSearchResultType Type { get; }

    /// <summary>
    /// Identificador del elemento. Para notas es la ruta completa del archivo; para el resto
    /// es el identificador propio del modelo.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Título o nombre del elemento.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Contexto secundario (carpeta, columna, lista o fecha del recordatorio).
    /// </summary>
    public string Subtitle { get; }

    /// <summary>
    /// Nombre del icono de Material.Icons.Avalonia que representa el tipo de resultado.
    /// </summary>
    public string? IconKind { get; }

    /// <summary>
    /// Objeto original del modelo, usado por el callback de navegación para abrir el elemento
    /// sin tener que volver a buscarlo.
    /// </summary>
    public object? Payload { get; }

    private readonly Action<GlobalSearchResultViewModel> _onOpen;

    public GlobalSearchResultViewModel(
        GlobalSearchResultType type,
        string id,
        string title,
        string subtitle,
        string? iconKind,
        object? payload,
        Action<GlobalSearchResultViewModel> onOpen)
    {
        Type = type;
        Id = id;
        Title = title;
        Subtitle = subtitle;
        IconKind = iconKind;
        Payload = payload;
        _onOpen = onOpen;
    }

    /// <summary>
    /// Abre el resultado invocando el callback que conoce cómo navegar al módulo propietario.
    /// </summary>
    [RelayCommand]
    private void Open() => _onOpen(this);
}
