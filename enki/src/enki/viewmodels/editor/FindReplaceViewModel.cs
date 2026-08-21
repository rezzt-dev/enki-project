using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;

namespace enki.ViewModels;

/// <summary>Panel de búsqueda y reemplazo del editor Markdown.</summary>
public sealed partial class FindReplaceViewModel : ObservableObject
{
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
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels() => OnPropertyChanged(nameof(StatusLabel));

    partial void OnSearchTextChanged(string value) => RefreshStatus();
    partial void OnMatchCaseChanged(bool value) => RefreshStatus();
    partial void OnIsOpenChanged(bool value)
    {
        if (value) RefreshStatus();
    }

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
