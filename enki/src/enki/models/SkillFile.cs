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

    /// <summary>Carpeta que contiene el archivo, en absoluto. Coincide con
    /// <see cref="Services.SkillService.SkillsDirectory"/> para una skill que vive en la raiz.</summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Ruta de <see cref="FolderPath"/> relativa a la carpeta de skills, con <c>/</c> como
    /// separador y cadena vacia en la raiz (p. ej. <c>"escritura/blog"</c>). Es lo que se muestra como
    /// "ubicacion" en la tarjeta y el detalle, y lo que distingue dos skills con el mismo nombre de
    /// archivo en carpetas distintas — <see cref="Id"/> sigue siendo solo el nombre de archivo porque es
    /// lo que se propone al duplicar.</summary>
    public string RelativeFolderPath { get; set; } = string.Empty;

    /// <summary>Indica si la skill vive en la raiz de la carpeta de skills.</summary>
    public bool IsAtRoot => string.IsNullOrEmpty(RelativeFolderPath);

    /// <summary>Indica si es una skill del sistema que no se puede eliminar.</summary>
    public bool IsDefault { get; set; }
}
