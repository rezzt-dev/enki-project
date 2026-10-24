using System.Threading;

namespace enki.Services;

/// <summary>Detects whether this process is the first Enki instance for the current user.
/// The Mutex is kept alive for the process lifetime (held in a static field) so later
/// launches see it as still owned; it is released automatically when the process exits.</summary>
public static class SingleInstanceGuard
{
    private const string MutexName = "Enki-SingleInstance-Guard";

    private static Mutex? _mutex;

    public static bool IsFirstInstance { get; private set; }

    public static void Acquire() => IsFirstInstance = TryAcquire(MutexName, out _mutex);

    /// <summary>Underlying mechanism behind <see cref="Acquire"/>, exposed with an explicit mutex name so
    /// tests can exercise "first instance wins, second instance detects it" without touching the real
    /// process-wide <see cref="MutexName"/> (which <see cref="Acquire"/> can only meaningfully claim once
    /// per process) — see <c>SingleInstanceGuardTests</c>. Returns <see langword="true"/> only when this
    /// call is the one that created the named system mutex; the caller owns disposing the returned
    /// <see cref="Mutex"/> once it's done "holding" the slot.</summary>
    internal static bool TryAcquire(string mutexName, out Mutex mutex)
    {
        mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        return createdNew;
    }
}
