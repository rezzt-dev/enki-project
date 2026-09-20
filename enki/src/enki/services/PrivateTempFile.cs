using System;
using System.IO;

namespace enki.Services;

/// <summary>
/// Dónde y cómo escribe Enki sus archivos temporales, para que ninguno pueda ser secuestrado por otra
/// cuenta local ni leído por ella.
///
/// <para><b>Por qué existe.</b> El directorio temporal del sistema es compartido por todos los usuarios
/// de la máquina en Linux y macOS (<c>/tmp</c>, típicamente <c>1777</c>). Escribir ahí con un nombre fijo
/// —lo que hacía <see cref="NotificationSoundPlayer"/> con <c>enki-notification-sound.wav</c>— permite que
/// otra cuenta deje ese nombre creado de antemano como enlace simbólico a un archivo del usuario de Enki
/// (<c>~/.bashrc</c>, <c>~/.config/autostart/…</c>): <c>File.Create</c> sigue el enlace y trunca el
/// objetivo (CWE-377/CWE-59). Y escribir contenido descifrado con la máscara heredada —lo que hacía
/// <see cref="VaultService.ResolveReadablePath"/>, que ponía el modo <em>después</em> de escribir— lo deja
/// legible por cualquier cuenta local durante la ventana entre las dos operaciones, justo con el contenido
/// que el usuario cifró a propósito (.docs/auditoria-seguridad-y-fallos-criticos.md, SEC-03 y SEC-04).</para>
///
/// <para><b>Qué hace.</b> Dos defensas que se refuerzan: todo vive dentro de un directorio propio del
/// usuario creado con modo <c>0700</c> y <b>comprobado</b> en cada uso (existe, no es un enlace simbólico,
/// y su modo es exactamente el esperado); y cada archivo nace con <c>FileMode.CreateNew</c> y modo
/// <c>0600</c> <em>antes</em> de recibir el primer byte, nunca sobre una ruta que pueda existir ya. Basta
/// con la primera para que la segunda sea redundante, y al revés — es deliberado: un fallo en una no deja
/// el contenido al descubierto.</para>
///
/// <para>En Windows <see cref="Path.GetTempPath"/> ya devuelve un directorio dentro del perfil del usuario
/// (<c>%LOCALAPPDATA%\Temp</c>), protegido por la ACL heredada del perfil, así que el subdirectorio no
/// añade nada allí; se crea igual por tener una sola ruta de código. De los modos POSIX, el del archivo
/// (<see cref="FileStreamOptions.UnixCreateMode"/>) se ignora allí sin lanzar nada y el del directorio va
/// por una API exclusiva de Unix, de ahí la única condición por plataforma que hay aquí.
/// <b>No verificado en Windows ni macOS</b>: el desarrollo y las pruebas de esta clase se han hecho en
/// Linux.</para>
/// </summary>
internal static class PrivateTempFile
{
    /// <summary>Solo el dueño puede entrar, listar y escribir. Que otra cuenta no pueda ni <em>listar</em>
    /// el directorio es lo que hace seguro usar dentro nombres fijos y predecibles.</summary>
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Directorio privado de esta cuenta dentro del temporal del sistema, creado si hace falta y
    /// validado siempre. Lanza <see cref="IOException"/> si la ruta existe pero no es utilizable con
    /// garantías —un enlace simbólico (que apuntaría la escritura a otro sitio) o un modo distinto de
    /// <c>0700</c> (que la dejaría legible por terceros)—: es preferible no escribir el temporal a
    /// escribirlo donde no toca, y quien llama ya sabe degradar (el reproductor de sonido pasa al
    /// siguiente eslabón de su cadena, y abrir un adjunto descifrado falla con un error visible en vez de
    /// filtrarlo).</summary>
    public static string ResolveDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"enki-{SanitizedUserName()}");
        // La sobrecarga con modo aplica el 0700 en la propia llamada al sistema que crea el directorio, no
        // en un chmod posterior, así que no hay ventana en la que exista con la máscara heredada. Es una API
        // exclusiva de Unix (lanza en Windows), de ahí la condición — allí el temporal ya está dentro del
        // perfil del usuario y no hay modo que poner. Si el directorio ya existía, ninguna de las dos ramas
        // toca su modo: de eso se encarga la comprobación de abajo, que es la que decide si fiarse de él.
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, DirectoryMode);

        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null)
            throw new IOException($"El directorio temporal privado '{path}' es un enlace simbólico.");

        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != DirectoryMode)
            throw new IOException($"El directorio temporal privado '{path}' no tiene modo 0700.");

        return path;
    }

    /// <summary>Escribe <paramref name="content"/> en un archivo nuevo de nombre irrepetible
    /// (<c>{prefix}{guid}{extension}</c>) dentro del directorio privado, y devuelve su ruta. El archivo
    /// nace con modo <c>0600</c>: nunca existe, ni un instante, con permisos más amplios. Si la escritura
    /// falla a medias, el archivo se borra antes de propagar el error — un temporal descifrado incompleto
    /// es exactamente lo que no debe quedarse en disco.</summary>
    public static string Write(string prefix, string extension, ReadOnlySpan<byte> content)
    {
        var path = Path.Combine(ResolveDirectory(), $"{prefix}{Guid.NewGuid():N}{extension}");
        try
        {
            using var stream = CreateExclusive(path);
            stream.Write(content);
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        return path;
    }

    /// <summary>Variante para contenido que ocupa un nombre estable dentro del directorio privado, en vez
    /// de uno irrepetible: se escribe primero en un archivo nuevo con nombre de <see cref="Guid"/> y solo
    /// al terminar se renombra encima del definitivo, que es el mismo patrón de escritura atómica que
    /// <see cref="JsonFileStore"/> y <c>VaultService.WriteFileAtomic</c> ya usan en el resto del proyecto.
    /// Así nadie llega a ver el archivo a medio escribir y, sobre todo, no se trunca nada siguiendo un
    /// enlace: el destino solo puede haberlo puesto esta misma cuenta, porque el directorio es
    /// <c>0700</c>.</summary>
    public static string WriteStable(string fileName, Stream content)
    {
        var directory = ResolveDirectory();
        var finalPath = Path.Combine(directory, fileName);
        var stagingPath = Path.Combine(directory, $"{fileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = CreateExclusive(stagingPath))
                content.CopyTo(stream);

            File.Move(stagingPath, finalPath, overwrite: true);
        }
        catch
        {
            TryDelete(stagingPath);
            throw;
        }

        return finalPath;
    }

    /// <summary>Borra los temporales que empiezan por <paramref name="prefix"/> y que quedaron de un
    /// arranque anterior. Barre dos sitios: el directorio privado (donde viven ahora) y la raíz del
    /// temporal del sistema (donde los dejaba cualquier versión anterior a este cambio, y que por tanto
    /// hay que seguir limpiando en la máquina de quien actualiza). Es best-effort de principio a fin: un
    /// temporal que otro proceso tenga abierto o que ya no esté no merece un error.</summary>
    public static void CleanupStale(string prefix, Action<string> delete)
    {
        foreach (var directory in CleanupDirectories())
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, $"{prefix}*");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Warning($"No se pudo listar '{directory}' para limpiar temporales de Enki", ex);
                continue;
            }

            foreach (var file in files) delete(file);
        }
    }

    private static string[] CleanupDirectories()
    {
        var systemTemp = Path.GetTempPath();
        try
        {
            return [ResolveDirectory(), systemTemp];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // El directorio privado no es utilizable en esta máquina; la raíz del temporal sigue mereciendo
            // el barrido, porque es donde están los restos de versiones anteriores.
            LogService.Warning("No se pudo abrir el directorio temporal privado para limpiarlo", ex);
            return [systemTemp];
        }
    }

    /// <summary><c>FileMode.CreateNew</c> falla si la ruta ya existe en vez de truncar lo que haya (o lo
    /// que un enlace apunte), y <see cref="FileStreamOptions.UnixCreateMode"/> —.NET 8— aplica el
    /// <c>0600</c> en la propia llamada al sistema que crea el archivo, no en un <c>chmod</c> posterior:
    /// ahí estaba exactamente la ventana TOCTOU de SEC-04.
    ///
    /// <para>El <c>if</c> de plataforma no es defensivo de más: el <i>setter</i> de esa propiedad está
    /// anotado <c>[UnsupportedOSPlatform("windows")]</c> en .NET 8, y este proyecto compila CA1416 como
    /// error, así que asignarla sin condición no compila. En Windows no se pierde nada — no hay modos POSIX
    /// y el temporal ya está dentro del perfil del usuario.</para></summary>
    private static FileStream CreateExclusive(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerReadWrite;
        return new FileStream(path, options);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning($"No se pudo borrar el temporal incompleto '{path}'", ex);
        }
    }

    /// <summary>El nombre de usuario solo sirve para que dos cuentas de la misma máquina no compitan por el
    /// mismo directorio; puede contener cualquier cosa (espacios, acentos, un <c>DOMINIO\usuario</c> en
    /// Windows), así que se reduce a caracteres seguros para un nombre de archivo en las tres plataformas.
    /// No es un secreto ni un identificador de seguridad: quien manda es el modo <c>0700</c>.</summary>
    private static string SanitizedUserName()
    {
        var raw = Environment.UserName;
        if (string.IsNullOrWhiteSpace(raw)) return "user";

        Span<char> buffer = stackalloc char[Math.Min(raw.Length, 32)];
        var length = 0;
        foreach (var c in raw)
        {
            if (length == buffer.Length) break;
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') buffer[length++] = c;
        }

        return length == 0 ? "user" : new string(buffer[..length]);
    }
}
