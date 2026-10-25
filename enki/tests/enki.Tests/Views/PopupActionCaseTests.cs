using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace enki.Tests.Views;

/// <summary>Las acciones de menús y botones mantienen la misma caja tipográfica en todas las vistas.</summary>
public class PopupActionCaseTests
{
    private static readonly string AppDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "enki"));

    [Fact]
    public void MenuActions_UseUpperCaseConverter()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppDirectory, "views"), "*.axaml", SearchOption.AllDirectories)
            .Append(Path.Combine(AppDirectory, "App.axaml"));
        var missing = new List<string>();

        foreach (var file in files)
        {
            var document = XDocument.Load(file);
            foreach (var item in document.Descendants().Where(element =>
                         element.Name.LocalName is "MenuItem" or "NativeMenuItem"))
            {
                var header = item.Attribute("Header")?.Value;
                if (header is not null && !header.Contains("UpperCaseConverter", StringComparison.Ordinal))
                    missing.Add($"{Path.GetFileName(file)}: {header}");
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void LocalizedButtonActions_UseUpperCaseConverter()
    {
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppDirectory, "views"), "*.axaml", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(file);
            foreach (var button in document.Descendants().Where(element => element.Name.LocalName == "Button"))
            {
                var content = button.Attribute("Content")?.Value;
                if (content?.StartsWith("{loc:Tr ", StringComparison.Ordinal) == true &&
                    !content.Contains("UpperCaseConverter", StringComparison.Ordinal))
                    missing.Add($"{Path.GetFileName(file)}: {content}");
            }
        }

        Assert.Empty(missing);
    }
}
