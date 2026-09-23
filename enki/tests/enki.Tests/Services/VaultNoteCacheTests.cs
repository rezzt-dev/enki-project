using System;
using System.Collections.Generic;
using System.IO;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public sealed class VaultNoteCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enki-cache-" + Guid.NewGuid().ToString("N"));

    public VaultNoteCacheTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void GetOrRead_OverBudget_EvictsLeastRecentlyUsedEntry()
    {
        var cache = new VaultNoteCache(memoryBudgetBytes: 2_500);
        var reads = new Dictionary<string, int>(StringComparer.Ordinal);
        var a = Create("a.md");
        var b = Create("b.md");
        var c = Create("c.md");

        (NoteFile Note, string Content) Read(FileInfo info)
        {
            reads[info.FullName] = reads.GetValueOrDefault(info.FullName) + 1;
            return (new NoteFile { FullPath = info.FullName, Title = info.Name }, new string(info.Name[0], 300));
        }

        cache.GetOrRead(a, Read);
        cache.GetOrRead(b, Read);
        cache.GetOrRead(a, Read); // a pasa a ser la más reciente.
        cache.GetOrRead(c, Read); // b es la candidata LRU.
        cache.GetOrRead(b, Read);

        Assert.Equal(2, reads[b.FullName]);
        Assert.Equal(1, reads[a.FullName]);
        Assert.True(cache.ApproximateBytes <= 2_500);
    }

    private FileInfo Create(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, name);
        return new FileInfo(path);
    }
}
