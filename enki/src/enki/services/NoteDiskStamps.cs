using System;
using System.IO;
using System.Security.Cryptography;
using enki.Models;

namespace enki.Services;

/// <summary>Lectura y comparación de la <see cref="NoteDiskStamp">huella en disco</see> de una nota: el
/// primitivo con el que Enki distingue "esto lo escribí yo" de "esto lo cambió otro programa". Lo usan
/// <see cref="VaultChangeWatcher"/> (al evaluar un evento del sistema de archivos),
/// <c>VaultService.WriteContentChecked</c> (comprobación previa a escribir) y <c>EditorViewModel</c> (al
/// abrir una pestaña). Ver <c>.docs/project-info/cambios-externos-en-el-vault.md</c>.
///
/// <para>Estático y sin estado, como <see cref="DurableFile"/> o <see cref="NoteTagHelper"/>: todo lo que
/// necesita está en la ruta que recibe, no depende del vault activo ni de ninguna caché, y así puede
/// probarse sobre un directorio temporal sin construir un <see cref="VaultService"/>.</para></summary>
public static class NoteDiskStamps
{
    /// <summary>Lee la huella actual de <paramref name="fullPath"/>, o <see langword="null"/> si el archivo
    /// no existe o no se puede leer. Hashea los bytes del archivo tal cual están —sin descifrar— para que
    /// valga igual en una nota normal que en una de sesión protegida (ver <see cref="NoteDiskStamp"/>).</summary>
    public static NoteDiskStamp? TryRead(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            return info.Exists ? Read(info) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning($"No se pudo leer el estado en disco de '{fullPath}'", ex);
            return null;
        }
    }

    /// <summary>Compara los bytes del archivo con <paramref name="baseline"/>. Fecha y tamaño pueden
    /// coincidir tras una edición externa (sincronización o marcas de tiempo de baja resolución), así
    /// que nunca bastan para autorizar una escritura sobre el contenido del usuario.</summary>
    /// <param name="current">La huella recién leída, que el llamante debería adoptar como nueva
    /// referencia salvo que el resultado sea <see cref="NoteDiskState.Missing"/>.</param>
    /// <exception cref="IOException">El archivo existe pero no se pudo leer (otro proceso lo tiene
    /// abierto en exclusiva mientras lo escribe). Se propaga a propósito: "no lo sé todavía" no es lo
    /// mismo que "no ha cambiado", y tratarlo como lo segundo es justo el fallo que esto evita.</exception>
    public static NoteDiskState Check(string fullPath, NoteDiskStamp baseline, out NoteDiskStamp current)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            current = baseline;
            return NoteDiskState.Missing;
        }

        current = Read(info);
        return current.HasSameContentAs(baseline) ? NoteDiskState.Unchanged : NoteDiskState.Modified;
    }

    /// <summary>Lee la huella de los bytes actuales. Los metadatos se toman antes de leer para no
    /// atribuir al contenido leído la fecha de una escritura posterior.</summary>
    public static NoteDiskStamp Read(FileInfo info)
    {
        var lastWriteUtc = info.LastWriteTimeUtc;
        var sizeBytes = info.Length;

        using var stream = new FileStream(info.FullName, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            // Un sincronizador puede tener el archivo abierto para escribir mientras lo copia: pedir
            // acceso exclusivo convertiría en error lo que aquí solo necesita una lectura.
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan
        });

        return new NoteDiskStamp(lastWriteUtc, sizeBytes, Convert.ToHexString(SHA256.HashData(stream)));
    }
}
