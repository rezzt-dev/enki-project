using System;
using enki.Models;

namespace enki.Services;

/// <summary>Reglas puras (sin estado, sin disco, sin UI, como <see cref="SnoozeSchedule"/>) que deciden
/// cuándo un <see cref="Reminder"/> empieza a reclamar la atención del usuario. Antes, la insignia de
/// «Recordatorios» contaba cualquier recordatorio sin leer, aunque fuera para el año siguiente; ahora un
/// recordatorio solo cuenta a partir de su <b>ventana de aviso previo</b> — de fábrica, el día antes a la
/// misma hora — que es también el instante en que se emite el aviso emergente de «próximo recordatorio».
///
/// <para>Todo lo que se decide aquí es local: ni <see cref="Reminder.IsRead"/> ni
/// <see cref="Reminder.AdvanceNoticeSentForUtc"/> viajan por el contrato con Polar
/// (docs/supabase-sync/05), así que un recordatorio creado en Polar avisa en Enki con las mismas reglas
/// que uno creado aquí.</para></summary>
public static class ReminderAttention
{
    /// <summary>Antelación de fábrica: el día antes a la misma hora.</summary>
    public const int DefaultLeadHours = 24;

    /// <summary>Máximo admitido por Ajustes › Notificaciones (tres días).</summary>
    public const int MaxLeadHours = 72;

    /// <summary>Acota un valor leído de <c>settings.json</c>. Cero es válido y significa «sin aviso
    /// previo»: el recordatorio solo reclama atención cuando llega su hora.</summary>
    public static int ClampLeadHours(int hours) => Math.Clamp(hours, 0, MaxLeadHours);

    /// <summary>Instante en que se abre la ventana de aviso previo de la ocurrencia actual.</summary>
    public static DateTime WindowStartUtc(Reminder reminder, int leadHours) =>
        WindowStartUtc(reminder.NotifyAtUtc, leadHours);

    public static DateTime WindowStartUtc(DateTime notifyAtUtc, int leadHours)
    {
        var lead = TimeSpan.FromHours(ClampLeadHours(leadHours));
        // Una fecha absurda (DateTime.MinValue de un JSON dañado) no debe desbordar la resta.
        return notifyAtUtc.Ticks > lead.Ticks ? notifyAtUtc - lead : DateTime.MinValue;
    }

    /// <summary>Si el recordatorio sigue «vivo» para avisar: ni borrado, ni completado, ni un evento de
    /// calendario importado (que por diseño no programa avisos).</summary>
    public static bool IsActionable(Reminder reminder) =>
        !reminder.IsDeleted && !reminder.IsCompleted && !reminder.IsImportedCalendarEvent;

    /// <summary>Si la ocurrencia actual ya está dentro de su ventana de aviso previo (o ha vencido).</summary>
    public static bool IsInWindow(Reminder reminder, DateTime nowUtc, int leadHours) =>
        IsActionable(reminder) && nowUtc >= WindowStartUtc(reminder, leadHours);

    /// <summary>Lo que cuenta la insignia de «Recordatorios»: dentro de su ventana y aún sin ver.</summary>
    public static bool NeedsAttention(Reminder reminder, DateTime nowUtc, int leadHours) =>
        !reminder.IsRead && IsInWindow(reminder, nowUtc, leadHours);

    /// <summary>Si toca emitir ahora el aviso de «próximo recordatorio». Solo una vez por ocurrencia
    /// (<see cref="Reminder.AdvanceNoticeSentForUtc"/>), solo antes de la hora —a partir de ahí ya avisa
    /// <see cref="ReminderService.PopDueReminders()"/>— y nunca para un recordatorio creado cuando la
    /// ventana ya estaba abierta: quien acaba de crearlo para dentro de dos horas no necesita que se lo
    /// recuerden ahora; ya le avisará la notificación de la hora.</summary>
    public static bool ShouldSendAdvanceNotice(Reminder reminder, DateTime nowUtc, int leadHours)
    {
        if (ClampLeadHours(leadHours) == 0 || !IsActionable(reminder) || reminder.IsTriggered) return false;
        if (reminder.AdvanceNoticeSentForUtc == reminder.NotifyAtUtc) return false;

        var windowStart = WindowStartUtc(reminder, leadHours);
        return nowUtc >= windowStart && nowUtc < reminder.NotifyAtUtc && reminder.CreatedUtc < windowStart;
    }

    /// <summary>Prepara una ocurrencia nueva generada en este equipo (reprogramada a mano o avanzada por su
    /// recurrencia): vuelve a estar sin ver, y si su ventana ya está abierta en este instante se da el
    /// aviso previo por emitido — quien la acaba de mover sabe cuándo es, y una serie diaria con un día de
    /// antelación no debe soltar el aviso de «mañana» justo después del de «ahora». Una reprogramación que
    /// llega desde Polar no pasa por aquí: la hizo otro dispositivo, y avisar de ella sí aporta.</summary>
    public static void ArmNewOccurrence(Reminder reminder, DateTime nowUtc, int leadHours)
    {
        reminder.IsRead = false;
        reminder.AdvanceNoticeSentForUtc = nowUtc >= WindowStartUtc(reminder, leadHours)
            ? reminder.NotifyAtUtc
            : null;
    }
}
