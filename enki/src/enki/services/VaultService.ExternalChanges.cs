using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using enki.Models;

namespace enki.Services;

/// <summary>Cambios hechos sobre el vault desde fuera de Enki (Syncthing, Dropbox, otro editor, un script)
/// — la parte que necesita conocer el vault. Leer y comparar huellas es
/// <see cref="NoteDiskStamps"/>, vigilar el disco es <see cref="VaultChangeWatcher"/>, y decidir qué hacer
/// con cada caso, <c>EditorViewModel</c>. Ver
/// <c>.docs/project-info/cambios-externos-en-el-vault.md</c>.
///
/// <para><b>Qué añade este archivo.</b> La comprobación previa a escribir que convierte el autoguardado en
/// una operación que nunca pisa un cambio ajeno (<see cref="WriteContentChecked"/>), la relectura que
/// devuelve contenido y huella coherentes entre sí sin pasar por la caché (<see cref="ReadFromDisk"/>) y el
/// punto único por el que se anuncia que un cambio externo se aceptó
/// (<see cref="ApplyExternalChange"/>), para que las pantallas se refresquen sin acoplarse al editor —
/// mismo patrón que <see cref="KanbanService.BoardChanged"/>.</para></summary>
public sealed partial class VaultService
{
    /// <summary>Se dispara cuando un cambio hecho fuera de Enki se ha aceptado y aplicado sobre una nota
    /// (recarga silenciosa o resolución de un conflicto). Sirve para que las pantallas que listan o
    /// indexan notas —hoy <c>NotesViewModel</c>— se refresquen sin que el editor tenga que conocerlas,
    /// igual que <see cref="KanbanService.BoardChanged"/>. Lo levanta <see cref="ApplyExternalChange"/>,
    /// que ya ha invalidado las cachés antes de avisar, así que un suscriptor que relea encontrará el
    /// contenido nuevo y no el anterior.
    ///
    /// <para>Se emite en el hilo del que llama. Hoy siempre es el de interfaz, porque quien resuelve un
    /// cambio externo es el editor; un futuro llamante en segundo plano tendría que marshalarlo él, misma
    /// convención que <see cref="VaultSizeChanged"/>.</para></summary>
    public event Action<string>? ExternalChangeApplied;

    /// <summary>Escribe una nota igual que <see cref="WriteContent"/>, pero solo después de comprobar que
    /// el archivo en disco sigue siendo el que <paramref name="baseline"/> describe. Si no lo es, no
    /// escribe nada y lanza <see cref="ExternalModificationException"/>.
    ///
    /// <para>Con <paramref name="baseline"/> nulo se comporta exactamente como <see cref="WriteContent"/>:
    /// es el caso de quien nunca leyó el archivo y por tanto no tiene nada con lo que comparar (una nota
    /// recién creada, un guardado desde una pantalla que no vigila esa nota). Un archivo que no existe
    /// cuando sí había huella se trata como cambio externo —lo borraron fuera— y no como "pues lo creo de
    /// nuevo", porque recrearlo en silencio escondería el borrado.</para>
    ///
    /// <para>Un fallo de E/S al comprobar (el sincronizador tiene el archivo abierto justo ahora) también
    /// aborta la escritura: se propaga como <see cref="IOException"/>, que es lo que el llamante ya sabe
    /// tratar como "no se pudo guardar, se reintenta en el siguiente tick".</para></summary>
    /// <returns>La huella de lo que acaba de quedar escrito, para que el llamante la adopte como nueva
    /// referencia.</returns>
    public NoteDiskStamp? WriteContentChecked(NoteFile note, string content, NoteDiskStamp? baseline)
    {
        // Antes de mirar el archivo: un vault entero que ya no está (unidad desconectada, carpeta movida)
        // no es "alguien borró esta nota", y confundirlos llevaría al usuario a un diálogo de conflicto
        // por una nota cuyo problema real es que no hay dónde escribirla.
        EnsureVaultAvailable();

        if (baseline is { } known)
        {
            var state = NoteDiskStamps.Check(note.FullPath, known, out _);
            if (state != NoteDiskState.Unchanged)
            {
                throw new ExternalModificationException(
                    note.FullPath,
                    state == NoteDiskState.Missing ? VaultExternalChangeKind.Deleted : VaultExternalChangeKind.Modified);
            }
        }

        WriteContent(note, content);
        return NoteDiskStamps.TryRead(note.FullPath);
    }

    /// <summary>Relee de disco una nota que cambió fuera de Enki, saltándose la caché en memoria — que
    /// se invalida aquí antes de leer, porque <see cref="VaultNoteCache"/> se refresca comparando fechas
    /// de modificación y este es precisamente el caso en que esa fecha puede no haber cambiado (dos
    /// escrituras dentro de la misma marca de tiempo, un sincronizador que la preserva). Devuelve
    /// <see langword="null"/> si el archivo ya no existe.</summary>
    /// <exception cref="SessionLockedException">La nota vive en una sesión protegida que está bloqueada;
    /// su contenido no se lee ni se compara hasta que se desbloquee.</exception>
    public (NoteFile Note, string Content, NoteDiskStamp Stamp)? ReadFromDisk(string fullPath)
    {
        _noteCache.Invalidate(fullPath);

        var info = new FileInfo(fullPath);
        if (!info.Exists) return null;

        // Contenido y hash proceden de la misma lectura, también en sesiones cifradas. Dos lecturas
        // independientes podrían asociar el texto viejo con la huella de una edición externa nueva.
        var lastWriteUtc = info.LastWriteTimeUtc;
        var raw = File.ReadAllBytes(fullPath);
        var stamp = new NoteDiskStamp(lastWriteUtc, raw.LongLength, Convert.ToHexString(SHA256.HashData(raw)));
        var encryptedRoot = ResolveEncryptedSessionRoot(fullPath);
        byte[] plaintext;
        try { plaintext = encryptedRoot is null ? raw : _encryptionService.Decrypt(encryptedRoot, raw); }
        catch (CryptographicException ex)
        {
            throw new IOException($"No se pudo descifrar la nota '{fullPath}'.", ex);
        }
        using var reader = new StreamReader(new MemoryStream(plaintext), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var content = reader.ReadToEnd();
        return (ReadNoteMetadata(info, content), content, stamp);
    }

    /// <summary>Deja constancia de que un cambio externo sobre <paramref name="fullPath"/> se ha aceptado:
    /// tira las cachés que podrían seguir sirviendo el contenido anterior y avisa vía
    /// <see cref="ExternalChangeApplied"/>. Lo llama el editor después de recargar la nota o de resolver
    /// un conflicto cargando la versión de disco.</summary>
    public void ApplyExternalChange(string fullPath)
    {
        _noteCache.Invalidate(fullPath);
        InvalidateVaultSizeCache();
        ExternalChangeApplied?.Invoke(fullPath);
    }

}
