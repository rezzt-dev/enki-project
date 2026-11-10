using enki.Services.Export;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="DocumentTableRules"/> — qué columnas de una tabla del PDF se tratan como cifras
/// y qué fila como total. Lo que se protege es que la regla sea conservadora: ante la duda, la tabla sale
/// como en el editor.</summary>
public class DocumentTableRulesTests
{
    [Theory]
    [InlineData("12")]
    [InlineData("1.234,56 €")]
    [InlineData("-12 %")]
    [InlineData("91,6%")]
    [InlineData("$40")]
    [InlineData("+3")]
    [InlineData("1 250")]
    public void IsNumeric_AcceptsFiguresWithSeparatorsAndSymbols(string text) =>
        Assert.True(DocumentTableRules.IsNumeric(text));

    [Theory]
    [InlineData("")]
    [InlineData("12/03/2025")]
    [InlineData("10:30")]
    [InlineData("A-12")]
    [InlineData("12 kg")]
    [InlineData("fila 3")]
    [InlineData("—")]
    [InlineData("12.")]
    public void IsNumeric_RejectsDatesCodesAndText(string text) =>
        Assert.False(DocumentTableRules.IsNumeric(text));

    [Fact]
    public void IsNumericColumn_IgnoresEmptyCells_ButNeedsAtLeastOneFigure()
    {
        Assert.True(DocumentTableRules.IsNumericColumn(["12", "", "3,5 €"]));
        Assert.False(DocumentTableRules.IsNumericColumn(["12", "n/d"]));
        Assert.False(DocumentTableRules.IsNumericColumn(["", ""]));
        Assert.False(DocumentTableRules.IsNumericColumn([]));
    }

    [Theory]
    [InlineData("Total", true)]
    [InlineData("TOTAL:", true)]
    [InlineData("Total general", true)]
    [InlineData("Totales", true)]
    [InlineData("Subtotal", true)]
    [InlineData("Totalmente", false)]
    [InlineData("Suma", false)]
    [InlineData("El total", false)]
    public void IsTotalLabel_MatchesOnlyAWholeLeadingWord(string text, bool expected) =>
        Assert.Equal(expected, DocumentTableRules.IsTotalLabel(text));

    [Theory]
    [InlineData("**Total**", "Total")]
    [InlineData(" `12` ", "12")]
    [InlineData("*1.200 €*", "1.200 €")]
    [InlineData("==91,6 %==", "91,6 %")]
    public void PlainText_DropsTheEmphasisMarkersAroundACell(string cell, string expected) =>
        Assert.Equal(expected, DocumentTableRules.PlainText(cell));
}
