using System;
using System.IO;

namespace enki.Services;

/// <summary>Alias de gramáticas para formatos de proyecto y nombres sin extensión.</summary>
internal static class CodePreviewService
{
    public static string LanguageExtension(string fileName)
    {
        if (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Containerfile", StringComparison.OrdinalIgnoreCase)) return ".dockerfile";
        if (fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("GNUmakefile", StringComparison.OrdinalIgnoreCase)) return ".makefile";
        if (fileName.Equals("Gemfile", StringComparison.OrdinalIgnoreCase)) return ".rb";
        if (fileName.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)) return ".cmake";
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".axaml" or ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets" or ".resx" => ".xml",
            ".mjs" or ".cjs" => ".js",
            ".mts" or ".cts" => ".ts",
            ".ipynb" => ".json",
            ".hcl" => ".tf",
            "" => ".txt",
            var extension => extension
        };
    }
}
