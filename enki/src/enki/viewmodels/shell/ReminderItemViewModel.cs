using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

public enum ReminderBucket
{
    Overdue,
    Today,
    Soon,

    /// <summary>Aplazado a un instante futuro (T5). Es un cubo propio y no una variante de
    /// <see cref="Soon"/> porque un aviso aplazado debe salir del recuento de vencidos —de eso trata
    /// aplazar— sin desaparecer de la bandeja: sigue listado, al final, con la hora a la que volverá.</summary>
    Snoozed
}

/// <summary>Which underlying entity a <see cref="ReminderItemViewModel"/> was projected from — the inbox
/// mixes two independent "due soon" sources (docs/agent-docs/notificaciones-y-recordatorios.md) into one
/// bucketed list, and <see cref="ReminderInboxViewModel.OpenCard"/> needs to know which one to jump to
/// (Kanban's board vs. the Recordatorios page).</summary>
public enum ReminderItemSourceKind
{
    KanbanCard,
    Reminder
}

/// <summary>One due-dated item surfaced in the title-bar inbox — a read projection, not a separate
/// persisted entity of its own. Wraps either a <see cref="KanbanCard"/>'s own <see cref="KanbanCard.DueDateUtc"/>
/// or a standalone <see cref="Models.Reminder"/>'s <see cref="Models.Reminder.NotifyAtUtc"/>; dismissing one
/// (see <see cref="ReminderInboxViewModel"/>) doesn't touch the source at all, it just records the dismissal
/// separately in <see cref="Services.DismissedReminderService"/>, keyed by <see cref="SourceId"/>.</summary>
public sealed partial class ReminderItemViewModel : ViewModelBase
{
    /// <summary>Si esta fila tiene desplegado su menú "Aplazar". Vive en el item y no solo en
    /// <see cref="ReminderInboxViewModel.SnoozeMenuItem"/> porque el <c>DataTemplate</c> de la lista
    /// necesita un booleano que enlazar sin comparar objetos desde XAML; el panel sigue siendo el dueño
    /// del estado y es quien mantiene el invariante de "solo uno abierto a la vez".</summary>
    [ObservableProperty]
    private bool _isSnoozeMenuOpen;

    public ReminderItemSourceKind SourceKind { get; }
    public string SourceId { get; }
    public string Title { get; }
    public string ColumnTitle { get; }
    public DateTime DueDateUtc { get; }

    /// <summary>Cubo en el que cae ahora mismo. Deja de ser el derivado puro de la fecha en cuanto hay un
    /// aplazamiento vigente: ver <see cref="ApplySnooze"/>.</summary>
    public ReminderBucket Bucket { get; private set; }

    /// <summary>Instante al que se aplazó este aviso, o <see langword="null"/> si no está aplazado. No es
    /// un dato del origen: vive en <see cref="Services.SnoozedReminderService"/> y se proyecta aquí igual
    /// que el resto de esta clase proyecta la tarjeta o el recordatorio.</summary>
    public DateTime? SnoozedUntilUtc { get; private set; }

    /// <summary>Aplazamientos consecutivos de este aviso — ver <see cref="Models.SnoozedReminder.Count"/>.</summary>
    public int SnoozeCount { get; private set; }

    public bool IsSnoozed => SnoozedUntilUtc is not null;

    /// <summary>"Aplazado hasta 15:40". Vacío si no está aplazado, para que la vista pueda enlazarlo sin
    /// convertidor.</summary>
    public string SnoozedUntilLabel => SnoozedUntilUtc is { } until
        ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Reminder_SnoozedUntilFormat"],
            RelativeTimeFormatter.FormatClockOrShortDate(until))
        : string.Empty;

    /// <summary>"Aplazado 3 veces" — solo a partir del segundo aplazamiento, que es cuando el contador
    /// aporta algo (§9: sin tope de aplazamientos, pero visibles).</summary>
    public string SnoozeCountLabel => SnoozeCount > 1
        ? Plural.Get("Reminder_SnoozeCountFormat", SnoozeCount, SnoozeCount)
        : string.Empty;

    public bool HasSnoozeCount => SnoozeCount > 1;

    public string BucketLabel => Bucket switch
    {
        ReminderBucket.Overdue => LocalizationService.Instance["Reminder_Bucket_Overdue"],
        ReminderBucket.Today => LocalizationService.Instance["Reminder_Bucket_Today"],
        ReminderBucket.Snoozed => LocalizationService.Instance["Reminder_Bucket_Snoozed"],
        _ => LocalizationService.Instance["Reminder_Bucket_Soon"]
    };

    /// <summary>Equivalente en <see cref="Models.SnoozeSourceKind"/> de <see cref="SourceKind"/>, para
    /// hablar con <see cref="Services.SnoozedReminderService"/> sin que <c>Models</c> tenga que conocer el
    /// enum de los viewmodels.</summary>
    public SnoozeSourceKind SnoozeSourceKind => SourceKind == ReminderItemSourceKind.Reminder
        ? Models.SnoozeSourceKind.Reminder
        : Models.SnoozeSourceKind.KanbanCard;

    public string DueDateLabel => KanbanCard.FormatDueLabel(DueDateUtc);

    public ReminderItemViewModel(KanbanCard card, string columnTitle)
    {
        if (card.DueDateUtc is not { } due)
            throw new ArgumentException("El card no tiene fecha límite.", nameof(card));

        SourceKind = ReminderItemSourceKind.KanbanCard;
        SourceId = card.Id;
        Title = card.Title;
        ColumnTitle = columnTitle;
        DueDateUtc = due;
        Bucket = BucketFor(due);
    }

    /// <summary>Recordatorio propio (creado desde la página Recordatorios), no una tarjeta Kanban —
    /// <see cref="ColumnTitle"/> se reutiliza como etiqueta de origen (<c>Nav_RemindersLabel</c>, "Recordatorios")
    /// para distinguirlo a simple vista de los proyectados desde el tablero.</summary>
    public ReminderItemViewModel(Models.Reminder reminder)
    {
        SourceKind = ReminderItemSourceKind.Reminder;
        SourceId = reminder.Id;
        Title = reminder.Title;
        ColumnTitle = LocalizationService.Instance["Nav_RemindersLabel"];
        DueDateUtc = reminder.NotifyAtUtc;
        Bucket = BucketFor(reminder.NotifyAtUtc);
    }

    /// <summary>Superpone el aplazamiento vigente (si lo hay) sobre el cubo derivado de la fecha. Se
    /// aplica después de construir el item, y no en el constructor, porque el aplazamiento no es un dato
    /// de la tarjeta ni del recordatorio: lo resuelve <see cref="ReminderInboxViewModel.Reload"/>
    /// consultando el almacén aparte, y pasa por las dos rutas de construcción por igual.</summary>
    public ReminderItemViewModel ApplySnooze(SnoozedReminder? snooze)
    {
        if (snooze is null) return this;

        SnoozedUntilUtc = snooze.SnoozedUntilUtc;
        SnoozeCount = snooze.Count;
        Bucket = ReminderBucket.Snoozed;
        return this;
    }

    private static ReminderBucket BucketFor(DateTime dueUtc)
    {
        var dueLocalDate = dueUtc.ToLocalTime().Date;
        var today = DateTime.Now.Date;
        return KanbanCard.IsPastDue(dueUtc) ? ReminderBucket.Overdue
            : dueLocalDate == today ? ReminderBucket.Today
            : ReminderBucket.Soon;
    }
}
