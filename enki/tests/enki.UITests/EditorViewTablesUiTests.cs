using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>
/// Comprueba contra el <see cref="EditorView"/> real que una tabla Markdown se dibuja como tabla: que las
/// celdas de una misma columna caen en la misma X aunque su texto mida distinto, que la fila separadora
/// desaparece plegada en vez de dejar una banda vacía, que las filas ganan aire vertical para la rejilla,
/// y que poner el cursor dentro devuelve la tabla a su forma de texto para poder editarla. Nada de esto
/// se puede verificar sin una plataforma Avalonia levantada: son medidas de tipografía real.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class EditorViewTablesUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _vaultDir;

    public EditorViewTablesUiTests()
    {
        HeadlessApp.EnsureInitialized();
        _vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(_vaultDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private (NotesView View, NotesViewModel ViewModel, Window Window) CreateView()
    {
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.SetVaultPath(_vaultDir);
        var session = new SessionService(vault);
        var viewModel = new NotesViewModel(vault, session, settingsService: settings);

        var view = new NotesView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();

        return (view, viewModel, window);
    }

    private static TextEditor FindEditor(NotesView view) =>
        view.GetVisualDescendants().OfType<TextEditor>().First();

    /// <summary>Deja la nota abierta con <paramref name="content"/> y el cursor donde se pida — por
    /// defecto al final del documento, es decir fuera de la tabla, que es la condición para que se
    /// dibuje.</summary>
    private TextEditor OpenNoteWith(NotesView view, NotesViewModel viewModel, Window window, string content,
        int? caretOffset = null)
    {
        viewModel.NewNoteCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var editor = FindEditor(view);
        editor.Document.Text = content;
        editor.CaretOffset = caretOffset ?? editor.Document.TextLength;
        // Las tablas se reescanean con el mismo debounce que las cercas de código, así que el escaneo se
        // fuerza aquí en vez de esperar al temporizador: lo que se está probando es el dibujo.
        Rescan(editor);
        window.GetLayoutManager()!.ExecuteLayoutPass();
        return editor;
    }

    private static void Rescan(TextEditor editor)
    {
        var state = LivePreviewStateOf(editor);
        var text = editor.Text ?? string.Empty;
        state.Fences = enki.Views.Editor.Markdown.MarkdownLineParser.ComputeFences(text);
        state.Tables.SetRows(enki.Views.Editor.Markdown.MarkdownTableParser.Scan(text, state.Fences));
        state.TableFolding!.Refresh();
        editor.TextArea.TextView.Redraw();
    }

    /// <summary>El estado del live preview lo crea EditorView y no lo expone; se recupera del propio
    /// generador de elementos registrado en el TextView, que es donde acaba.</summary>
    private static enki.Views.Editor.Markdown.LivePreviewState LivePreviewStateOf(TextEditor editor)
    {
        var generator = editor.TextArea.TextView.ElementGenerators
            .OfType<enki.Views.Editor.Markdown.MarkdownElementGenerator>().First();
        var field = typeof(enki.Views.Editor.Markdown.MarkdownElementGenerator)
            .GetField("_state", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (enki.Views.Editor.Markdown.LivePreviewState)field.GetValue(generator)!;
    }

    /// <summary>La geometría ya calculada de la tabla que empieza en la línea 1, tal y como la ven los tres
    /// renderizadores (el estilo sale del propio generador, que es quien lo tiene).</summary>
    private static enki.Views.Editor.Markdown.MdTableGeometry GeometryOfFirstTable(TextEditor editor)
    {
        var generator = editor.TextArea.TextView.ElementGenerators
            .OfType<enki.Views.Editor.Markdown.MarkdownElementGenerator>().First();
        var style = (enki.Views.Editor.Markdown.MarkdownStyle)typeof(enki.Views.Editor.Markdown.MarkdownElementGenerator)
            .GetField("_style", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(generator)!;
        var state = LivePreviewStateOf(editor);
        return state.Tables.GetGeometry(state.Tables.GetRow(1)!.Value.Table, editor.TextArea.TextView, style, state)!;
    }

    private static VisualLine VisualLineOf(TextEditor editor, int lineNumber) =>
        editor.TextArea.TextView.GetOrConstructVisualLine(editor.Document.GetLineByNumber(lineNumber));

    /// <summary>X en píxeles donde acaba dibujándose el carácter <paramref name="columnInLine"/> (0-based)
    /// de una línea — con los espaciadores de la tabla ya aplicados.</summary>
    private static double XOf(TextEditor editor, int lineNumber, int columnInLine)
    {
        var visualLine = VisualLineOf(editor, lineNumber);
        var visualColumn = visualLine.GetVisualColumn(columnInLine);
        return visualLine.GetTextLineVisualXPosition(visualLine.TextLines[0], visualColumn);
    }

    /// <summary>Índice dentro de la línea del primer carácter de la celda <paramref name="cell"/>.</summary>
    private static int CellStart(TextEditor editor, int lineNumber, int cell)
    {
        var line = editor.Document.GetLineByNumber(lineNumber);
        var text = editor.Document.GetText(line);
        var cells = enki.Views.Editor.Markdown.MarkdownTableParser.SplitCells(text, 0);
        return cells[cell].Start;
    }

    private const string Table =
        "| Fruta | Precio |\n" +
        "| --- | --- |\n" +
        "| Manzana | 2 |\n" +
        "| Kiwi | 14 |\n";

    [Fact]
    public void TableCells_OfTheSameColumn_StartAtTheSameX()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, Table);

        // "Manzana" y "Kiwi" miden distinto, así que sin alinear por columnas el "2" y el "14" caerían en
        // X distintas: que coincidan es exactamente lo que significa "la tabla está dibujada".
        var header = XOf(editor, 1, CellStart(editor, 1, 1));
        var first = XOf(editor, 3, CellStart(editor, 3, 1));
        var second = XOf(editor, 4, CellStart(editor, 4, 1));

        Assert.Equal(header, first, precision: 1);
        Assert.Equal(header, second, precision: 1);
        Assert.True(header > 0, "la segunda columna debería empezar a la derecha del margen");

        window.Close();
    }

    [Fact]
    public void FirstColumn_StartsIndentedByTheCellPadding_NotAtTheLineEdge()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, Table);

        // El texto no puede empezar pegado al borde izquierdo de la tabla: ahí va la línea de la rejilla.
        Assert.True(XOf(editor, 1, CellStart(editor, 1, 0)) > 4);

        window.Close();
    }

    /// <summary>Si la fila separadora está plegada: al pedir su línea visual sale la del encabezado, que
    /// es la que se la ha tragado (ver MarkdownTableFolding).</summary>
    private static bool DelimiterIsFolded(TextEditor editor) =>
        VisualLineOf(editor, 2).FirstDocumentLine.LineNumber == 1;

    [Fact]
    public void DelimiterRow_IsFoldedAwayInsteadOfLeavingAnEmptyBand()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, Table);

        Assert.True(DelimiterIsFolded(editor), "la fila separadora debería estar plegada");
        // Y plegada de verdad: la línea que se la traga mide lo mismo que cualquier otra fila, no el doble.
        Assert.Equal(VisualLineOf(editor, 3).Height, VisualLineOf(editor, 1).Height, precision: 1);

        window.Close();
    }

    [Fact]
    public void TableRows_GetVerticalPaddingSoTheGridIsNotGluedToTheText()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, Table + "\nTexto normal.\n");

        var row = VisualLineOf(editor, 3).Height;
        var plain = VisualLineOf(editor, 6).Height;

        Assert.True(row > plain, $"una fila de tabla mide {row} y una línea normal {plain}");

        window.Close();
    }

    [Fact]
    public void CaretInsideTheTable_ShowsTheRawMarkdownAgain()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, Table, caretOffset: 3);

        // Con el cursor dentro, la tabla vuelve a ser texto editable: la fila separadora recupera su
        // propia línea y la primera celda empieza justo tras la barra de apertura, sin espaciador alguno.
        Assert.False(DelimiterIsFolded(editor));
        Assert.Equal(XOf(editor, 1, 1) + CharWidth(editor), XOf(editor, 1, CellStart(editor, 1, 0)), precision: 0);

        window.Close();
    }

    private static double CharWidth(TextEditor editor) => XOf(editor, 1, 2) - XOf(editor, 1, 1);

    [Fact]
    public void CaretOnTheLineBelow_LeavesTheTableRendered()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, Table + "\nTexto normal.\n");
        editor.CaretOffset = editor.Document.GetLineByNumber(6).Offset + 2;
        LivePreviewStateOf(editor).TableFolding!.Refresh();
        editor.TextArea.TextView.Redraw();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.True(DelimiterIsFolded(editor));

        window.Close();
    }

    [Fact]
    public void TableWithTooManyColumnsToEvenFitAMinimumWidth_StaysAsPlainText()
    {
        var (view, viewModel, window) = CreateView();
        var columns = string.Concat(Enumerable.Repeat("| dato ", 20));
        var delimiter = string.Concat(Enumerable.Repeat("| --- ", 20));
        var editor = OpenNoteWith(view, viewModel, window,
            $"{columns}|\n{delimiter}|\n{columns}|\n");

        // El único caso que sigue quedando en crudo: tantas columnas que ninguna llega al ancho mínimo.
        // Por debajo de él una celda envolvería una o dos letras por línea, que se lee peor que el
        // Markdown sin dibujar. Una tabla que simplemente es larga sí se dibuja, envolviendo.
        Assert.False(DelimiterIsFolded(editor));

        window.Close();
    }

    [Fact]
    public void CenteredColumn_CentersTheShorterCellsWithinTheirColumn()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window,
            "| a | Encabezado largo |\n| --- | :---: |\n| b | x |\n");

        var header = XOf(editor, 1, CellStart(editor, 1, 1));
        var body = XOf(editor, 3, CellStart(editor, 3, 1));

        Assert.True(body > header + 10,
            $"la celda corta debería quedar centrada en su columna (encabezado en {header}, celda en {body})");

        window.Close();
    }

    [Fact]
    public void RightAlignedColumn_PushesTheCellToTheEndOfItsColumn()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window,
            "| a | Encabezado largo |\n| --- | ---: |\n| b | x |\n");

        var centered = XOf(editor, 3, CellStart(editor, 3, 1));
        var header = XOf(editor, 1, CellStart(editor, 1, 1));

        Assert.True(centered > header + 20, "la celda debería quedar pegada al final de su columna");

        window.Close();
    }

    [Fact]
    public void PlainTextWithPipes_IsNotTouched()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, "uno | dos\ntres | cuatro\n");

        // Sin fila separadora no hay tabla, así que la segunda línea sigue siendo suya y con su alto.
        Assert.Equal(2, VisualLineOf(editor, 2).FirstDocumentLine.LineNumber);
        Assert.Equal(VisualLineOf(editor, 1).Height, VisualLineOf(editor, 2).Height, precision: 1);

        window.Close();
    }

    [Fact]
    public void BoldInsideACell_KeepsTheColumnAligned()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window,
            "| Estado | Nota |\n| --- | --- |\n| **hecho** | ok |\n| pendiente | ok |\n");

        // Los asteriscos se ocultan, así que la columna tiene que medirse por lo que se ve, no por el
        // texto crudo: si no, "ok" bailaría entre las dos filas.
        Assert.Equal(XOf(editor, 3, CellStart(editor, 3, 1)), XOf(editor, 4, CellStart(editor, 4, 1)), precision: 1);

        window.Close();
    }

    private static void PressKey(TextEditor editor, Avalonia.Input.Key key,
        Avalonia.Input.KeyModifiers modifiers = Avalonia.Input.KeyModifiers.None)
    {
        editor.TextArea.RaiseEvent(new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
            Source = editor.TextArea
        });
    }

    [Fact]
    public void EnterOnATableRow_AddsAnotherRowWithTheSameColumns()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, Table,
            caretOffset: 0);
        editor.CaretOffset = editor.Document.GetLineByNumber(3).EndOffset;
        Rescan(editor);

        PressKey(editor, Avalonia.Input.Key.Enter);

        Assert.Equal("|  |  |", editor.Document.GetText(editor.Document.GetLineByNumber(4)));
        // El cursor cae dentro de la primera celda, listo para escribir.
        Assert.Equal(editor.Document.GetLineByNumber(4).Offset + 2, editor.CaretOffset);

        window.Close();
    }

    [Fact]
    public void EnterOnAnEmptyTableRow_RemovesItInsteadOfAddingAnother()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, Table + "|  |  |\n", caretOffset: 0);
        editor.CaretOffset = editor.Document.GetLineByNumber(5).Offset + 2;
        Rescan(editor);

        PressKey(editor, Avalonia.Input.Key.Enter);

        Assert.Equal(string.Empty, editor.Document.GetText(editor.Document.GetLineByNumber(5)));

        window.Close();
    }

    [Fact]
    public void EnterOnTheHeaderRow_AddsTheRowBelowTheDelimiter_NotBetweenThem()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, "| A | B |\n| --- | --- |\n", caretOffset: 0);
        editor.CaretOffset = 4;
        Rescan(editor);

        PressKey(editor, Avalonia.Input.Key.Enter);

        // Una fila entre el encabezado y la separadora dejaría de ser una tabla.
        Assert.Equal("| --- | --- |", editor.Document.GetText(editor.Document.GetLineByNumber(2)));
        Assert.Equal("|  |  |", editor.Document.GetText(editor.Document.GetLineByNumber(3)));

        window.Close();
    }

    [Fact]
    public void TabInsideATable_JumpsToTheNextCell()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, Table, caretOffset: 0);
        var line = editor.Document.GetLineByNumber(3);
        editor.CaretOffset = line.Offset + 2;
        Rescan(editor);

        PressKey(editor, Avalonia.Input.Key.Tab);

        Assert.Equal(line.Offset + "| Manzana | ".Length, editor.CaretOffset);

        window.Close();
    }

    [Fact]
    public void ShiftTabInsideATable_JumpsToThePreviousCell()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, Table, caretOffset: 0);
        var line = editor.Document.GetLineByNumber(3);
        editor.CaretOffset = line.Offset + "| Manzana | ".Length;
        Rescan(editor);

        PressKey(editor, Avalonia.Input.Key.Tab, Avalonia.Input.KeyModifiers.Shift);

        Assert.Equal(line.Offset + 2, editor.CaretOffset);

        window.Close();
    }

    [Fact]
    public void TabOnTheLastCellOfTheLastRow_AddsANewRow()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, Table, caretOffset: 0);
        editor.CaretOffset = editor.Document.GetLineByNumber(4).EndOffset - 2;
        Rescan(editor);

        PressKey(editor, Avalonia.Input.Key.Tab);

        Assert.Equal("|  |  |", editor.Document.GetText(editor.Document.GetLineByNumber(5)));

        window.Close();
    }

    [Fact]
    public void TabOutsideATable_IsLeftAlone()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, "texto normal\n", caretOffset: 0);

        PressKey(editor, Avalonia.Input.Key.Tab);

        // El tabulador fuera de una tabla lo sigue gestionando AvaloniaEdit (indentar), no este código.
        Assert.Equal("texto normal", editor.Document.GetText(editor.Document.GetLineByNumber(1)));
        Assert.Equal(0, editor.CaretOffset);

        window.Close();
    }

    [Fact]
    public void TableToolbarButton_InsertsATableTemplate()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, string.Empty);

        var button = view.GetVisualDescendants().OfType<Button>().First(b =>
            Avalonia.Automation.AutomationProperties.GetName(b) ==
            enki.Localization.LocalizationService.Instance["Editor_Toolbar_Table_Tooltip"]);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        var text = editor.Document.Text;
        Assert.Contains("| --- | --- |", text);
        Assert.Equal(3, text.TrimEnd('\n').Split('\n').Length);

        window.Close();
    }

    [Fact]
    public void Table_AlwaysSpansTheFullWidthOfTheTextColumn()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, Table);

        var geometry = GeometryOfFirstTable(editor);

        // Una tabla no se dimensiona por su contenido sino por la columna: el borde derecho cae justo
        // donde acaba el texto de cualquier párrafo.
        Assert.Equal(editor.TextArea.TextView.Bounds.Width, geometry.TotalWidth, precision: 1);

        window.Close();
    }

    [Fact]
    public void StretchedColumns_KeepTheProportionOfTheirContent()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window,
            "| Sí | Una nota bastante más larga que la otra columna |\n| --- | --- |\n| no | otra nota igual de larga que la de arriba |\n");

        var geometry = GeometryOfFirstTable(editor);

        // El hueco sobrante se reparte en proporción al contenido: la columna de las notas se lo lleva,
        // no se reparte a partes iguales dejando la del "sí/no" igual de ancha.
        Assert.True(geometry.ColumnWidths[1] > geometry.ColumnWidths[0] * 2,
            $"columnas de {geometry.ColumnWidths[0]} y {geometry.ColumnWidths[1]}");

        window.Close();
    }

    [Fact]
    public void EmptyTable_StillSpansTheFullWidth_WithEvenColumns()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, "|  |  |\n| --- | --- |\n|  |  |\n");

        var geometry = GeometryOfFirstTable(editor);

        Assert.Equal(editor.TextArea.TextView.Bounds.Width, geometry.TotalWidth, precision: 1);
        // Sin contenido no hay proporción que respetar: las columnas salen iguales.
        Assert.Equal(geometry.ColumnWidths[0], geometry.ColumnWidths[1], precision: 1);

        window.Close();
    }

    /// <summary>Texto de celda que no cabe de una vez en la columna de texto del editor, con el que se
    /// provoca la envoltura en las pruebas de abajo.</summary>
    private const string LongCell =
        "porridge: 100 g avena, 250 ml bebida de avena, 120 g platano, 15 g crema de cacahuete, " +
        "10 g pipas de girasol y un poco mas de texto para no caber de ninguna manera";

    [Fact]
    public void TableWithACellTooLongForOneLine_IsStillDrawn_Wrapping()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window,
            $"| comida | que comer |\n| --- | --- |\n| desayuno | {LongCell} |\n");

        // Antes esto devolvía null ("no cabe") y la tabla se mostraba como Markdown en crudo: bastaba con
        // que una celda pasara de una línea para perder la tabla entera.
        var geometry = GeometryOfFirstTable(editor);

        Assert.True(geometry.Wraps, "la tabla debería dibujarse con celdas envueltas");
        Assert.Equal(editor.TextArea.TextView.Bounds.Width, geometry.TotalWidth, precision: 1);
        // La columna corta se queda con su ancho natural y la larga se lleva el resto, como en un
        // <table> al 100 % de ancho.
        Assert.True(geometry.ColumnWidths[1] > geometry.ColumnWidths[0] * 2,
            $"columnas de {geometry.ColumnWidths[0]} y {geometry.ColumnWidths[1]}");

        window.Close();
    }

    [Fact]
    public void WrappedRow_IsTallerThanARowThatFitsInOneLine()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window,
            $"| comida | que comer |\n| --- | --- |\n| cena | tortilla |\n| desayuno | {LongCell} |\n");

        var wrapped = VisualLineOf(editor, 4).Height;
        var single = VisualLineOf(editor, 3).Height;

        // Dos líneas de texto donde la otra tiene una: la diferencia es del orden de una línea entera,
        // no de un par de píxeles de redondeo.
        Assert.True(wrapped > single + 10,
            $"la fila envuelta mide {wrapped} y la de una línea {single}");

        window.Close();
    }

    /// <summary>La queja que destapó todo esto: añadir un carácter a una celda —un "+" en
    /// "patatas + ketchup"— hacía desaparecer la tabla. No era el carácter, era que la fila cruzaba el
    /// ancho de la columna; el mismo texto sin él sí cabía. Ahora las dos se dibujan.</summary>
    [Fact]
    public void OneMoreCharacterInACell_DoesNotMakeTheTableDisappear()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window,
            $"| comida | que comer |\n| --- | --- |\n| desayuno | {LongCell} + 10 g pipas |\n");

        Assert.NotNull(GeometryOfFirstTable(editor));

        window.Close();
    }

    [Fact]
    public void TableThatFitsInOneLine_DoesNotWrap()
    {
        var (view, viewModel, window) = CreateView();
        var editor = OpenNoteWith(view, viewModel, window, Table);

        Assert.False(GeometryOfFirstTable(editor).Wraps);

        window.Close();
    }
}
