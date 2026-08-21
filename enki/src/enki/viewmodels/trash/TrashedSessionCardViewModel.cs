using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Tarjeta de la categoría "Sesiones" de la Papelera (Fase 3 de docs/analisis-proyecto/roadmap.md)
/// — una sesión borrada arrastra consigo su carpeta de notas, así que la tarjeta destaca el recuento de
/// notas que se recuperarían/perderían con ella. Mismo patrón que <see cref="TrashedTaskCardViewModel"/>:
/// envoltura fina sobre el modelo, sin estado propio.</summary>
public sealed class TrashedSessionCardViewModel(Session session)
{
    public Session Session { get; } = session;
    public string Name => Session.Name;

    /// <summary>"{0} notas" pluralizado por idioma — reutiliza las mismas claves que la lista de sesiones
    /// (<see cref="SessionListItemViewModel.NoteCountLabel"/>) en vez de duplicar variantes.</summary>
    public string NoteCountLabel => Plural.Get("Sessions_NoteCount", Session.NoteCount, Session.NoteCount);
}
