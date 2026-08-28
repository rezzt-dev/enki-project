using System.Linq;
using enki.Views.Editor.Markdown;
using Xunit;

namespace enki.Tests.Views.Editor.Markdown;

/// <summary>Cubre la parte de las tablas que se puede razonar sin pintar nada: qué es una tabla y qué no,
/// dónde empieza y acaba, y en qué celdas se parte cada fila. Lo visual (anchos de columna, rejilla) se
/// prueba en <c>enki.UITests</c>, que es la única capa con una plataforma Avalonia levantada.</summary>
public class MarkdownTableParserTests
{
    private static System.Collections.Generic.Dictionary<int, MdTableRow> Scan(string document) =>
        MarkdownTableParser.Scan(document, MarkdownLineParser.ComputeFences(document));

    [Fact]
    public void Scan_RecognizesHeaderDelimiterAndBodyRows()
    {
        var rows = Scan("| A | B |\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 |\n");

        Assert.Equal(MdTableRowKind.Header, rows[1].Kind);
        Assert.Equal(MdTableRowKind.Delimiter, rows[2].Kind);
        Assert.Equal(MdTableRowKind.Body, rows[3].Kind);
        Assert.Equal(MdTableRowKind.Body, rows[4].Kind);

        var table = rows[1].Table;
        Assert.Equal(1, table.StartLine);
        Assert.Equal(4, table.EndLine);
        Assert.Equal(2, table.ColumnCount);
        Assert.Equal(2, table.BodyRowCount);
    }

    [Fact]
    public void Scan_TableEndsAtABlankLine()
    {
        var rows = Scan("| A |\n| --- |\n| 1 |\n\n| ya no |\n");

        Assert.Equal(3, rows[1].Table.EndLine);
        Assert.False(rows.ContainsKey(4));
        Assert.False(rows.ContainsKey(5));
    }

    [Fact]
    public void Scan_TableEndsAtALineThatStartsAnotherBlock()
    {
        var rows = Scan("| A |\n| --- |\n| 1 |\n# Título | con barra\n");

        Assert.Equal(3, rows[1].Table.EndLine);
        Assert.False(rows.ContainsKey(4));
    }

    [Fact]
    public void Scan_TwoLinesWithPipesButNoDelimiterRow_AreNotATable()
    {
        Assert.Empty(Scan("a | b\nc | d\n"));
    }

    [Fact]
    public void Scan_DelimiterWithADifferentColumnCountThanTheHeader_IsNotATable()
    {
        Assert.Empty(Scan("| A | B |\n| --- | --- | --- |\n| 1 | 2 |\n"));
    }

    [Fact]
    public void Scan_TableInsideAFencedCodeBlock_IsNotATable()
    {
        Assert.Empty(Scan("```\n| A | B |\n| --- | --- |\n| 1 | 2 |\n```\n"));
    }

    [Fact]
    public void Scan_TableWithoutOuterPipes_IsRecognized()
    {
        var rows = Scan("A | B\n--- | ---\n1 | 2\n");

        Assert.Equal(3, rows[1].Table.EndLine);
        Assert.Equal(2, rows[1].Table.ColumnCount);
    }

    [Fact]
    public void Scan_ReadsTheAlignmentOfEachColumn()
    {
        var rows = Scan("| a | b | c | d |\n| --- | :-- | :-: | --: |\n");

        Assert.Equal(
            new[] { MdTableAlign.Default, MdTableAlign.Left, MdTableAlign.Center, MdTableAlign.Right },
            rows[1].Table.Alignments);
    }

    [Fact]
    public void Scan_RowWithMoreCellsThanTheHeader_WidensTheTableInsteadOfLosingTheCell()
    {
        // Recortar la celda de más (lo que haría un renderizador GFM estricto) escondería en el editor
        // texto que el usuario sí escribió; ensanchar la tabla lo deja a la vista.
        var rows = Scan("| A | B |\n| --- | --- |\n| 1 | 2 | 3 |\n");

        Assert.Equal(3, rows[1].Table.ColumnCount);
        Assert.Equal(MdTableAlign.Default, rows[1].Table.Alignments[2]);
    }

    [Fact]
    public void Scan_TwoTablesSeparatedByABlankLine_AreTwoDifferentTables()
    {
        var rows = Scan("| A |\n| --- |\n| 1 |\n\n| B |\n| --- |\n| 2 |\n");

        Assert.NotSame(rows[1].Table, rows[5].Table);
        Assert.Equal(MdTableRowKind.Header, rows[5].Kind);
        Assert.Equal(7, rows[5].Table.EndLine);
    }

    [Fact]
    public void Scan_TableWithOnlyHeaderAndDelimiter_IsStillATable()
    {
        var rows = Scan("| A | B |\n| --- | --- |\n");

        Assert.Equal(2, rows[1].Table.EndLine);
        Assert.Equal(0, rows[1].Table.BodyRowCount);
    }

    [Theory]
    [InlineData("| a | b |")]
    [InlineData("a | b")]
    [InlineData("|a|b|")]
    [InlineData("|   a   |   b   |")]
    public void SplitCells_TrimsEachCellAndIgnoresTheOuterPipes(string line)
    {
        var cells = MarkdownTableParser.SplitCells(line, 0);

        Assert.Equal(2, cells.Count);
        Assert.Equal("a", line[cells[0].Start..cells[0].End]);
        Assert.Equal("b", line[cells[1].Start..cells[1].End]);
    }

    [Fact]
    public void SplitCells_ReportsAbsoluteOffsets()
    {
        var line = "| ab | c |";
        var cells = MarkdownTableParser.SplitCells(line, 100);

        Assert.Equal(102, cells[0].Start);
        Assert.Equal(104, cells[0].End);
        Assert.Equal(2, cells[0].Length);
        Assert.Equal(0, cells[0].Column);
        Assert.Equal(1, cells[1].Column);
    }

    [Fact]
    public void SplitCells_EscapedPipeIsContentNotASeparator()
    {
        var line = @"| a \| b | c |";
        var cells = MarkdownTableParser.SplitCells(line, 0);

        Assert.Equal(2, cells.Count);
        Assert.Equal(@"a \| b", line[cells[0].Start..cells[0].End]);
    }

    [Fact]
    public void SplitCells_EmptyCellsKeepTheirPlace()
    {
        var line = "|  | b |";
        var cells = MarkdownTableParser.SplitCells(line, 0);

        Assert.Equal(2, cells.Count);
        Assert.Equal(0, cells[0].Length);
        Assert.Equal("b", line[cells[1].Start..cells[1].End]);
    }

    [Theory]
    [InlineData("| --- | --- |")]
    [InlineData("|---|---|")]
    [InlineData("| :--- | ---: |")]
    [InlineData("- | -")]
    public void ParseDelimiterRow_AcceptsTheGfmForms(string line)
    {
        Assert.Equal(2, MarkdownTableParser.ParseDelimiterRow(line)?.Count);
    }

    [Theory]
    [InlineData("| a | b |")]
    [InlineData("| --- | b |")]
    [InlineData("| -- - | --- |")]
    [InlineData("|  | --- |")]
    [InlineData("| :: | --- |")]
    public void ParseDelimiterRow_RejectsAnythingThatIsNotOnlyDashesAndColons(string line)
    {
        Assert.Null(MarkdownTableParser.ParseDelimiterRow(line));
    }

    [Fact]
    public void Scan_ListItemWithPipes_StaysAList()
    {
        Assert.Empty(Scan("- a | b\n- --- | ---\n"));
    }

    [Fact]
    public void Scan_QuotedTable_IsNotTreatedAsATable()
    {
        // Una cita con una tabla dentro sigue siendo una cita línea a línea para este parser: reconocerla
        // como tabla dibujaría la rejilla ignorando el carril de la cita.
        Assert.Empty(Scan("> | a | b |\n> | --- | --- |\n"));
    }

    [Fact]
    public void Scan_DocumentWithoutTables_ReturnsNothing()
    {
        Assert.Empty(Scan("# Título\n\nUn párrafo normal.\n\n- lista\n"));
    }

    [Fact]
    public void Scan_LinesAfterATable_AreScannedForMoreTables()
    {
        var rows = Scan("Texto\n| A |\n| --- |\n| 1 |\n");

        Assert.False(rows.ContainsKey(1));
        Assert.Equal(MdTableRowKind.Header, rows[2].Kind);
        Assert.Equal(4, rows[2].Table.EndLine);
    }

    [Fact]
    public void Scan_EveryLineOfATableSharesTheSameTableInstance()
    {
        var rows = Scan("| A |\n| --- |\n| 1 |\n| 2 |\n");

        Assert.Single(rows.Values.Select(r => r.Table).Distinct());
    }
}
