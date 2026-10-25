using System;
using System.IO;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public sealed class ClaudeCliLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void ConfiguredPath_WinsWhenItExists()
    {
        var configured = Touch("custom", "claude");
        Touch("bin", "claude");

        Assert.Equal(configured, ClaudeCliLocator.Resolve($"  \"{configured}\" ", Path.Combine(_root, "bin"), false, "", "", ""));
    }

    [Fact]
    public void MissingConfiguredPath_DoesNotFallBackToAnotherBinary()
    {
        Touch("bin", "claude");

        Assert.Null(ClaudeCliLocator.Resolve(Path.Combine(_root, "nope", "claude"), Path.Combine(_root, "bin"), false, "", "", ""));
    }

    [Fact]
    public void FindsBinaryInPath_InOrder()
    {
        var first = Touch("a", "claude");
        Touch("b", "claude");
        var path = string.Join(':', Path.Combine(_root, "vacio"), Path.Combine(_root, "a"), Path.Combine(_root, "b"));

        Assert.Equal(first, ClaudeCliLocator.Resolve(null, path, false, "", "", ""));
    }

    [Fact]
    public void FallsBackToNativeInstallerFolder_WhenPathDoesNotHaveIt()
    {
        var home = Path.Combine(_root, "home");
        var installed = Touch("home", ".local", "bin", "claude");

        Assert.Equal(installed, ClaudeCliLocator.Resolve(null, "", false, home, "", ""));
    }

    [Fact]
    public void Windows_PrefersExeOverCmdWrapper()
    {
        var exe = Touch("w", "claude.exe");
        Touch("w", "claude.cmd");

        Assert.Equal(exe, ClaudeCliLocator.Resolve(null, Path.Combine(_root, "w"), true, "", "", ""));
    }

    [Theory]
    [InlineData("C:/x/claude.cmd", true)]
    [InlineData("C:/x/CLAUDE.BAT", true)]
    [InlineData("C:/x/claude.exe", false)]
    [InlineData("/usr/local/bin/claude", false)]
    public void IsBatchWrapper(string path, bool expected) => Assert.Equal(expected, ClaudeCliLocator.IsBatchWrapper(path));
}
