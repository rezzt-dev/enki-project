using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Services;
using enki.Controls;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Vista simplificada de Notas (Ajustes › Editor › Vista del editor). Igual que
/// <see cref="NotesViewUiTests"/>, esto monta el XAML compilado de verdad sobre un viewmodel real: es la
/// única capa donde se cae una ruta de binding mal escrita, un converter que no está registrado en
/// App.axaml o un <c>DataType</c> que no cuadra — nada de eso lo puede ver un test de solo viewmodel.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class SimpleNotesViewUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _vaultDir;

    public SimpleNotesViewUiTests()
    {
        HeadlessApp.EnsureInitialized();
        _vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(_vaultDir);
    }

    /// <summary><see cref="EditorPreferences"/> es estático y lo comparten todos los tests del mismo
    /// proceso, así que la vista simplificada se apaga al terminar — si no, el siguiente test de UI
    /// arrancaría con la disposición que dejó este.</summary>
    public void Dispose()
    {
        EditorPreferences.SimplifiedNotesView = false;
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (NotesView View, NotesViewModel ViewModel, Window Window) CreateView(bool simplified)
    {
        var settings = new SettingsService(_tempDir);
        settings.Settings.NotesViewMode = simplified ? "Simple" : "Full";
        settings.Save();

        var vault = new VaultService(settings);
        vault.SetVaultPath(_vaultDir);
        var session = new SessionService(vault);
        var viewModel = new NotesViewModel(vault, session, settingsService: settings);

        var view = new NotesView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        return (view, viewModel, window);
    }

    [Fact]
    public void WithNotesViewModeSimple_MountsTheSimplifiedLayoutOnly()
    {
        var (view, viewModel, window) = CreateView(simplified: true);

        Assert.True(viewModel.IsSimplifiedView);
        Assert.Single(view.GetVisualDescendants().OfType<SimpleNotesView>());
        Assert.Empty(view.GetVisualDescendants().OfType<FullNotesView>());

        window.Close();
    }

    [Fact]
    public void WithNotesViewModeFull_MountsTheFullLayoutOnly()
    {
        var (view, viewModel, window) = CreateView(simplified: false);

        Assert.False(viewModel.IsSimplifiedView);
        Assert.Single(view.GetVisualDescendants().OfType<FullNotesView>());
        Assert.Empty(view.GetVisualDescendants().OfType<SimpleNotesView>());

        window.Close();
    }

    /// <summary>Solo una de las dos disposiciones puede estar montada a la vez: cada una contiene su
    /// propio <see cref="EditorView"/>, y dos vivos sobre el mismo <see cref="EditorViewModel"/> se
    /// suscribirían los dos a sus eventos, aplicando cada comando "/" dos veces sobre el documento.</summary>
    [Fact]
    public void SwitchingViewMode_SwapsTheLayoutInsteadOfKeepingBothAlive()
    {
        var (view, viewModel, window) = CreateView(simplified: false);

        EditorPreferences.SimplifiedNotesView = true;
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.True(viewModel.IsSimplifiedView);
        Assert.Single(view.GetVisualDescendants().OfType<SimpleNotesView>());
        Assert.Empty(view.GetVisualDescendants().OfType<FullNotesView>());
        Assert.Single(view.GetVisualDescendants().OfType<EditorView>());

        EditorPreferences.SimplifiedNotesView = false;
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.Single(view.GetVisualDescendants().OfType<FullNotesView>());
        Assert.Empty(view.GetVisualDescendants().OfType<SimpleNotesView>());

        window.Close();
    }

    /// <summary>El árbol mezcla carpetas y notas en la misma lista de hijos, cosa que el panel de
    /// carpetas de la vista completa no hace — así que se comprueba sobre el árbol real construido desde
    /// disco, no solo sobre el conteo de nodos raíz.</summary>
    [Fact]
    public void Tree_ListsFoldersAndTheirNotes_AndRendersThem()
    {
        var (view, viewModel, window) = CreateView(simplified: true);

        var folder = VaultService.CreateFolder(_vaultDir, "Proyectos");
        File.WriteAllText(Path.Combine(folder.FullPath, "dentro.md"), "# Un titulo distinto\n");
        File.WriteAllText(Path.Combine(_vaultDir, "suelta.md"), "# Otro titulo distinto\n");
        File.WriteAllText(Path.Combine(_vaultDir, "no-es-nota.txt"), "no markdown");

        viewModel.Reload();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var roots = viewModel.SimpleTreeRoots;
        Assert.Equal(2, roots.Count);
        Assert.True(roots[0].IsFolder);
        Assert.Equal("Proyectos", roots[0].Name);
        // El árbol muestra el nombre de fichero, no el título extraído del encabezado markdown —
        // al estilo Obsidian, distinto de la rejilla de la vista completa (NoteCardViewModel).
        Assert.Equal("dentro", Assert.Single(roots[0].Children).Name);
        Assert.True(roots[1].IsNote);
        Assert.Equal("suelta", roots[1].Name);

        Assert.True(viewModel.HasSimpleTreeItems);
        Assert.NotEmpty(view.GetVisualDescendants().OfType<ItemsControl>());

        window.Close();
    }

    /// <summary>Pulsar una nota del árbol la abre en pestaña; volver a pulsarla salta a la pestaña que ya
    /// la tiene en vez de duplicarla — el requisito explícito de esta vista.</summary>
    [Fact]
    public void ActivatingTheSameNoteTwice_ReusesItsTabInsteadOfOpeningASecondOne()
    {
        var (view, viewModel, window) = CreateView(simplified: true);

        viewModel.NewNoteAtRootCommand.Execute(null);
        viewModel.NewNoteAtRootCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.Equal(2, viewModel.OpenEditors.Count);

        var first = viewModel.SimpleTreeRoots.First(n => n.IsNote);
        viewModel.ActivateTreeItemCommand.Execute(first);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.Equal(2, viewModel.OpenEditors.Count);
        Assert.Equal(first.FullPath, viewModel.ActiveEditor!.Note.FullPath);
        Assert.True(first.IsSelected);

        window.Close();
    }

    /// <summary>Las dos cosas que esta vista quita por definición: la barra de formato y el panel lateral
    /// de esquema/enlaces/estadísticas.</summary>
    [Fact]
    public void OpenNote_HasNoFormattingToolbarAndNoSidePanel()
    {
        var (view, viewModel, window) = CreateView(simplified: true);

        viewModel.NewNoteAtRootCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var editor = viewModel.ActiveEditor!;
        Assert.True(editor.IsSimplifiedView);
        Assert.False(editor.ShowFormattingToolbar);
        Assert.False(editor.ShowFullViewChrome);
        Assert.Equal(0, editor.SidePanelWidth);

        var toolbar = view.GetVisualDescendants().OfType<StackPanel>().First(s => s.Name == "FormattingToolbar");
        Assert.False(toolbar.IsVisible);

        window.Close();
    }

    /// <summary>La cabecera se queda en el nombre de la nota —en pequeño— y las acciones: sin caja de
    /// etiquetas y sin un titular que compita con el texto, para que el peso visual se lo lleve el espacio
    /// de edición. En la vista completa las dos cosas siguen como estaban.</summary>
    [Fact]
    public void OpenNote_HidesTheTagBoxAndShrinksTheTitle()
    {
        var (view, viewModel, window) = CreateView(simplified: true);

        viewModel.NewNoteAtRootCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var editor = viewModel.ActiveEditor!;
        var tagsBox = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "TagsBox");
        Assert.False(tagsBox.IsVisible);
        Assert.Equal(13, editor.TitleFontSize);

        EditorPreferences.SimplifiedNotesView = false;
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var fullTagsBox = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "TagsBox");
        Assert.True(fullTagsBox.IsVisible);
        Assert.Equal(24, editor.TitleFontSize);

        window.Close();
    }

    /// <summary>Ocultar el panel deja el raíl de navegación, y la elección se
    /// guarda en ajustes para reabrir la app tal como se dejó.</summary>
    [Fact]
    public void TogglingTheSidebar_PersistsTheChoice()
    {
        var settings = new SettingsService(_tempDir);
        settings.Settings.NotesViewMode = "Simple";
        settings.Save();

        var vault = new VaultService(settings);
        vault.SetVaultPath(_vaultDir);
        var viewModel = new NotesViewModel(vault, new SessionService(vault), settingsService: settings);
        var window = new Window { Content = new NotesView { DataContext = viewModel }, Width = 1200, Height = 800 };
        window.Show();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.True(viewModel.IsSimpleSidebarVisible);

        viewModel.ToggleSimpleSidebarCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.False(viewModel.IsSimpleSidebarVisible);
        Assert.False(settings.Settings.NotesSimpleSidebarVisible);
        Assert.False(new SettingsService(_tempDir).Settings.NotesSimpleSidebarVisible);

        window.Close();
    }

    /// <summary>El navegador del raíl se superpone sin quitar ancho al editor ni guardar el panel
    /// como desplegado. Abrir una nota desde una carpeta lo descarta y reutiliza su pestaña.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollapsedRail_BrowsesNestedNotesWithoutResizingEditorOrPersistingExpansion(bool onRight)
    {
        var folder = VaultService.CreateFolder(_vaultDir, "Proyecto");
        File.WriteAllText(Path.Combine(folder.FullPath, "nota.md"), "# Nota\n");
        var (view, vm, window) = CreateView(simplified: true);
        try
        {
            view.GetVisualDescendants().OfType<SidePanelGrid>().Single().IsPanelOnRight = onRight;
            vm.ToggleSimpleSidebarCommand.Execute(null);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var simple = view.GetVisualDescendants().OfType<SimpleNotesView>().Single();
            var tabs = simple.GetVisualDescendants().OfType<EditorTabStripView>().Single();
            var editorPosition = tabs.TranslatePoint(default, window);
            var editorWidth = tabs.Bounds.Width;
            var trigger = simple.FindControl<Button>("RailTreeButton")!;
            ClickControl(window, trigger);
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsSimpleNavigatorOpen);
            Assert.False(vm.IsSimpleSidebarVisible);
            Assert.False(new SettingsService(_tempDir).Settings.NotesSimpleSidebarVisible);
            Assert.Equal(editorPosition, tabs.TranslatePoint(default, window));
            Assert.Equal(editorWidth, tabs.Bounds.Width);
            Assert.Contains("active", trigger.Classes);
            var rail = simple.FindControl<Grid>("CollapsedRail")!;
            var navigator = simple.FindControl<Border>("SimpleNavigator")!;
            Assert.Equal(52, rail.Bounds.Width);
            var railLeft = rail.TranslatePoint(default, window)!.Value.X;
            var navigatorLeft = navigator.TranslatePoint(default, window)!.Value.X;
            if (onRight)
                Assert.Equal(railLeft, navigatorLeft + navigator.Bounds.Width, 1);
            else
                Assert.Equal(railLeft + rail.Bounds.Width, navigatorLeft, 1);

            var folderNode = Assert.Single(vm.SimpleTreeRoots);
            PressRowLeftEdge(view, window, folderNode);
            Assert.True(vm.IsSimpleNavigatorOpen);
            var note = Assert.Single(folderNode.Children);
            PressRowLeftEdge(view, window, note);
            Assert.False(vm.IsSimpleNavigatorOpen);
            Assert.Equal(note.FullPath, vm.ActiveEditor!.Note.FullPath);

            ClickControl(window, trigger);
            PressRowLeftEdge(view, window, Assert.Single(Assert.Single(vm.SimpleTreeRoots).Children));
            Assert.Single(vm.OpenEditors);
            Assert.False(vm.IsSimpleNavigatorOpen);
        }
        finally { window.Close(); }
    }

    /// <summary>La búsqueda recibe el foco también al reabrirse en el mismo modo. Escape y clic fuera
    /// cierran el navegador; la consulta se conserva para poder retomarla.</summary>
    [Fact]
    public void CollapsedSearch_RefocusesOnReopen_AndDismissesWithEscapeOrOutsideClick()
    {
        File.WriteAllText(Path.Combine(_vaultDir, "consulta.md"), "# Consulta\n");
        var (view, vm, window) = CreateView(simplified: true);
        try
        {
            vm.ToggleSimpleSidebarCommand.Execute(null);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var simple = view.GetVisualDescendants().OfType<SimpleNotesView>().Single();
            var trigger = simple.FindControl<Button>("RailSearchButton")!;
            var search = simple.FindControl<TextBox>("SimpleSearchBox")!;
            ClickControl(window, trigger);
            Dispatcher.UIThread.RunJobs();
            Assert.True(search.IsFocused);
            vm.SimpleSearchText = "consulta";

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.False(vm.IsSimpleNavigatorOpen);
            Assert.True(trigger.IsFocused);

            ClickControl(window, trigger);
            Dispatcher.UIThread.RunJobs();
            Assert.True(search.IsFocused);
            Assert.Equal("consulta", search.Text);
            var dismissArea = simple.FindControl<Border>("NavigatorDismissArea")!;
            var outside = dismissArea.TranslatePoint(new Point(dismissArea.Bounds.Width - 20, 150), window)!.Value;
            window.MouseDown(outside, MouseButton.Left);
            window.MouseUp(outside, MouseButton.Left);
            Assert.False(vm.IsSimpleNavigatorOpen);
            Assert.False(vm.IsSimpleSidebarVisible);

            // Retomar una búsqueda y abrir su resultado con el teclado también descarta el panel.
            vm.ShowSimpleTreeCommand.Execute(null);
            vm.SimpleSearchText = "consulta";
            ClickControl(window, trigger);
            Dispatcher.UIThread.RunJobs();
            var result = Assert.Single(vm.SimpleSearchResults);
            var row = simple.GetVisualDescendants().OfType<Border>()
                .Single(b => b.IsEffectivelyVisible && ReferenceEquals(b.DataContext, result)
                    && b.Classes.Contains("folder-row"));
            row.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.False(vm.IsSimpleNavigatorOpen);
            Assert.Equal(result.Note.FullPath, vm.ActiveEditor!.Note.FullPath);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void CollapsedRail_CreatesNotes_AndCanPinTheTemporaryNavigator()
    {
        var (view, vm, window) = CreateView(simplified: true);
        try
        {
            vm.ToggleSimpleSidebarCommand.Execute(null);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var simple = view.GetVisualDescendants().OfType<SimpleNotesView>().Single();
            ClickControl(window, simple.FindControl<Button>("RailNewNoteButton")!);
            Assert.Single(vm.OpenEditors);
            Assert.False(vm.IsSimpleSidebarVisible);

            ClickControl(window, simple.FindControl<Button>("RailTreeButton")!);
            var navigator = simple.FindControl<Border>("SimpleNavigator")!;
            var pin = navigator.GetVisualDescendants().OfType<Button>()
                .Single(b => b.IsEffectivelyVisible && ReferenceEquals(b.Command, vm.ToggleSimpleSidebarCommand));
            ClickControl(window, pin);
            Assert.True(vm.IsSimpleSidebarVisible);
            Assert.False(vm.IsSimpleNavigatorOpen);
            Assert.True(new SettingsService(_tempDir).Settings.NotesSimpleSidebarVisible);
            Assert.Equal(0, Grid.GetColumn(navigator));
        }
        finally { window.Close(); }
    }

    /// <summary>El raíl y el panel desplegado comparten el fondo de la app (`AppBackgroundBrush`) y no el
    /// alterno: este último es más oscuro que la propia página, así que marcaba la sección como una franja
    /// aparte en vez de como un bloque. La invariante se fija en las dos mitades porque plegado y
    /// desplegado son dos contenedores distintos, y volver a `AppBackgroundAltBrush` en uno solo de ellos
    /// no rompería ningún otro test.</summary>
    [Fact]
    public void SidebarRailAndExpandedPanel_ShareTheAppBackground()
    {
        var (view, _, window) = CreateView(simplified: true);
        try
        {
            var simple = view.GetVisualDescendants().OfType<SimpleNotesView>().Single();
            var rail = simple.FindControl<Grid>("CollapsedRail")!;
            var panel = Assert.IsType<Grid>(simple.FindControl<Border>("SimpleNavigator")!.Child);

            var appBackground = ResourceColor(rail, "AppBackgroundBrush");
            Assert.NotEqual(appBackground, ResourceColor(rail, "AppBackgroundAltBrush"));
            Assert.Equal(appBackground, ColorOf(rail.Background));
            Assert.Equal(appBackground, ColorOf(panel.Background));
        }
        finally { window.Close(); }
    }

    private static Color? ColorOf(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    private static Color ResourceColor(StyledElement host, string key)
    {
        Assert.True(host.TryFindResource(key, out var value), $"Recurso no encontrado: {key}");
        return ColorOf(value as IBrush) ?? throw new Xunit.Sdk.XunitException($"{key} no es un brush sólido");
    }

    private static void ClickControl(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.GetLayoutManager()!.ExecuteLayoutPass();
    }

    /// <summary>La búsqueda del panel es recursiva pero acotada a la sesión activa — aquí, el vault.</summary>
    [Fact]
    public void Search_MatchesNotesAtAnyDepth()
    {
        var (view, viewModel, window) = CreateView(simplified: true);

        var folder = VaultService.CreateFolder(_vaultDir, "Anidada");
        File.WriteAllText(Path.Combine(folder.FullPath, "objetivo.md"), "# Objetivo\n");
        File.WriteAllText(Path.Combine(_vaultDir, "otra.md"), "# Otra\n");
        viewModel.Reload();

        // El texto se escribe antes de entrar en modo búsqueda a propósito: teclear lleva debounce (un
        // DispatcherTimer que no llega a dispararse dentro del test), mientras que entrar en modo búsqueda
        // recarga los resultados en el acto — mismo camino de código, sin depender del reloj.
        viewModel.SimpleSearchText = "objet";
        viewModel.ShowSimpleSearchCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.Equal("Objetivo", Assert.Single(viewModel.SimpleSearchResults).Title);

        window.Close();
    }

    /// <summary>El cuadro de renombrar en línea ocupa una franja estrecha de una fila ancha, así que un
    /// clic para colocar el cursor cae casi siempre en el <c>Border</c> de la fila, no en el
    /// <see cref="TextBox"/>. Mientras la fila está en modo renombrar ese clic no debe activar el nodo
    /// (abrir la nota o plegar la carpeta): hacerlo sacaría el foco del cuadro y su <c>LostFocus</c>
    /// cerraría el renombrado antes de tiempo.</summary>
    [Fact]
    public void PressingARenamingRow_DoesNotOpenTheNoteOrCollapseTheFolder()
    {
        var (view, viewModel, window) = CreateView(simplified: true);

        var folder = VaultService.CreateFolder(_vaultDir, "Carpeta");
        File.WriteAllText(Path.Combine(folder.FullPath, "hija.md"), "# Hija\n");
        File.WriteAllText(Path.Combine(_vaultDir, "suelta.md"), "# Suelta\n");
        viewModel.Reload();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        // Una nota en modo renombrar: pulsar su fila no la abre y no cierra la edición.
        var noteNode = viewModel.SimpleTreeRoots.First(n => n.IsNote);
        noteNode.BeginRenameCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        PressRowLeftEdge(view, window, noteNode);

        Assert.True(noteNode.IsRenaming);
        Assert.Empty(viewModel.OpenEditors);

        // Una carpeta en modo renombrar: pulsar su fila no la pliega y no cierra la edición.
        var folderNode = viewModel.SimpleTreeRoots.First(n => n.IsFolder);
        folderNode.IsExpanded = true;
        folderNode.BeginRenameCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        PressRowLeftEdge(view, window, folderNode);

        Assert.True(folderNode.IsRenaming);
        Assert.True(folderNode.IsExpanded);

        window.Close();
    }

    /// <summary>Y una vez la fila deja el modo renombrar, vuelve a comportarse como un objetivo de clic
    /// normal — el guardado anterior no puede dejarla "muerta".</summary>
    [Fact]
    public void AfterRenamingEnds_TheRowActivatesOnPressAgain()
    {
        var (view, viewModel, window) = CreateView(simplified: true);

        File.WriteAllText(Path.Combine(_vaultDir, "suelta.md"), "# Suelta\n");
        viewModel.Reload();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var noteNode = viewModel.SimpleTreeRoots.First(n => n.IsNote);
        noteNode.BeginRenameCommand.Execute(null);
        noteNode.EditingName = "renombrada";
        viewModel.CommitRenameTreeItemCommand.Execute(noteNode);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.True(File.Exists(Path.Combine(_vaultDir, "renombrada.md")));

        var renamed = viewModel.SimpleTreeRoots.First(n => n.IsNote);
        Assert.False(renamed.IsRenaming);
        PressRowLeftEdge(view, window, renamed);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.Single(viewModel.OpenEditors);
        Assert.EndsWith("renombrada.md", viewModel.ActiveEditor!.Note.FullPath);

        window.Close();
    }

    /// <summary>En la vista simplificada el nombre de la nota en la cabecera del editor también renombra
    /// el fichero: Enter lo confirma en el sitio (sin saltar al cuerpo del documento) y el árbol lateral
    /// pasa a mostrar el nombre nuevo.</summary>
    [Fact]
    public void EditorHeaderTitle_EnterCommitsTheRename_AndRefreshesTheTree()
    {
        var (view, viewModel, window) = CreateView(simplified: true);

        File.WriteAllText(Path.Combine(_vaultDir, "original.md"), "# Original\ncuerpo");
        viewModel.Reload();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        viewModel.ActivateTreeItemCommand.Execute(viewModel.SimpleTreeRoots.First(n => n.IsNote));
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var titleBox = view.GetVisualDescendants().OfType<TextBox>()
            .First(t => t.Classes.Contains("title-edit"));
        titleBox.Focus();
        titleBox.Text = "renombrada-desde-editor";
        titleBox.RaiseEvent(new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            Source = titleBox,
        });
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.True(File.Exists(Path.Combine(_vaultDir, "renombrada-desde-editor.md")));
        Assert.False(File.Exists(Path.Combine(_vaultDir, "original.md")));
        Assert.EndsWith("renombrada-desde-editor.md", viewModel.ActiveEditor!.Note.FullPath);
        Assert.Contains(viewModel.SimpleTreeRoots, n => n.IsNote && n.Name == "renombrada-desde-editor");
        Assert.True(titleBox.IsFocused, "el foco no debe saltar al cuerpo del documento al confirmar");

        window.Close();
    }

    /// <summary>Un clic con el botón izquierdo en el borde izquierdo de la fila del árbol de
    /// <paramref name="node"/> — sobre el <c>Border</c> que lleva el manejador de puntero, no sobre el
    /// cuadro de texto de renombrar.</summary>
    private static void PressRowLeftEdge(NotesView view, Window window, NoteTreeItemViewModel node)
    {
        var row = view.GetVisualDescendants().OfType<Border>()
            .First(b => ReferenceEquals(b.DataContext, node) && b.Classes.Contains("folder-row"));
        var p = row.TranslatePoint(new Point(6, row.Bounds.Height / 2), window) ?? new Point();
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        window.GetLayoutManager()!.ExecuteLayoutPass();
    }
}
