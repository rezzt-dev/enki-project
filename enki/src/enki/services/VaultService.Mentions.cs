using enki.Models;

namespace enki.Services;

/// <summary>Enlazado en un clic de las menciones sin enlazar (<see cref="NoteRelationsService"/>) —
/// separado de <c>VaultService.cs</c> por el mismo motivo que <c>VaultService.Tags.cs</c>: sigue siendo la
/// misma clase parcial, solo mantiene ese archivo centrado en la lectura y escritura básica de notas.</summary>
public sealed partial class VaultService
{
    /// <summary>Convierte en <c>[[enlace]]</c> la primera mención sin enlazar de <paramref name="target"/>
    /// dentro de la nota <paramref name="mention"/>. Relee la nota de disco antes de tocarla (la mención se
    /// calculó hace un rato y puede haber cambiado), toma un snapshot en el historial de versiones y
    /// escribe con <see cref="WriteContentChecked"/>, así que un cambio externo posterior a la lectura
    /// aborta la escritura sin pisarlo.</summary>
    /// <returns><see langword="true"/> si la nota se reescribió; <see langword="false"/> si ya no había
    /// ninguna mención que enlazar (la nota cambió desde el cálculo) o la nota ya no existe.</returns>
    /// <exception cref="ExternalModificationException">El archivo cambió entre la lectura y la escritura.</exception>
    public bool LinkMention(UnlinkedMention mention, NoteFile target)
    {
        EnsureVaultAvailable();

        if (ReadFromDisk(mention.FullPath) is not { } fresh) return false;

        var linked = NoteRelationsService.LinkFirstMention(fresh.Content, target, mention.LinkName);
        if (linked is null || linked == fresh.Content) return false;

        CaptureSnapshot(fresh.Note.Clone(), fresh.Content, NoteVersionTrigger.PreBulkOperation);
        WriteContentChecked(fresh.Note, linked, fresh.Stamp);
        _noteCache.Invalidate(mention.FullPath);
        return true;
    }
}
