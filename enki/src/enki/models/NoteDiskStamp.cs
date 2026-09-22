using System;

namespace enki.Models;

/// <summary>Resultado de comparar lo que hay en disco con la huella que se tenía — ver
/// <see cref="Services.NoteDiskStamps.Check"/>.</summary>
public enum NoteDiskState
{
    /// <summary>El contenido del archivo es el mismo que cuando se tomó la huella. Puede que su fecha de
    /// modificación sí haya cambiado (un sincronizador que reescribe bytes idénticos): la huella recién
    /// leída es la buena y conviene adoptarla para no repetir la comprobación cara.</summary>
    Unchanged,

    /// <summary>El archivo existe y su contenido es distinto.</summary>
    Modified,

    /// <summary>El archivo ya no está en esa ruta.</summary>
    Missing
}

/// <summary>Huella de una nota tal y como está en disco en un instante dado: fecha de modificación,
/// tamaño y hash del contenido del archivo. Es la unidad con la que Enki distingue "esto lo escribí yo"
/// de "esto lo cambió otro programa" (Syncthing, Dropbox, otro editor) — ver
/// <see cref="Services.NoteDiskStamps"/> y <see cref="Services.VaultChangeWatcher"/>.
///
/// <para><b>Por qué tres campos y no solo la fecha.</b> La granularidad de <c>LastWriteTimeUtc</c> no es la
/// misma en todos los sistemas de archivos (FAT/exFAT redondean a 2 s, NTFS y ext4 no coinciden entre sí),
/// y un sincronizador puede reescribir un archivo byte a byte idéntico solo para refrescar su marca de
/// tiempo. Fecha y tamaño sirven como metadatos de diagnóstico; el hash decide siempre si el contenido
/// cambió, incluso cuando el sincronizador conserva ambos metadatos. También evita un diálogo de
/// conflicto injustificado cuando solo cambia la fecha.</para>
///
/// <para><b>El hash es de los bytes del archivo, no del texto.</b> Para una nota dentro de una sesión
/// cifrada eso significa hashear el criptograma, que es exactamente lo que interesa: permite saber que el
/// archivo cambió sin descifrarlo ni necesitar la sesión desbloqueada.</para></summary>
public readonly record struct NoteDiskStamp(DateTime LastWriteUtc, long SizeBytes, string ContentHash)
{
    /// <summary>Comparación barata (sin tocar el disco) de los metadatos que <see cref="System.IO.FileInfo"/>
    /// ya trae consigo. La igualdad no garantiza que el contenido siga siendo el mismo.</summary>
    public bool HasSameMetadataAs(NoteDiskStamp other) =>
        LastWriteUtc == other.LastWriteUtc && SizeBytes == other.SizeBytes;

    /// <summary>Comparación del contenido real. Es la que manda cuando los metadatos difieren.</summary>
    public bool HasSameContentAs(NoteDiskStamp other) =>
        string.Equals(ContentHash, other.ContentHash, StringComparison.Ordinal);
}
