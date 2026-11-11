using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public sealed class AiEvaluationServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RealTools_UseDisposableFixtures_ScoreOutcomesAndStoreOnlyLocalMetrics()
    {
        var service = new AiEvaluationService(_directory);
        async Task<ClaudeTurnResult> Run(ClaudeTurnRequest request, CancellationToken cancellation)
        {
            var tools = await EnkiMcpClient.ListToolsAsync(EnkiMcpClient.LoopbackHttp, request.Tools!, cancellation);
            Assert.DoesNotContain(tools, t => t?["name"]?.GetValue<string>() == "change_safe_setting");
            if (request.Prompt.StartsWith("Edit", StringComparison.Ordinal))
            {
                var read = await EnkiMcpClient.CallToolAsync(EnkiMcpClient.LoopbackHttp, request.Tools!, "read_note",
                    new JsonObject { ["path"] = "Evaluation.md" }, cancellation);
                Assert.False(read.IsError, read.Text);
                var edit = await EnkiMcpClient.CallToolAsync(EnkiMcpClient.LoopbackHttp, request.Tools!, "edit_note",
                    new JsonObject { ["path"] = "Evaluation.md", ["edits"] = new JsonArray(new JsonObject
                        { ["find"] = "Status: pending", ["replace"] = "Status: complete" }) }, cancellation);
                Assert.False(edit.IsError, edit.Text);
            }
            else
            {
                var card = await EnkiMcpClient.CallToolAsync(EnkiMcpClient.LoopbackHttp, request.Tools!, "create_kanban_card",
                    new JsonObject { ["title"] = "Evaluation card", ["priority"] = "high", ["description"] = "Synthetic task" }, cancellation);
                Assert.False(card.IsError, card.Text);
            }
            return new ClaudeTurnResult("Do not store this response", null, ClaudeFailureKind.None);
        }
        var result = await service.RunAsync(AiProvider.Ollama, "model-test", Run, work => work());
        Assert.Equal(100, result.Score);
        Assert.Equal(2, result.Tasks.Count);
        Assert.All(result.Tasks, t => Assert.True(t.SuccessfulCalls > 0));
        Assert.Single(service.Load());
        var json = File.ReadAllText(Directory.GetFiles(_directory, "*.json", SearchOption.AllDirectories).Single());
        Assert.DoesNotContain("Do not store", json);
        Assert.DoesNotContain("Status: pending", json);
        service.Clear();
        Assert.Empty(service.Load());
    }

    [Fact]
    public async Task TextOnlyResponse_ScoresZero_AndCancellationDoesNotPersistPartialResults()
    {
        var service = new AiEvaluationService(_directory);
        var result = await service.RunAsync(AiProvider.Claude, "", (_, _) =>
            Task.FromResult(new ClaudeTurnResult("Done", null, ClaudeFailureKind.None)), work => work());
        Assert.Equal(0, result.Score);
        service.Clear();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(AiProvider.Claude, "",
            (_, _) => { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); throw new InvalidOperationException(); },
            work => work(), cancellation: cancellation.Token));
        Assert.Empty(service.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
