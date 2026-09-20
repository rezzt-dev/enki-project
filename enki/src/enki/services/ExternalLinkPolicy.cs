using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace enki.Services;

/// <summary>Por qué no se abre el destino de un enlace de una nota. <see cref="None"/> es el único valor
/// que significa "adelante".</summary>
public enum ExternalLinkBlockReason
{
    None,

    /// <summary>El esquema no es ninguno de <see cref="ExternalLinkPolicy.SafeSchemes"/>:
    /// <c>javascript:</c>, <c>file:</c>, <c>ms-msdt:</c>, una ruta UNC <c>\\servidor\recurso</c>…</summary>
    UnsupportedScheme,

    /// <summary>Ruta local válida, pero fuera del vault.</summary>
    OutsideVault,

    /// <summary>Ruta dentro del vault cuya extensión ejecuta algo al abrirla.</summary>
    ExecutableFile,

    /// <summary>Ruta dentro del vault que no existe: enlace roto.</summary>
    Missing,

    /// <summary>Un ancla dentro del propio documento (<c>[x](#sección)</c>). No es un fallo ni algo que
    /// avisar: Enki todavía no navega a anclas, así que el clic simplemente no hace nada.</summary>
    InPageAnchor
}

/// <summary>Destino ya validado de un enlace. <see cref="Target"/> no es null solo cuando
/// <see cref="Reason"/> es <see cref="ExternalLinkBlockReason.None"/>, y es exactamente lo que se le puede
/// entregar al sistema — nunca el texto crudo de la nota.</summary>
public sealed record ExternalLinkResolution(string? Target, ExternalLinkBlockReason Reason)
{
    public static ExternalLinkResolution Allowed(string target) => new(target, ExternalLinkBlockReason.None);
    public static ExternalLinkResolution Blocked(ExternalLinkBlockReason reason) => new(null, reason);
}

/// <summary>
/// Qué destinos de un enlace escrito en una nota se pueden entregar al sistema operativo, y cuáles no.
///
/// <para><b>Por qué existe.</b> El destino de un <c>[texto](destino)</c> es texto libre dentro de un
/// archivo Markdown, y un archivo Markdown puede no haberlo escrito quien lo lee: un vault sincronizado
/// entre equipos, una plantilla descargada, una carpeta compartida, un repositorio clonado. Hasta este
/// punto, el Ctrl+clic del editor pasaba ese texto tal cual a <c>Process.Start(UseShellExecute = true)</c>,
/// que es el equivalente a un doble clic del usuario: <c>[Documentación](\\host\share\x.exe)</c>,
/// <c>[Documentación](./payload.desktop)</c> o un esquema de handler del sistema ejecutaban lo que fuera,
/// con el rótulo del enlace tapando el destino real (.docs/auditoria-seguridad-y-fallos-criticos.md,
/// SEC-01).</para>
///
/// <para><b>Qué se permite.</b> Una URL <c>http</c>/<c>https</c>/<c>mailto</c> —la lista blanca que el
/// exportador a HTML ya aplicaba por su cuenta y que ahora vive aquí para que exportación y editor
/// compartan un solo vocabulario— o un archivo que exista <em>dentro del vault</em> y cuya extensión no
/// ejecute nada. Todo lo demás se rechaza con un motivo concreto, para que el aviso al usuario pueda
/// decir qué pasó en vez de un "no se pudo abrir" genérico.</para>
///
/// <para>Es estática y pura salvo la comprobación de existencia en disco, así que se puede probar
/// directamente — ver <c>ExternalLinkPolicyTests</c>.</para>
/// </summary>
public static class ExternalLinkPolicy
{
    /// <summary>Esquemas que se abren sin más preguntas. Deliberadamente corto: son los tres que un
    /// documento de texto puede necesitar y ninguno de ellos ejecuta nada por sí mismo.</summary>
    public static readonly IReadOnlySet<string> SafeSchemes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

    /// <summary>Extensiones que el sistema ejecuta —o que pueden ejecutar algo— al abrirlas, y que por
    /// tanto no se entregan aunque el archivo viva dentro del vault. La lista cubre las tres plataformas a
    /// la vez a propósito: un vault se sincroniza entre equipos, así que un <c>.desktop</c> dejado en un
    /// Windows o un <c>.bat</c> en un Linux tienen que seguir bloqueados cuando ese vault llegue a la
    /// máquina donde sí se ejecutan.</summary>
    private static readonly HashSet<string> ExecutableExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".msi", ".msix", ".bat", ".cmd", ".com", ".scr", ".pif", ".cpl", ".msc", ".hta",
            ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".lnk", ".url", ".reg",
            ".jar", ".sh", ".bash", ".zsh", ".run", ".appimage", ".desktop", ".app", ".command",
            ".py", ".rb", ".pl", ".php", ".deb", ".rpm", ".pkg", ".dmg"
        };

    private static readonly Regex SchemeRegex = new(@"^[a-zA-Z][a-zA-Z0-9+.\-]*:", RegexOptions.Compiled);

    /// <summary>Una ruta absoluta de Windows (<c>C:\Users\…</c>) encaja también con
    /// <see cref="SchemeRegex"/> —una letra suelta seguida de <c>:</c> es indistinguible de un esquema—
    /// así que hace falta esta comprobación para no confundirla con un esquema desconocido.</summary>
    private static readonly Regex WindowsDrivePathRegex = new(@"^[a-zA-Z]:[\\/]", RegexOptions.Compiled);

    /// <summary>Longitud máxima del destino que se enseña en un aviso. Un enlace puede ser una URL enorme
    /// o un <c>data:</c> entero, y el panel de notificaciones no es sitio para volcarlo.</summary>
    private const int MaxDescribedLength = 80;

    public static bool IsSafeUrl(string url)
    {
        var match = SchemeRegex.Match(url);
        return match.Success && SafeSchemes.Contains(match.Value[..^1]);
    }

    /// <summary>Cierto para lo que tiene pinta de ruta del sistema de archivos y no de URL: sin esquema, o
    /// con una letra de unidad de Windows. Excluye <c>//servidor/…</c> (ruta de red sin esquema), que no es
    /// local aunque lo parezca.</summary>
    public static bool LooksLikeLocalPath(string url) =>
        url.Length > 0 && !url.StartsWith("//", StringComparison.Ordinal)
        && (!SchemeRegex.IsMatch(url) || WindowsDrivePathRegex.IsMatch(url));

    /// <summary>Decide qué hacer con <paramref name="target"/>, el destino crudo escrito en la nota.
    /// <paramref name="noteDirectory"/> es el ancla de las rutas relativas y <paramref name="vaultPath"/>
    /// la frontera de lo que se considera "del usuario": sin uno de los dos, una ruta local no se puede
    /// validar y se rechaza en vez de abrirse a ciegas.</summary>
    public static ExternalLinkResolution Resolve(string? target, string? noteDirectory, string? vaultPath)
    {
        var trimmed = target?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return ExternalLinkResolution.Blocked(ExternalLinkBlockReason.UnsupportedScheme);

        if (IsSafeUrl(trimmed)) return ExternalLinkResolution.Allowed(trimmed);

        // Un ancla del propio documento no es un destino del sistema; se distingue antes que nada para no
        // acabar tratándola como un archivo que falta.
        if (trimmed.StartsWith('#')) return ExternalLinkResolution.Blocked(ExternalLinkBlockReason.InPageAnchor);

        // Una ruta UNC (\\servidor\recurso) no lleva esquema y por tanto no la caza SchemeRegex, pero
        // abrirla es exactamente el caso que hay que impedir: un ejecutable servido desde la red.
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith(@"\\", StringComparison.Ordinal))
            return ExternalLinkResolution.Blocked(ExternalLinkBlockReason.UnsupportedScheme);

        if (!LooksLikeLocalPath(trimmed)) return ExternalLinkResolution.Blocked(ExternalLinkBlockReason.UnsupportedScheme);

        if (string.IsNullOrWhiteSpace(vaultPath)) return ExternalLinkResolution.Blocked(ExternalLinkBlockReason.OutsideVault);

        var decoded = DecodePath(trimmed);
        string full;
        string root;
        try
        {
            var combined = Path.IsPathRooted(decoded) || string.IsNullOrEmpty(noteDirectory)
                ? decoded
                : Path.Combine(noteDirectory, decoded);
            full = Path.GetFullPath(combined);
            root = Path.GetFullPath(vaultPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ExternalLinkResolution.Blocked(ExternalLinkBlockReason.OutsideVault);
        }

        if (!IsInside(full, root)) return ExternalLinkResolution.Blocked(ExternalLinkBlockReason.OutsideVault);
        if (ExecutableExtensions.Contains(Path.GetExtension(full)))
            return ExternalLinkResolution.Blocked(ExternalLinkBlockReason.ExecutableFile);
        if (!File.Exists(full) && !Directory.Exists(full))
            return ExternalLinkResolution.Blocked(ExternalLinkBlockReason.Missing);

        return ExternalLinkResolution.Allowed(full);
    }

    /// <summary>Recorta el destino para enseñarlo en un aviso sin llenar la línea.</summary>
    public static string Describe(string? target)
    {
        var trimmed = target?.Trim() ?? string.Empty;
        return trimmed.Length <= MaxDescribedLength
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, MaxDescribedLength - 1), "…");
    }

    /// <summary>La comparación se hace sobre rutas ya normalizadas y con el separador final añadido, para
    /// que <c>/vault-privado</c> no cuente como dentro de <c>/vault</c>.</summary>
    private static bool IsInside(string fullPath, string root)
    {
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, VaultService.PathComparison);
    }

    /// <summary>Deshace el porcentaje-escapado con el que se escribe un espacio en un destino Markdown y
    /// normaliza las barras al separador del sistema — mismo tratamiento que
    /// <c>Views.Editor.Markdown.MarkdownImageResolver</c> da a la ruta de una imagen.</summary>
    private static string DecodePath(string value)
    {
        var decoded = value;
        if (decoded.Contains('%', StringComparison.Ordinal))
        {
            try
            {
                decoded = Uri.UnescapeDataString(decoded);
            }
            catch (UriFormatException)
            {
                // Un '%' suelto que no forma un escape válido es parte del nombre del archivo.
            }
        }
        return decoded.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }
}
