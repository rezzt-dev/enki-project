using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public sealed class KanbanMultipleBoardsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-boards-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_directory, "kanban.json");
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void LegacyMigration_PreservesAllFieldsAndOriginalBytes_AndIsIdempotent()
    {
        Directory.CreateDirectory(_directory);
        var original = """
            {"Name":"Trabajo","Columns":[{"Id":"todo","Title":"Pendiente","IsCore":true,"Cards":[]},
            {"Id":"done","Title":"Hecho","Role":"Done","IsCore":true,"Cards":[
            {"Id":"card","Title":"Informe","Description":"Texto","Tags":["importante"],"LinkedNotePath":"/vault/report.md",
            "CreatedUtc":"2026-01-01T10:00:00Z","CompletedAtUtc":"2026-01-02T10:00:00Z","ArchivedAtUtc":"2026-01-03T10:00:00Z",
            "DueDateUtc":"2026-01-02T12:00:00Z","DurationMinutes":45,"Subtasks":[{"Id":"sub","Title":"Revisar","IsDone":true}]}]}],
            "TrashedCards":[{"Card":{"Id":"trash","Title":"Borrada"},"ColumnId":"done","ColumnTitle":"Hecho","DeletedUtc":"2099-01-01T00:00:00Z","CardIndex":0}]}
            """;
        File.WriteAllText(StorePath, original);
        var service = new KanbanService(_directory);
        Assert.False(service.LoadedFromCorruptData);
        Assert.Equal(original, File.ReadAllText(StorePath + ".pre-multiboard.bak"));
        var card = service.FindCardLocation("card")!.Card;
        Assert.Equal("/vault/report.md", card.LinkedNotePath);
        Assert.Equal(45, card.DurationMinutes);
        Assert.NotNull(card.ArchivedAtUtc);
        Assert.True(Assert.Single(card.Subtasks).IsDone);
        Assert.Single(service.AllTrashedCards);
        var serialized = File.ReadAllText(StorePath);
        var reopened = new KanbanService(_directory);
        Assert.Single(reopened.Boards);
        Assert.Equal(service.DefaultBoard.Id, reopened.DefaultBoard.Id);
        Assert.Equal(serialized, File.ReadAllText(StorePath));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"SchemaVersion\":99,\"Boards\":[]}")]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":1,\"Boards\":[null]}")]
    public void InvalidStore_IsPreservedAndCannotBeOverwritten(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StorePath, json);
        var service = new KanbanService(_directory);
        Assert.True(service.LoadedFromCorruptData);
        Assert.Throws<InvalidOperationException>(() => service.CreateBoard("Trabajo"));
        Assert.Single(service.Boards);
        Assert.Equal(json, File.ReadAllText(StorePath));
    }

    [Fact]
    public void Migration_BackupFailure_DoesNotReplaceOriginal()
    {
        Directory.CreateDirectory(_directory);
        var json = "{\"Name\":\"Original\",\"Columns\":[]}";
        File.WriteAllText(StorePath, json);
        Directory.CreateDirectory(StorePath + ".pre-multiboard.bak");
        var service = new KanbanService(_directory);
        Assert.True(service.LoadedFromCorruptData);
        Assert.Equal(json, File.ReadAllText(StorePath));
    }

    [Fact]
    public void Boards_ValidateNames_PersistDefault_AndProtectContent()
    {
        var service = new KanbanService(_directory);
        var original = service.DefaultBoard;
        var board = service.CreateBoard("  Estudios  ");
        Assert.Equal("Estudios", board.Name);
        Assert.Equal(original, service.DefaultBoard);
        Assert.Equal(3, board.Columns.Count);
        Assert.All(board.Columns, c => Assert.Empty(c.Cards));
        Assert.Throws<ArgumentException>(() => service.CreateBoard("estudios"));
        Assert.Throws<ArgumentException>(() => service.RenameBoard(board.Id, "  "));
        Assert.Throws<ArgumentException>(() => service.CreateBoard(new string('x', 81)));
        service.SetDefaultBoard(board.Id);
        Assert.Equal(board.Id, new KanbanService(_directory).DefaultBoard.Id);
        Assert.Throws<InvalidOperationException>(() => service.DeleteEmptyBoard(original.Id));
        board.TrashedCards.Add(new() { Card = new() { Id = "trash", Title = "Papelera" }, ColumnId = board.Columns[0].Id, ColumnTitle = "Origen" });
        Assert.False(service.CanDeleteBoard(board));
        board.TrashedCards.Clear();
        service.DeleteEmptyBoard(board.Id);
        Assert.Equal(original, service.DefaultBoard);
        foreach (var column in original.Columns) column.Cards.Clear();
        Assert.False(service.CanDeleteBoard(original));
    }

    [Fact]
    public void Move_PreservesIdentityAndEquivalentRoleState_ThenAppliesRoleTransition()
    {
        var service = new KanbanService(_directory);
        var source = service.DefaultBoard.Columns.Single(c => c.Role == CoreColumnRole.Done);
        var card = source.Cards[0];
        card.ArchivedAtUtc = card.CompletedAtUtc = DateTime.UtcNow.AddDays(-2);
        card.LinkedNotePath = "/vault/note.md";
        card.Subtasks.Add(new() { Id = "sub", Title = "Paso", IsDone = false });
        var completed = card.CompletedAtUtc;
        var archived = card.ArchivedAtUtc;
        var target = service.CreateBoard("Destino");
        var changes = 0;
        service.BoardChanged += () => changes++;
        service.MoveCardToBoard(card.Id, target.Id, target.Columns.Single(c => c.Role == CoreColumnRole.Done).Id);
        Assert.Equal(1, changes);
        Assert.Same(card, service.FindCardLocation(card.Id)!.Card);
        Assert.Same(target, service.FindCardLocation(card.Id)!.Board);
        Assert.Equal(completed, card.CompletedAtUtc);
        Assert.Equal(archived, card.ArchivedAtUtc);
        Assert.False(card.Subtasks[0].IsDone);
        Assert.Equal("/vault/note.md", card.LinkedNotePath);
        service.MoveCardToBoard(card.Id, target.Id, target.Columns[0].Id);
        Assert.Null(card.ArchivedAtUtc);
        Assert.Null(card.CompletedAtUtc);
        service.MoveCardToBoard(card.Id, target.Id, target.Columns.Single(c => c.Role == CoreColumnRole.Done).Id);
        Assert.True(card.Subtasks[0].IsDone);
        Assert.NotNull(card.CompletedAtUtc);
    }

    [Fact]
    public void FailedMove_RestoresReferencesOrderAndState_WithoutPublishingChange()
    {
        var service = new KanbanService(_directory);
        var source = service.DefaultBoard.Columns[0];
        var card = source.Cards[0];
        card.Subtasks.Add(new() { Id = "sub", Title = "Paso" });
        var target = service.CreateBoard("Destino");
        File.Delete(StorePath);
        Directory.CreateDirectory(StorePath);
        var changes = 0;
        service.BoardChanged += () => changes++;
        Assert.ThrowsAny<Exception>(() => service.MoveCardToBoard(card.Id, target.Id, target.Columns[2].Id));
        Assert.Same(card, source.Cards[0]);
        Assert.Empty(target.Columns[2].Cards);
        Assert.Null(card.CompletedAtUtc);
        Assert.False(card.Subtasks[0].IsDone);
        Assert.Equal(0, changes);
        Assert.ThrowsAny<Exception>(() => service.CreateBoard("Fallará"));
        Assert.Equal(2, service.Boards.Count);
    }

    [Fact]
    public void GlobalQueriesRelinkingAndTrash_IncludeEveryBoard()
    {
        var service = new KanbanService(_directory);
        var board = service.CreateBoard("Otro");
        var card = new KanbanCard { Id = "linked", Title = "Vinculada", LinkedNotePath = "old.md" };
        board.Columns[0].Cards.Add(card);
        var trashed = new KanbanTrashedCard { Card = new() { Id = "trashed", Title = "Borrada", LinkedNotePath = "old.md" }, ColumnId = "deleted", ColumnTitle = "Antes" };
        board.TrashedCards.Add(trashed);
        service.RelinkNotePath("old.md", "new.md");
        Assert.Equal("new.md", card.LinkedNotePath);
        Assert.Equal("new.md", trashed.Card.LinkedNotePath);
        Assert.Same(board, service.FindTrashBoard(trashed));
        string? deleted = null;
        service.CardPermanentlyDeleted += id => deleted = id;
        service.PermanentlyDeleteTrashedCard(trashed);
        Assert.Empty(board.TrashedCards);
        Assert.Equal("trashed", deleted);
    }
    [Fact]
    public void CalendarAggregation_IncludesOtherBoardsAndKeepsColumnColorIdentity()
    {
        var service = new KanbanService(_directory);
        var board = service.CreateBoard("Personal");
        var due = DateTime.UtcNow.AddDays(3);
        var card = new KanbanCard { Id = "scheduled", Title = "Cita", DueDateUtc = due, ArchivedAtUtc = DateTime.UtcNow };
        board.Columns[2].Cards.Add(card);
        var settings = new SettingsService(_directory);
        var auth = new PolarAuthService(settings);
        var tasks = new PolarTaskService(_directory);
        var reminders = new ReminderService(_directory);
        var colors = new CalendarLayerColorService(service, tasks, auth, settings);
        var aggregation = new CalendarAggregationService(service, tasks, reminders, auth, colors);
        var item = Assert.Single(aggregation.BuildItems(due.AddMinutes(-1), due.AddMinutes(1)), i => i.SourceId == card.Id);
        Assert.Equal($"Personal · {board.Columns[2].Title}", item.ParentContextLabel);
        Assert.Contains(CalendarLayerColorService.KanbanColumnColorKey(board.Columns[2].Id), colors.BuildMap().Keys);
    }

    [Fact]
    public void Purge_RetainsRecentTrashInEveryBoard_AndPersistsRemoval()
    {
        var service = new KanbanService(_directory);
        var board = service.CreateBoard("Histórico");
        board.TrashedCards.Add(new() { Card = new() { Id = "expired", Title = "Antigua" }, ColumnId = "old", ColumnTitle = "Old", DeletedUtc = DateTime.UtcNow.AddDays(-40) });
        board.TrashedCards.Add(new() { Card = new() { Id = "recent", Title = "Reciente" }, ColumnId = "old", ColumnTitle = "Old" });
        service.Save();
        var reloaded = new KanbanService(_directory);
        Assert.Equal("recent", Assert.Single(reloaded.AllTrashedCards).Card.Id);
        Assert.Equal("recent", Assert.Single(new KanbanService(_directory).AllTrashedCards).Card.Id);
    }

    [Fact]
    public void DuplicateIdentifiers_AreRejectedWithoutChangingFile()
    {
        var service = new KanbanService(_directory);
        var board = service.CreateBoard("Otro");
        board.Columns[0].Id = service.DefaultBoard.Columns[0].Id;
        Assert.Throws<InvalidDataException>(() => service.Save());
        File.WriteAllText(StorePath, JsonSerializer.Serialize(new KanbanStore { DefaultBoardId = service.DefaultBoard.Id, Boards = service.Boards.ToList() }));
        var original = File.ReadAllText(StorePath);
        var reopened = new KanbanService(_directory);
        Assert.True(reopened.LoadedFromCorruptData);
        Assert.Equal(original, File.ReadAllText(StorePath));
    }

}
