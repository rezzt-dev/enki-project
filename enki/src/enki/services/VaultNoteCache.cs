using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private const long DefaultMemoryBudgetBytes = 64L * 1024 * 1024;

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries;
    private readonly long _memoryBudgetBytes;
    private long _approximateBytes;
    private long _usageSequence;

    public VaultNoteCache(long memoryBudgetBytes = DefaultMemoryBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryBudgetBytes);
        _memoryBudgetBytes = memoryBudgetBytes;
        _entries = new Dictionary<string, Entry>(PathComparer());
    }

    private static StringComparer PathComparer() =>
        VaultService.PathComparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed record Entry(NoteFile Note, string Content, DateTime FileWriteUtc, long ApproximateBytes)
    {
        public long LastUsed { get; set; }
    }

    internal int Count { get { lock (_lock) return _entries.Count; } }
    internal long ApproximateBytes { get { lock (_lock) return _approximateBytes; } }

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
            {
                cached.LastUsed = ++_usageSequence;
                return (cached.Note.Clone(), cached.Content);
            }
        }

        var result = read(info);
        lock (_lock) { PutUnderLock(info.FullName, result.Note, result.Content, info.LastWriteTimeUtc); }
        return (result.Note.Clone(), result.Content);
    }

    /// <summary>Seeds/refreshes the cache directly with content and metadata this call already has in
    /// hand (a note that was just written), so the very next read of it doesn't need to touch disk at all.</summary>
    public void Put(string fullPath, NoteFile note, string content, DateTime fileWriteUtc)
    {
        lock (_lock) { PutUnderLock(fullPath, note, content, fileWriteUtc); }
    }

    /// <summary>Drops any cached entry for <paramref name="fullPath"/> — called wherever a note stops
    /// existing at that path (deleted, moved, renamed), so a stale entry never lingers under a path a
    /// future scan won't even enumerate.</summary>
    public void Invalidate(string fullPath)
    {
        lock (_lock) { RemoveUnderLock(fullPath); }
    }

    /// <summary>Drops every cached entry under <paramref name="rootPath"/> — called when a session is
    /// locked, so no plaintext content cached in memory while it was unlocked survives the lock (the cache
    /// itself never persists to disk, but keeping decrypted content resident in memory after the user
    /// deliberately locked the session would defeat the point of locking it).</summary>
    public void InvalidatePrefix(string rootPath)
    {
        lock (_lock)
        {
            var prefix = rootPath.EndsWith(Path.DirectorySeparatorChar) ? rootPath : rootPath + Path.DirectorySeparatorChar;
            var toRemove = _entries.Keys
                .Where(key => key.StartsWith(prefix, VaultService.PathComparison) || string.Equals(key, rootPath, VaultService.PathComparison))
                .ToList();
            foreach (var key in toRemove) RemoveUnderLock(key);
        }
    }

    /// <summary>Vacía la caché entera — lo llama <see cref="VaultService.SetVaultPath"/> al cambiar de
    /// vault en caliente (T4). <see cref="InvalidatePrefix"/> no sirve para eso: las notas del vault
    /// anterior no están bajo la ruta del nuevo, así que sin este método se quedarían residentes en
    /// memoria (con su contenido) durante todo lo que dure la sesión del vault nuevo.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _approximateBytes = 0;
        }
    }

    private void PutUnderLock(string fullPath, NoteFile note, string content, DateTime fileWriteUtc)
    {
        RemoveUnderLock(fullPath);

        var approximateBytes = EstimateBytes(fullPath, note, content);
        _entries[fullPath] = new Entry(note, content, fileWriteUtc, approximateBytes)
        {
            LastUsed = ++_usageSequence
        };
        _approximateBytes += approximateBytes;
        TrimUnderLock();
    }

    private void RemoveUnderLock(string fullPath)
    {
        if (!_entries.Remove(fullPath, out var removed)) return;
        _approximateBytes -= removed.ApproximateBytes;
    }

    /// <summary>Desaloja primero la nota menos usada. La estimación cuenta los caracteres UTF-16 de
    /// contenido y metadatos; no pretende medir cada cabecera del runtime, solo imponer un techo estable
    /// al componente dominante (el texto completo de las notas).</summary>
    private void TrimUnderLock()
    {
        while (_approximateBytes > _memoryBudgetBytes && _entries.Count > 0)
        {
            var oldest = _entries.Aggregate((left, right) =>
                left.Value.LastUsed <= right.Value.LastUsed ? left : right);
            RemoveUnderLock(oldest.Key);
        }
    }

    private static long EstimateBytes(string key, NoteFile note, string content)
    {
        static long StringBytes(string? value) => (long)(value?.Length ?? 0) * sizeof(char);

        var total = StringBytes(key) + StringBytes(content) + StringBytes(note.FullPath)
                    + StringBytes(note.Title) + StringBytes(note.Preview) + 128;
        foreach (var tag in note.Tags) total += StringBytes(tag) + 16;
        return total;
    }
}
