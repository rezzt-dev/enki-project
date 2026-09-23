using AvaloniaEdit.Document;
using enki.Views.Editor.Markdown;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Las operaciones de edición Markdown del editor sobre un documento pelado: alternar formato,
/// marcadores de bloque, sangría y numeración de listas, y las operaciones de línea.</summary>
public class MarkdownEditingTests
{
    private static TextDocument Doc(string text) => new(text);

    // ===================== Formato en línea =====================

    [Fact]
    public void ToggleInline_WrapsTheSelectionAndLeavesItSelected()
    {
        var doc = Doc("hola mundo");
        var result = MarkdownEditing.ToggleInline(doc, new MdSelection(5, 5), "**");

        Assert.Equal("hola **mundo**", doc.Text);
        Assert.Equal("mundo", doc.GetText(result.Start, result.Length));
    }

    [Fact]
    public void ToggleInline_OnAlreadyFormattedSelection_RemovesTheMarkers()
    {
        var doc = Doc("hola **mundo**");
        var result = MarkdownEditing.ToggleInline(doc, new MdSelection(5, 9), "**");

        Assert.Equal("hola mundo", doc.Text);
        Assert.Equal("mundo", doc.GetText(result.Start, result.Length));
    }

    /// <summary>El caso que más se da: se selecciona la palabra (sin los asteriscos, que están ocultos en
    /// la vista previa) y se pulsa el atajo para quitarle la negrita.</summary>
    [Fact]
    public void ToggleInline_WithMarkersJustOutsideTheSelection_RemovesThem()
    {
        var doc = Doc("hola **mundo**");
        var result = MarkdownEditing.ToggleInline(doc, new MdSelection(7, 5), "**");

        Assert.Equal("hola mundo", doc.Text);
        Assert.Equal("mundo", doc.GetText(result.Start, result.Length));
    }

    [Fact]
    public void ToggleInline_WithoutSelection_WrapsTheWordUnderTheCaret()
    {
        var doc = Doc("hola mundo");
        MarkdownEditing.ToggleInline(doc, MdSelection.Caret(7), "_");

        Assert.Equal("hola _mundo_", doc.Text);
    }

    [Fact]
    public void ToggleInline_WithCaretInsideAFormattedWord_RemovesTheMarkers()
    {
        var doc = Doc("hola _mundo_ ya");
        MarkdownEditing.ToggleInline(doc, MdSelection.Caret(8), "_");

        Assert.Equal("hola mundo ya", doc.Text);
    }

    [Fact]
    public void ToggleInline_OnEmptySpot_LeavesTheCaretBetweenTheMarkers()
    {
        var doc = Doc("hola ");
        var result = MarkdownEditing.ToggleInline(doc, MdSelection.Caret(5), "**");

        Assert.Equal("hola ****", doc.Text);
        Assert.Equal(7, result.Start);
        Assert.Equal(0, result.Length);
    }

    // ===================== Encabezados y listas =====================

    [Fact]
    public void ToggleHeading_ReplacesTheExistingLevelInsteadOfStacking()
    {
        var doc = Doc("# Título");
        MarkdownEditing.ToggleHeading(doc, MdSelection.Caret(3), 2);

        Assert.Equal("## Título", doc.Text);
    }

    [Fact]
    public void ToggleHeading_WithTheSameLevel_RemovesIt()
    {
        var doc = Doc("## Título");
        MarkdownEditing.ToggleHeading(doc, MdSelection.Caret(4), 2);

        Assert.Equal("Título", doc.Text);
    }

    [Fact]
    public void ToggleList_ConvertsBetweenListKindsWithoutStackingMarkers()
    {
        var doc = Doc("- uno");
        MarkdownEditing.ToggleList(doc, MdSelection.Caret(3), MdBlockKind.TaskList);
        Assert.Equal("- [ ] uno", doc.Text);

        MarkdownEditing.ToggleList(doc, MdSelection.Caret(7), MdBlockKind.BulletList);
        Assert.Equal("- uno", doc.Text);

        MarkdownEditing.ToggleList(doc, MdSelection.Caret(3), MdBlockKind.BulletList);
        Assert.Equal("uno", doc.Text);
    }

    [Fact]
    public void ToggleList_OrderedOverSeveralLines_NumbersThemInSequence()
    {
        var doc = Doc("uno\ndos\ntres");
        MarkdownEditing.ToggleList(doc, new MdSelection(0, doc.TextLength), MdBlockKind.OrderedList);

        Assert.Equal("1. uno\n2. dos\n3. tres", doc.Text);
    }

    [Fact]
    public void ToggleQuote_AddsALevelAndTakesItBack()
    {
        var doc = Doc("uno\ndos");
        MarkdownEditing.ToggleQuote(doc, new MdSelection(0, doc.TextLength));
        Assert.Equal("> uno\n> dos", doc.Text);

        MarkdownEditing.ToggleQuote(doc, new MdSelection(0, doc.TextLength));
        Assert.Equal("uno\ndos", doc.Text);
    }

    /// <summary>Con una línea citada y otra no, el gesto las iguala citándolas — nunca sube unas y baja
    /// otras.</summary>
    [Fact]
    public void ToggleQuote_OnAMixedSelection_QuotesEverything()
    {
        var doc = Doc("> uno\ndos");
        MarkdownEditing.ToggleQuote(doc, new MdSelection(0, doc.TextLength));

        Assert.Equal("> > uno\n> dos", doc.Text);
    }

    // ===================== Sangría y numeración =====================

    [Fact]
    public void IndentList_NestsTheItemAndRenumbersBothLevels()
    {
        var doc = Doc("1. uno\n2. dos\n3. tres");
        var result = MarkdownEditing.IndentList(doc, MdSelection.Caret(10), tabSize: 2, outdent: false);

        Assert.NotNull(result);
        Assert.Equal("1. uno\n  2. dos\n2. tres", doc.Text);
    }

    [Fact]
    public void IndentList_Outdent_RestoresTheLevelAndTheNumbering()
    {
        var doc = Doc("1. uno\n  1. dos\n2. tres");
        MarkdownEditing.IndentList(doc, MdSelection.Caret(12), tabSize: 2, outdent: true);

        Assert.Equal("1. uno\n2. dos\n3. tres", doc.Text);
    }

    [Fact]
    public void IndentList_OutsideAList_DoesNothingSoTabKeepsIndenting()
    {
        var doc = Doc("texto normal");
        Assert.Null(MarkdownEditing.IndentList(doc, MdSelection.Caret(3), tabSize: 4, outdent: false));
        Assert.Equal("texto normal", doc.Text);
    }

    [Fact]
    public void RenumberOrderedLists_FixesAListWithARepeatedNumber()
    {
        var doc = Doc("1. uno\n1. dos\n1. tres");
        MarkdownEditing.RenumberOrderedLists(doc, 0);

        Assert.Equal("1. uno\n2. dos\n3. tres", doc.Text);
    }

    /// <summary>Una lista que su autor empieza en 3 sigue en 4: renumerar es corregir la serie, no
    /// imponer que toda lista empiece por uno.</summary>
    [Fact]
    public void RenumberOrderedLists_KeepsTheNumberTheListStartsAt()
    {
        var doc = Doc("3. uno\n9. dos");
        MarkdownEditing.RenumberOrderedLists(doc, 0);

        Assert.Equal("3. uno\n4. dos", doc.Text);
    }

    [Fact]
    public void RenumberOrderedLists_StopsAtTheBlankLineThatEndsTheList()
    {
        var doc = Doc("1. uno\n1. dos\n\n5. otra lista\n5. sigue");
        MarkdownEditing.RenumberOrderedLists(doc, 0);

        Assert.Equal("1. uno\n2. dos\n\n5. otra lista\n5. sigue", doc.Text);
    }

    // ===================== Tareas =====================

    [Fact]
    public void ToggleTask_TurnsALineIntoATaskAndThenMarksIt()
    {
        var doc = Doc("comprar pan");
        MarkdownEditing.ToggleTask(doc, MdSelection.Caret(3));
        Assert.Equal("- [ ] comprar pan", doc.Text);

        MarkdownEditing.ToggleTask(doc, MdSelection.Caret(9));
        Assert.Equal("- [x] comprar pan", doc.Text);

        MarkdownEditing.ToggleTask(doc, MdSelection.Caret(9));
        Assert.Equal("- [ ] comprar pan", doc.Text);
    }

    [Fact]
    public void ToggleTask_OverSeveralLines_MarksThemAllOrNone()
    {
        var doc = Doc("- [x] uno\n- [ ] dos");
        MarkdownEditing.ToggleTask(doc, new MdSelection(0, doc.TextLength));
        Assert.Equal("- [x] uno\n- [x] dos", doc.Text);

        MarkdownEditing.ToggleTask(doc, new MdSelection(0, doc.TextLength));
        Assert.Equal("- [ ] uno\n- [ ] dos", doc.Text);
    }

    // ===================== Operaciones de línea =====================

    [Fact]
    public void MoveLines_SwapsWithTheNeighbourAndCarriesTheCaret()
    {
        var doc = Doc("uno\ndos\ntres");
        var result = MarkdownEditing.MoveLines(doc, MdSelection.Caret(5), up: true);

        Assert.Equal("dos\nuno\ntres", doc.Text);
        Assert.Equal(1, result.Start);
    }

    [Fact]
    public void MoveLines_AtTheEdges_LeavesTheDocumentAlone()
    {
        var doc = Doc("uno\ndos");
        Assert.Equal(MdSelection.Caret(1), MarkdownEditing.MoveLines(doc, MdSelection.Caret(1), up: true));
        Assert.Equal(MdSelection.Caret(5), MarkdownEditing.MoveLines(doc, MdSelection.Caret(5), up: false));
        Assert.Equal("uno\ndos", doc.Text);
    }

    [Fact]
    public void DuplicateLines_LeavesTheSelectionOnTheCopy()
    {
        var doc = Doc("uno\ndos");
        var result = MarkdownEditing.DuplicateLines(doc, new MdSelection(0, 3));

        Assert.Equal("uno\nuno\ndos", doc.Text);
        Assert.Equal("uno", doc.GetText(result.Start, result.Length));
        Assert.Equal(4, result.Start);
    }

    [Fact]
    public void DeleteLines_TakesTheLineBreakWithIt()
    {
        var doc = Doc("uno\ndos\ntres");
        MarkdownEditing.DeleteLines(doc, MdSelection.Caret(5));

        Assert.Equal("uno\ntres", doc.Text);
    }

    [Fact]
    public void DeleteLines_OnTheLastLine_DoesNotLeaveAnEmptyOneBehind()
    {
        var doc = Doc("uno\ndos");
        MarkdownEditing.DeleteLines(doc, MdSelection.Caret(5));

        Assert.Equal("uno", doc.Text);
    }

    [Fact]
    public void ExpandToLines_TakesTheWholeLineAndThenTheNextOne()
    {
        var doc = Doc("uno\ndos\ntres");
        var first = MarkdownEditing.ExpandToLines(doc, MdSelection.Caret(1));
        Assert.Equal(new MdSelection(0, 3), first);

        var second = MarkdownEditing.ExpandToLines(doc, first);
        Assert.Equal(new MdSelection(0, 7), second);
    }

    // ===================== Cursor =====================

    [Fact]
    public void SmartHome_GoesToTheContentFirstAndToColumnZeroOnTheSecondPress()
    {
        var doc = Doc("- [ ] tarea");
        var content = MarkdownEditing.SmartHome(doc, 9);
        Assert.Equal(6, content);
        Assert.Equal(0, MarkdownEditing.SmartHome(doc, content));
    }

    [Fact]
    public void BackspaceOverMarker_RemovesTheWholeMarkerFromTheStartOfTheContent()
    {
        var doc = Doc("- [ ] tarea");
        var range = MarkdownEditing.BackspaceOverMarker(doc, 6);

        Assert.Equal(new MdSelection(0, 6), range);
        Assert.Null(MarkdownEditing.BackspaceOverMarker(doc, 8));
    }

    // ===================== Pares y pegado =====================

    [Theory]
    [InlineData('(', ")")]
    [InlineData('*', "*")]
    [InlineData('«', "»")]
    public void ClosingForWrap_CoversBracketsQuotesAndMarkdownMarkers(char opening, string closing) =>
        Assert.Equal(closing, MarkdownEditing.ClosingForWrap(opening));

    /// <summary>El autocierre es más corto a propósito: cerrar solo un <c>*</c> o un <c>_</c> estorbaría
    /// al escribir una multiplicación o un nombre_con_guiones.</summary>
    [Theory]
    [InlineData('(', ")")]
    [InlineData('*', null)]
    [InlineData('_', null)]
    public void ClosingForAutoPair_OnlyClosesBracketsAndQuotes(char opening, string? closing) =>
        Assert.Equal(closing, MarkdownEditing.ClosingForAutoPair(opening));

    [Fact]
    public void ShouldStepOver_OnlyWhenTheSameClosingCharIsAlreadyThere()
    {
        var doc = Doc("(texto)");
        Assert.True(MarkdownEditing.ShouldStepOver(doc, 6, ')'));
        Assert.False(MarkdownEditing.ShouldStepOver(doc, 3, ')'));
    }

    [Theory]
    [InlineData("texto", "https://ejemplo.com", "[texto](https://ejemplo.com)")]
    [InlineData("texto", "no es una url", null)]
    [InlineData("", "https://ejemplo.com", null)]
    [InlineData("texto", "javascript:alert(1)", null)]
    public void LinkFromPaste_OnlyBuildsALinkForARealUrlOverSelectedText(
        string selected, string pasted, string? expected) =>
        Assert.Equal(expected, MarkdownEditing.LinkFromPaste(selected, pasted));

    [Fact]
    public void ToggleHeading_CaretInsideRemovedMarker_SurvivesAndCanUndo()
    {
        var doc = Doc("### Título");
        var result = MarkdownEditing.ToggleHeading(doc, MdSelection.Caret(2), 3);
        Assert.Equal("Título", doc.Text);
        Assert.Equal(MdSelection.Caret(0), result);
        doc.UndoStack.Undo();
        Assert.Equal("### Título", doc.Text);
    }

    [Fact]
    public void ToggleList_MixedSelection_AppliesTheSameKindToEveryLine()
    {
        var doc = Doc("- uno\ndos");
        MarkdownEditing.ToggleList(doc, new MdSelection(0, doc.TextLength), MdBlockKind.BulletList);
        Assert.Equal("- uno\n- dos", doc.Text);
    }

    [Fact]
    public void ToggleList_RenumberAcrossDigitBoundary_KeepsTheWholeSelection()
    {
        var doc = Doc("9. previo\nuno\ndos");
        var start = doc.GetLineByNumber(2).Offset;
        var result = MarkdownEditing.ToggleList(doc, new MdSelection(start, doc.TextLength - start),
            MdBlockKind.OrderedList);
        Assert.Equal("9. previo\n10. uno\n11. dos", doc.Text);
        Assert.Equal("uno\n11. dos", doc.GetText(result.Start, result.Length));
        doc.UndoStack.Undo();
        Assert.Equal("9. previo\nuno\ndos", doc.Text);
    }

    [Theory]
    [InlineData(true, 6, 1)]
    [InlineData(false, 1, 6)]
    public void MoveLines_PreservesWindowsLineEndings(bool up, int caret, int expectedCaret)
    {
        var doc = Doc("uno\r\ndos\r\ntres");
        var result = MarkdownEditing.MoveLines(doc, MdSelection.Caret(caret), up);
        Assert.Equal("dos\r\nuno\r\ntres", doc.Text);
        Assert.Equal(expectedCaret, result.Start);
        doc.UndoStack.Undo();
        Assert.Equal("uno\r\ndos\r\ntres", doc.Text);
    }

    [Fact]
    public void DuplicateLines_AtEnd_PreservesWindowsLineEndingsAndSelection()
    {
        var doc = Doc("uno\r\ndos");
        var result = MarkdownEditing.DuplicateLines(doc, new MdSelection(5, 3));
        Assert.Equal("uno\r\ndos\r\ndos", doc.Text);
        Assert.Equal(new MdSelection(10, 3), result);
    }

    [Fact]
    public void RenumberOrderedLists_DoesNotModifyCodeExamples()
    {
        var doc = Doc("1. uno\n1. dos\n```md\n1. ejemplo\n1. ejemplo\n```");
        MarkdownEditing.RenumberOrderedLists(doc, 0);
        Assert.Equal("1. uno\n2. dos\n```md\n1. ejemplo\n1. ejemplo\n```", doc.Text);
        MarkdownEditing.RenumberOrderedLists(doc, doc.GetLineByNumber(4).Offset);
        Assert.Contains("1. ejemplo\n1. ejemplo", doc.Text);
        Assert.Null(MarkdownEditing.BackspaceOverMarker(doc, doc.GetLineByNumber(4).Offset + 3));
    }

    [Fact]
    public void Backticks_DoNotAutoClose_SoTypingAFenceRemainsPossible()
    {
        Assert.Null(MarkdownEditing.ClosingForAutoPair('`'));
        Assert.Equal("`", MarkdownEditing.ClosingForWrap('`'));
    }

    [Fact]
    public void InsertCodeBlock_PreservesSelectionPositionAndSurroundingText()
    {
        var doc = Doc("antes código después");
        var result = MarkdownEditing.InsertCodeBlock(doc, new MdSelection(6, 6));
        Assert.Equal("antes \n```\ncódigo\n```\n después", doc.Text);
        Assert.Equal(10, result.Start);
        doc.UndoStack.Undo();
        Assert.Equal("antes código después", doc.Text);
    }

    [Fact]
    public void InsertCodeBlock_EscapesNestedFencesAndPreservesLineEndings()
    {
        var doc = Doc("previo\r\n```\r\nejemplo\r\n```");
        var start = doc.GetLineByNumber(2).Offset;
        MarkdownEditing.InsertCodeBlock(doc, new MdSelection(start, doc.TextLength - start));
        Assert.Equal("previo\r\n````\r\n```\r\nejemplo\r\n```\r\n````", doc.Text);
    }

    [Fact]
    public void IndentList_InsideCode_LeavesTheNativeTabHandlerInCharge()
    {
        var doc = Doc("```md\n1. uno\n1. dos\n```");
        var before = doc.Text;
        Assert.Null(MarkdownEditing.IndentList(doc, MdSelection.Caret(9), 4, false));
        Assert.Equal(before, doc.Text);
    }
}
