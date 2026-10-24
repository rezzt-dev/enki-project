using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Cubre las notas diarias mejoradas (propuesta 8) desde Documentos: abrir la nota de un día
/// concreto, el calendario de constancia y la navegación día anterior / día siguiente del editor. Mismo
/// banco de pruebas de vault temporal y servicios reales que el resto de tests de viewmodels.</summary>
public class NotesViewModelDailyNoteTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public NotesViewModelDailyNoteTests() => _vaultPath = Path.Combine(_storageDirectory, "vault");

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private sealed record Harness(NotesViewModel Notes, VaultService Vault, HabitService Habits, VaultSettingsService VaultSettings);

    private Harness CreateViewModel()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vaultService = new VaultService(settingsService);
        vaultService.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessionService = new SessionService(vaultService);
        var habitService = new HabitService(vaultService);
        var vaultSettings = new VaultSettingsService(vaultService);

        var notes = new NotesViewModel(vaultService, sessionService, settingsService: settingsService,
            vaultSettingsService: vaultSettings, habitService: habitService);
        return new Harness(notes, vaultService, habitService, vaultSettings);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private static string FileNameFor(DateOnly date) => date.ToString("yyyy-MM-dd") + ".md";

    [Fact]
    public void OpenDailyNote_CreatesTodaysNoteAndOpensItInATab()
    {
        var h = CreateViewModel();

        h.Notes.OpenDailyNoteCommand.Execute(null);

        Assert.True(File.Exists(Path.Combine(_vaultPath, FileNameFor(Today))));
        Assert.Single(h.Notes.OpenEditors);
        Assert.True(h.Notes.ActiveEditor!.IsDailyNote);
    }

    [Fact]
    public void OpenDailyNote_Twice_ReusesTheSameTab()
    {
        var h = CreateViewModel();

        h.Notes.OpenDailyNoteCommand.Execute(null);
        h.Notes.OpenDailyNoteCommand.Execute(null);

        Assert.Single(h.Notes.OpenEditors);
        Assert.Single(Directory.GetFiles(_vaultPath, "*.md"));
    }

    /// <summary>La navegación del editor crea la nota del día vecino si no existe — es justo lo que pide la
    /// propuesta 8— y la abre como pestaña propia.</summary>
    [Fact]
    public void EditorPreviousDay_OpensYesterdaysNoteCreatingIt()
    {
        var h = CreateViewModel();
        h.Notes.OpenDailyNoteCommand.Execute(null);

        h.Notes.ActiveEditor!.OpenPreviousDayCommand.Execute(null);

        var yesterday = Today.AddDays(-1);
        Assert.True(File.Exists(Path.Combine(_vaultPath, FileNameFor(yesterday))));
        Assert.Equal(FileNameFor(yesterday), h.Notes.ActiveEditor!.Note.FileName);
        Assert.True(h.Notes.ActiveEditor.CanReturnToToday);
    }

    [Fact]
    public void EditorToday_ReturnsFromAnotherDayToTodaysNote()
    {
        var h = CreateViewModel();
        h.Notes.OpenDailyNoteCommand.Execute(null);
        h.Notes.ActiveEditor!.OpenPreviousDayCommand.Execute(null);

        h.Notes.ActiveEditor!.OpenTodayNoteCommand.Execute(null);

        Assert.Equal(FileNameFor(Today), h.Notes.ActiveEditor!.Note.FileName);
        Assert.False(h.Notes.ActiveEditor.CanReturnToToday);
    }

    /// <summary>Una nota normal no muestra la navegación: es la condición para que la cabecera de quien no
    /// lleva un diario quede exactamente como estaba.</summary>
    [Fact]
    public void RegularNote_IsNotADailyNote()
    {
        var h = CreateViewModel();
        var note = h.Vault.CreateNote("Ideas sueltas", _vaultPath);

        h.Notes.OpenNote(note);

        Assert.False(h.Notes.ActiveEditor!.IsDailyNote);
        Assert.False(h.Notes.ActiveEditor.CanReturnToToday);
    }

    [Fact]
    public void ToggleConsistency_BuildsAWeekAlignedWindowEndingThisWeek()
    {
        var h = CreateViewModel();

        h.Notes.ToggleConsistencyCommand.Execute(null);

        Assert.True(h.Notes.IsConsistencyOpen);
        Assert.Equal(70, h.Notes.ConsistencyDays.Count);
        Assert.Equal(7, h.Notes.ConsistencyWeekdayLabels.Count);
        Assert.Contains(h.Notes.ConsistencyDays, day => day.IsToday);
        Assert.All(h.Notes.ConsistencyDays.Where(day => day.IsFuture), day => Assert.False(day.IsInteractive));
    }

    [Fact]
    public void Consistency_MarksTheDaysThatHaveADailyNote()
    {
        var h = CreateViewModel();
        var marked = Today.AddDays(-3);
        File.WriteAllText(Path.Combine(_vaultPath, FileNameFor(marked)), "# nota\n");
        File.WriteAllText(Path.Combine(_vaultPath, "Ideas.md"), "# Ideas\n");

        h.Notes.ToggleConsistencyCommand.Execute(null);

        var days = h.Notes.ConsistencyDays.Where(day => day.HasNote).Select(day => day.Date).ToList();
        Assert.Equal([marked], days);
        Assert.Contains("1", h.Notes.ConsistencySummary);
    }

    [Fact]
    public void Consistency_OpeningADayFromTheGridCreatesThatDaysNoteAndClosesThePanel()
    {
        var h = CreateViewModel();
        h.Notes.ToggleConsistencyCommand.Execute(null);
        var target = h.Notes.ConsistencyDays.First(day => !day.IsFuture && !day.HasNote);

        h.Notes.OpenConsistencyDayCommand.Execute(target);

        Assert.False(h.Notes.IsConsistencyOpen);
        Assert.True(File.Exists(Path.Combine(_vaultPath, FileNameFor(target.Date))));
        Assert.Equal(FileNameFor(target.Date), h.Notes.ActiveEditor!.Note.FileName);
    }

    /// <summary>Sin hábitos activos, la fila de hábitos no se muestra: un vault que no usa Hábitos no tiene
    /// por qué ver una rejilla vacía en Documentos.</summary>
    [Fact]
    public void Consistency_WithoutHabits_HidesTheHabitRow()
    {
        var h = CreateViewModel();

        h.Notes.ToggleConsistencyCommand.Execute(null);

        Assert.False(h.Notes.HasHabitConsistency);
        Assert.Empty(h.Notes.HabitConsistencyDays);
    }

    [Fact]
    public void Consistency_WithHabits_ReadsTheRealCompletionMarks()
    {
        var h = CreateViewModel();
        var habit = h.Habits.AddHabit("Meditar", HabitFrequency.Daily, []);
        habit.CreatedUtc = DateTime.UtcNow.AddDays(-30);
        h.Habits.ToggleCompletion(habit, Today.AddDays(-1));

        h.Notes.ToggleConsistencyCommand.Execute(null);

        Assert.True(h.Notes.HasHabitConsistency);
        Assert.Equal(70, h.Notes.HabitConsistencyDays.Count);
        var done = h.Notes.HabitConsistencyDays.Where(day => day.IsDone).Select(day => day.Date).ToList();
        Assert.Equal([Today.AddDays(-1)], done);
    }

    /// <summary>Un hábito no cuenta antes de existir: crearlo hoy no puede dejar diez semanas de casillas
    /// "sin cumplir" a su espalda.</summary>
    [Fact]
    public void Consistency_DoesNotCountDaysBeforeAHabitExisted()
    {
        var h = CreateViewModel();
        h.Habits.AddHabit("Meditar", HabitFrequency.Daily, []);

        h.Notes.ToggleConsistencyCommand.Execute(null);

        var applicable = h.Notes.HabitConsistencyDays.Where(day => day.IsApplicable).Select(day => day.Date).ToList();
        Assert.Equal([Today], applicable);
    }

    [Fact]
    public void Consistency_WithoutNavigationCallbacks_HidesTheJumpsToHabitsAndStatistics()
    {
        var h = CreateViewModel();

        Assert.False(h.Notes.CanOpenHabits);
        Assert.False(h.Notes.CanOpenStatistics);
    }

    /// <summary>La plantilla configurada manda también en el nombre del día vecino, y la nota que ya
    /// existía no se duplica al navegar hasta ella.</summary>
    [Fact]
    public void EditorNavigation_WithATemplate_UsesTheTemplateNaming()
    {
        var h = CreateViewModel();
        var templatesDirectory = Path.Combine(_vaultPath, ".enki", NoteTemplateService.TemplatesFolderName);
        Directory.CreateDirectory(templatesDirectory);
        File.WriteAllText(Path.Combine(templatesDirectory, "daily.md"),
            "---\nname: Diario\ntitle: \"Diario {{date}}\"\n---\n\n## Hoy\n");
        h.VaultSettings.Settings.DailyNoteTemplate = "daily.md";
        h.VaultSettings.Save();

        h.Notes.OpenDailyNoteCommand.Execute(null);
        h.Notes.ActiveEditor!.OpenPreviousDayCommand.Execute(null);

        var yesterday = Today.AddDays(-1);
        Assert.Equal($"Diario {yesterday:yyyy-MM-dd}.md", h.Notes.ActiveEditor!.Note.FileName);
        Assert.True(h.Notes.ActiveEditor.IsDailyNote);
    }

    /// <summary>Combinar con la plantilla añade lo que falta y respeta lo escrito — el caso central de
    /// "combinar la nota diaria con su plantilla propia".</summary>
    [Fact]
    public void MergeDailyTemplate_AddsTheMissingSectionsWithoutTouchingWhatIsWritten()
    {
        var h = CreateViewModel();
        File.WriteAllText(Path.Combine(_vaultPath, FileNameFor(Today)), "# Hoy\n\n## Hoy\n\n- escrito a mano\n");
        var templatesDirectory = Path.Combine(_vaultPath, ".enki", NoteTemplateService.TemplatesFolderName);
        Directory.CreateDirectory(templatesDirectory);
        File.WriteAllText(Path.Combine(templatesDirectory, "daily.md"),
            "---\nname: Diario\n---\n\n## Hoy\n\n## Mañana\n");
        h.VaultSettings.Settings.DailyNoteTemplate = "daily.md";
        h.VaultSettings.Save();

        h.Notes.OpenDailyNoteCommand.Execute(null);
        var editor = h.Notes.ActiveEditor!;
        Assert.True(editor.HasPendingDailyTemplateSections);

        editor.MergeDailyTemplateCommand.Execute(null);

        Assert.Contains("- escrito a mano", editor.Document.Text);
        Assert.Contains("## Mañana", editor.Document.Text);
        Assert.False(editor.HasPendingDailyTemplateSections);
        Assert.Contains("## Mañana", File.ReadAllText(Path.Combine(_vaultPath, FileNameFor(Today))));
    }

    [Fact]
    public void MergeDailyTemplate_AfterExternalEdit_PreservesBothVersionsAndOpensConflict()
    {
        var h = CreateViewModel();
        var path = Path.Combine(_vaultPath, FileNameFor(Today));
        File.WriteAllText(path, "## Hoy\n\nTexto original\n");
        var templatesDirectory = Path.Combine(_vaultPath, ".enki", NoteTemplateService.TemplatesFolderName);
        Directory.CreateDirectory(templatesDirectory);
        File.WriteAllText(Path.Combine(templatesDirectory, "daily.md"),
            "---\nname: Diario\n---\n\n## Hoy\n\n## Mañana\n");
        h.VaultSettings.Settings.DailyNoteTemplate = "daily.md";
        h.VaultSettings.Save();
        h.Notes.OpenDailyNoteCommand.Execute(null);
        var editor = h.Notes.ActiveEditor!;
        editor.Document.Text = "## Hoy\n\nBorrador local\n";
        editor.OnTextChanged(editor.Document.Text);
        File.WriteAllText(path, "## Hoy\n\nEdición externa\n");

        editor.MergeDailyTemplateCommand.Execute(null);

        Assert.Equal("## Hoy\n\nEdición externa\n", File.ReadAllText(path));
        Assert.Contains("Borrador local", editor.Document.Text);
        Assert.True(editor.IsExternalConflictOpen);
        Assert.True(editor.IsAutosavePaused);
    }
}
