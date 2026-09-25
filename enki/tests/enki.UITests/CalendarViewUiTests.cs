using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
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
        var layerColorService = new CalendarLayerColorService(kanbanService, polarTaskService, authService, settings);
        var aggregationService = new CalendarAggregationService(kanbanService, polarTaskService, reminderService, authService, layerColorService);
        var viewModel = new CalendarViewModel(aggregationService, layerColorService, kanbanService, polarTaskService, reminderService,
            authService, settings, notificationService, _ => { });

        var view = new CalendarView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        window.Show();

        return (view, viewModel, window);
    }

    [Fact]
    public void SidebarSections_ToggleContent_AndRestoreAfterRestart()
    {
        var (view, vm, window) = CreateView();
        try
        {
            foreach (var section in new[] { "Layers", "Search", "Filters" })
            {
                var toggle = view.FindControl<ToggleButton>(section + "SectionToggle")!;
                var content = view.FindControl<StackPanel>(section + "SectionContent")!;
                Assert.False(content.IsVisible);
                toggle.IsChecked = true;
                Assert.True(content.IsVisible);
            }
            vm.IsMiniCalendarExpanded = false;
            vm.IsUnscheduledExpanded = true;
            vm.IsSidebarCollapsed = true;
        }
        finally { window.Close(); }

        var (_, restored, restoredWindow) = CreateView();
        try
        {
            Assert.False(restored.IsMiniCalendarExpanded);
            Assert.True(restored.IsLayersExpanded);
            Assert.True(restored.IsSearchExpanded);
            Assert.True(restored.IsFiltersExpanded);
            Assert.True(restored.IsUnscheduledExpanded);
            Assert.True(restored.IsSidebarCollapsed);
        }
        finally { restoredWindow.Close(); }
    }

    /// <summary>El selector de color de una capa (docs/calendar-feature/07-filtros-busqueda-y-capas.md
    /// §1b) vive dentro de un <see cref="Popup"/>, así que su plantilla no se materializa hasta que se
    /// abre: sin abrirlo, un error de enlace en la rejilla de muestras pasaría desapercibido (un
    /// <c>{Binding}</c> roto en Avalonia no lanza, solo deja el control muerto).</summary>
    [Fact]
    public void LayerColorPopover_RendersItsSwatches_AndApplyingOneRepaintsTheLayer()
    {
        var (view, vm, window) = CreateView();
        try
        {
            view.FindControl<ToggleButton>("LayersSectionToggle")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var row = vm.Layers.First(l => l.CanEditColor);
            var originalColor = row.ColorHex;
            row.OpenColorPickerCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var popup = view.GetLogicalDescendants().OfType<Popup>().Single(p => ReferenceEquals(p.DataContext, row));
            Assert.True(popup.IsOpen);
            var swatches = popup.Child!.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("calendar-layer-swatch"))
                .ToList();

            Assert.Equal(row.PaletteSwatches.Count, swatches.Count);
            Assert.Equal(row.PaletteSwatches, swatches.Select(b => (string)b.DataContext!).ToList());
            // Un {Binding} roto deja Command a null en vez de lanzar, así que esto es la comprobación real.
            Assert.All(swatches, b => Assert.NotNull(b.Command));

            // Pulsar una muestra la aplica y cierra el emergente, sin pasar por ningún "guardar".
            var target = swatches.First(b => (string)b.DataContext! == CalendarLayerPalette.AccentPool[0]);
            target.Command!.Execute(target.CommandParameter);

            Assert.Equal(CalendarLayerPalette.AccentPool[0], row.ColorHex);
            Assert.NotEqual(originalColor, row.ColorHex);
            Assert.False(row.IsColorPickerOpen);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void LayerColorPopover_ToggleInitializesDraft_AndPickerAndHexStayInSync()
    {
        var (view, vm, window) = CreateView();
        try
        {
            view.FindControl<ToggleButton>("LayersSectionToggle")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var row = vm.Layers.First(l => l.CanEditColor);
            var toggle = view.GetVisualDescendants().OfType<ToggleButton>()
                .Single(b => b.Name == "LayerColorToggle" && ReferenceEquals(b.DataContext, row));
            toggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var popup = view.GetLogicalDescendants().OfType<Popup>()
                .Single(p => ReferenceEquals(p.DataContext, row));
            var picker = popup.Child!.GetVisualDescendants().OfType<enki.Controls.LocalizedColorPicker>().Single();
            var input = popup.Child!.GetVisualDescendants().OfType<TextBox>().Single();
            var original = row.ColorHex;
            Assert.Equal(original, input.Text);
            Assert.Equal(original, HexColorUtility.ColorToHex(picker.Color));

            picker.Color = Avalonia.Media.Color.Parse("#123ABC");
            Assert.Equal("#123ABC", input.Text);
            Assert.Equal(original, row.ColorHex);
            Assert.True(popup.IsOpen);

            input.Text = "#456789";
            Assert.Equal("#456789", HexColorUtility.ColorToHex(picker.Color));
            row.ApplyDraftColorCommand.Execute(null);
            Assert.Equal("#456789", row.ColorHex);
            Assert.False(row.IsColorPickerOpen);

            toggle.IsChecked = true;
            Assert.Equal("#456789", row.ColorDraftHex);
            input.Text = "#invalid";
            row.ApplyDraftColorCommand.Execute(null);
            Assert.Equal("#456789", row.ColorHex);
            Assert.Equal("#456789", input.Text);
            Assert.True(row.IsColorPickerOpen);
        }
        finally { window.Close(); }
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
