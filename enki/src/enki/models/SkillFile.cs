using System.Collections.Generic;

namespace enki.Models;

/// <summary>Representa una skill de IA almacenada como un archivo Markdown con frontmatter YAML.
/// El identificador se deriva del nombre de archivo sin extensión.</summary>
public sealed class SkillFile
{
    /// <summary>Nombre técnico de la skill (nombre de archivo sin extensión).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Nombre legible definido en el frontmatter; si no existe, <see cref="Id"/>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Descripción corta definida en el frontmatter.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Emoji opcional definido en el frontmatter.</summary>
    public string? Emoji { get; set; }

    /// <summary>Color opcional (nombre o hex) definido en el frontmatter.</summary>
    public string? Color { get; set; }

    /// <summary>Vibe/personalidad opcional definida en el frontmatter.</summary>
    public string? Vibe { get; set; }

    /// <summary>Herramientas que la skill puede usar en una futura integración con IA.</summary>
    public List<string> Tools { get; set; } = [];

    /// <summary>Etiquetas opcionales para organizar skills.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Campo reservado para permisos futuros (leer notas, escribir archivos, ejecutar comandos...).</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>Contenido Markdown completo del archivo (incluyendo frontmatter).</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Ruta absoluta del archivo en disco.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Indica si es una skill del sistema que no se puede eliminar.</summary>
    public bool IsDefault { get; set; }
}
