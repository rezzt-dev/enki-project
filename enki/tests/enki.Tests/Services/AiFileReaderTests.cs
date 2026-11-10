using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using enki.Models;
using enki.Services;
using ImageMagick;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Lectura de archivos para el asistente sobre archivos reales en un vault desechable: texto, troceado,
/// hojas de cálculo, PDF con y sin capa de texto, e imágenes.</summary>
public sealed class AiFileReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly VaultService _vault;

    public AiFileReaderTests()
    {
        var settings = new SettingsService(_directory);
        _vault = new VaultService(settings);
        _vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        AiFileReader.ClearCache();
    }

    public void Dispose()
    {
        AiFileReader.ClearCache();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private VaultFile Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_vault.VaultPath, name);
        File.WriteAllBytes(path, bytes);
        return AiFileReader.Describe(path);
    }

    internal static byte[] Zip(params (string Name, string Content)[] entries)
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

    /// <summary>PDF mínimo válido: una página por elemento, con ese texto en Helvetica o en blanco si es
    /// <see langword="null"/> (como una página escaneada, sin capa de texto).</summary>
    internal static byte[] Pdf(params string?[] pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', pages.Select((_, i) => $"{4 + i * 2} 0 R"))}] /Count {pages.Length} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        for (var i = 0; i < pages.Length; i++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + i * 2} 0 R >>");
            var stream = pages[i] is { } text ? $"BT /F1 12 Tf 20 150 Td ({text}) Tj ET" : string.Empty;
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
        }
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    internal static byte[] Png(int width, int height)
    {
        using var image = new MagickImage(MagickColors.SteelBlue, (uint)width, (uint)height);
        return image.ToByteArray(MagickFormat.Png);
    }

    // ===================== Formatos y límites =====================

    [Theory]
    [InlineData("notas.txt", AiFileFormat.Text)]
    [InlineData("informe.pdf", AiFileFormat.Pdf)]
    [InlineData("acta.docx", AiFileFormat.Document)]
    [InlineData("diapositivas.odp", AiFileFormat.Document)]
    [InlineData("cuentas.xlsx", AiFileFormat.Spreadsheet)]
    [InlineData("cuentas.ods", AiFileFormat.Spreadsheet)]
    [InlineData("copia.zip", AiFileFormat.Archive)]
    [InlineData("foto.heic", AiFileFormat.Image)]
    [InlineData("logo.svg", AiFileFormat.Image)]
    [InlineData("cancion.mp3", AiFileFormat.Media)]
    [InlineData("datos.bin", AiFileFormat.Binary)]
    public void FormatOf_FollowsTheFilesSectionKinds(string name, AiFileFormat expected) =>
        Assert.Equal(expected, AiFileReader.FormatOf(new VaultFile { FullPath = name, Kind = VaultFile.KindFromPath(name) }));

    [Fact]
    public void Check_RejectsMediaAndUnknownBinaries_AndAppliesPerFormatLimits()
    {
        VaultFile File(string name, long size) => new() { FullPath = name, Kind = VaultFile.KindFromPath(name), SizeBytes = size };
        Assert.Equal(AiFileProblem.Unsupported, AiFileReader.Check(File("video.mp4", 10)));
        Assert.Equal(AiFileProblem.Unsupported, AiFileReader.Check(File("datos.bin", 10)));
        Assert.Equal(AiFileProblem.None, AiFileReader.Check(File("datos.bin", 10), allowSniffing: true));
        Assert.Equal(AiFileProblem.TooLarge, AiFileReader.Check(File("notas.txt", AiFileReader.MaxTextBytes + 1)));
        Assert.Equal(AiFileProblem.None, AiFileReader.Check(File("informe.pdf", 20 * 1024 * 1024)));
        Assert.Equal(AiFileProblem.TooLarge, AiFileReader.Check(File("informe.pdf", AiFileReader.MaxPdfBytes + 1)));
    }

    // ===================== Texto y troceado =====================

    [Fact]
    public void Text_IsReadInChunks_ThatEndOnALineAndContinueWithNextOffset()
    {
        var file = Write("lineas.txt", Encoding.UTF8.GetBytes("﻿" + string.Join('\n', Enumerable.Range(1, 200).Select(i => $"linea {i:D3}"))));
        var first = AiFileReader.Read(_vault, file, new AiFileReadOptions { MaxChars = 1000 });
        Assert.StartsWith("linea 001", first.Text);
        Assert.EndsWith("\n", first.Text);
        Assert.NotNull(first.NextOffset);
        var rest = new StringBuilder(first.Text);
        var next = first.NextOffset;
        while (next is { } offset)
        {
            var chunk = AiFileReader.Read(_vault, file, new AiFileReadOptions { Offset = offset, MaxChars = 1000 });
            rest.Append(chunk.Text);
            next = chunk.NextOffset;
        }
        Assert.Equal(first.TotalChars, rest.Length);
        Assert.EndsWith("linea 200", rest.ToString());
    }

    [Fact]
    public void Slice_NeverSplitsASurrogatePair()
    {
        var (slice, _, next) = AiFileReader.Slice("ab😀cd", 0, 3);
        Assert.Equal("ab", slice);
        Assert.Equal(2, next);
    }

    [Fact]
    public void UnknownExtension_IsReadOnlyWhenItsContentIsText()
    {
        Assert.Contains("hola", AiFileReader.Read(_vault, Write("apuntes.org", Encoding.UTF8.GetBytes("* hola")), new AiFileReadOptions()).Text);
        Assert.Throws<InvalidDataException>(() => AiFileReader.Read(_vault, Write("datos.bin", [0xFF, 0xFE, 0x00, 0x01]), new AiFileReadOptions()));
    }

    [Fact]
    public void Media_IsNeverRead() =>
        Assert.Throws<InvalidDataException>(() => AiFileReader.Read(_vault, Write("cancion.mp3", [0x49, 0x44, 0x33]), new AiFileReadOptions()));

    // ===================== Documentos y hojas de cálculo =====================

    private const string W = "xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main' "
        + "xmlns:mc='http://schemas.openxmlformats.org/markup-compatibility/2006' "
        + "xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'";

    [Fact]
    public void Word_KeepsHeadingsListsTablesNotesAndComments_WithoutDuplicatingTextBoxes()
    {
        var bytes = Zip(
            ("word/styles.xml", $"<w:styles {W}>"
                + "<w:style w:type='paragraph' w:styleId='Ttulo1'><w:name w:val='heading 1'/></w:style>"
                + "<w:style w:type='paragraph' w:styleId='Vietas'><w:name w:val='List Bullet'/><w:pPr><w:numPr><w:numId w:val='3'/></w:numPr></w:pPr></w:style>"
                + "</w:styles>"),
            ("word/document.xml", $"<w:document {W}><w:body>"
                + "<w:p><w:pPr><w:pStyle w:val='Ttulo1'/></w:pPr><w:r><w:t>Resumen</w:t></w:r></w:p>"
                + "<w:p><w:r><w:t xml:space='preserve'>Hola </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>mundo</w:t></w:r>"
                + "<w:del><w:r><w:delText>borrado</w:delText></w:r></w:del><w:r><w:footnoteReference w:id='1'/></w:r></w:p>"
                + "<w:p/><w:p/>"
                + "<w:p><w:pPr><w:pStyle w:val='Vietas'/></w:pPr><w:r><w:t>Por estilo</w:t></w:r></w:p>"
                + "<w:p><w:pPr><w:numPr><w:ilvl w:val='1'/><w:numId w:val='5'/></w:numPr></w:pPr><w:r><w:t>Anidado</w:t></w:r></w:p>"
                + "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Mes</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Total</w:t></w:r></w:p></w:tc></w:tr>"
                + "<w:tr><w:tc><w:p><w:r><w:t>Enero</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>100</w:t></w:r></w:p></w:tc></w:tr></w:tbl>"
                + "<w:p><w:r><mc:AlternateContent><mc:Choice Requires='wps'><w:drawing><w:txbxContent><w:p><w:r><w:t>En el cuadro</w:t></w:r></w:p></w:txbxContent></w:drawing></mc:Choice>"
                + "<mc:Fallback><w:pict><w:txbxContent><w:p><w:r><w:t>En el cuadro</w:t></w:r></w:p></w:txbxContent></w:pict></mc:Fallback></mc:AlternateContent></w:r></w:p>"
                + "<w:sectPr/></w:body></w:document>"),
            ("word/header1.xml", $"<w:hdr {W}><w:p><w:r><w:t>Empresa</w:t></w:r></w:p></w:hdr>"),
            ("word/header2.xml", $"<w:hdr {W}><w:p><w:r><w:t>Empresa</w:t></w:r></w:p></w:hdr>"),
            ("word/footnotes.xml", $"<w:footnotes {W}><w:footnote w:type='separator' w:id='-1'><w:p><w:r><w:separator/></w:r></w:p></w:footnote>"
                + "<w:footnote w:id='1'><w:p><w:r><w:t>Fuente: INE</w:t></w:r></w:p></w:footnote></w:footnotes>"),
            ("word/comments.xml", $"<w:comments {W}><w:comment w:id='0' w:author='Ana'><w:p><w:r><w:t>Revisar</w:t></w:r></w:p></w:comment></w:comments>"));
        var text = AiFileReader.Read(_vault, Write("acta.docx", bytes), new AiFileReadOptions()).Text;
        Assert.Equal(
            "[Header] Empresa\n\n# Resumen\nHola mundo[^1]\n- Por estilo\n  - Anidado\n\n| Mes | Total |\n| Enero | 100 |\n\n"
            + "En el cuadro\n\n[^1] Fuente: INE\n[Comment by Ana] Revisar",
            text);
    }

    [Fact]
    public void PowerPoint_FollowsThePresentationOrder_WithTitlesTablesNotesAndHiddenSlides()
    {
        const string p = "xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main' "
            + "xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main' "
            + "xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'";
        static string Shape(string type, string text) =>
            $"<p:sp><p:nvSpPr><p:cNvPr id='1' name='s'/><p:cNvSpPr/><p:nvPr>{(type.Length > 0 ? $"<p:ph type='{type}'/>" : "")}</p:nvPr></p:nvSpPr>"
            + $"<p:txBody><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp>";
        var bytes = Zip(
            ("ppt/presentation.xml", $"<p:presentation {p}><p:sldIdLst><p:sldId id='256' r:id='rId3'/><p:sldId id='257' r:id='rId2'/></p:sldIdLst></p:presentation>"),
            ("ppt/_rels/presentation.xml.rels", "<Relationships><Relationship Id='rId2' Target='slides/slide1.xml'/><Relationship Id='rId3' Target='slides/slide2.xml'/></Relationships>"),
            ("ppt/slides/slide1.xml", $"<p:sld {p} show='0'><p:cSld><p:spTree>{Shape("title", "Cierre")}{Shape("sldNum", "7")}</p:spTree></p:cSld></p:sld>"),
            ("ppt/slides/slide2.xml", $"<p:sld {p}><p:cSld><p:spTree>{Shape("ctrTitle", "Portada")}"
                + "<p:sp><p:nvSpPr><p:cNvPr id='2' name='b'/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:txBody>"
                + "<a:p><a:r><a:t>Idea</a:t></a:r></a:p><a:p><a:pPr lvl='1'/><a:r><a:t>Detalle</a:t></a:r><a:br/><a:r><a:t>más</a:t></a:r></a:p></p:txBody></p:sp>"
                + "<p:graphicFrame><a:graphic><a:graphicData><a:tbl><a:tr><a:tc><a:txBody><a:p><a:r><a:t>Q1</a:t></a:r></a:p></a:txBody></a:tc>"
                + "<a:tc><a:txBody><a:p><a:r><a:t>10</a:t></a:r></a:p></a:txBody></a:tc></a:tr></a:tbl></a:graphicData></a:graphic></p:graphicFrame>"
                + "</p:spTree></p:cSld></p:sld>"),
            ("ppt/slides/_rels/slide2.xml.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide' Target='../notesSlides/notesSlide1.xml'/></Relationships>"),
            ("ppt/notesSlides/notesSlide1.xml", $"<p:notes {p}><p:cSld><p:spTree>{Shape("sldImg", "")}{Shape("body", "Saludar al público")}</p:spTree></p:cSld></p:notes>"));
        var text = AiFileReader.Read(_vault, Write("charla.pptx", bytes), new AiFileReadOptions()).Text;
        Assert.Equal("## Slide 1: Portada\nIdea\n  Detalle más\n| Q1 | 10 |\nNotes: Saludar al público\n\n## Slide 2 (hidden): Cierre", text);
    }

    [Theory]
    [InlineData("macro.docm", AiFileFormat.Document)]
    [InlineData("plantilla.potx", AiFileFormat.Document)]
    [InlineData("libro.xlsm", AiFileFormat.Spreadsheet)]
    public void OfficeVariants_AreRecognised(string name, AiFileFormat expected) =>
        Assert.Equal(expected, AiFileReader.FormatOf(new VaultFile { FullPath = name, Kind = VaultFile.KindFromPath(name) }));

    [Fact]
    public void Office_RejectsCorruptFilesAndExternalEntities()
    {
        Assert.Throws<InvalidDataException>(() => AiFileReader.Read(_vault, Write("roto.docx", [1, 2, 3]), new AiFileReadOptions()));
        var entity = Zip(("word/document.xml", "<!DOCTYPE d [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><document><body><p><r><t>&x;</t></r></p></body></document>"));
        Assert.Throws<InvalidDataException>(() => AiFileReader.Read(_vault, Write("malo.docx", entity), new AiFileReadOptions()));
        Assert.Throws<InvalidDataException>(() => AiFileReader.Read(_vault, Write("vacia.pptx", Zip(("ppt/presentation.xml", "<presentation/>"))), new AiFileReadOptions()));
    }

    [Fact]
    public void Xlsx_FormulaWithoutCachedValue_ShowsTheFormula_AndHiddenSheetsAreMarked()
    {
        var bytes = Zip(
            ("xl/workbook.xml", "<workbook xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets>"
                + "<sheet name='Datos' sheetId='1' r:id='rId1'/><sheet name='Aux' sheetId='2' state='hidden' r:id='rId2'/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id='rId1' Target='worksheets/sheet1.xml'/><Relationship Id='rId2' Target='worksheets/sheet2.xml'/></Relationships>"),
            ("xl/worksheets/sheet1.xml", "<worksheet><sheetData><row r='1'><c r='A1'><v>2</v></c><c r='B1'><f>A1*2</f><v></v></c></row></sheetData></worksheet>"),
            ("xl/worksheets/sheet2.xml", "<worksheet><sheetData><row r='1'><c r='A1' t='inlineStr'><is><t>x</t></is></c></row></sheetData></worksheet>"));
        var text = AiFileReader.Read(_vault, Write("libro.xlsm", bytes), new AiFileReadOptions()).Text;
        Assert.Contains("1\t2\t=A1*2", text);
        Assert.Contains("## Aux (hidden)", text);
    }

    [Fact]
    public void Xlsx_UsesSheetNames_RowNumbers_TabsAndDates()
    {
        var bytes = Zip(
            ("xl/workbook.xml", "<workbook xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets>"
                + "<sheet name='Ventas' sheetId='1' r:id='rId1'/><sheet name='Vacía' sheetId='2' r:id='rId2'/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id='rId1' Target='worksheets/sheet1.xml'/>"
                + "<Relationship Id='rId2' Target='/xl/worksheets/sheet2.xml'/></Relationships>"),
            ("xl/styles.xml", "<styleSheet><cellXfs count='2'><xf numFmtId='0'/><xf numFmtId='14'/></cellXfs></styleSheet>"),
            ("xl/sharedStrings.xml", "<sst><si><t>Fecha</t></si><si><r><t>Impo</t></r><r><t>rte</t></r></si></sst>"),
            ("xl/worksheets/sheet1.xml", "<worksheet><sheetData>"
                + "<row r='1'><c r='A1' t='s'><v>0</v></c><c r='B1' t='s'><v>1</v></c></row>"
                + "<row r='2'/>"
                + "<row r='3'><c r='A3' s='1'><v>45292</v></c><c r='B3'><f>SUM(X1)</f><v>42.5</v></c><c r='D3' t='b'><v>1</v></c></row>"
                + "</sheetData></worksheet>"),
            ("xl/worksheets/sheet2.xml", "<worksheet><sheetData/></worksheet>"));
        var text = AiFileReader.Read(_vault, Write("cuentas.xlsx", bytes), new AiFileReadOptions()).Text;
        Assert.Contains("## Ventas · A1:D3", text);
        Assert.Contains("1\tFecha\tImporte\n", text);
        Assert.Contains("3\t2024-01-01\t42.5\t\tTRUE", text);
        Assert.DoesNotContain("\n2\t", text);
        Assert.Contains("## Vacía (empty)", text);
    }

    [Fact]
    public void Ods_ExpandsRepeatedCells_KeepsRowNumbers_AndReadsTypedValues()
    {
        const string content = """
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0">
              <office:body><office:spreadsheet><table:table table:name="Datos">
                <table:table-row>
                  <table:table-cell office:value-type="string"><text:p>Nombre</text:p></table:table-cell>
                  <table:table-cell office:value-type="float" office:value="3"><text:p>3,00 €</text:p></table:table-cell>
                  <table:table-cell table:number-columns-repeated="2"/>
                  <table:table-cell office:value-type="date" office:date-value="2026-11-09"><text:p>09/11/26</text:p></table:table-cell>
                </table:table-row>
                <table:table-row table:number-rows-repeated="1000"><table:table-cell table:number-columns-repeated="5"/></table:table-row>
                <table:table-row><table:table-cell office:value-type="string"><text:p>Ana</text:p></table:table-cell></table:table-row>
              </table:table></office:spreadsheet></office:body>
            </office:document-content>
            """;
        var text = AiFileReader.Read(_vault, Write("cuentas.ods", Zip(("content.xml", content))), new AiFileReadOptions()).Text;
        Assert.Contains("## Datos · A1:E1002", text);
        Assert.Contains("1\tNombre\t3\t\t\t2026-11-09\n", text);
        Assert.EndsWith("1002\tAna", text);
    }

    [Fact]
    public void Spreadsheet_RejectsExternalEntities()
    {
        var bytes = Zip(("xl/worksheets/sheet1.xml", "<!DOCTYPE w [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><worksheet><row><c><v>&x;</v></c></row></worksheet>"));
        Assert.Throws<InvalidDataException>(() => AiFileReader.Read(_vault, Write("malo.xlsx", bytes), new AiFileReadOptions()));
    }

    [Theory]
    [InlineData("dd/mm/yyyy", true)]
    [InlineData("[$-409]h:mm AM/PM", true)]
    [InlineData("#,##0.00 \"días\"", false)]
    [InlineData("0.00%", false)]
    public void DateFormats_AreRecognisedOutsideQuotesAndBrackets(string code, bool isDate) =>
        Assert.Equal(isDate, SpreadsheetTextExtractor.IsDateFormat(code));

    // ===================== PDF =====================

    [Fact]
    public void Pdf_ReturnsTextPerPage_AndRendersOnlyPagesWithoutText()
    {
        var file = Write("informe.pdf", Pdf("Hola desde la primera pagina del informe", null, "Tercera pagina con texto suficiente"));
        var extract = AiFileReader.Read(_vault, file, new AiFileReadOptions());
        Assert.Equal(3, extract.PageCount);
        Assert.Contains("[page 1]\nHola desde la primera pagina del informe", extract.Text);
        Assert.Contains("[page 2: no text layer]", extract.Text);
        Assert.Equal([2], extract.PagesWithoutText);
        Assert.Empty(extract.Images);

        var scan = AiFileReader.Read(_vault, file, new AiFileReadOptions { Pages = "2" });
        Assert.Equal([2], scan.RenderedPages);
        var image = Assert.Single(scan.Images);
        Assert.Equal("image/jpeg", image.MimeType);
        Assert.Equal(0xFF, image.Data[0]);
        Assert.Equal(0xD8, image.Data[1]);

        var noImages = AiFileReader.Read(_vault, file, new AiFileReadOptions { Pages = "2", IncludeImages = false });
        Assert.Empty(noImages.Images);
    }

    [Fact]
    public void Pdf_PagesAreValidated_AndAsImagesIsCapped()
    {
        var file = Write("largo.pdf", Pdf(Enumerable.Range(1, 5).Select(i => $"Pagina numero {i} con algo de texto").ToArray()));
        Assert.Throws<ArgumentException>(() => AiFileReader.Read(_vault, file, new AiFileReadOptions { Pages = "6" }));
        Assert.Throws<ArgumentException>(() => AiFileReader.Read(_vault, file, new AiFileReadOptions { Pages = "a-b" }));
        var some = AiFileReader.Read(_vault, file, new AiFileReadOptions { Pages = "2-3,5" });
        Assert.DoesNotContain("[page 1]", some.Text);
        Assert.Contains("[page 5]", some.Text);
        var rendered = AiFileReader.Read(_vault, file, new AiFileReadOptions { PagesAsImages = true });
        Assert.Equal(AiFileReader.MaxRenderedPages, rendered.Images.Count);
        Assert.Equal("1-3", AiFileReader.FormatPages(rendered.RenderedPages));
    }

    [Fact]
    public void Pdf_ThatIsDamaged_IsReportedAsUnreadable() =>
        Assert.Throws<InvalidDataException>(() => AiFileReader.Read(_vault, Write("roto.pdf", Encoding.ASCII.GetBytes("no es un pdf")), new AiFileReadOptions()));

    // ===================== Imágenes =====================

    [Fact]
    public void Image_IsDownscaledToTheRequestedSide_AsJpeg()
    {
        var file = Write("foto.png", Png(3000, 2000));
        var standard = AiFileReader.Read(_vault, file, new AiFileReadOptions());
        Assert.Equal(3000, standard.OriginalWidth);
        Assert.Equal(2000, standard.OriginalHeight);
        Assert.Equal(AiFileReader.StandardImageSide, standard.ImageWidth);
        Assert.Equal("image/jpeg", Assert.Single(standard.Images).MimeType);

        var low = AiFileReader.Read(_vault, file, new AiFileReadOptions { ImageSide = AiFileReader.LowImageSide });
        Assert.Equal(AiFileReader.LowImageSide, low.ImageWidth);
        Assert.True(low.Images[0].Data.Length < standard.Images[0].Data.Length);
    }

    [Fact]
    public void Image_WithoutImages_OnlyProbesItsSize()
    {
        var extract = AiFileReader.Read(_vault, Write("pequeña.png", Png(40, 30)), new AiFileReadOptions { IncludeImages = false });
        Assert.Empty(extract.Images);
        Assert.Equal(40, extract.OriginalWidth);
        Assert.Contains("[Image, 40×30 px. Not included as text: call read_file with path=\"x\"",
            AiFileReader.ToAttachmentText(extract, "path=\"x\""));
        Assert.Contains("cannot be shown", AiFileReader.ToAttachmentText(extract, null));
    }

    [Fact]
    public void Svg_IsRasterised()
    {
        var svg = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='200' height='100'><rect width='200' height='100' fill='red'/></svg>");
        var extract = AiFileReader.Read(_vault, Write("logo.svg", svg), new AiFileReadOptions { ImageSide = AiFileReader.LowImageSide });
        Assert.Equal(AiFileReader.LowImageSide, extract.ImageWidth);
        Assert.Single(extract.Images);
    }

    [Fact]
    public void AttachmentText_ExplainsHowToContinue()
    {
        var file = Write("largo.txt", Encoding.UTF8.GetBytes(new string('x', 5000)));
        var extract = AiFileReader.Read(_vault, file, new AiFileReadOptions { MaxChars = 1000 });
        Assert.Contains("4000 more characters not included; read_file with path=\"largo.txt\" offset=1000 continues",
            AiFileReader.ToAttachmentText(extract, "path=\"largo.txt\""));
    }
}
