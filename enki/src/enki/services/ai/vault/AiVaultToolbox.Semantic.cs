using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using enki.Models;

namespace enki.Services;

/// <summary>Búsqueda por significado (AIA-16) sobre el índice local de <see cref="AiSemanticIndexService"/>. Solo se
/// ofrece con la búsqueda semántica encendida en Ajustes. El índice cubre el vault y las sesiones sin contraseña; el
/// ámbito del chat se aplica aquí, a cada resultado, igual que en el resto de lecturas.</summary>
public sealed partial class AiVaultToolbox
{
    private const int DefaultSemanticResults = 8;
    private const int MaxSemanticResults = 25;
    private const int MaxSemanticSnippet = 400;

    /// <summary>Lo que se espera a que el índice se ponga al día antes de buscar. Si tarda más, se busca con lo que
    /// ya hay y la actualización sigue en segundo plano.</summary>
    private static readonly TimeSpan SemanticRefreshWait = TimeSpan.FromSeconds(3);

    private void RegisterSemanticSearchTools()
    {
        RegisterAsync("semantic_search", AiToolScope.Content, AiToolEffect.Read,
            "Finds notes by meaning rather than exact words, with a local embeddings index: synonyms, paraphrases, related concepts "
            + "or questions (\"where did I write about burnout?\"). Returns the most similar notes with the matching section, a snippet "
            + "and a similarity score from 0 to 1; below about 0.5 results are usually unrelated. Complements search_notes, which "
            + "matches exact words.",
            Schema(Req("query", Text("What to look for, in natural language.")),
                WorkspaceParam("Limit the search to this workspace (omit to search all of them)."),
                Opt("limit", Number($"Maximum results (default {DefaultSemanticResults}, max {MaxSemanticResults})."))),
            async call =>
            {
                var semantic = _semantic!;
                var query = call.RequireText("query").Trim();
                call.Target(AiToolTargetKind.None, query);
                var limit = call.GetInteger("limit", 1, MaxSemanticResults) ?? DefaultSemanticResults;
                var scope = call.Context.Scope;
                var only = call.Has("workspace") ? Workspace(call) : null;

                // Antes de buscar se recogen los cambios recientes, sin hacer esperar al usuario más de unos segundos.
                await Task.WhenAny(semantic.TryRefreshAsync(call.Context.Cancellation), Task.Delay(SemanticRefreshWait, call.Context.Cancellation));
                call.Context.Cancellation.ThrowIfCancellationRequested();
                if (!semantic.IsReady) throw SemanticUnavailable(semantic.Status);

                System.Collections.Generic.IReadOnlyList<AiSemanticHit> hits;
                try
                {
                    hits = await semantic.SearchAsync(query, limit, path => InScope(scope, only, path), call.Context.Cancellation);
                }
                catch (HttpRequestException)
                {
                    throw SemanticUnavailable(semantic.Status with { Problem = AiSemanticProblem.OllamaUnavailable });
                }
                var status = semantic.Status;
                return new
                {
                    Index = new { status.Notes, Updating = status.State == AiSemanticIndexState.Indexing ? status.Pending : (int?)null },
                    Results = hits.Select(hit =>
                    {
                        var workspace = WorkspaceOf(scope, hit.FullPath)!;
                        return new
                        {
                            Workspace = WorkspaceKey(workspace),
                            Path = RelativePath(workspace, hit.FullPath),
                            hit.Title,
                            Section = hit.Heading.Length > 0 ? hit.Heading : null,
                            Score = Math.Round(Math.Max(0, hit.Score), 3),
                            Snippet = SemanticSnippet(hit)
                        };
                    }).ToList()
                };
            },
            condition: () => _semantic?.IsEnabled == true);
    }

    /// <summary>La ruta está en el ámbito del chat (y, si se pidió, en ese espacio).</summary>
    private bool InScope(AiAccessScope scope, AiWorkspace? only, string fullPath) =>
        WorkspaceOf(scope, fullPath) is { } workspace && (only is null || workspace.Id == only.Id);

    private static AiToolException SemanticUnavailable(AiSemanticIndexStatus status) => new(AiToolErrorKind.Unavailable,
        status switch
        {
            { State: AiSemanticIndexState.Indexing } => $"The semantic index is still being built ({status.Pending} notes left). "
                + "Use search_notes for now, or try again in a moment.",
            { Problem: AiSemanticProblem.OllamaUnavailable or AiSemanticProblem.InvalidEndpoint } =>
                "Semantic search needs Ollama running on this computer, and it is not responding. Use search_notes instead, "
                + "and tell the user they can start Ollama or turn semantic search off in Settings.",
            { Problem: AiSemanticProblem.ModelMissing } =>
                "The embedding model for semantic search is not installed in Ollama. Use search_notes instead, and tell the user "
                + "to install it (Settings > Artificial intelligence > Semantic search shows the command).",
            { Problem: not null } => "Semantic search is not working with the configured model. Use search_notes instead.",
            _ => "The semantic index is empty. Use search_notes for now; the index builds in the background."
        });

    /// <summary>El fragmento encontrado, leído ahora de la nota (el índice no guarda texto).</summary>
    private static string? SemanticSnippet(AiSemanticHit hit)
    {
        try
        {
            var content = File.ReadAllText(hit.FullPath);
            if (hit.Start < 0 || hit.Start >= content.Length) return null;
            var text = content.Substring(hit.Start, Math.Min(hit.Length, content.Length - hit.Start)).Trim();
            return Truncate(text, MaxSemanticSnippet);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
