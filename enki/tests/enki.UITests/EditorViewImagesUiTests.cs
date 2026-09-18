using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
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

    [Theory]
    [InlineData("![](captura.png)", 1)]
    [InlineData("![](captura.png)\nTexto", 1)]
    [InlineData("<div align=\"right\">\n![](captura.png)\n</div>\nTexto", 2)]
    public void Image_IsCenteredWithoutAnExtraVisualLine(string content, int imageLineNumber)
    {
        var (view, viewModel, window) = CreateView();
        try
        {
            var (editor, _) = OpenNoteWith(view, viewModel, window, content, CreatePng(120, 60), "captura.png");
            Pump(window, () => RenderedImages(editor).Length > 0);
            var image = Assert.Single(RenderedImages(editor));
            var textView = editor.TextArea.TextView;
            Assert.InRange(Math.Abs(image.Bounds.Center.X - textView.Bounds.Width / 2), 0, 0.5);
            var line = textView.VisualLines.First(line => line.FirstDocumentLine.LineNumber == imageLineNumber);
            Assert.Single(line.TextLines);
            Assert.True(line.Height < image.Height + textView.DefaultLineHeight,
                $"Alto de imagen: {image.Height}; alto de línea: {line.Height}");

            window.Width = 900;
            Pump(window, () => RenderedImages(editor).Any(current =>
                Math.Abs(current.Bounds.Center.X - textView.Bounds.Width / 2) <= 0.5));
            image = Assert.Single(RenderedImages(editor));
            Assert.InRange(Math.Abs(image.Bounds.Center.X - textView.Bounds.Width / 2), 0, 0.5);
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData("", 0, "![](captura.png)")]
    [InlineData("\nTexto", 0, "![](captura.png)\nTexto")]
    [InlineData("\r\nTexto", 0, "![](captura.png)\r\nTexto")]
    [InlineData("Antes", 5, "Antes\n![](captura.png)")]
    [InlineData("AntesDespues", 5, "Antes\n![](captura.png)\nDespues")]
    public void InsertImage_DoesNotAddAnEmptyLine(string initial, int offset, string expected)
    {
        var (view, viewModel, window) = CreateView();
        try
        {
            var (editor, _) = OpenNoteWith(view, viewModel, window, initial, CreatePng(120, 60), "captura.png");
            var editorView = view.GetVisualDescendants().OfType<EditorView>().First();
            typeof(EditorView).GetMethod("InsertImageReferences",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(editorView, [new System.Collections.Generic.List<string> { "captura.png" }, offset]);
            Assert.Equal(expected, editor.Document.Text);
            Pump(window, () => RenderedImages(editor).Length > 0);
            Assert.Single(RenderedImages(editor));
            editor.Undo();
            Assert.Equal(initial, editor.Document.Text);
        }
        finally { window.Close(); }
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

    // ===================== Pegar del portapapeles =====================

    /// <summary>Deja en el portapapeles un mapa de bits, como una herramienta de recorte. Con
    /// <paramref name="alsoText"/> deja además texto, que es lo que viaja al copiar una imagen en un
    /// navegador o en un procesador de textos — y ahí lo que se pega es el texto.</summary>
    private static async Task CopyBitmapAsync(Window window, byte[] png, string? alsoText = null)
    {
        var transfer = new DataTransfer();
        using var stream = new MemoryStream(png);
        var item = new DataTransferItem();
        item.SetBitmap(new Bitmap(stream));
        transfer.Add(item);
        if (alsoText is not null) transfer.Add(DataTransferItem.CreateText(alsoText));

        await TopLevel.GetTopLevel(window)!.Clipboard!.SetDataAsync(transfer);
    }

    /// <summary>Deja en el portapapeles archivos de imagen, como copiar una imagen en el explorador del
    /// sistema: el mismo <see cref="DataFormat.File"/> que pondría la plataforma, resuelto por el
    /// <see cref="IStorageProvider"/> del propio TopLevel para que <c>TryGetLocalPath</c> devuelva una ruta
    /// de verdad.</summary>
    private static async Task CopyImageFilesAsync(Window window, params string[] paths)
    {
        var transfer = new DataTransfer();
        foreach (var path in paths)
        {
            var file = await window.StorageProvider.TryGetFileFromPathAsync(path);
            Assert.NotNull(file);
            transfer.Add(DataTransferItem.CreateFile(file));
        }
        await TopLevel.GetTopLevel(window)!.Clipboard!.SetDataAsync(transfer);
    }

    private static async Task CopyTextAsync(Window window, string text)
    {
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        await TopLevel.GetTopLevel(window)!.Clipboard!.SetDataAsync(transfer);
    }

    /// <summary>Un archivo de imagen fuera del vault, como el que se copiaría desde el explorador.</summary>
    private string SeedExternalImage(string name, byte[] png)
    {
        var path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(_tempDir);
        File.WriteAllBytes(path, png);
        return path;
    }

    /// <summary>El gesto de pegar tal y como llega del teclado: al <c>TextArea</c>, que es donde
    /// <see cref="EditorView"/> engancha su manejador en fase de captura.</summary>
    private static void PressPaste(TextEditor editor) =>
        editor.TextArea.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.V,
            KeyModifiers = KeyModifiers.Control,
            Source = editor.TextArea
        });

    /// <summary>Archivos de la carpeta de adjuntos de la nota abierta, ordenados.</summary>
    private static string[] AttachmentsOf(EditorViewModel vm)
    {
        var folder = NoteImageAttachments.FolderFor(vm.Note.FullPath)!;
        return Directory.Exists(folder) ? Directory.GetFiles(folder).OrderBy(path => path).ToArray() : [];
    }

    /// <summary>La referencia que la nota debería escribir para un adjunto suyo: relativa a la nota y
    /// escapada como manda <see cref="NoteImageAttachments.MarkdownReference"/> (el vault de pruebas crea
    /// notas con espacio en el nombre, así que la carpeta de adjuntos lo lleva también).</summary>
    private static string ReferenceTo(EditorViewModel vm, string attachment) =>
        NoteImageAttachments.MarkdownReference(vm.Note.FullPath, attachment);

    [Fact]
    public async Task PastingAnImageFile_SavesItAsAnAttachmentAndInsertsItsMarkdown()
    {
        var (view, viewModel, window) = CreateView();
        try
        {
            var (editor, vm) = OpenNoteWith(view, viewModel, window, "Antes", image: null, imageName: "");
            await CopyImageFilesAsync(window, SeedExternalImage("Captura de pantalla.png", CreatePng(120, 60)));

            PressPaste(editor);
            Pump(window, () => editor.Document.Text.Contains("!["));

            // El adjunto va a la carpeta hermana de la nota, con el nombre original saneado —el sistema de
            // adjuntos de siempre—, y con sus bytes intactos.
            var attachment = Assert.Single(AttachmentsOf(vm));
            Assert.Equal("Captura-de-pantalla.png", Path.GetFileName(attachment));
            Assert.Equal(NoteImageAttachments.FolderFor(vm.Note.FullPath), Path.GetDirectoryName(attachment));
            Assert.Equal(CreatePng(120, 60).Length, File.ReadAllBytes(attachment).Length);

            Assert.Equal($"Antes\n![]({ReferenceTo(vm, attachment)})", editor.Document.Text);
        }
        finally { window.Close(); }
    }

    /// <summary>Varias imágenes en el portapapeles entran todas, cada una en su línea — el mismo resultado
    /// que arrastrarlas juntas.</summary>
    [Fact]
    public async Task PastingSeveralImageFiles_InsertsEachOneOnItsOwnLine()
    {
        var (view, viewModel, window) = CreateView();
        try
        {
            var (editor, vm) = OpenNoteWith(view, viewModel, window, string.Empty, image: null, imageName: "");
            await CopyImageFilesAsync(window,
                SeedExternalImage("una.png", CreatePng(120, 60)),
                SeedExternalImage("otra.png", CreatePng(80, 40)));

            PressPaste(editor);
            Pump(window, () => editor.Document.Lines.Count == 2);

            var attachments = AttachmentsOf(vm);
            Assert.Equal(["otra.png", "una.png"], attachments.Select(Path.GetFileName));
            Assert.Equal(
                $"![]({ReferenceTo(vm, attachments[1])})\n![]({ReferenceTo(vm, attachments[0])})",
                editor.Document.Text);
        }
        finally { window.Close(); }
    }

    /// <summary>Una imagen que ya vive dentro del vault se enlaza donde está en vez de duplicarse: pegar en
    /// una nota una captura que ya está en el módulo Archivos no puede dejar dos copias que mantener.</summary>
    [Fact]
    public async Task PastingAnImageAlreadyInTheVault_LinksItInsteadOfCopyingIt()
    {
        var (view, viewModel, window) = CreateView();
        try
        {
            var (editor, vm) = OpenNoteWith(view, viewModel, window, string.Empty, image: null, imageName: "");
            var insideVault = Path.Combine(_vaultDir, "ya-en-el-vault.png");
            File.WriteAllBytes(insideVault, CreatePng(120, 60));

            await CopyImageFilesAsync(window, insideVault);
            PressPaste(editor);
            Pump(window, () => editor.Document.Text.Contains("!["));

            Assert.Empty(AttachmentsOf(vm));
            Assert.Equal($"![]({ReferenceTo(vm, insideVault)})", editor.Document.Text);
        }
        finally { window.Close(); }
    }

    /// <summary>Pegar sobre un texto seleccionado lo sustituye, como cualquier otro pegado — y el borrado y
    /// la inserción son una sola operación, así que un único Ctrl+Z devuelve la nota como estaba.</summary>
    [Fact]
    public async Task PastingAnImageOverASelection_ReplacesItInASingleUndoStep()
    {
        var (view, viewModel, window) = CreateView();
        try
        {
            var (editor, vm) = OpenNoteWith(view, viewModel, window, "borrame", image: null, imageName: "");
            editor.Select(0, editor.Document.TextLength);
            await CopyImageFilesAsync(window, SeedExternalImage("captura.png", CreatePng(120, 60)));

            PressPaste(editor);
            Pump(window, () => editor.Document.Text.Contains("!["));

            var attachment = Assert.Single(AttachmentsOf(vm));
            Assert.Equal($"![]({ReferenceTo(vm, attachment)})", editor.Document.Text);

            editor.Undo();
            Assert.Equal("borrame", editor.Document.Text);
        }
        finally { window.Close(); }
    }

    /// <summary>La intercepción de Ctrl+V no puede cambiar el pegado de siempre: sin imagen en el
    /// portapapeles se pega el texto y no se crea ninguna carpeta de adjuntos.</summary>
    [Fact]
    public async Task PastingText_StillPastesTextAndCreatesNoAttachments()
    {
        var (view, viewModel, window) = CreateView();
        try
        {
            var (editor, vm) = OpenNoteWith(view, viewModel, window, string.Empty, image: null, imageName: "");
            await CopyTextAsync(window, "texto pegado");

            PressPaste(editor);
            Pump(window, () => editor.Document.Text.Length > 0);

            Assert.Equal("texto pegado", editor.Document.Text);
            Assert.Empty(AttachmentsOf(vm));
        }
        finally { window.Close(); }
    }

    /// <summary>Un portapapeles que trae mapa de bits <em>y</em> texto —lo que deja copiar en un navegador
    /// o en un procesador de textos— se pega como texto: ahí lo que se está copiando es el texto.</summary>
    [Fact]
    public async Task PastingABitmapThatAlsoCarriesText_PastesTheTextInstead()
    {
        var (view, viewModel, window) = CreateView();
        try
        {
            var (editor, vm) = OpenNoteWith(view, viewModel, window, string.Empty, image: null, imageName: "");
            await CopyBitmapAsync(window, CreatePng(120, 60), alsoText: "texto con imagen");

            PressPaste(editor);
            Pump(window, () => editor.Document.Text.Length > 0);

            Assert.Equal("texto con imagen", editor.Document.Text);
            Assert.Empty(AttachmentsOf(vm));
        }
        finally { window.Close(); }
    }

    /// <summary>Un mapa de bits que el portapapeles anuncia pero del que no se obtiene un solo byte no
    /// puede dejar un adjunto vacío con un <c>![](…)</c> apuntando a él: eso sería una imagen rota en la
    /// nota, indistinguible de una borrada por error. La plataforma headless de Avalonia es exactamente
    /// ese caso —decodifica a 1×1 y no codifica nada—, así que aquí se prueba con el escenario real.</summary>
    [Fact]
    public async Task PastingABitmapThatYieldsNoBytes_LeavesNeitherAttachmentNorBrokenLink()
    {
        var (view, viewModel, window) = CreateView();
        try
        {
            var (editor, vm) = OpenNoteWith(view, viewModel, window, "Antes", image: null, imageName: "");
            await CopyBitmapAsync(window, CreatePng(120, 60));

            PressPaste(editor);
            Pump(window, () => AttachmentsOf(vm).Length > 0 || editor.Document.Text != "Antes", timeoutMs: 1000);

            Assert.Empty(AttachmentsOf(vm));
            Assert.Equal("Antes", editor.Document.Text);
        }
        finally { window.Close(); }
    }
}
