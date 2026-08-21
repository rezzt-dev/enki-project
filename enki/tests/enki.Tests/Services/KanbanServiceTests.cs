using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="KanbanService"/>, including the optional <c>storageDirectory</c>
/// constructor added so tests can write to a throwaway folder instead of the developer's real
/// <c>%AppData%/Enki/kanban.json</c>.</summary>
public class KanbanServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    [Fact]
    public void Constructor_WithoutStorageDirectory_LoadsBoard()
    {
        var service = new KanbanService();

        Assert.NotNull(service.Board);
        Assert.NotEmpty(service.Board.Columns);
    }

    [Fact]
    public void Constructor_WithStorageDirectory_CreatesSeedBoard_WhenFileMissing()
    {
        var service = new KanbanService(_storageDirectory);

        Assert.NotNull(service.Board);
        Assert.NotEmpty(service.Board.Columns);
        Assert.Contains(service.Board.Columns, c => c.Cards.Count > 0);
    }

    [Fact]
    public void Save_PersistsChanges_ToTheSameDirectory()
    {
        var first = new KanbanService(_storageDirectory);
        first.Board.Name = "Tablero de prueba";
        first.Save();

        var second = new KanbanService(_storageDirectory);

        Assert.Equal("Tablero de prueba", second.Board.Name);
    }

    [Fact]
    public void Save_PersistsAddedCards()
    {
        var first = new KanbanService(_storageDirectory);
        var column = first.Board.Columns[0];
        column.Cards.Add(new KanbanCard
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "Tarjeta de prueba"
        });
        first.Save();

        var second = new KanbanService(_storageDirectory);

        Assert.Contains(second.Board.Columns.SelectMany(c => c.Cards), c => c.Title == "Tarjeta de prueba");
    }

    [Fact]
    public void RelinkNotePath_UpdatesMatchingCards_OnBoardAndInTrash()
    {
        var service = new KanbanService(_storageDirectory);
        var boardCard = service.Board.Columns[0].Cards[0];
        boardCard.LinkedNotePath = "/vault/Old.md";

        var trashedCard = new KanbanCard { Id = "trashed", Title = "Trashed", LinkedNotePath = "/vault/Old.md" };
        service.Board.TrashedCards.Add(new KanbanTrashedCard { Card = trashedCard, ColumnId = "c1", ColumnTitle = "Col" });

        service.RelinkNotePath("/vault/Old.md", "/vault/New.md");

        Assert.Equal("/vault/New.md", boardCard.LinkedNotePath);
        Assert.Equal("/vault/New.md", trashedCard.LinkedNotePath);
    }

    [Fact]
    public void RelinkNotePath_IgnoresCardsLinkedToOtherPaths()
    {
        var service = new KanbanService(_storageDirectory);
        var card = service.Board.Columns[0].Cards[0];
        card.LinkedNotePath = "/vault/Unrelated.md";

        service.RelinkNotePath("/vault/Old.md", "/vault/New.md");

        Assert.Equal("/vault/Unrelated.md", card.LinkedNotePath);
    }

    [Fact]
    public void RelinkNotePath_Persists_SoItSurvivesReload()
    {
        var first = new KanbanService(_storageDirectory);
        first.Board.Columns[0].Cards[0].LinkedNotePath = "/vault/Old.md";
        first.Save();

        first.RelinkNotePath("/vault/Old.md", "/vault/New.md");

        var second = new KanbanService(_storageDirectory);
        Assert.Equal("/vault/New.md", second.Board.Columns[0].Cards[0].LinkedNotePath);
    }

    /// <summary>Purga automática de la papelera del tablero (Fase 3 de docs/analisis-proyecto/roadmap.md):
    /// al arrancar, las tarjetas que llevan más de 30 días en la papelera se eliminan físicamente — mismo
    /// criterio de retención que <c>ReminderService.PurgeOldTombstones</c> — y las recientes se conservan.</summary>
    [Fact]
    public void Constructor_PurgesTrashedCardsOlderThan30Days_ButKeepsRecentOnes()
    {
        var first = new KanbanService(_storageDirectory);
        first.Board.TrashedCards.Add(new KanbanTrashedCard
        {
            Card = new KanbanCard { Id = "old", Title = "Vieja" },
            ColumnId = "c1",
            ColumnTitle = "Col",
            DeletedUtc = DateTime.UtcNow.AddDays(-31)
        });
        first.Board.TrashedCards.Add(new KanbanTrashedCard
        {
            Card = new KanbanCard { Id = "recent", Title = "Reciente" },
            ColumnId = "c1",
            ColumnTitle = "Col",
            DeletedUtc = DateTime.UtcNow.AddDays(-2)
        });
        first.Save();

        var second = new KanbanService(_storageDirectory);

        Assert.DoesNotContain(second.Board.TrashedCards, t => t.Card.Id == "old");
        Assert.Contains(second.Board.TrashedCards, t => t.Card.Id == "recent");

        // La purga se persiste: una tercera carga no recupera la tarjeta vieja.
        var third = new KanbanService(_storageDirectory);
        Assert.DoesNotContain(third.Board.TrashedCards, t => t.Card.Id == "old");
    }

    // ===================== CardPermanentlyDeleted (Fase 10) =====================
    // Fase 10 (docs/analisis-proyecto/roadmap.md): el compositor engancha este evento a
    // DismissedReminderService.RemoveForCard para que un descarte de recordatorio no quede huérfano tras
    // borrar la tarjeta para siempre — cubre las tres vías que de verdad quitan una tarjeta de la papelera.

    [Fact]
    public void PermanentlyDeleteTrashedCard_RemovesItAndRaisesCardPermanentlyDeleted()
    {
        var service = new KanbanService(_storageDirectory);
        var trashed = new KanbanTrashedCard
        {
            Card = new KanbanCard { Id = "card-1", Title = "Tarjeta" },
            ColumnId = "c1",
            ColumnTitle = "Col",
            DeletedUtc = DateTime.UtcNow
        };
        service.Board.TrashedCards.Add(trashed);
        service.Save();

        string? deletedCardId = null;
        service.CardPermanentlyDeleted += id => deletedCardId = id;

        service.PermanentlyDeleteTrashedCard(trashed);

        Assert.Empty(service.Board.TrashedCards);
        Assert.Equal("card-1", deletedCardId);

        var reloaded = new KanbanService(_storageDirectory);
        Assert.Empty(reloaded.Board.TrashedCards);
    }

    [Fact]
    public void PermanentlyDeleteAllTrashedCards_ClearsEverything_AndRaisesTheEventPerCard()
    {
        var service = new KanbanService(_storageDirectory);
        service.Board.TrashedCards.Add(new KanbanTrashedCard
        { Card = new KanbanCard { Id = "card-1", Title = "Uno" }, ColumnId = "c1", ColumnTitle = "Col", DeletedUtc = DateTime.UtcNow });
        service.Board.TrashedCards.Add(new KanbanTrashedCard
        { Card = new KanbanCard { Id = "card-2", Title = "Dos" }, ColumnId = "c1", ColumnTitle = "Col", DeletedUtc = DateTime.UtcNow });
        service.Save();

        var deletedCardIds = new System.Collections.Generic.List<string>();
        service.CardPermanentlyDeleted += id => deletedCardIds.Add(id);

        var removedCount = service.PermanentlyDeleteAllTrashedCards();

        Assert.Equal(2, removedCount);
        Assert.Empty(service.Board.TrashedCards);
        Assert.Equal(["card-1", "card-2"], deletedCardIds);
    }

    [Fact]
    public void PermanentlyDeleteAllTrashedCards_WithNothingTrashed_ReturnsZero_AndRaisesNothing()
    {
        var service = new KanbanService(_storageDirectory);
        var raised = false;
        service.CardPermanentlyDeleted += _ => raised = true;

        var removedCount = service.PermanentlyDeleteAllTrashedCards();

        Assert.Equal(0, removedCount);
        Assert.False(raised);
    }
}
