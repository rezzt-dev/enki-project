using System;
using System.Collections.Generic;
using System.IO;
using enki.Models;

namespace enki.Services;

/// <summary>In-memory cache of parsed note metadata + raw content, keyed by full path, shared by every
/// <see cref="VaultService"/> method that reads notes (the backlinks panel, global search, the quick
/// switcher, the note grid, session graphs) — a full vault scan used to mean every one of those callers
/// independently re-read and re-parsed the same <c>.md</c> files from disk, and the backlinks panel alone
/// did it twice per refresh (once for metadata via <c>GetAllNotesRecursive</c>, once more for content via
/// its own <c>File.ReadAllText</c>) (docs/analisis-proyecto/roadmap.md, Fase 6, análisis §3).
///
/// <para>Freshness is checked against <see cref="FileInfo.LastWriteTimeUtc"/>, captured for free as part of
/// the directory enumeration <see cref="VaultService"/> already has to do (no extra stat call per file), so
/// an edit made outside the app (another editor, another Enki window) is still picked up on the next scan
/// instead of serving stale content forever. Every in-app write additionally calls <see cref="Put"/> or
/// <see cref="Invalidate"/> directly so the common case — this app being the one making the change — never
/// depends on that mtime granularity at all.</para></summary>
internal sealed class VaultNoteCache
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries;

    public VaultNoteCache()
    {
        _entries = new Dictionary<string, Entry>(PathComparer());
    }

    private static StringComparer PathComparer() =>
        VaultService.PathComparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private readonly record struct Entry(NoteFile Note, string Content, DateTime FileWriteUtc);

    /// <summary>Returns the cached (note, content) pair for <paramref name="info"/> if it's still fresh
    /// (same <see cref="FileInfo.LastWriteTimeUtc"/> as when it was cached), otherwise reads it via
    /// <paramref name="read"/> and caches the result. Always hands back a fresh
    /// <see cref="NoteFile.Clone"/> of whatever's cached rather than the stored instance itself — callers
    /// throughout <see cref="VaultService"/> mutate a returned <see cref="NoteFile"/> in place (a rename
    /// reassigns its own <c>FullPath</c>), which would otherwise silently corrupt the cache entry (and any
    /// snapshot another caller took moments earlier from the same entry) instead of just updating the
    /// caller's own copy.</summary>
    public (NoteFile Note, string Content) GetOrRead(FileInfo info, Func<FileInfo, (NoteFile Note, string Content)> read)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(info.FullName, out var cached) && cached.FileWriteUtc == info.LastWriteTimeUtc)
                return (cached.Note.Clone(), cached.Content);
        }

        var result = read(info);
        lock (_lock) { _entries[info.FullName] = new Entry(result.Note, result.Content, info.LastWriteTimeUtc); }
        return (result.Note.Clone(), result.Content);
    }

    /// <summary>Seeds/refreshes the cache directly with content and metadata this call already has in
    /// hand (a note that was just written), so the very next read of it doesn't need to touch disk at all.</summary>
    public void Put(string fullPath, NoteFile note, string content, DateTime fileWriteUtc)
    {
        lock (_lock) { _entries[fullPath] = new Entry(note, content, fileWriteUtc); }
    }

    /// <summary>Drops any cached entry for <paramref name="fullPath"/> — called wherever a note stops
    /// existing at that path (deleted, moved, renamed), so a stale entry never lingers under a path a
    /// future scan won't even enumerate.</summary>
    public void Invalidate(string fullPath)
    {
        lock (_lock) { _entries.Remove(fullPath); }
    }
}
