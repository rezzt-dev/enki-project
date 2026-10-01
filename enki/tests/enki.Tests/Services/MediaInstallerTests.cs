using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Ejecuta el instalador real con gestores simulados: nunca instala paquetes ni solicita sudo.</summary>
public sealed class MediaInstallerTests
{
    private static readonly string Script = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "packaging", "linux", "install-media-runtime.sh"));

    [Theory]
    [InlineData("ubuntu", "debian", "apt-get install -y --no-install-recommends libvlc5 vlc-plugin-base vlc-plugin-video-output")]
    [InlineData("XeroLinux", "arch", "pacman -S --needed --noconfirm libvlc vlc-plugins-all")]
    [InlineData("derivative", "arch linux", "pacman -S --needed --noconfirm libvlc vlc-plugins-all")]
    [InlineData("fedora", "", "dnf install -y vlc")]
    [InlineData("opensuse-tumbleweed", "suse", "zypper --non-interactive install --no-recommends vlc")]
    public async Task Installer_SelectsAndInvokesTheSystemPackageManager(string id, string like, string command)
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await RunInstaller(id, like, fail: false);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(command, result.Output);
        if (id == "ubuntu") Assert.True(result.Output.IndexOf("apt-get update", StringComparison.Ordinal)
            < result.Output.IndexOf("apt-get install", StringComparison.Ordinal));
        Assert.DoesNotContain("-Sy", result.Output);
    }

    [Fact]
    public async Task Installer_PropagatesPackageFailureInsteadOfReportingSuccess()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await RunInstaller("arch", "", fail: true);
        Assert.Equal(23, result.ExitCode);
    }

    [Fact]
    public async Task Installer_RejectsUnknownDistributionWithoutRunningCommands()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await RunInstaller("unsupported", "", fail: false);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Output);
    }

    private static async Task<(int ExitCode, string Output)> RunInstaller(string id, string like, bool fail)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var directory = Path.Combine(Path.GetTempPath(), "enki-installer-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var executable in new[] { "sudo", "apt-get", "pacman", "dnf", "zypper" })
            {
                var path = Path.Combine(directory, executable);
                await File.WriteAllTextAsync(path, executable == "sudo"
                    ? "#!/bin/bash\nexec \"$@\"\n"
                    : $"#!/bin/bash\nprintf '%s %s\\n' '{executable}' \"$*\"\nexit {(fail ? 23 : 0)}\n");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var start = new ProcessStartInfo("bash")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("source \"$1\"; select_media_packages \"$2\" \"$3\"; install_selected_media_runtime");
            start.ArgumentList.Add("enki-installer-test");
            start.ArgumentList.Add(Script);
            start.ArgumentList.Add(id);
            start.ArgumentList.Add(like);
            start.Environment["PATH"] = directory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await output + await error);
        }
        finally { Directory.Delete(directory, true); }
    }
}
