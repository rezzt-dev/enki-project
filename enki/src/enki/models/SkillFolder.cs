using System.Collections.Generic;
using System.IO;

namespace enki.Models;

/// <summary>Una carpeta dentro de <c>&lt;vault&gt;/.enki/skills/</c>, con la que el usuario organiza su
/// biblioteca de skills por temas. Es un tipo propio y no <see cref="NoteFolder"/> —pese a que ambos
/// describen "una carpeta en disco"— porque este árbol vive en el almacén de skills, no en el del vault,
/// y porque lleva los contadores que la rejilla y el árbol de la sección necesitan pintar sin volver a
/// enumerar el disco por cada fila.</summary>
public sealed class SkillFolder
{
    public required string FullPath { get; set; }

    public List<SkillFolder> Children { get; set; } = [];

    public string Name => Path.GetFileName(FullPath);

    /// <summary>Skills que cuelgan directamente de esta carpeta, sin contar las de sus subcarpetas.</summary>
    public int SkillCount { get; set; }

    /// <summary>Skills de esta carpeta y de todas sus subcarpetas — lo que se muestra en la tarjeta de
    /// carpeta, porque una carpeta que solo contiene subcarpetas llenas no debe leerse como vacía.</summary>
    public int TotalSkillCount { get; set; }
}
