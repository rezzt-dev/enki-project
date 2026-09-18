using System;
using System.Collections.Generic;
using System.Linq;

namespace enki.Services;

/// <summary>Primitivo puro de reescritura de listas de etiquetas, compartido por las tres pasadas del
/// Gestor global de etiquetas (<see cref="TagManagerService"/>): renombrar, fusionar y eliminar son la
/// misma transformación — "sustituye cualquier etiqueta de <c>sourceTags</c> por <c>targetTag</c> (o
/// quítala si es null)" — aplicada una, varias o ninguna vez respectivamente. Usado tal cual por
/// <see cref="VaultService.ReplaceTagAcrossVault"/> (notas), <see cref="TagManagerService"/> (tarjetas
/// Kanban) y <see cref="PolarTaskService.ReplaceTagAcrossTasks"/>, para no reescribir esta lógica tres
/// veces con matices ligeramente distintos.</summary>
internal static class TagListRewriter
{
    /// <summary>True si alguna etiqueta de <paramref name="currentTags"/> coincide (insensible a
    /// mayúsculas) con <paramref name="sourceTags"/> — comprobación barata para saltarse por completo las
    /// entidades que la operación no toca.</summary>
    public static bool Matches(IEnumerable<string> currentTags, IReadOnlySet<string> sourceTags) =>
        currentTags.Any(sourceTags.Contains);

    /// <summary>Devuelve una nueva lista con cada etiqueta de <paramref name="sourceTags"/> sustituida por
    /// <paramref name="targetTag"/> (una sola vez, en la posición de su primera aparición) o eliminada si
    /// <paramref name="targetTag"/> es <see langword="null"/>. El resto de etiquetas se conserva en su
    /// orden original, deduplicando de forma insensible a mayúsculas — necesario porque fusionar puede
    /// hacer que la entidad acabe con la misma etiqueta repetida (p. ej. ya tenía "trabajo" y también
    /// "curro", y ambas se fusionan en "trabajo"). <paramref name="sourceTags"/> debe compararse con
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> (el llamador construye el <see cref="HashSet{T}"/>
    /// con ese comparador).</summary>
    public static List<string> Apply(IEnumerable<string> currentTags, IReadOnlySet<string> sourceTags, string? targetTag)
    {
        var result = new List<string>();
        var targetAdded = false;

        foreach (var tag in currentTags)
        {
            if (sourceTags.Contains(tag))
            {
                if (targetTag is not null && !targetAdded && !result.Contains(targetTag, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(targetTag);
                    targetAdded = true;
                }
                continue;
            }

            if (!result.Contains(tag, StringComparer.OrdinalIgnoreCase)) result.Add(tag);
        }

        return result;
    }
}
