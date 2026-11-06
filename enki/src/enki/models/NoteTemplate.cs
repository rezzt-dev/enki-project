namespace enki.Models;

/// <summary>Una plantilla de notas: un <c>.md</c> de <c>&lt;vault&gt;/.enki/templates/</c> con un
/// frontmatter YAML opcional (<c>name</c>, <c>description</c>, <c>title</c>) y un cuerpo Markdown que
/// puede contener variables <c>{{...}}</c> (ver <see cref="Services.NoteTemplateRenderer"/>).
///
/// <para>POCO puro, como el resto de <c>models/</c>: lo construye y lo relee siempre
/// <see cref="Services.NoteTemplateService"/> a partir del archivo, que es la única fuente de verdad —
/// aquí no se cachea nada que pueda quedar desincronizado con el disco.</para></summary>
public sealed class NoteTemplate
{
    /// <summary>Nombre del archivo sin extensión (<c>daily</c>). No es la identidad de la plantilla —dos
    /// subcarpetas pueden tener una <c>daily.md</c> cada una—, para eso está
    /// <see cref="RelativePath"/>.</summary>
    public required string Id { get; init; }

    /// <summary>Lo que se lee en la lista: el <c>name</c> del frontmatter o, si no lo trae,
    /// <see cref="Id"/>.</summary>
    public required string Name { get; init; }

    /// <summary>Una línea explicando para qué sirve la plantilla (<c>description</c> del frontmatter).
    /// Cadena vacía si no la declara.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Patrón del título de la nota creada (<c>title</c> del frontmatter), con las mismas
    /// variables que el cuerpo — p. ej. <c>"{{date}}"</c> para que cada nota nazca con la fecha por
    /// nombre. Null cuando la plantilla no lo declara, y entonces el título lo decide quien la aplica
    /// (<see cref="Services.NoteTemplateService.Prepare"/>).</summary>
    public string? TitlePattern { get; init; }

    /// <summary>Ruta absoluta del archivo de plantilla.</summary>
    public required string FilePath { get; init; }

    /// <summary>Ruta relativa a la carpeta de plantillas, siempre con <c>/</c> como separador
    /// (<c>daily.md</c>, <c>trabajo/reunion.md</c>). Es la identidad estable de la plantilla: lo que
    /// guarda <see cref="VaultSettings.DailyNoteTemplate"/>, porque una ruta absoluta dejaría de valer al
    /// mover el vault de carpeta y el nombre a secas no distinguiría dos homónimas en subcarpetas
    /// distintas.</summary>
    public required string RelativePath { get; init; }

    /// <summary>El Markdown de la plantilla sin su frontmatter — los metadatos describen la plantilla, no
    /// forman parte de la nota, así que nunca se copian a la nota creada.</summary>
    public required string Body { get; init; }
}
