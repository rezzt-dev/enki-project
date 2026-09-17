using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>Persists which reminders the user dismissed from the inbox panel — same load/save shape as
/// <see cref="SettingsService"/>/<see cref="KanbanService"/>, stored at
/// <c>&lt;vault&gt;/.tasks/dismissed-reminders.json</c>. See <see cref="DismissedReminder"/> for why entries are
/// keyed by card id *and* the due date snapshot rather than just card id.</summary>
public sealed class DismissedReminderService
{
    /// <summary>Tope de entradas conservadas (Fase 3 de docs/analisis-proyecto/roadmap.md) — mismo criterio
    /// que el <c>MaxItems</c> de <see cref="NotificationService"/>: sin él el fichero crecía sin límite con
    /// cada tarjeta descartada a lo largo de la vida de la app. Como <see cref="Dismiss"/> reemplaza la
    /// entrada previa de la misma tarjeta y la reinserta al final, la lista queda ordenada por descarte
    /// (más antiguo primero), así que el recorte simplemente suelta las entradas más viejas — en el peor
    /// caso, un recordatorio descartado hace mucho vuelve a aparecer en la bandeja, nunca se pierde uno
    /// reciente.</summary>
    private const int MaxItems = 50;

    private readonly string? _storageDirectory;
    private readonly VaultService? _vaultService;
    private string _path => _vaultService is not null
        ? VaultDataStorage.FilePath(_vaultService, "dismissed-reminders.json")
        : Path.Combine(_storageDirectory!, "dismissed-reminders.json");
    private List<DismissedReminder> _dismissed;

    /// <param name="storageDirectory">Overrides the default <c>%AppData%/Enki</c> folder — unused in the
    /// running app, lets tests point persistence at a throwaway directory (see enki.Tests).</param>
    public DismissedReminderService(string? storageDirectory = null)
    {
        _storageDirectory = storageDirectory ?? AppDataDirectory.Resolve();
        Directory.CreateDirectory(_storageDirectory);
        _dismissed = Load();
        if (TrimExcess()) Save();
    }

    public DismissedReminderService(VaultService vaultService)
    {
        _vaultService = vaultService;
        _dismissed = Load();
        if (TrimExcess()) Save();
    }

    /// <summary>Recarga los descartes del vault activo.</summary>
    public void Reload()
    {
        _dismissed = Load();
        if (TrimExcess()) Save();
    }

    private List<DismissedReminder> Load() =>
        JsonFileStore.TryLoad<List<DismissedReminder>>(_path, out var loaded) ? loaded! : [];

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        JsonFileStore.Save(_path, _dismissed);
    }

    public bool IsDismissed(string cardId, DateTime dueDateUtc) =>
        _dismissed.Any(d => d.CardId == cardId && d.DueDateUtcSnapshot == dueDateUtc);

    /// <summary>Records a dismissal, replacing any earlier dismissal of the same card (a stale snapshot
    /// from a since-changed due date would otherwise pile up forever without ever matching again).</summary>
    public void Dismiss(string cardId, DateTime dueDateUtc)
    {
        _dismissed.RemoveAll(d => d.CardId == cardId);
        _dismissed.Add(new DismissedReminder { CardId = cardId, DueDateUtcSnapshot = dueDateUtc });
        TrimExcess();
        Save();
    }

    private bool TrimExcess()
    {
        if (_dismissed.Count <= MaxItems) return false;
        _dismissed.RemoveRange(0, _dismissed.Count - MaxItems);
        return true;
    }

    /// <summary>Drops every dismissal recorded for a card, regardless of the due-date snapshot — two
    /// distinct callers as of Fase 10 (docs/analisis-proyecto/roadmap.md): <c>ReminderInboxViewModel</c>'s
    /// "Deshacer" right after a <see cref="Dismiss"/> (the snapshot can't have gone stale within a few
    /// seconds, so matching by card id alone is safe and simpler than re-deriving the exact due date), and
    /// the composition root when a card is permanently deleted (<c>KanbanService.CardPermanentlyDeleted</c>)
    /// — an entry pointing at a card that no longer exists would otherwise linger in
    /// <c>dismissed-reminders.json</c> until <see cref="MaxItems"/> eventually evicts it.</summary>
    public void RemoveForCard(string cardId)
    {
        if (_dismissed.RemoveAll(d => d.CardId == cardId) == 0) return;
        Save();
    }
}
