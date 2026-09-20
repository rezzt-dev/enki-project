using System;
using System.IO;
using System.Threading;
using enki.Views.Editor.Markdown;
using Xunit;

namespace enki.Tests.Views.Editor.Markdown;

public sealed class MarkdownImageCacheSafetyTests : IDisposable
{
    public MarkdownImageCacheSafetyTests() => MarkdownImageCache.Clear();
    public void Dispose() => MarkdownImageCache.Clear();

    [Fact]
    public void IsNaturalSizeAllowed_RejectsDecompressionBombBeforeDecode()
    {
        Assert.True(MarkdownImageCache.IsNaturalSizeAllowed(10_000, 5_000));
        Assert.False(MarkdownImageCache.IsNaturalSizeAllowed(100, 20_000_000));
        Assert.False(MarkdownImageCache.IsNaturalSizeAllowed(int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void InvalidatePrefix_LoadFinishingLater_DoesNotReinsertEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "enki-images-" + Guid.NewGuid().ToString("N"));
        var noteDirectory = Path.Combine(root, "session", "notes");
        var releaseRead = new ManualResetEventSlim();
        var context = new MarkdownImageContext
        {
            NoteDirectory = noteDirectory,
            VaultPath = root,
            ReadBytes = _ => { releaseRead.Wait(TimeSpan.FromSeconds(2)); return []; }
        };

        // La ruta existe para que Load llegue al delegado bloqueado y simule un decode en curso.
        Directory.CreateDirectory(noteDirectory);
        var image = Path.Combine(noteDirectory, "image.png");
        File.WriteAllBytes(image, [1]);
        try
        {
            MarkdownImageCache.Get(context, image, 400, () => { });
            Assert.Equal(1, MarkdownImageCache.EntryCount);

            MarkdownImageCache.InvalidatePrefix(Path.Combine(root, "session"));
            releaseRead.Set();

            Assert.True(SpinWait.SpinUntil(() => MarkdownImageCache.EntryCount == 0, TimeSpan.FromSeconds(2)));
            Thread.Sleep(100);
            Assert.Equal(0, MarkdownImageCache.EntryCount);
        }
        finally
        {
            releaseRead.Set();
            Directory.Delete(root, recursive: true);
        }
    }
}
