using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Localization;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Festivos en la vista real (docs/calendar-feature/15-festivos-y-dias-no-laborables.md): la
/// casilla del festivo se aclara y se rotula en Mes, y el selector de países de Ajustes guarda la elección.
/// Con <c>ENKI_GRAPH_PREVIEW</c> apuntando a una carpeta, guarda además capturas para revisarlas a ojo.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class CalendarHolidaysUiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _language = LocalizationService.Instance.CurrentCulture.Name;

    public CalendarHolidaysUiTests()
    {
        HeadlessApp.EnsureInitialized();
        LocalizationService.Instance.SetLanguage("es-ES");
    }

    public void Dispose()
    {
        LocalizationService.Instance.SetLanguage(_language);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private static void SavePreview(Control view, string name, int width, int height)
    {
        if (Environment.GetEnvironmentVariable("ENKI_GRAPH_PREVIEW") is not { Length: > 0 } preview) return;
        Directory.CreateDirectory(preview);
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        bitmap.Render(view);
        bitmap.Save(Path.Combine(preview, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static Border MonthCell(CalendarView view, DateTime date) =>
        view.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("calendar-month-cell") && b.DataContext is CalendarMonthDayViewModel { IsCurrentMonth: true } d && d.Date == date);

    private static bool HasVisibleFill(Border cell) =>
        cell.GetVisualDescendants().OfType<Border>().Any(b => b.Classes.Contains("calendar-nonworking-fill") && b.IsEffectivelyVisible);

    [Fact]
    public void MonthView_ShadesAndLabelsHolidays_AndWeekends()
    {
        var settings = new SettingsService(_directory);
        settings.Settings.CalendarHolidayCountries = ["ES"];
        settings.Settings.CalendarLayersExpanded = true;
        var auth = new PolarAuthService(settings);
        var kanban = new KanbanService(_directory);
        var polar = new PolarTaskService(_directory);
        var reminders = new ReminderService(_directory);
        var colors = new CalendarLayerColorService(kanban, polar, auth, settings);
        var vm = new CalendarViewModel(new CalendarAggregationService(kanban, polar, reminders, auth, colors), colors, kanban, polar,
            reminders, auth, settings, new NotificationService(settings, _directory), _ => { });
        vm.SelectMiniDayCommand.Execute(new CalendarMiniDayViewModel(new DateTime(2026, 12, 15), true, false, false, false));

        var view = new CalendarView { DataContext = vm };
        var window = new Window { Content = view, Width = 1600, Height = 960 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();

            var holiday = MonthCell(view, new DateTime(2026, 12, 8));
            Assert.True(HasVisibleFill(holiday));
            Assert.Contains(holiday.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Classes.Contains("calendar-holiday-label") && t.IsEffectivelyVisible && t.Text == "Inmaculada Concepción");

            Assert.True(HasVisibleFill(MonthCell(view, new DateTime(2026, 12, 12))));   // sábado
            Assert.False(HasVisibleFill(MonthCell(view, new DateTime(2026, 12, 9))));   // miércoles laborable
            Assert.False(HasVisibleFill(MonthCell(view, new DateTime(2026, 12, 24))));  // Nochebuena: solo rótulo

            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Festivos · España");
            SavePreview(view, "calendar-holidays-month", 1600, 960);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void SettingsCountryPicker_PersistsSelection_AndFilters()
    {
        var settings = new SettingsService(_directory);
        var vault = new VaultService(settings);
        vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        var auth = new PolarAuthService(settings);
        var sync = new PolarSyncService(auth, new SupabaseRestClient(auth), new PolarTaskService(_directory),
            new ReminderService(_directory), settings);
        var calendarChanges = 0;
        var vm = new SettingsViewModel(settings, new CustomThemeService(settings), new BrandLogoService(_directory), new VaultSettingsService(vault), vault,
            new VaultBackupService(vault, _directory), auth, sync, new NotificationService(settings, _directory),
            (_, _) => Task.FromResult(false), _ => { }, _ => { }, _ => { }, _ => { }, (_, _) => { }, () => { }, () => calendarChanges++,
            _ => { }, _ => { }, _ => { }, _ => { }, () => { });
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 1400 };
        try
        {
            window.Show();
            vm.SelectSectionCommand.Execute(SettingsSection.Calendar);
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();

            ToggleButton Chip(string name) => view.GetVisualDescendants().OfType<ToggleButton>()
                .Single(t => t.Classes.Contains("tag-chip") && t.DataContext is HolidayCountryOptionViewModel o && o.DisplayName == name);

            var spain = Chip("España");
            Assert.True(spain.IsEffectivelyVisible);
            spain.IsChecked = true;
            Chip("México").IsChecked = true;
            Assert.Equal(["ES", "MX"], settings.Settings.CalendarHolidayCountries);
            Assert.True(Chip("España").IsChecked);            Assert.Contains("España", vm.HolidaySelectionSummary, StringComparison.Ordinal);

            vm.HolidayCountrySearch = "mexi";
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.False(Chip("España").IsEffectivelyVisible);
            Assert.True(Chip("México").IsEffectivelyVisible);

            vm.HolidayCountrySearch = string.Empty;
            window.GetLayoutManager()!.ExecuteLayoutPass();
            SavePreview(view, "settings-holidays", 1200, 1400);

            vm.ClearHolidayCountriesCommand.Execute(null);
            Assert.Empty(settings.Settings.CalendarHolidayCountries);
            Assert.False(Chip("España").IsChecked);
            Assert.True(calendarChanges >= 3);
        }
        finally { window.Close(); }
    }
}
