using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using enki.Localization;
using enki.Models;
using YamlDotNet.Serialization;

namespace enki.Services;

/// <summary>Instala skills de la biblioteca en los directorios de usuario de Claude Code, Codex y Gemini
/// CLI, para que esos asistentes las descubran por su cuenta, sin pasar por el asistente de Enki
/// (.docs/project-info/ia-skills/instalacion-en-el-sistema.md).
///
/// Cada instalación es una carpeta <c>&lt;destino&gt;/&lt;nombre&gt;/SKILL.md</c> con el formato del
/// estándar Agent Skills: <c>name</c> en minúsculas con guiones y igual al nombre de la carpeta,
/// <c>description</c> obligatoria y el resto del frontmatter tal cual lo escribió el usuario, salvo los
/// campos propios de Enki. Junto a ella se deja <see cref="ManifestFileName"/>, que es lo que autoriza a
/// Enki a actualizarla o desinstalarla; una carpeta sin manifiesto nunca se modifica.
///
/// El servicio no conoce el vault ni la interfaz: recibe la carpeta personal para que los tests trabajen
/// en un directorio temporal y nunca toquen el <c>~/.claude</c> real del desarrollador.</summary>
public sealed class SkillInstallService
{
    /// <summary>Manifiesto que identifica una skill instalada por Enki.</summary>
    public const string ManifestFileName = ".enki-skill.json";

    /// <summary>Límites del estándar Agent Skills, que Codex valida al cargar.</summary>
    public const int MaxInstallNameLength = 64;
    public const int MaxDescriptionLength = 1024;

    /// <summary>Tamaño máximo de un paquete: protege de copiar por error una carpeta enorme al
    /// directorio de un asistente.</summary>
    public const long MaxPackageBytes = 50L * 1024 * 1024;

    /// <summary>Campos del frontmatter que solo usa la biblioteca de Enki. No se copian al
    /// <c>SKILL.md</c> instalado: en particular <c>tools</c> y <c>permissions</c> no deben confundirse con
    /// <c>allowed-tools</c>, que concede permisos reales en Claude Code.</summary>
    private static readonly string[] EnkiOnlyKeys = ["emoji", "color", "vibe", "tools", "tags", "permissions"];

    private static readonly ISerializer ScalarSerializer = new SerializerBuilder().Build();

    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    private readonly string _homeDirectory;
    private readonly Func<string, string?> _environment;

    /// <summary>Se dispara tras instalar, actualizar o desinstalar.</summary>
    public Action? InstallationsChanged { get; set; }

    /// <param name="homeDirectory">Carpeta personal; por defecto la del usuario actual.</param>
    /// <param name="environment">Lectura de variables de entorno (<c>CLAUDE_CONFIG_DIR</c>,
    /// <c>CODEX_HOME</c>); los tests pasan una función vacía.</param>
    public SkillInstallService(string? homeDirectory = null, Func<string, string?>? environment = null)
    {
        _homeDirectory = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    // ===================== Destinos =====================

    /// <summary>Los tres destinos resueltos para este equipo, en orden fijo.</summary>
    public IReadOnlyList<SkillInstallTarget> GetTargets() =>
        [GetTarget(SkillInstallTargetKind.ClaudeCode), GetTarget(SkillInstallTargetKind.Codex), GetTarget(SkillInstallTargetKind.GeminiCli)];

    public SkillInstallTarget GetTarget(SkillInstallTargetKind kind)
    {
        switch (kind)
        {
            case SkillInstallTargetKind.ClaudeCode:
            {
                var configured = NonEmpty(_environment("CLAUDE_CONFIG_DIR"));
                var root = configured ?? Path.Combine(_homeDirectory, ".claude");
                return new SkillInstallTarget(kind, "Claude Code", Path.Combine(root, "skills"), Directory.Exists(root));
            }
            case SkillInstallTargetKind.Codex:
            {
                // Codex busca las skills de usuario en ~/.agents/skills; su configuración vive en
                // ~/.codex (o $CODEX_HOME), que es lo que delata que está instalado.
                var codexHome = NonEmpty(_environment("CODEX_HOME")) ?? Path.Combine(_homeDirectory, ".codex");
                return new SkillInstallTarget(kind, "Codex", Path.Combine(_homeDirectory, ".agents", "skills"), Directory.Exists(codexHome));
            }
            default:
            {
                var root = Path.Combine(_homeDirectory, ".gemini");
                return new SkillInstallTarget(kind, "Gemini CLI", Path.Combine(root, "skills"), Directory.Exists(root));
            }
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ===================== Nombre de instalación =====================

    /// <summary>Nombre de carpeta y <c>name</c> del <c>SKILL.md</c> instalado: el id de la skill (nombre
    /// de archivo o de paquete) en minúsculas, sin diacríticos y con guiones, como exige el estándar
    /// Agent Skills. Se deriva del id y no del <c>name</c> visible para que renombrar la skill en su
    /// frontmatter no deje instalaciones huérfanas.</summary>
    public static string GetInstallName(SkillFile skill)
    {
        var slug = Slugify(skill.Id);
        if (slug.Length > 0) return slug;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(skill.FilePath)))[..8].ToLowerInvariant();
        return $"skill-{hash}";
    }

    internal static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousHyphen = true;
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            var lower = char.ToLowerInvariant(character);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(lower);
                previousHyphen = false;
            }
            else if (!previousHyphen)
            {
                builder.Append('-');
                previousHyphen = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > MaxInstallNameLength) slug = slug[..MaxInstallNameLength].TrimEnd('-');
        return slug;
    }

    // ===================== Generación =====================

    /// <summary>Avisos de compatibilidad para mostrar antes de instalar. No impiden la instalación.</summary>
    public static IReadOnlyList<SkillInstallWarning> Validate(SkillFile skill)
    {
        var warnings = new List<SkillInstallWarning>();
        var (yaml, _, hasFrontMatter) = SkillService.SplitFrontMatter(skill.Content);
        if (!hasFrontMatter)
            warnings.Add(new SkillInstallWarning("SkillInstall_Warning_NoFrontMatter", []));
        else if (!IsValidYaml(yaml))
            warnings.Add(new SkillInstallWarning("SkillInstall_Warning_InvalidYaml", []));

        if (string.IsNullOrWhiteSpace(skill.Description))
            warnings.Add(new SkillInstallWarning("SkillInstall_Warning_NoDescription", []));
        else if (skill.Description.Length > MaxDescriptionLength)
            warnings.Add(new SkillInstallWarning("SkillInstall_Warning_LongDescription", [MaxDescriptionLength]));

        var installName = GetInstallName(skill);
        if (!string.Equals(installName, skill.Id, StringComparison.Ordinal))
            warnings.Add(new SkillInstallWarning("SkillInstall_Warning_NameNormalized", [skill.Id, installName]));

        if (skill.IsPackage && GetPackageSize(skill) > MaxPackageBytes)
            warnings.Add(new SkillInstallWarning("SkillInstall_Warning_PackageTooLarge", [MaxPackageBytes / (1024 * 1024)]));
        return warnings;
    }

    private static bool IsValidYaml(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return true;
        try
        {
            new DeserializerBuilder().Build().Deserialize<object>(yaml);
            return true;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return false;
        }
    }

    private static long GetPackageSize(SkillFile skill) =>
        SkillService.EnumeratePackageFiles(skill.PackageDirectory!).Sum(file => new FileInfo(file).Length);

    /// <summary>El <c>SKILL.md</c> que se instala: el frontmatter original editado línea a línea (para no
    /// alterar el formato de campos como <c>allowed-tools</c> o <c>hooks</c>), con <c>name</c> igual al
    /// nombre de instalación, <c>description</c> garantizada y sin los campos propios de Enki.</summary>
    public static string RenderSkillMarkdown(SkillFile skill)
    {
        var installName = GetInstallName(skill);
        var (yaml, body, hasFrontMatter) = SkillService.SplitFrontMatter(skill.Content);
        var lines = hasFrontMatter && yaml is not null
            ? yaml.Split('\n').ToList()
            : [];

        var keepDescription = !string.IsNullOrWhiteSpace(skill.Description)
                              && skill.Description.Length <= MaxDescriptionLength
                              && TopLevelKeys(lines).Contains("description");
        lines = RemoveTopLevelKeys(lines, keepDescription
            ? [.. EnkiOnlyKeys, "name"]
            : [.. EnkiOnlyKeys, "name", "description"]);

        var header = new List<string> { $"name: {installName}" };
        if (!keepDescription) header.Add(SerializeScalar("description", BuildDescription(skill)));

        var frontMatter = string.Join('\n', header.Concat(TrimBlankEdges(lines)));
        var normalizedBody = (hasFrontMatter ? body : skill.Content.Replace("\r\n", "\n")).TrimStart('\n');
        return $"---\n{frontMatter}\n---\n\n{normalizedBody}".TrimEnd('\n') + "\n";
    }

    private static string BuildDescription(SkillFile skill)
    {
        var description = string.IsNullOrWhiteSpace(skill.Description) ? skill.Name : skill.Description;
        description = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(description)) description = GetInstallName(skill);
        return description.Length <= MaxDescriptionLength ? description : description[..(MaxDescriptionLength - 1)].TrimEnd() + "…";
    }

    private static string SerializeScalar(string key, string value) =>
        ScalarSerializer.Serialize(new Dictionary<string, string> { [key] = value }).TrimEnd('\n', '\r');

    private static bool IsTopLevelKeyLine(string line, out string key)
    {
        key = string.Empty;
        if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] is '#' or '-') return false;
        var colon = line.IndexOf(':');
        if (colon <= 0) return false;
        key = line[..colon].Trim().Trim('"', '\'');
        return key.Length > 0;
    }

    private static HashSet<string> TopLevelKeys(IEnumerable<string> lines)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
            if (IsTopLevelKeyLine(line, out var key)) keys.Add(key);
        return keys;
    }

    /// <summary>Quita las claves de primer nivel indicadas junto con sus líneas de continuación
    /// (sangradas o elementos de lista).</summary>
    private static List<string> RemoveTopLevelKeys(List<string> lines, IReadOnlyCollection<string> keys)
    {
        var result = new List<string>(lines.Count);
        var skipping = false;
        foreach (var line in lines)
        {
            if (IsTopLevelKeyLine(line, out var key))
            {
                skipping = keys.Contains(key, StringComparer.OrdinalIgnoreCase);
                if (!skipping) result.Add(line);
                continue;
            }

            var isContinuation = line.Length > 0 && (char.IsWhiteSpace(line[0]) || line[0] == '-');
            if (skipping && (isContinuation || line.Trim().Length == 0)) continue;
            skipping = false;
            result.Add(line);
        }
        return result;
    }

    private static IEnumerable<string> TrimBlankEdges(List<string> lines)
    {
        var start = lines.FindIndex(line => line.Trim().Length > 0);
        if (start < 0) return [];
        var end = lines.FindLastIndex(line => line.Trim().Length > 0);
        return lines.GetRange(start, end - start + 1).Select(line => line.TrimEnd('\r'));
    }

    /// <summary>Archivos que forman la instalación, con su ruta relativa y su contenido. Para un paquete,
    /// todos sus archivos auxiliares más el <c>SKILL.md</c> generado.</summary>
    private static SortedDictionary<string, byte[]> BuildPackage(SkillFile skill)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [SkillService.PackageEntryFileName] = new UTF8Encoding(false).GetBytes(RenderSkillMarkdown(skill))
        };
        if (!skill.IsPackage) return files;

        long total = 0;
        foreach (var file in SkillService.EnumeratePackageFiles(skill.PackageDirectory!))
        {
            var relative = Path.GetRelativePath(skill.PackageDirectory!, file).Replace(Path.DirectorySeparatorChar, '/');
            if (string.Equals(relative, SkillService.PackageEntryFileName, StringComparison.OrdinalIgnoreCase)) continue;
            var bytes = File.ReadAllBytes(file);
            total += bytes.Length;
            if (total > MaxPackageBytes)
                throw LocalizedValidationErrors.Create("SkillInstall_Error_PackageTooLarge", MaxPackageBytes / (1024 * 1024));
            files[relative] = bytes;
        }
        return files;
    }

    private static string HashFiles(SortedDictionary<string, byte[]> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (relative, bytes) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            hash.AppendData(BitConverter.GetBytes((long)bytes.Length));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string HashInstalledDirectory(string directory)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in SkillService.EnumeratePackageFiles(directory))
            files[Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/')] = File.ReadAllBytes(file);
        return HashFiles(files);
    }

    // ===================== Estado =====================

    public SkillInstallStatus GetStatus(SkillFile skill, SkillInstallTargetKind kind)
    {
        var target = GetTarget(kind);
        var installName = GetInstallName(skill);
        var directory = Path.Combine(target.SkillsDirectory, installName);
        return new SkillInstallStatus(target, ResolveState(skill, directory), installName, directory);
    }

    public IReadOnlyList<SkillInstallStatus> GetStatuses(SkillFile skill) =>
        GetTargets().Select(target => GetStatus(skill, target.Kind)).ToList();

    private static SkillInstallState ResolveState(SkillFile skill, string directory)
    {
        if (!Directory.Exists(directory) && !File.Exists(directory)) return SkillInstallState.NotInstalled;
        var manifest = ReadManifest(directory);
        if (manifest is null) return SkillInstallState.NameTaken;

        var sameSource = PathsEqual(manifest.SourcePath, skill.FilePath);
        // Si la skill se movió o renombró dentro de la biblioteca, su origen anterior ya no existe: la
        // instalación sigue siendo suya y solo hay que actualizar el manifiesto.
        if (!sameSource && File.Exists(manifest.SourcePath)) return SkillInstallState.NameTaken;

        try
        {
            if (!string.Equals(HashInstalledDirectory(directory), manifest.ContentHash, StringComparison.Ordinal))
                return SkillInstallState.ModifiedExternally;
            if (!sameSource) return SkillInstallState.Outdated;
            return string.Equals(HashFiles(BuildPackage(skill)), manifest.ContentHash, StringComparison.Ordinal)
                ? SkillInstallState.Installed
                : SkillInstallState.Outdated;
        }
        catch (InvalidOperationException)
        {
            return SkillInstallState.Outdated;
        }
    }

    private static SkillInstallManifest? ReadManifest(string directory)
    {
        var path = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize<SkillInstallManifest>(File.ReadAllText(path));
            return manifest is { Generator: "enki" } ? manifest : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LogService.Warning($"Manifiesto de skill instalada no válido en '{directory}'", ex);
            return null;
        }
    }

    // ===================== Instalar / desinstalar =====================

    /// <summary>Instala o actualiza <paramref name="skill"/> en <paramref name="kind"/>. Se niega si el
    /// nombre lo ocupa una skill ajena y, salvo <paramref name="overwriteExternalChanges"/>, si la copia
    /// instalada se editó fuera de Enki. La carpeta se prepara aparte y se sustituye al final, para que el
    /// asistente nunca vea una skill a medio escribir.</summary>
    public SkillInstallStatus Install(SkillFile skill, SkillInstallTargetKind kind, bool overwriteExternalChanges = false)
    {
        var status = GetStatus(skill, kind);
        switch (status.State)
        {
            case SkillInstallState.NameTaken:
                throw LocalizedValidationErrors.Create("SkillInstall_Error_NameTaken", status.InstallName, status.Target.DisplayName);
            case SkillInstallState.ModifiedExternally when !overwriteExternalChanges:
                throw LocalizedValidationErrors.Create("SkillInstall_Error_ModifiedExternally", status.InstallName, status.Target.DisplayName);
        }

        var files = BuildPackage(skill);
        var manifest = new SkillInstallManifest
        {
            Target = kind.ToString(),
            SourcePath = Path.GetFullPath(skill.FilePath),
            ContentHash = HashFiles(files),
            InstalledAtUtc = DateTime.UtcNow
        };

        var skillsDirectory = status.Target.SkillsDirectory;
        Directory.CreateDirectory(skillsDirectory);
        // La preparación se hace junto al directorio de skills y no dentro: el asistente vigila esa
        // carpeta y no debe descubrir una skill temporal.
        var stagingRoot = Path.GetDirectoryName(Path.GetFullPath(skillsDirectory).TrimEnd(Path.DirectorySeparatorChar))!;
        var staging = Path.Combine(stagingRoot, $".enki-staging-{Guid.NewGuid():N}");
        var backup = Path.Combine(stagingRoot, $".enki-backup-{Guid.NewGuid():N}");
        try
        {
            foreach (var (relative, bytes) in files)
            {
                var destination = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, bytes);
            }
            File.WriteAllText(Path.Combine(staging, ManifestFileName), JsonSerializer.Serialize(manifest, ManifestJsonOptions));

            var hadPrevious = Directory.Exists(status.InstallDirectory);
            if (hadPrevious) Directory.Move(status.InstallDirectory, backup);
            try
            {
                Directory.Move(staging, status.InstallDirectory);
            }
            catch
            {
                if (hadPrevious && Directory.Exists(backup)) Directory.Move(backup, status.InstallDirectory);
                throw;
            }
            if (hadPrevious) TryDeleteDirectory(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error($"No se pudo instalar la skill '{skill.FilePath}' en '{status.InstallDirectory}'", ex);
            throw;
        }
        finally
        {
            TryDeleteDirectory(staging);
        }

        InstallationsChanged?.Invoke();
        return GetStatus(skill, kind);
    }

    /// <summary>Desinstala la copia de <paramref name="skill"/> en <paramref name="kind"/>. Solo borra
    /// carpetas con manifiesto de Enki.</summary>
    public void Uninstall(SkillFile skill, SkillInstallTargetKind kind)
    {
        var status = GetStatus(skill, kind);
        if (status.State == SkillInstallState.NotInstalled) return;
        if (status.State == SkillInstallState.NameTaken)
            throw LocalizedValidationErrors.Create("SkillInstall_Error_NotManaged", status.InstallName);
        Directory.Delete(status.InstallDirectory, recursive: true);
        InstallationsChanged?.Invoke();
    }

    /// <summary>Desinstala una skill del listado del sistema; solo las que instaló Enki.</summary>
    public void Uninstall(SystemSkill skill)
    {
        if (!skill.IsManagedByEnki || ReadManifest(skill.DirectoryPath) is null)
            throw LocalizedValidationErrors.Create("SkillInstall_Error_NotManaged", skill.InstallName);
        if (!IsUnder(Path.GetFullPath(skill.DirectoryPath), Path.GetFullPath(skill.Target.SkillsDirectory)))
            throw LocalizedValidationErrors.Create("SkillInstall_Error_NotManaged", skill.InstallName);
        Directory.Delete(skill.DirectoryPath, recursive: true);
        InstallationsChanged?.Invoke();
    }

    /// <summary>Actualiza las copias instaladas de <paramref name="skill"/> que estén desactualizadas y
    /// sin cambios externos. Devuelve los destinos actualizados; los fallos se registran y se devuelven
    /// aparte para que quien llama los anuncie sin interrumpir el guardado.</summary>
    public (IReadOnlyList<SkillInstallTarget> Updated, IReadOnlyList<(SkillInstallTarget Target, Exception Error)> Failed) SyncInstalled(SkillFile skill)
    {
        var updated = new List<SkillInstallTarget>();
        var failed = new List<(SkillInstallTarget, Exception)>();
        foreach (var status in GetStatuses(skill).Where(s => s.State == SkillInstallState.Outdated))
        {
            try
            {
                Install(skill, status.Target.Kind);
                updated.Add(status.Target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed.Add((status.Target, ex));
            }
        }
        return (updated, failed);
    }

    // ===================== Listado del sistema =====================

    /// <summary>Todas las skills de usuario de los tres destinos, instaladas por Enki o no. El directorio
    /// compartido <c>~/.agents/skills</c> se lista una sola vez, bajo Codex.</summary>
    public IReadOnlyList<SystemSkill> ListSystemSkills()
    {
        var result = new List<SystemSkill>();
        foreach (var target in GetTargets())
        {
            if (!Directory.Exists(target.SkillsDirectory)) continue;
            List<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(target.SkillsDirectory, "*", new EnumerationOptions { IgnoreInaccessible = true })
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Warning($"No se pudo listar '{target.SkillsDirectory}'", ex);
                continue;
            }

            foreach (var directory in directories)
            {
                var entry = SkillService.TryGetPackageEntry(directory);
                if (entry is null) continue;

                string? name = null, description = null;
                try { (name, description) = SkillService.ReadFrontMatterSummary(File.ReadAllText(entry)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { LogService.Debug($"No se pudo leer '{entry}': {ex}"); }

                var manifest = ReadManifest(directory);
                var installName = Path.GetFileName(directory);
                result.Add(new SystemSkill(target, installName,
                    string.IsNullOrWhiteSpace(name) ? installName : name!, description ?? string.Empty,
                    directory, manifest is not null, manifest?.SourcePath,
                    manifest is not null && !File.Exists(manifest.SourcePath)));
            }
        }
        return result;
    }

    // ===================== Utilidades =====================

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { LogService.Warning($"No se pudo limpiar el temporal '{path}'", ex); }
    }

    private static bool PathsEqual(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string candidate, string ancestor)
    {
        var normalizedAncestor = ancestor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedAncestor, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }
}
