using System.Collections.Generic;
using enki.Models;

namespace enki.Services;

/// <summary>Mutación masiva de etiquetas de notas para el Gestor global de etiquetas
/// (<see cref="TagManagerService"/>) — separado de <c>VaultService.cs</c> por el mismo motivo que
/// <c>VaultService.NoteHistory.cs</c>: sigue siendo la misma clase parcial, solo mantiene ese archivo
/// centrado en la lectura/escritura básica de notas.</summary>
public sealed partial class VaultService
{
    /// <summary>Renombra, fusiona o elimina una etiqueta en todas las notas activas del vault (raíz +
    /// todas las sesiones desbloqueadas, vía <see cref="GetAllNotesAcrossVaultWithContent"/> — las notas
    /// de la Papelera quedan fuera a propósito, igual que <see cref="TagManagerService"/> excluye el resto
    /// de la Papelera). Para cada nota cuyas <see cref="NoteFile.Tags"/> coinciden con
    /// <paramref name="sourceTags"/>: reescribe su lista de etiquetas con <see cref="TagListRewriter.Apply"/>,
    /// persiste el nuevo frontmatter con <see cref="NoteTagHelper.SetTags"/> conservando el resto de claves,
    /// y captura un snapshot con <see cref="NoteVersionTrigger.PreBulkOperation"/> — el mismo trigger y el
    /// mismo patrón de "snapshot justo antes de tocarla sin que el usuario revise cada cambio" que ya usa
    /// <c>RewriteWikilinksToRenamedNote</c> al renombrar una nota — para que un renombrado/fusión masivo de
    /// etiquetas sea recuperable desde el historial de versiones de cada nota afectada.</summary>
    /// <param name="sourceTags">Etiquetas a sustituir/eliminar — debe construirse con
    /// <see cref="System.StringComparer.OrdinalIgnoreCase"/> para que la coincidencia sea insensible a
    /// mayúsculas, igual que en el resto del Gestor de etiquetas.</param>
    /// <param name="targetTag">Nombre destino (renombrar/fusionar), o <see langword="null"/> para eliminar
    /// la etiqueta sin sustituirla por otra. Se pasa tal cual a <see cref="NoteTagHelper.SetTags"/>, que ya
    /// lo normaliza a minúsculas — mismo comportamiento que el resto de ediciones de etiquetas de notas.</param>
    /// <returns>Número de notas cuyo contenido se reescribió.</returns>
    public int ReplaceTagAcrossVault(IReadOnlySet<string> sourceTags, string? targetTag)
    {
        EnsureVaultAvailable();

        var touched = 0;
        foreach (var (note, content) in GetAllNotesAcrossVaultWithContent())
        {
            if (!TagListRewriter.Matches(note.Tags, sourceTags)) continue;

            var newTags = TagListRewriter.Apply(note.Tags, sourceTags, targetTag);
            var newContent = NoteTagHelper.SetTags(content, newTags);
            if (newContent == content) continue;

            CaptureSnapshot(note.Clone(), content, NoteVersionTrigger.PreBulkOperation);
            WriteFileAtomic(note.FullPath, newContent);
            _noteCache.Invalidate(note.FullPath);
            touched++;
        }

        return touched;
    }
}
