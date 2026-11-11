using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using enki.Models;

namespace enki.Services;

public sealed record AiEvaluationTaskResult(string Task, int Score, int SuccessfulCalls, int FailedCalls,
    ClaudeFailureKind Failure, long ElapsedMilliseconds, long InputTokens, long OutputTokens);

public sealed record AiEvaluationResult(int SuiteVersion, AiProvider Provider, string Model, DateTime CreatedUtc,
    IReadOnlyList<AiEvaluationTaskResult> Tasks)
{
    public int Score => Tasks.Count == 0 ? 0 : (int)Math.Round(Tasks.Average(t => t.Score));
}

/// <summary>Banco local reproducible. Solo se envían dos peticiones sintéticas al proveedor elegido.
/// Cada tarea dispone de vault, servicios, token y servidor desechables, sin acceso al vault del usuario.
/// Se puntúa el estado real de los datos; no se guardan prompts, respuestas ni credenciales.</summary>
public sealed class AiEvaluationService
{
    public const int SuiteVersion = 1;
    private readonly string _directory;
    private static readonly HashSet<string> AllowedTools = new(StringComparer.Ordinal)
        { "get_workspace_overview", "list_notes", "read_note", "edit_note", "write_note", "list_kanban", "create_kanban_card" };

    public AiEvaluationService(string? storageDirectory = null) =>
        _directory = Path.Combine(storageDirectory ?? AppDataDirectory.Resolve(), "ai", "evaluations");

    public IReadOnlyList<AiEvaluationResult> Load()
    {
        if (!Directory.Exists(_directory)) return [];
        return Directory.EnumerateFiles(_directory, "*.json").OrderDescending().Take(100)
            .Select(path => JsonFileStore.TryLoad<AiEvaluationResult>(path, out var result) ? result : null)
            .OfType<AiEvaluationResult>().ToList();
    }

    public void Clear()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json")) File.Delete(path);
    }

    public async Task<AiEvaluationResult> RunAsync(AiProvider provider, string model,
        Func<ClaudeTurnRequest, CancellationToken, Task<ClaudeTurnResult>> runTurn,
        Func<Func<Task>, Task> dispatch, Action<int>? progress = null, CancellationToken cancellation = default)
    {
        var results = new List<AiEvaluationTaskResult>();
        for (var index = 0; index < 2; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Invoke(index);
            results.Add(await RunTaskAsync(index, model, runTurn, dispatch, cancellation));
        }
        var report = new AiEvaluationResult(SuiteVersion, provider, model, DateTime.UtcNow, results);
        cancellation.ThrowIfCancellationRequested();
        PrivateDataPermissions.EnsurePrivateDirectory(_directory);
        JsonFileStore.Save(Path.Combine(_directory, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff",
            System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".json"), report);
        return report;
    }

    private static async Task<AiEvaluationTaskResult> RunTaskAsync(int index, string model,
        Func<ClaudeTurnRequest, CancellationToken, Task<ClaudeTurnResult>> runTurn,
        Func<Func<Task>, Task> dispatch, CancellationToken cancellation)
    {
        var directory = Path.Combine(Path.GetTempPath(), "enki-ai-evaluation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new SettingsService(directory);
            settings.Settings.Ai.AllowVaultChanges = true;
            var vault = new VaultService(settings);
            vault.InitializeVault(Path.Combine(directory, "vault"), seedWelcomeNote: false);
            var note = vault.CreateNote("Evaluation", initialContent: "# Evaluation\n\nStatus: pending\n\nKeep this paragraph.\n");
            var kanban = new KanbanService(directory);
            foreach (var column in kanban.DefaultBoard.Columns) column.Cards.Clear();
            var polar = new PolarTaskService(directory);
            var auth = new PolarAuthService(settings);
            var reminders = new ReminderService(directory);
            var toolbox = new AiVaultToolbox(vault, new SessionService(vault), kanban, polar, auth, reminders, settings);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var activities = new ConcurrentDictionary<AiToolActivity, byte>();
            using var server = new EnkiMcpServer(toolbox, dispatch);
            var access = server.Register(new AiToolContext
            {
                Scope = AiAccessScope.Vault, ReadAccess = AiReadAccess.Unrestricted,
                Cancellation = timeout.Token, AllowedTools = AllowedTools,
                ActivityReported = a => activities.TryAdd(a, 0)
            });
            var prompt = index == 0
                ? "Edit Evaluation.md: replace 'Status: pending' with 'Status: complete'. Preserve every other character. Use Enki tools."
                : "Create exactly one Kanban card titled 'Evaluation card', with high priority and description 'Synthetic task'. "
                    + "Put it in the backlog column of the default board. Use Enki tools. Do not edit notes.";
            var clock = Stopwatch.StartNew();
            ClaudeTurnResult result;
            try
            {
                result = await runTurn(new ClaudeTurnRequest(prompt,
                    "You are completing a synthetic Enki tool evaluation in a disposable vault. Execute only the requested task using Enki tools.",
                    null, model.Length > 0 ? model : null, access), timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                result = new ClaudeTurnResult(string.Empty, null, ClaudeFailureKind.Failed);
            }
            finally { server.Unregister(access.BearerToken); }
            cancellation.ThrowIfCancellationRequested();
            var cards = kanban.AllColumns.SelectMany(c => c.Cards).ToList();
            var noteText = vault.ReadContent(note);
            var successful = activities.Keys.Count(a => a.State == AiToolActivityState.Succeeded);
            var failed = activities.Keys.Count(a => a.State is AiToolActivityState.Failed or AiToolActivityState.Denied);
            var score = ScoreTask(index, noteText, cards, kanban.DefaultBoard.Columns
                .Where(c => c.Role == CoreColumnRole.Backlog).SelectMany(c => c.Cards).Select(c => c.Id).ToHashSet());
            if (!result.Succeeded) score = 0;
            return new AiEvaluationTaskResult(index == 0 ? "edit_note" : "create_kanban_card", score, successful, failed,
                result.Failure, clock.ElapsedMilliseconds, result.Usage?.InputTokens ?? 0, result.Usage?.OutputTokens ?? 0);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { LogService.Warning("No se pudo retirar el entorno sintético de evaluación", ex); }
        }
    }

    internal static int ScoreTask(int task, string note, IReadOnlyList<KanbanCard> cards, IReadOnlySet<string> backlog)
    {
        if (task == 0)
            return note == "# Evaluation\n\nStatus: complete\n\nKeep this paragraph.\n" && cards.Count == 0 ? 100 : 0;
        if (cards.Count != 1 || note != "# Evaluation\n\nStatus: pending\n\nKeep this paragraph.\n") return 0;
        var card = cards[0];
        return (card.Title == "Evaluation card" ? 25 : 0) + (card.Priority == KanbanPriority.High ? 25 : 0)
            + (card.Description == "Synthetic task" ? 25 : 0) + (backlog.Contains(card.Id) ? 25 : 0);
    }
}
