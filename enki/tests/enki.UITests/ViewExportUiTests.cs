using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Models;
using enki.Services;
using enki.Services.Export;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>T8 ("Exportar Calendario y Kanban a imagen o PDF"): comprueba lo que solo se puede comprobar
/// con las vistas reales maquetadas — que la región exportable existe con el nombre que el servicio busca,
/// que está dispuesta a su tamaño completo (no al recortado por el <c>ScrollViewer</c>) y que el documento
/// de una nota se compone con los bloques del editor y se pagina por donde se debe.
///
/// <para>Sí se comprueba la maqueta a tamaño completo con la que se rasteriza (<see cref="FullSizeLayout"/>):
/// que la región crece hasta enseñar lo que tapan sus visores internos y que vuelve a su sitio después.</para>
///
/// <para>No se rasteriza nada aquí: la plataforma headless de Avalonia dibuja en el vacío, así que un
/// <c>RenderTargetBitmap</c> no diría nada útil. Lo que sale de la rasterización —que la imagen se vea
/// bien— es comprobación visual y vive en la checklist de QA manual. La excepción es el PDF de una nota,
/// que con <c>ENKI_GRAPH_PREVIEW=&lt;carpeta&gt;</c> (Skia real) se genera de verdad y se deja en esa
/// carpeta para revisarlo.</para></summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class ViewExportUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");

    public ViewExportUiTests() => HeadlessApp.EnsureInitialized();

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static readonly ExportAppearance Appearance = new(Colors.White, Colors.Black, Colors.Gray, FontFamily.Default);

    private static Control? FindNamed(Visual root, string name) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);

    // ===================== Kanban =====================

    [Fact]
    public void KanbanBoard_ExposesItsExportRoot_LaidOutAtFullWidth()
    {
        var settings = new SettingsService(_tempDir);
        var authService = new PolarAuthService(settings);
        var taskService = new PolarTaskService(_tempDir);
        var reminderService = new ReminderService(_tempDir);
        var syncService = new PolarSyncService(authService, new SupabaseRestClient(authService), taskService, reminderService, settings);
        var viewModel = new KanbanViewModel(new KanbanService(_tempDir), authService,
            new PolarTaskManagementViewModel(taskService, syncService));

        var view = new KanbanView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        window.Show();
        try
        {
            var root = FindNamed(view, "KanbanExportRootVertical");
            Assert.NotNull(root);
            Assert.True(root!.Bounds.Width > 0, "La región exportable del tablero debe estar maquetada.");
            Assert.True(root.Bounds.Height > 0);

            // Lo que se exporta es el contenido del ScrollViewer, no el ScrollViewer: por eso puede ser más
            // ancho que él, que es justo lo que pide el criterio de aceptación de T8.
            Assert.NotNull(root.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault());
        }
        finally
        {
            window.Close();
        }
    }

    // ===================== Calendario =====================

    [Fact]
    public void CalendarView_ExposesItsExportRoot_WithoutTheNavigationToolbar()
    {
        var settings = new SettingsService(_tempDir);
        var authService = new PolarAuthService(settings);
        var kanbanService = new KanbanService(_tempDir);
        var polarTaskService = new PolarTaskService(_tempDir);
        var reminderService = new ReminderService(_tempDir);
        var notificationService = new NotificationService(settings, _tempDir);
        var layerColorService = new CalendarLayerColorService(kanbanService, polarTaskService, authService, settings);
        var aggregationService = new CalendarAggregationService(kanbanService, polarTaskService, reminderService, authService, layerColorService);
        var viewModel = new CalendarViewModel(aggregationService, layerColorService, kanbanService, polarTaskService,
            reminderService, authService, settings, notificationService, _ => { });

        var view = new CalendarView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        window.Show();
        try
        {
            var root = FindNamed(view, "CalendarExportRoot");
            Assert.NotNull(root);
            Assert.True(root!.Bounds.Width > 0);
            Assert.True(root.Bounds.Height > 0);

            // La barra de navegación (◀ ▶ Hoy, selector de vista) queda fuera: un PNG del calendario no
            // debe llevar botones de interfaz.
            Assert.DoesNotContain(root.GetVisualDescendants().OfType<RadioButton>(), r => r.Classes.Contains("calendar-toolbar-btn"));
        }
        finally
        {
            window.Close();
        }
    }

    // ===================== Maqueta a tamaño completo =====================

    /// <summary>Las tarjetas que no caben en una columna viven tras el <c>ScrollViewer</c> propio de esa
    /// columna. Mientras dura la exportación, la región tiene que medir lo que mide la columna más larga,
    /// y al terminar tiene que volver exactamente a como estaba.</summary>
    [Fact]
    public void FullSizeLayout_GrowsTheKanbanBoard_ToItsLongestColumn_AndRestoresIt()
    {
        var kanbanService = new KanbanService(_tempDir);
        for (var i = 0; i < 30; i++)
            kanbanService.DefaultBoard.Columns[0].Cards.Add(new KanbanCard { Id = $"card-{i}", Title = $"Tarjeta {i}" });
        kanbanService.Save();

        var settings = new SettingsService(_tempDir);
        var authService = new PolarAuthService(settings);
        var taskService = new PolarTaskService(_tempDir);
        var reminderService = new ReminderService(_tempDir);
        var syncService = new PolarSyncService(authService, new SupabaseRestClient(authService), taskService, reminderService, settings);
        var viewModel = new KanbanViewModel(kanbanService, authService, new PolarTaskManagementViewModel(taskService, syncService));

        var view = new KanbanView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        window.Show();
        try
        {
            var root = FindNamed(view, "KanbanExportRootVertical")!;
            var before = root.Bounds;

            using (FullSizeLayout.Apply(root))
            {
                Assert.True(root.Bounds.Height > before.Height * 2,
                    $"Treinta tarjetas no caben en {before.Height:F0}px: la región exportada debe crecer (mide {root.Bounds.Height:F0}px).");
                Assert.Equal(before.Width, root.Bounds.Width);
                Assert.All(root.GetVisualDescendants().OfType<ScrollViewer>(),
                    s => Assert.True(s.Extent.Height <= s.Viewport.Height + 0.5, "Ningún visor interno debe quedar recortando."));
            }

            Assert.Equal(before, root.Bounds);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>La rejilla de horas de Semana solo enseña en pantalla una parte del día; exportada tiene
    /// que llevar las 24 franjas.</summary>
    [Fact]
    public void FullSizeLayout_ShowsTheWholeDay_InTheWeekView_AndRestoresIt()
    {
        var settings = new SettingsService(_tempDir);
        var authService = new PolarAuthService(settings);
        var kanbanService = new KanbanService(_tempDir);
        var polarTaskService = new PolarTaskService(_tempDir);
        var reminderService = new ReminderService(_tempDir);
        var notificationService = new NotificationService(settings, _tempDir);
        var layerColorService = new CalendarLayerColorService(kanbanService, polarTaskService, authService, settings);
        var aggregationService = new CalendarAggregationService(kanbanService, polarTaskService, reminderService, authService, layerColorService);
        var viewModel = new CalendarViewModel(aggregationService, layerColorService, kanbanService, polarTaskService,
            reminderService, authService, settings, notificationService, _ => { });

        var view = new CalendarView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        window.Show();
        try
        {
            viewModel.SetViewCommand.Execute(CalendarViewMode.Week);
            Dispatcher.UIThread.RunJobs();

            var root = FindNamed(view, "CalendarExportRoot")!;
            var before = root.Bounds;
            Assert.True(before.Height < 24 * 60, "La ventana de prueba debe ser más baja que el día entero.");

            using (FullSizeLayout.Apply(root))
                Assert.True(root.Bounds.Height >= 24 * 60, $"El día entero mide 1440px; la región mide {root.Bounds.Height:F0}px.");

            Assert.Equal(before, root.Bounds);
        }
        finally
        {
            window.Close();
        }
    }

    // ===================== Documento de una nota =====================

    private const double SheetWidth = 425;
    private const double SheetHeight = 700;

    private static NoteExportSource Source(string markdown, string title = "Mi nota", params string[] tags) =>
        new(title, markdown, tags, NotePath: null, VaultPath: null, ReadBytes: null, ResolveTransclusion: null);

    /// <summary>Ejecuta una tarea asíncrona de la exportación en el hilo de UI de la plataforma headless,
    /// como la ejecuta la app: con el contexto de sincronización de Avalonia, para que lo que sigue a cada
    /// <c>await</c> vuelva a este hilo y pueda tocar controles.</summary>
    private static T RunOnUiThread<T>(Func<Task<T>> work)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
        try
        {
            var task = work();
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("La exportación no terminó.");
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }
            return task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static NoteDocument Compose(NoteExportSource source)
    {
        var style = NoteDocumentStyle.ForPrint();
        var assets = RunOnUiThread(() => NoteDocumentAssets.LoadAsync(source, style, SheetWidth));
        return NoteDocumentComposer.Compose(source, style, assets, SheetWidth, SheetHeight);
    }

    private static IReadOnlyList<IReadOnlyList<PageFragment>> Paginate(NoteDocument document) =>
        DocumentPaginator.Paginate(document.Blocks.Select(b => b.ToPagination()).ToList(), SheetHeight);

    private const string EveryBlock = """
        # Capítulo
        Un párrafo con **negrita**, *cursiva* y un [enlace](https://example.com).

        - uno
          - anidado
        - [x] hecha

        | A | B |
        | :-- | --: |
        | 1 | 2 |

        ```cs
        var x = 1;
        ```

        > cita

        > [!warning] Ojo
        > cuidado

        ---
        <div align="center">
        centrado
        </div>
        """;

    [Fact]
    public void NoteDocument_ComposesTheEditorBlocks_AtTheSheetWidth()
    {
        var document = Compose(Source(EveryBlock));

        // Título, encabezado, párrafo, lista, tabla, código, cita, aviso, regla y párrafo centrado: las
        // líneas que solo abren o cierran el <div> no ocupan sitio.
        Assert.Equal(10, document.Blocks.Count);
        Assert.All(document.Blocks, b => Assert.Equal(SheetWidth, b.Visual.Bounds.Width));
        Assert.All(document.Blocks, b => Assert.True(b.Visual.Bounds.Height > 0));

        Assert.True(document.Blocks[1].KeepWithNext, "Un encabezado no puede quedarse solo al pie de una página.");
        Assert.NotNull(document.Blocks[4].RepeatedHeader);

        var centered = document.Blocks[^1].Visual.GetSelfAndVisualDescendants().OfType<TextBlock>().Single();
        Assert.Equal(TextAlignment.Center, centered.TextAlignment);

        var paragraph = document.Blocks[2].Visual.GetSelfAndVisualDescendants().OfType<TextBlock>().Single();
        var link = Assert.Single(DocumentLinks.Of(paragraph));
        Assert.Equal("https://example.com", link.Url);
    }

    /// <summary>Los marcadores (Markdown y HTML) no se ven en el documento, igual que en el live preview.</summary>
    [Fact]
    public void NoteDocument_HidesMarkers_LikeTheLivePreview()
    {
        var document = Compose(Source("Texto **fuerte** con <u>subrayado</u> y \\*escapado\\*."));

        var text = document.Blocks[^1].Visual.GetSelfAndVisualDescendants().OfType<TextBlock>().Single();
        var visible = string.Concat(text.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
        Assert.Equal("Texto fuerte con subrayado y *escapado*.", visible);
    }

    /// <summary>Si la nota ya empieza por un encabezado con su propio título, no se repite encima.</summary>
    [Fact]
    public void NoteDocument_ThatStartsWithItsOwnTitle_DoesNotRepeatIt()
    {
        var document = Compose(Source("# Mi nota\n\nTexto."));

        Assert.Equal(2, document.Blocks.Count);
        Assert.IsType<TextBlock>(document.Blocks[0].Visual);
    }

    /// <summary><c>![[Nota]]</c> solo en su línea se compone con el contenido de la nota embebida, como en
    /// el editor; si no se puede resolver, la referencia queda como texto y no tumba el documento.</summary>
    [Fact]
    public void NoteDocument_Transclusion_IsComposedInline_AndABrokenOneStaysAsText()
    {
        var embedded = new NoteFile { FullPath = "/vault/Otra.md", Title = "Otra" };
        NoteTransclusionResult Resolve(string reference, IReadOnlyList<string>? ancestry) => reference == "Otra"
            ? new(NoteTransclusionStatus.Resolved, reference, embedded, "Contenido **embebido**", ancestry ?? [])
            : new(NoteTransclusionStatus.Broken, reference, null, string.Empty, ancestry ?? []);

        var source = new NoteExportSource(string.Empty, "![[Otra]]\n\n![[Rota]]", [], "/vault/Nota.md", "/vault", null, Resolve);
        var document = Compose(source);

        Assert.Equal(2, document.Blocks.Count);
        var embeddedText = document.Blocks[0].Visual.GetSelfAndVisualDescendants().OfType<TextBlock>()
            .Select(t => string.Concat(t.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)));
        Assert.Contains("Contenido embebido", embeddedText);
        var broken = document.Blocks[1].Visual.GetSelfAndVisualDescendants().OfType<TextBlock>().Single();
        Assert.Equal("Rota", string.Concat(broken.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)));
    }

    [Fact]
    public void NoteDocument_LongNote_IsPaginated_OnLineBoundaries()
    {
        var markdown = string.Join("\n\n", Enumerable.Repeat(
            "Un párrafo bastante largo de texto normal que ocupa varias líneas al maquetarse en la caja de texto de una hoja.", 120));
        var document = Compose(Source(markdown));
        var pages = Paginate(document);

        Assert.True(pages.Count > 1, "Ciento veinte párrafos ocupan más de una página.");
        foreach (var fragment in pages.SelectMany(p => p))
        {
            var block = document.Blocks[fragment.Block].ToPagination();
            Assert.True(fragment.From == 0 || block.Breaks.Contains(fragment.From),
                $"El corte en {fragment.From:F2} parte una línea por la mitad.");
            Assert.True(fragment.Top + fragment.Height <= SheetHeight + 0.01);
        }
    }

    /// <summary>Una nota vacía sigue dando exactamente una página: exportar algo vacío es un PDF de una
    /// hoja en blanco, no un PDF sin hojas.</summary>
    [Fact]
    public void NoteDocument_ForAnEmptyNote_StillProducesOnePage()
    {
        Assert.Single(Paginate(Compose(Source(string.Empty, title: string.Empty))));
    }

    [Fact]
    public void NoteDocument_LongTable_RepeatsItsHeaderOnEveryPage()
    {
        var rows = string.Join("\n", Enumerable.Range(1, 120).Select(i => $"| fila {i} | valor {i} |"));
        var document = Compose(Source($"| Nombre | Valor |\n| --- | --- |\n{rows}", title: string.Empty));
        var pages = Paginate(document);
        var header = document.Blocks[0].RepeatedHeader!.Bounds.Height;

        Assert.True(pages.Count > 1);
        Assert.All(pages.Skip(1), page => Assert.Equal(new PageFragment(0, 0, header, 0), page[0]));
    }

    /// <summary>Solo con Skia real (<c>ENKI_GRAPH_PREVIEW=&lt;carpeta&gt;</c>): genera el PDF de una nota con
    /// todo lo que el editor sabe dibujar —una imagen adjunta de verdad, centrada y con tamaño, una tabla y
    /// un bloque de código que no caben en una página, fórmulas, citas anidadas…—, comprueba que es un PDF
    /// con texto de verdad, fuentes incrustadas y enlaces, y lo deja en la carpeta para revisarlo a ojo. Con
    /// el dibujo headless no hay lienzo de Skia sobre el que dibujar.</summary>
    [Fact]
    public void NoteDocument_Pdf_IsAVectorDocument()
    {
        if (Environment.GetEnvironmentVariable("ENKI_GRAPH_PREVIEW") is not { Length: > 0 } preview) return;

        Directory.CreateDirectory(_tempDir);
        var notePath = Path.Combine(_tempDir, "Informe.md");
        using (var bitmap = new SkiaSharp.SKBitmap(800, 400))
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(new SkiaSharp.SKColor(0x2E, 0x8B, 0x57));
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White };
            canvas.DrawCircle(400, 200, 150, paint);
            using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(_tempDir, "grafico.png"), png.ToArray());
        }

        var filler = string.Join("\n\n", Enumerable.Repeat(
            "Párrafo de relleno con suficiente texto como para ocupar varias líneas justificadas en la hoja y forzar la paginación del documento.", 12));
        var rows = string.Join("\n", Enumerable.Range(1, 45).Select(i => $"| Elemento {i} | {i * 3} € | `ok` |"));
        var code = string.Join("\n", Enumerable.Range(1, 70).Select(i => $"    Console.WriteLine(\"línea {i}\"); // comentario"));
        var markdown = EveryBlock + $$"""


            ## Imágenes y fórmulas
            <div align="center">
            ![Gráfico|300](grafico.png)
            </div>

            La energía es $E = mc^2$ y la suma vale:

            $$
            \sum_{i=1}^{n} i = \frac{n(n+1)}{2}
            $$

            > Primer nivel con ==resaltado== y `código`
            >> Segundo nivel <u>subrayado</u> y ~~tachado~~

            1. Primero
            2. Segundo
               - Detalle con una frase bastante larga para comprobar que la sangría colgante se mantiene al partir la línea.

            {{filler}}

            ## Tabla larga
            | Concepto | Importe | Estado |
            | --- | ---: | :---: |
            {{rows}}

            ## Código largo
            ```csharp
            {{code}}
            ```
            """;
        File.WriteAllText(notePath, markdown);

        var source = new NoteExportSource("Informe", markdown, ["proyecto", "pdf"], notePath, _tempDir, File.ReadAllBytes, null);
        var pdf = RunOnUiThread(() => NoteDocumentExporter.CreatePdfAsync(source, NotePageSetup.A4));

        Directory.CreateDirectory(preview);
        File.WriteAllBytes(Path.Combine(preview, "nota.pdf"), pdf);

        var text = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-", text, StringComparison.Ordinal);
        Assert.Contains("/FontFile", text, StringComparison.Ordinal);
        Assert.Contains("/URI", text, StringComparison.Ordinal);
        Assert.Contains("/Subtype /Image", text, StringComparison.Ordinal);

        // La lámina PNG comparte composición con el PDF.
        var sheet = RunOnUiThread(() => NoteDocumentExporter.CreatePngAsync(source, NotePageSetup.A4, Appearance,
            VisualSnapshotRenderer.ScaleStandard));
        File.WriteAllBytes(Path.Combine(preview, "nota.png"), sheet.Png);
        Assert.True(sheet.PixelHeight > sheet.PixelWidth, "La nota entera sale en una sola lámina alta.");
    }

    [Fact]
    public void FindExportRoot_WithNoWindow_ReturnsNull() =>
        Assert.Null(ViewExportService.FindExportRoot(["NoExisteEstaRegion"]));
}
