using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Comprueba el generador real de configuración sin compilar la aplicación ni usar
/// las credenciales del desarrollador. Requiere el SDK que ejecuta la suite.</summary>
public sealed class PolarBuildConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-polar-build-" + Guid.NewGuid().ToString("N"));

    public PolarBuildConfigurationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Publish_PublicConfiguration_ExportsOnlyAllowedValues()
    {
        var (exitCode, content) = await GenerateAsync(
            "SUPABASE_URL=https://example.supabase.co/\nSUPABASE_ANON_KEY=sb_publishable_test\nOTHER_SECRET=private-value\n", true);

        Assert.Equal(0, exitCode);
        Assert.Equal("SUPABASE_URL=https://example.supabase.co\nSUPABASE_ANON_KEY=sb_publishable_test\n", content);
    }

    [Theory]
    [InlineData("", false, true)]
    [InlineData("", true, false)]
    [InlineData("SUPABASE_URL=http://example.supabase.co\nSUPABASE_ANON_KEY=sb_publishable_test", true, false)]
    [InlineData("SUPABASE_URL=https://example.supabase.co\nSUPABASE_ANON_KEY=sb_secret_test", true, false)]
    [InlineData("SUPABASE_URL=https://example.supabase.co\nSUPABASE_PUBLISHABLE_KEY=sb_publishable_test", true, true)]
    public async Task Generate_ConfigurationRules_AreEnforced(string source, bool publish, bool succeeds)
    {
        var (exitCode, content) = await GenerateAsync(source, publish);

        Assert.Equal(succeeds, exitCode == 0);
        if (!succeeds) Assert.DoesNotContain("SUPABASE_ANON_KEY=", content);
    }

    [Fact]
    public async Task Publish_PartialEnvironment_DoesNotMixWithLocalBackendOrKeepStaleOutput()
    {
        const string source = "SUPABASE_URL=https://example.supabase.co\nSUPABASE_ANON_KEY=sb_publishable_test";
        Assert.Equal(0, (await GenerateAsync(source, true)).ExitCode);

        var (exitCode, content) = await GenerateAsync(source, true, "https://other.supabase.co");

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("SUPABASE_ANON_KEY=", content);
    }

    private async Task<(int ExitCode, string Content)> GenerateAsync(string source, bool publish, string? environmentUrl = null)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "enki", "build", "PolarConfiguration.targets")))
            root = root.Parent;
        Assert.NotNull(root);

        var sourcePath = Path.Combine(_directory, "local.properties");
        var outputDirectory = Path.Combine(_directory, "obj") + Path.DirectorySeparatorChar;
        var projectPath = Path.Combine(_directory, "test.proj");
        File.WriteAllText(sourcePath, source);
        new XDocument(new XElement("Project",
            new XElement("PropertyGroup",
                new XElement("IntermediateOutputPath", outputDirectory),
                new XElement("PolarConfigurationSource", sourcePath)),
            new XElement("Import", new XAttribute("Project", Path.Combine(root.FullName, "enki", "build", "PolarConfiguration.targets")))))
            .Save(projectPath);

        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(projectPath);
        start.ArgumentList.Add("-nologo");
        start.ArgumentList.Add("-v:quiet");
        start.ArgumentList.Add("-t:" + (publish ? "ValidatePolarPublishConfiguration" : "GeneratePolarConfigurationFile"));
        start.Environment.Remove("SUPABASE_URL");
        start.Environment.Remove("SUPABASE_ANON_KEY");
        start.Environment.Remove("SUPABASE_PUBLISHABLE_KEY");
        if (environmentUrl != null) start.Environment["SUPABASE_URL"] = environmentUrl;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        var outputPath = Path.Combine(outputDirectory, "polar.properties");
        Assert.True(File.Exists(outputPath), await stdout + await stderr);
        return (process.ExitCode, File.ReadAllText(outputPath));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
