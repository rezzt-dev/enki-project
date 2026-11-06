using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Panel de búsqueda y reemplazo del editor Markdown.</summary>
public sealed partial class FindReplaceViewModel : ObservableObject
{
    /// <summary>Debounces <see cref="RefreshStatus"/> against every keystroke in the search box — it
    /// triggers a full-document scan for match count/position, which on a long note added up on each
    /// character typed (análisis §4.2, Fase 8). Same delay/restart-on-Tick pattern as
    /// <see cref="EditorOutlineViewModel"/>'s own debounce timer.</summary>
    private readonly DispatcherTimer _refreshStatusTimer;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _replaceText = string.Empty;

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private bool _isReplaceMode;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private int _currentMatchIndex;

    [ObservableProperty]
    private int _totalMatches;

    public string StatusLabel => TotalMatches == 0
        ? LocalizationService.Instance["Editor_FindReplace_NoMatches"]
        : string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Editor_FindReplace_MatchStatus"], CurrentMatchIndex + 1, TotalMatches);

    public FindReplaceViewModel()
    {
        _refreshStatusTimer = new DispatcherTimer { Interval = EditorDebounce.Delay };
        _refreshStatusTimer.Tick += (_, _) =>
        {
            _refreshStatusTimer.Stop();
            RefreshStatus();
        };
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels() => OnPropertyChanged(nameof(StatusLabel));

    private void ScheduleRefreshStatus()
    {
        _refreshStatusTimer.Stop();
        _refreshStatusTimer.Start();
    }

    partial void OnSearchTextChanged(string value) => ScheduleRefreshStatus();
    partial void OnMatchCaseChanged(bool value) => ScheduleRefreshStatus();
    // Runs on both open (to show the initial match count) and close (so the code-behind clears the
    // "highlight all matches" overlay instead of leaving it shading a panel that's no longer open) —
    // immediate, not debounced, since neither is a per-keystroke event.
    partial void OnIsOpenChanged(bool value) => RefreshStatus();

    /// <summary>Se dispara cuando el panel necesita interactuar con el editor (buscar siguiente,
    /// reemplazar, etc.). El código detrás de la vista responde ejecutando la operación sobre
    /// el <c>TextEditor</c> real y reportando el resultado.</summary>
    public event Action<FindReplaceOperation, string, string, bool>? OperationRequested;

    [RelayCommand]
    private void FindNext() => OperationRequested?.Invoke(FindReplaceOperation.FindNext, SearchText, ReplaceText, MatchCase);

    [RelayCommand]
    private void FindPrevious() => OperationRequested?.Invoke(FindReplaceOperation.FindPrevious, SearchText, ReplaceText, MatchCase);

    [RelayCommand]
    private void Replace() => OperationRequested?.Invoke(FindReplaceOperation.Replace, SearchText, ReplaceText, MatchCase);

    [RelayCommand]
    private void ReplaceAll() => OperationRequested?.Invoke(FindReplaceOperation.ReplaceAll, SearchText, ReplaceText, MatchCase);

    [RelayCommand]
    private void Close() => IsOpen = false;

    public void ReportStatus(int currentIndex, int total)
    {
        CurrentMatchIndex = currentIndex;
        TotalMatches = total;
        OnPropertyChanged(nameof(StatusLabel));
    }

    public void RefreshStatus()
    {
        OperationRequested?.Invoke(FindReplaceOperation.RefreshStatus, SearchText, ReplaceText, MatchCase);
    }
}

public enum FindReplaceOperation
{
    FindNext,
    FindPrevious,
    Replace,
    ReplaceAll,
    RefreshStatus
}
