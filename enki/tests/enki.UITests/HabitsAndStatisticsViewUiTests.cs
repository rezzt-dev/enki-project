using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using enki.Controls.Charts;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Propuestas 13/15 (docs/propuesta-nuevas-funcionalidades.md): drives the real
/// <see cref="HabitsView"/>/<see cref="StatisticsView"/> XAML bound to real ViewModels — same
/// "render, don't just unit-test the ViewModel" reasoning as <see cref="KanbanViewUiTests"/>, catching
/// XAML issues (missing styles/converters, DataTemplate mismatches) compile-time XamlIL checking alone
/// might not.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class HabitsAndStatisticsViewUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _vaultPath;

    public HabitsAndStatisticsViewUiTests()
    {
        HeadlessApp.EnsureInitialized();
        _vaultPath = Path.Combine(_tempDir, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private VaultService CreateVault()
    {
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        return vault;
    }

    [Fact]
    public void HabitsView_WithOneHabit_RendersItsNameAndFrequency()
    {
        var vault = CreateVault();
        var habitService = new HabitService(vault);
        habitService.AddHabit("Meditar", HabitFrequency.Daily, []);
        var viewModel = new HabitsViewModel(habitService, new SettingsService(_tempDir));

        var view = new HabitsView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1000, Height = 760 };
        window.Show();

        var renderedTexts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Meditar", renderedTexts);

        window.Close();
    }

    [Fact]
    public void HabitsView_EmptyState_RendersWithoutThrowing()
    {
        var habitService = new HabitService(CreateVault());
        var viewModel = new HabitsViewModel(habitService, new SettingsService(_tempDir));

        var view = new HabitsView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1000, Height = 760 };
        window.Show();

        Assert.True(viewModel.IsEmpty);
        window.Close();
    }

    [Fact]
    public void StatisticsView_EmptyVault_RendersWithoutThrowing()
    {
        var vault = CreateVault();
        var settings = new SettingsService(_tempDir);
        var kanbanService = new KanbanService(_tempDir);
        var taskService = new PolarTaskService(_tempDir);
        var authService = new PolarAuthService(settings);
        var habitService = new HabitService(vault);
        var aggregationService = new StatisticsAggregationService(vault, kanbanService, taskService, authService, habitService);
        var viewModel = new StatisticsViewModel(aggregationService, authService);

        var view = new StatisticsView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1180, Height = 820 };
        window.Show();

        Assert.False(viewModel.HasEnoughData);
        window.Close();
    }

    /// <summary>El estado vacío no ejercita ni uno de los gráficos: con <c>HasEnoughData</c> en false toda
    /// la página está colapsada. Este monta un vault con notas, tarjetas y hábitos reales para que el
    /// árbol visual llegue de verdad a los controles de <c>controls/charts/</c> — que es donde vive la
    /// aritmética de layout (MeasureOverride, escalas, ejes) que ninguna comprobación de XAML en tiempo de
    /// compilación puede validar.</summary>
    [Fact]
    public void StatisticsView_WithRealActivity_RendersEveryChartWithoutThrowing()
    {
        var vault = CreateVault();
        var settings = new SettingsService(_tempDir);

        for (var i = 0; i < 12; i++)
        {
            var path = Path.Combine(_vaultPath, $"nota-{i:00}.md");
            File.WriteAllText(path, $"---\ntags: [trabajo]\n---\n# Nota {i}\n\n{new string('a', 40)}");
            File.SetLastWriteTime(path, DateTime.Now.AddDays(-i));
        }
        File.WriteAllBytes(Path.Combine(_vaultPath, "adjunto.pdf"), new byte[2048]);

        var kanbanService = new KanbanService(_tempDir);
        kanbanService.Board.Columns[0].Cards.Add(new KanbanCard
        {
            Id = "c1", Title = "Tarea", Priority = KanbanPriority.High,
            DueDateUtc = DateTime.UtcNow.AddDays(-1),
            Subtasks = [new KanbanSubtask { Id = "s1", Title = "sub", IsDone = true }]
        });
        kanbanService.Board.Columns[^1].Cards.Add(new KanbanCard
        {
            Id = "c2", Title = "Hecha", CompletedAtUtc = DateTime.UtcNow.AddDays(-2)
        });

        var taskService = new PolarTaskService(_tempDir);
        var authService = new PolarAuthService(settings);
        var habitService = new HabitService(vault);
        var habit = habitService.AddHabit("Meditar", HabitFrequency.Daily, []);
        habitService.ToggleCompletion(habit, DateOnly.FromDateTime(DateTime.Today));

        var reminderService = new ReminderService(_tempDir);
        reminderService.Create("Recordatorio", "", DateTime.UtcNow.AddDays(2), "BellRingOutline", null, PolarRecurrence.None);

        var sessionService = new SessionService(vault);
        sessionService.EnsureDefaultSession();

        var aggregationService = new StatisticsAggregationService(vault, kanbanService, taskService, authService,
            habitService, reminderService, sessionService);
        var viewModel = new StatisticsViewModel(aggregationService, authService, settings);

        var view = new StatisticsView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1320, Height = 900 };
        window.Show();

        Assert.True(viewModel.HasEnoughData);

        // Cada familia de gráfico tiene que haberse instanciado y medido: un enlace roto o un tipo mal
        // resuelto en el XAML dejaría el control fuera del árbol sin que la vista fallara.
        Assert.NotEmpty(view.GetVisualDescendants().OfType<LineAreaChart>());
        Assert.NotEmpty(view.GetVisualDescendants().OfType<BarSeriesChart>());
        Assert.NotEmpty(view.GetVisualDescendants().OfType<CalendarHeatmap>());
        Assert.NotEmpty(view.GetVisualDescendants().OfType<PunchCard>());
        Assert.NotEmpty(view.GetVisualDescendants().OfType<DonutChart>());
        Assert.NotEmpty(view.GetVisualDescendants().OfType<RadialGauge>());
        Assert.NotEmpty(view.GetVisualDescendants().OfType<SegmentedBar>());
        Assert.NotEmpty(view.GetVisualDescendants().OfType<Sparkline>());

        // Los que se miden solos (RankingBars/SegmentedBar/CalendarHeatmap/PunchCard) deben haber
        // reservado alto real; a 0 el gráfico existiría pero no se vería nada.
        Assert.All(view.GetVisualDescendants().OfType<RankingBars>().Where(r => r.IsVisible),
            ranking => Assert.True(ranking.Bounds.Height > 0));

        window.Close();
    }

    /// <summary>Cambiar de rango recarga el informe entero, no solo la serie semanal: con "Semana" el
    /// número de días del rango tiene que encogerse de verdad.</summary>
    [Fact]
    public void StatisticsView_SwitchingRange_RebuildsTheReport()
    {
        var vault = CreateVault();
        var settings = new SettingsService(_tempDir);
        var kanbanService = new KanbanService(_tempDir);
        var taskService = new PolarTaskService(_tempDir);
        var authService = new PolarAuthService(settings);
        var habitService = new HabitService(vault);
        var aggregationService = new StatisticsAggregationService(vault, kanbanService, taskService, authService, habitService);
        var viewModel = new StatisticsViewModel(aggregationService, authService);

        var view = new StatisticsView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1180, Height = 820 };
        window.Show();

        var monthDays = viewModel.RangeDays;
        viewModel.SelectRangeCommand.Execute("Week");

        Assert.Equal(StatisticsRange.Week, viewModel.Range);
        Assert.True(viewModel.RangeDays < monthDays);
        Assert.Equal(viewModel.RangeDays, viewModel.DailyNotesSeries.Count);

        window.Close();
    }

    /// <summary>Plegar una sección tiene que sacar su contenido del árbol visual de verdad (no dejarlo
    /// ocupando alto con IsVisible mal enlazado) y dejar en pie la cabecera con su resumen.</summary>
    [Fact]
    public void StatisticsView_CollapsingASection_HidesItsContentButKeepsTheHeader()
    {
        var vault = CreateVault();
        var settings = new SettingsService(_tempDir);
        File.WriteAllText(Path.Combine(_vaultPath, "nota.md"), "# Nota\n\nalgo de texto");

        var kanbanService = new KanbanService(_tempDir);
        var taskService = new PolarTaskService(_tempDir);
        var authService = new PolarAuthService(settings);
        var habitService = new HabitService(vault);
        var aggregationService = new StatisticsAggregationService(vault, kanbanService, taskService, authService, habitService);
        var viewModel = new StatisticsViewModel(aggregationService, authService, settings);

        var view = new StatisticsView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1320, Height = 900 };
        window.Show();

        Assert.NotEmpty(view.GetVisualDescendants().OfType<LineAreaChart>());

        viewModel.PulseSection.ToggleCommand.Execute(null);
        viewModel.KnowledgeSection.ToggleCommand.Execute(null);

        // Los dos únicos LineAreaChart de la página viven en Pulso (actividad diaria) y en Conocimiento
        // (crecimiento del vault): con ambas plegadas no puede quedar ninguno visible.
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<LineAreaChart>(), c => c.IsEffectivelyVisible);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.PulseSection.Summary));
        Assert.Equal(["pulse", "knowledge"], settings.Settings.StatisticsCollapsedSections);

        window.Close();
    }
}
