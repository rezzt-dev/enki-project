using System;
using System.Collections.Generic;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>La nota diaria de un día, tal como la deja <see cref="DailyNoteService.OpenOrCreate"/>.</summary>
/// <param name="Note">La nota, existente o recién creada.</param>
/// <param name="Created">Si ha nacido en esta llamada — lo que decide si tiene sentido colocar el cursor
/// donde lo pedía la plantilla y si el cambio hay que anunciarlo.</param>
/// <param name="CaretOffset">Posición del cursor pedida por la plantilla al crear, o null.</param>
public readonly record struct DailyNoteOpening(NoteFile Note, bool Created, int? CaretOffset);

/// <summary>Todo lo que la app sabe de la nota diaria en un solo sitio: cómo se llama la de cada día, dónde
/// está, cómo se crea aplicando la plantilla configurada y cómo se combina esa misma plantilla con una nota
/// que ya existía (<see cref="DailyNoteTemplateMerge"/>).
///
/// <para>Sin estado propio ni persistencia: lee el vault a través de <see cref="VaultService"/>, la
/// plantilla configurada a través de <see cref="VaultSettingsService"/> y el catálogo a través de
/// <see cref="NoteTemplateService"/> —los tres la única fuente de verdad de lo suyo—, exactamente como
/// <see cref="NoteTemplateService"/> no cachea las plantillas. Existe para que las cuatro superficies que
/// hoy tocan la nota diaria (el botón de Documentos, la paleta de comandos, la navegación día anterior /
/// día siguiente del editor y el calendario de constancia) compartan una sola definición de qué es la nota
/// diaria, en vez de repetir el "buscar por título, crear si no está" cada una a su manera.</para></summary>
public sealed class DailyNoteService
{
    private readonly VaultService _vaultService;
    private readonly NoteTemplateService _templateService;

    /// <summary>Null en los tests que no configuran ajustes de vault: entonces no hay plantilla de nota
    /// diaria y esta se comporta como siempre (nombre <c>yyyy-MM-dd</c> y encabezado con la fecha).</summary>
    private readonly VaultSettingsService? _vaultSettingsService;

    public DailyNoteService(VaultService vaultService, NoteTemplateService templateService,
        VaultSettingsService? vaultSettingsService = null)
    {
        _vaultService = vaultService;
        _templateService = templateService;
        _vaultSettingsService = vaultSettingsService;
    }

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>La plantilla elegida en Ajustes › Editor › Plantillas, o null si no hay ninguna o el
    /// archivo ya no existe (un ajuste que apunta a una plantilla borrada es "sin plantilla", no un
    /// error). Se relee del disco en cada llamada, como el resto de consumidores de
    /// <see cref="NoteTemplateService"/>; quien necesite resolver varios días seguidos la resuelve una vez
    /// y la pasa por parámetro a los métodos de abajo.</summary>
    public NoteTemplate? ResolveTemplate() =>
        _templateService.FindTemplate(_vaultSettingsService?.Settings.DailyNoteTemplate);

    /// <summary>Nombre que debe tener la nota diaria de <paramref name="date"/>.</summary>
    public string TitleFor(DateOnly date, NoteTemplate? template = null) =>
        DailyNoteNaming.TitleFor(date, template ?? ResolveTemplate());

    /// <summary>Qué día es la nota diaria <paramref name="note"/>, o null si no es una nota diaria.
    /// Se mira el <b>nombre del archivo</b> y no <see cref="NoteFile.Title"/>: el título de una nota es su
    /// primer encabezado, que el usuario puede cambiar mientras escribe, y una nota cualquiera que hoy
    /// empiece por "# 2026-09-18" no es la nota diaria de ese día.</summary>
    public DateOnly? DateOf(NoteFile note, NoteTemplate? template = null) =>
        DailyNoteNaming.TryParseDate(note.FileNameWithoutExtension, template ?? ResolveTemplate(), out var date)
            ? date
            : null;

    /// <summary>La nota diaria de <paramref name="date"/> si ya existe en el vault, buscando en todas sus
    /// carpetas: la nota diaria es una sola por día, esté donde esté guardada.
    ///
    /// <para>Se reconoce por los dos nombres posibles —el que dicta la plantilla actual y el
    /// <c>yyyy-MM-dd</c> de siempre—, de modo que configurar una plantilla hoy no deja huérfanas las notas
    /// diarias de antes ni crea una segunda nota para un día que ya tenía la suya.</para></summary>
    public NoteFile? Find(DateOnly date, NoteTemplate? template = null)
    {
        template ??= ResolveTemplate();
        var expected = ExpectedFileNames(date, template).ToList();

        return _vaultService.GetAllNotesRecursive(_vaultService.VaultPath)
            .FirstOrDefault(note => expected.Any(name =>
                string.Equals(name, note.FileNameWithoutExtension, VaultService.PathComparison)));
    }

    /// <summary>Abre la nota diaria de <paramref name="date"/>, creándola en
    /// <paramref name="folderPath"/> si aún no existe. Al crear —y solo al crear— se aplica la plantilla
    /// configurada: volver a aplicarla sobre una nota que ya existe pisaría lo escrito, que es justo lo que
    /// evita <see cref="BuildMergedContent"/>.</summary>
    /// <exception cref="System.IO.IOException">Fallo de escritura en el vault.</exception>
    /// <exception cref="UnauthorizedAccessException">Sin permisos sobre el vault.</exception>
    /// <exception cref="SessionLockedException">La carpeta de destino es una sesión cifrada bloqueada.</exception>
    public DailyNoteOpening OpenOrCreate(DateOnly date, string folderPath)
    {
        var template = ResolveTemplate();
        if (Find(date, template) is { } existing) return new DailyNoteOpening(existing, Created: false, null);

        var title = DailyNoteNaming.TitleFor(date, template);
        // El cuerpo se expande aquí en vez de con NoteTemplateService.Prepare porque el nombre de la nota
        // diaria lo decide DailyNoteNaming, no el patrón de título tal cual: pasándole ese nombre como
        // {{title}}, lo que la plantilla escriba dentro de la nota coincide con cómo se llama el archivo.
        var rendered = template is null
            ? (NoteTemplateRenderResult?)null
            : NoteTemplateRenderer.Render(template.Body, title, ReferenceFor(date));

        var note = _vaultService.CreateNote(title, folderPath, rendered?.Content);
        return new DailyNoteOpening(note, Created: true, rendered?.CaretOffset);
    }

    /// <summary>Qué días del intervalo tienen nota diaria, con la nota de cada uno. Una sola pasada por el
    /// vault para todo el intervalo —el calendario de constancia pinta setenta días de golpe—, en vez de
    /// una búsqueda por día.</summary>
    public IReadOnlyDictionary<DateOnly, NoteFile> FindInRange(DateOnly from, DateOnly to)
    {
        if (to < from) (from, to) = (to, from);

        var template = ResolveTemplate();
        var found = new Dictionary<DateOnly, NoteFile>();

        foreach (var note in _vaultService.GetAllNotesRecursive(_vaultService.VaultPath))
        {
            if (!DailyNoteNaming.TryParseDate(note.FileNameWithoutExtension, template, out var date)) continue;
            if (date < from || date > to) continue;
            // Dos archivos para el mismo día (uno con el nombre de la plantilla y otro con el yyyy-MM-dd
            // anterior a configurarla) cuentan como un día con nota, no como dos.
            found.TryAdd(date, note);
        }

        return found;
    }

    // ===================== Combinar con la plantilla =====================

    /// <summary>Las secciones de la plantilla que le faltan a <paramref name="content"/>, ya expandidas
    /// para <paramref name="date"/> — lo que la acción "combinar con la plantilla" añadiría. Lista vacía si
    /// no hay plantilla configurada o la nota ya las tiene todas.</summary>
    public IReadOnlyList<DailyTemplateSection> PendingSections(DateOnly date, string? content)
    {
        var rendered = RenderFor(date);
        return rendered is null ? [] : DailyNoteTemplateMerge.MissingSections(content, rendered);
    }

    /// <summary>El contenido que debe quedar en la nota diaria de <paramref name="date"/> tras combinarla
    /// con su plantilla, o null si no hay plantilla o no hay nada que añadir.</summary>
    public string? BuildMergedContent(DateOnly date, string? content)
    {
        var rendered = RenderFor(date);
        return rendered is null ? null : DailyNoteTemplateMerge.Merge(content, rendered);
    }

    /// <summary>La plantilla de la nota diaria ya expandida para <paramref name="date"/>, o null si no hay
    /// ninguna configurada. El título con el que se expande es el de la nota de ese día, para que un
    /// <c>{{title}}</c> dentro del cuerpo valga lo mismo que valdría al crearla.</summary>
    private string? RenderFor(DateOnly date)
    {
        if (ResolveTemplate() is not { } template) return null;

        return NoteTemplateRenderer.Render(template.Body, DailyNoteNaming.TitleFor(date, template), ReferenceFor(date)).Content;
    }

    /// <summary>Los nombres de archivo con los que puede estar guardada la nota diaria de un día: el que
    /// dicta la plantilla —saneado como lo sanea <see cref="VaultService.CreateNote"/>, que es quien pone
    /// el nombre real— y el <c>yyyy-MM-dd</c> de siempre.</summary>
    private static IEnumerable<string> ExpectedFileNames(DateOnly date, NoteTemplate? template)
    {
        yield return VaultService.SanitizeFileName(DailyNoteNaming.TitleFor(date, template));
        if (template is not null) yield return DailyNoteNaming.FallbackTitleFor(date);
    }

    /// <summary>El instante con el que se expanden las variables del <b>cuerpo</b> de la plantilla para un
    /// día dado: el reloj real cuando es hoy —para que un <c>{{time}}</c> dentro de la plantilla marque la
    /// hora a la que se abrió el diario— y el mediodía para cualquier otro día, que no tiene una hora
    /// propia que registrar. El <b>nombre</b> de la nota no pasa por aquí: lo decide siempre
    /// <see cref="DailyNoteNaming"/>, que es deterministico por día para que buscarla y crearla no puedan
    /// discrepar.</summary>
    private static DateTime ReferenceFor(DateOnly date) =>
        date == Today ? DateTime.Now : date.ToDateTime(new TimeOnly(12, 0));
}
