using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Would have caught the "/tabla" bug before it reached production
/// (docs/version-alfa-3/01-analisis-detallado.md §4.2): <c>SlashCommandsViewModel.AllCommands</c>
/// referenced <c>Editor_SlashCommand_Table_InsertText</c> as an <see cref="Models.SlashCommand.InsertTextKey"/>,
/// but that key didn't exist in any <c>.resx</c> — <see cref="Models.SlashCommand.InsertText"/> silently
/// resolved to a resource-key-shaped fallback string (bracketed in Debug, the bare key in Release) instead
/// of real Markdown, and that fallback got inserted verbatim into the user's note. Iterates
/// <c>AllCommands</c> directly rather than a hardcoded list of keys, so a future command with the same
/// mistake fails here instead of shipping.</summary>
public class SlashCommandsViewModelTests
{
    private static readonly string s_sourceResxPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "enki", "localization", "Strings.resx"));

    [Fact]
    public void InsertText_ResolvesToRealContent_ForEveryCommand()
    {
        var resourceKeys = LoadResourceKeys();

        foreach (var command in SlashCommandsViewModel.AllCommands)
        {
            if (command.InsertTextKey is { } key)
            {
                Assert.True(resourceKeys.Contains(key),
                    $"Comando '{command.Key}': InsertTextKey '{key}' no existe en Strings.resx.");
            }

            var resolved = command.InsertText;
            Assert.DoesNotContain("[[", resolved);
            Assert.NotEqual(command.InsertTextKey, resolved);
        }
    }

    /// <summary>A command's <see cref="Models.SlashCommand.CursorOffset"/> must never land past the end of
    /// its own resolved <see cref="Models.SlashCommand.InsertText"/> — exactly the second half of the
    /// "/tabla" bug (<c>CursorOffset = 53</c> against a resolved fallback far shorter than that), which
    /// would have thrown from <c>TextEditor.CaretOffset</c> once the missing-key half of the bug above was
    /// fixed but this one wasn't.</summary>
    [Fact]
    public void CursorOffset_NeverExceedsInsertTextLength_ForEveryCommand()
    {
        foreach (var command in SlashCommandsViewModel.AllCommands)
        {
            Assert.InRange(command.CursorOffset, 0, command.InsertText.Length);
        }
    }

    private static HashSet<string> LoadResourceKeys()
    {
        var document = XDocument.Load(s_sourceResxPath);
        return document.Root!.Elements("data")
            .Select(e => e.Attribute("name")?.Value!)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet();
    }
}
