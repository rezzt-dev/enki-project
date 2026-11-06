using System;
using System.Collections.Generic;

namespace enki.Models;

/// <summary>Asistente de IA externo cuyo directorio de skills de usuario puede recibir una skill de la
/// biblioteca (.docs/project-info/ia-skills/instalacion-en-el-sistema.md).</summary>
public enum SkillInstallTargetKind
{
    /// <summary>Claude Code: <c>~/.claude/skills</c> o <c>$CLAUDE_CONFIG_DIR/skills</c>.</summary>
    ClaudeCode,

    /// <summary>Codex: <c>~/.agents/skills</c>, el directorio de usuario del estándar Agent Skills.</summary>
    Codex,

    /// <summary>Gemini CLI: <c>~/.gemini/skills</c>.</summary>
    GeminiCli
}

/// <summary>Estado de una skill de la biblioteca respecto a un destino.</summary>
public enum SkillInstallState
{
    /// <summary>No hay ninguna carpeta con su nombre de instalación.</summary>
    NotInstalled,

    /// <summary>Instalada por Enki y al día con la biblioteca.</summary>
    Installed,

    /// <summary>Instalada por Enki, pero la biblioteca ha cambiado desde entonces.</summary>
    Outdated,

    /// <summary>Instalada por Enki y editada después fuera de Enki: reinstalar descartaría esos cambios.</summary>
    ModifiedExternally,

    /// <summary>Ya existe una skill con ese nombre que Enki no instaló o que pertenece a otra skill de la
    /// biblioteca. Enki nunca la sustituye.</summary>
    NameTaken
}

/// <summary>Destino resuelto en este equipo.</summary>
public sealed record SkillInstallTarget(SkillInstallTargetKind Kind, string DisplayName, string SkillsDirectory, bool IsAssistantDetected);

/// <summary>Estado de una skill de la biblioteca en un destino concreto.</summary>
public sealed record SkillInstallStatus(SkillInstallTarget Target, SkillInstallState State, string InstallName, string InstallDirectory);

/// <summary>Skill encontrada en el directorio de un asistente, la haya instalado Enki o no.</summary>
public sealed record SystemSkill(
    SkillInstallTarget Target,
    string InstallName,
    string Name,
    string Description,
    string DirectoryPath,
    bool IsManagedByEnki,
    string? SourcePath,
    bool IsSourceMissing);

/// <summary>Aviso de compatibilidad detectado al preparar la instalación.</summary>
public sealed record SkillInstallWarning(string ResourceKey, IReadOnlyList<object> Arguments);

/// <summary>Contenido de <c>.enki-skill.json</c>, el manifiesto que Enki deja en cada skill instalada.
/// Solo las carpetas con este manifiesto se actualizan o desinstalan desde Enki: una skill escrita a mano
/// en el mismo directorio nunca se toca.</summary>
public sealed class SkillInstallManifest
{
    public int Version { get; set; } = 1;
    public string Generator { get; set; } = "enki";
    public string Target { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Huella SHA-256 de los archivos generados (sin este manifiesto). Si el disco ya no
    /// coincide, alguien editó la skill instalada fuera de Enki.</summary>
    public string ContentHash { get; set; } = string.Empty;

    public DateTime InstalledAtUtc { get; set; }
}
