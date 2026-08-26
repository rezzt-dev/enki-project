using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using enki.Services;
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

    /// <summary>Ocultar el panel deja solo el raíl (mostrar panel + cambiar de sesión), y la elección se
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
}
