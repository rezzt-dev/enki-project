using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using enki.Services.Export;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="DocumentPaginator"/> — las reglas de composición del PDF de una nota. Son
/// pruebas sobre geometría pura (alturas y cortes), sin maquetar nada: lo que se garantiza es que nunca se
/// corta por en medio de una línea, que se respetan viudas, huérfanas y encabezados, que la tabla repite
/// su encabezado y que un bloque imposible no deja la paginación colgada.</summary>
public class DocumentPaginatorTests
{
    private const double LineHeight = 16;

    /// <summary>Un párrafo de <paramref name="lines"/> líneas: un corte al principio de cada línea.</summary>
    private static PaginationBlock Paragraph(int lines, double spaceBefore = 0, bool keepWithNext = false) =>
        new(lines * LineHeight, Enumerable.Range(1, lines - 1).Select(i => i * LineHeight).ToList(), [],
            spaceBefore, keepWithNext);

    /// <summary>Una imagen: sin cortes posibles.</summary>
    private static PaginationBlock Picture(double height) => new(height, [], []);

    private static IEnumerable<PageFragment> Fragments(IReadOnlyList<IReadOnlyList<PageFragment>> pages, int block) =>
        pages.SelectMany(p => p).Where(f => f.Block == block);

    [Fact]
    public void Paginate_WithNoBlocks_StillProducesOnePage()
    {
        var pages = DocumentPaginator.Paginate([], 100);

        Assert.Single(pages);
        Assert.Empty(pages[0]);
    }

    [Fact]
    public void Paginate_BlocksThatFit_StayOnOnePage_WithTheirSpacing()
    {
        var pages = DocumentPaginator.Paginate([Paragraph(2), Paragraph(2, spaceBefore: 10)], 200);

        var page = Assert.Single(pages);
        Assert.Equal(0, page[0].Top);
        Assert.Equal(2 * LineHeight + 10, page[1].Top);
    }

    [Fact]
    public void Paginate_NeverCutsInsideALine()
    {
        var block = Paragraph(40);
        var pages = DocumentPaginator.Paginate([block], 100);

        Assert.True(pages.Count > 1);
        foreach (var fragment in Fragments(pages, 0))
        {
            Assert.Equal(0, fragment.From % LineHeight, 6);
            Assert.True(fragment.To % LineHeight < 1e-6 || Math.Abs(fragment.To - block.Height) < 1e-6);
            Assert.True(fragment.Top + fragment.Height <= 100 + 1e-6, "Ningún trozo puede salirse de la página.");
        }
        Assert.Equal(block.Height, Fragments(pages, 0).Sum(f => f.Height), 6);
    }

    /// <summary>Un párrafo de tres líneas que llega al pie con sitio para una sola no se parte 1 + 2 (dejaría
    /// una huérfana) ni 2 + 1 (una viuda): pasa entero a la página siguiente.</summary>
    [Fact]
    public void Paginate_ShortParagraphAtTheBottom_MovesWhole_InsteadOfLeavingAWidowOrOrphan()
    {
        var filler = Paragraph(5);   // 80 de 100
        var pages = DocumentPaginator.Paginate([filler, Paragraph(3)], 100);

        Assert.Equal(2, pages.Count);
        var moved = Assert.Single(Fragments(pages, 1));
        Assert.Equal(0, moved.From);
        Assert.Equal(3 * LineHeight, moved.To);
        Assert.Contains(moved, pages[1]);
    }

    [Fact]
    public void Paginate_SplitParagraph_LeavesAtLeastTwoLinesOnEachSide()
    {
        // 4 de 6 líneas caben, pero 4 + 2 respeta las reglas; con 5 libres quedaría 5 + 1, que no.
        var pages = DocumentPaginator.Paginate([Paragraph(1), Paragraph(6)], 6 * LineHeight);

        var parts = Fragments(pages, 1).ToList();
        Assert.Equal(2, parts.Count);
        Assert.True(parts[0].Height >= 2 * LineHeight);
        Assert.True(parts[1].Height >= 2 * LineHeight);
    }

    [Fact]
    public void Paginate_SpaceBefore_IsDroppedAtTheTopOfAPage()
    {
        var pages = DocumentPaginator.Paginate([Picture(90), Picture(50) with { SpaceBefore = 20 }], 100);

        Assert.Equal(2, pages.Count);
        Assert.Equal(0, pages[1][0].Top);
    }

    /// <summary>Un encabezado que cabe justo al pie pero sin sitio para el arranque de su párrafo pasa con
    /// él a la página siguiente.</summary>
    [Fact]
    public void Paginate_HeadingAtTheBottom_IsKeptWithTheNextBlock()
    {
        var heading = Paragraph(1, keepWithNext: true);
        var pages = DocumentPaginator.Paginate([Picture(70), heading, Paragraph(4)], 100);

        Assert.Equal(2, pages.Count);
        Assert.Contains(pages[1], f => f.Block == 1);
        Assert.Equal(0, pages[1][0].Top);
    }

    [Fact]
    public void Paginate_ContinuedTable_RepeatsItsHeaderRow()
    {
        const double row = 20;
        var table = new PaginationBlock(row * 12, Enumerable.Range(1, 11).Select(i => i * row).ToList(), [],
            RepeatHeight: row);
        var pages = DocumentPaginator.Paginate([table], 100);

        Assert.True(pages.Count > 1);
        foreach (var page in pages.Skip(1))
        {
            Assert.Equal(new PageFragment(0, 0, row, 0), page[0]);
            Assert.Equal(row, page[1].Top);
            Assert.True(page[1].From > row);
        }
    }

    [Fact]
    public void Paginate_ImageTallerThanAPage_IsCutRatherThanLoopingForever()
    {
        var pages = DocumentPaginator.Paginate([Picture(250)], 100);

        Assert.Equal(3, pages.Count);
        Assert.Equal(250, Fragments(pages, 0).Sum(f => f.Height), 6);
    }

    [Fact]
    public void Paginate_RowTallerThanAPage_FallsBackToTheLinesInsideIt()
    {
        var block = new PaginationBlock(300, [], [60, 120, 180, 240]);
        var pages = DocumentPaginator.Paginate([block], 130);

        Assert.All(Fragments(pages, 0).Skip(1), f => Assert.Contains(f.From, new[] { 60.0, 120, 180, 240 }));
    }

    [Theory]
    [InlineData("es-ES", 595.28)]
    [InlineData("de-DE", 595.28)]
    [InlineData("en-GB", 595.28)]
    [InlineData("fr-FR", 595.28)]
    [InlineData("en-US", 612)]
    [InlineData("es-419", 612)]
    public void PageSetup_FollowsTheRegionalPaperSize(string culture, double width)
    {
        Assert.Equal(width, NotePageSetup.ForCulture(CultureInfo.GetCultureInfo(culture)).Width);
    }

    /// <summary>Los márgenes «Normal» de Word: 2,5 cm arriba y abajo y 3 cm a los lados en A4; 1" en Carta.</summary>
    [Fact]
    public void PageSetup_UsesWordNormalMargins()
    {
        var a4 = NotePageSetup.A4;
        Assert.Equal(70.87, a4.MarginTop, 2);
        Assert.Equal(85.04, a4.MarginLeft, 2);
        Assert.Equal(72, NotePageSetup.Letter.MarginLeft);
        Assert.Equal(a4.Width - 2 * 85.04, a4.ContentWidth, 1);
    }
}
