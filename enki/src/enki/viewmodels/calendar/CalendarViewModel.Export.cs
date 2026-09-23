using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services.Export;

namespace enki.ViewModels;

/// <summary>Calendario › "Exportar vista" a PNG y a PDF (T8 de
/// .docs/funcionalidades-pendientes-priorizadas.md §4.4) — en su propio archivo parcial, igual que
/// <see cref="KanbanViewModel"/> hace con lo mismo.
///
/// <para>Lo exportado es exactamente lo que está en pantalla: el rango (mes/semana/día/agenda), las capas
/// activas y los filtros aplicados. El documento de la funcionalidad proponía además un diálogo con rango
/// y capas configurables aparte; se descartó porque duplicaría en un formulario los controles que la
/// propia vista ya tiene, y "exporta lo que veo" es más predecible que "exporta lo que dije en otro
/// sitio".</para></summary>
public sealed partial class CalendarViewModel
{
    private static readonly string[] ExportRootNames = ["CalendarExportRoot"];

    [RelayCommand]
    private Task ExportViewAsPng() => ExportViewAsync(ExportFormat.Png);

    [RelayCommand]
    private Task ExportViewAsPdf() => ExportViewAsync(ExportFormat.Pdf);

    public Task ExportViewAsync(ExportFormat format)
    {
        var request = new ViewExportRequest(
            ExportRootNames,
            LocalizationService.Instance["Nav_Calendar"],
            PeriodLabel,
            ViewExportService.TimestampedFileName(LocalizationService.Instance["Nav_Calendar"]));

        return ViewExportService.ExportViewAsync(request, format, VisualSnapshotRenderer.ScaleHighResolution,
            App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null,
            (title, message, severity) => _notificationService.PushLocalized(title, message, severity));
    }
}
