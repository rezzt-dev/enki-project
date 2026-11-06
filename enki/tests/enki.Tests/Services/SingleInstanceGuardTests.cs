using System;
using System.Threading;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Fase 6 (gap 32): exercises the "first instance wins, a second one detects it" mechanism behind
/// <see cref="SingleInstanceGuard"/> — the check <c>MainWindowViewModel.ShowVaultSetupOrShell</c> relies on
/// to keep a second Enki window from reopening (and racing writes against) whatever vault the first window
/// already has open. <see cref="SingleInstanceGuard.Acquire"/> itself can only be meaningfully called once
/// per process (it's backed by one static field), so these tests go through the internal
/// <see cref="SingleInstanceGuard.TryAcquire"/> overload with a fresh GUID-based mutex name per test instead
/// — same underlying named-<see cref="Mutex"/> mechanism, but each test gets its own isolated "first
/// instance" race.</summary>
public class SingleInstanceGuardTests
{
    private static string UniqueMutexName() => $"Enki-Test-SingleInstance-{Guid.NewGuid():N}";

    [Fact]
    public void TryAcquire_FirstCaller_WinsTheSlot()
    {
        var name = UniqueMutexName();

        var won = SingleInstanceGuard.TryAcquire(name, out var mutex);

        Assert.True(won);
        mutex.ReleaseMutex();
        mutex.Dispose();
    }

    [Fact]
    public void TryAcquire_SecondCaller_WhileFirstStillHoldsIt_DetectsAnExistingInstance()
    {
        var name = UniqueMutexName();

        var firstWon = SingleInstanceGuard.TryAcquire(name, out var firstMutex);
        var secondWon = SingleInstanceGuard.TryAcquire(name, out var secondMutex);

        Assert.True(firstWon);
        Assert.False(secondWon);

        firstMutex.ReleaseMutex();
        firstMutex.Dispose();
        secondMutex.Dispose();
    }

    /// <summary>Mirrors the real-world sequence a bit more closely than the two tests above: several
    /// "processes" race to acquire the same named mutex at once, and exactly one of them should win —
    /// not zero, not more than one.</summary>
    [Fact]
    public void TryAcquire_ManyConcurrentCallers_ExactlyOneWins()
    {
        var name = UniqueMutexName();
        const int callers = 16;
        var results = new bool[callers];
        var mutexes = new Mutex[callers];
        var barrier = new Barrier(callers);

        var threads = new Thread[callers];
        for (var i = 0; i < callers; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                results[index] = SingleInstanceGuard.TryAcquire(name, out mutexes[index]);
                // A Mutex must be released from the same thread that owns it, so the winner releases
                // here rather than after Join (by which point its thread has already exited).
                if (results[index]) mutexes[index].ReleaseMutex();
            });
            threads[i].Start();
        }

        foreach (var thread in threads) thread.Join();

        Assert.Single(Array.FindAll(results, r => r));
        foreach (var mutex in mutexes) mutex.Dispose();
    }
}
