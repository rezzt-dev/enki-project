using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Fase 6 (roadmap §11, "Tests de UI básicos ... mover tarjeta Kanban") — same reasoning as
/// <see cref="NotesViewUiTests"/>: drives the real <see cref="KanbanView"/> XAML bound to a real
/// <see cref="KanbanViewModel"/> and exercises <see cref="KanbanViewModel.MoveCard"/>, the exact method
/// <c>KanbanView.axaml.cs</c>'s drag-and-drop handlers call on a real drop — then re-renders and asserts
/// the board's visual tree actually reflects the card under its new column, not just that the underlying
/// collections changed.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class KanbanViewUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");

    public KanbanViewUiTests() => HeadlessApp.EnsureInitialized();

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (KanbanView View, KanbanViewModel ViewModel, Window Window) CreateView()
    {
        var settings = new SettingsService(_tempDir);
        var authService = new PolarAuthService(settings);
        var restClient = new SupabaseRestClient(authService);
        var taskService = new PolarTaskService(_tempDir);
        var reminderService = new ReminderService(_tempDir);
        var syncService = new PolarSyncService(authService, restClient, taskService, reminderService, settings);
        var taskManagement = new PolarTaskManagementViewModel(taskService, syncService);
        var kanbanService = new KanbanService(_tempDir);
        var viewModel = new KanbanViewModel(kanbanService, authService, taskManagement);

        var view = new KanbanView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        window.Show();

        return (view, viewModel, window);
    }

    /// <summary>El nombre de una columna se dibuja siempre en mayúsculas, escriba el usuario lo que
    /// escriba — es estructura del tablero, no contenido. Así que lo que se busca en el árbol visual es el
    /// título en mayúsculas, y de paso se comprueba que el modelo conserva el original: la regla es de
    /// presentación, no debe tocar el dato.</summary>
    [Fact]
    public void Loading_TheSeedBoard_RendersEveryColumnTitle_Uppercased()
    {
        var (view, viewModel, window) = CreateView();

        var renderedTexts = view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .ToList();

        foreach (var column in viewModel.Columns)
        {
            Assert.Contains(column.Title.ToUpper(CultureInfo.CurrentCulture), renderedTexts);
            Assert.Equal(column.Column.Title, column.Title);
        }

        window.Close();
    }

    /// <summary>La regla de las mayúsculas es de presentación: un título tecleado en minúsculas se dibuja
    /// en mayúsculas en la cabecera, pero el campo de renombrar sigue ofreciendo el texto literal para
    /// editarlo — si no, renombrar iría convirtiendo el título a mayúsculas de verdad a cada pasada.</summary>
    [Fact]
    public void ATitleTypedInLowercase_RendersUppercased_ButIsEditedAsTyped()
    {
        var (view, viewModel, window) = CreateView();
        var column = viewModel.Columns[0];
        column.ApplyTitle("pendientes de revisar");
        view.Measure(new Size(1400, 900));
        view.Arrange(new Rect(0, 0, 1400, 900));

        var renderedTexts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("PENDIENTES DE REVISAR", renderedTexts);
        Assert.DoesNotContain("pendientes de revisar", renderedTexts);

        column.BeginRenameCommand.Execute(null);

        Assert.Equal("pendientes de revisar", column.EditingTitle);
        Assert.Equal("pendientes de revisar", column.Column.Title);

        window.Close();
    }

    /// <summary>Enlaces web de una tarjeta: se añaden desde el formulario, quedan guardados en la tarjeta,
    /// el pie enseña cuántos hay y el detalle los lista con su etiqueta corta para abrirlos.</summary>
    [Fact]
    public void CardLinks_AddedInTheEditor_RenderOnTheCardAndInTheDetail()
    {
        var (view, viewModel, window) = CreateView();
        var card = viewModel.Columns.First(c => c.Cards.Count > 0).Cards[0];

        viewModel.OpenEditCardCommand.Execute(card);
        var editor = viewModel.CardEditor!;
        editor.NewLinkUrl = "www.example.com/docs/";
        editor.AddLinkCommand.Execute(null);
        editor.NewLinkUrl = "https://example.org";
        editor.AddLinkCommand.Execute(null);
        view.Measure(new Size(1400, 900));
        view.Arrange(new Rect(0, 0, 1400, 900));
        // Los modales viven en un Popup fuera del árbol visual del tablero. En headless se mide su
        // tarjeta explícitamente, igual que las otras pruebas de ModalOverlayHost.
        var editorHost = view.GetLogicalDescendants().OfType<enki.Controls.ModalOverlayHost>()
            .Single(h => h.Content is Border { DataContext: KanbanCardEditorViewModel });
        Assert.True(editorHost.IsOpen);
        var editorCard = (Border)editorHost.Content!;
        editorHost.Content = null;
        window.Content = null;
        editorCard.DataContext = editor;
        window.Content = editorCard;
        window.GetLayoutManager()!.ExecuteLayoutPass();
        Assert.Contains("example.com/docs", editorCard.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        window.Content = null;
        editorHost.Content = editorCard;
        window.Content = view;

        viewModel.SaveCardEditorCommand.Execute(null);
        Assert.Equal(["https://www.example.com/docs/", "https://example.org/"], card.Card.Links);
        Assert.True(card.HasLinks);

        viewModel.OpenCardDetailCommand.Execute(card);
        view.Measure(new Size(1400, 900));
        view.Arrange(new Rect(0, 0, 1400, 900));
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        var detailHost = view.GetLogicalDescendants().OfType<enki.Controls.ModalOverlayHost>()
            .Single(h => h.Content is Border { DataContext: KanbanCardDetailViewModel });
        Assert.True(detailHost.IsOpen);
        var detailCard = (Border)detailHost.Content!;
        detailHost.Content = null;
        window.Content = null;
        detailCard.DataContext = viewModel.CardDetail;
        window.Content = detailCard;
        window.GetLayoutManager()!.ExecuteLayoutPass();
        Assert.Contains("example.org", detailCard.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains("2", texts);

        window.Close();
    }

    /// <summary>Simulates the real drop: takes the first card of the first column and moves it to the last
    /// column via <see cref="KanbanViewModel.MoveCard"/> — the same call
    /// <c>KanbanView.axaml.cs</c>'s <c>ColumnBody_Drop</c> makes — then asserts both the viewmodel state
    /// and the re-rendered visual tree agree on where the card ended up.</summary>
    [Fact]
    public void MoveCard_ToAnotherColumn_UpdatesViewModelAndVisualTree()
    {
        var (view, viewModel, window) = CreateView();
        var sourceColumn = viewModel.Columns.First(c => c.Cards.Count > 0);
        var card = sourceColumn.Cards[0];
        var targetColumn = viewModel.Columns.Last(c => c != sourceColumn);

        viewModel.MoveCard(card, targetColumn, null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.DoesNotContain(card, sourceColumn.Cards);
        Assert.Contains(card, targetColumn.Cards);

        var renderedCardTitles = view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .ToList();
        Assert.Contains(card.Title, renderedCardTitles);

        window.Close();
    }

    /// <summary>Archivar una tarjeta la saca del tablero dibujado sin sacarla del modelo
    /// (docs/agent-docs/kanban-tareas-archivadas.md), y la ventana de archivadas —que es XAML nuevo con su
    /// propia plantilla de fila— la vuelve a pintar con su título. Esta prueba existe sobre todo por la
    /// segunda mitad: recorre el árbol visual real, así que un binding roto en el modal o una plantilla
    /// que no resuelve se ven aquí y no en la primera ejecución del usuario.</summary>
    [Fact]
    public void ArchivingACard_TakesItOffTheBoard_AndTheArchiveWindowRendersIt()
    {
        var (view, viewModel, window) = CreateView();
        var doneColumn = viewModel.Columns.First(c => c.IsDoneColumn && c.Cards.Count > 0);
        var card = doneColumn.Cards[0];
        var title = card.Title;

        viewModel.ArchiveCardCommand.Execute(card);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.True(card.IsArchived);
        Assert.DoesNotContain(card, doneColumn.Cards);
        Assert.Contains(card.Card, doneColumn.Column.Cards);
        Assert.DoesNotContain(title, view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

        viewModel.OpenArchiveCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var renderedInArchive = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(title, renderedInArchive);
        Assert.Contains(viewModel.ArchiveCountLabel, renderedInArchive);

        viewModel.UnarchiveCardCommand.Execute(card);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.False(card.IsArchived);
        Assert.Contains(card, doneColumn.Cards);

        window.Close();
    }

    // ===================== MED-07: dependencias y vista de lista =====================

    private static void SeedBlockedPair(KanbanViewModel viewModel, KanbanService service)
    {
        foreach (var column in service.DefaultBoard.Columns) column.Cards.Clear();
        service.DefaultBoard.Columns[0].Cards.Add(new enki.Models.KanbanCard { Id = "a", Title = "Redactar informe", Blocks = ["b"] });
        service.DefaultBoard.Columns[0].Cards.Add(new enki.Models.KanbanCard { Id = "b", Title = "Enviar informe" });
        viewModel.Reload();
    }

    [Fact]
    public void ABlockedCard_ShowsItsLockIndicator_WithTheBlockerInTheTooltip()
    {
        var (view, viewModel, window) = CreateView();
        var service = (KanbanService)typeof(KanbanViewModel)
            .GetField("_kanbanService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(viewModel)!;
        SeedBlockedPair(viewModel, service);
        window.UpdateLayout();

        var tooltips = view.GetVisualDescendants().OfType<Control>()
            .Select(c => ToolTip.GetTip(c) as string)
            .Where(t => t is not null)
            .ToList();

        Assert.Contains(tooltips, t => t!.Contains("Redactar informe", StringComparison.Ordinal));
        window.Close();
    }

    [Fact]
    public void ListLayout_SwapsTheColumnsForARowPerCard_AndBackAgain()
    {
        var (view, viewModel, window) = CreateView();
        var service = (KanbanService)typeof(KanbanViewModel)
            .GetField("_kanbanService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(viewModel)!;
        SeedBlockedPair(viewModel, service);
        var list = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "KanbanListView");
        var board = view.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "BoardVerticalScroll");
        window.UpdateLayout();
        Assert.False(list.IsVisible);
        Assert.True(board.IsVisible);

        viewModel.ToggleListLayoutCommand.Execute(null);
        window.UpdateLayout();

        Assert.True(list.IsVisible);
        Assert.False(board.IsVisible);
        var titles = list.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Redactar informe", titles);
        Assert.Contains("Enviar informe", titles);

        viewModel.ToggleListLayoutCommand.Execute(null);
        window.UpdateLayout();
        Assert.False(list.IsVisible);
        Assert.True(board.IsVisible);
        window.Close();
    }

    [Fact]
    public void CompletingABlockedCard_OpensTheConfirmationDialog_WithTheBlockerName()
    {
        var (view, viewModel, window) = CreateView();
        var service = (KanbanService)typeof(KanbanViewModel)
            .GetField("_kanbanService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(viewModel)!;
        SeedBlockedPair(viewModel, service);
        var blocked = viewModel.Columns[0].Cards.Single(c => c.Id == "b");
        var done = viewModel.Columns.Single(c => c.Column.Role == enki.Models.CoreColumnRole.Done);

        viewModel.MoveCard(blocked, done, null);
        window.UpdateLayout();

        // El contenido del diálogo vive en el Popup de un ModalOverlayHost, que headless no abre: se
        // comprueba que el host está abierto y qué dice su tarjeta.
        var title = enki.Localization.LocalizationService.Instance["Kanban_BlockedMove_Title"];
        var host = view.GetLogicalDescendants().OfType<enki.Controls.ModalOverlayHost>()
            .Single(h => h.Content is Border card && card.GetLogicalDescendants().OfType<TextBlock>()
                .Any(t => string.Equals(t.Text, title, StringComparison.OrdinalIgnoreCase)));
        Assert.True(host.IsOpen);
        var message = ((Border)host.Content!).GetLogicalDescendants().OfType<TextBlock>()
            .Single(t => t.Text is not null && t.Text.Contains("Redactar informe", StringComparison.Ordinal));
        Assert.Contains("Enviar informe", message.Text, StringComparison.Ordinal);
        window.Close();
    }
}
