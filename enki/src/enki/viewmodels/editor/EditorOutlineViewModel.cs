using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class EditorOutlineViewModel : ObservableObject
{
    private readonly TextDocument _document;

    /// <summary>Debounces <see cref="Refresh"/> against <see cref="TextDocument.TextChanged"/> — that
    /// event fires on every single keystroke, and re-extracting headings from the whole document on each
    /// one added up on long notes (docs/analisis-proyecto/roadmap.md, Fase 6, análisis §3). Same delay as
    /// the editor's autosave/backlinks debounce (<see cref="EditorDebounce"/>), just a separate timer since
    /// this view model has no reference to <c>EditorViewModel</c>'s own.</summary>
    private readonly DispatcherTimer _refreshTimer;

    public ObservableCollection<EditorOutlineItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private EditorOutlineItemViewModel? _activeItem;

    public bool HasItems => Items.Count > 0;

    /// <summary>Se dispara cuando el usuario elige un heading en el outline; la vista (EditorView)
    /// responde moviendo el caret al offset correspondiente.</summary>
    public event Action<int>? NavigationRequested;

    public EditorOutlineViewModel(TextDocument document)
    {
        _document = document;
        _refreshTimer = new DispatcherTimer { Interval = EditorDebounce.Delay };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            Refresh();
        };
        document.TextChanged += (_, _) =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        };
        Refresh();
    }

    public void Refresh()
    {
        var items = EditorOutlineService.Extract(_document.Text);
        Items.Clear();
        foreach (var item in items) Items.Add(new EditorOutlineItemViewModel(item));
        OnPropertyChanged(nameof(HasItems));
    }

    public void UpdateActiveItem(int caretOffset)
    {
        var previous = Items.LastOrDefault(i => i.Item.Offset <= caretOffset);
        if (ActiveItem is not null) ActiveItem.IsActive = false;
        ActiveItem = previous;
        if (ActiveItem is not null) ActiveItem.IsActive = true;
    }

    [RelayCommand]
    private void Navigate(EditorOutlineItemViewModel item)
    {
        if (item.Item.Offset >= 0 && item.Item.Offset <= _document.TextLength)
            NavigationRequested?.Invoke(item.Item.Offset);
    }
}
