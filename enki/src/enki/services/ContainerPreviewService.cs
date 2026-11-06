using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace enki.Services;

/// <summary>Lectura acotada de contenedores ZIP y texto de documentos, sin extraer archivos ni ejecutar contenido.</summary>
internal static class ContainerPreviewService
{
    private const int MaxCharacters = 512_000;
    private const int MaxEntries = 2000;
    private const long MaxXmlBytes = 8 * 1024 * 1024;

    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".zip" or ".jar" or ".nupkg" or ".docx" or ".xlsx" or ".pptx" or ".odt" or ".odp" or ".ods";

    public static string Read(byte[] bytes, string path, out bool truncated)
    {
        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var text = new StringBuilder();
        truncated = false;
        if (extension is ".zip" or ".jar" or ".nupkg")
        {
            foreach (var entry in archive.Entries.Take(MaxEntries))
            {
                text.Append(entry.FullName).Append('\t').Append(entry.Length).AppendLine(" B");
                if (text.Length >= MaxCharacters) break;
            }
            truncated = archive.Entries.Count > MaxEntries || text.Length > MaxCharacters;
        }
        else if (extension == ".xlsx")
        {
            long totalBytes = 0;
            var shared = archive.GetEntry("xl/sharedStrings.xml") is { } strings
                ? ReadXml(strings, ref totalBytes).Descendants().Where(e => e.Name.LocalName == "si")
                    .Select(e => string.Concat(e.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value))).ToArray()
                : Array.Empty<string>();
            var sheets = archive.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)
                && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).Take(MaxEntries).ToArray();
            if (sheets.Length == 0) throw new InvalidDataException("No se encontraron hojas de cálculo.");
            foreach (var sheet in sheets)
            {
                if (totalBytes + sheet.Length > MaxXmlBytes) { truncated = true; break; }
                text.AppendLine(sheet.FullName);
                foreach (var row in ReadXml(sheet, ref totalBytes).Descendants().Where(e => e.Name.LocalName == "row"))
                {
                    foreach (var cell in row.Elements().Where(e => e.Name.LocalName == "c"))
                    {
                        var value = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? "";
                        if ((string?)cell.Attribute("t") == "s" && int.TryParse(value, out var index) && index >= 0 && index < shared.Length)
                            value = shared[index];
                        else if ((string?)cell.Attribute("t") == "inlineStr")
                            value = string.Concat(cell.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
                        text.Append((string?)cell.Attribute("r")).Append(": ").Append(value).Append('	');
                    }
                    text.AppendLine();
                    if (text.Length >= MaxCharacters) { truncated = true; break; }
                }
                if (truncated) break;
            }
        }
        else
        {
            var entries = archive.Entries.Where(e => extension switch
            {
                ".docx" => e.FullName == "word/document.xml",
                ".pptx" => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
                    && e.FullName.EndsWith(".xml", StringComparison.Ordinal),
                _ => e.FullName == "content.xml"
            }).OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.Ordinal).Take(MaxEntries).ToArray();
            if (entries.Length == 0) throw new InvalidDataException("El contenedor no contiene un documento compatible.");
            long totalBytes = 0;
            foreach (var entry in entries)
            {
                if (totalBytes + entry.Length > MaxXmlBytes) { truncated = true; break; }
                var document = ReadXml(entry, ref totalBytes);
                foreach (var paragraph in document.Descendants().Where(e => e.Name.LocalName is "p" or "h"))
                {
                    // Word/DrawingML usan nodos t; OpenDocument guarda el texto directamente en p/h.
                    var runs = paragraph.Descendants().Where(e => e.Name.LocalName == "t").ToArray();
                    text.AppendLine(runs.Length > 0 ? string.Concat(runs.Select(e => e.Value)) : paragraph.Value);
                    if (text.Length >= MaxCharacters) { truncated = true; break; }
                }
                if (truncated) break;
                text.AppendLine();
            }
        }
        if (text.Length > MaxCharacters) text.Length = MaxCharacters;
        return text.ToString();
    }

    private static XDocument ReadXml(ZipArchiveEntry entry, ref long totalBytes)
    {
        totalBytes += entry.Length;
        if (totalBytes > MaxXmlBytes) throw new InvalidDataException("El documento supera el límite de lectura XML.");
        using var input = entry.Open();
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes
        });
        return XDocument.Load(reader);
    }
}
