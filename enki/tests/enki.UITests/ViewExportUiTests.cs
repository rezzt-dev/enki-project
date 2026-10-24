using System;
using System.IO;
using System.Linq;
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
/// que está dispuesta a su tamaño completo (no al recortado por el <c>ScrollViewer</c>) y que la hoja de
/// una nota se compone y se pagina.
///
/// <para>Sí se comprueba la maqueta a tamaño completo con la que se rasteriza (<see cref="FullSizeLayout"/>):
/// que la región crece hasta enseñar lo que tapan sus visores internos y que vuelve a su sitio después.</para>
///
/// <para>No se rasteriza nada aquí: la plataforma headless de Avalonia dibuja en el vacío, así que un
/// <c>RenderTargetBitmap</c> no diría nada útil. Lo que sale de la rasterización —que la imagen se vea
/// bien— es comprobación visual y vive en la checklist de QA manual.</para></summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class ViewExportUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");

    public ViewExportUiTests() => HeadlessApp.EnsureInitialized();

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static readonly ExportAppearance Appearance = new(
        Colors.White, Colors.Black, Colors.Gray, Colors.Black, Colors.Gray,
        Color.FromRgb(0xF2, 0xF2, 0xF2), FontFamily.Default, FontFamily.Default);

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

    // ===================== Hoja de una nota =====================

    [Fact]
    public void NotePage_IsComposedAtTheSheetWidth_AndGrowsWithTheContent()
    {
        var page = NotePageComposer.Build("Mi nota", "# Título\n\nUn párrafo con **negrita**.\n\n- uno\n- dos\n", Appearance);

        Assert.Equal(NotePageComposer.PageWidth, page.Width);
        Assert.True(page.DesiredSize.Height > 0);
    }

    [Fact]
    public void NotePage_PaginatesLongNotes_IntoFixedHeightPages()
    {
        var shortNote = NotePageComposer.BuildPages("Corta", "Una línea.", Appearance);
        var longNote = NotePageComposer.BuildPages("Larga", string.Join('\n', Enumerable.Repeat("Un párrafo bastante largo de texto normal.", 400)), Appearance);

        Assert.Single(shortNote);
        Assert.True(longNote.Count > 1, "Una nota de 400 párrafos tiene que ocupar más de una página A4.");
        Assert.All(longNote, p => Assert.Equal(NotePageComposer.PageHeight, p.Bounds.Height));
        Assert.All(longNote, p => Assert.Equal(NotePageComposer.PageWidth, p.Bounds.Width));
    }

    /// <summary>Una nota vacía sigue dando exactamente una página: exportar algo vacío es un PDF de una
    /// hoja en blanco, no un PDF sin hojas (que <see cref="PdfImageWriter"/> rechaza).</summary>
    [Fact]
    public void NotePage_ForAnEmptyNote_StillProducesOnePage()
    {
        Assert.Single(NotePageComposer.BuildPages("Vacía", string.Empty, Appearance));
    }

    [Fact]
    public void FindExportRoot_WithNoWindow_ReturnsNull() =>
        Assert.Null(ViewExportService.FindExportRoot(["NoExisteEstaRegion"]));
}
