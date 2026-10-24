using System;
using System.Collections.Generic;
using System.IO;
using enki.Models;

namespace enki.Services;

/// <summary>Persists the app's own event log (vault switched, sync errors, etc.) to
/// <c>&lt;vault&gt;/.enki/notifications.json</c> — same load/save shape as <see cref="SettingsService"/> and
/// <see cref="KanbanService"/>. Capped at <see cref="MaxItems"/> so the file can't grow forever.</summary>
public sealed class NotificationService
{
    private const int MaxItems = 50;

    private readonly string? _storageDirectory;
    private readonly VaultService? _vaultService;
    private string _path => _vaultService is not null
        ? VaultDataStorage.FilePath(_vaultService, "notifications.json")
        : Path.Combine(_storageDirectory!, "notifications.json");
    private readonly SettingsService _settingsService;

    public List<AppNotification> Notifications { get; private set; }

    /// <summary>True cuando <c>notifications.json</c> existía pero no se pudo leer o deserializar — el
    /// registro se reemplazó por una lista vacía. Mismo patrón que
    /// <see cref="KanbanService.LoadedFromCorruptData"/>/<see cref="SettingsService.LoadedFromCorruptData"/>
    /// (docs/analisis-proyecto/roadmap.md, Fase 1).</summary>
    public bool LoadedFromCorruptData { get; private set; }

    /// <summary>Raised after a new notification is pushed and persisted, so open UI can refresh.</summary>
    public event Action<AppNotification>? NotificationAdded;

    /// <summary>Raised after the log is mutated in any way (added, read state changed, cleared), so the
    /// bell dropdown (<see cref="NotificationCenterViewModel"/>) and the full "Notificaciones" page
    /// (<see cref="NotificationsViewModel"/>) stay in sync with each other even though they're separate
    /// view-model instances reading the same list.</summary>
    public event Action? NotificationsChanged;

    /// <param name="storageDirectory">Overrides the default <c>%AppData%/Enki</c> folder — unused in the
    /// running app, but lets tests point persistence at a throwaway directory instead of the developer's
    /// real notification log (see enki.Tests).</param>
    public NotificationService(SettingsService settingsService, string? storageDirectory = null)
    {
        _settingsService = settingsService;

        _storageDirectory = storageDirectory ?? AppDataDirectory.Resolve();
        Directory.CreateDirectory(_storageDirectory);
        Notifications = Load();
    }

    public NotificationService(SettingsService settingsService, VaultService vaultService)
    {
        _settingsService = settingsService;
        _vaultService = vaultService;
        Notifications = Load();
    }

    /// <summary>Recarga el historial de avisos del vault activo.</summary>
    public void Reload()
    {
        LoadedFromCorruptData = false;
        Notifications = Load();
        NotificationsChanged?.Invoke();
    }

    private List<AppNotification> Load()
    {
        if (JsonFileStore.TryLoad<List<AppNotification>>(_path, out var loaded)) return loaded!;

        if (File.Exists(_path)) LoadedFromCorruptData = true;
        return [];
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        JsonFileStore.Save(_path, Notifications);
    }

    /// <summary>Inserta, persiste y anuncia un aviso salvo que el interruptor global esté apagado o
    /// su categoría esté silenciada en Ajustes › Notificaciones. En ambos casos devuelve el objeto al
    /// llamante sin guardarlo ni emitir eventos; tampoco se disparan toast o sonido.</summary>
    public AppNotification Push(string title, string message = "", NotificationSeverity severity = NotificationSeverity.Info)
        => PushCore(title, message, severity);

    /// <summary>Guarda las claves junto al texto de respaldo para que el historial siga el idioma
    /// activo incluso tras reiniciar. Mantiene el mismo almacén y los mismos eventos de notificación.</summary>
    public AppNotification PushLocalized(LocalizedText title, LocalizedText message, NotificationSeverity severity = NotificationSeverity.Info)
        => PushCore(enki.Localization.LocalizedTextFormatter.Resolve(title),
            enki.Localization.LocalizedTextFormatter.Resolve(message), severity, title, message);

    /// <summary>Igual que <see cref="PushLocalized"/> pero dejando anotado de qué vencimiento nace el
    /// aviso, para que el centro de notificaciones pueda ofrecer "Aplazar" en esa fila (T5). Un método
    /// aparte y no un parámetro opcional más porque es el único tipo de aviso que lo lleva, y así las
    /// decenas de <c>Push</c> repartidos por la app no cambian de firma.</summary>
    public AppNotification PushDue(LocalizedText title, LocalizedText message, SnoozeTarget snoozeTarget,
        NotificationSeverity severity = NotificationSeverity.Info)
        => PushCore(enki.Localization.LocalizedTextFormatter.Resolve(title),
            enki.Localization.LocalizedTextFormatter.Resolve(message), severity, title, message, snoozeTarget);

    /// <summary>Aviso con un enlace que el usuario puede abrir desde la campana — hoy, el de versión
    /// nueva (T9). Método aparte por lo mismo que <see cref="PushDue"/>: es el único tipo de aviso que lo
    /// lleva.</summary>
    public AppNotification PushWithLink(LocalizedText title, LocalizedText message, string linkUrl,
        NotificationSeverity severity = NotificationSeverity.Info)
        => PushCore(enki.Localization.LocalizedTextFormatter.Resolve(title),
            enki.Localization.LocalizedTextFormatter.Resolve(message), severity, title, message, linkUrl: linkUrl);

    private AppNotification PushCore(string title, string message, NotificationSeverity severity,
        LocalizedText? localizedTitle = null, LocalizedText? localizedMessage = null,
        SnoozeTarget? snoozeTarget = null, string? linkUrl = null)
    {
        var notification = new AppNotification
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            Message = message,
            LocalizedTitle = localizedTitle,
            LocalizedMessage = localizedMessage,
            Severity = severity,
            SnoozeTarget = snoozeTarget,
            LinkUrl = linkUrl
        };

        if (!_settingsService.Settings.NotificationsEnabled || IsMuted(severity)) return notification;

        Notifications.Insert(0, notification);
        if (Notifications.Count > MaxItems) Notifications.RemoveRange(MaxItems, Notifications.Count - MaxItems);

        try
        {
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // El aviso de un fallo de escritura debe poder mostrarse aunque el disco siga fallando.
            LogService.Warning("No se pudo persistir la notificación; se conserva en memoria", ex);
        }
        NotificationAdded?.Invoke(notification);
        NotificationsChanged?.Invoke();
        return notification;
    }

    private bool IsMuted(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Info => _settingsService.Settings.MuteInfoNotifications,
        NotificationSeverity.Success => _settingsService.Settings.MuteSuccessNotifications,
        NotificationSeverity.Warning => _settingsService.Settings.MuteWarningNotifications,
        NotificationSeverity.Error => _settingsService.Settings.MuteErrorNotifications,
        _ => false
    };

    public void MarkAsRead(string id)
    {
        var notification = Notifications.Find(n => n.Id == id);
        if (notification is null || notification.IsRead) return;

        notification.IsRead = true;
        Save();
        NotificationsChanged?.Invoke();
    }

    public void MarkAllAsRead()
    {
        var changed = false;
        foreach (var notification in Notifications)
        {
            if (notification.IsRead) continue;
            notification.IsRead = true;
            changed = true;
        }

        if (!changed) return;
        Save();
        NotificationsChanged?.Invoke();
    }

    public void ClearAll()
    {
        if (Notifications.Count == 0) return;
        Notifications.Clear();
        Save();
        NotificationsChanged?.Invoke();
    }
}
