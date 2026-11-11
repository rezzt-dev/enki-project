using System;
using System.Collections.Generic;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>Notas relacionadas y enlaces sugeridos (AIA-13). Reúne, para una nota, las candidatas a enlazarse con ella
/// y por qué: las que ella nombra sin enlazar, las que la nombran a ella sin enlazarla, las que comparten etiquetas o
/// enlaces (<see cref="NoteRelationsService"/>, el mismo del panel de enlaces del editor) y, con el índice semántico
/// encendido, las que tratan de lo mismo con otras palabras. No escribe nada: el modelo explica las propuestas y,
/// si el usuario acepta, añade los enlaces con <c>edit_note</c>, que pasa por la revisión de cambios si está activa.</summary>
public sealed partial class AiVaultToolbox
{
    private const int DefaultLinkSuggestions = 10;
    private const int MaxLinkSuggestions = 30;

    /// <summary>Por debajo de esta similitud, una nota "parecida" del índice semántico no aporta nada.</summary>
    private const double MinSemanticRelatedScore = 0.55;

    private void RegisterLinkSuggestionTools()
    {
        Register("suggest_links", AiToolScope.Content, AiToolEffect.Read,
            "Suggests [[wikilinks]] and related notes for a note, each with the reasons: notes it mentions by name without linking "
            + "(add the link in this note), notes that mention it without linking (add the link in the other note), notes sharing "
            + "tags or links, and, when semantic search is on, notes about the same topic in other words. Notes already linked in "
            + "either direction are left out. It changes nothing: explain the suggestions briefly and add links with edit_note only "
            + "if the user asks or agrees.",
            Schema(WorkspaceParam(), Req("path", Text("Note path or exact title.")),
                Opt("limit", Number($"Maximum suggestions (default {DefaultLinkSuggestions}, max {MaxLinkSuggestions})."))),
            call =>
            {
                var (_, path) = FindNote(call);
                var scope = call.Context.Scope;
                var limit = call.GetInteger("limit", 1, MaxLinkSuggestions) ?? DefaultLinkSuggestions;
                var notes = AccessibleNotesWithContent(scope);
                var target = notes.FirstOrDefault(n => VaultService.PathsEqual(n.Note.FullPath, path));
                if (target.Note is null) throw NoteNotFound(call.GetText("path"));
                call.Target(AiToolTargetKind.Note, target.Note.Title, path);

                var all = notes.Select(n => n.Note).ToList();
                var linked = LinkedPaths(notes, target.Note, target.Content);
                var candidates = new Dictionary<string, LinkCandidate>(PathComparer);
                LinkCandidate For(NoteFile note) => candidates.TryGetValue(note.FullPath, out var existing)
                    ? existing
                    : candidates[note.FullPath] = new LinkCandidate(note);

                foreach (var mention in NoteRelationsService.FindMentionedNotes(notes, target.Note, MaxLinkSuggestions))
                {
                    var candidate = For(all.First(n => VaultService.PathsEqual(n.FullPath, mention.FullPath)));
                    candidate.Reasons.Add("mentioned_in_this_note");
                    candidate.LinkName ??= mention.LinkName;
                    candidate.Context ??= mention.Context;
                    candidate.Where ??= "this_note";
                }
                foreach (var mention in NoteRelationsService.FindUnlinkedMentions(notes, target.Note, MaxLinkSuggestions))
                {
                    var candidate = For(all.First(n => VaultService.PathsEqual(n.FullPath, mention.FullPath)));
                    candidate.Reasons.Add("mentions_this_note");
                    candidate.LinkToThis = mention.LinkName;
                    candidate.Context ??= mention.Context;
                    candidate.Where ??= "other_note";
                }
                foreach (var related in NoteRelationsService.FindRelated(notes, target.Note, MaxLinkSuggestions))
                {
                    var candidate = For(all.First(n => VaultService.PathsEqual(n.FullPath, related.FullPath)));
                    if (related.SharedTags.Count > 0) candidate.Reasons.Add("shared_tags");
                    if (related.SharedLinks.Count > 0) candidate.Reasons.Add("shared_links");
                    candidate.SharedTags = related.SharedTags;
                    candidate.SharedLinks = related.SharedLinks;
                    candidate.LinkName ??= related.LinkName;
                }
                var semantic = _semantic is { IsReady: true };
                if (semantic)
                {
                    foreach (var hit in _semantic!.SimilarTo(path, MaxLinkSuggestions, p => WorkspaceOf(scope, p) is not null
                                 && !linked.Contains(p)))
                    {
                        if (hit.Score < MinSemanticRelatedScore) break;
                        if (all.FirstOrDefault(n => VaultService.PathsEqual(n.FullPath, hit.FullPath)) is not { } note) continue;
                        var candidate = For(note);
                        candidate.Reasons.Add("similar_meaning");
                        candidate.Similarity = Math.Round(hit.Score, 3);
                        candidate.Section ??= hit.Heading.Length > 0 ? hit.Heading : null;
                    }
                }

                var ranked = candidates.Values
                    .Where(c => !linked.Contains(c.Note.FullPath))
                    .OrderByDescending(c => c.Weight)
                    .ThenByDescending(c => c.Note.ModifiedUtc)
                    .Take(limit)
                    .ToList();
                return new
                {
                    Note = NoteLocation(scope, target.Note),
                    SemanticSearch = semantic ? "on" : "off",
                    Suggestions = ranked.Select(c => new
                    {
                        Location = NoteLocation(scope, c.Note),
                        // Cómo escribir el enlace a esa nota desde esta (null si su nombre es ambiguo en el vault).
                        Link = c.LinkName is { } name ? $"[[{name}]]" : null,
                        // En la otra nota, cómo enlazar a esta.
                        LinkToThisNote = c.LinkToThis is { } back ? $"[[{back}]]" : null,
                        Where = c.Where ?? "either",
                        c.Reasons,
                        SharedTags = c.SharedTags.Count > 0 ? c.SharedTags : null,
                        SharedLinks = c.SharedLinks.Count > 0 ? c.SharedLinks : null,
                        c.Similarity,
                        c.Section,
                        c.Context
                    })
                };
            });
    }

    /// <summary>Notas ya enlazadas con la nota en cualquier sentido: no hay nada que sugerir sobre ellas.</summary>
    private static HashSet<string> LinkedPaths(List<(NoteFile Note, string Content)> notes, NoteFile target, string content)
    {
        var all = notes.Select(n => n.Note).ToList();
        var linked = new HashSet<string>(PathComparer) { target.FullPath };
        foreach (var link in WikiLinkParser.ExtractTargets(content))
            if (WikiLinkParser.ResolveTarget(all, link) is { } resolved) linked.Add(resolved.FullPath);
        foreach (var (note, noteContent) in notes)
            if (WikiLinkParser.ExtractTargets(noteContent).Any(t => WikiLinkParser.ResolveTarget(all, t) is { } r
                    && VaultService.PathsEqual(r.FullPath, target.FullPath)))
                linked.Add(note.FullPath);
        return linked;
    }

    private sealed class LinkCandidate(NoteFile note)
    {
        public NoteFile Note { get; } = note;
        public List<string> Reasons { get; } = [];
        public string? LinkName { get; set; }
        public string? LinkToThis { get; set; }
        public string? Context { get; set; }
        public string? Where { get; set; }
        public string? Section { get; set; }
        public double? Similarity { get; set; }
        public IReadOnlyList<string> SharedTags { get; set; } = [];
        public IReadOnlyList<string> SharedLinks { get; set; } = [];

        /// <summary>Una mención explícita pesa más que un parecido: es casi seguro que el enlace tiene sentido.</summary>
        public double Weight => Reasons.Sum(r => r switch
        {
            "mentioned_in_this_note" => 4,
            "mentions_this_note" => 3,
            "shared_links" => 2,
            "shared_tags" => 1,
            _ => 0
        }) + (Similarity ?? 0) * 2;
    }
}
