using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using enki.Models;

namespace enki.Services;

public enum NoteTransclusionStatus
{
    Resolved,
    Broken,
    Ambiguous,
    SectionMissing,
    BlockMissing,
    Locked,
    Cycle,
    DepthLimit,
    ReadFailed
}

/// <summary>Resultado inmutable que consume el visor de Markdown. <see cref="Markdown"/> solo contiene
/// texto cuando se pudo resolver y leer el destino; ningún estado de error filtra contenido parcial.</summary>
public sealed record NoteTransclusionResult(
    NoteTransclusionStatus Status,
    string Reference,
    NoteFile? Note,
    string Markdown,
    IReadOnlyList<string> Ancestry);

/// <summary>Resuelve <c>![[Nota]]</c>, secciones y bloques directamente sobre las notas reales del vault.
/// No mantiene una copia ni un almacén secundario: cada resolución pasa por <see cref="VaultService"/>,
/// cuya caché ya se invalida por fecha/tamaño de archivo.</summary>
public sealed class NoteTransclusionService
{
    public const int MaxDepth = 5;

    private static readonly Regex HeadingRegex = new(
        @"^(?<marks>#{1,6})[ \t]+(?<title>.+?)[ \t]*#*[ \t]*$",
        RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex BlockIdRegex = new(
        @"(?:^|[ \t]+)\^(?<id>[A-Za-z0-9-]+)[ \t]*$",
        RegexOptions.Compiled, RegexSafety.MatchTimeout);

    private readonly VaultService _vaultService;

    public NoteTransclusionService(VaultService vaultService) => _vaultService = vaultService;

    /// <summary>Resuelve una referencia usando el mismo conjunto de notas que los wikilinks. La ruta de
    /// la nota que contiene el embebido debe ir ya en <paramref name="ancestry"/>; eso permite detectar
    /// A → B → A antes de leer y evita exponer una sesión que se haya bloqueado entre ambos pasos.</summary>
    public NoteTransclusionResult Resolve(string reference, IReadOnlyList<NoteFile> notes,
        IReadOnlyList<string>? ancestry = null)
    {
        var chain = ancestry ?? Array.Empty<string>();
        if (chain.Count >= MaxDepth)
            return Failure(NoteTransclusionStatus.DepthLimit, reference, chain);

        var target = WikiLinkParser.ParseTarget(reference);
        if (string.IsNullOrWhiteSpace(target.Note))
            return Failure(NoteTransclusionStatus.Broken, reference, chain);

        var resolution = WikiLinkParser.ResolveTargetWithStatus(notes, target.Note);
        if (resolution.Note is not { } note)
            return Failure(resolution.Status == WikiLinkResolution.Ambiguous
                ? NoteTransclusionStatus.Ambiguous
                : NoteTransclusionStatus.Broken, reference, chain);

        if (chain.Any(path => string.Equals(path, note.FullPath, VaultService.PathComparison)))
            return new NoteTransclusionResult(NoteTransclusionStatus.Cycle, reference, note, string.Empty, chain);

        if (_vaultService.IsPathInLockedSession(note.FullPath))
            return new NoteTransclusionResult(NoteTransclusionStatus.Locked, reference, note, string.Empty, chain);

        string markdown;
        try
        {
            markdown = _vaultService.ReadContent(note);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            LogService.Warning($"No se pudo leer la transclusión '{reference}'", ex);
            return new NoteTransclusionResult(ex is SessionLockedException
                ? NoteTransclusionStatus.Locked
                : NoteTransclusionStatus.ReadFailed, reference, note, string.Empty, chain);
        }

        var status = NoteTransclusionStatus.Resolved;
        if (target.Heading is { } heading)
        {
            if (!TryExtractSection(markdown, heading, out markdown)) status = NoteTransclusionStatus.SectionMissing;
        }
        else if (target.BlockId is { } blockId)
        {
            if (!TryExtractBlock(markdown, blockId, out markdown)) status = NoteTransclusionStatus.BlockMissing;
        }

        var nextChain = chain.Concat([note.FullPath]).ToArray();
        return new NoteTransclusionResult(status, reference, note,
            status == NoteTransclusionStatus.Resolved ? markdown : string.Empty, nextChain);
    }

    internal static bool TryExtractSection(string markdown, string requestedHeading, out string section)
    {
        var lines = NormalizeLines(markdown);
        var wanted = NormalizeAnchor(requestedHeading);
        for (var i = 0; i < lines.Length; i++)
        {
            Match match;
            try { match = HeadingRegex.Match(lines[i]); }
            catch (RegexMatchTimeoutException) { break; }
            if (!match.Success || NormalizeAnchor(match.Groups["title"].Value) != wanted) continue;

            var level = match.Groups["marks"].Value.Length;
            var end = i + 1;
            while (end < lines.Length)
            {
                Match next;
                try { next = HeadingRegex.Match(lines[end]); }
                catch (RegexMatchTimeoutException) { next = Match.Empty; }
                if (next.Success && next.Groups["marks"].Value.Length <= level) break;
                end++;
            }

            section = string.Join('\n', lines[i..end]).TrimEnd();
            return true;
        }

        section = string.Empty;
        return false;
    }

    internal static bool TryExtractBlock(string markdown, string requestedBlockId, out string block)
    {
        var lines = NormalizeLines(markdown);
        for (var i = 0; i < lines.Length; i++)
        {
            Match match;
            try { match = BlockIdRegex.Match(lines[i]); }
            catch (RegexMatchTimeoutException) { break; }
            if (!match.Success || !string.Equals(match.Groups["id"].Value, requestedBlockId,
                    StringComparison.OrdinalIgnoreCase)) continue;

            var clean = lines[i][..match.Index].TrimEnd();
            if (clean.Length > 0)
            {
                block = clean;
                return true;
            }

            // Un identificador en línea propia pertenece al párrafo no vacío inmediatamente anterior.
            var previous = i - 1;
            if (previous >= 0 && !string.IsNullOrWhiteSpace(lines[previous]))
            {
                block = lines[previous].TrimEnd();
                return true;
            }
        }

        block = string.Empty;
        return false;
    }

    private static NoteTransclusionResult Failure(NoteTransclusionStatus status, string reference,
        IReadOnlyList<string> ancestry) => new(status, reference, null, string.Empty, ancestry);

    private static string[] NormalizeLines(string markdown) =>
        markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string NormalizeAnchor(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingDash = false;
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingDash && builder.Length > 0) builder.Append('-');
                builder.Append(c);
                pendingDash = false;
            }
            else if (char.IsWhiteSpace(c) || c == '-')
            {
                pendingDash = true;
            }
        }
        return builder.ToString();
    }
}
