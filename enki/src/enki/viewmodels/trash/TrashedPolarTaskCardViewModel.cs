using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Tarjeta de la categoría "Tareas Polar" de la Papelera para una tarea borrada (análisis alfa-3
/// §4.3: antes no existía ningún camino de recuperación). Mismo patrón de envoltura fina que
/// <see cref="TrashedTaskCardViewModel"/> (la versión Kanban).</summary>
public sealed class TrashedPolarTaskCardViewModel(PolarTask task)
{
    public PolarTask Task { get; } = task;
    public string Title => Task.Title;

    public string DeletedLabel => Task.DeletedAtUtc is { } deletedUtc
        ? RelativeTimeFormatter.Format(deletedUtc)
        : string.Empty;
}
