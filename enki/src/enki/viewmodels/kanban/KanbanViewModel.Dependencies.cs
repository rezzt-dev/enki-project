using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Columna por la que se ordena la vista de lista (MED-07).</summary>
public enum KanbanListSortColumn
{
    Column,
    Title,
    Priority,
    DueDate,
    Blocked
}

/// <summary>Una fila de la vista de lista: la misma tarjeta del tablero (<see cref="Card"/>), no una copia,
/// más el nombre y la posición de su columna para poder mostrarla y ordenarla.</summary>
public sealed class KanbanListRowViewModel(KanbanCardViewModel card, string columnLabel, int columnOrder)
{
    public KanbanCardViewModel Card { get; } = card;
    public string ColumnLabel { get; } = columnLabel;
    public int ColumnOrder { get; } = columnOrder;
}

/// <summary>Una mudanza de columna que espera confirmación porque terminaría una tarjeta bloqueada.</summary>
public sealed record BlockedMoveRequest(KanbanCardViewModel Card, KanbanColumnViewModel Target,
    KanbanCardViewModel? Before, IReadOnlyList<string> Blockers);

/// <summary>Kanban › dependencias «bloquea a» y vista de lista (MED-07). Las dos viven sobre los datos del
/// tablero que ya existen (<see cref="KanbanCard.Blocks"/> y <c>Columns</c>): no hay segundo almacén ni
/// copia de las tarjetas.</summary>
public sealed partial class KanbanViewModel
{
    // ===================== Dependencias =====================

    /// <summary>Vuelve a resolver, para todas las tarjetas del tablero, qué las bloquea y a qué bloquean, y
    /// se lo sella a cada una. Barato (una pasada por las tarjetas) y sin efectos sobre lo guardado.</summary>
    private void RefreshDependencies()
    {
        var board = CurrentBoard;
        foreach (var column in Columns)
        foreach (var card in column.Cards)
        {
            var blockers = KanbanDependencies.ActiveBlockers(board, card.Card).Select(b => b.Title).ToList();
            // Una tarjeta terminada ya no bloquea a nadie, aunque la relación siga guardada.
            var blocking = IsFinished(board, card.Card)
                ? []
                : KanbanDependencies.BlockedBy(board, card.Card).Where(t => !IsFinished(board, t)).Select(t => t.Title).ToList();
            card.SetDependencies(blockers, blocking);
        }
        RebuildListRows();
    }

    private static bool IsFinished(KanbanBoard board, KanbanCard card)
    {
        var column = board.Columns.FirstOrDefault(c => c.Cards.Contains(card));
        return column is not null && KanbanDependencies.IsFinished(card, column);
    }

    /// <summary>Mudanza pendiente de confirmación, o <see langword="null"/>. La vista abre un diálogo
    /// mientras haya una.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingBlockedMoveMessage))]
    private BlockedMoveRequest? _pendingBlockedMove;

    /// <summary>Cuerpo del diálogo: qué tarjeta se quiere terminar y qué la bloquea todavía.</summary>
    public string PendingBlockedMoveMessage => PendingBlockedMove is { } request
        ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Kanban_BlockedMove_Message"],
            request.Card.Title, string.Join(", ", request.Blockers))
        : string.Empty;

    /// <summary>Decide qué hacer con una mudanza de una tarjeta bloqueada. Terminarla (columna «Hecho») no
    /// se hace sin confirmar: se guarda la petición y se devuelve <see langword="true"/> para que
    /// <c>MoveCard</c> no mueva nada todavía. Empezarla (columna «En curso») sí se hace, con un aviso, y
    /// devuelve <see langword="false"/>. Cualquier otra columna no tiene nada que decir.</summary>
    private bool GuardBlockedMove(KanbanCardViewModel card, KanbanColumnViewModel target, KanbanCardViewModel? before)
    {
        if (!card.IsBlocked) return false;

        var role = target.Column.Role;
        if (role == CoreColumnRole.Done)
        {
            PendingBlockedMove = new BlockedMoveRequest(card, target, before, card.BlockedByTitles);
            return true;
        }

        if (role == CoreColumnRole.InProgress)
        {
            _notificationService?.PushLocalized(
                LocalizedTextFormatter.Format("Kanban_BlockedStart_Title"),
                LocalizedTextFormatter.Format("Kanban_BlockedStart_Message", card.Title, string.Join(", ", card.BlockedByTitles)),
                NotificationSeverity.Warning);
        }
        return false;
    }

    [RelayCommand]
    private void ConfirmBlockedMove()
    {
        if (PendingBlockedMove is not { } request) return;
        PendingBlockedMove = null;

        // La tarjeta pudo cambiar de sitio o borrarse mientras el diálogo estaba abierto.
        var source = Columns.FirstOrDefault(c => c.Cards.Contains(request.Card));
        if (source is null || !Columns.Contains(request.Target)) return;
        MoveCardCore(request.Card, source, request.Target, request.Before is { } before && request.Target.Cards.Contains(before) ? before : null);
    }

    [RelayCommand]
    private void CancelBlockedMove() => PendingBlockedMove = null;

    // ===================== Vista de lista =====================

    /// <summary>Muestra las tarjetas del tablero como una lista ordenable en vez de columnas. Es la misma
    /// colección de tarjetas, con los mismos filtros, buscador y etiquetas.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVerticalBoardVisible))]
    [NotifyPropertyChangedFor(nameof(IsHorizontalBoardVisible))]
    private bool _isListLayout;

    partial void OnIsListLayoutChanged(bool value) => RebuildListRows();

    partial void OnBoardOrientationChanged(KanbanBoardOrientation value)
    {
        OnPropertyChanged(nameof(IsVerticalBoardVisible));
        OnPropertyChanged(nameof(IsHorizontalBoardVisible));
    }

    public bool IsVerticalBoardVisible => !IsListLayout && BoardOrientation == KanbanBoardOrientation.Vertical;
    public bool IsHorizontalBoardVisible => !IsListLayout && BoardOrientation == KanbanBoardOrientation.Horizontal;

    public ObservableCollection<KanbanListRowViewModel> ListRows { get; } = [];

    public bool HasListRows => ListRows.Count > 0;

    [ObservableProperty]
    private KanbanListSortColumn _listSortColumn = KanbanListSortColumn.Column;

    [ObservableProperty]
    private bool _listSortDescending;

    partial void OnListSortColumnChanged(KanbanListSortColumn value) => RebuildListRows();

    partial void OnListSortDescendingChanged(bool value) => RebuildListRows();

    [RelayCommand]
    private void ToggleListLayout() => IsListLayout = !IsListLayout;

    /// <summary>Ordena por <paramref name="column"/>; pulsar la columna ya activa invierte el sentido.</summary>
    [RelayCommand]
    private void SortList(KanbanListSortColumn column)
    {
        if (ListSortColumn == column) ListSortDescending = !ListSortDescending;
        else
        {
            ListSortDescending = false;
            ListSortColumn = column;
        }
    }

    /// <summary>Reconstruye las filas desde las columnas del tablero, respetando el filtro activo
    /// (<see cref="KanbanCardViewModel.IsVisible"/>). Solo trabaja con la lista a la vista.</summary>
    private void RebuildListRows()
    {
        if (!IsListLayout)
        {
            if (ListRows.Count > 0) { ListRows.Clear(); OnPropertyChanged(nameof(HasListRows)); }
            return;
        }

        var rows = new List<KanbanListRowViewModel>();
        for (var i = 0; i < Columns.Count; i++)
        {
            var label = _kanbanService.ColumnLabel(Columns[i].Column);
            foreach (var card in Columns[i].Cards.Where(c => c.IsVisible))
                rows.Add(new KanbanListRowViewModel(card, label, i));
        }

        var sorted = SortRows(rows, ListSortColumn, ListSortDescending);
        ListRows.Clear();
        foreach (var row in sorted) ListRows.Add(row);
        OnPropertyChanged(nameof(HasListRows));
    }

    /// <summary>Orden estable: el criterio elegido y, a igualdad, el orden del tablero (columna y posición).
    /// Las tarjetas sin fecha van siempre al final, también al invertir el orden.</summary>
    internal static List<KanbanListRowViewModel> SortRows(IReadOnlyList<KanbanListRowViewModel> rows,
        KanbanListSortColumn column, bool descending)
    {
        var indexed = rows.Select((row, position) => (row, position)).ToList();
        IOrderedEnumerable<(KanbanListRowViewModel row, int position)> ordered;
        int Direction(int value) => descending ? -value : value;

        switch (column)
        {
            case KanbanListSortColumn.Title:
                ordered = descending
                    ? indexed.OrderByDescending(t => t.row.Card.Title, StringComparer.CurrentCultureIgnoreCase)
                    : indexed.OrderBy(t => t.row.Card.Title, StringComparer.CurrentCultureIgnoreCase);
                break;
            case KanbanListSortColumn.Priority:
                // Alta primero en sentido ascendente, que es lo que se espera al ordenar por prioridad.
                ordered = indexed.OrderBy(t => Direction(-(int)t.row.Card.Priority));
                break;
            case KanbanListSortColumn.DueDate:
                // Las tarjetas sin fecha van siempre al final, también al invertir el orden.
                var dated = indexed.Where(t => DueKey(t.row.Card) is not null);
                var datedSorted = descending
                    ? dated.OrderByDescending(t => DueKey(t.row.Card)).ThenBy(t => t.position)
                    : dated.OrderBy(t => DueKey(t.row.Card)).ThenBy(t => t.position);
                return datedSorted.Concat(indexed.Where(t => DueKey(t.row.Card) is null)).Select(t => t.row).ToList();
            case KanbanListSortColumn.Blocked:
                // Bloqueadas primero en sentido ascendente.
                ordered = indexed.OrderBy(t => Direction(t.row.Card.IsBlocked ? 0 : 1));
                break;
            default:
                ordered = indexed.OrderBy(t => Direction(t.row.ColumnOrder));
                break;
        }

        return ordered.ThenBy(t => t.row.ColumnOrder).ThenBy(t => t.position).Select(t => t.row).ToList();
    }

    private static DateTime? DueKey(KanbanCardViewModel card) => card.Card.DueDateUtc ?? card.Card.StartDateUtc;
}
