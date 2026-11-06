using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>Persiste los avisos que el usuario aplazó (T5 de
/// .docs/funcionalidades-pendientes-priorizadas.md) en
/// <c>&lt;vault&gt;/.tasks/snoozed-reminders.json</c> — misma forma de carga/guardado, mismo tope de
/// entradas y mismos constructores que <see cref="DismissedReminderService"/>, del que es el gemelo
/// exacto: uno guarda "no me lo vuelvas a enseñar", el otro "vuélvemelo a enseñar a tal hora".
///
/// <para>No toca ninguna entidad de origen: ver <see cref="SnoozedReminder"/> para por qué aplazar no es
/// reprogramar. Deliberadamente "tonto" como <see cref="ReminderService"/>: sabe qué está aplazado y qué
/// aplazamiento ha vencido (<see cref="PopDue"/>), pero no sabe nada de notificaciones ni de tarjetas —
/// el shell (<c>MainWindowViewModel</c>) es quien convierte un aplazamiento vencido en un aviso real,
/// mismo reparto de papeles que ya tiene con <see cref="ReminderService.PopDueReminders()"/>.</para></summary>
public sealed class SnoozedReminderService
{
    /// <summary>Tope de entradas conservadas, mismo criterio que el <c>MaxItems</c> de
    /// <see cref="DismissedReminderService"/>. Como <see cref="Snooze"/> reemplaza la entrada previa del
    /// mismo origen y la reinserta al final, la lista queda ordenada por aplazamiento (más antiguo
    /// primero) y el recorte suelta siempre los más viejos.</summary>
    private const int MaxItems = 100;

    private readonly string? _storageDirectory;
    private readonly VaultService? _vaultService;
    private string _path => _vaultService is not null
        ? VaultDataStorage.FilePath(_vaultService, "snoozed-reminders.json")
        : Path.Combine(_storageDirectory!, "snoozed-reminders.json");
    private List<SnoozedReminder> _snoozed;

    /// <summary>Se dispara tras cada cambio persistido, igual que
    /// <see cref="ReminderService.RemindersChanged"/> — así la bandeja de recordatorios se repinta cuando
    /// un aplazamiento se crea, se cancela o vence desde otra pantalla.</summary>
    public event Action? SnoozesChanged;

    /// <param name="storageDirectory">Sustituye la carpeta <c>%AppData%/Enki</c> por defecto — sin uso en
    /// la app real, permite que los tests apunten la persistencia a una carpeta desechable.</param>
    public SnoozedReminderService(string? storageDirectory = null)
    {
        _storageDirectory = storageDirectory ?? AppDataDirectory.Resolve();
        Directory.CreateDirectory(_storageDirectory);
        _snoozed = Load();
        if (TrimExcess()) Save();
    }

    public SnoozedReminderService(VaultService vaultService)
    {
        _vaultService = vaultService;
        _snoozed = Load();
        if (TrimExcess()) Save();
    }

    /// <summary>Recarga los aplazamientos del vault activo.</summary>
    public void Reload()
    {
        _snoozed = Load();
        if (TrimExcess()) Save();
        SnoozesChanged?.Invoke();
    }

    private List<SnoozedReminder> Load() =>
        JsonFileStore.TryLoad<List<SnoozedReminder>>(_path, out var loaded)
            ? loaded!.Where(s => !string.IsNullOrWhiteSpace(s.SourceId)).ToList()
            : [];

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        JsonFileStore.Save(_path, _snoozed);
        SnoozesChanged?.Invoke();
    }

    /// <summary>Aplazamiento vigente de un aviso, o <see langword="null"/> si no lo hay.
    /// <paramref name="dueUtc"/> es la fecha que muestra el aviso ahora mismo: si no coincide con la que
    /// se capturó al aplazar, la entidad se reprogramó por otro lado y el aplazamiento ya no corresponde a
    /// este aviso (mismo criterio que <see cref="DismissedReminderService.IsDismissed"/>).</summary>
    public SnoozedReminder? Find(string sourceId, DateTime dueUtc) =>
        _snoozed.FirstOrDefault(s => s.SourceId == sourceId && s.DueUtcSnapshot == dueUtc);

    public bool IsSnoozed(string sourceId, DateTime dueUtc) => Find(sourceId, dueUtc) is not null;

    /// <summary>Registra (o renueva) el aplazamiento de un aviso y devuelve la entrada resultante. Renovar
    /// incrementa <see cref="SnoozedReminder.Count"/> aunque la fecha de origen haya cambiado entre medias:
    /// el usuario percibe "lo he aplazado otra vez", y ese contador es justo lo que hace visible el
    /// aplazamiento infinito que §9 decidió no limitar.</summary>
    public SnoozedReminder Snooze(SnoozeSourceKind kind, string sourceId, DateTime dueUtc, DateTime untilUtc)
    {
        var previousCount = _snoozed.FirstOrDefault(s => s.SourceId == sourceId)?.Count ?? 0;
        _snoozed.RemoveAll(s => s.SourceId == sourceId);

        var entry = new SnoozedReminder
        {
            SourceId = sourceId,
            SourceKind = kind,
            SnoozedUntilUtc = DateTime.SpecifyKind(untilUtc, DateTimeKind.Utc),
            DueUtcSnapshot = dueUtc,
            Count = previousCount + 1
        };

        _snoozed.Add(entry);
        TrimExcess();
        Save();
        return entry;
    }

    /// <summary>Cancela el aplazamiento de un aviso: vuelve a la bandeja tal y como estaba, sin reavisar.
    /// También es la vía que usa el shell cuando el origen se elimina o se completa.</summary>
    public void Cancel(string sourceId)
    {
        if (_snoozed.RemoveAll(s => s.SourceId == sourceId) == 0) return;
        Save();
    }

    /// <summary>Devuelve y retira los aplazamientos cuya hora ya llegó, para que el llamador convierta
    /// cada uno en un aviso exactamente una vez — mismo contrato que
    /// <see cref="ReminderService.PopDueReminders()"/>.</summary>
    public List<SnoozedReminder> PopDue() => PopDue(DateTime.UtcNow);

    /// <summary>Núcleo de <see cref="PopDue()"/> con el instante inyectado, para tests deterministas.</summary>
    public List<SnoozedReminder> PopDue(DateTime nowUtc)
    {
        var due = _snoozed.Where(s => s.SnoozedUntilUtc <= nowUtc).ToList();
        if (due.Count == 0) return due;

        _snoozed.RemoveAll(s => s.SnoozedUntilUtc <= nowUtc);
        Save();
        return due;
    }

    /// <summary>Suelta los aplazamientos que ya no apuntan a nada — tarjeta borrada o completada,
    /// recordatorio eliminado… El shell le pasa el predicado porque es el único que ve los almacenes de
    /// origen; este servicio no los conoce a propósito.</summary>
    public void PruneWhere(Func<SnoozedReminder, bool> isStale)
    {
        if (_snoozed.RemoveAll(new Predicate<SnoozedReminder>(isStale)) == 0) return;
        Save();
    }

    private bool TrimExcess()
    {
        if (_snoozed.Count <= MaxItems) return false;
        _snoozed.RemoveRange(0, _snoozed.Count - MaxItems);
        return true;
    }
}
