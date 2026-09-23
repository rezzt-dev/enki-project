using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Fase 6 (gap 32): <see cref="JsonFileStore"/> is what every shared JSON state file
/// (<c>settings.json</c>, <c>kanban.json</c>, <c>notifications.json</c>…) now goes through instead of raw
/// <c>File.ReadAllText</c>/<c>File.WriteAllText</c>, specifically so two Enki processes (or, as exercised
/// here, many concurrent writers within one process) sharing a file can't interleave writes or observe a
/// half-written one. Each test uses its own throwaway temp file so the suite can run fully in parallel with
/// itself.</summary>
public class JsonFileStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        var tempLeftovers = Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_path) + ".tmp-*");
        foreach (var leftover in tempLeftovers) File.Delete(leftover);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        JsonFileStore.Save(_path, new List<string> { "a", "b", "c" });

        var loaded = JsonFileStore.TryLoad<List<string>>(_path, out var value);

        Assert.True(loaded);
        Assert.Equal(["a", "b", "c"], value);
    }

    [Fact]
    public void Save_OnUnix_CreatesPrivateStateFile()
    {
        if (OperatingSystem.IsWindows()) return;

        JsonFileStore.Save(_path, new { Secret = "value" });

        Assert.Equal(PrivateDataPermissions.FileMode, File.GetUnixFileMode(_path));
    }

    [Fact]
    public void TryLoad_MissingFile_ReturnsFalse()
    {
        var loaded = JsonFileStore.TryLoad<List<string>>(_path, out var value);

        Assert.False(loaded);
        Assert.Null(value);
    }

    [Fact]
    public void TryLoad_CorruptFile_ReturnsFalseInsteadOfThrowing()
    {
        File.WriteAllText(_path, "{ not valid json ][");

        var loaded = JsonFileStore.TryLoad<List<string>>(_path, out var value);

        Assert.False(loaded);
        Assert.Null(value);
    }

    [Fact]
    public void Save_DoesNotLeaveTempFilesBehind()
    {
        JsonFileStore.Save(_path, new List<int> { 1 });

        var leftovers = Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".tmp-*");
        Assert.Empty(leftovers);
    }

    /// <summary>The core regression this whole file exists for: without a lock, N threads racing
    /// <c>File.WriteAllText</c> on the same path can interleave and leave a file <c>JsonSerializer</c> can't
    /// parse at all (mid-write torn read) — the exact "kanban.json corrupto" failure mode
    /// <c>KanbanService.LoadedFromCorruptData</c> exists to detect. Fires 64 concurrent writers at the same
    /// file and asserts the file is always fully valid JSON afterwards, holding exactly one of the values
    /// a writer actually wrote (last-writer-wins is fine; a torn/merged write is not).</summary>
    [Fact]
    public async Task Save_UnderConcurrentWriters_NeverProducesATornFile()
    {
        const int writerCount = 64;
        var candidateValues = Enumerable.Range(0, writerCount).ToList();

        await Task.WhenAll(candidateValues.Select(i => Task.Run(() => JsonFileStore.Save(_path, new List<int> { i, i, i }))));

        var raw = File.ReadAllText(_path);
        var parsed = JsonSerializer.Deserialize<List<int>>(raw);

        Assert.NotNull(parsed);
        Assert.Equal(3, parsed!.Count);
        Assert.True(parsed.Distinct().Count() == 1, "A torn write mixed values from two different writers.");
        Assert.Contains(parsed[0], candidateValues);
    }

    /// <summary>Same race as above, but for the read side: a reader running concurrently with writers must
    /// always see either a complete previous value or a complete new one — <see cref="JsonFileStore.TryLoad{T}"/>
    /// should never observe (and fail to parse) a half-written temp-file swap in progress.</summary>
    [Fact]
    public async Task TryLoad_UnderConcurrentWriters_NeverObservesATornFile()
    {
        JsonFileStore.Save(_path, new List<int> { -1, -1, -1 });

        var writers = Enumerable.Range(0, 32)
            .Select(i => Task.Run(() => JsonFileStore.Save(_path, new List<int> { i, i, i })));
        var readers = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                var ok = JsonFileStore.TryLoad<List<int>>(_path, out var value);
                Assert.True(ok);
                Assert.Equal(3, value!.Count);
                Assert.True(value.Distinct().Count() == 1);
            }));

        await Task.WhenAll(writers.Concat(readers));
    }

    [Fact]
    public async Task Save_WhenAnotherThreadRetainsMutex_TimesOutInsteadOfWaitingForever()
    {
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(false, JsonFileStore.MutexName(_path));
            mutex.WaitOne();
            acquired.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(2)));

        try
        {
            Assert.Throws<TimeoutException>(() =>
                JsonFileStore.Save(_path, new List<int> { 1 }, TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            release.Set();
            await holder;
        }
    }

    [Fact]
    public async Task TryLoad_WhenMutexTimesOut_ReadsAtomicFileWithoutTheLock()
    {
        JsonFileStore.Save(_path, new List<int> { 7 });
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(false, JsonFileStore.MutexName(_path));
            mutex.WaitOne();
            acquired.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(2)));

        try
        {
            Assert.True(JsonFileStore.TryLoad<List<int>>(
                _path, out var value, TimeSpan.FromMilliseconds(50)));
            Assert.Equal([7], value);
        }
        finally
        {
            release.Set();
            await holder;
        }
    }
}
