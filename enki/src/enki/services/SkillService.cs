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

/// <summary>Persiste las skills de IA (docs/agent-docs/ia-skills/especificacion-desarrollo.md) como
/// archivos Markdown con frontmatter YAML en <c>&lt;vault&gt;/.enki/skills/</c> — mismo patrón
/// "persistencia directa a archivo" que <see cref="VaultService"/>, sin capa de almacén JSON intermedia.
///
/// La biblioteca es un árbol: el usuario puede crear carpetas y subcarpetas dentro de esa ruta para
/// agrupar skills por tema, y este servicio enumera y opera recursivamente sobre ellas. Las operaciones
/// de carpeta (crear/renombrar/eliminar/mover) viven aquí y no se delegan en las de
/// <see cref="VaultService"/> porque el almacén es otro: aquéllas mueven a la papelera del vault, que no
/// tiene nada que ver con esta carpeta interna, y aquí sí hay una regla propia — una carpeta que contiene
/// skills default no se puede eliminar.</summary>
public sealed class SkillService
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>Carpeta de skills empaquetadas con la app (docs/agent-docs/ia-skills/especificacion-desarrollo.md §2) —
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

    /// <summary>Se dispara cuando cambia el conjunto de skills (import, delete, guardado, mover...).</summary>
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

    // ===================== Lectura =====================

    /// <summary>Carga toda la biblioteca, incluidas las skills que viven dentro de subcarpetas — la
    /// sección necesita el conjunto completo para buscar, filtrar por etiquetas, exportar y contar, y es
    /// la vista (carpeta actual, árbol) la que decide qué subconjunto pinta. Un archivo individual que no
    /// se pueda leer (permisos, corrupción) se omite en vez de abortar la carga completa.</summary>
    public List<SkillFile> LoadSkills()
    {
        if (!Directory.Exists(SkillsDirectory)) return [];

        return Directory.EnumerateFiles(SkillsDirectory, "*.md", SearchOption.AllDirectories)
            .Select(TryReadSkill)
            .OfType<SkillFile>()
            .OrderBy(s => s.RelativeFolderPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Skills que cuelgan directamente de <paramref name="folderPath"/>, sin descender a sus
    /// subcarpetas — lo que pinta la rejilla del modo explorador para la carpeta actual.</summary>
    public List<SkillFile> LoadSkillsIn(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return [];

        return Directory.EnumerateFiles(folderPath, "*.md", SearchOption.TopDirectoryOnly)
            .Select(TryReadSkill)
            .OfType<SkillFile>()
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Árbol de carpetas bajo <paramref name="root"/> (por defecto, la raíz de la biblioteca),
    /// con los contadores de skills ya resueltos. Mismo contrato que
    /// <see cref="VaultService.GetFolderTree"/>: devuelve lista vacía si la ruta no existe todavía, para
    /// que la sección no tenga que comprobar antes si la biblioteca está inicializada.</summary>
    public List<SkillFolder> GetFolderTree(string? root = null)
    {
        root ??= SkillsDirectory;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return [];

        return BuildFolderChildren(root);
    }

    private static List<SkillFolder> BuildFolderChildren(string path)
    {
        var folders = new List<SkillFolder>();
        foreach (var directory in Directory.EnumerateDirectories(path).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var folder = new SkillFolder { FullPath = directory };
            folder.Children = BuildFolderChildren(directory);
            folder.SkillCount = CountMarkdown(directory, SearchOption.TopDirectoryOnly);
            folder.TotalSkillCount = CountMarkdown(directory, SearchOption.AllDirectories);
            folders.Add(folder);
        }
        return folders;
    }

    private static int CountMarkdown(string directory, SearchOption option)
    {
        try { return Directory.EnumerateFiles(directory, "*.md", option).Count(); }
        catch (UnauthorizedAccessException) { return 0; }
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
        return ParseSkill(id, filePath, content, isDefault, RelativeFolderOf(filePath));
    }

    /// <summary>Carpeta de <paramref name="filePath"/> relativa a la biblioteca, con <c>/</c> como
    /// separador y cadena vacía en la raíz. Devuelve cadena vacía también para un archivo que esté fuera
    /// de la biblioteca (el caso de <see cref="ReadSkill"/> sobre un archivo aún sin importar).</summary>
    private string RelativeFolderOf(string filePath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (folder is null) return string.Empty;

        var root = Path.GetFullPath(SkillsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!folder.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return string.Empty;

        var relative = Path.GetRelativePath(root, folder);
        return relative == "." ? string.Empty : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static SkillFile ParseSkill(string id, string filePath, string content, bool isDefault, string relativeFolderPath)
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
            FolderPath = Path.GetDirectoryName(filePath) ?? string.Empty,
            RelativeFolderPath = relativeFolderPath,
            IsDefault = isDefault
        };
    }

    // ===================== Escritura de una skill =====================

    /// <summary>Guarda el contenido Markdown de una skill existente. Si el contenido ya no trae
    /// frontmatter (el usuario lo borró al editar), se le añade uno mínimo con <c>name</c>,
    /// <c>description</c> y <c>permissions: []</c> antes de escribir. Dispara <see cref="SkillsChanged"/>.
    /// Escribe de forma atómica (fichero temporal + <see cref="File.Move(string, string, bool)"/>), mismo
    /// patrón que <see cref="VaultService.WriteContent"/> — antes usaba <see cref="File.WriteAllText(string, string)"/>
    /// directo, el mismo defecto estructural que tenía el contenido de notas
    /// (docs/agent-docs/analisis-proyecto/roadmap.md, Fase 1). Un fallo de E/S se registra en
    /// <see cref="LogService"/> y se propaga en vez de tragarse, para que el llamador
    /// (<c>SkillEditorViewModel.Save</c>) pueda reaccionar en vez de perder el cambio en silencio.</summary>
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

        var reloaded = ParseSkill(skill.Id, skill.FilePath, finalContent, skill.IsDefault, skill.RelativeFolderPath);
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
    /// "Duplicar" como para el flujo de editar una skill default
    /// (docs/agent-docs/ia-skills/especificacion-desarrollo.md §11, "Editar una skill default"). La copia
    /// nace en la misma carpeta que el original, no en la raíz: duplicar una skill que el usuario colocó
    /// dentro de un tema debe dejar las dos juntas.</summary>
    public SkillFile DuplicateSkill(SkillFile source, string newId)
    {
        var targetFolder = string.IsNullOrEmpty(source.FolderPath) ? SkillsDirectory : source.FolderPath;
        Directory.CreateDirectory(targetFolder);

        var safeId = SanitizeId(newId);
        var destination = Path.Combine(targetFolder, $"{safeId}.md");
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

    /// <summary>Mueve una skill a otra carpeta de la biblioteca (arrastrar y soltar sobre una fila del
    /// árbol o sobre la raíz). Un nombre ya ocupado en el destino se resuelve con sufijo numérico, misma
    /// política que <see cref="Import"/>/<see cref="VaultService.RenameFolder"/>, para que mover nunca
    /// pise una skill que ya estaba allí. Devuelve la skill releída desde su nueva ruta.</summary>
    public SkillFile MoveSkill(SkillFile skill, string destinationFolderPath)
    {
        if (PathsEqual(skill.FolderPath, destinationFolderPath)) return skill;

        Directory.CreateDirectory(destinationFolderPath);
        var destination = Path.Combine(destinationFolderPath, Path.GetFileName(skill.FilePath));
        if (File.Exists(destination)) destination = MakeUniqueDestination(destination);

        File.Move(skill.FilePath, destination);
        SkillsChanged?.Invoke();
        return ReadSkill(destination);
    }

    // ===================== Carpetas =====================

    /// <summary>Crea una subcarpeta dentro de <paramref name="parentPath"/>, con sufijo numérico si el
    /// nombre ya existe — misma política de colisión que <see cref="VaultService.CreateFolder"/>.</summary>
    public SkillFolder CreateFolder(string parentPath, string? desiredName = null)
    {
        var safeName = SanitizeId(desiredName ?? LocalizationService.Instance["Skills_DefaultFolderName"]);
        var path = Path.Combine(parentPath, safeName);
        var suffix = 2;
        while (Directory.Exists(path))
        {
            path = Path.Combine(parentPath, $"{safeName} {suffix}");
            suffix++;
        }

        Directory.CreateDirectory(path);
        SkillsChanged?.Invoke();
        return new SkillFolder { FullPath = path };
    }

    /// <summary>Renombra una carpeta, añadiendo sufijo numérico en vez de no hacer nada si el nombre ya
    /// está ocupado — mismo criterio "sin renombrados fantasma" que
    /// <see cref="VaultService.RenameFolder"/>.</summary>
    public SkillFolder RenameFolder(SkillFolder folder, string newName)
    {
        var safeName = SanitizeId(newName);
        var parent = Path.GetDirectoryName(folder.FullPath);
        if (parent is null) return folder;

        var newPath = Path.Combine(parent, safeName);
        if (PathsEqual(newPath, folder.FullPath)) return folder;

        var suffix = 2;
        while (Directory.Exists(newPath))
        {
            newPath = Path.Combine(parent, $"{safeName} {suffix}");
            suffix++;
        }

        Directory.Move(folder.FullPath, newPath);
        folder.FullPath = newPath;
        SkillsChanged?.Invoke();
        return folder;
    }

    /// <summary>Elimina una carpeta y todo lo que contiene. Se niega si dentro cuelga alguna skill
    /// default: si no, borrar la carpeta sería la puerta trasera para eliminar justo lo que
    /// <see cref="DeleteSkill"/> protege. A diferencia de <see cref="VaultService.DeleteFolder"/> no hay
    /// papelera intermedia — la biblioteca de skills no tiene una — así que quien llama debe confirmar
    /// antes (<c>SkillsViewModel.RequestDeleteFolder</c>).</summary>
    public void DeleteFolder(SkillFolder folder)
    {
        if (!Directory.Exists(folder.FullPath)) return;

        var blocking = Directory.EnumerateFiles(folder.FullPath, "*.md", SearchOption.AllDirectories)
            .FirstOrDefault(file => IsDefaultFileName(Path.GetFileName(file)));
        if (blocking is not null)
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture,
                LocalizationService.Instance["Skills_Error_FolderHasDefault"], Path.GetFileName(blocking)));

        Directory.Delete(folder.FullPath, recursive: true);
        SkillsChanged?.Invoke();
    }

    /// <summary>Mueve una carpeta dentro de otra. Se niega a meter una carpeta dentro de sí misma o de
    /// una de sus descendientes, que dejaría el árbol (y el <c>Directory.Move</c>) en un estado
    /// inconsistente.</summary>
    public SkillFolder MoveFolder(SkillFolder folder, string destinationParentPath)
    {
        var source = Path.GetFullPath(folder.FullPath);
        var destinationParent = Path.GetFullPath(destinationParentPath);

        if (PathsEqual(Path.GetDirectoryName(source), destinationParent)) return folder;
        if (PathsEqual(source, destinationParent) || IsUnder(destinationParent, source))
            throw new InvalidOperationException(LocalizationService.Instance["Skills_Error_FolderIntoItself"]);

        Directory.CreateDirectory(destinationParent);
        var newPath = Path.Combine(destinationParent, Path.GetFileName(source));
        var suffix = 2;
        while (Directory.Exists(newPath))
        {
            newPath = Path.Combine(destinationParent, $"{Path.GetFileName(source)} {suffix}");
            suffix++;
        }

        Directory.Move(source, newPath);
        folder.FullPath = newPath;
        SkillsChanged?.Invoke();
        return folder;
    }

    // ===================== Importar / exportar =====================

    /// <summary>Importa archivos/carpetas a la biblioteca (solo <c>.md</c>, el resto se ignora en
    /// silencio). Si el origen es una carpeta, sus <b>subcarpetas se recrean</b> bajo el destino: la
    /// organización que el usuario ya tenía fuera de Enki es exactamente la que esta funcionalidad existe
    /// para conservar. <paramref name="targetFolderPath"/> es la carpeta activa de la sección (null = la
    /// raíz de la biblioteca) y <paramref name="defaultResolution"/> se aplica a toda colisión de nombre
    /// encontrada en esta llamada — la UI (<c>SkillsViewModel</c>) decide ese valor preguntando al
    /// usuario antes de invocar este método. Devuelve el resumen de importados/omitidos, con rutas
    /// relativas al destino para que dos archivos homónimos en subcarpetas distintas no se confundan.</summary>
    public SkillImportResult Import(IReadOnlyList<string> paths, ConflictResolution defaultResolution, string? targetFolderPath = null)
    {
        var target = string.IsNullOrWhiteSpace(targetFolderPath) ? SkillsDirectory : targetFolderPath;
        Directory.CreateDirectory(target);

        var result = new SkillImportResult();
        foreach (var (sourcePath, relativeDestination) in ExpandMarkdownFiles(paths))
        {
            var destination = Path.Combine(target, relativeDestination);

            if (File.Exists(destination))
            {
                if (defaultResolution == ConflictResolution.Cancel)
                {
                    result.Skipped.Add(relativeDestination);
                    continue;
                }
                if (defaultResolution == ConflictResolution.Rename)
                {
                    destination = MakeUniqueDestination(destination);
                }
                // Overwrite: se mantiene destination tal cual, File.Copy con overwrite=true la sobreescribe.
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourcePath, destination, overwrite: true);
            result.Imported.Add(ToRelativeKey(target, destination));
        }

        if (result.Imported.Count > 0) SkillsChanged?.Invoke();
        return result;
    }

    /// <summary>Pares (origen, ruta relativa de destino) de todo el Markdown alcanzable desde
    /// <paramref name="paths"/>. Un archivo suelto aporta solo su nombre; una carpeta aporta la ruta
    /// relativa a ella misma, de modo que su primer nivel aterriza directamente en el destino y sus
    /// subcarpetas se recrean por debajo.</summary>
    public static IEnumerable<(string SourcePath, string RelativeDestination)> ExpandMarkdownFiles(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*.md", SearchOption.AllDirectories))
                    yield return (file, Path.GetRelativePath(path, file));
            }
            else if (File.Exists(path) && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                yield return (path, Path.GetFileName(path));
            }
        }
    }

    private static string ToRelativeKey(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

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

    /// <summary>Exporta una lista de skills a la carpeta destino, recreando bajo ella la carpeta en la que
    /// vivía cada skill — así una exportación y una importación posterior devuelven la biblioteca a la
    /// misma organización, en vez de aplanarla.</summary>
    public static void Export(IReadOnlyList<SkillFile> skills, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var skill in skills)
        {
            var relativeFolder = skill.RelativeFolderPath.Replace('/', Path.DirectorySeparatorChar);
            var folder = string.IsNullOrEmpty(relativeFolder)
                ? destinationDirectory
                : Path.Combine(destinationDirectory, relativeFolder);
            Directory.CreateDirectory(folder);
            File.Copy(skill.FilePath, Path.Combine(folder, Path.GetFileName(skill.FilePath)), overwrite: true);
        }
    }

    /// <summary>Copia las skills empaquetadas en <see cref="_defaultSkillsSourceDirectory"/> a
    /// <see cref="SkillsDirectory"/> — solo si la biblioteca no existe o está vacía (a cualquier
    /// profundidad, no solo en la raíz), para no resucitar skills que el usuario borró deliberadamente ni
    /// duplicar las que simplemente movió a una carpeta suya.</summary>
    public void EnsureDefaultSkills()
    {
        if (!Directory.Exists(_defaultSkillsSourceDirectory)) return;

        var alreadyHasSkills = Directory.Exists(SkillsDirectory) &&
            Directory.EnumerateFiles(SkillsDirectory, "*.md", SearchOption.AllDirectories).Any();
        if (alreadyHasSkills) return;

        Directory.CreateDirectory(SkillsDirectory);
        foreach (var source in Directory.EnumerateFiles(_defaultSkillsSourceDirectory, "*.md"))
        {
            var destination = Path.Combine(SkillsDirectory, Path.GetFileName(source));
            File.Copy(source, destination, overwrite: true);
        }

        SkillsChanged?.Invoke();
    }

    // ===================== Utilidades de ruta =====================

    /// <summary>Si <paramref name="filePath"/> cuelga de la biblioteca, a cualquier profundidad — desde
    /// que existen subcarpetas ya no basta con comparar su directorio con la raíz.</summary>
    private bool IsWithinSkillsDirectory(string filePath) =>
        IsUnder(Path.GetFullPath(filePath), Path.GetFullPath(SkillsDirectory));

    private static bool IsUnder(string candidate, string ancestor)
    {
        var normalizedAncestor = ancestor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedAncestor, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
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

/// <summary>Cómo resolver un nombre de archivo que ya existe en el destino de
/// <see cref="SkillService.Import"/>.</summary>
public enum ConflictResolution
{
    /// <summary>No importar el archivo en conflicto.</summary>
    Cancel,

    /// <summary>Importar con un sufijo numérico añadido al nombre.</summary>
    Rename,

    /// <summary>Sobrescribir el archivo existente.</summary>
    Overwrite
}

/// <summary>Resumen de una llamada a <see cref="SkillService.Import"/>. Las entradas son rutas relativas
/// a la carpeta de destino (p. ej. <c>"escritura/redactor.md"</c>), no solo nombres de archivo, para que
/// el diálogo de colisión distinga dos homónimos en subcarpetas distintas.</summary>
public sealed class SkillImportResult
{
    /// <summary>Rutas efectivamente copiadas a la biblioteca.</summary>
    public List<string> Imported { get; } = [];

    /// <summary>Rutas que colisionaban y se omitieron por <see cref="ConflictResolution.Cancel"/>.</summary>
    public List<string> Skipped { get; } = [];
}
