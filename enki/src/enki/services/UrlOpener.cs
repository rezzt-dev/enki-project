using System.Diagnostics;

namespace enki.Services;

/// <summary>Abre una URL en el navegador por defecto del sistema — mismo patrón try/catch-e-ignorar que
/// el Ctrl+click de enlaces en <c>EditorView</c> (no hay diálogo de error si no hay navegador asociado,
/// solo se ignora en silencio).</summary>
public static class UrlOpener
{
    /// <summary>Solo abre lo que <see cref="ExternalLinkPolicy.IsSafeUrl"/> acepta. Hoy todas las llamadas
    /// le pasan una constante del propio código, así que el filtro no rechaza nada real — está por lo que
    /// es esta clase: un ayudante público cuya firma invita a pasarle una cadena de cualquier procedencia,
    /// y el destino acaba en <c>UseShellExecute</c>, que ejecuta lo que sea que el sistema tenga asociado
    /// (.docs/auditoria-seguridad-y-fallos-criticos.md, SEC-01).</summary>
    public static void Open(string url)
    {
        if (!ExternalLinkPolicy.IsSafeUrl(url))
        {
            LogService.Warning($"URL no abierta por política de enlaces: '{ExternalLinkPolicy.Describe(url)}'");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Ignorar: sin navegador asociado o URL no soportada.
        }
    }
}
