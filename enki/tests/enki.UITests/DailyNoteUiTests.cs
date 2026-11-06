using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Propuesta 8 (docs/project-info/notas-diarias.md): monta el XAML real del calendario de
/// constancia y de la cabecera del editor contra viewmodels reales — mismo criterio de "renderizar, no solo
/// probar el viewmodel" que <see cref="HabitsAndStatisticsViewUiTests"/>, para cazar lo que la compilación
/// de XamlIL no ve: una celda que no se pinta, un DataTemplate que no encaja o un grupo de la cabecera que
/// se muestra en una nota que no es una nota diaria.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class DailyNoteUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _vaultPath;

    public DailyNoteUiTests()
    {
        HeadlessApp.EnsureInitialized();
        _vaultPath = Path.Combine(_tempDir, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private sealed record Harness(NotesViewModel Notes, VaultService Vault, HabitService Habits);

    private Harness CreateNotes()
    {
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var habits = new HabitService(vault);
        var notes = new NotesViewModel(vault, new SessionService(vault), settingsService: settings,
            vaultSettingsService: new VaultSettingsService(vault), habitService: habits);
        return new Harness(notes, vault, habits);
    }

    private static Window Show(Control view)
    {
        var window = new Window { Content = view, Width = 1000, Height = 760 };
        window.Show();
        return window;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    [Fact]
    public void ConsistencyView_RendersOneCellPerDayAndTheWeekdayColumn()
    {
        var h = CreateNotes();
        File.WriteAllText(Path.Combine(_vaultPath, $"{Today.AddDays(-2):yyyy-MM-dd}.md"), "# nota\n");
        h.Notes.ToggleConsistencyCommand.Execute(null);

        var view = new DailyConsistencyView { DataContext = h.Notes };
        var window = Show(view);

        var cells = view.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("day-cell")).ToList();
        Assert.Equal(70, cells.Count);
        Assert.Contains(cells, cell => cell.Classes.Contains("has-note"));
        Assert.Contains(cells, cell => cell.Classes.Contains("today"));
        // Un día futuro de la semana en curso se pinta apagado y no se puede pulsar.
        Assert.All(cells.Where(cell => cell.Classes.Contains("future")), cell => Assert.False(cell.IsEnabled));

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(h.Notes.ConsistencySummary, texts);

        window.Close();
    }

    [Fact]
    public void ConsistencyView_WithHabits_RendersTheHabitRowAlignedWithTheNotesRow()
    {
        var h = CreateNotes();
        var habit = h.Habits.AddHabit("Meditar", HabitFrequency.Daily, []);
        habit.CreatedUtc = DateTime.UtcNow.AddDays(-30);
        h.Habits.ToggleCompletion(habit, Today.AddDays(-1));
        h.Notes.ToggleConsistencyCommand.Execute(null);

        var view = new DailyConsistencyView { DataContext = h.Notes };
        var window = Show(view);

        var habitCells = view.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("habit-cell")).ToList();
        Assert.Equal(70, habitCells.Count);
        Assert.Contains(habitCells, cell => cell.Classes.Contains("done"));

        window.Close();
    }

    [Fact]
    public void ConsistencyView_WithoutHabits_DoesNotRenderTheHabitRow()
    {
        var h = CreateNotes();
        h.Notes.ToggleConsistencyCommand.Execute(null);

        var view = new DailyConsistencyView { DataContext = h.Notes };
        var window = Show(view);

        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("habit-cell"));

        window.Close();
    }

    [Fact]
    public void EditorView_DailyNote_RendersTheDayNavigationWithItsDate()
    {
        var h = CreateNotes();
        h.Notes.OpenDailyNoteCommand.Execute(null);
        var editor = h.Notes.ActiveEditor!;

        var view = new EditorView { DataContext = editor };
        var window = Show(view);

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(editor.DailyNoteDateLabel, texts);
        // Es la nota de hoy: el botón de "ir a hoy" no tiene nada que hacer y no se muestra.
        Assert.False(editor.CanReturnToToday);

        window.Close();
    }

    [Fact]
    public void EditorView_RegularNote_RendersNoDayNavigation()
    {
        var h = CreateNotes();
        h.Notes.OpenNote(h.Vault.CreateNote("Ideas sueltas", _vaultPath));
        var editor = h.Notes.ActiveEditor!;

        var view = new EditorView { DataContext = editor };
        var window = Show(view);

        Assert.False(editor.IsDailyNote);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text),
            text => text == editor.DailyNoteDateLabel && !string.IsNullOrEmpty(text));

        window.Close();
    }
}
