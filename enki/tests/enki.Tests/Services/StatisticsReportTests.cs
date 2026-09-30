using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="StatisticsAggregationService.BuildReport"/> — el informe único que alimenta
/// la página de Estadísticas rediseñada. Mismo harness de vault desechable que
/// <see cref="StatisticsAggregationServiceTests"/>, y las mismas precauciones con la zona horaria: nada
/// aquí fija un día concreto a un cubo concreto, solo totales y formas.</summary>
public class StatisticsReportTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public StatisticsReportTests() => _vaultPath = Path.Combine(_storageDirectory, "vault");

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private sealed record Harness(
        StatisticsAggregationService Service, VaultService Vault, KanbanService Kanban,
        HabitService Habits, ReminderService Reminders);

    private Harness CreateHarness()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vaultService = new VaultService(settingsService);
        vaultService.InitializeVault(_vaultPath, seedWelcomeNote: false);

        var kanbanService = new KanbanService(_storageDirectory);
        foreach (var column in kanbanService.DefaultBoard.Columns) column.Cards.Clear();

        var taskService = new PolarTaskService(_storageDirectory);
        var authService = new PolarAuthService(settingsService, baseUrl: "https://test.example", anonKey: "test-key");
        var habitService = new HabitService(vaultService);
        var reminderService = new ReminderService(_storageDirectory);
        var sessionService = new SessionService(vaultService);

        var service = new StatisticsAggregationService(vaultService, kanbanService, taskService, authService,
            habitService, reminderService, sessionService);
        return new Harness(service, vaultService, kanbanService, habitService, reminderService);
    }

    private static (DateTime StartUtc, DateTime EndUtc) LastMonthRange() =>
        (DateTime.UtcNow.AddDays(-28), DateTime.UtcNow.Date.AddDays(1).ToUniversalTime());

    private string WriteNote(string relativePath, int words, params string[] tags)
    {
        var full = Path.Combine(_vaultPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var frontmatter = tags.Length == 0 ? string.Empty : $"---\ntags: [{string.Join(", ", tags)}]\n---\n";
        File.WriteAllText(full, frontmatter + string.Join(' ', Enumerable.Repeat("palabra", words)));
        return full;
    }

    [Fact]
    public void BuildReport_EmptyVault_IsAllZeroButStillWellFormed()
    {
        var h = CreateHarness();
        var (start, end) = LastMonthRange();

        var report = h.Service.BuildReport(start, end);

        // Todas las series existen aunque no haya datos: la vista enlaza contra ellas incondicionalmente y
        // solo decide con los flags Has* si enseña el gráfico o el estado vacío.
        Assert.NotEmpty(report.Daily);
        Assert.All(report.Daily, d => Assert.Equal(0, d.Total));
        Assert.Equal(7, report.WeekdayActivity.Count);
        Assert.Equal(7 * 24, report.PunchCard.Count);
        Assert.Empty(report.TopTags);
        Assert.Equal(0, report.Vault.Notes);
        Assert.Null(report.HabitCompletionPercent);
        Assert.Equal(0, report.ActivityStreak.Current);
    }

    [Fact]
    public void BuildReport_CountsNotesWordsAndFoldersOfTheVault()
    {
        var h = CreateHarness();
        WriteNote("suelta.md", 120, "trabajo");
        WriteNote(Path.Combine("Proyectos", "uno.md"), 300, "trabajo", "ideas");
        WriteNote(Path.Combine("Proyectos", "dos.md"), 40);
        var (start, end) = LastMonthRange();

        var report = h.Service.BuildReport(start, end);

        Assert.Equal(3, report.Vault.Notes);
        Assert.Equal(1, report.Vault.Folders);
        Assert.Equal(2, report.Vault.DistinctTags);
        // El recuento de palabras es el que ya calcula VaultService (incluye el frontmatter visible, que
        // forma parte del texto plano de la nota), así que se comprueba la relación entre total y media,
        // no una cifra clavada que dependería de ese detalle.
        Assert.True(report.Vault.TotalWords >= 460);
        Assert.Equal(report.Vault.TotalWords / 3.0, report.Vault.AverageWords, 3);
        Assert.Equal("uno", report.Vault.LongestNoteTitle);
    }

    [Fact]
    public void BuildReport_TopTags_CountsNotesAndCardsTogether_MostUsedFirst()
    {
        var h = CreateHarness();
        WriteNote("a.md", 10, "trabajo");
        WriteNote("b.md", 10, "trabajo");
        h.Kanban.DefaultBoard.Columns[0].Cards.Add(new KanbanCard { Id = "c1", Title = "T", Tags = ["trabajo"] });
        h.Kanban.DefaultBoard.Columns[0].Cards.Add(new KanbanCard { Id = "c2", Title = "T", Tags = ["ideas"] });
        var (start, end) = LastMonthRange();

        var report = h.Service.BuildReport(start, end);

        Assert.Equal("#trabajo", report.TopTags[0].Label);
        Assert.Equal(3, report.TopTags[0].Value);
        Assert.Equal("#ideas", report.TopTags[1].Label);
    }

    [Fact]
    public void BuildReport_NoteLengthBuckets_AlwaysSixBandsSummingToTheNoteCount()
    {
        var h = CreateHarness();
        WriteNote("corta.md", 10);
        WriteNote("media.md", 200);
        WriteNote("larga.md", 1500);
        var (start, end) = LastMonthRange();

        var report = h.Service.BuildReport(start, end);

        Assert.Equal(6, report.NoteLengthBuckets.Count);
        Assert.Equal(3, report.NoteLengthBuckets.Sum(b => b.Value));
        Assert.Equal(1, report.NoteLengthBuckets[0].Value);
    }

    [Fact]
    public void BuildReport_TaskHealth_SplitsOverdueFromUpcomingAndCountsSubtasks()
    {
        var h = CreateHarness();
        h.Kanban.DefaultBoard.Columns[0].Cards.Add(new KanbanCard
        {
            Id = "c1", Title = "Vencida", DueDateUtc = DateTime.UtcNow.AddDays(-3),
            Subtasks = [new KanbanSubtask { Id = "s1", Title = "a", IsDone = true },
                        new KanbanSubtask { Id = "s2", Title = "b", IsDone = false }]
        });
        h.Kanban.DefaultBoard.Columns[0].Cards.Add(new KanbanCard
        {
            Id = "c2", Title = "Próxima", DueDateUtc = DateTime.UtcNow.AddDays(3)
        });
        h.Kanban.DefaultBoard.Columns[0].Cards.Add(new KanbanCard { Id = "c3", Title = "Sin fecha" });
        var (start, end) = LastMonthRange();

        var report = h.Service.BuildReport(start, end);

        Assert.Equal(1, report.Tasks.Overdue);
        Assert.Equal(1, report.Tasks.DueNext7Days);
        Assert.Equal(1, report.Tasks.NoDueDate);
        Assert.Equal(2, report.Tasks.Subtasks);
        Assert.Equal(1, report.Tasks.SubtasksDone);
        Assert.Equal(50, report.Tasks.SubtaskCompletionPercent);
    }

    [Fact]
    public void BuildReport_CompletedCardDoesNotCountAsOverdue()
    {
        // Una tarjeta ya terminada con fecha pasada no es deuda: contarla dejaría el marcador de
        // "vencidas" creciendo para siempre en cualquier tablero con historial.
        var h = CreateHarness();
        h.Kanban.DefaultBoard.Columns[^1].Cards.Add(new KanbanCard
        {
            Id = "c1", Title = "Hecha tarde",
            DueDateUtc = DateTime.UtcNow.AddDays(-5),
            CompletedAtUtc = DateTime.UtcNow.AddDays(-1)
        });
        var (start, end) = LastMonthRange();

        var report = h.Service.BuildReport(start, end);

        Assert.Equal(0, report.Tasks.Overdue);
        Assert.Equal(1, report.TasksCompletedInRange);
    }

    [Fact]
    public void BuildReport_HabitStreak_CountsConsecutiveFullyCompletedDays()
    {
        var h = CreateHarness();
        var habit = h.Habits.AddHabit("Meditar", HabitFrequency.Daily, []);
        var today = DateOnly.FromDateTime(DateTime.Today);
        for (var d = 0; d < 4; d++) h.Habits.ToggleCompletion(habit, today.AddDays(-d));
        var (start, end) = LastMonthRange();

        var report = h.Service.BuildReport(start, end);

        Assert.Equal(4, report.HabitStreak.Current);
        Assert.True(report.HabitStreak.Best >= 4);
        Assert.NotNull(report.HabitCompletionPercent);
        Assert.Single(report.HabitConsistency);
    }

    [Fact]
    public void BuildReport_HabitStreak_TodayNotYetDoneDoesNotBreakYesterdaysStreak()
    {
        var h = CreateHarness();
        var habit = h.Habits.AddHabit("Leer", HabitFrequency.Daily, []);
        var today = DateOnly.FromDateTime(DateTime.Today);
        for (var d = 1; d <= 3; d++) h.Habits.ToggleCompletion(habit, today.AddDays(-d));
        var (start, end) = LastMonthRange();

        var report = h.Service.BuildReport(start, end);

        Assert.Equal(3, report.HabitStreak.Current);
    }

    [Fact]
    public void BuildReport_ActivityHeatmap_CoversAFullYearEndingToday()
    {
        var h = CreateHarness();
        var (start, end) = LastMonthRange();

        var report = h.Service.BuildReport(start, end);

        Assert.NotEmpty(report.ActivityHeatmap);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), report.ActivityHeatmap[^1].Date);
        Assert.True(report.ActivityHeatmap.Count >= 365);
        Assert.All(report.ActivityHeatmap, c => Assert.InRange(c.Intensity, 0, 1));
    }

    [Fact]
    public void BuildReport_Growth_IsMonotonicAndEndsAtTheVaultNoteCount()
    {
        var h = CreateHarness();
        WriteNote("a.md", 10);
        WriteNote("b.md", 10);
        var (start, end) = LastMonthRange();

        var report = h.Service.BuildReport(start, end);

        Assert.NotEmpty(report.Growth);
        for (var i = 1; i < report.Growth.Count; i++)
            Assert.True(report.Growth[i].Value >= report.Growth[i - 1].Value);
        Assert.Equal(report.Vault.Notes, report.Growth[^1].Value);
    }

    [Fact]
    public void BuildReport_Reminders_SplitOverdueUpcomingAndCompleted()
    {
        var h = CreateHarness();
        h.Reminders.Create("Pasado", "", DateTime.UtcNow.AddDays(-2), "BellRingOutline", null, PolarRecurrence.None);
        h.Reminders.Create("Futuro", "", DateTime.UtcNow.AddDays(2), "BellRingOutline", null, PolarRecurrence.Daily);
        var (start, end) = LastMonthRange();

        var report = h.Service.BuildReport(start, end);

        Assert.Equal(2, report.Reminders.Active);
        Assert.Equal(1, report.Reminders.Overdue);
        Assert.Equal(1, report.Reminders.Upcoming);
        Assert.Equal(1, report.Reminders.Recurring);
    }

    [Fact]
    public void BuildReport_PunchCard_TotalsMatchTheTimestampsInsideTheRange()
    {
        var h = CreateHarness();
        var path = WriteNote("a.md", 10);
        File.SetLastWriteTime(path, DateTime.Now.AddDays(-2));
        var (start, end) = LastMonthRange();

        var report = h.Service.BuildReport(start, end);

        Assert.Equal(7 * 24, report.PunchCard.Count);
        Assert.True(report.PunchCard.Sum() > 0);
    }
}
