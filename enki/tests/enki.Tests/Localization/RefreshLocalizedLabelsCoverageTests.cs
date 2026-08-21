using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace enki.Tests.Localization;

/// <summary>Guards gap 2 (docs/agent-docs/inventario-funcionalidades-y-mejora-continua.md §10.1):
/// a viewmodel can declare <c>RefreshLocalizedLabels()</c> to re-evaluate its cached, computed
/// localized strings on a hot language switch, but nothing forces that method to actually be wired
/// into the chain rooted at <c>MainWindowViewModel.RefreshAllLocalizedLabels</c> — a method can sit
/// there, fully unused, and its labels quietly go stale after <c>LocalizationService.SetLanguage</c>
/// runs. This happened for real with <c>EditorStatsViewModel</c> and <c>FindReplaceViewModel</c>
/// before this test existed. Since there's no DI container or call graph to walk here (MainWindowViewModel
/// composes everything by hand — see docs/agent-docs/inventario-funcionalidades-y-mejora-continua.md
/// §2.1), this test uses a simple, deliberately coarse heuristic instead of true call-graph analysis:
/// every file that *declares* a <c>RefreshLocalizedLabels()</c> method must be matched by at least one
/// *call site* (<c>something.RefreshLocalizedLabels()</c>/<c>something?.RefreshLocalizedLabels()</c>)
/// somewhere else in the source tree. It won't catch a call site wired to the wrong instance, but it
/// does catch the exact failure mode above: a method that nobody calls at all.</summary>
public class RefreshLocalizedLabelsCoverageTests
{
    private static readonly string s_sourceDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "enki"));

    private const string MethodName = "RefreshLocalizedLabels";

    [Fact]
    public void EveryDeclaration_HasAtLeastOneCallSite()
    {
        var declarationPattern = new Regex($@"\b(?:public|internal|private)\s+void\s+{MethodName}\s*\(");
        var callPattern = new Regex($@"(?<!void\s){MethodName}\s*\(\s*\)");

        var declaringFiles = new List<string>();
        var totalCallSites = 0;

        foreach (var file in Directory.EnumerateFiles(s_sourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (declarationPattern.IsMatch(text))
                declaringFiles.Add(Path.GetRelativePath(s_sourceDirectory, file));

            totalCallSites += callPattern.Matches(text).Count(m => !IsDeclarationMatch(text, m.Index));
        }

        Assert.True(totalCallSites >= declaringFiles.Count,
            $"Found {declaringFiles.Count} RefreshLocalizedLabels() declaration(s) but only " +
            $"{totalCallSites} call site(s) across enki/src/enki. Every viewmodel that declares this " +
            "method must be reachable from MainWindowViewModel.RefreshAllLocalizedLabels (directly or " +
            "through a parent viewmodel's own RefreshLocalizedLabels), otherwise its cached labels go " +
            $"stale on a language switch. Files declaring the method: {string.Join(", ", declaringFiles)}");
    }

    /// <summary>The call-site regex alone can't tell "void RefreshLocalizedLabels()" (declaration) apart
    /// from "someMethodNamedVoid.RefreshLocalizedLabels()" (call, never happens in practice but keep the
    /// heuristic honest) — re-checks the immediate context to skip matches that are actually the method
    /// signature itself, since the negative look-behind only skips the exact "void " prefix.</summary>
    private static bool IsDeclarationMatch(string text, int matchIndex)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, matchIndex - 1)) + 1;
        var line = text[lineStart..matchIndex];
        return Regex.IsMatch(line, @"\b(?:public|internal|private)\s+void\s*$");
    }
}
