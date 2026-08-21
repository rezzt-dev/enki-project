using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Fase 6 (roadmap §11, "Tests de UI básicos ... abrir calendario") — same reasoning as
/// <see cref="NotesViewUiTests"/>/<see cref="KanbanViewUiTests"/>: renders the real <see cref="CalendarView"/>
/// XAML bound to a real <see cref="CalendarViewModel"/> (backed by throwaway-directory services, same as
/// <c>CalendarAggregationServiceTests</c>) and asserts the month grid's visual tree actually has one
/// container per day the viewmodel says the month has.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class CalendarViewUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");

    public CalendarViewUiTests() => HeadlessApp.EnsureInitialized();

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (CalendarView View, CalendarViewModel ViewModel, Window Window) CreateView()
    {
        var settings = new SettingsService(_tempDir);
        var authService = new PolarAuthService(settings);
        var kanbanService = new KanbanService(_tempDir);
        var polarTaskService = new PolarTaskService(_tempDir);
        var reminderService = new ReminderService(_tempDir);
        var notificationService = new NotificationService(settings, _tempDir);
        var aggregationService = new CalendarAggregationService(kanbanService, polarTaskService, reminderService, authService);
        var viewModel = new CalendarViewModel(aggregationService, kanbanService, polarTaskService, reminderService,
            authService, settings, notificationService, _ => { });

        var view = new CalendarView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        window.Show();

        return (view, viewModel, window);
    }

    [Fact]
    public void Opening_DefaultsToMonthView_AndRendersOneCellPerDay()
    {
        var (view, viewModel, window) = CreateView();

        Assert.True(viewModel.IsMonthView);
        Assert.NotEmpty(viewModel.Days);

        var renderedDayCells = view.GetVisualDescendants()
            .Where(v => v.DataContext is CalendarMonthDayViewModel)
            .Select(v => (CalendarMonthDayViewModel)v.DataContext!)
            .Distinct()
            .Count();

        Assert.Equal(viewModel.Days.Count, renderedDayCells);

        window.Close();
    }

    /// <summary>Switching to Semana/Día/Agenda via <c>SetViewCommand</c> — the exact command the
    /// view-switcher buttons in <c>CalendarView.axaml</c> are bound to (plain assignment to
    /// <see cref="CalendarViewModel.ActiveView"/> only flips the <c>IsXxxView</c> flags; it doesn't rebuild
    /// <see cref="CalendarViewModel.WeekColumns"/>) — verifies the View reacts and re-renders the week grid
    /// without throwing.</summary>
    [Fact]
    public void SwitchingToWeekView_UpdatesTheRenderedLayout()
    {
        var (view, viewModel, window) = CreateView();

        viewModel.SetViewCommand.Execute(CalendarViewMode.Week);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.True(viewModel.IsWeekView);
        Assert.NotEmpty(viewModel.WeekColumns);
        var renderedWeekColumns = view.GetVisualDescendants()
            .Where(v => v.DataContext is CalendarWeekColumnViewModel)
            .Select(v => (CalendarWeekColumnViewModel)v.DataContext!)
            .Distinct()
            .Count();
        Assert.Equal(viewModel.WeekColumns.Count, renderedWeekColumns);

        window.Close();
    }
}
