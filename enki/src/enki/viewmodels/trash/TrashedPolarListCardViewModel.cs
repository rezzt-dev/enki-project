using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Tarjeta de la categoría "Tareas Polar" de la Papelera para una lista borrada (análisis alfa-3
/// §4.3: antes no existía ningún camino de recuperación). Mismo patrón de envoltura fina que
/// <see cref="TrashedTaskCardViewModel"/>.</summary>
public sealed class TrashedPolarListCardViewModel(PolarTaskList list)
{
    public PolarTaskList List { get; } = list;
    public string Title => List.Title;

    public string DeletedLabel => List.DeletedAtUtc is { } deletedUtc
        ? RelativeTimeFormatter.Format(deletedUtc)
        : string.Empty;
}
