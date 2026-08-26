using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using enki.Models;

namespace enki.Services;

/// <summary>Outcome of resolving a wikilink's raw target text against a note set — distinguishes a target
/// that matches no note (<see cref="Broken"/>) from one that matches more than one note by title with no
/// filename match to disambiguate it (<see cref="Ambiguous"/>), so callers that only had a plain nullable
/// result before — "ambos casos se ven igual de 'no hacen nada'" (see docs/analisis-proyecto/roadmap.md,
/// Fase 5) — can tell the two apart — e.g. to render them with a different style
/// (see <c>Views.Editor.Markdown.MarkdownLivePreviewColorizer</c>).</summary>
public enum WikiLinkResolution
{
    Resolved,
    Broken,
    Ambiguous
}

/// <summary>One <c>[[Target]]</c> / <c>[[Target|Alias]]</c> / <c>[[Target#Anchor]]</c> occurrence found in
/// raw Markdown, with its position — <see cref="Start"/>/<see cref="End"/> span the whole
/// <c>[[...]]</c> construct (absolute offsets into the source text), not just the target text.</summary>
public readonly struct WikiLinkOccurrence
{
    /// <summary>Target text between <c>[[</c> and the first <c>|</c>/<c>#</c> (or <c>]]</c>), trimmed —
    /// what <see cref="WikiLinkParser.ResolveTarget"/> resolves against a note set.</summary>
    public required string Target { get; init; }

    /// <summary>Absolute offset of the opening <c>[[</c>.</summary>
    public required int Start { get; init; }

    /// <summary>Absolute offset one past the closing <c>]]</c>.</summary>
    public required int End { get; init; }

    /// <summary>The exact matched substring, e.g. <c>"[[Old Title|alias]]"</c> — kept alongside
    /// <see cref="Target"/> so callers that need to rewrite the link (see
    /// <see cref="VaultService.RenameNote"/>) can recover the <c>|alias</c>/<c>#anchor</c> suffix without
    /// re-scanning the source.</summary>
    public required string RawText { get; init; }
}

/// <summary>Extracts Obsidian-style <c>[[Note]]</c> / <c>[[Note|Alias]]</c> references from raw Markdown.
/// Purely textual — independent of the live-preview editor's own Markdown model — since all it needs to
/// feed is the session graph's derived edges, not any on-screen rendering.</summary>
public static class WikiLinkParser
{
    private static readonly Regex Pattern = new(@"\[\[([^\]|#]+?)(?:[|#][^\]]*)?\]\]", RegexOptions.Compiled);

    /// <summary>Every wikilink occurrence in <paramref name="markdown"/>, in document order, with its
    /// position — the shared primitive behind <see cref="ExtractTargets"/>,
    /// <see cref="EditorBacklinksService.FindBacklinks"/> and the rename-cascade rewrite in
    /// <see cref="VaultService.RenameNote"/>, so all three agree on what counts as a wikilink.</summary>
    public static IEnumerable<WikiLinkOccurrence> FindOccurrences(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) yield break;

        foreach (Match match in Pattern.Matches(markdown))
        {
            var target = match.Groups[1].Value.Trim();
            if (target.Length == 0) continue;

            yield return new WikiLinkOccurrence
            {
                Target = target,
                Start = match.Index,
                End = match.Index + match.Length,
                RawText = match.Value
            };
        }
    }

    /// <summary>Distinct link targets referenced in <paramref name="markdown"/>, trimmed, in first-seen order.</summary>
    public static IReadOnlyList<string> ExtractTargets(string markdown)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<string>();

        foreach (var occurrence in FindOccurrences(markdown))
        {
            if (seen.Add(occurrence.Target)) targets.Add(occurrence.Target);
        }

        return targets;
    }

    /// <summary>Resolves a single wikilink <paramref name="target"/> (as extracted by
    /// <see cref="ExtractTargets"/> — no brackets, alias or heading anchor) to the note it refers to among
    /// <paramref name="notes"/>, and says which of the three ways that resolution went (see
    /// docs/analisis-proyecto/roadmap.md, Fase 5). An exact filename match wins first;
    /// failing that, a title match only counts if it's unambiguous among <paramref name="notes"/> — two
    /// notes sharing a title (e.g. two untitled "Nueva nota" files) has no plausible-but-wrong guess to
    /// make, so it resolves to <see cref="WikiLinkResolution.Ambiguous"/> rather than jumping to a
    /// possibly-unrelated note. Same treatment for a filename shared by more than one note: guaranteed
    /// unique within a single folder (<see cref="VaultService.CreateNote"/> de-duplicates there), but not
    /// vault-wide — two different sessions can each hold a "Nueva nota.md" the same way they can each hold
    /// a note titled "Nueva nota", now that <paramref name="notes"/> may span the whole vault
    /// (<see cref="VaultService.GetAllNotesAcrossVault"/>) instead of just one session. This is the single
    /// resolution strategy every consumer shares — <see cref="EditorBacklinksService.FindBacklinks"/>, the
    /// editor's Ctrl+click (<c>ViewModels.EditorViewModel.OpenWikiLink</c>) and its live-preview styling
    /// (<c>Views.Editor.Markdown.MarkdownLivePreviewColorizer</c>) — so the backlinks panel and a click on
    /// the same link can never disagree about what it points to.</summary>
    public static (WikiLinkResolution Status, NoteFile? Note) ResolveTargetWithStatus(IReadOnlyList<NoteFile> notes, string target)
    {
        var fileNameMatches = notes.Where(n =>
            string.Equals(Path.GetFileNameWithoutExtension(n.FullPath), target, StringComparison.OrdinalIgnoreCase)).ToList();
        if (fileNameMatches.Count == 1) return (WikiLinkResolution.Resolved, fileNameMatches[0]);
        if (fileNameMatches.Count > 1) return (WikiLinkResolution.Ambiguous, null);

        var titleMatches = notes.Where(n => string.Equals(n.Title, target, StringComparison.OrdinalIgnoreCase)).ToList();
        if (titleMatches.Count == 1) return (WikiLinkResolution.Resolved, titleMatches[0]);

        return (titleMatches.Count > 1 ? WikiLinkResolution.Ambiguous : WikiLinkResolution.Broken, null);
    }

    /// <summary>Convenience wrapper over <see cref="ResolveTargetWithStatus"/> for callers that only care
    /// about the resolved note (or lack of one), not why — the editor's Ctrl+click, and the rename cascade
    /// deciding which links to rewrite.</summary>
    public static NoteFile? ResolveTarget(IReadOnlyList<NoteFile> notes, string target) =>
        ResolveTargetWithStatus(notes, target).Note;
}
