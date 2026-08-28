using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using SkiaSharp;
using Xunit;

namespace enki.UITests;

/// <summary>
/// Comprueba, contra el <see cref="EditorView"/> real compilado, que una imagen escrita en una nota se
/// dibuja de verdad dentro del texto — no solo que el parser la reconoce. Es la única capa donde se puede
/// verificar lo que de esta funcionalidad es puramente visual: que el <c>![](…)</c> desaparece, que en su
/// sitio aparece un <see cref="Image"/> con el tamaño calculado, que la línea crece para alojarlo, y que
/// poner el cursor encima devuelve el Markdown en crudo para poder editarlo.
///
/// <para>La carga de imágenes es asíncrona a propósito (ver <c>MarkdownImageCache</c>), así que estas
/// pruebas bombean el dispatcher y repiten el layout hasta que la imagen aparece, en vez de asumir que
/// está lista en el primer pase.</para>
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class EditorViewImagesUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _vaultDir;

    public EditorViewImagesUiTests()
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

    /// <summary>Un PNG real de <paramref name="width"/>×<paramref name="height"/>, para que lo que se
    /// prueba sea la decodificación de verdad y no un camino de error disfrazado.</summary>
    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void Pump(Window window, Func<bool> until, int timeoutMs = 5000)
    {
        var layout = window.GetLayoutManager()!;
        var stopwatch = Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            layout.ExecuteLayoutPass();
            if (until()) return;
            Thread.Sleep(10);
        } while (stopwatch.ElapsedMilliseconds < timeoutMs);
    }

    private static TextEditor FindEditor(NotesView view) =>
        view.GetVisualDescendants().OfType<TextEditor>().First();

    private static Image[] RenderedImages(TextEditor editor) =>
        editor.TextArea.TextView.GetVisualChildren().OfType<Image>().ToArray();

    /// <summary>Deja la nota abierta con <paramref name="content"/> y el cursor al final del documento —
    /// es decir, fuera del span de la imagen, que es la condición para que el live preview la dibuje.</summary>
    private (TextEditor Editor, EditorViewModel Vm) OpenNoteWith(
        NotesView view, NotesViewModel viewModel, Window window, string content, byte[]? image, string imageName)
    {
        viewModel.NewNoteCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var vm = viewModel.ActiveEditor!;
        if (image is not null)
        {
            var directory = Path.GetDirectoryName(vm.Note.FullPath)!;
            File.WriteAllBytes(Path.Combine(directory, imageName), image);
        }

        var editor = FindEditor(view);
        editor.Document.Text = content;
        editor.CaretOffset = editor.Document.TextLength;
        window.GetLayoutManager()!.ExecuteLayoutPass();
        return (editor, vm);
    }

    [Fact]
    public void ImageInNote_IsRenderedInlineAtItsNaturalSize()
    {
        var (view, viewModel, window) = CreateView();
        var (editor, _) = OpenNoteWith(view, viewModel, window, "![](captura.png)\n", CreatePng(120, 60), "captura.png");

        Pump(window, () => RenderedImages(editor).Length > 0);

        var image = Assert.Single(RenderedImages(editor));
        Assert.Equal(120, image.Width);
        Assert.Equal(60, image.Height);

        window.Close();
    }

    [Fact]
    public void RenderedImage_AnnouncesTheAltTextWrittenInTheNote()
    {
        var (view, viewModel, window) = CreateView();
        var (editor, _) = OpenNoteWith(view, viewModel, window, "![diagrama de flujo](captura.png)\n",
            CreatePng(120, 60), "captura.png");

        Pump(window, () => RenderedImages(editor).Length > 0);

        var image = Assert.Single(RenderedImages(editor));
        Assert.Equal("diagrama de flujo", Avalonia.Automation.AutomationProperties.GetName(image));

        window.Close();
    }

    [Fact]
    public void ImageWiderThanTheColumn_IsScaledDownKeepingItsAspectRatio()
    {
        var (view, viewModel, window) = CreateView();
        var (editor, _) = OpenNoteWith(view, viewModel, window, "![](ancha.png)\n", CreatePng(4000, 2000), "ancha.png");

        Pump(window, () => RenderedImages(editor).Length > 0);

        var image = Assert.Single(RenderedImages(editor));
        Assert.True(image.Width < 4000, $"la imagen debería reducirse al ancho de la columna, pero mide {image.Width}");
        Assert.True(image.Width <= editor.TextArea.TextView.Bounds.Width);
        Assert.Equal(image.Width / 2, image.Height, precision: 3);

        window.Close();
    }

    [Fact]
    public void ImageWithExplicitWidth_UsesIt()
    {
        var (view, viewModel, window) = CreateView();
        var (editor, _) = OpenNoteWith(view, viewModel, window, "![|300](captura.png)\n", CreatePng(120, 60), "captura.png");

        Pump(window, () => RenderedImages(editor).Length > 0);

        var image = Assert.Single(RenderedImages(editor));
        Assert.Equal(300, image.Width);
        Assert.Equal(150, image.Height);

        window.Close();
    }

    [Fact]
    public void RenderedImage_MakesItsLineTallerThanAPlainTextLine()
    {
        var (view, viewModel, window) = CreateView();
        var (editor, _) = OpenNoteWith(view, viewModel, window, "![](captura.png)\n", CreatePng(200, 400), "captura.png");

        Pump(window, () => RenderedImages(editor).Length > 0);

        var textView = editor.TextArea.TextView;
        var imageLine = textView.VisualLines.First(line => line.FirstDocumentLine.LineNumber == 1);

        // Si la línea no creciera, la imagen se dibujaría encima del texto de las líneas siguientes. El
        // umbral es el alto real de la imagen (400 px), no un múltiplo del alto de línea: así la prueba no
        // puede pasar por accidente con el hueco de "cargando", que mide solo dos líneas.
        Assert.True(imageLine.Height >= 400,
            $"la línea de la imagen mide {imageLine.Height} y la imagen 400");

        window.Close();
    }

    [Fact]
    public void CaretInsideTheImageSpan_ShowsTheRawMarkdownAgain()
    {
        var (view, viewModel, window) = CreateView();
        var (editor, _) = OpenNoteWith(view, viewModel, window, "![](captura.png)\n", CreatePng(120, 60), "captura.png");

        Pump(window, () => RenderedImages(editor).Length > 0);
        Assert.Single(RenderedImages(editor));

        // Cursor dentro del `![](…)`: el editor devuelve el Markdown en crudo para poder editarlo, igual
        // que hace con un enlace o con una negrita.
        editor.CaretOffset = 4;
        Pump(window, () => RenderedImages(editor).Length == 0);

        Assert.Empty(RenderedImages(editor));

        window.Close();
    }

    [Fact]
    public void MissingImage_LeavesAVisiblePlaceholderInsteadOfNothing()
    {
        var (view, viewModel, window) = CreateView();
        var (editor, _) = OpenNoteWith(view, viewModel, window, "![](no-existe.png)\n", image: null, imageName: "");

        var textView = editor.TextArea.TextView;
        Pump(window, () => textView.GetVisualChildren().OfType<Border>().Any());

        // El hueco es un Border con el nombre del archivo: una imagen que falta tiene que verse como tal,
        // no desaparecer sin dejar rastro de que la nota pedía algo ahí.
        var placeholder = Assert.Single(textView.GetVisualChildren().OfType<Border>());
        var label = placeholder.GetVisualDescendants().OfType<TextBlock>().First();
        Assert.Equal("no-existe.png", label.Text);

        window.Close();
    }
}
