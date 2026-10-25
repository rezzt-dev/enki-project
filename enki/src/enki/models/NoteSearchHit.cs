namespace enki.Models;

/// <summary>Qué parte de una nota casó con la consulta. Determina el orden de los resultados (un acierto
/// en el título pesa más que uno en el cuerpo) y qué se muestra como subtítulo: la ruta de la nota para
/// un acierto de título/etiqueta, el fragmento de texto real para uno de contenido.</summary>
public enum NoteSearchField
{
    Title,
    Tag,
    Content
}

/// <summary>Un acierto de búsqueda sobre una nota: la nota, dónde casó y —cuando casó en el cuerpo— el
/// punto exacto del documento, para que abrir el resultado lleve el cursor a la línea correcta en vez de
/// al principio de la nota.
///
/// <para>Existe porque hasta ahora ninguna de las cuatro superficies de búsqueda de la app miraba el
/// contenido real de las notas: todas comparaban contra <see cref="NoteFile.Preview"/>, que son los
/// primeros 160 caracteres del cuerpo aplanados. Cualquier cosa escrita más allá de esos 160 caracteres
/// era, literalmente, imposible de encontrar desde la aplicación.</para></summary>
public sealed class NoteSearchHit
{
    public required NoteFile Note { get; init; }

    public required NoteSearchField Field { get; init; }

    /// <summary>Desplazamiento en caracteres del primer acierto dentro del contenido crudo de la nota, o
    /// <c>-1</c> si el acierto no fue en el cuerpo. El editor lo usa para colocar el cursor
    /// (<c>EditorViewModel.RequestCaretAt</c>).</summary>
    public int Offset { get; init; } = -1;

    /// <summary>Número de línea (empezando en 1) del primer acierto en el cuerpo; <c>0</c> si el acierto
    /// no fue en el cuerpo.</summary>
    public int LineNumber { get; init; }

    /// <summary>Cuántas veces aparece la consulta en el cuerpo de la nota. Permite decir "3 coincidencias"
    /// en vez de listar la misma nota tres veces en el popup.</summary>
    public int MatchCount { get; init; }

    /// <summary>Fragmento de una línea alrededor del primer acierto, ya recortado y con elipsis, listo para
    /// pintar como subtítulo del resultado. Cadena vacía para un acierto de título o etiqueta.</summary>
    public string Snippet { get; init; } = string.Empty;
}
