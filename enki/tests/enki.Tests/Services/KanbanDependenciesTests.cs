using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Relación «bloquea a» entre tarjetas Kanban (MED-07), sobre un tablero en memoria.</summary>
public sealed class KanbanDependenciesTests
{
    private static (KanbanBoard Board, KanbanCard A, KanbanCard B, KanbanCard C) Board()
    {
        var a = new KanbanCard { Id = "a", Title = "A" };
        var b = new KanbanCard { Id = "b", Title = "B" };
        var c = new KanbanCard { Id = "c", Title = "C" };
        var board = new KanbanBoard
        {
            Name = "T",
            Columns =
            [
                new KanbanColumn { Id = "todo", Title = "Todo", Role = CoreColumnRole.Backlog, Cards = [a, b] },
                new KanbanColumn { Id = "doing", Title = "Doing", Role = CoreColumnRole.InProgress, Cards = [] },
                new KanbanColumn { Id = "done", Title = "Done", Role = CoreColumnRole.Done, Cards = [c] }
            ]
        };
        return (board, a, b, c);
    }

    [Fact]
    public void AddBlock_StoresOnlyTheBlockingHalf_AndDerivesTheOther()
    {
        var (board, a, b, _) = Board();

        Assert.Equal(KanbanBlockResult.Added, KanbanDependencies.AddBlock(board, a, b));

        Assert.Equal(["b"], a.Blocks);
        Assert.Empty(b.Blocks);
        Assert.Equal([a], KanbanDependencies.ActiveBlockers(board, b));
        Assert.True(KanbanDependencies.IsBlocked(board, b));
        Assert.False(KanbanDependencies.IsBlocked(board, a));
        Assert.Equal([b], KanbanDependencies.BlockedBy(board, a));
    }

    [Fact]
    public void AddBlock_RejectsSelfDuplicatesAndMissingCards()
    {
        var (board, a, b, _) = Board();
        var ghost = new KanbanCard { Id = "ghost", Title = "Fuera" };

        Assert.Equal(KanbanBlockResult.SameCard, KanbanDependencies.AddBlock(board, a, a));
        Assert.Equal(KanbanBlockResult.NotOnBoard, KanbanDependencies.AddBlock(board, a, ghost));
        Assert.Equal(KanbanBlockResult.Added, KanbanDependencies.AddBlock(board, a, b));
        Assert.Equal(KanbanBlockResult.AlreadyExists, KanbanDependencies.AddBlock(board, a, b));
        Assert.Equal(["b"], a.Blocks);
    }

    [Fact]
    public void AddBlock_RejectsDirectAndIndirectCycles()
    {
        var (board, a, b, c) = Board();
        KanbanDependencies.AddBlock(board, a, b);
        KanbanDependencies.AddBlock(board, b, c);

        Assert.Equal(KanbanBlockResult.Cycle, KanbanDependencies.AddBlock(board, b, a));
        Assert.Equal(KanbanBlockResult.Cycle, KanbanDependencies.AddBlock(board, c, a));
        Assert.Equal(KanbanBlockResult.Cycle, KanbanDependencies.AddBlock(board, c, b));
        Assert.Empty(c.Blocks);
    }

    [Fact]
    public void AnExistingCycleInAHandEditedFile_DoesNotHangTheCheck()
    {
        var (board, a, b, c) = Board();
        a.Blocks = ["b"];
        b.Blocks = ["a"];

        // El recorrido termina aunque a y b se bloqueen entre sí, y no inventa ciclos ajenos a ellas.
        Assert.Equal(KanbanBlockResult.Added, KanbanDependencies.CanBlock(board, c, a));
        Assert.Equal(KanbanBlockResult.Added, KanbanDependencies.CanBlock(board, a, c));
        Assert.Equal(KanbanBlockResult.AlreadyExists, KanbanDependencies.CanBlock(board, a, b));
    }

    [Fact]
    public void FinishingTheBlocker_ReleasesTheBlockedCard_WithoutChangingWhatIsStored()
    {
        var (board, a, b, _) = Board();
        KanbanDependencies.AddBlock(board, a, b);

        // A pasa a «Hecho»: B deja de estar bloqueada, pero la relación guardada sigue siendo la misma.
        board.Columns[0].Cards.Remove(a);
        board.Columns[2].Cards.Add(a);

        Assert.False(KanbanDependencies.IsBlocked(board, b));
        Assert.Equal(["b"], a.Blocks);
        Assert.Empty(KanbanDependencies.ActiveBlockers(board, b));
        Assert.Single(KanbanDependencies.BlockersOf(board, b));
    }

    [Fact]
    public void ACompletedOrArchivedBlocker_DoesNotBlock()
    {
        var (board, a, b, _) = Board();
        KanbanDependencies.AddBlock(board, a, b);

        a.CompletedAtUtc = System.DateTime.UtcNow;

        Assert.False(KanbanDependencies.IsBlocked(board, b));
    }

    [Fact]
    public void ADeletedBlocker_NoLongerBlocks_AndTheDanglingIdIsHarmless()
    {
        var (board, a, b, _) = Board();
        KanbanDependencies.AddBlock(board, a, b);

        board.Columns[0].Cards.Remove(b);

        Assert.Empty(KanbanDependencies.BlockedBy(board, a));
        Assert.Equal(["b"], a.Blocks);
        board.Columns[0].Cards.Add(b);
        Assert.True(KanbanDependencies.IsBlocked(board, b));
    }

    [Fact]
    public void RemoveBlock_ReportsWhetherThereWasAnything()
    {
        var (board, a, b, _) = Board();
        KanbanDependencies.AddBlock(board, a, b);

        Assert.True(KanbanDependencies.RemoveBlock(a, "b"));
        Assert.False(KanbanDependencies.RemoveBlock(a, "b"));
        Assert.False(KanbanDependencies.IsBlocked(board, b));
    }

    [Fact]
    public void TheRelation_RoundTripsThroughTheBoardFile()
    {
        var (board, a, b, _) = Board();
        KanbanDependencies.AddBlock(board, a, b);

        var json = System.Text.Json.JsonSerializer.Serialize(board);
        var restored = System.Text.Json.JsonSerializer.Deserialize<KanbanBoard>(json)!;

        var restoredA = restored.Columns.SelectMany(c => c.Cards).Single(c => c.Id == "a");
        Assert.Equal(["b"], restoredA.Blocks);
    }
}
