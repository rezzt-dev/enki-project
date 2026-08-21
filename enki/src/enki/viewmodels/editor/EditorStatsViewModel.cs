using System;
using System.Globalization;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class EditorStatsViewModel : ObservableObject
{
    private readonly TextDocument _document;
    private readonly string _fullPath;

    /// <summary>Debounces <see cref="Refresh"/> against <see cref="TextDocument.TextChanged"/> — same
    /// reasoning and delay as <see cref="EditorOutlineViewModel"/>'s own timer
    /// (docs/analisis-proyecto/roadmap.md, Fase 6, análisis §3): word/char/reading-time stats don't need to
    /// be exact mid-keystroke, only once typing pauses.</summary>
    private readonly DispatcherTimer _refreshTimer;

    [ObservableProperty]
    private int _wordCount;

    [ObservableProperty]
    private int _charCount;

    [ObservableProperty]
    private int _charCountNoSpaces;

    [ObservableProperty]
    private int _paragraphCount;

    [ObservableProperty]
    private int _lineCount;

    [ObservableProperty]
    private int _readingTimeMinutes;

    /// <summary>"{0} min" (docs/agent-docs/internacionalizacion-i18n.md §1.3) instead of two separate
    /// TextBlock Runs concatenating the number and a fixed unit suffix — keeps word order adaptable
    /// per locale instead of baking "number then unit" into the view.</summary>
    public string ReadingTimeLabel => string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Editor_Stats_ReadingTime_Format"], ReadingTimeMinutes);

    partial void OnReadingTimeMinutesChanged(int value) => OnPropertyChanged(nameof(ReadingTimeLabel));

    [ObservableProperty]
    private string _createdLabel = string.Empty;

    [ObservableProperty]
    private string _modifiedLabel = string.Empty;

    public EditorStatsViewModel(TextDocument document, string fullPath)
    {
        _document = document;
        _fullPath = fullPath;
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

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(ReadingTimeLabel));
        OnPropertyChanged(nameof(CreatedLabel));
        OnPropertyChanged(nameof(ModifiedLabel));
    }

    public void Refresh()
    {
        var stats = EditorStatsService.Compute(_document.Text, _fullPath);
        WordCount = stats.WordCount;
        CharCount = stats.CharCount;
        CharCountNoSpaces = stats.CharCountNoSpaces;
        ParagraphCount = stats.ParagraphCount;
        LineCount = stats.LineCount;
        ReadingTimeMinutes = stats.ReadingTimeMinutes;
        CreatedLabel = FormatDate(stats.CreatedUtc);
        ModifiedLabel = FormatDate(stats.ModifiedUtc);
    }

    private static string FormatDate(DateTime utc) =>
        utc == DateTime.MinValue ? "—" : RelativeTimeFormatter.FormatAbsolute(utc);
}
