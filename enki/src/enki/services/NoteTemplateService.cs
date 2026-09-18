using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using enki.Localization;
using enki.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace enki.Services;

/// <summary>Lo que <see cref="NoteTemplateService.Prepare"/> entrega a quien va a crear la nota: el título
/// ya resuelto, el Markdown ya expandido y dónde dejar el cursor.</summary>
/// <param name="Title">Título (y por tanto nombre de archivo) que debe recibir la nota nueva.</param>
/// <param name="Content">Contenido Markdown ya sin variables.</param>
/// <param name="CaretOffset">Posición del cursor pedida por la plantilla, o null.</param>
public readonly record struct NoteTemplateDraft(string Title, string Content, int? CaretOffset);

/// <summary>Plantillas de notas reutilizables, guardadas como <c>.md</c> en
/// <c>&lt;vault&gt;/.enki/templates/</c> — misma convención "carpeta oculta dentro del vault" que
/// <c>.enki/skills/</c> (<see cref="SkillService"/>), <c>.trash</c> y <c>.sessions</c>, y por el mismo
/// motivo práctico: <see cref="VaultService"/> ya excluye de todo recorrido de notas cualquier carpeta que
/// empiece por punto, así que una plantilla nunca aparece como nota en la rejilla, la búsqueda, el grafo
/// de sesiones ni los backlinks sin necesidad de ninguna exclusión especial.
///
/// <para>Igual que las skills, una plantilla es un archivo de texto y nada más: el usuario puede crearlas,
/// editarlas, versionarlas o sincronizarlas con sus propias herramientas fuera de Enki, y la app las
/// vuelve a leer del disco en cada apertura del selector en vez de mantener una copia en JSON que pudiera
/// quedar desincronizada.</para></summary>
public sealed class NoteTemplateService
{
    /// <summary>Nombre fijo (no traducido) de la carpeta, por el mismo motivo que
    /// <c>VaultService.VaultSubfolderName</c>: un vault creado con la app en un idioma tiene que seguir
    /// reconociéndose al cambiar de idioma.</summary>
    public const string TemplatesFolderName = "templates";

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly VaultService? _vaultService;
    private readonly string? _fixedTemplatesDirectory;

    /// <summary>Carpeta de plantillas del vault activo, calculada en vivo a partir de
    /// <see cref="VaultService.VaultPath"/> —no fijada en el constructor— para que cambiar de vault desde
    /// Ajustes apunte a las plantillas del vault nuevo sin reconstruir el servicio. Mismo criterio que
    /// <see cref="SkillService.SkillsDirectory"/>.</summary>
    public string TemplatesDirectory =>
        _vaultService is not null
            ? Path.Combine(_vaultService.VaultPath, ".enki", TemplatesFolderName)
            : _fixedTemplatesDirectory!;

    /// <summary>Se dispara cuando cambia el conjunto de plantillas (creación, borrado, siembra inicial),
    /// para que Ajustes refresque su desplegable de la nota diaria sin tener que preguntar. Mismo contrato
    /// que <see cref="SkillService.SkillsChanged"/>.</summary>
    public Action? TemplatesChanged { get; set; }

    public NoteTemplateService(VaultService vaultService) => _vaultService = vaultService;

    /// <param name="storageDirectory">La propia carpeta de plantillas, sin carpeta de configuración
    /// intermedia — igual que <see cref="SkillService(string)"/>, para que los tests usen un directorio
    /// temporal sin tocar ningún vault real.</param>
    public NoteTemplateService(string storageDirectory) => _fixedTemplatesDirectory = storageDirectory;

    // ===================== Siembra =====================

    /// <summary>Crea la carpeta de plantillas y siembra los dos ejemplos traducidos si —y solo si— la
    /// carpeta no existía todavía.
    ///
    /// <para>La marca de "ya sembrado" es la existencia de la carpeta, no que esté vacía (el criterio de
    /// <see cref="SkillService.EnsureDefaultSkills"/>): una biblioteca de skills vacía es un estado
    /// transitorio, pero un usuario que borra todas sus plantillas y se queda con la carpeta vacía está
    /// diciendo justo que no quiere plantillas, y volver a sembrarlas en el siguiente arranque sería
    /// devolverle lo que acaba de tirar.</para></summary>
    public void EnsureInitialized()
    {
        if (string.IsNullOrWhiteSpace(TemplatesDirectory)) return;
        if (Directory.Exists(TemplatesDirectory)) return;

        try
        {
            Directory.CreateDirectory(TemplatesDirectory);
            WriteTemplateFile(
                Path.Combine(TemplatesDirectory, "daily.md"),
                LocalizationService.Instance["Templates_Seed_Daily_Name"],
                LocalizationService.Instance["Templates_Seed_Daily_Description"],
                // Título fijo, no traducible: es el nombre del archivo de la nota diaria, el mismo
                // yyyy-MM-dd invariante que ya usaba Documentos antes de existir las plantillas.
                "{{date}}",
                LocalizationService.Instance["Templates_Seed_Daily_Body"]);
            WriteTemplateFile(
                Path.Combine(TemplatesDirectory, "meeting.md"),
                LocalizationService.Instance["Templates_Seed_Meeting_Name"],
                LocalizationService.Instance["Templates_Seed_Meeting_Description"],
                titlePattern: null,
                LocalizationService.Instance["Templates_Seed_Meeting_Body"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Un vault de solo lectura o un disco lleno no deben impedir arrancar: sin carpeta de
            // plantillas, el selector muestra su estado vacío y el resto de la app sigue igual.
            LogService.Error($"No se pudo inicializar la carpeta de plantillas '{TemplatesDirectory}'", ex);
            return;
        }

        TemplatesChanged?.Invoke();
    }

    // ===================== Lectura =====================

    /// <summary>Todas las plantillas del vault, incluidas las de subcarpetas que el usuario haya creado
    /// para organizarlas, ordenadas por ruta relativa. Una plantilla ilegible (permisos, archivo a medio
    /// escribir por otra herramienta) se omite en vez de abortar la carga entera, igual que
    /// <see cref="SkillService.LoadSkills"/>.</summary>
    public List<NoteTemplate> LoadTemplates()
    {
        if (!Directory.Exists(TemplatesDirectory)) return [];

        try
        {
            return Directory.EnumerateFiles(TemplatesDirectory, "*.md", SearchOption.AllDirectories)
                .Select(TryRead)
                .OfType<NoteTemplate>()
                .OrderBy(t => t.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error($"No se pudieron enumerar las plantillas de '{TemplatesDirectory}'", ex);
            return [];
        }
    }

    /// <summary>La plantilla identificada por su <see cref="NoteTemplate.RelativePath"/>, o null si ya no
    /// existe — el caso normal de un ajuste que apunta a una plantilla que el usuario borró después, que
    /// cada consumidor trata como "sin plantilla" en vez de como un error.</summary>
    public NoteTemplate? FindTemplate(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;

        var normalized = relativePath.Replace('\\', '/').Trim();
        return LoadTemplates().FirstOrDefault(t =>
            string.Equals(t.RelativePath, normalized, VaultService.PathComparison));
    }

    private NoteTemplate? TryRead(string filePath)
    {
        try { return Read(filePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error($"No se pudo leer la plantilla '{filePath}'", ex);
            return null;
        }
    }

    /// <summary>Lee una plantilla concreta del disco.</summary>
    public NoteTemplate Read(string filePath)
    {
        var content = File.ReadAllText(filePath);
        var (yaml, body) = SplitFrontMatter(content);
        var frontMatter = TryParseFrontMatter(yaml);
        var id = Path.GetFileNameWithoutExtension(filePath);

        return new NoteTemplate
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(frontMatter?.Name) ? id : frontMatter!.Name!.Trim(),
            Description = frontMatter?.Description?.Trim() ?? string.Empty,
            TitlePattern = string.IsNullOrWhiteSpace(frontMatter?.Title) ? null : frontMatter!.Title!.Trim(),
            FilePath = filePath,
            RelativePath = RelativePathOf(filePath),
            Body = body
        };
    }

    // ===================== Aplicar =====================

    /// <summary>Expande <paramref name="template"/> para una nota nueva.
    ///
    /// <para>El título sale del <c>title</c> del frontmatter si la plantilla lo declara, y si no de
    /// <paramref name="fallbackTitle"/> — lo que permite que la misma llamada sirva a los dos flujos:
    /// "nueva nota desde plantilla" pasa el nombre de la plantilla como respaldo y la nota diaria pasa la
    /// fecha. Dentro del propio patrón de título, <c>{{title}}</c> vale ese mismo respaldo, así que
    /// <c>title: "{{date}} · {{title}}"</c> produce "2026-09-18 · Reunión" sin recursión posible.</para>
    ///
    /// <para>Un mismo instante <paramref name="now"/> se reparte entre título y cuerpo: si no, una nota
    /// creada a las 23:59:59.9 podría acabar con el nombre de un día y el contenido del siguiente.</para></summary>
    public static NoteTemplateDraft Prepare(NoteTemplate template, string fallbackTitle, DateTime? now = null)
    {
        var reference = now ?? DateTime.Now;
        var title = ResolveTitle(template.TitlePattern, fallbackTitle, reference);
        var rendered = NoteTemplateRenderer.Render(template.Body, title, reference);
        return new NoteTemplateDraft(title, rendered.Content, rendered.CaretOffset);
    }

    /// <summary>Solo el título que produciría <see cref="Prepare"/> a partir de un patrón, sin expandir
    /// ningún cuerpo — lo que necesita la nota diaria para saber cómo se llama la nota de un día cualquiera
    /// (el de ayer, el de mañana, los setenta del calendario de constancia) sin renderizar setenta veces
    /// una plantilla cuyo cuerpo va a descartar. Toma el patrón y no la <see cref="NoteTemplate"/> entera
    /// porque <see cref="DailyNoteNaming"/> le pasa una variante del patrón de la plantilla, no el patrón
    /// tal cual. <see cref="Prepare"/> lo usa a su vez, así que las dos vías no pueden divergir.</summary>
    public static string ResolveTitle(string? titlePattern, string fallbackTitle, DateTime? now = null)
    {
        var title = titlePattern is null
            ? fallbackTitle
            : NoteTemplateRenderer.Render(titlePattern, fallbackTitle, now ?? DateTime.Now).Content.Trim();
        return string.IsNullOrWhiteSpace(title) ? fallbackTitle : title;
    }

    // ===================== Escritura =====================

    /// <summary>Crea una plantilla nueva con un frontmatter y un cuerpo de ejemplo ya rellenos —la vía por
    /// la que el usuario descubre la sintaxis sin salir de la app— y devuelve la plantilla ya releída para
    /// que quien la pide pueda abrirla en el editor. El nombre se resuelve con sufijo numérico si ya
    /// existe, misma política de colisión que <see cref="VaultService.CreateNote"/>.</summary>
    public NoteTemplate CreateTemplate(string? desiredName = null)
    {
        Directory.CreateDirectory(TemplatesDirectory);

        var name = string.IsNullOrWhiteSpace(desiredName)
            ? LocalizationService.Instance["Templates_NewTemplate_Name"]
            : desiredName.Trim();
        var safeName = SanitizeFileName(name);

        var path = Path.Combine(TemplatesDirectory, $"{safeName}.md");
        var suffix = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(TemplatesDirectory, $"{safeName} {suffix}.md");
            suffix++;
        }

        WriteTemplateFile(path, name, LocalizationService.Instance["Templates_NewTemplate_Description"],
            titlePattern: null, LocalizationService.Instance["Templates_NewTemplate_Body"]);
        TemplatesChanged?.Invoke();
        return Read(path);
    }

    /// <summary>Borra la plantilla del disco. A diferencia de una nota, no pasa por la papelera del vault:
    /// esa papelera es la de las notas del usuario (<see cref="VaultService.DeleteNote"/> registra de
    /// dónde salió cada archivo para poder restaurarlo allí), y meter en ella archivos de una carpeta
    /// interna la convertiría en un cajón de sastre. Quien llama confirma antes.</summary>
    public void DeleteTemplate(NoteTemplate template)
    {
        if (File.Exists(template.FilePath)) File.Delete(template.FilePath);
        TemplatesChanged?.Invoke();
    }

    /// <summary>Escribe un archivo de plantilla completo (frontmatter + cuerpo) de forma atómica —fichero
    /// temporal y <see cref="File.Move(string, string, bool)"/>—, mismo patrón que
    /// <see cref="VaultService.WriteContent"/> y <see cref="SkillService.SaveSkill"/>: nadie llega a leer
    /// una plantilla a medio escribir.</summary>
    private static void WriteTemplateFile(string path, string name, string description, string? titlePattern, string body)
    {
        var frontMatter = new List<string>
        {
            "---",
            $"name: {YamlScalar(name)}",
            $"description: {YamlScalar(description)}"
        };
        if (titlePattern is not null) frontMatter.Add($"title: {YamlScalar(titlePattern)}");
        frontMatter.Add("---");

        var content = string.Join('\n', frontMatter) + "\n\n" + body.TrimStart('\n') + "\n";

        var tempPath = $"{path}.tmp-{Guid.NewGuid():N}";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>Entrecomillado YAML de un escalar. Se cita siempre en vez de solo cuando hace falta: los
    /// valores que escribimos aquí son traducciones y patrones con llaves (<c>{{date}}</c>), y varios de
    /// los caracteres que YAML trata de forma especial —<c>:</c>, <c>#</c>, <c>{</c>— aparecen en ellos con
    /// toda naturalidad.</summary>
    private static string YamlScalar(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // ===================== Utilidades =====================

    private string RelativePathOf(string filePath)
    {
        var relative = Path.GetRelativePath(TemplatesDirectory, Path.GetFullPath(filePath));
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "template" : cleaned;
    }

    /// <summary>Separa el frontmatter YAML (entre delimitadores <c>---</c>) del cuerpo Markdown, con las
    /// mismas reglas que <c>SkillService.SplitFrontMatter</c>: sin apertura en la primera línea o sin
    /// cierre, todo el contenido es cuerpo. El cuerpo se devuelve sin los saltos de línea que separaban el
    /// frontmatter, para que la nota creada no empiece con líneas en blanco de regalo.</summary>
    private static (string? Yaml, string Body) SplitFrontMatter(string content)
    {
        var normalized = content.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return (null, normalized);

        var closingIndex = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (closingIndex < 0) return (null, normalized);

        var yaml = string.Join('\n', lines[1..closingIndex]);
        var body = string.Join('\n', lines[(closingIndex + 1)..]).TrimStart('\n');
        return (yaml, body);
    }

    private static TemplateFrontMatter? TryParseFrontMatter(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;
        try { return YamlDeserializer.Deserialize<TemplateFrontMatter>(yaml); }
        catch (YamlDotNet.Core.YamlException) { return null; }
    }

    /// <summary>Forma tipada del frontmatter, solo para deserializar; los campos desconocidos se ignoran
    /// (<c>IgnoreUnmatchedProperties</c>), así que una plantilla con metadatos propios del usuario se
    /// sigue leyendo sin quejas.</summary>
    private sealed class TemplateFrontMatter
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Title { get; set; }
    }
}
