using System;
using System.Collections.Generic;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>Resultado de intentar añadir una relación «bloquea a».</summary>
public enum KanbanBlockResult
{
    Added,
    AlreadyExists,
    /// <summary>Una tarjeta no puede bloquearse a sí misma.</summary>
    SameCard,
    /// <summary>La relación cerraría un ciclo (A bloquea a B y B, directa o indirectamente, a A): ninguna
    /// de las dos podría terminarse nunca.</summary>
    Cycle,
    /// <summary>Alguna de las dos tarjetas no está en el tablero.</summary>
    NotOnBoard
}

/// <summary>Relación «bloquea a» entre tarjetas Kanban (MED-07). Calculada sobre los datos reales del
/// tablero (<see cref="KanbanCard.Blocks"/>): no hay un segundo almacén. Una tarjeta está <i>bloqueada</i>
/// mientras alguna tarjeta del mismo tablero que la bloquea siga sin terminar; terminar el bloqueo la
/// libera sin tocar nada guardado. Terminada significa en la columna «Hecho», con fecha de completado o
/// archivada.</summary>
public static class KanbanDependencies
{
    /// <summary>Tarjetas del tablero con su columna, por identificador.</summary>
    private static Dictionary<string, (KanbanCard Card, KanbanColumn Column)> Index(KanbanBoard board)
    {
        var index = new Dictionary<string, (KanbanCard, KanbanColumn)>(StringComparer.Ordinal);
        foreach (var column in board.Columns)
            foreach (var card in column.Cards)
                index[card.Id] = (card, column);
        return index;
    }

    /// <summary>¿Está terminada <paramref name="card"/> estando en <paramref name="column"/>?</summary>
    public static bool IsFinished(KanbanCard card, KanbanColumn column) =>
        column.Role == CoreColumnRole.Done || card.IsCompleted;

    /// <summary>Tarjetas que bloquean ahora mismo a <paramref name="card"/>: las que la tienen en su lista
    /// y todavía no están terminadas.</summary>
    public static IReadOnlyList<KanbanCard> ActiveBlockers(KanbanBoard board, KanbanCard card) =>
        BlockersOf(board, card).Where(b => !IsFinished(b.Card, b.Column)).Select(b => b.Card).ToList();

    /// <summary>Todas las tarjetas que declaran bloquear a <paramref name="card"/>, terminadas o no.</summary>
    public static IReadOnlyList<(KanbanCard Card, KanbanColumn Column)> BlockersOf(KanbanBoard board, KanbanCard card)
    {
        var result = new List<(KanbanCard, KanbanColumn)>();
        foreach (var column in board.Columns)
            foreach (var other in column.Cards)
                if (other.Blocks.Contains(card.Id, StringComparer.Ordinal) && !ReferenceEquals(other, card))
                    result.Add((other, column));
        return result;
    }

    /// <summary>Tarjetas de las que <paramref name="card"/> es bloqueo y que siguen en el tablero.</summary>
    public static IReadOnlyList<KanbanCard> BlockedBy(KanbanBoard board, KanbanCard card)
    {
        var index = Index(board);
        return card.Blocks.Where(id => index.ContainsKey(id)).Select(id => index[id].Card).ToList();
    }

    /// <summary>¿Tiene <paramref name="card"/> algún bloqueo sin terminar?</summary>
    public static bool IsBlocked(KanbanBoard board, KanbanCard card) => ActiveBlockers(board, card).Count > 0;

    /// <summary>Comprueba si <paramref name="blocker"/> puede bloquear a <paramref name="target"/> sin
    /// repetir la relación ni cerrar un ciclo, sin modificar nada.</summary>
    public static KanbanBlockResult CanBlock(KanbanBoard board, KanbanCard blocker, KanbanCard target)
    {
        if (ReferenceEquals(blocker, target) || blocker.Id == target.Id) return KanbanBlockResult.SameCard;
        var index = Index(board);
        if (!index.ContainsKey(blocker.Id) || !index.ContainsKey(target.Id)) return KanbanBlockResult.NotOnBoard;
        if (blocker.Blocks.Contains(target.Id, StringComparer.Ordinal)) return KanbanBlockResult.AlreadyExists;
        return ReachesThroughBlocks(index, target.Id, blocker.Id) ? KanbanBlockResult.Cycle : KanbanBlockResult.Added;
    }

    /// <summary>Añade «<paramref name="blocker"/> bloquea a <paramref name="target"/>» si es válido. No
    /// guarda el tablero: eso lo hace quien llama junto con el resto del cambio.</summary>
    public static KanbanBlockResult AddBlock(KanbanBoard board, KanbanCard blocker, KanbanCard target)
    {
        var result = CanBlock(board, blocker, target);
        if (result == KanbanBlockResult.Added) blocker.Blocks.Add(target.Id);
        return result;
    }

    /// <summary>Quita la relación si existe. Devuelve si había algo que quitar.</summary>
    public static bool RemoveBlock(KanbanCard blocker, string targetId) =>
        blocker.Blocks.RemoveAll(id => string.Equals(id, targetId, StringComparison.Ordinal)) > 0;

    /// <summary>¿Se llega desde <paramref name="fromId"/> hasta <paramref name="goalId"/> siguiendo «bloquea a»?
    /// Recorrido en anchura con conjunto de visitados, así que un ciclo ya existente (de un archivo editado a
    /// mano) no lo deja dando vueltas.</summary>
    private static bool ReachesThroughBlocks(Dictionary<string, (KanbanCard Card, KanbanColumn Column)> index, string fromId, string goalId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { fromId };
        var queue = new Queue<string>();
        queue.Enqueue(fromId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!index.TryGetValue(current, out var entry)) continue;
            foreach (var next in entry.Card.Blocks)
            {
                if (next == goalId) return true;
                if (visited.Add(next)) queue.Enqueue(next);
            }
        }
        return false;
    }
}
