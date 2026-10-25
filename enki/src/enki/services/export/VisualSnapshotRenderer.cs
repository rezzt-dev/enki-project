using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using enki.Localization;

namespace enki.Services.Export;

/// <summary>Colores y tipografía de la lámina exportada. Se resuelven de los recursos del tema activo en
/// <see cref="ViewExportService"/> y se pasan aquí como valores, para que este renderizador no dependa de
/// <c>Application.Current</c> y se pueda ejercitar con valores fijos.</summary>
/// <param name="Background">Fondo de la lámina, bajo el contenido y la cabecera.</param>
/// <param name="Title">Color del título.</param>
/// <param name="Subtitle">Color de la línea secundaria (rango de fechas, nombre del vault…).</param>
/// <param name="Body">Color del texto normal — solo lo usa la lámina de una nota
/// (<see cref="NotePageComposer"/>), que sí compone texto propio.</param>
/// <param name="Muted">Color del texto secundario y de las reglas de la lámina de una nota.</param>
/// <param name="Surface">Fondo de los bloques de código de la lámina de una nota.</param>
/// <param name="FontFamily">Tipografía de la cabecera — la de la interfaz, para que la lámina y la app se
/// lean como lo mismo.</param>
/// <param name="MonoFontFamily">Tipografía de ancho fijo para el código.</param>
public readonly record struct ExportAppearance(
    Color Background,
    Color Title,
    Color Subtitle,
    Color Body,
    Color Muted,
    Color Surface,
    FontFamily FontFamily,
    FontFamily MonoFontFamily);

/// <summary>Rasteriza un control de la aplicación —el tablero Kanban, el calendario, una hoja de nota— a
/// PNG y a píxeles RGB en una sola pasada (T8).
///
/// <para>Dos detalles hacen que esto no sea una simple captura de pantalla, que es justamente lo que pide
/// el criterio de aceptación "el sistema deberá incluir todas las columnas y tarjetas visibles del tablero,
/// aunque excedan el área en pantalla":</para>
///
/// <list type="number">
/// <item>Se rasteriza el <em>contenido</em> del <c>ScrollViewer</c>, no el <c>ScrollViewer</c>: el
/// contenido está dispuesto a su tamaño completo y es el visor quien lo recorta, así que renderizarlo
/// directamente da el tablero entero.</item>
/// <item>Los <c>ScrollViewer</c> de <em>dentro</em> de la región (las tarjetas de cada columna del
/// Kanban, la rejilla de horas y la cuadrícula del mes del calendario) también recortan. Mientras dura
/// el render, <see cref="FullSizeLayout"/> maqueta la región con alto ilimitado —y ancho ilimitado si
/// alguno sigue desbordando en horizontal— para que cada visor mida lo mismo que su contenido; después
/// se restauran la maqueta y los desplazamientos, siempre.</item>
/// </list></summary>
public static class VisualSnapshotRenderer
{
    /// <summary>Margen de la lámina alrededor del contenido, en píxeles lógicos.</summary>
    private const double Padding = 28;

    private const double TitleFontSize = 20;
    private const double SubtitleFontSize = 12;

    /// <summary>Tope de píxeles de la imagen final. 80 millones son ~240 MB de RGB en memoria, ya en el
    /// límite de lo razonable para un tablero enorme a 2×; por encima se baja la escala y, si aun así no
    /// cabe, se aborta con un motivo en vez de intentar reservar el buffer y tumbar la app.</summary>
    internal const long MaxTotalPixels = 80_000_000;

    /// <summary>Escalas ofrecidas en la interfaz: tamaño en pantalla y el doble, para imprimir.</summary>
    public const double ScaleStandard = 1;
    public const double ScaleHighResolution = 2;

    public static ExportSnapshot Capture(Control target, string title, string subtitle, double scale, ExportAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (LogicalSize(target) is not { Width: >= 1, Height: >= 1 })
            throw new ExportRenderException(
                $"La vista '{target.Name ?? target.GetType().Name}' todavía no tiene tamaño maquetado.",
                LocalizedTextFormatter.Format("Export_Error_NothingToExport"));

        RenderTargetBitmap content;
        Size contentSize;
        var hasHeader = !string.IsNullOrWhiteSpace(title);
        var headerHeight = hasHeader ? TitleFontSize * 1.35 + SubtitleFontSize * 1.6 + Padding * 0.5 : 0;
        Size sheetSize;

        using (FullSizeLayout.Apply(target))
        {
            contentSize = LogicalSize(target);
            sheetSize = new Size(contentSize.Width + Padding * 2, contentSize.Height + Padding * 2 + headerHeight);
            scale = FitScale(sheetSize, scale);
            content = new RenderTargetBitmap(ToPixelSize(contentSize, scale), new Vector(96 * scale, 96 * scale));
            try
            {
                content.Render(target);
            }
            catch
            {
                content.Dispose();
                throw;
            }
        }

        using (content)
        using (var sheet = new RenderTargetBitmap(ToPixelSize(sheetSize, scale), new Vector(96 * scale, 96 * scale)))
        {
            using (var context = sheet.CreateDrawingContext())
            {
                context.FillRectangle(new SolidColorBrush(appearance.Background), new Rect(sheetSize));
                if (hasHeader) DrawHeader(context, title, subtitle, appearance, sheetSize.Width - Padding * 2);
                // El rectángulo de origen va en píxeles del bitmap, no en su tamaño lógico: con la sobrecarga
                // corta, Skia toma Bitmap.Size (lógico) como si fueran píxeles, así que a 2× solo copiaba el
                // cuadrante superior izquierdo y lo estiraba al doble — la lámina salía ampliada y recortada.
                context.DrawImage(content,
                    new Rect(0, 0, content.PixelSize.Width, content.PixelSize.Height),
                    new Rect(Padding, Padding + headerHeight, contentSize.Width, contentSize.Height));
            }

            return new ExportSnapshot(sheet.PixelSize.Width, sheet.PixelSize.Height, scale, EncodePng(sheet), ExtractRgb(sheet));
        }
    }

    /// <summary>Tamaño maquetado del control. <see cref="Layoutable.DesiredSize"/> es el respaldo para el
    /// caso de una lámina compuesta fuera del árbol visual (la de una nota), que se mide y dispone a mano
    /// justo antes de llegar aquí y cuyo <c>Bounds</c> puede no estar publicado todavía.</summary>
    private static Size LogicalSize(Control target)
    {
        var bounds = target.Bounds.Size;
        return bounds is { Width: >= 1, Height: >= 1 } ? bounds : target.DesiredSize;
    }

    private static void DrawHeader(DrawingContext context, string title, string subtitle, ExportAppearance appearance, double maxWidth)
    {
        var culture = LocalizationService.Instance.CurrentCulture;
        var titleText = new FormattedText(title, culture, FlowDirection.LeftToRight,
            new Typeface(appearance.FontFamily, FontStyle.Normal, FontWeight.Bold), TitleFontSize,
            new SolidColorBrush(appearance.Title))
        { MaxTextWidth = maxWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        context.DrawText(titleText, new Point(Padding, Padding));

        if (string.IsNullOrWhiteSpace(subtitle)) return;

        var subtitleText = new FormattedText(subtitle, culture, FlowDirection.LeftToRight,
            new Typeface(appearance.FontFamily), SubtitleFontSize, new SolidColorBrush(appearance.Subtitle))
        { MaxTextWidth = maxWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        context.DrawText(subtitleText, new Point(Padding, Padding + TitleFontSize * 1.35));
    }

    /// <summary>Baja la escala pedida lo justo para no pasarse de <see cref="MaxTotalPixels"/>, y aborta
    /// si ni siquiera a 1× cabe — el criterio EARS es "mostrar el motivo", no fallar a medias.</summary>
    private static double FitScale(Size sheet, double requested)
    {
        var scale = Math.Clamp(requested, ScaleStandard, ScaleHighResolution);
        while (scale > ScaleStandard && sheet.Width * scale * sheet.Height * scale > MaxTotalPixels)
            scale -= 0.5;

        if (sheet.Width * scale * sheet.Height * scale <= MaxTotalPixels) return scale;

        throw new ExportRenderException(
            $"La vista mide {sheet.Width:F0}x{sheet.Height:F0} px lógicos, por encima del tope de exportación.",
            LocalizedTextFormatter.Format("Export_Error_TooLarge"));
    }

    private static PixelSize ToPixelSize(Size size, double scale) => new(
        Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
        Math.Max(1, (int)Math.Ceiling(size.Height * scale)));

    private static byte[] EncodePng(RenderTargetBitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    /// <summary>Convierte los píxeles BGRA premultiplicados del bitmap a RGB plano, que es lo que entiende
    /// <c>/DeviceRGB</c>. La lámina se dibuja sobre un fondo opaco, así que el alfa es 255 en toda la
    /// superficie y no hace falta deshacer la premultiplicación.</summary>
    private static byte[] ExtractRgb(RenderTargetBitmap bitmap)
    {
        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        var stride = width * 4;
        var bgra = new byte[(long)stride * height];

        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), bgra.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        var rgb = new byte[(long)width * height * 3];
        for (long pixel = 0, source = 0, destination = 0; pixel < (long)width * height; pixel++, source += 4, destination += 3)
        {
            rgb[destination] = bgra[source + 2];
            rgb[destination + 1] = bgra[source + 1];
            rgb[destination + 2] = bgra[source];
        }

        return rgb;
    }
}
