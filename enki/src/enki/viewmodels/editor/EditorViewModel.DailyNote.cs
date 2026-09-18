using System;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Lo que la cabecera del editor añade cuando la nota abierta <b>es</b> una nota diaria
/// (propuesta 8, docs/project-info/propuesta-nuevas-funcionalidades.md §8): ir al día anterior o al
/// siguiente, volver a hoy y combinar la nota con su plantilla sin pisar lo escrito.
///
/// <para>Todo esto desaparece por completo en cualquier otra nota —<see cref="IsDailyNote"/> es falso y la
/// cabecera queda exactamente como estaba—, que es la condición para que un grupo de controles más en una
/// cabecera ya poblada no moleste a quien no lleva un diario.</para>
///
/// <para>El reconocimiento ("¿esta nota es la de qué día?") no vive aquí sino en
/// <see cref="DailyNoteService"/>, y la creación de la nota del día vecino tampoco: navegar al día anterior
/// llama al mismo <c>NotesViewModel.OpenDailyNoteFor</c> que el botón de Documentos y que el calendario de
/// constancia, así que las tres vías crean la nota en la misma carpeta, con la misma plantilla y con el
/// mismo nombre.</para></summary>
public sealed partial class EditorViewModel
{
    /// <summary>Null en los tests y en cualquier composición que no pase el servicio: entonces ninguna nota
    /// es "nota diaria" y la cabecera no muestra la navegación.</summary>
    private readonly DailyNoteService? _dailyNoteService;

    /// <summary>Abrir la nota diaria de un día concreto, creándola si no existe — lo pone
    /// <c>NotesViewModel</c>, que es quien decide en qué carpeta nacen las notas y quién recarga la rejilla
    /// después.</summary>
    private readonly Action<DateOnly>? _onOpenDailyNoteFor;

    /// <summary>El día del que esta nota es la nota diaria, o null si no lo es.</summary>
    private DateOnly? _dailyNoteDate;

    /// <summary>Si la cabecera debe mostrar la navegación de días. Se recalcula al abrir la pestaña y cada
    /// vez que la nota cambia de nombre: renombrar <c>2026-09-18</c> a <c>Ideas</c> deja de ser una nota
    /// diaria en ese mismo momento, y al revés.</summary>
    [ObservableProperty]
    private bool _isDailyNote;

    /// <summary>La fecha de la nota, escrita para leerse ("jue, 18 sep") y no como nombre de archivo — el
    /// nombre ya está en el título, justo al lado. Sigue al idioma de la interfaz.</summary>
    [ObservableProperty]
    private string _dailyNoteDateLabel = string.Empty;

    /// <summary>Si la nota abierta no es la de hoy: es entonces cuando el botón "hoy" tiene algo que
    /// hacer, y cuando ofrecerlo evita la vuelta larga (cerrar, volver a Documentos, botón de nota
    /// diaria).</summary>
    [ObservableProperty]
    private bool _canReturnToToday;

    /// <summary>Si la plantilla de la nota diaria tiene secciones que a esta nota le faltan. Es la
    /// condición para ofrecer "combinar con la plantilla": sin nada que añadir, el botón no aparece en vez
    /// de aparecer y no hacer nada al pulsarlo.</summary>
    [ObservableProperty]
    private bool _hasPendingDailyTemplateSections;

    /// <summary>Qué secciones añadiría exactamente la combinación, enumeradas en el tooltip del botón: la
    /// acción escribe en la nota del usuario, así que se ve de antemano qué va a escribir.</summary>
    [ObservableProperty]
    private string _pendingDailyTemplateTooltip = string.Empty;

    // ===================== Navegación =====================

    [RelayCommand]
    private void OpenPreviousDay() => NavigateDays(-1);

    [RelayCommand]
    private void OpenNextDay() => NavigateDays(1);

    /// <summary>Vuelve a la nota de hoy desde la de cualquier otro día.</summary>
    [RelayCommand]
    private void OpenTodayNote()
    {
        if (!IsDailyNote) return;
        // Lo pendiente se escribe antes de cambiar de nota: la pestaña de origen puede quedarse abierta en
        // segundo plano y su autoguardado tardaría hasta 600 ms más en llegar.
        FlushPendingSave();
        _onOpenDailyNoteFor?.Invoke(DailyNoteService.Today);
    }

    private void NavigateDays(int offset)
    {
        if (!IsDailyNote || _dailyNoteDate is not { } date) return;
        FlushPendingSave();
        _onOpenDailyNoteFor?.Invoke(date.AddDays(offset));
    }

    // ===================== Combinar con la plantilla =====================

    /// <summary>Añade a la nota las secciones de la plantilla que le falten, al final y sin tocar nada de
    /// lo que ya hay (<see cref="DailyNoteTemplateMerge"/>). Se escribe a disco en el acto —no se deja como
    /// cambio pendiente— para que el historial de versiones registre el punto anterior a la combinación y
    /// deshacerla sea siempre posible desde ahí.</summary>
    [RelayCommand]
    private void MergeDailyTemplate()
    {
        if (!IsDailyNote || _dailyNoteService is null || _dailyNoteDate is not { } date) return;

        var current = Document.Text;
        var merged = _dailyNoteService.BuildMergedContent(date, current);
        if (merged is null || string.Equals(merged, current, StringComparison.Ordinal))
        {
            RefreshDailyTemplateState();
            return;
        }

        var sections = _dailyNoteService.PendingSections(date, current);

        try
        {
            _vaultService.WriteContent(Note, merged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            LogService.Error($"No se pudo combinar la nota diaria '{Note.FullPath}' con su plantilla", ex);
            _notificationService?.PushLocalized(
                LocalizedTextFormatter.Format("Notification_DailyTemplateMergeFailed_Title"),
                VaultFailureReasons.DescribeLocalized(ex),
                NotificationSeverity.Error);
            return;
        }

        ApplyRestoredContent(merged);
        RefreshDailyTemplateState();

        _notificationService?.PushLocalized(
            LocalizedTextFormatter.Format("Notification_DailyTemplateMerged_Title"),
            LocalizedTextFormatter.Format("Notification_DailyTemplateMerged_Message",
                string.Join(", ", sections.Select(section => section.Heading))),
            NotificationSeverity.Info);
    }

    // ===================== Estado =====================

    /// <summary>Recalcula si esta nota es una nota diaria y de qué día. Se llama al construir la pestaña y
    /// tras cada renombrado (propio o hecho desde la rejilla), que son los dos únicos momentos en que la
    /// respuesta puede cambiar: el día de una nota diaria está en el nombre del archivo, no en su
    /// contenido.</summary>
    private void RefreshDailyNoteState()
    {
        _dailyNoteDate = _dailyNoteService?.DateOf(Note);
        IsDailyNote = _dailyNoteDate is not null;

        if (_dailyNoteDate is { } date)
        {
            DailyNoteDateLabel = FormatDateLabel(date);
            CanReturnToToday = date != DailyNoteService.Today;
        }
        else
        {
            DailyNoteDateLabel = string.Empty;
            CanReturnToToday = false;
        }

        RefreshDailyTemplateState();
    }

    /// <summary>Recalcula qué le falta a la nota respecto de su plantilla. Cuelga del mismo tic de
    /// autoguardado que los backlinks (600 ms tras dejar de escribir) en vez de recalcularse por
    /// pulsación: es leer la plantilla del disco y comparar encabezados, barato para hacerlo al parar de
    /// escribir y un desperdicio para hacerlo sesenta veces por frase.</summary>
    private void RefreshDailyTemplateState()
    {
        if (!IsDailyNote || _dailyNoteService is null || _dailyNoteDate is not { } date)
        {
            HasPendingDailyTemplateSections = false;
            PendingDailyTemplateTooltip = string.Empty;
            return;
        }

        var sections = _dailyNoteService.PendingSections(date, Document.Text);
        HasPendingDailyTemplateSections = sections.Count > 0;
        PendingDailyTemplateTooltip = sections.Count == 0
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture,
                LocalizationService.Instance["Editor_DailyNote_MergeTemplate_Tooltip"],
                string.Join(", ", sections.Select(section => section.Heading)));
    }

    private static string FormatDateLabel(DateOnly date) =>
        date.Year == DateTime.Today.Year
            ? date.ToString("ddd d MMM", CultureInfo.CurrentCulture)
            : date.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture);
}
