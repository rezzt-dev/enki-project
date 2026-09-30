using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services.Export;

namespace enki.ViewModels;

/// <summary>Kanban › "Exportar tablero" a PNG y a PDF (T8 de
/// .docs/funcionalidades-pendientes-priorizadas.md §4.4) — en su propio archivo parcial, mismo criterio que
/// <c>MainWindowViewModel.Composition.cs</c>. El viewmodel no toca ningún control: describe qué región hay
/// que rasterizar por su nombre y <see cref="ViewExportService"/> la localiza en la ventana viva.</summary>
public sealed partial class KanbanViewModel
{
    /// <summary>Las dos disposiciones del tablero (vertical y horizontal, Ajustes › Apariencia) viven a la
    /// vez en el árbol y solo una está visible; se ofrecen las dos y gana la que lo esté.</summary>
    private static readonly string[] ExportRootNames = ["KanbanExportRootVertical", "KanbanExportRootHorizontal"];

    [RelayCommand]
    private Task ExportBoardAsPng() => ExportBoardAsync(ExportFormat.Png);

    [RelayCommand]
    private Task ExportBoardAsPdf() => ExportBoardAsync(ExportFormat.Pdf);

    public Task ExportBoardAsync(ExportFormat format)
    {
        var request = new ViewExportRequest(
            ExportRootNames,
            BoardName,
            LocalizedTextFormatter.Resolve(LocalizedTextFormatter.PluralFormat("Export_Kanban_Subtitle", TotalCards, TotalCards)),
            ViewExportService.TimestampedFileName(BoardName));

        return ViewExportService.ExportViewAsync(request, format, VisualSnapshotRenderer.ScaleHighResolution,
            App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null,
            ExportNotifyCallback);
    }

    /// <summary>Mismo puente que <c>NotesViewModel.NotifyCallback</c>: el servicio de exportación anuncia
    /// éxito y fallo sin referenciar <c>NotificationService</c>, y en los tests que construyen este
    /// viewmodel sin él simplemente no anuncia nada.</summary>
    private System.Action<LocalizedText, LocalizedText, NotificationSeverity>? ExportNotifyCallback =>
        _notificationService is null
            ? null
            : (title, message, severity) => _notificationService.PushLocalized(title, message, severity);
}
