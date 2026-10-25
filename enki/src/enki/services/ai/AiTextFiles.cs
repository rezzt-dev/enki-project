using System;
using System.IO;
using System.Text;
using enki.Models;

namespace enki.Services;

/// <summary>Por qué un archivo del vault no se puede enviar al asistente como texto.</summary>
public enum AiTextFileProblem
{
    None,
    /// <summary>Imagen, PDF, audio, vídeo u otro formato binario: no se carga como texto.</summary>
    NotText,
    TooLarge
}

/// <summary>Reglas comunes para leer como texto un archivo del vault que no es una nota: las usan tanto la
/// herramienta <c>read_text_file</c> del asistente como los archivos que el usuario adjunta a un mensaje, para
/// que las dos vías acepten y rechacen exactamente lo mismo. Nunca interpreta ni ejecuta el contenido.</summary>
public static class AiTextFiles
{
    /// <summary>Tamaño máximo de un archivo que se lee entero.</summary>
    public const long MaxBytes = 1_000_000;

    /// <summary>Lo que se sabe sin abrir el archivo: su tipo por extensión y su tamaño.</summary>
    public static AiTextFileProblem Check(VaultFile file) =>
        file.Kind != VaultFileKind.Code ? AiTextFileProblem.NotText
        : file.SizeBytes > MaxBytes ? AiTextFileProblem.TooLarge
        : AiTextFileProblem.None;

    /// <summary>Lee el archivo como UTF-8. Lanza <see cref="InvalidDataException"/> si, pese a su extensión, no
    /// es texto (contiene bytes nulos).</summary>
    public static string Read(VaultService vault, string fullPath)
    {
        var text = Encoding.UTF8.GetString(vault.ReadFileBytes(fullPath));
        return text.Contains('\0', StringComparison.Ordinal)
            ? throw new InvalidDataException($"\"{Path.GetFileName(fullPath)}\" is not a text file.")
            : text;
    }

    public static VaultFile Describe(string fullPath)
    {
        var info = new FileInfo(fullPath);
        return new VaultFile
        {
            FullPath = fullPath,
            ModifiedUtc = info.LastWriteTimeUtc,
            SizeBytes = info.Length,
            Kind = VaultFile.KindFromPath(fullPath)
        };
    }
}
