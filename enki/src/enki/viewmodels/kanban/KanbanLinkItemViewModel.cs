using CommunityToolkit.Mvvm.Input;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Un enlace web de una tarjeta, tal como lo enseñan el formulario y el diálogo de detalle.
/// Inmutable: editar un enlace es quitarlo y volver a añadirlo, igual que una dependencia «bloquea a».
/// <see cref="OpenCommand"/> lo entrega al navegador predeterminado del sistema a través de
/// <see cref="UrlOpener"/>, que vuelve a validar el esquema antes de abrir nada.</summary>
public sealed partial class KanbanLinkItemViewModel
{
    public string Url { get; }

    /// <summary>Servidor y ruta sin esquema (<see cref="WebLinkNormalizer.Describe"/>); la URL completa va
    /// en el tooltip.</summary>
    public string Label { get; }

    public KanbanLinkItemViewModel(string url)
    {
        Url = url;
        Label = WebLinkNormalizer.Describe(url);
    }

    [RelayCommand]
    private void Open() => UrlOpener.Open(Url);
}
