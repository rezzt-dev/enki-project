using System;
using System.IO;
using System.IO.Compression;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public class ContainerPreviewServiceTests
{
    private static byte[] Archive(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        return buffer.ToArray();
    }

    [Fact]
    public void Zip_ListsPathsWithoutExtractingThem()
    {
        var text = ContainerPreviewService.Read(Archive(("../escape.txt", "hello")), "example.zip", out var truncated);
        Assert.Contains("../escape.txt", text);
        Assert.False(truncated);
    }

    [Fact]
    public void Word_PreservesParagraphsAndRuns()
    {
        var bytes = Archive(("word/document.xml", "<document><p><r><t>Hola </t></r><r><t>mundo</t></r></p><p><r><t>Dos</t></r></p></document>"));
        var text = ContainerPreviewService.Read(bytes, "example.docx", out var truncated);
        Assert.Contains("Hola mundo" + Environment.NewLine + "Dos", text);
        Assert.False(truncated);
    }

    [Fact]
    public void Xml_RejectsExternalEntities()
    {
        var bytes = Archive(("content.xml", "<!DOCTYPE doc [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><doc><p>&x;</p></doc>"));
        Assert.Throws<System.Xml.XmlException>(() => ContainerPreviewService.Read(bytes, "example.odt", out _));
    }

    [Fact]
    public void Spreadsheet_ResolvesSharedAndInlineStringsAndKeepsCellReferences()
    {
        var bytes = Archive(("xl/sharedStrings.xml", "<sst><si><t>Nombre</t></si></sst>"),
            ("xl/worksheets/sheet1.xml", "<worksheet><row><c r='A1' t='s'><v>0</v></c><c r='C1' t='inlineStr'><is><t>Ana</t></is></c><c r='D1'><v>42</v></c></row></worksheet>"));
        var text = ContainerPreviewService.Read(bytes, "example.xlsx", out _);
        Assert.Contains("A1: Nombre", text);
        Assert.Contains("C1: Ana", text);
        Assert.Contains("D1: 42", text);
    }

    [Fact]
    public void Archive_LimitsEntryCount()
    {
        var entries = new (string, string)[2001];
        for (var i = 0; i < entries.Length; i++) entries[i] = ($"{i}.txt", "");
        ContainerPreviewService.Read(Archive(entries), "many.zip", out var truncated);
        Assert.True(truncated);
    }

    [Fact]
    public void CorruptContainer_IsRejected() =>
        Assert.Throws<InvalidDataException>(() => ContainerPreviewService.Read([1, 2, 3], "broken.docx", out _));
}
