using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public sealed class CodexCliServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _bin = Path.Combine(Path.GetTempPath(), "enki-fake-codex-" + Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (Directory.Exists(_bin)) Directory.Delete(_bin, true);
    }

    [Fact]
    public void Parser_IgnoresReasoningAndTools_AndKeepsMessagesInOrder()
    {
        var deltas = new List<string>();
        var parser = new CodexTurnAccumulator(e => { if (e is ClaudeTextDelta d) deltas.Add(d.Text); });
        parser.OnLine("not json");
        parser.OnLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"reasoning\",\"text\":\"private\"}}");
        parser.OnLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Uno\"}}");
        parser.OnLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"mcp_tool_call\",\"text\":\"tool output\"}}");
        parser.OnLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Dos\"}}");
        parser.OnLine("{\"type\":\"turn.completed\"}");
        var result = parser.Complete(new(0, "", false));
        Assert.True(result.Succeeded);
        Assert.Equal("Uno\n\nDos", result.Text);
        Assert.Equal(result.Text, string.Concat(deltas));
    }

    [Theory]
    [InlineData("usage limit reached", ClaudeFailureKind.RateLimited)]
    [InlineData("401 Unauthorized", ClaudeFailureKind.SignedOut)]
    [InlineData("model not supported", ClaudeFailureKind.ModelUnavailable)]
    [InlineData("connection failed", ClaudeFailureKind.Failed)]
    public void Parser_ClassifiesErrors(string error, ClaudeFailureKind failure)
    {
        var parser = new CodexTurnAccumulator(null);
        parser.OnLine(JsonSerializer.Serialize(new { type = "turn.failed", error = new { message = error } }));
        Assert.Equal(failure, parser.Complete(new(1, "", false)).Failure);
    }

    [Fact]
    public void Parser_RequiresCompletedEvent_AndHonorsTimeout()
    {
        var parser = new CodexTurnAccumulator(null);
        Assert.False(parser.Complete(new(0, "", false)).Succeeded);
        Assert.Equal(ClaudeFailureKind.Timeout, parser.Complete(new(-1, "", true)).Failure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OfficialCliFlow_DetectsAccount_AndSendsPromptsThroughStdin(bool loggedIn)
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_bin);
        var executable = Path.Combine(_bin, "codex");
        await File.WriteAllTextAsync(executable, $$$"""
            #!/bin/sh
            case "$1" in
              --version) echo 'codex-cli 1.0.0';;
              login) echo 'Logged in using ChatGPT' >&2; exit {{{(loggedIn ? 0 : 1)}}};;
              exec)
                if [ "$2" = '--help' ]; then
                  echo '--ignore-user-config --ignore-rules --ephemeral --json --sandbox --strict-config'
                else
                  cat > '{{{_bin}}}/stdin.txt'
                  printf '%s\n' "$@" > '{{{_bin}}}/args.txt'
                  printf '%s' "$ENKI_MCP_TOKEN" > '{{{_bin}}}/token.txt'
                  echo '{"type":"item.completed","item":{"type":"agent_message","text":"Hola OpenAI"}}'
                  echo '{"type":"turn.completed"}'
                fi;;
            esac
            """ + "\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var settings = new SettingsService(_root);
        settings.Settings.Ai.CodexCliPath = executable;
        var service = new CodexCliService(settings, _root);
        var status = await service.DetectAsync();
        Assert.Equal(loggedIn ? ClaudeCliState.Ready : ClaudeCliState.SignedOut, status.State);
        if (!loggedIn) return;
        var result = await service.RunTurnAsync(new("Mensaje privado", "Sistema privado", null, null,
            new("http://127.0.0.1:12345/mcp", "secret-turn-token")), null, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("Hola OpenAI", result.Text);
        var args = await File.ReadAllTextAsync(Path.Combine(_bin, "args.txt"));
        Assert.Contains("--ignore-user-config", args);
        Assert.Contains("features.shell_tool=false", args);
        Assert.Contains("features.view_image=false", args);
        Assert.Contains("--strict-config", args);
        Assert.Contains("mcp_servers.enki.default_tools_approval_mode=\"approve\"", args);
        Assert.Contains("read-only", args);
        Assert.DoesNotContain("privado", args);
        Assert.DoesNotContain("secret-turn-token", args);
        Assert.Equal("secret-turn-token", await File.ReadAllTextAsync(Path.Combine(_bin, "token.txt")));
        Assert.Contains("Mensaje privado", await File.ReadAllTextAsync(Path.Combine(_bin, "stdin.txt")));
    }

    [Fact]
    public async Task OldCliWithoutIsolation_IsUnsupported()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_bin);
        var executable = Path.Combine(_bin, "codex");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\necho 'codex-cli old'\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var settings = new SettingsService(_root);
        settings.Settings.Ai.CodexCliPath = executable;
        Assert.Equal(ClaudeCliState.Unsupported, (await new CodexCliService(settings, _root).DetectAsync()).State);
    }
}
