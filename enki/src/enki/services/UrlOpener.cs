using System.Diagnostics;

namespace enki.Services;

/// <summary>Abre una URL en el navegador por defecto del sistema — mismo patrón try/catch-e-ignorar que
/// el Ctrl+click de enlaces en <c>EditorView</c> (no hay diálogo de error si no hay navegador asociado,
/// solo se ignora en silencio).</summary>
public static class UrlOpener
{
    public static void Open(string url)
    {
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
