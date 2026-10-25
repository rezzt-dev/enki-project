using System;

namespace enki.Services.Export;

/// <summary>Formatos de salida de "Exportar vista" (T8).</summary>
public enum ExportFormat
{
    Png,
    Pdf
}

/// <summary>Una vista ya rasterizada, en los dos formatos que necesitan los dos destinos: PNG ya
/// codificado para escribirlo tal cual, y píxeles RGB en crudo para empotrarlos en el PDF
/// (<see cref="PdfImageWriter"/>). Se generan de una sola pasada de render para no rasterizar dos veces
/// lo mismo.</summary>
/// <param name="PixelWidth">Ancho real en píxeles (ya multiplicado por <paramref name="Scale"/>).</param>
/// <param name="PixelHeight">Alto real en píxeles.</param>
/// <param name="Scale">Píxeles renderizados por píxel lógico: 1× o 2×.</param>
/// <param name="Png">Bytes del PNG codificado.</param>
/// <param name="Rgb">Píxeles RGB, 3 bytes por píxel, sin comprimir.</param>
public sealed record ExportSnapshot(int PixelWidth, int PixelHeight, double Scale, byte[] Png, byte[] Rgb)
{
    public PdfImagePage ToPdfPage() => new(PixelWidth, PixelHeight, Rgb, Scale);
}

/// <summary>Motivo por el que una exportación no pudo llegar siquiera a rasterizarse: la vista no está en
/// pantalla, no tiene tamaño todavía, o el resultado sería una imagen desmesurada. Se traduce a un aviso
/// del centro de notificaciones en <see cref="ViewExportService"/>; nunca sube sin capturar.</summary>
public sealed class ExportRenderException(string reason, Models.LocalizedText userMessage) : Exception(reason)
{
    /// <summary>Texto ya localizable para enseñárselo al usuario — el mensaje de la excepción es
    /// diagnóstico para el log, como en el resto de la app.</summary>
    public Models.LocalizedText UserMessage { get; } = userMessage;
}
