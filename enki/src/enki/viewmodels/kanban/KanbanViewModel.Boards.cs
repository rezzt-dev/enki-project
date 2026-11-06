using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

public sealed partial class KanbanViewModel
{
    private string? _currentBoardId;
    private string? _originalEditorDraft;
    partial void OnCardEditorChanged(KanbanCardEditorViewModel? value) => _originalEditorDraft = value is null ? null : EditorDraft(value);
    private static string EditorDraft(KanbanCardEditorViewModel editor)
    {
        var draft = editor.BuildCard(string.Empty);
        draft.CreatedUtc = default;
        return editor.SelectedColumn.Id + System.Text.Json.JsonSerializer.Serialize(draft);
    }
    private Action? _pendingBoardNavigation;
    private readonly Dictionary<string, BoardSessionState> _boardStates = [];
    private sealed record BoardSessionState(string Search, KanbanQuickFilter Filter, KanbanCardSortMode Sort, string[] Tags,
        double HorizontalOffset, double VerticalOffset, string ArchiveSearch, KanbanPriority? ArchivePriority,
        KanbanArchiveSortMode ArchiveSort, string[] ArchiveTags);
    public double BoardHorizontalOffset { get; set; }
    public double BoardVerticalOffset { get; set; }
    public event Action? BoardSwitching;
    public KanbanBoard CurrentBoard => _kanbanService.Boards.FirstOrDefault(b => b.Id == _currentBoardId)
        ?? _kanbanService.DefaultBoard;
    public bool MultipleBoardsEnabled => _settingsService?.Settings.KanbanMultipleBoardsEnabled == true || _kanbanService.Boards.Count > 1;
    public string BoardModeLabel => LocalizationService.Instance[MultipleBoardsEnabled ? "Kanban_Multi_Boards" : "Kanban_Board_TabKanban"];
    public ObservableCollection<KanbanBoardOption> BoardOptions { get; } = [];
    public ObservableCollection<KanbanBoard> MoveBoards { get; } = [];
    public ObservableCollection<KanbanColumn> MoveColumns { get; } = [];
    [ObservableProperty]
    private bool _isBoardSelectorOpen;
    [ObservableProperty]
    private string _boardSearchText = string.Empty;
    [ObservableProperty]
    private bool _isBoardNameEditorOpen;
    [ObservableProperty]
    private string _boardNameDraft = string.Empty;
    [ObservableProperty]
    private string? _boardError;
    [ObservableProperty]
    private bool _isBoardSwitchPending;
    [ObservableProperty]
    private bool _isBoardDeletePending;
    [ObservableProperty]
    private bool _isMoveCardOpen;
    [ObservableProperty]
    private KanbanBoard? _moveTargetBoard;
    [ObservableProperty]
    private KanbanColumn? _moveTargetColumn;
    private bool _renamingBoard;
    private string? _movingCardId;
    public bool CanDeleteCurrentBoard => _kanbanService.CanDeleteBoard(CurrentBoard);
    public bool IsCurrentBoardDefault => CurrentBoard == _kanbanService.DefaultBoard;
    public bool MoveWillUnarchive => _movingCardId is { } id && _kanbanService.FindCardLocation(id)?.Card.ArchivedAtUtc is not null
        && MoveTargetColumn is { Role: not CoreColumnRole.Done };

    partial void OnBoardSearchTextChanged(string value) => RefreshBoardOptions();
    partial void OnMoveTargetColumnChanged(KanbanColumn? value) => OnPropertyChanged(nameof(MoveWillUnarchive));
    partial void OnMoveTargetBoardChanged(KanbanBoard? value)
    {
        MoveColumns.Clear();
        if (value is not null) foreach (var column in value.Columns) MoveColumns.Add(column);
        var role = _movingCardId is { } id ? _kanbanService.FindCardLocation(id)?.Column.Role : null;
        MoveTargetColumn = role is not null and not CoreColumnRole.None ? MoveColumns.FirstOrDefault(c => c.Role == role) : null;
    }

    public KanbanBoardOption? CurrentBoardOption => BoardOptions.FirstOrDefault(b => b.IsSelected);
    public bool HasBoardSearchResults => BoardOptions.Count > 0;

    private void RefreshBoardOptions()
    {
        BoardOptions.Clear();
        foreach (var board in _kanbanService.Boards.Where(b => b.Name.Contains(BoardSearchText, StringComparison.CurrentCultureIgnoreCase)))
            BoardOptions.Add(new(board, board == _kanbanService.DefaultBoard, board == CurrentBoard));
        OnPropertyChanged(nameof(HasBoardSearchResults));
        OnPropertyChanged(nameof(CurrentBoardOption));
        OnPropertyChanged(nameof(CurrentBoard));
        OnPropertyChanged(nameof(MultipleBoardsEnabled));
        OnPropertyChanged(nameof(BoardModeLabel));
        OnPropertyChanged(nameof(CanDeleteCurrentBoard));
        OnPropertyChanged(nameof(IsCurrentBoardDefault));
    }

    private void RefreshMultipleBoardsSettings()
    {
        if (_kanbanService.Boards.Count > 1 && _settingsService is { } settings && !settings.Settings.KanbanMultipleBoardsEnabled)
        {
            settings.Settings.KanbanMultipleBoardsEnabled = true;
            settings.Save();
            _notificationService?.Push(LocalizationService.Instance["Kanban_Multi_Boards"],
                LocalizationService.Instance["Kanban_Multi_Recovered"], NotificationSeverity.Warning);
        }
        RefreshBoardOptions();
    }

    [RelayCommand]
    private void ToggleBoardSelector()
    {
        BoardSearchText = string.Empty;
        IsBoardSelectorOpen = !IsBoardSelectorOpen;
    }
    [RelayCommand]
    private void SelectBoard(KanbanBoardOption option) => NavigateToBoard(option.Board.Id);
    [RelayCommand]
    private void RenameSelectedBoard(KanbanBoardOption option) => NavigateToBoard(option.Board.Id, RenameBoard);
    [RelayCommand]
    private void DeleteSelectedBoard(KanbanBoardOption option) => NavigateToBoard(option.Board.Id, RequestDeleteBoard);
    [RelayCommand]
    private void UseSelectedBoardAsDefault(KanbanBoardOption option) => TryBoardOperation(() =>
    {
        _kanbanService.SetDefaultBoard(option.Board.Id);
        RefreshBoardOptions();
    });

    private void NavigateToBoard(string boardId, Action? after = null)
    {
        if (CardEditor is { } editor && EditorDraft(editor) != _originalEditorDraft)
        {
            _pendingBoardNavigation = () => NavigateToBoard(boardId, after);
            IsBoardSwitchPending = true;
            return;
        }
        if (!_kanbanService.Boards.Any(b => b.Id == boardId)) return;
        CardEditor = null;
        if (_currentBoardId != boardId)
        {
            BoardSwitching?.Invoke();
            if (_kanbanService.Boards.Any(b => b.Id == _currentBoardId))
                _boardStates[CurrentBoard.Id] = new(SearchText, QuickFilter, SortMode, TagFilter.SelectedTags.ToArray(), BoardHorizontalOffset, BoardVerticalOffset,
                    ArchiveSearchText, ArchivePriorityFilter, ArchiveSortMode, ArchiveTagFilter.SelectedTags.ToArray());
            _currentBoardId = boardId;
            CardDetail = null;
            IsArchiveOpen = false;
            IsSortMenuOpen = false;
            CardPendingDelete = null;
            ColumnPendingDelete = null;
            IsArchiveAllPending = false;
            _returnToArchive = false;
            ClearFilters();
            ClearArchiveFilters();
            Reload();
            if (_boardStates.TryGetValue(boardId, out var state))
            {
                SearchText = state.Search;
                QuickFilter = state.Filter;
                SortMode = state.Sort;
                foreach (var tag in TagFilter.Items) tag.IsSelected = state.Tags.Contains(tag.Tag, StringComparer.OrdinalIgnoreCase);
                BoardHorizontalOffset = state.HorizontalOffset;
                BoardVerticalOffset = state.VerticalOffset;
                ArchiveSearchText = state.ArchiveSearch;
                ArchivePriorityFilter = state.ArchivePriority;
                ArchiveSortMode = state.ArchiveSort;
                foreach (var tag in ArchiveTagFilter.Items) tag.IsSelected = state.ArchiveTags.Contains(tag.Tag, StringComparer.OrdinalIgnoreCase);
            }
            else { SortMode = KanbanCardSortMode.Manual; ArchiveSortMode = KanbanArchiveSortMode.ArchivedDate; BoardHorizontalOffset = BoardVerticalOffset = 0; }
            if (_settingsService is not null)
            {
                _settingsService.Settings.KanbanLastSelectedBoardId = boardId;
                _settingsService.Save();
            }
        }
        DisplayMode = KanbanDisplayMode.Board;
        IsBoardSelectorOpen = false;
        after?.Invoke();
    }

    [RelayCommand]
    private void CancelBoardSwitch() { IsBoardSwitchPending = false; _pendingBoardNavigation = null; }
    [RelayCommand]
    private void DiscardAndSwitchBoard()
    {
        CardEditor = null;
        ContinueBoardSwitch();
    }
    [RelayCommand]
    private void SaveAndSwitchBoard()
    {
        SaveCardEditor();
        if (CardEditor is null) ContinueBoardSwitch();
    }
    private void ContinueBoardSwitch()
    {
        var next = _pendingBoardNavigation;
        CancelBoardSwitch();
        next?.Invoke();
    }

    [RelayCommand]
    private void CreateFirstCard() { if (Columns.FirstOrDefault() is { } column) OpenCreateCard(column); }
    [RelayCommand]
    private void CreateBoard()
    {
        if (!MultipleBoardsEnabled) return;
        _renamingBoard = false;
        BoardNameDraft = string.Empty;
        BoardError = null;
        IsBoardSelectorOpen = false;
        IsBoardNameEditorOpen = true;
    }
    [RelayCommand]
    private void RenameBoard()
    {
        _renamingBoard = true;
        BoardNameDraft = CurrentBoard.Name;
        BoardError = null;
        IsBoardNameEditorOpen = true;
    }
    [RelayCommand]
    private void CancelBoardNameEditor() { IsBoardNameEditorOpen = false; BoardError = null; }
    [RelayCommand]
    private void SaveBoardName()
    {
        TryBoardOperation(() =>
        {
            if (_renamingBoard) { _kanbanService.RenameBoard(CurrentBoard.Id, BoardNameDraft); Reload(); }
            else
            {
                var board = _kanbanService.CreateBoard(BoardNameDraft);
                NavigateToBoard(board.Id);
            }
            IsBoardNameEditorOpen = false;
            RefreshBoardOptions();
        });
    }
    [RelayCommand]
    private void UseBoardAsDefault() => TryBoardOperation(() => { _kanbanService.SetDefaultBoard(CurrentBoard.Id); RefreshBoardOptions(); });
    [RelayCommand]
    private void RequestDeleteBoard() { BoardError = null; IsBoardDeletePending = true; }
    [RelayCommand]
    private void CancelDeleteBoard() => IsBoardDeletePending = false;
    [RelayCommand]
    private void DeleteBoard() => TryBoardOperation(() =>
    {
        _kanbanService.DeleteEmptyBoard(CurrentBoard.Id);
        NavigateToBoard(_kanbanService.DefaultBoard.Id);
        Reload();
        IsBoardDeletePending = false;
        _notificationService?.Push(LocalizationService.Instance["Kanban_Multi_Boards"],
            LocalizationService.Instance["Kanban_Multi_Deleted"], NotificationSeverity.Success);
    });
    [RelayCommand]
    private void MoveCardFromDetail()
    {
        if (CardDetail is not { } detail) return;
        OpenMoveCard(detail.SourceCard);
        CardDetail = null;
    }
    [RelayCommand]
    private void OpenMoveCard(KanbanCardViewModel card)
    {
        _movingCardId = card.Id;
        MoveBoards.Clear();
        foreach (var board in _kanbanService.Boards.Where(b => b != CurrentBoard)) MoveBoards.Add(board);
        MoveTargetBoard = null;
        MoveTargetBoard = MoveBoards.FirstOrDefault();
        BoardError = null;
        IsMoveCardOpen = true;
    }
    [RelayCommand]
    private void CancelMoveCard() { IsMoveCardOpen = false; _movingCardId = null; }
    [ObservableProperty]
    private string? _lastMovedBoardId;
    [RelayCommand]
    private void OpenMovedBoard() { if (LastMovedBoardId is { } id) NavigateToBoard(id); }
    [RelayCommand]
    private void ConfirmMoveCard() => TryBoardOperation(() =>
    {
        if (_movingCardId is null || MoveTargetBoard is null || MoveTargetColumn is null)
            throw new ArgumentException("Kanban_Multi_MissingDestination");
        _kanbanService.MoveCardToBoard(_movingCardId, MoveTargetBoard.Id, MoveTargetColumn.Id);
        LastMovedBoardId = MoveTargetBoard.Id;
        CancelMoveCard();
        Reload();
        _notificationService?.Push(LocalizationService.Instance["Kanban_Multi_Boards"],
            LocalizationService.Instance["Kanban_Multi_Moved"], NotificationSeverity.Success);
    });

    private void TryBoardOperation(Action operation)
    {
        BoardError = null;
        try { operation(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            BoardError = LocalizationService.Instance[ex.Message.StartsWith("Kanban_Multi_", StringComparison.Ordinal) ? ex.Message : "Kanban_Multi_SaveFailed"];
            _notificationService?.Push(LocalizationService.Instance["Kanban_Multi_Boards"], BoardError, NotificationSeverity.Error);
        }
    }
}
