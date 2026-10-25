using System;

namespace enki.Models;

/// <summary>Se lanza cuando <c>VaultService.WriteContentChecked</c> se dispone a escribir una nota y el
/// archivo que hay en disco ya no es el que quien pide la escritura leyó por última vez: otro programa
/// (Syncthing, Dropbox, otro editor) lo tocó mientras la pestaña estaba abierta. La escritura se aborta
/// <em>antes</em> de tocar nada, así que el contenido ajeno sigue intacto en disco y el del editor sigue
/// intacto en memoria; quien llama decide cuál gana.
///
/// <para>Es un tipo propio, y no un <see cref="System.IO.IOException"/> cualquiera, porque no es un fallo
/// de E/S: el disco funciona perfectamente y la operación se rechaza a propósito. El
/// <c>catch (IOException …)</c> que ya envuelve cada guardado lo trataría como "no se pudo guardar,
/// reintenta en el siguiente tick", que es justo lo contrario de lo que hay que hacer aquí (reintentar
/// pisaría el cambio externo en cuanto el disco cooperase).</para>
///
/// <para>Es la red de seguridad independiente de <see cref="Services.VaultChangeWatcher"/>: cubre los
/// casos en los que el vigilante no llegó a enterarse — desbordamiento de su búfer, unidad de red sin
/// notificaciones, suspensión del equipo — porque esta comprobación mira el disco en el mismo instante de
/// escribir.</para></summary>
public sealed class ExternalModificationException : Exception
{
    public ExternalModificationException(string fullPath, VaultExternalChangeKind kind)
        : base($"'{fullPath}' cambió fuera de Enki ({kind}); la escritura se ha abortado.")
    {
        FullPath = fullPath;
        Kind = kind;
    }

    public string FullPath { get; }

    /// <summary>Qué se encontró en disco: <see cref="VaultExternalChangeKind.Modified"/> (contenido
    /// distinto) o <see cref="VaultExternalChangeKind.Deleted"/> (el archivo ya no está).</summary>
    public VaultExternalChangeKind Kind { get; }
}
