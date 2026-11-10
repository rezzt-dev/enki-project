using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace enki.Services;

/// <summary>Hojas de cálculo (XLSX y ODS) como texto compacto para el asistente: una sección por hoja con su
/// nombre y su rango, y cada fila como valores separados por tabuladores precedidos del número de fila. Es
/// mucho más barato en tokens que repetir la referencia de cada celda y conserva lo necesario para hablar de
/// "la fila 57" o "la columna C". Las fechas guardadas como número se escriben como fecha; las fórmulas
/// aportan su último valor calculado, sin recalcular. Se lee en streaming, sin DTD ni entidades externas, y se
/// para al llegar al límite de caracteres.</summary>
internal static class SpreadsheetTextExtractor
{
    private const int MaxSheets = 256;
    private const int MaxColumns = 1024;
    private const long MaxXmlCharacters = 256L * 1024 * 1024;
    private const string OdsTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string OdsOffice = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";

    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".xlsm" or ".xltx" or ".xltm" or ".ods";

    /// <exception cref="InvalidDataException">El archivo no es una hoja de cálculo válida.</exception>
    /// <exception cref="XmlException">El XML está mal formado o declara DTD.</exception>
    public static string Read(byte[] bytes, string path, int maxChars, out bool truncated)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var output = new StringBuilder();
        truncated = Path.GetExtension(path).Equals(".ods", StringComparison.OrdinalIgnoreCase)
            ? ReadOds(archive, output, maxChars)
            : ReadXlsx(archive, output, maxChars);
        if (output.Length > maxChars)
        {
            output.Length = maxChars;
            truncated = true;
        }
        return output.ToString().TrimEnd();
    }

    // ===================== XLSX =====================

    private static bool ReadXlsx(ZipArchive archive, StringBuilder output, int maxChars)
    {
        var shared = ReadSharedStrings(archive);
        var (dateStyles, date1904) = ReadDateStyles(archive);
        var sheets = ResolveXlsxSheets(archive);
        if (sheets.Count == 0) throw new InvalidDataException("No worksheets were found.");
        foreach (var (name, entry) in sheets.Take(MaxSheets))
        {
            var rows = new List<Row>();
            var stop = false;
            using (var reader = CreateReader(entry))
            {
                Row? row = null;
                var nextRow = 1;
                var nextColumn = 1;
                var budget = maxChars - output.Length;
                var used = 0;
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.LocalName == "row")
                    {
                        if (row is { Cells.Count: > 0 }) rows.Add(row);
                        var number = int.TryParse(reader.GetAttribute("r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : nextRow;
                        nextRow = number + 1;
                        nextColumn = 1;
                        row = new Row(number);
                        if (used > budget) { stop = true; break; }
                    }
                    else if (reader.LocalName == "c" && row is not null)
                    {
                        var reference = reader.GetAttribute("r");
                        var column = reference is null ? nextColumn : ColumnIndex(reference);
                        nextColumn = column + 1;
                        var type = reader.GetAttribute("t");
                        var style = int.TryParse(reader.GetAttribute("s"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : -1;
                        var value = ReadXlsxCell(reader, type, style, shared, dateStyles, date1904);
                        if (value.Length == 0 || column > MaxColumns) continue;
                        row.Cells.Add((column, value));
                        used += value.Length + 1;
                    }
                }
                if (!stop && row is { Cells.Count: > 0 }) rows.Add(row);
            }
            WriteSheet(output, name, rows);
            if (stop || output.Length >= maxChars) return true;
        }
        return sheets.Count > MaxSheets;
    }

    private static string ReadXlsxCell(XmlReader reader, string? type, int style, string[] shared, HashSet<int> dateStyles, bool date1904)
    {
        if (reader.IsEmptyElement) return string.Empty;
        string? raw = null;
        string? formula = null;
        var inline = new StringBuilder();
        var depth = reader.Depth;
        reader.Read();
        // ReadElementContentAsString ya avanza al nodo siguiente: no se vuelve a llamar a Read después.
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "v") raw = reader.ReadElementContentAsString();
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "f") formula = reader.ReadElementContentAsString();
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t" && type == "inlineStr")
                inline.Append(reader.ReadElementContentAsString());
            else reader.Read();
        }
        var value = type switch
        {
            "s" => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < shared.Length
                ? shared[index] : string.Empty,
            "inlineStr" => inline.ToString(),
            "b" => raw == "1" ? "TRUE" : raw == "0" ? "FALSE" : raw ?? string.Empty,
            "e" or "str" => raw ?? string.Empty,
            _ when raw is not null && dateStyles.Contains(style)
                   && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) => FormatSerialDate(serial, date1904),
            // Una fórmula que el libro guardó sin calcular (generado por un programa, no por Excel) aporta su texto.
            _ when string.IsNullOrEmpty(raw) && !string.IsNullOrWhiteSpace(formula) => "=" + formula,
            _ => raw ?? string.Empty
        };
        return CleanCell(value);
    }

    private static string[] ReadSharedStrings(ZipArchive archive)
    {
        if (archive.GetEntry("xl/sharedStrings.xml") is not { } entry) return [];
        var strings = new List<string>();
        using var reader = CreateReader(entry);
        StringBuilder? current = null;
        var phonetic = 0;
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t" && current is not null && phonetic == 0)
            {
                current.Append(reader.ReadElementContentAsString());
                continue;
            }
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == "si") current = new StringBuilder();
                // Las guías fonéticas (rPh) no son parte del texto visible de la celda.
                else if (reader.LocalName == "rPh" && !reader.IsEmptyElement) phonetic++;
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "rPh") phonetic--;
                else if (reader.LocalName == "si" && current is not null)
                {
                    strings.Add(current.ToString());
                    current = null;
                }
            }
            reader.Read();
        }
        return strings.ToArray();
    }

    /// <summary>Estilos de celda cuyo formato numérico es de fecha u hora (los integrados 14–22 y 45–47, y los
    /// personalizados que llevan d, m con y, h o s fuera de comillas y corchetes), y si el libro cuenta los
    /// días desde 1904.</summary>
    private static (HashSet<int> DateStyles, bool Date1904) ReadDateStyles(ZipArchive archive)
    {
        var date1904 = false;
        if (archive.GetEntry("xl/workbook.xml") is { } workbook)
        {
            using var reader = CreateReader(workbook);
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "workbookPr")
                {
                    date1904 = reader.GetAttribute("date1904") is "1" or "true";
                    break;
                }
        }
        var styles = new HashSet<int>();
        if (archive.GetEntry("xl/styles.xml") is not { } entry) return (styles, date1904);
        var customDates = new HashSet<int>();
        var inCellXfs = false;
        var index = 0;
        using (var reader = CreateReader(entry))
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "cellXfs") inCellXfs = false;
                if (reader.NodeType != XmlNodeType.Element) continue;
                switch (reader.LocalName)
                {
                    case "numFmt" when int.TryParse(reader.GetAttribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                                       && IsDateFormat(reader.GetAttribute("formatCode") ?? string.Empty):
                        customDates.Add(id);
                        break;
                    case "cellXfs":
                        inCellXfs = !reader.IsEmptyElement;
                        break;
                    case "xf" when inCellXfs:
                        if (int.TryParse(reader.GetAttribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var format)
                            && (format is >= 14 and <= 22 or >= 45 and <= 47 || customDates.Contains(format)))
                            styles.Add(index);
                        index++;
                        break;
                }
            }
        }
        return (styles, date1904);
    }

    internal static bool IsDateFormat(string code)
    {
        var quoted = false;
        var bracket = false;
        bool hasDay = false, hasYear = false, hasTime = false;
        for (var i = 0; i < code.Length; i++)
        {
            var c = char.ToLowerInvariant(code[i]);
            if (c == '\\' ) { i++; continue; }
            if (c == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (c == '[') { bracket = true; continue; }
            if (c == ']') { bracket = false; continue; }
            if (bracket) continue;
            if (c is 'd') hasDay = true;
            else if (c is 'y') hasYear = true;
            else if (c is 'h' or 's') hasTime = true;
        }
        return hasDay || hasYear || hasTime;
    }

    private static string FormatSerialDate(double serial, bool date1904)
    {
        if (serial is < 0 or > 2_958_465) return serial.ToString(CultureInfo.InvariantCulture);
        try
        {
            var date = DateTime.FromOADate(serial + (date1904 ? 1462 : 0));
            return serial < 1 ? date.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        catch (ArgumentException)
        {
            return serial.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Hojas en el orden del libro con su nombre visible; si el libro no lo dice, las hojas que haya.</summary>
    private static List<(string Name, ZipArchiveEntry Entry)> ResolveXlsxSheets(ZipArchive archive)
    {
        var result = new List<(string, ZipArchiveEntry)>();
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (archive.GetEntry("xl/_rels/workbook.xml.rels") is { } rels)
        {
            using var reader = CreateReader(rels);
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship"
                    && reader.GetAttribute("Id") is { } id && reader.GetAttribute("Target") is { } target)
                    targets[id] = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        }
        if (archive.GetEntry("xl/workbook.xml") is { } workbook)
        {
            using var reader = CreateReader(workbook);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sheet") continue;
                var relation = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                if (relation is not null && targets.TryGetValue(relation, out var target) && archive.GetEntry(target) is { } entry)
                    result.Add(((reader.GetAttribute("name") ?? Path.GetFileNameWithoutExtension(target))
                        + (reader.GetAttribute("state") is "hidden" or "veryHidden" ? " (hidden)" : string.Empty), entry));
            }
        }
        if (result.Count > 0) return result;
        return archive.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.Ordinal)
            .Select(e => (Path.GetFileNameWithoutExtension(e.FullName), e))
            .ToList();
    }

    // ===================== ODS =====================

    private static bool ReadOds(ZipArchive archive, StringBuilder output, int maxChars)
    {
        var entry = archive.GetEntry("content.xml") ?? throw new InvalidDataException("No spreadsheet content was found.");
        using var reader = CreateReader(entry);
        string? sheet = null;
        var rows = new List<Row>();
        Row? row = null;
        var rowNumber = 0;
        var column = 0;
        var sheets = 0;
        var used = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "table-row" && row is { Cells.Count: > 0 })
                {
                    // Una fila repetida con contenido se escribe una vez: repetirla solo gastaría tokens.
                    rows.Add(row);
                    row = null;
                }
                else if (reader.LocalName == "table" && sheet is not null)
                {
                    WriteSheet(output, sheet, rows);
                    sheet = null;
                    if (output.Length >= maxChars) return true;
                }
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.LocalName)
            {
                case "table":
                    if (++sheets > MaxSheets) return true;
                    sheet = reader.GetAttribute("name", OdsTable) ?? $"Sheet{sheets}";
                    rows = [];
                    rowNumber = 0;
                    used = 0;
                    break;
                case "table-row" when sheet is not null:
                    var repeated = Repeat(reader, "number-rows-repeated");
                    rowNumber++;
                    column = 0;
                    row = new Row(rowNumber);
                    if (reader.IsEmptyElement) { rowNumber += repeated - 1; row = null; }
                    else rowNumber += repeated - 1;
                    if (used > maxChars - output.Length)
                    {
                        WriteSheet(output, sheet, rows);
                        return true;
                    }
                    break;
                case "table-cell" or "covered-table-cell" when row is not null:
                    var span = Repeat(reader, "number-columns-repeated");
                    var value = CleanCell(ReadOdsCell(reader));
                    if (value.Length > 0)
                        for (var i = 0; i < Math.Min(span, MaxColumns - column); i++)
                        {
                            row.Cells.Add((column + 1 + i, value));
                            used += value.Length + 1;
                        }
                    column += span;
                    break;
            }
        }
        if (sheet is not null) WriteSheet(output, sheet, rows);
        if (sheets == 0) throw new InvalidDataException("No spreadsheet content was found.");
        return false;
    }

    private static int Repeat(XmlReader reader, string attribute) =>
        int.TryParse(reader.GetAttribute(attribute, OdsTable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0
            ? Math.Min(count, 1_048_576) : 1;

    private static string ReadOdsCell(XmlReader reader)
    {
        var type = reader.GetAttribute("value-type", OdsOffice);
        var typed = type switch
        {
            "float" or "percentage" or "currency" => reader.GetAttribute("value", OdsOffice),
            "date" => reader.GetAttribute("date-value", OdsOffice)?.Replace('T', ' '),
            "time" => reader.GetAttribute("time-value", OdsOffice),
            "boolean" => reader.GetAttribute("boolean-value", OdsOffice)?.ToUpperInvariant(),
            _ => null
        };
        if (reader.IsEmptyElement) return typed ?? string.Empty;
        var text = new StringBuilder();
        var depth = reader.Depth;
        while (reader.Read() && reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "p" && text.Length > 0) text.Append(' ');
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "s") text.Append(' ');
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace) text.Append(reader.Value);
        }
        return typed ?? text.ToString();
    }

    // ===================== Comunes =====================

    private sealed record Row(int Number)
    {
        public List<(int Column, string Value)> Cells { get; } = [];
    }

    /// <summary>"## Hoja · A1:F120", seguida de una línea por fila no vacía: número de fila y valores separados
    /// por tabuladores, alineados con la primera columna con datos.</summary>
    private static void WriteSheet(StringBuilder output, string name, List<Row> rows)
    {
        if (output.Length > 0) output.Append('\n');
        output.Append("## ").Append(CleanCell(name));
        if (rows.Count == 0)
        {
            output.Append(" (empty)\n");
            return;
        }
        var first = rows.Min(r => r.Cells.Min(c => c.Column));
        var last = rows.Max(r => r.Cells.Max(c => c.Column));
        output.Append(" · ").Append(ColumnName(first)).Append(rows[0].Number.ToString(CultureInfo.InvariantCulture))
            .Append(':').Append(ColumnName(last)).Append(rows[^1].Number.ToString(CultureInfo.InvariantCulture))
            .Append(" · row number, then columns ").Append(ColumnName(first)).Append('-').Append(ColumnName(last)).Append('\n');
        foreach (var row in rows)
        {
            output.Append(row.Number.ToString(CultureInfo.InvariantCulture));
            var position = first - 1;
            foreach (var (column, value) in row.Cells.OrderBy(c => c.Column))
            {
                if (column <= position) continue;
                output.Append('\t', column - position);
                output.Append(value);
                position = column;
            }
            output.Append('\n');
        }
    }

    private static string CleanCell(string value)
    {
        if (value.Length == 0) return value;
        var builder = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = builder.Length > 0; continue; }
            if (space) builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        return builder.ToString();
    }

    internal static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var c in reference)
        {
            if (c is >= 'A' and <= 'Z') index = index * 26 + (c - 'A' + 1);
            else if (c is >= 'a' and <= 'z') index = index * 26 + (c - 'a' + 1);
            else break;
            if (index > 16_384) return 16_384;
        }
        return Math.Max(1, index);
    }

    internal static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var n = index; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }

    private static XmlReader CreateReader(ZipArchiveEntry entry) => XmlReader.Create(entry.Open(), new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxXmlCharacters,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = true
    });
}
