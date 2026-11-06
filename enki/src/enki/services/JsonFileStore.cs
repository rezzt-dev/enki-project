using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace enki.Services;

/// <summary>Serializes reads/writes to Enki's shared JSON state files (<c>settings.json</c>,
/// <c>kanban.json</c>, <c>notifications.json</c>, <c>reminders.json</c>, <c>polar-tasks.json</c>,
/// <c>dismissed-reminders.json</c>, and each vault's own <c>.enki/settings.json</c>) both within this
/// process and across concurrent Enki processes.
///
/// <see cref="SingleInstanceGuard"/> only stops a *second full shell* from starting — a second process still
/// constructs and runs its own copy of every "always live" service (<c>NotificationService</c>,
/// <c>ReminderService</c>, <c>KanbanService</c>…) before it ever reaches that check, so two processes
/// reading/writing the same file with plain <see cref="File.ReadAllText(string)"/>/
/// <see cref="File.WriteAllText(string, string)"/> could interleave writes or read a half-written file
/// (docs/agent-docs/inventario-funcionalidades-y-mejora-continua.md §10.10, gap 32). Each file gets its own
/// named system <see cref="Mutex"/> — the same cross-platform mechanism <see cref="SingleInstanceGuard"/>
/// already relies on — keyed off its full path, and every write goes through a temp-file-then-atomic
/// <see cref="File.Move(string, string, bool)"/> so a concurrent reader never observes a partially-written
/// file.</summary>
internal static class JsonFileStore
{
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(5);

    internal static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>Reads and deserializes <paramref name="path"/> under the file's lock. Returns
    /// <see langword="false"/> — instead of throwing — when the file doesn't exist, is empty, or fails to
    /// parse, so every caller falls back to its own seed/default value exactly like it did before this
    /// helper existed.</summary>
    public static bool TryLoad<T>(string path, out T? value, TimeSpan? mutexTimeout = null)
    {
        using var fileLock = TryLock(path, mutexTimeout ?? MutexTimeout);
        if (fileLock is null)
            LogService.Warning($"Se agotó la espera del bloqueo al leer '{Path.GetFullPath(path)}'; se continúa en modo solo lectura sin mutex");
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<T>(json);
                if (loaded is not null)
                {
                    value = loaded;
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogService.Warning($"No se pudo leer o deserializar el JSON '{Path.GetFullPath(path)}'", ex);
        }

        value = default;
        return false;
    }

    /// <summary>Serializes <paramref name="value"/> and atomically replaces <paramref name="path"/>, under
    /// the same per-path lock <see cref="TryLoad{T}"/> uses — a concurrent writer either sees the file
    /// entirely before or entirely after this call, never a half-written one.</summary>
    public static void Save<T>(string path, T value, TimeSpan? mutexTimeout = null)
    {
        using var fileLock = LockForWrite(path, mutexTimeout ?? MutexTimeout);
        var json = JsonSerializer.Serialize(value, IndentedOptions);
        DurableFile.WriteAllTextAtomic(path, json, privateFile: true);
    }

    /// <summary>Runs <paramref name="action"/> under the same per-path lock <see cref="TryLoad{T}"/>/
    /// <see cref="Save{T}"/> use, for callers that need a read-then-write to happen as one atomic step
    /// instead of two separate lock/unlock cycles — e.g. <c>SettingsService.Save</c>, which reads
    /// <c>VaultPath</c> off disk before deciding what to write back.</summary>
    public static void WithLock(string path, Action action)
    {
        using var fileLock = LockForWrite(path, MutexTimeout);
        action();
    }

    private static LockHandle LockForWrite(string path, TimeSpan timeout) => TryLock(path, timeout)
        ?? throw new TimeoutException(
            $"No se pudo adquirir en {timeout.TotalSeconds:0.###} segundos el bloqueo para escribir '{Path.GetFullPath(path)}'.");

    private static LockHandle? TryLock(string path, TimeSpan timeout)
    {
        var fullPath = Path.GetFullPath(path);
        var mutex = new Mutex(initiallyOwned: false, MutexName(fullPath));
        try
        {
            if (!mutex.WaitOne(timeout))
            {
                mutex.Dispose();
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous holder's process died while the mutex was held (e.g. mid-write) — we still
            // acquired ownership, so proceed rather than propagate the exception.
        }

        return new LockHandle(mutex);
    }

    internal static string MutexName(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)));
        return $"Enki-File-{hash}";
    }

    private sealed class LockHandle(Mutex mutex) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
