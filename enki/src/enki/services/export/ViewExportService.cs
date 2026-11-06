using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using enki.Localization;
using enki.Models;

namespace enki.Services.Export;

/// <summary>Qué se exporta y con qué nombre. Los viewmodels construyen esto sin tocar ningún control: la
/// región a rasterizar se nombra (<paramref name="RootNames"/>) y la localiza
/// <see cref="ViewExportService"/> en la ventana viva, mismo recurso —<c>App.MainWindowRef</c>— que ya usan
/// <see cref="ToastService"/> y <c>NoteExportService</c> para lo que necesita un <c>TopLevel</c>.</summary>
/// <param name="RootNames">Nombres candidatos del elemento a rasterizar, en orden de preferencia; se usa
/// el primero que esté realmente visible. Hay varios porque una misma vista puede tener dos disposiciones
/// mutuamente excluyentes (el tablero Kanban vertical y el horizontal).</param>
/// <param name="Title">Título de la lámina.</param>
/// <param name="Subtitle">Segunda línea de la lámina: rango de fechas, columna, nombre del tablero…</param>
/// <param name="SuggestedFileName">Nombre propuesto en el diálogo de guardado, sin extensión.</param>
public sealed record ViewExportRequest(IReadOnlyList<string> RootNames, string Title, string Subtitle, string SuggestedFileName);

/// <summary>"Exportar vista a imagen o PDF" de punta a punta (T8 de
/// .docs/funcionalidades-pendientes-priorizadas.md §4.4): localiza la región a rasterizar, la convierte en
/// láminas con <see cref="VisualSnapshotRenderer"/>, elige destino con el diálogo del sistema y escribe el
/// archivo de forma atómica con <see cref="DurableFile"/>.
///
/// <para>Sin estado propio —el render es puro y el selector es efímero— así que toda la clase es estática,
/// igual que <c>NoteExportService</c>, del que copia también la forma de avisar: los éxitos y los fallos
/// llegan al centro de notificaciones por el callback <c>notify</c> que le pasa el llamador, no por una
/// referencia propia a <see cref="NotificationService"/>.</para></summary>
public static class ViewExportService
{
    /// <summary>Claves de los textos del flujo: el título del diálogo de guardado y los avisos de éxito y
    /// fallo. Una nota exportada se anuncia como nota, no como «vista».</summary>
    private sealed record FlowTexts(string DialogTitle, string SuccessTitle, string SuccessMessage, string FailedTitle);

    private static readonly FlowTexts ViewTexts = new("Export_ViewDialog_Title", "Notification_ViewExportSuccess_Title",
        "Notification_ViewExportSuccess_Message", "Notification_ViewExportFailed_Title");

    private static readonly FlowTexts NoteTexts = new("Export_NoteDialog_Title", "Notification_NoteExportSuccess_Title",
        "Notification_NoteExportSuccess_Message", "Notification_NoteExportFailed_Title");

    /// <summary>Exporta una región de la interfaz ya en pantalla: el calendario o el tablero Kanban.</summary>
    public static Task ExportViewAsync(ViewExportRequest request, ExportFormat format, double scale,
        TopLevel? topLevel, Action<LocalizedText, LocalizedText, NotificationSeverity>? notify)
    {
        ArgumentNullException.ThrowIfNull(request);

        return RunAsync(request.SuggestedFileName, format, ViewTexts, topLevel, notify, appearance =>
        {
            var target = FindExportRoot(request.RootNames)
                ?? throw new ExportRenderException(
                    $"Ninguna de las regiones exportables [{string.Join(", ", request.RootNames)}] está visible.",
                    LocalizedTextFormatter.Format("Export_Error_ViewNotOpen"));

            var snapshot = VisualSnapshotRenderer.Capture(target, request.Title, request.Subtitle, scale, appearance);
            return Task.FromResult(format == ExportFormat.Png
                ? snapshot.Png
                : PdfImageWriter.Create([snapshot.ToPdfPage()]));
        });
    }

    /// <summary>Exporta una nota. A diferencia del calendario y del tablero, no se rasteriza la pantalla:
    /// el documento se compone aparte a partir del Markdown (ver <see cref="NoteDocumentExporter"/>, y en
    /// <see cref="NoteDocumentComposer"/> por qué el editor no sirve de origen). El PDF sale vectorial y
    /// paginado en la hoja de la región del usuario (A4 o Carta, con los márgenes de Word); el PNG, en una
    /// sola lámina con la paleta del tema activo.</summary>
    public static Task ExportNoteAsync(NoteExportSource source, ExportFormat format, TopLevel? topLevel,
        Action<LocalizedText, LocalizedText, NotificationSeverity>? notify)
    {
        ArgumentNullException.ThrowIfNull(source);
        var page = NotePageSetup.ForCulture(LocalizationService.Instance.CurrentCulture);

        return RunAsync(source.Title, format, NoteTexts, topLevel, notify, async appearance => format == ExportFormat.Pdf
            ? await NoteDocumentExporter.CreatePdfAsync(source, page)
            // La lámina se compone en puntos, como el PDF: 96/72 la devuelve al tamaño en píxeles que
            // tenía la exportación PNG de una nota antes de compartir composición con el PDF.
            : (await NoteDocumentExporter.CreatePngAsync(source, page, appearance,
                VisualSnapshotRenderer.ScaleHighResolution * 96 / 72)).Png);
    }

    private static async Task RunAsync(string suggestedFileName, ExportFormat format, FlowTexts texts, TopLevel? topLevel,
        Action<LocalizedText, LocalizedText, NotificationSeverity>? notify,
        Func<ExportAppearance, Task<byte[]>> render)
    {
        // Sin TopLevel no hay diálogo de guardado ni ventana que rasterizar — mismo no-op silencioso que
        // NoteExportService y que los comandos de importar/exportar ajustes en los tests.
        if (topLevel?.StorageProvider is null) return;

        var destination = await AskDestinationAsync(suggestedFileName, format, texts.DialogTitle, topLevel);
        if (destination is null) return;

        try
        {
            var bytes = await render(ResolveAppearance());

            DurableFile.WriteAllBytesAtomic(destination, bytes);
            notify?.Invoke(
                LocalizedTextFormatter.Format(texts.SuccessTitle),
                LocalizedTextFormatter.Format(texts.SuccessMessage, destination),
                NotificationSeverity.Success);
            FolderOpener.RevealFile(destination);
        }
        catch (ExportRenderException ex)
        {
            LogService.Warning("Exportación de vista cancelada antes de rasterizar", ex);
            notify?.Invoke(LocalizedTextFormatter.Format(texts.FailedTitle), ex.UserMessage, NotificationSeverity.Error);
        }
        // Los errores visibles usan recursos; el diagnóstico técnico se conserva en el log — mismo reparto
        // que NoteExportService.
        catch (UnauthorizedAccessException)
        {
            notify?.Invoke(LocalizedTextFormatter.Format(texts.FailedTitle), LocalizedTextFormatter.Format("Common_Error_AccessDenied"), NotificationSeverity.Error);
        }
        catch (IOException)
        {
            notify?.Invoke(LocalizedTextFormatter.Format(texts.FailedTitle), LocalizedTextFormatter.Format("Common_Error_IOFailure"), NotificationSeverity.Error);
        }
        catch (Exception ex)
        {
            LogService.Error($"No se pudo exportar a {format}", ex);
            notify?.Invoke(LocalizedTextFormatter.Format(texts.FailedTitle), VaultFailureReasons.DescribeLocalized(ex), NotificationSeverity.Error);
        }
    }

    private static async Task<string?> AskDestinationAsync(string suggestedFileName, ExportFormat format, string dialogTitle,
        TopLevel topLevel)
    {
        var loc = LocalizationService.Instance;
        var extension = format == ExportFormat.Png ? "png" : "pdf";
        var typeLabel = loc[format == ExportFormat.Png ? "Export_PngFileType" : "Export_PdfFileType"];

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = loc[dialogTitle],
            SuggestedFileName = SanitizeFileName(suggestedFileName) + "." + extension,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(typeLabel) { Patterns = ["*." + extension] }]
        });

        var path = file?.TryGetLocalPath();
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <summary>Primer elemento visible con uno de esos nombres dentro de la ventana principal. Se compara
    /// <see cref="Visual.IsEffectivelyVisible"/> y no <c>IsVisible</c> porque las disposiciones alternativas
    /// (tablero vertical/horizontal, las cuatro vistas del calendario) conviven en el árbol y solo se
    /// distinguen por el <c>IsVisible</c> de algún ancestro.</summary>
    internal static Control? FindExportRoot(IReadOnlyList<string> names)
    {
        if (App.MainWindowRef is not { } window) return null;

        foreach (var name in names)
        {
            var match = window.GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);
            if (match is not null) return match;
        }

        return null;
    }

    /// <summary>Colores y tipografías de la lámina, tomados de los recursos del tema activo para que lo
    /// exportado se lea como lo que hay en pantalla. El documento de la funcionalidad dejaba abierto
    /// "fondo según tema activo o forzar claro para imprimir": se usa el tema activo, porque el contenido
    /// (tarjetas, textos, colores de capa) se rasteriza con el tema activo de todas formas, y un fondo
    /// claro forzado bajo un contenido oscuro sería peor que cualquiera de las dos opciones puras.</summary>
    private static ExportAppearance ResolveAppearance()
    {
        var theme = Application.Current?.ActualThemeVariant ?? ThemeVariant.Default;
        var text = Resource("TextPrimaryBrush", theme, Colors.Black);
        var muted = Resource("TextMutedBrush", theme, Color.FromRgb(0x6B, 0x6B, 0x6B));
        return new ExportAppearance(
            // Fondo de panel y no de aplicación: la lámina es una superficie de contenido, igual que una
            // tarjeta, no el lienzo de fondo de la ventana.
            Resource("PanelBackgroundColor", theme, Colors.White),
            text,
            muted,
            Font("DisplayFontFamily", theme));
    }

    /// <summary>Acepta tanto una clave de <c>Color</c> como una de <c>SolidColorBrush</c>: Colors.axaml
    /// declara las dos formas y cuál existe depende del recurso.</summary>
    private static Color Resource(string key, ThemeVariant theme, Color fallback)
    {
        if (Application.Current?.TryFindResource(key, theme, out var value) != true) return fallback;
        return value switch
        {
            Color color => color,
            ISolidColorBrush brush => brush.Color,
            _ => fallback
        };
    }

    private static FontFamily Font(string key, ThemeVariant theme) =>
        Application.Current?.TryFindResource(key, theme, out var value) == true && value is FontFamily family
            ? family
            : FontFamily.Default;

    /// <summary>Deja el nombre propuesto en algo que cualquier sistema de archivos acepte. El usuario
    /// siempre puede cambiarlo en el diálogo; esto solo evita proponer un nombre que el propio diálogo
    /// rechace por llevar el título de una nota con <c>/</c> o <c>:</c>.</summary>
    internal static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((name ?? string.Empty).Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "export" : cleaned[..Math.Min(cleaned.Length, 120)];
    }

    /// <summary>Nombre de archivo propuesto con la fecha del día, en formato ordenable — dos exportaciones
    /// del mismo tablero en días distintos no deberían proponer el mismo nombre.</summary>
    public static string TimestampedFileName(string prefix) =>
        $"{prefix}-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}
