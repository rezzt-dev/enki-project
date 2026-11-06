using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Contra scripts de <c>/bin/sh</c> triviales: los tests de esta clase no se ejecutan en Windows
/// (salen sin hacer nada), donde el mismo código se ejercita con el CLI real en la QA manual.</summary>
public sealed class CliProcessRunnerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public CliProcessRunnerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string Script(string body)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task Run_EchoesStdinLineByLine()
    {
        if (OperatingSystem.IsWindows()) return;
        var lines = new List<string>();

        var result = await CliProcessRunner.RunAsync(new CliProcessStartInfo
        {
            FileName = Script("cat"),
            StandardInput = "uno\ndos ñ 😀\n"
        }, lines.Add, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(["uno", "dos ñ 😀"], lines);
    }

    [Fact]
    public async Task Run_PassesArgumentsVerbatim_WithoutShellInterpretation()
    {
        if (OperatingSystem.IsWindows()) return;
        var lines = new List<string>();
        var dangerous = "\"; touch " + Path.Combine(_directory, "pwned") + "; echo \"$(whoami)\nsegunda";

        await CliProcessRunner.RunAsync(new CliProcessStartInfo
        {
            FileName = Script("printf '%s\\n' \"$1\"; printf '[%s]\\n' \"$2\""),
            Arguments = [dangerous, ""]
        }, lines.Add, CancellationToken.None);

        Assert.Equal(dangerous.Split('\n')[0], lines[0]);
        Assert.Equal("segunda", lines[1]);
        Assert.Equal("[]", lines[2]);
        Assert.False(File.Exists(Path.Combine(_directory, "pwned")));
    }

    [Fact]
    public async Task Run_ReportsExitCodeAndStandardError()
    {
        if (OperatingSystem.IsWindows()) return;

        var result = await CliProcessRunner.RunAsync(new CliProcessStartInfo
        {
            FileName = Script("echo 'algo falló' >&2; exit 3")
        }, null, CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("algo falló", result.StandardError);
    }

    [Fact]
    public async Task Run_KillsProcessThatGoesIdle()
    {
        if (OperatingSystem.IsWindows()) return;
        var watch = Stopwatch.StartNew();

        var result = await CliProcessRunner.RunAsync(new CliProcessStartInfo
        {
            FileName = Script("echo inicio; sleep 30; echo nunca"),
            IdleTimeout = TimeSpan.FromMilliseconds(400)
        }, null, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Run_IdleTimeoutRestartsWithEachLine()
    {
        if (OperatingSystem.IsWindows()) return;
        var lines = new List<string>();

        var result = await CliProcessRunner.RunAsync(new CliProcessStartInfo
        {
            FileName = Script("for i in 1 2 3 4; do echo $i; sleep 0.2; done"),
            IdleTimeout = TimeSpan.FromMilliseconds(600)
        }, lines.Add, CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(["1", "2", "3", "4"], lines);
    }

    [Fact]
    public async Task Run_CancellationKillsAndThrows()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CliProcessRunner.RunAsync(new CliProcessStartInfo
        {
            FileName = Script("sleep 30")
        }, null, cancellation.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Run_RemovesExcludedEnvironmentVariables()
    {
        if (OperatingSystem.IsWindows()) return;
        Environment.SetEnvironmentVariable("ENKI_TEST_SECRET_VAR", "secreto");
        try
        {
            var lines = new List<string>();
            await CliProcessRunner.RunAsync(new CliProcessStartInfo
            {
                FileName = Script("echo \"[$ENKI_TEST_SECRET_VAR]\""),
                ExcludeEnvironmentVariable = name => name == "ENKI_TEST_SECRET_VAR"
            }, lines.Add, CancellationToken.None);

            Assert.Equal(["[]"], lines);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ENKI_TEST_SECRET_VAR", null);
        }
    }

    [Fact]
    public async Task Run_MissingExecutable_Throws()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => CliProcessRunner.RunAsync(new CliProcessStartInfo
        {
            FileName = Path.Combine(_directory, "no-existe")
        }, null, CancellationToken.None));
    }
}
