using System;
using System.IO;

namespace enki.Services;

/// <summary>Primitivas de escritura durable: el contenido se escribe en un temporal hermano, se fuerza
/// al dispositivo antes del renombrado y solo entonces sustituye al destino.</summary>
internal static class DurableFile
{
    internal static void WriteAllBytesAtomic(string path, byte[] content, bool privateFile = false) =>
        WriteAtomic(path, stream => stream.Write(content), privateFile);

    internal static void WriteAllTextAtomic(string path, string content, bool privateFile = false) =>
        WriteAtomic(path, stream =>
        {
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write(content);
            writer.Flush();
        }, privateFile);

    internal static void CopyAtomic(string path, Stream source, long maxBytes, bool privateFile = false) =>
        WriteAtomic(path, destination => CopyBounded(source, destination, maxBytes), privateFile);

    private static void WriteAtomic(string path, Action<FileStream> write, bool privateFile)
    {
        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.SequentialScan
            };
            if (privateFile && !OperatingSystem.IsWindows())
                options.UnixCreateMode = PrivateDataPermissions.FileMode;

            using (var stream = new FileStream(temporary, options))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            if (privateFile) PrivateDataPermissions.EnsurePrivateFile(path);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                LogService.Warning($"No se pudo eliminar el archivo temporal '{temporary}'", cleanupError);
            }
            throw;
        }
    }

    private static void CopyBounded(Stream source, Stream destination, long maxBytes)
    {
        var buffer = new byte[81920];
        long copied = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) return;
            copied += read;
            if (copied > maxBytes)
                throw new InvalidDataException($"El archivo supera el límite de {maxBytes} bytes.");
            destination.Write(buffer, 0, read);
        }
    }
}
