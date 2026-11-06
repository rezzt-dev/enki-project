using System;
using System.IO;
using CSharpMath.SkiaSharp;
using SkiaSharp;

namespace enki.Services;

/// <summary>Render local y sin estado de una fórmula LaTeX a PNG transparente. CSharpMath compone los
/// glifos en proceso: no usa navegador, CDN, comandos externos ni archivos temporales.</summary>
public static class LatexRenderService
{
    public const int MaxSourceLength = 8_192;

    /// <summary>Devuelve el PNG o null si la expresión no es válida o rebasa el límite defensivo. El
    /// llamador decide la caché y el timeout porque ambos dependen del ciclo de vida de la vista.</summary>
    public static byte[]? RenderPng(string latex, bool display, float fontSize, SKColor foreground)
    {
        if (string.IsNullOrWhiteSpace(latex) || latex.Length > MaxSourceLength) return null;

        try
        {
            var painter = new MathPainter
            {
                FontSize = fontSize,
                TextColor = foreground,
                DisplayErrorInline = false,
                LineStyle = display ? CSharpMath.Atom.LineStyle.Display : CSharpMath.Atom.LineStyle.Text,
                LaTeX = latex
            };
            // ErrorMessage vuelve vacío —no null— cuando el LaTeX es válido, y Display no se compone
            // hasta que se dibuja: preguntar por él antes descartaría toda fórmula correcta.
            if (!string.IsNullOrEmpty(painter.ErrorMessage)) return null;

            using var rendered = painter.DrawAsStream();
            if (rendered is null) return null;
            using var buffer = new MemoryStream();
            rendered.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        {
            LogService.Warning("No se pudo renderizar una fórmula LaTeX; se mostrará su código", ex);
            return null;
        }
    }
}
