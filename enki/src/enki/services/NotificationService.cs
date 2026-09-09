using System;
using System.Collections.Generic;
using System.IO;
using enki.Models;

namespace enki.Services;

/// <summary>Persists the app's own event log (vault switched, sync errors, etc.) to
/// <c>%AppData%/Enki/notifications.json</c> — same load/save shape as <see cref="SettingsService"/> and
/// <see cref="KanbanService"/>. Capped at <see cref="MaxItems"/> so the file can't grow forever.</summary>
public sealed class NotificationService
{
    private const int MaxItems = 50;

    private readonly string _path;
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

        var configDir = storageDirectory ?? AppDataDirectory.Resolve();
        Directory.CreateDirectory(configDir);
        _path = Path.Combine(configDir, "notifications.json");
        Notifications = Load();
    }

    private List<AppNotification> Load()
    {
        if (JsonFileStore.TryLoad<List<AppNotification>>(_path, out var loaded)) return loaded!;

        if (File.Exists(_path)) LoadedFromCorruptData = true;
        return [];
    }

    public void Save() => JsonFileStore.Save(_path, Notifications);

    /// <summary>Inserts, persists and broadcasts a new notification — unless its severity is muted in
    /// Ajustes › Notificaciones (see docs/notificaciones-y-recordatorios.md), in which case the object is
    /// still handed back to the caller (keeps the signature simple) but never recorded or broadcast, so a
    /// muted category leaves no trace in the log either.</summary>
    public AppNotification Push(string title, string message = "", NotificationSeverity severity = NotificationSeverity.Info)
        => PushCore(title, message, severity);

    /// <summary>Guarda las claves junto al texto de respaldo para que el historial siga el idioma
    /// activo incluso tras reiniciar. Mantiene el mismo almacén y los mismos eventos de notificación.</summary>
    public AppNotification PushLocalized(LocalizedText title, LocalizedText message, NotificationSeverity severity = NotificationSeverity.Info)
        => PushCore(enki.Localization.LocalizedTextFormatter.Resolve(title),
            enki.Localization.LocalizedTextFormatter.Resolve(message), severity, title, message);

    private AppNotification PushCore(string title, string message, NotificationSeverity severity,
        LocalizedText? localizedTitle = null, LocalizedText? localizedMessage = null)
    {
        var notification = new AppNotification
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            Message = message,
            LocalizedTitle = localizedTitle,
            LocalizedMessage = localizedMessage,
            Severity = severity
        };

        if (IsMuted(severity)) return notification;

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
