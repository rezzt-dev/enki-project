using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using enki.Localization;
using enki.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace enki.Services;

/// <summary>Persiste las skills de IA (docs/ia-skills/especificacion-desarrollo.md) como archivos Markdown
/// con frontmatter YAML en <c>&lt;vault&gt;/.enki/skills/</c> — mismo patrón "persistencia directa a
/// archivo" que <see cref="VaultService"/>, sin capa de almacén JSON intermedia.</summary>
public sealed class SkillService
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>Carpeta de skills empaquetadas con la app (docs/ia-skills/especificacion-desarrollo.md §2) —
    /// copiadas junto al ejecutable vía el <c>Content</c> del <c>.csproj</c>. Un archivo cuyo nombre
    /// coincide con uno de aquí se considera "default" (ver <see cref="IsDefaultFileName"/>).</summary>
    private readonly string _defaultSkillsSourceDirectory;

    private readonly VaultService? _vaultService;
    private readonly string? _fixedSkillsDirectory;

    /// <summary>Carpeta donde se almacenan las skills del vault activo. Calculada en vivo a partir de
    /// <see cref="VaultService.VaultPath"/> (en vez de fijarse en el constructor) para que cambiar de
    /// vault desde Ajustes (<c>SettingsViewModel.ChangeVaultFolder</c>) apunte a las skills del vault
    /// nuevo sin tener que reconstruir el servicio.</summary>
    public string SkillsDirectory =>
        _vaultService is not null ? Path.Combine(_vaultService.VaultPath, ".enki", "skills") : _fixedSkillsDirectory!;

    /// <summary>Indica si la carpeta de skills existe y está inicializada.</summary>
    public bool IsInitialized => Directory.Exists(SkillsDirectory);

    /// <summary>Se dispara cuando cambia el conjunto de skills (import, delete, guardado...).</summary>
    public Action? SkillsChanged { get; set; }

    public SkillService(VaultService vaultService)
    {
        _vaultService = vaultService;
        _defaultSkillsSourceDirectory = Path.Combine(AppContext.BaseDirectory, "default-skills");
    }

    /// <param name="storageDirectory">La propia carpeta de skills — a diferencia de otros servicios
    /// (<c>KanbanService</c>, <c>NotificationService</c>), aquí no hay una carpeta de configuración
    /// intermedia: el parámetro apunta directo a lo que sería <c>&lt;vault&gt;/.enki/skills/</c>, así que
    /// los tests pueden usar un directorio temporal sin tocar ningún vault real.</param>
    public SkillService(string storageDirectory)
    {
        _fixedSkillsDirectory = storageDirectory;
        _defaultSkillsSourceDirectory = Path.Combine(AppContext.BaseDirectory, "default-skills");
    }

    /// <summary>Carga todas las skills de <see cref="SkillsDirectory"/>. Un archivo individual que no se
    /// pueda leer (permisos, corrupción) se omite en vez de abortar la carga completa.</summary>
    public List<SkillFile> LoadSkills()
    {
        if (!Directory.Exists(SkillsDirectory)) return [];

        return Directory.EnumerateFiles(SkillsDirectory, "*.md", SearchOption.TopDirectoryOnly)
            .Select(TryReadSkill)
            .OfType<SkillFile>()
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private SkillFile? TryReadSkill(string filePath)
    {
        try { return ReadSkill(filePath); }
        catch { return null; }
    }

    /// <summary>Lee una skill desde una ruta absoluta sin importarla al vault.</summary>
    public SkillFile ReadSkill(string filePath)
    {
        var content = File.ReadAllText(filePath);
        var id = Path.GetFileNameWithoutExtension(filePath);
        var isDefault = IsWithinSkillsDirectory(filePath) && IsDefaultFileName(Path.GetFileName(filePath));
        return ParseSkill(id, filePath, content, isDefault);
    }

    private static SkillFile ParseSkill(string id, string filePath, string content, bool isDefault)
    {
        var (yaml, _, _) = SplitFrontMatter(content);
        var frontMatter = TryParseFrontMatter(yaml);

        return new SkillFile
        {
            Id = id,
            Name = !string.IsNullOrWhiteSpace(frontMatter?.Name) ? frontMatter!.Name! : id,
            Description = frontMatter?.Description ?? string.Empty,
            Emoji = frontMatter?.Emoji,
            Color = frontMatter?.Color,
            Vibe = frontMatter?.Vibe,
            Tools = frontMatter?.Tools ?? [],
            Tags = frontMatter?.Tags ?? [],
            Permissions = frontMatter?.Permissions ?? [],
            Content = content,
            FilePath = filePath,
            IsDefault = isDefault
        };
    }

    /// <summary>Guarda el contenido Markdown de una skill existente. Si el contenido ya no trae
    /// frontmatter (el usuario lo borró al editar), se le añade uno mínimo con <c>name</c>,
    /// <c>description</c> y <c>permissions: []</c> antes de escribir. Dispara <see cref="SkillsChanged"/>.
    /// Escribe de forma atómica (fichero temporal + <see cref="File.Move(string, string, bool)"/>), mismo
    /// patrón que <see cref="VaultService.WriteContent"/> — antes usaba <see cref="File.WriteAllText(string, string)"/>
    /// directo, el mismo defecto estructural que tenía el contenido de notas
    /// (docs/analisis-proyecto/roadmap.md, Fase 1). Un fallo de E/S se registra en <see cref="LogService"/>
    /// y se propaga en vez de tragarse, para que el llamador (<c>SkillEditorViewModel.Save</c>) pueda
    /// reaccionar en vez de perder el cambio en silencio.</summary>
    public void SaveSkill(SkillFile skill, string content)
    {
        var (_, _, hasFrontMatter) = SplitFrontMatter(content);
        var finalContent = hasFrontMatter ? content : PrependMinimalFrontMatter(content, skill);

        try
        {
            var tempPath = $"{skill.FilePath}.tmp-{Guid.NewGuid():N}";
            File.WriteAllText(tempPath, finalContent);
            File.Move(tempPath, skill.FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error($"No se pudo guardar la skill '{skill.FilePath}'", ex);
            throw;
        }

        var reloaded = ParseSkill(skill.Id, skill.FilePath, finalContent, skill.IsDefault);
        skill.Name = reloaded.Name;
        skill.Description = reloaded.Description;
        skill.Emoji = reloaded.Emoji;
        skill.Color = reloaded.Color;
        skill.Vibe = reloaded.Vibe;
        skill.Tools = reloaded.Tools;
        skill.Tags = reloaded.Tags;
        skill.Permissions = reloaded.Permissions;
        skill.Content = finalContent;

        SkillsChanged?.Invoke();
    }

    private static string PrependMinimalFrontMatter(string content, SkillFile skill)
    {
        var frontMatter = new Dictionary<string, object>
        {
            ["name"] = string.IsNullOrWhiteSpace(skill.Name) ? skill.Id : skill.Name,
            ["description"] = skill.Description,
            ["permissions"] = new List<string>()
        };
        var yaml = YamlSerializer.Serialize(frontMatter).TrimEnd();
        return $"---\n{yaml}\n---\n\n{content}";
    }

    /// <summary>Crea una copia editable de una skill con un nuevo id/nombre de archivo — usado tanto para
    /// "Duplicar" como para el flujo de editar una skill default (docs/ia-skills/especificacion-desarrollo.md
    /// §11, "Editar una skill default").</summary>
    public SkillFile DuplicateSkill(SkillFile source, string newId)
    {
        Directory.CreateDirectory(SkillsDirectory);

        var safeId = SanitizeId(newId);
        var destination = Path.Combine(SkillsDirectory, $"{safeId}.md");
        if (File.Exists(destination))
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Skills_Error_NameExists"], safeId));

        File.WriteAllText(destination, source.Content);
        SkillsChanged?.Invoke();
        return ReadSkill(destination);
    }

    /// <summary>Elimina una skill del disco. No permite eliminar skills default.</summary>
    public void DeleteSkill(SkillFile skill)
    {
        if (skill.IsDefault)
            throw new InvalidOperationException(LocalizationService.Instance["Skills_Error_CannotDeleteDefault"]);

        if (File.Exists(skill.FilePath)) File.Delete(skill.FilePath);
        SkillsChanged?.Invoke();
    }

    /// <summary>Importa archivos/carpetas al vault (solo <c>.md</c>, el resto se ignora en silencio si el
    /// origen es una carpeta). <paramref name="defaultResolution"/> se aplica a toda colisión de nombre
    /// encontrada en esta llamada — la UI (<c>SkillsViewModel</c>) decide ese valor preguntando al usuario
    /// antes de invocar este método. Devuelve el resumen de importados/omitidos.</summary>
    public SkillImportResult Import(IReadOnlyList<string> paths, ConflictResolution defaultResolution)
    {
        Directory.CreateDirectory(SkillsDirectory);

        var result = new SkillImportResult();
        foreach (var sourcePath in ExpandMarkdownFiles(paths))
        {
            var fileName = Path.GetFileName(sourcePath);
            var destination = Path.Combine(SkillsDirectory, fileName);

            if (File.Exists(destination))
            {
                if (defaultResolution == ConflictResolution.Cancel)
                {
                    result.Skipped.Add(fileName);
                    continue;
                }
                if (defaultResolution == ConflictResolution.Rename)
                {
                    destination = MakeUniqueDestination(destination);
                }
                // Overwrite: se mantiene destination tal cual, File.Copy con overwrite=true la sobreescribe.
            }

            File.Copy(sourcePath, destination, overwrite: true);
            result.Imported.Add(Path.GetFileName(destination));
        }

        if (result.Imported.Count > 0) SkillsChanged?.Invoke();
        return result;
    }

    private static IEnumerable<string> ExpandMarkdownFiles(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*.md", SearchOption.TopDirectoryOnly))
                    yield return file;
            }
            else if (File.Exists(path) && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static string MakeUniqueDestination(string destination)
    {
        var directory = Path.GetDirectoryName(destination)!;
        var baseName = Path.GetFileNameWithoutExtension(destination);
        var extension = Path.GetExtension(destination);
        var suffix = 2;
        var candidate = destination;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{baseName} {suffix}{extension}");
            suffix++;
        }
        return candidate;
    }

    /// <summary>Exporta una lista de skills a la carpeta destino, conservando su nombre de archivo original.</summary>
    public static void Export(IReadOnlyList<SkillFile> skills, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var skill in skills)
        {
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(skill.FilePath));
            File.Copy(skill.FilePath, destination, overwrite: true);
        }
    }

    /// <summary>Copia las skills empaquetadas en <see cref="_defaultSkillsSourceDirectory"/> a
    /// <see cref="SkillsDirectory"/> — solo si esta última no existe o está vacía, para no resucitar
    /// skills que el usuario borró deliberadamente.</summary>
    public void EnsureDefaultSkills()
    {
        if (!Directory.Exists(_defaultSkillsSourceDirectory)) return;

        var alreadyHasSkills = Directory.Exists(SkillsDirectory) &&
            Directory.EnumerateFiles(SkillsDirectory, "*.md", SearchOption.TopDirectoryOnly).Any();
        if (alreadyHasSkills) return;

        Directory.CreateDirectory(SkillsDirectory);
        foreach (var source in Directory.EnumerateFiles(_defaultSkillsSourceDirectory, "*.md"))
        {
            var destination = Path.Combine(SkillsDirectory, Path.GetFileName(source));
            File.Copy(source, destination, overwrite: true);
        }

        SkillsChanged?.Invoke();
    }

    private bool IsWithinSkillsDirectory(string filePath) =>
        string.Equals(
            Path.GetDirectoryName(Path.GetFullPath(filePath)),
            Path.GetFullPath(SkillsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private bool IsDefaultFileName(string fileName) =>
        Directory.Exists(_defaultSkillsSourceDirectory) &&
        File.Exists(Path.Combine(_defaultSkillsSourceDirectory, fileName));

    /// <summary>Separa el frontmatter YAML (entre delimitadores <c>---</c>) del cuerpo Markdown. Devuelve
    /// <c>HasFrontMatter = false</c> si el contenido no empieza con un delimitador o no tiene cierre.</summary>
    private static (string? Yaml, string Body, bool HasFrontMatter) SplitFrontMatter(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return (null, content, false);

        var closingIndex = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (closingIndex < 0) return (null, content, false);

        var yaml = string.Join('\n', lines[1..closingIndex]);
        var body = string.Join('\n', lines[(closingIndex + 1)..]);
        return (yaml, body, true);
    }

    private static SkillFrontMatter? TryParseFrontMatter(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;
        try { return YamlDeserializer.Deserialize<SkillFrontMatter>(yaml); }
        catch { return null; }
    }

    private static string SanitizeId(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? Guid.NewGuid().ToString("N") : cleaned;
    }

    /// <summary>Forma tipada del frontmatter YAML, usada solo para (de)serializar — los campos
    /// desconocidos se ignoran gracias a <c>IgnoreUnmatchedProperties</c> en <see cref="YamlDeserializer"/>.</summary>
    private sealed class SkillFrontMatter
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Emoji { get; set; }
        public string? Color { get; set; }
        public string? Vibe { get; set; }
        public List<string>? Tools { get; set; }
        public List<string>? Tags { get; set; }
        public List<string>? Permissions { get; set; }
    }
}

/// <summary>Cómo resolver un nombre de archivo que ya existe en <see cref="SkillService.SkillsDirectory"/>
/// al importar.</summary>
public enum ConflictResolution
{
    /// <summary>No importar el archivo en conflicto.</summary>
    Cancel,

    /// <summary>Importar con un sufijo numérico añadido al nombre.</summary>
    Rename,

    /// <summary>Sobrescribir el archivo existente.</summary>
    Overwrite
}

/// <summary>Resumen de una llamada a <see cref="SkillService.Import"/>.</summary>
public sealed class SkillImportResult
{
    /// <summary>Nombres de archivo efectivamente copiados a <see cref="SkillService.SkillsDirectory"/>.</summary>
    public List<string> Imported { get; } = [];

    /// <summary>Nombres de archivo que colisionaban y se omitieron por <see cref="ConflictResolution.Cancel"/>.</summary>
    public List<string> Skipped { get; } = [];
}
