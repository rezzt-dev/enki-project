using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Regresión de maquetación de la Papelera: siembra un elemento en cada una de las siete
/// plantillas de tarjeta (documento, archivo, tarea Kanban, lista Polar, tarea Polar, recordatorio y
/// sesión), renderiza la vista real y comprueba dos invariantes que antes no se cumplían y que no se ven
/// en un test de texto plano:
///
/// 1. todas las tarjetas miden exactamente lo mismo — antes cada sección declaraba su propio tamaño
///    (224x150 / 224x120 / 224x100) y las filas de un WrapPanel no cuadraban entre secciones;
/// 2. dentro de cada tarjeta ningún texto se arregla más bajo de lo que pidió al medirse y ningún botón
///    se sale de la caja — en las tarjetas de 100px el título de dos líneas se recortaba a media letra
///    porque la fila central era <c>*</c> y no le quedaba alto.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class TrashViewCardLayoutTests : IDisposable
{
    private const int ExpectedCardCount = 7;

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");

    public TrashViewCardLayoutTests() => HeadlessApp.EnsureInitialized();

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>Deja exactamente una tarjeta en cada categoría, con títulos largos a propósito: el caso
    /// que reventaba la maquetación anterior es justo el título que necesita dos líneas.</summary>
    private (Window Window, TrashViewModel ViewModel) CreateSeededView(double windowWidth)
    {
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.InitializeVault(Path.Combine(_tempDir, "vault"), seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _tempDir);
        var kanban = new KanbanService(_tempDir);
        var polar = new PolarTaskService(_tempDir);
        var reminders = new ReminderService(_tempDir);

        const string LongTitle = "Un titulo deliberadamente largo que necesita dos lineas";

        var note = vault.CreateNote(LongTitle);
        vault.DeleteNote(note);

        var sourceFile = Path.Combine(_tempDir, "documento-con-nombre-larguisimo.txt");
        File.WriteAllText(sourceFile, "contenido");
        vault.ImportFile(sourceFile);
        vault.DeleteFile(vault.GetFiles().Single());

        kanban.DefaultBoard.TrashedCards.Add(new KanbanTrashedCard
        {
            Card = new KanbanCard { Id = "c1", Title = LongTitle },
            ColumnId = "col-1",
            ColumnTitle = "Una columna con nombre largo"
        });

        var list = polar.CreateList(LongTitle, "format-list-bulleted", "#888888", isDependencyChain: false);
        var task = polar.CreateTask(list, LongTitle, string.Empty, 0, null, PolarRecurrence.None, 0, []);
        polar.DeleteTask(task);
        polar.DeleteList(list);

        var reminder = reminders.Create(LongTitle, string.Empty, DateTime.UtcNow.AddHours(1), "bell");
        reminders.Delete(reminder.Id);

        var session = sessions.Create(LongTitle);
        sessions.Delete(session);

        var viewModel = new TrashViewModel(vault, kanban, polar, reminders, notifications, sessions);
        var window = new Window { Content = new TrashView { DataContext = viewModel }, Width = windowWidth, Height = 900 };
        window.Show();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        return (window, viewModel);
    }

    private static List<Border> Cards(Window window) => window.GetVisualDescendants()
        .OfType<Border>()
        .Where(b => b.Classes.Contains("trash-card"))
        .ToList();

    [Theory]
    [InlineData(1000d)]
    [InlineData(1600d)]
    public void EveryCard_AcrossEverySection_HasTheSameBox(double windowWidth)
    {
        var (window, _) = CreateSeededView(windowWidth);

        var cards = Cards(window);
        Assert.Equal(ExpectedCardCount, cards.Count);

        var reference = cards[0].Bounds.Size;
        Assert.All(cards, card => Assert.Equal(reference, card.Bounds.Size));

        window.Close();
    }

    [Theory]
    [InlineData(1000d)]
    [InlineData(1600d)]
    public void NoCardContent_IsSqueezedOrOverflowsItsCard(double windowWidth)
    {
        var (window, _) = CreateSeededView(windowWidth);

        var cards = Cards(window);
        Assert.Equal(ExpectedCardCount, cards.Count);

        // El alto que un TextBlock declara tras el pase de layout ya viene recortado por la altura que
        // le concedió su Grid, así que compararlo con su Bounds no detecta nada: hay que volver a
        // medirlo con altura infinita para saber cuánto necesitaba de verdad (dentro del límite de su
        // propio MaxLines) y contrastar ESO con lo que acabó ocupando.
        var texts = cards
            .SelectMany(card => card.GetVisualDescendants().OfType<TextBlock>())
            .Select(text => (Text: text, Arranged: text.Bounds.Height, Width: text.Bounds.Width))
            .ToList();

        foreach (var (text, arranged, width) in texts)
        {
            text.Measure(new Size(width, double.PositiveInfinity));
            var natural = text.DesiredSize.Height - text.Margin.Top - text.Margin.Bottom;
            Assert.True(arranged + 0.5 >= natural,
                $"'{text.Text}' se arregló a {arranged:0.##}px necesitando {natural:0.##}px — se recorta.");
        }

        foreach (var card in cards)
        {
            foreach (var button in card.GetVisualDescendants().OfType<Button>())
            {
                var origin = button.TranslatePoint(new Point(0, 0), card) ?? default;
                Assert.True(origin.Y >= -0.5 && origin.Y + button.Bounds.Height <= card.Bounds.Height + 0.5,
                    $"Un botón de acción se sale de su tarjeta (y={origin.Y:0.##}, alto={button.Bounds.Height:0.##}, tarjeta={card.Bounds.Height:0.##}).");
                Assert.True(origin.X >= -0.5 && origin.X + button.Bounds.Width <= card.Bounds.Width + 0.5,
                    $"Un botón de acción se sale de su tarjeta (x={origin.X:0.##}, ancho={button.Bounds.Width:0.##}, tarjeta={card.Bounds.Width:0.##}).");
            }
        }

        window.Close();
    }

    /// <summary>Los botones "VACIAR" de cabecera de sección usan <c>Button.pill.pill-compact</c> porque
    /// <c>Button.pill</c> fija <c>Height="40"</c>: con solo un <c>Padding</c> inline seguían midiendo 40px
    /// y competían visualmente con el "VACIAR PAPELERA" de la cabecera de página, que es la acción
    /// destructiva mayor.</summary>
    [Fact]
    public void SectionEmptyButtons_AreShorterThanThePageLevelEmptyTrashButton()
    {
        var (window, _) = CreateSeededView(1200d);

        var pills = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("pill"))
            .ToList();

        var compact = pills.Where(b => b.Classes.Contains("pill-compact")).ToList();
        var pageLevel = pills.Where(b => !b.Classes.Contains("pill-compact")).ToList();

        Assert.Equal(6, compact.Count);
        var pageLevelHeight = Assert.Single(pageLevel).Bounds.Height;
        Assert.All(compact, b => Assert.True(b.Bounds.Height < pageLevelHeight,
            $"Un 'VACIAR' de sección mide {b.Bounds.Height:0.##}px, igual o más que el 'VACIAR PAPELERA' ({pageLevelHeight:0.##}px)."));

        window.Close();
    }
}
