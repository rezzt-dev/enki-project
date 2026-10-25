using System;
using System.Linq;
using enki.Models;

namespace enki.Services;

public sealed partial class KanbanService
{
    /// <summary>Resuelve la entidad real, incluidas las tarjetas archivadas.</summary>
    public KanbanCardLocation? FindCardLocation(string cardId)
    {
        foreach (var board in Boards)
        foreach (var column in board.Columns)
            if (column.Cards.FirstOrDefault(c => c.Id == cardId) is { } card)
                return new(board, column, card);
        return null;
    }

    public KanbanBoard? FindTrashBoard(KanbanTrashedCard card) =>
        Boards.FirstOrDefault(b => b.TrashedCards.Contains(card));

    public string ColumnLabel(KanbanColumn column) => Boards.Count > 1
        ? $"{Boards.First(b => b.Columns.Contains(column)).Name} · {column.Title}" : column.Title;

    private string ValidateBoardName(string name, string? excludingId = null)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 80) throw new ArgumentException("Kanban_Multi_InvalidName");
        if (Boards.Any(b => b.Id != excludingId && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Kanban_Multi_DuplicateName");
        return name;
    }

    /// <summary>Crea un tablero vacío con las tres columnas base, sin datos de ejemplo.</summary>
    public KanbanBoard CreateBoard(string name)
    {
        name = ValidateBoardName(name);
        var board = CreateSeedBoard();
        board.Name = name;
        foreach (var column in board.Columns) column.Cards.Clear();
        _store.Boards.Add(board);
        try { Save(); }
        catch { _store.Boards.Remove(board); throw; }
        return board;
    }

    public void RenameBoard(string boardId, string name)
    {
        var board = Boards.First(b => b.Id == boardId);
        name = ValidateBoardName(name, boardId);
        var previous = board.Name;
        board.Name = name;
        try { Save(); }
        catch { board.Name = previous; throw; }
    }

    public void SetDefaultBoard(string boardId)
    {
        if (!Boards.Any(b => b.Id == boardId)) throw new ArgumentException("Kanban_Multi_MissingDestination");
        var previous = _store.DefaultBoardId;
        _store.DefaultBoardId = boardId;
        try { Save(); }
        catch { _store.DefaultBoardId = previous; throw; }
    }

    public bool CanDeleteBoard(KanbanBoard board) => Boards.Contains(board) && Boards.Count > 1
        && board.Columns.All(c => c.Cards.Count == 0) && board.TrashedCards.Count == 0;

    public void DeleteEmptyBoard(string boardId)
    {
        var board = Boards.First(b => b.Id == boardId);
        if (!CanDeleteBoard(board)) throw new InvalidOperationException("Kanban_Multi_CannotDelete");
        var index = _store.Boards.IndexOf(board);
        var previousDefault = _store.DefaultBoardId;
        _store.Boards.RemoveAt(index);
        if (previousDefault == boardId) _store.DefaultBoardId = Boards[0].Id;
        try { Save(); }
        catch
        {
            _store.Boards.Insert(index, board);
            _store.DefaultBoardId = previousDefault;
            throw;
        }
    }

    /// <summary>Guarda un borrador sobre la tarjeta real y conserva el original si falla el disco.</summary>
    public void SaveCardEdits(string cardId, string columnId, KanbanCard draft)
    {
        var location = FindCardLocation(cardId) ?? throw new ArgumentException("Kanban_Multi_MissingDestination");
        var card = location.Card;
        var previous = new KanbanCard
        {
            Id = card.Id, Title = card.Title, Description = card.Description, Priority = card.Priority,
            StartDateUtc = card.StartDateUtc, DueDateUtc = card.DueDateUtc, DurationMinutes = card.DurationMinutes, LinkedNotePath = card.LinkedNotePath,
            Tags = card.Tags, Subtasks = card.Subtasks
        };
        var next = location.Column.Id == columnId
            ? location.Column.Cards.SkipWhile(c => c != card).Skip(1).FirstOrDefault()?.Id : null;
        ApplyCardDraft(card, draft);
        try { MoveCardToBoard(cardId, location.Board.Id, columnId, next); }
        catch { ApplyCardDraft(card, previous); throw; }
    }

    private static void ApplyCardDraft(KanbanCard card, KanbanCard draft)
    {
        card.Title = draft.Title;
        card.Description = draft.Description;
        card.Priority = draft.Priority;
        card.StartDateUtc = draft.StartDateUtc;
        card.DueDateUtc = draft.DueDateUtc;
        card.DurationMinutes = draft.DurationMinutes;
        card.LinkedNotePath = draft.LinkedNotePath;
        card.Tags = draft.Tags;
        card.Subtasks = draft.Subtasks;
    }

    /// <summary>Inserta una tarjeta solo en un destino vigente y revierte un fallo de persistencia.</summary>
    public void CreateCard(string boardId, string columnId, KanbanCard card)
    {
        var column = Boards.FirstOrDefault(b => b.Id == boardId)?.Columns.FirstOrDefault(c => c.Id == columnId)
            ?? throw new ArgumentException("Kanban_Multi_MissingDestination");
        if (FindCardLocation(card.Id) is not null || AllTrashedCards.Any(t => t.Card.Id == card.Id))
            throw new ArgumentException("Kanban_Multi_MissingDestination");
        column.Cards.Add(card);
        try { Save(); }
        catch { column.Cards.Remove(card); throw; }
    }

    /// <summary>Envía la tarjeta a la papelera de su tablero recordando columna y posición, para que
    /// <see cref="RestoreTrashedCard"/> la devuelva al mismo sitio. La marca de archivado se conserva: es la
    /// restauración la que decide quitarla. Ante un fallo de disco la tarjeta vuelve a su columna.</summary>
    public KanbanTrashedCard TrashCard(string cardId)
    {
        var location = FindCardLocation(cardId) ?? throw new ArgumentException("Kanban_Multi_MissingDestination");
        var index = location.Column.Cards.IndexOf(location.Card);
        var trashed = new KanbanTrashedCard
        {
            Card = location.Card,
            ColumnId = location.Column.Id,
            ColumnTitle = location.Column.Title,
            CardIndex = index
        };
        location.Column.Cards.RemoveAt(index);
        location.Board.TrashedCards.Add(trashed);
        try { Save(); }
        catch
        {
            location.Board.TrashedCards.Remove(trashed);
            location.Column.Cards.Insert(index, location.Card);
            throw;
        }
        return trashed;
    }

    /// <summary>Devuelve una tarjeta de la papelera a su columna y posición originales, o a la primera columna
    /// de su tablero si la original ya no existe. La columna se resuelve antes de sacarla de la papelera: sin
    /// ninguna columna válida la tarjeta se queda allí, recuperable, y se lanza
    /// <see cref="InvalidOperationException"/>. Restaurar significa "vuelve al tablero", así que pierde la
    /// marca de archivado (docs/agent-docs/kanban-tareas-archivadas.md).</summary>
    /// <returns>La columna en la que quedó la tarjeta.</returns>
    public KanbanColumn RestoreTrashedCard(KanbanTrashedCard trashed)
    {
        var board = FindTrashBoard(trashed) ?? throw new ArgumentException("Kanban_Multi_MissingDestination");
        var targetColumn = board.Columns.FirstOrDefault(c => c.Id == trashed.ColumnId) ?? board.Columns.FirstOrDefault()
            ?? throw new InvalidOperationException("Trash_Notify_CardRestoreFailed_Title");

        var trashIndex = board.TrashedCards.IndexOf(trashed);
        var archivedAt = trashed.Card.ArchivedAtUtc;
        board.TrashedCards.RemoveAt(trashIndex);
        var insertIndex = trashed.CardIndex is { } index ? Math.Min(index, targetColumn.Cards.Count) : targetColumn.Cards.Count;
        targetColumn.Cards.Insert(insertIndex, trashed.Card);
        trashed.Card.ArchivedAtUtc = null;

        try { Save(); }
        catch
        {
            targetColumn.Cards.Remove(trashed.Card);
            board.TrashedCards.Insert(trashIndex, trashed);
            trashed.Card.ArchivedAtUtc = archivedAt;
            throw;
        }
        return targetColumn;
    }

    /// <summary>Archiva o desarchiva una tarjeta de la columna "Hecho". Archivar no la mueve: sigue en su
    /// columna y es el tablero el que deja de dibujarla; como archivar es afirmar que está terminada, sella
    /// también su fecha de completado si no la tenía (ver <c>KanbanViewModel.ArchiveCardCore</c>).</summary>
    public void SetCardArchived(string cardId, bool archived)
    {
        var location = FindCardLocation(cardId) ?? throw new ArgumentException("Kanban_Multi_MissingDestination");
        var card = location.Card;
        if (archived && location.Column.Role != CoreColumnRole.Done)
            throw new InvalidOperationException("Kanban_Archive_OnlyDone");
        if (card.IsArchived == archived) return;

        var (archivedAt, completedAt) = (card.ArchivedAtUtc, card.CompletedAtUtc);
        card.ArchivedAtUtc = archived ? DateTime.UtcNow : null;
        if (archived) card.CompletedAtUtc ??= DateTime.UtcNow;
        try { Save(); }
        catch
        {
            (card.ArchivedAtUtc, card.CompletedAtUtc) = (archivedAt, completedAt);
            throw;
        }
    }

    /// <summary>Mueve la misma tarjeta y aplica las transiciones de rol en una única escritura.
    /// Ante fallo restaura posición, archivo, completado y subtareas sobre las mismas referencias.</summary>
    public void MoveCardToBoard(string cardId, string boardId, string columnId, string? beforeCardId = null)
    {
        var source = FindCardLocation(cardId) ?? throw new ArgumentException("Kanban_Multi_MissingDestination");
        var target = Boards.FirstOrDefault(b => b.Id == boardId)?.Columns.FirstOrDefault(c => c.Id == columnId)
            ?? throw new ArgumentException("Kanban_Multi_MissingDestination");
        if (beforeCardId == cardId) return;
        var before = beforeCardId is null ? null : target.Cards.FirstOrDefault(c => c.Id == beforeCardId)
            ?? throw new ArgumentException("Kanban_Multi_MissingDestination");
        var card = source.Card;
        var index = source.Column.Cards.IndexOf(card);
        var completed = card.CompletedAtUtc;
        var archived = card.ArchivedAtUtc;
        var subtasks = card.Subtasks.Select(s => s.IsDone).ToArray();
        source.Column.Cards.Remove(card);
        target.Cards.Insert(before is null ? target.Cards.Count : target.Cards.IndexOf(before), card);
        if (source.Column.Role != target.Role)
        {
            card.CompletedAtUtc = target.Role == CoreColumnRole.Done ? card.CompletedAtUtc ?? DateTime.UtcNow : null;
            if (target.Role != CoreColumnRole.Done) card.ArchivedAtUtc = null;
            if (target.Role is CoreColumnRole.Done or CoreColumnRole.Backlog)
                foreach (var subtask in card.Subtasks) subtask.IsDone = target.Role == CoreColumnRole.Done;
        }
        try { Save(); }
        catch
        {
            target.Cards.Remove(card);
            source.Column.Cards.Insert(index, card);
            card.CompletedAtUtc = completed;
            card.ArchivedAtUtc = archived;
            for (var i = 0; i < subtasks.Length; i++) card.Subtasks[i].IsDone = subtasks[i];
            throw;
        }
    }
}
