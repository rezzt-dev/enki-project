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

        return EnumerateSkillFiles(SkillsDirectory, recursive: true)
            .Select(TryReadSkill)
            .OfType<SkillFile>()
            .OrderBy(s => s.RelativeFolderPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Skills que cuelgan directamente de <paramref name="folderPath"/>, sin descender a sus
    /// subcarpetas — lo que pinta la rejilla del modo explorador para la carpeta actual. Los paquetes
    /// que viven en esa carpeta cuentan como skills suyas.</summary>
    public List<SkillFile> LoadSkillsIn(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return [];

        return EnumerateSkillFiles(folderPath, recursive: false)
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
        foreach (var directory in EnumerateSubdirectories(path))
        {
            // Un paquete es una skill, no una carpeta de la biblioteca: sus scripts y referencias no
            // deben aparecer como carpetas navegables ni sus Markdown como skills sueltas.
            if (TryGetPackageEntry(directory) is not null) continue;

            var folder = new SkillFolder { FullPath = directory };
            folder.Children = BuildFolderChildren(directory);
            folder.SkillCount = CountSkills(directory, recursive: false);
            folder.TotalSkillCount = CountSkills(directory, recursive: true);
            folders.Add(folder);
        }
        return folders;
    }

    /// <summary>Nombre del archivo de entrada de un paquete de skill, el mismo que exigen Claude Code,
    /// Codex y Gemini CLI.</summary>
    public const string PackageEntryFileName = "SKILL.md";

    /// <summary>Incluye .MD en Linux y evita ciclos de enlaces simbólicos en bibliotecas sincronizadas.</summary>
    private static IEnumerable<string> EnumerateMarkdownFiles(string directory, SearchOption option) =>
        Directory.EnumerateFiles(directory, "*.md", new EnumerationOptions
        {
            RecurseSubdirectories = option == SearchOption.AllDirectories,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        });

    private static List<string> EnumerateSubdirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory, "*", new EnumerationOptions
                {
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    IgnoreInaccessible = true
                })
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            LogService.Debug($"No se pudieron enumerar las carpetas de '{directory}': {ex}");
            return [];
        }
    }

    /// <summary>Ruta del <c>SKILL.md</c> de <paramref name="directory"/> si es un paquete, sin
    /// distinguir mayúsculas para aceptar paquetes creados en Windows o macOS.</summary>
    public static string? TryGetPackageEntry(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, PackageEntryFileName, new EnumerationOptions
            {
                MatchCasing = MatchCasing.CaseInsensitive,
                AttributesToSkip = 0,
                IgnoreInaccessible = true
            }).FirstOrDefault();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            LogService.Debug($"No se pudo comprobar el paquete '{directory}': {ex}");
            return null;
        }
    }

    /// <summary>Archivos de entrada de todas las skills de <paramref name="directory"/>: los Markdown
    /// sueltos y el <c>SKILL.md</c> de cada paquete hijo, sin descender dentro de los paquetes.</summary>
    private static IEnumerable<string> EnumerateSkillFiles(string directory, bool recursive)
    {
        List<string> files;
        try { files = EnumerateMarkdownFiles(directory, SearchOption.TopDirectoryOnly).ToList(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            LogService.Debug($"No se pudieron enumerar las skills de '{directory}': {ex}");
            files = [];
        }
        foreach (var file in files) yield return file;

        foreach (var subdirectory in EnumerateSubdirectories(directory))
        {
            if (TryGetPackageEntry(subdirectory) is { } entry)
            {
                yield return entry;
                continue;
            }
            if (!recursive) continue;
            foreach (var file in EnumerateSkillFiles(subdirectory, recursive: true)) yield return file;
        }
    }

    private static int CountSkills(string directory, bool recursive) =>
        EnumerateSkillFiles(directory, recursive).Count();

    private SkillFile? TryReadSkill(string filePath)
    {
        try { return ReadSkill(filePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning($"No se pudo leer la skill '{filePath}'", ex);
            return null;
        }
    }

    /// <summary>Lee una skill desde una ruta absoluta sin importarla al vault.</summary>
    public SkillFile ReadSkill(string filePath)
    {
        var content = File.ReadAllText(filePath);
        var packageDirectory = PackageDirectoryOf(filePath);
        var id = packageDirectory is not null
            ? Path.GetFileName(packageDirectory)
            : Path.GetFileNameWithoutExtension(filePath);
        var folderPath = Path.GetDirectoryName(packageDirectory ?? filePath) ?? string.Empty;
        var isDefault = packageDirectory is null && IsWithinSkillsDirectory(filePath) && IsDefaultFileName(Path.GetFileName(filePath));
        var skill = ParseSkill(id, filePath, content, isDefault, RelativeFolderOf(folderPath));
        skill.FolderPath = folderPath;
        skill.PackageDirectory = packageDirectory;
        return skill;
    }

    /// <summary>Carpeta del paquete al que pertenece <paramref name="filePath"/>, o null si es una skill
    /// de un solo archivo. Un <c>SKILL.md</c> suelto en la raíz de la biblioteca no convierte la raíz en
    /// un paquete.</summary>
    private string? PackageDirectoryOf(string filePath)
    {
        if (!string.Equals(Path.GetFileName(filePath), PackageEntryFileName, StringComparison.OrdinalIgnoreCase)) return null;
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (string.IsNullOrEmpty(directory) || PathsEqual(directory, SkillsDirectory)) return null;
        return directory;
    }

    /// <summary>Archivos auxiliares de un paquete (scripts, referencias, plantillas), relativos a su
    /// carpeta y con <c>/</c> como separador. Vacía para una skill de un solo archivo.</summary>
    public static List<string> GetPackageFiles(SkillFile skill, int limit = 200)
    {
        if (!skill.IsPackage || !Directory.Exists(skill.PackageDirectory)) return [];
        return EnumeratePackageFiles(skill.PackageDirectory!)
            .Where(file => !PathsEqual(file, skill.FilePath))
            .Select(file => Path.GetRelativePath(skill.PackageDirectory!, file).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    /// <summary>Todos los archivos de un paquete, sin enlaces simbólicos, sin carpetas de control de
    /// versiones y sin el manifiesto que deja la instalación en el sistema.</summary>
    internal static IEnumerable<string> EnumeratePackageFiles(string packageDirectory) =>
        Directory.EnumerateFiles(packageDirectory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true
            })
            .Where(file =>
            {
                var relative = Path.GetRelativePath(packageDirectory, file);
                var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !segments.Contains(".git", StringComparer.OrdinalIgnoreCase)
                       && !string.Equals(relative, SkillInstallService.ManifestFileName, StringComparison.OrdinalIgnoreCase);
            });

    /// <summary>Carpeta <paramref name="folder"/> relativa a la biblioteca, con <c>/</c> como
    /// separador y cadena vacía en la raíz. Devuelve cadena vacía también para un archivo que esté fuera
    /// de la biblioteca (el caso de <see cref="ReadSkill"/> sobre un archivo aún sin importar).</summary>
    private string RelativeFolderOf(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return string.Empty;
        folder = Path.GetFullPath(folder);

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
        if (File.ReadAllText(skill.FilePath) != skill.Content)
            throw new IOException(LocalizationService.Instance["Skills_ExternalChange"]);
        var (_, _, hasFrontMatter) = SplitFrontMatter(content);
        var finalContent = hasFrontMatter ? content : PrependMinimalFrontMatter(content, skill);

        var tempPath = $"{skill.FilePath}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(tempPath, finalContent);
            File.Move(tempPath, skill.FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error($"No se pudo guardar la skill '{skill.FilePath}'", ex);
            throw;
        }

        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { LogService.Error("No se pudo eliminar el temporal de la skill", ex); }
            }
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

    /// <summary>Crea una skill editable sin sobrescribir archivos existentes.</summary>
    public SkillFile CreateSkill(string folderPath, string name)
    {
        Directory.CreateDirectory(folderPath);
        var destination = MakeUniqueDestination(Path.Combine(folderPath, $"{SanitizeId(name)}.md"));
        var content = PrependMinimalFrontMatter("# " + name + "\n\n" +
            LocalizationService.Instance["Skills_TemplateBody"], new SkillFile { Name = name });
        File.WriteAllText(destination, content);
        SkillsChanged?.Invoke();
        return ReadSkill(destination);
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
        if (source.IsPackage)
        {
            // Un paquete se duplica entero: sin sus scripts y referencias la copia no funcionaría.
            var packageDestination = Path.Combine(targetFolder, safeId);
            if (Directory.Exists(packageDestination) || File.Exists(packageDestination))
                throw LocalizedValidationErrors.Create("Skills_Error_NameExists", safeId);
            CopyPackage(source.PackageDirectory!, packageDestination);
            SkillsChanged?.Invoke();
            return ReadSkill(Path.Combine(packageDestination, Path.GetFileName(source.FilePath)));
        }

        var destination = Path.Combine(targetFolder, $"{safeId}.md");
        if (File.Exists(destination))
            throw LocalizedValidationErrors.Create("Skills_Error_NameExists", safeId);

        File.WriteAllText(destination, source.Content);
        SkillsChanged?.Invoke();
        return ReadSkill(destination);
    }

    /// <summary>Elimina una skill del disco. No permite eliminar skills default. Un paquete se elimina
    /// con todos sus archivos auxiliares.</summary>
    public void DeleteSkill(SkillFile skill)
    {
        if (skill.IsDefault)
            throw LocalizedValidationErrors.Create("Skills_Error_CannotDeleteDefault");

        if (skill.IsPackage)
        {
            if (Directory.Exists(skill.PackageDirectory)) Directory.Delete(skill.PackageDirectory!, recursive: true);
        }
        else if (File.Exists(skill.FilePath))
        {
            File.Delete(skill.FilePath);
        }
        SkillsChanged?.Invoke();
    }

    /// <summary>Convierte una skill de un solo archivo en un paquete <c>&lt;id&gt;/SKILL.md</c>, el
    /// formato que permite añadirle scripts, referencias y plantillas como en Claude Code, Codex y
    /// Gemini CLI. El contenido no cambia; solo se mueve el archivo. Devuelve la skill releída.</summary>
    public SkillFile ConvertToPackage(SkillFile skill)
    {
        if (skill.IsPackage) return skill;
        if (skill.IsDefault)
            throw LocalizedValidationErrors.Create("Skills_Error_CannotConvertDefault");

        var folder = string.IsNullOrEmpty(skill.FolderPath) ? SkillsDirectory : skill.FolderPath;
        var packageDirectory = MakeUniqueDirectory(Path.Combine(folder, SanitizeId(skill.Id)));
        Directory.CreateDirectory(packageDirectory);
        var entry = Path.Combine(packageDirectory, PackageEntryFileName);
        try
        {
            File.Move(skill.FilePath, entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { Directory.Delete(packageDirectory); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { LogService.Debug($"No se pudo limpiar el paquete vacío '{packageDirectory}': {cleanup}"); }
            throw;
        }

        SkillsChanged?.Invoke();
        return ReadSkill(entry);
    }

    /// <summary>Mueve una skill a otra carpeta de la biblioteca (arrastrar y soltar sobre una fila del
    /// árbol o sobre la raíz). Un nombre ya ocupado en el destino se resuelve con sufijo numérico, misma
    /// política que <see cref="Import"/>/<see cref="VaultService.RenameFolder"/>, para que mover nunca
    /// pise una skill que ya estaba allí. Devuelve la skill releída desde su nueva ruta.</summary>
    public SkillFile MoveSkill(SkillFile skill, string destinationFolderPath)
    {
        if (PathsEqual(skill.FolderPath, destinationFolderPath)) return skill;

        Directory.CreateDirectory(destinationFolderPath);
        if (skill.IsPackage)
        {
            var source = Path.GetFullPath(skill.PackageDirectory!);
            var target = Path.GetFullPath(destinationFolderPath);
            if (PathsEqual(source, target) || IsUnder(target, source))
                throw LocalizedValidationErrors.Create("Skills_Error_FolderIntoItself");

            var packageDestination = MakeUniqueDirectory(Path.Combine(destinationFolderPath, Path.GetFileName(source)));
            Directory.Move(source, packageDestination);
            SkillsChanged?.Invoke();
            return ReadSkill(Path.Combine(packageDestination, Path.GetFileName(skill.FilePath)));
        }

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
        while (Directory.Exists(path) || File.Exists(path))
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
        while (Directory.Exists(newPath) || File.Exists(newPath))
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

        var blocking = EnumerateSkillFiles(folder.FullPath, recursive: true)
            .Where(file => PackageDirectoryOf(file) is null)
            .FirstOrDefault(file => IsDefaultFileName(Path.GetFileName(file)));
        if (blocking is not null)
            throw LocalizedValidationErrors.Create("Skills_Error_FolderHasDefault", Path.GetFileName(blocking));

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
            throw LocalizedValidationErrors.Create("Skills_Error_FolderIntoItself");

        Directory.CreateDirectory(destinationParent);
        var newPath = Path.Combine(destinationParent, Path.GetFileName(source));
        var suffix = 2;
        while (Directory.Exists(newPath) || File.Exists(newPath))
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
        foreach (var entry in ExpandImportEntries(paths).ToList())
        {
            var destination = Path.Combine(target, entry.RelativeDestination);
            var relativeKey = entry.RelativeDestination.Replace(Path.DirectorySeparatorChar, '/');

            if (PathsEqual(entry.SourcePath, destination) || (entry.IsPackage && IsUnder(Path.GetFullPath(destination), Path.GetFullPath(entry.SourcePath))))
            {
                result.Skipped.Add(relativeKey);
                continue;
            }
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                if (defaultResolution == ConflictResolution.Cancel)
                {
                    result.Skipped.Add(relativeKey);
                    continue;
                }
                if (defaultResolution == ConflictResolution.Rename)
                {
                    destination = entry.IsPackage ? MakeUniqueDirectory(destination) : MakeUniqueDestination(destination);
                }
                else if (entry.IsPackage)
                {
                    // Sobrescribir un paquete reemplaza la carpeta entera: mezclar archivos de dos
                    // versiones dejaría referencias huérfanas.
                    if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                    else File.Delete(destination);
                }
                else if (Directory.Exists(destination))
                {
                    result.Skipped.Add(relativeKey);
                    continue;
                }
                // Overwrite de un archivo: File.Copy con overwrite=true lo sobrescribe.
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (entry.IsPackage) CopyPackage(entry.SourcePath, destination);
            else File.Copy(entry.SourcePath, destination, overwrite: true);
            result.Imported.Add(ToRelativeKey(target, destination));
        }

        if (result.Imported.Count > 0) SkillsChanged?.Invoke();
        return result;
    }

    /// <summary>Rutas relativas de <paramref name="paths"/> que ya existen en
    /// <paramref name="targetFolderPath"/>: las que harán aparecer el diálogo de colisión. Usa la misma
    /// expansión que <see cref="Import"/> para no razonar dos veces sobre qué entra.</summary>
    public static List<string> DetectImportConflicts(IReadOnlyList<string> paths, string targetFolderPath) =>
        ExpandImportEntries(paths)
            .Where(entry =>
            {
                var destination = Path.Combine(targetFolderPath, entry.RelativeDestination);
                return (File.Exists(destination) || Directory.Exists(destination)) && !PathsEqual(entry.SourcePath, destination);
            })
            .Select(entry => entry.RelativeDestination.Replace(Path.DirectorySeparatorChar, '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Lo que aporta cada ruta a una importación. Un Markdown suelto aporta solo su nombre;
    /// una carpeta aporta su contenido relativo a ella misma, de modo que su primer nivel aterriza
    /// directamente en el destino y sus subcarpetas se recrean por debajo. Un paquete
    /// (<c>carpeta/SKILL.md</c>) se importa entero y conserva su nombre, también cuando es la propia
    /// carpeta elegida: así se pueden traer skills de <c>~/.claude/skills</c> sin perder sus scripts.</summary>
    public static IEnumerable<SkillImportEntry> ExpandImportEntries(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (TryGetPackageEntry(full) is not null)
                {
                    yield return new SkillImportEntry(full, Path.GetFileName(full), IsPackage: true);
                    continue;
                }
                foreach (var entry in ExpandFolder(full, string.Empty)) yield return entry;
            }
            else if (File.Exists(path) && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                yield return new SkillImportEntry(path, Path.GetFileName(path), IsPackage: false);
            }
        }
    }

    private static IEnumerable<SkillImportEntry> ExpandFolder(string directory, string relative)
    {
        foreach (var file in EnumerateMarkdownFiles(directory, SearchOption.TopDirectoryOnly))
            yield return new SkillImportEntry(file, Path.Combine(relative, Path.GetFileName(file)), IsPackage: false);

        foreach (var subdirectory in EnumerateSubdirectories(directory))
        {
            var childRelative = Path.Combine(relative, Path.GetFileName(subdirectory));
            if (TryGetPackageEntry(subdirectory) is not null)
            {
                yield return new SkillImportEntry(subdirectory, childRelative, IsPackage: true);
                continue;
            }
            foreach (var entry in ExpandFolder(subdirectory, childRelative)) yield return entry;
        }
    }

    /// <summary>Compatibilidad con los llamadores que solo necesitaban pares origen/destino.</summary>
    public static IEnumerable<(string SourcePath, string RelativeDestination)> ExpandMarkdownFiles(IReadOnlyList<string> paths) =>
        ExpandImportEntries(paths).Select(entry => (entry.SourcePath, entry.RelativeDestination));

    /// <summary>Copia un paquete completo, sin enlaces simbólicos, sin <c>.git</c> y sin el manifiesto de
    /// instalación. La lista de archivos se materializa antes de copiar para que un destino dentro del
    /// origen no provoque una copia infinita.</summary>
    internal static void CopyPackage(string sourceDirectory, string destinationDirectory)
    {
        var files = EnumeratePackageFiles(sourceDirectory).ToList();
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in files)
        {
            var destination = Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static string MakeUniqueDirectory(string destination)
    {
        var parent = Path.GetDirectoryName(destination)!;
        var baseName = Path.GetFileName(destination);
        var candidate = destination;
        var suffix = 2;
        while (Directory.Exists(candidate) || File.Exists(candidate))
        {
            candidate = Path.Combine(parent, $"{baseName} {suffix}");
            suffix++;
        }
        return candidate;
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
        while (File.Exists(candidate) || Directory.Exists(candidate))
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
            if (skill.IsPackage)
            {
                var packageDestination = Path.Combine(folder, Path.GetFileName(skill.PackageDirectory!));
                if (Directory.Exists(packageDestination)) Directory.Delete(packageDestination, recursive: true);
                CopyPackage(skill.PackageDirectory!, packageDestination);
                continue;
            }
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
            EnumerateMarkdownFiles(SkillsDirectory, SearchOption.AllDirectories).Any();
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

    /// <summary>Cuerpo para el lector; el modo código conserva el documento íntegro.</summary>
    public static string GetMarkdownBody(string content) => SplitFrontMatter(content).Body.TrimStart();

    /// <summary>Nombre y descripción del frontmatter de <paramref name="content"/>, sin construir una
    /// <see cref="SkillFile"/>. Lo usa el listado de skills instaladas en el sistema, que no forman
    /// parte de la biblioteca.</summary>
    public static (string? Name, string? Description) ReadFrontMatterSummary(string content)
    {
        var frontMatter = TryParseFrontMatter(SplitFrontMatter(content).Yaml);
        return (frontMatter?.Name, frontMatter?.Description);
    }

    /// <summary>Separa el frontmatter YAML del cuerpo; sin cierre conserva todo el documento.</summary>
    internal static (string? Yaml, string Body, bool HasFrontMatter) SplitFrontMatter(string content)
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
        catch (YamlDotNet.Core.YamlException ex)
        {
            LogService.Debug($"Frontmatter YAML de skill no válido: {ex}");
            return null;
        }
    }

    private static string SanitizeId(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or ".." ? Guid.NewGuid().ToString("N") : cleaned;
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

/// <summary>Unidad de importación: un Markdown suelto o un paquete completo. En un paquete,
/// <see cref="SourcePath"/> es su carpeta y <see cref="RelativeDestination"/> la carpeta que ocupará
/// bajo el destino.</summary>
public sealed record SkillImportEntry(string SourcePath, string RelativeDestination, bool IsPackage);

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
